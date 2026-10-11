package nspro

import (
	"context"
	"encoding/binary"
	"io"
	"testing"
	"time"

	viiperTesting "github.com/Alia5/VIIPER/_testing"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/internal/server/api"
	apihandler "github.com/Alia5/VIIPER/internal/server/api/handler"
	"github.com/Alia5/VIIPER/usbip"
	"github.com/Alia5/VIIPER/viiperclient"
	"github.com/Alia5/VIIPER/virtualbus"
	"github.com/stretchr/testify/require"
)

// Uses loopback API and USBIP sockets, not a Windows kernel attachment or radio.
// Covers the real registration, streaming codec, USB handshake and feedback path.
func TestAPIStreamUSBIPAndStereoFeedback(t *testing.T) {
	s := viiperTesting.NewTestServer(t)
	defer s.UsbServer.Close()
	defer s.ApiServer.Close()
	router := s.ApiServer.Router()
	router.Register("bus/{id}/add", apihandler.BusDeviceAdd(s.UsbServer, s.ApiServer))
	router.RegisterStream("bus/{busId}/{deviceid}", api.DeviceStreamHandler(s.UsbServer))
	require.NoError(t, s.ApiServer.Start())
	b, err := virtualbus.NewWithBusID(1)
	require.NoError(t, err)
	defer b.Close()
	require.NoError(t, s.UsbServer.AddBus(b))
	client := viiperclient.New(s.ApiServer.Addr())
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	stream, _, err := client.AddDeviceAndConnect(ctx, b.BusID(), "nspro", nil)
	require.NoError(t, err)
	defer stream.Close()
	usbClient := viiperTesting.NewUsbIpClient(t, s.UsbServer.Addr())
	devices, err := usbClient.ListDevices()
	require.NoError(t, err)
	require.Len(t, devices, 1)
	imp, err := usbClient.AttachDevice(devices[0].BusID)
	require.NoError(t, err)
	defer imp.Conn.Close()

	for _, id := range []byte{1, 2, 3} {
		require.NoError(t, usbClient.Submit(imp.Conn, usbip.DirOut, 1, []byte{0x80, id}, nil))
		ack, err := usbClient.ReadInputReportWithTimeout(imp.Conn, time.Second)
		require.NoError(t, err)
		require.Len(t, ack, 64)
		require.Equal(t, []byte{0x81, id}, ack[:2])
	}
	require.NoError(t, usbClient.Submit(imp.Conn, usbip.DirOut, 1, []byte{0x80, 4}, nil))
	initial, err := usbClient.ReadInputReportWithTimeout(imp.Conn, time.Second)
	require.NoError(t, err)
	require.Equal(t, byte(0x30), initial[0])

	for _, command := range []byte{0x40, 0x48} {
		out := make([]byte, 12)
		out[0], out[10], out[11] = 1, command, 1
		require.NoError(t, usbClient.Submit(imp.Conn, usbip.DirOut, 1, out, nil))
		ack, err := usbClient.ReadInputReportWithTimeout(imp.Conn, time.Second)
		require.NoError(t, err)
		require.Equal(t, byte(0x21), ack[0])
		require.Equal(t, command, ack[14])
	}
	// Synthetic fresh frames exercise the wire contract; this is not a radio Hz measurement.
	for i := 0; i < 32; i++ {
		state := ns2pro.InputState{Buttons: ns2pro.ButtonA | ns2pro.ButtonUp,
			LX: uint16(2200 + i), LY: 2048, RX: 2048, RY: 2048,
			GyroX: 1638, AccelZ: 4096, MotionTimestampUs: uint32(i * 7500)}
		require.NoError(t, stream.WriteBinary(&state))
		input, err := usbClient.ReadInputReportWithTimeout(imp.Conn, time.Second)
		require.NoError(t, err)
		require.Len(t, input, 64)
		require.Equal(t, byte(0x30), input[0])
		require.Equal(t, byte(8), input[3])
		require.Equal(t, byte(2), input[5])
		require.Equal(t, state.LX, uint16(input[6])|uint16(input[7]&15)<<8)
		require.Equal(t, int16(4096), int16(binary.LittleEndian.Uint16(input[17:19])))
		require.Equal(t, gyro(state.GyroX), int16(binary.LittleEndian.Uint16(input[19:21])))
	}
	// Enabling vibration sends its neutral command frame to the software stream.
	// Verify a subsequent stereo command arrives intact even if that frame precedes it.
	rumble := []byte{0x10, 0, 0, 0x40, 0x40, 0x48, 0, 0x30, 0x40, 0x44}
	require.NoError(t, usbClient.Submit(imp.Conn, usbip.DirOut, 1, rumble, nil))
	require.NoError(t, stream.SetReadDeadline(time.Now().Add(time.Second)))
	matched := false
	for i := 0; i < 3; i++ {
		var feedback [FeedbackSize]byte
		_, err := io.ReadFull(stream, feedback[:])
		require.NoError(t, err)
		if feedback[8]&1 != 0 && feedback[1] == 0x40 && feedback[5] == 0x30 {
			require.Equal(t, rumble[2:10], feedback[:8])
			matched = true
			break
		}
	}
	require.True(t, matched, "stereo feedback must cross the complete USBIP/API path")
}

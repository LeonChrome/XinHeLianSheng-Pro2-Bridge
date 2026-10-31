package dualsense

import (
	"bytes"
	"context"
	"encoding/binary"
	"testing"

	"github.com/Alia5/VIIPER/usbip"
)

// Physical USB capture: nondebug/dualsense/lsusb-descriptor-info.txt.
// Validate the wire, including UAC lengths that permissive HID clients ignore.
func TestNativeDualSenseUSBIdentityAndAudioLengths(t *testing.T) {
	d := hapticDescriptor()
	if d.Device.BcdDevice != 0x0100 || d.Strings[2] != "Wireless Controller" {
		t.Fatal("native USB identity differs from the physical wired device")
	}
	ac := d.Interfaces[0].ClassDescriptors
	actual := 0
	for _, item := range ac {
		actual += len(item.Bytes())
	}
	header := ac[0].Bytes()
	if want := int(binary.LittleEndian.Uint16(header[5:7])); want != actual || actual != 73 {
		t.Fatalf("AudioControl wTotalLength=%d actual=%d want=73", want, actual)
	}
	for _, index := range []int{2, 4} {
		iface := d.Interfaces[index]
		if n := len(iface.ClassDescriptors[0].Bytes()); n != 7 {
			t.Fatalf("AS_GENERAL interface=%d length=%d want=7", index, n)
		}
		var wire bytes.Buffer
		iface.Endpoints[0].Write(&wire)
		got := wire.Bytes()
		if len(got) != 9 || got[0] != 9 || got[7] != 0 || got[8] != 0 {
			t.Fatalf("UAC endpoint missing refresh/sync: %x", got)
		}
	}
	hid, _ := d.Interface(3)
	report, err := hid.HID.ReportBytes()
	if err != nil || len(report) != 273 || !bytes.Equal(report, nativeUSBReportDescriptor) {
		t.Fatal("standard DualSense must expose the physical 273-byte USB HID descriptor")
	}
	for _, endpoint := range hid.Endpoints {
		var wire bytes.Buffer
		endpoint.Write(&wire)
		if wire.Len() != 7 || wire.Bytes()[0] != 7 {
			t.Fatal("HID endpoint changed shape")
		}
	}
}

func TestVirtualCaptureIsSilentAndFollowsAlternateSetting(t *testing.T) {
	dev, _ := (&hapticHandler{}).CreateDevice(nil)
	d := dev.(*DualSense)
	if got := d.HandleTransfer(context.Background(), 2, usbip.DirIn, nil); len(got) != 0 {
		t.Fatal("capture must not run before its interface is opened")
	}
	d.HandleControl(1, 0x0b, 1, 2, 0, nil)
	got := d.HandleTransfer(context.Background(), 2, usbip.DirIn, nil)
	if len(got) != 192 || !bytes.Equal(got, make([]byte, 192)) {
		t.Fatal("virtual capture must complete with silent stereo PCM, not physical microphone claims")
	}
	d.HandleControl(1, 0x0b, 0, 2, 0, nil)
	if got := d.HandleTransfer(context.Background(), 2, usbip.DirIn, nil); len(got) != 0 {
		t.Fatal("closed capture interface still emits data")
	}
}

func TestNativeInputSequenceAndUSBConnectionMetadata(t *testing.T) {
	d, _ := New(nil)
	s := NewInputState()
	var previous uint32
	for i := 0; i < 260; i++ {
		got := d.buildUSBInputReportAt(s, d.metaState, uint64(1000+i*22500))
		seq := binary.LittleEndian.Uint32(got[12:16])
		if seq != previous+1 || got[7] != byte(i+1) {
			t.Fatalf("native sequence is frozen or discontinuous at %d: %d/%d", i, seq, got[7])
		}
		if got[54] != 0x08 || got[53] != d.metaState.BatteryStatus {
			t.Fatal("USB-connected battery metadata absent")
		}
		if binary.LittleEndian.Uint32(got[49:53]) != binary.LittleEndian.Uint32(got[28:32]) {
			t.Fatal("secondary timestamp stale")
		}
		previous = seq
	}
	d.packetSequence = ^uint32(0)
	got := d.buildUSBInputReport(s, d.metaState)
	if binary.LittleEndian.Uint32(got[12:16]) != 0 {
		t.Fatal("sequence must wrap modulo uint32")
	}
}

func TestNativeHostControlTelemetryBounded(t *testing.T) {
	d, _ := New(nil)
	d.hapticEnabled = true
	for i := 0; i < 100; i++ {
		d.traceHostControl(0xa1, 1, uint16(i), 3, 64, 0, false)
	}
	if len(d.hostControlRequests) != 32 {
		t.Fatal("host trace must remain bounded")
	}
}

func TestAdaptiveAudioPacketsKeepEverySampleFrame(t *testing.T) {
	for _, size := range []int{8, 376, 384, 392, 768, 784} {
		input := make([]byte, size)
		for i := range input {
			input[i] = byte(i)
		}
		var got []byte
		forEachHapticPCMChunk(input, func(chunk []byte) {
			if len(chunk) > 384 || len(chunk)%8 != 0 {
				t.Fatal("invalid client PCM chunk")
			}
			got = append(got, chunk...)
		})
		if !bytes.Equal(input, got) {
			t.Fatalf("adaptive packet size=%d loses samples", size)
		}
	}
}

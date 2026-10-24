package ratecontract_test

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"sync/atomic"
	"testing"
	"time"

	viiperTesting "github.com/Alia5/VIIPER/_testing"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/dualsense"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/device/nspro"
	"github.com/Alia5/VIIPER/device/xbox360"
	"github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
	"github.com/Alia5/VIIPER/virtualbus"
)

type socketSpec struct {
	dev    usb.Device
	ep     uint32
	update func(int)
	decode func([]byte) int
}

func socketDevice(t *testing.T, name string) socketSpec {
	switch name {
	case "Pro1":
		p, e := nspro.New(nil)
		if e != nil {
			t.Fatal(e)
		}
		p.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0x80, 4})
		return socketSpec{p, 1, func(v int) { s := *ns2pro.NewInputState(); s.LX = uint16(v); p.UpdateInputState(s) }, func(b []byte) int { return int(b[6]) | int(b[7]&15)<<8 }}
	case "PS5":
		p, e := dualsense.New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
		if e != nil {
			t.Fatal(e)
		}
		// Same HID endpoint address/speed/bInterval as production dualsensehaptic,
		// without attaching audio interfaces or Windows device drivers.
		p.GetDescriptor().Device.Speed = 3
		for i := range p.GetDescriptor().Interfaces[0].Endpoints {
			p.GetDescriptor().Interfaces[0].Endpoints[i].BInterval = 6
		}
		return socketSpec{p, 4, func(v int) { s := dualsense.NewInputState(); s.GyroX = int16(v); p.UpdateInputState(s) }, func(b []byte) int { return int(binary.LittleEndian.Uint16(b[16:18])) }}
	default:
		p, e := xbox360.New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
		if e != nil {
			t.Fatal(e)
		}
		return socketSpec{p, 1, func(v int) { s := *xbox360.NewInputState(); s.LX = int16(v); p.UpdateInputState(s) }, func(b []byte) int { return int(binary.LittleEndian.Uint16(b[6:8])) }}
	}
}
func attachSocket(t *testing.T, spec socketSpec) net.Conn {
	s := viiperTesting.NewTestServer(t)
	t.Cleanup(func() { s.UsbServer.Close(); s.ApiServer.Close() })
	b, e := virtualbus.NewWithBusID(1)
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { b.Close() })
	if _, e = b.Add(spec.dev); e != nil {
		t.Fatal(e)
	}
	if e = s.UsbServer.AddBus(b); e != nil {
		t.Fatal(e)
	}
	c := viiperTesting.NewUsbIpClient(t, s.UsbServer.Addr())
	ds, e := c.ListDevices()
	if e != nil {
		t.Fatal(e)
	}
	if len(ds) != 1 {
		t.Fatal(ds)
	}
	imp, e := c.AttachDevice(ds[0].BusID)
	if e != nil {
		t.Fatal(e)
	}
	t.Cleanup(func() { imp.Conn.Close() })
	return imp.Conn
}
func readEndpoint(t *testing.T, c net.Conn, ep uint32, seq uint32) []byte {
	t.Helper()
	c.SetDeadline(time.Now().Add(time.Second))
	q := usbip.CmdSubmit{Basic: usbip.HeaderBasic{Command: usbip.CmdSubmitCode, Seqnum: seq, Dir: usbip.DirIn, Ep: ep}, TransferBufferLen: 255}
	if e := q.Write(c); e != nil {
		t.Fatal(e)
	}
	h := make([]byte, 48)
	if _, e := io.ReadFull(c, h); e != nil {
		t.Fatal(e)
	}
	if binary.BigEndian.Uint32(h[:4]) != usbip.RetSubmitCode || binary.BigEndian.Uint32(h[20:24]) != 0 {
		t.Fatalf("invalid RET %x", h)
	}
	b := make([]byte, int(binary.BigEndian.Uint32(h[24:28])))
	if _, e := io.ReadFull(c, b); e != nil {
		t.Fatal(e)
	}
	return b
}

func TestServerWaitsForFreshSourceRatherThanCachedReplay(t *testing.T) {
	for _, name := range []string{"Pro1", "PS5", "Xbox"} {
		t.Run(name, func(t *testing.T) {
			s := socketDevice(t, name)
			s.update(1234)
			c := attachSocket(t, s)
			if got := s.decode(readEndpoint(t, c, s.ep, 0)); got != 1234 {
				t.Fatal(got)
			}
			for i := 1; i <= 2; i++ {
				marker := 1234 + i
				start := time.Now()
				go func() { time.Sleep(25 * time.Millisecond); s.update(marker) }()
				b := readEndpoint(t, c, s.ep, uint32(i))
				if time.Since(start) < 20*time.Millisecond || s.decode(b) != marker {
					t.Fatal("server replayed cached input before fresh source")
				}
			}
			t.Logf("FIXED USBIP mode=%s source_updates=3 returned_reports=3 cached_repeats=0", name)
		})
	}
}

func TestActualSocketRateVs250Poll(t *testing.T) {
	for _, hz := range []int{66, 133, 250} {
		for _, name := range []string{"Pro1", "PS5", "Xbox"} {
			t.Run(fmt.Sprintf("%s_%d", name, hz), func(t *testing.T) {
				s := socketDevice(t, name)
				s.update(1)
				c := attachSocket(t, s)
				// Prime cached state; subsequent samples are generated on a measured clock.
				previous := readEndpoint(t, c, s.ep, 0)
				var source atomic.Int64
				source.Store(1)
				done := make(chan struct{})
				stopped := make(chan struct{})
				go func() {
					defer close(stopped)
					tick := time.NewTicker(time.Second / time.Duration(hz))
					defer tick.Stop()
					for {
						select {
						case <-done:
							return
						case <-tick.C:
							s.update(int(source.Add(1)))
						}
					}
				}()
				start := time.Now()
				poll := time.NewTicker(4 * time.Millisecond)
				defer poll.Stop()
				returned, repeated, unique, last, repeatNewClock := 0, 0, 0, 1, 0
				duration := 450 * time.Millisecond
				if hz == 66 && name != "Xbox" {
					duration = 1500 * time.Millisecond
				}
				for time.Since(start) < duration {
					<-poll.C
					b := readEndpoint(t, c, s.ep, uint32(returned+1))
					returned++
					v := s.decode(b)
					if v == last {
						repeated++
						if name == "PS5" && string(previous[28:32]) != string(b[28:32]) {
							repeatNewClock++
						}
					} else {
						unique++
						last = v
					}
					previous = b
				}
				close(done)
				<-stopped
				elapsed := time.Since(start).Seconds()
				produced := int(source.Load()) - 1
				t.Logf("AUDIT SOCKET mode=%s target_source_hz=%d duration=%.3f actual_source=%d returned=%d unique_source=%d repeated=%d overwritten_before_last=%d tail_pending=%d repeat_new_timestamp=%d returned_hz=%.1f unique_hz=%.1f", name, hz, elapsed, produced, returned, unique, repeated, last-1-unique, produced-(last-1), repeatNewClock, float64(returned)/elapsed, float64(unique)/elapsed)
				if repeated != 0 || repeatNewClock != 0 {
					t.Fatal("strict source-paced mode fabricated repeated source samples")
				}
			})
		}
	}
}

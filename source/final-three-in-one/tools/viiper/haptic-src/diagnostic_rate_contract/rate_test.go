package ratecontract_test

// Offline transfer-contract audit. No Windows USBIP attachment or Bluetooth access.
import (
	"context"
	"encoding/binary"
	"fmt"
	"testing"
	"time"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/dualsense"
	"github.com/Alia5/VIIPER/device/ns2pro"
	"github.com/Alia5/VIIPER/device/nspro"
	"github.com/Alia5/VIIPER/device/xbox360"
	"github.com/Alia5/VIIPER/usbip"
)

type endpoint struct {
	name              string
	update            func(int)
	read              func(context.Context) []byte
	decode            func([]byte) int
	repeatsOnDeadline bool
}

func endpoints(t *testing.T) []endpoint {
	p, err := nspro.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	p.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0x80, 4})
	p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	d, err := dualsense.New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if err != nil {
		t.Fatal(err)
	}
	d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	x, err := xbox360.New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if err != nil {
		t.Fatal(err)
	}
	x.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	return []endpoint{
		{"Pro1", func(v int) {
			s := *ns2pro.NewInputState()
			s.LX = uint16(v)
			s.GyroX = int16(v)
			s.MotionTimestampUs = uint32(v * 7500)
			p.UpdateInputState(s)
		}, func(c context.Context) []byte { return p.HandleTransfer(c, 1, usbip.DirIn, nil) }, func(b []byte) int { return int(b[6]) | int(b[7]&15)<<8 }, false},
		{"PS5", func(v int) { s := dualsense.NewInputState(); s.GyroX = int16(v); d.UpdateInputState(s) }, func(c context.Context) []byte { return d.HandleTransfer(c, 4, usbip.DirIn, nil) }, func(b []byte) int { return int(binary.LittleEndian.Uint16(b[16:18])) }, false},
		{"Xbox", func(v int) { s := *xbox360.NewInputState(); s.LX = int16(v); x.UpdateInputState(s) }, func(c context.Context) []byte { return x.HandleTransfer(c, 1, usbip.DirIn, nil) }, func(b []byte) int { return int(binary.LittleEndian.Uint16(b[6:8])) }, false},
	}
}

func TestSourceRateAgainst250Poll(t *testing.T) {
	// Deterministic event scheduling for one virtual second, not a wall-clock Hz benchmark.
	// A poll without new input is completed by its expired request deadline.
	for _, hz := range []int{66, 133, 250, 500} {
		for _, ep := range endpoints(t) {
			t.Run(fmt.Sprintf("%s_source_%d", ep.name, hz), func(t *testing.T) {
				source, seen, newReports, repeated, empty, last := 0, 0, 0, 0, 0, 0
				for poll := 1; poll <= 250; poll++ {
					since := 0
					for (source+1)*250 <= poll*hz {
						source++
						ep.update(source)
						since++
					}
					var b []byte
					if since > 0 {
						b = ep.read(context.Background())
					} else {
						c, cancel := context.WithDeadline(context.Background(), time.Unix(0, 0))
						b = ep.read(c)
						cancel()
					}
					if len(b) == 0 {
						empty++
						continue
					}
					seen++
					marker := ep.decode(b)
					if marker == last {
						repeated++
					} else {
						newReports++
						last = marker
					}
					if marker != source {
						t.Fatalf("marker=%d newest_source=%d", marker, source)
					}
				}
				expected := min(hz, 250)
				if newReports != expected {
					t.Fatalf("new=%d expected=%d", newReports, expected)
				}
				expectedRepeats := 0
				if ep.repeatsOnDeadline {
					expectedRepeats = 250 - expected
				}
				if repeated != expectedRepeats {
					t.Fatalf("repeated=%d expected=%d", repeated, expectedRepeats)
				}
				t.Logf("AUDIT mode=%s source=%d poll=250 returned=%d unique_source=%d repeats=%d empty=%d overwritten=%d", ep.name, source, seen, newReports, repeated, empty, source-newReports)
			})
		}
	}
}

func TestPro1CommandRepliesAndSensorSlots(t *testing.T) {
	p, err := nspro.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	p.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0x80, 4})
	p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	imu := make([]byte, 12)
	imu[0] = 1
	imu[10] = 0x40
	imu[11] = 1
	p.HandleTransfer(context.Background(), 1, usbip.DirOut, imu)
	ack := p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if ack[0] != 0x21 {
		t.Fatal("expected initialization ACK")
	}
	ids := map[byte]int{}
	for i := 1; i <= 65; i++ {
		s := *ns2pro.NewInputState()
		s.LX = uint16(i)
		s.GyroX = 1638
		s.MotionTimestampUs = uint32(i * 15000)
		p.UpdateInputState(s)
		// An unrelated host LED command queues a reply without adding an input sample.
		out := make([]byte, 12)
		out[0] = 1
		out[10] = 0x30
		out[11] = byte(i & 15)
		p.HandleTransfer(context.Background(), 1, usbip.DirOut, out)
		for n := 0; n < 2; n++ {
			b := p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
			ids[b[0]]++
			if b[0] == 0x30 {
				for k := 0; k < 12; k++ {
					if b[13+k] != b[25+k] || b[13+k] != b[37+k] {
						t.Fatal("three repeated slots differ")
					}
				}
			}
		}
	}
	if ids[0x21] != 65 || ids[0x30] != 65 {
		t.Fatalf("report counts=%v", ids)
	}
	t.Logf("AUDIT Pro1 source=65 state_0x30=%d command_ACK_0x21=%d repeated_IMU_slots=%d; SDL3 ignores 0x21 for sensor events", ids[0x30], ids[0x21], ids[0x30]*3)
}

func TestPS5LegacyDeadlineRepeatIsExplicitCompatibility(t *testing.T) {
	d, err := dualsense.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	s := dualsense.NewInputState()
	s.GyroX = 1638
	d.UpdateInputState(s)
	a := d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	time.Sleep(2 * time.Millisecond)
	c, cancel := context.WithDeadline(context.Background(), time.Unix(0, 0))
	defer cancel()
	b := d.HandleTransfer(c, 4, usbip.DirIn, nil)
	if len(b) != 64 {
		t.Fatalf("deadline repeat size=%d", len(b))
	}
	if string(a[16:28]) != string(b[16:28]) {
		t.Fatal("sensor changed without source")
	}
	ta, tb := binary.LittleEndian.Uint32(a[28:32]), binary.LittleEndian.Uint32(b[28:32])
	if tb <= ta {
		t.Fatalf("clock did not advance %d->%d", ta, tb)
	}
	t.Logf("AUDIT PS5 identical sensor source repeated with a fresh output timestamp %d -> %d; not a new physical measurement", ta, tb)
}

func TestBackendPreservesShortPressThenRelease(t *testing.T) {
	p, e := nspro.New(nil)
	if e != nil {
		t.Fatal(e)
	}
	p.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0x80, 4})
	a := *ns2pro.NewInputState()
	a.Buttons = ns2pro.ButtonA
	p.UpdateInputState(a)
	a.Buttons = 0
	p.UpdateInputState(a)
	b := p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if b[3]&8 == 0 {
		t.Fatal("Pro1 press overwritten")
	}
	b = p.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if b[3]&8 != 0 {
		t.Fatal("Pro1 release lost")
	}
	d, e := dualsense.New(nil)
	if e != nil {
		t.Fatal(e)
	}
	ds := dualsense.NewInputState()
	ds.Buttons = 0x20
	d.UpdateInputState(ds)
	ds2 := *ds
	ds2.Buttons = 0
	d.UpdateInputState(&ds2)
	b = d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	if b[8]&0x20 == 0 {
		t.Fatal("PS5 press overwritten")
	}
	b = d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	if b[8]&0x20 != 0 {
		t.Fatal("PS5 release lost")
	}
	x, e := xbox360.New(nil)
	if e != nil {
		t.Fatal(e)
	}
	xs := *xbox360.NewInputState()
	xs.Buttons = 0x1000
	x.UpdateInputState(xs)
	xs.Buttons = 0
	x.UpdateInputState(xs)
	b = x.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if binary.LittleEndian.Uint16(b[2:4])&0x1000 == 0 {
		t.Fatal("Xbox press overwritten")
	}
	b = x.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if binary.LittleEndian.Uint16(b[2:4])&0x1000 != 0 {
		t.Fatal("Xbox release lost")
	}
	t.Log("FIXED Pro1/PS5/Xbox: press and release submitted before first USB consumer read -> both edges preserved in order")
}

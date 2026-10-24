package dualsense

import (
	"context"
	"encoding/binary"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usbip"
	"testing"
	"time"
)

func TestSourcePacedPreservesEdgesAndSampleArrivalClock(t *testing.T) {
	d, e := New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if e != nil {
		t.Fatal(e)
	}
	a := *NewInputState()
	a.Buttons = 0x20
	a.GyroX = 111
	d.UpdateInputState(&a)
	time.Sleep(time.Millisecond)
	b := a
	b.Buttons = 0
	b.GyroX = 222
	d.UpdateInputState(&b)
	first := d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	second := d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	if first[8]&0x20 == 0 || second[8]&0x20 != 0 || binary.LittleEndian.Uint16(first[16:]) != 111 || binary.LittleEndian.Uint16(second[16:]) != 222 {
		t.Fatal("press/release or associated IMU lost")
	}
	ta, tb := binary.LittleEndian.Uint32(first[28:]), binary.LittleEndian.Uint32(second[28:])
	if int32(tb-ta) <= 0 {
		t.Fatal("timestamps not sample ordered", ta, tb)
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	if d.HandleTransfer(ctx, 4, usbip.DirIn, nil) != nil {
		t.Fatal("stale sensor got fresh timestamp")
	}
}
func TestPendingInputDoesNotBlockRumbleOutput(t *testing.T) {
	d, e := New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if e != nil {
		t.Fatal(e)
	}
	d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan struct{})
	go func() { d.HandleTransfer(ctx, 4, usbip.DirIn, nil); close(done) }()
	feedback := make(chan []byte, 2)
	d.SetRawOutputCallback(func(b []byte) { feedback <- b })
	report := make([]byte, 48)
	report[0] = 2
	report[1] = 3
	report[3] = 77
	report[4] = 99
	d.HandleTransfer(context.Background(), 3, usbip.DirOut, report)
	if _, ok := d.HandleControl(0x21, 9, 0x0202, 3, 48, report); !ok {
		t.Fatal("control report rejected")
	}
	for range 2 {
		select {
		case b := <-feedback:
			if b[3] != 77 || b[4] != 99 {
				t.Fatal(b)
			}
		case <-time.After(100 * time.Millisecond):
			t.Fatal("rumble waited for new input")
		}
	}
	select {
	case <-done:
		t.Fatal("source IN finished without source")
	default:
	}
	cancel()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("input cancellation stuck")
	}
}

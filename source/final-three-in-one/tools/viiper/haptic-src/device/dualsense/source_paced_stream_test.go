package dualsense

import (
	"context"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usbip"
	"io"
	"log/slog"
	"net"
	"testing"
	"time"
)

func TestHapticFeedbackStreamContinuesWithNoFreshInput(t *testing.T) {
	h := &hapticHandler{}
	u, e := h.CreateDevice(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if e != nil {
		t.Fatal(e)
	}
	d := u.(*DualSense)
	server, client := net.Pipe()
	defer server.Close()
	defer client.Close()
	streamDone := make(chan error, 1)
	var dev usb.Device = d
	go func() { streamDone <- h.StreamHandler()(server, &dev, slog.Default()) }()
	ready := time.Now().Add(time.Second)
	for {
		d.mtx.Lock()
		ok := d.rawOutputFunc != nil
		d.mtx.Unlock()
		if ok {
			break
		}
		if time.Now().After(ready) {
			t.Fatal("callback never registered")
		}
		time.Sleep(time.Millisecond)
	}
	d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	inDone := make(chan struct{})
	go func() { d.HandleTransfer(ctx, 4, usbip.DirIn, nil); close(inDone) }()
	outDone := make(chan struct{})
	go func() {
		report := make([]byte, 48)
		report[0] = 2
		report[1] = 3
		report[3] = 77
		report[4] = 99
		d.HandleTransfer(context.Background(), 3, usbip.DirOut, report)
		close(outDone)
	}()
	client.SetReadDeadline(time.Now().Add(time.Second))
	frame := make([]byte, HapticFeedbackFrameSize)
	if _, e := io.ReadFull(client, frame); e != nil {
		t.Fatal(e)
	}
	if frame[0] != hapticFrameHIDOutput || frame[7] != 77 || frame[8] != 99 {
		t.Fatalf("wrong feedback: %x", frame[:12])
	}
	select {
	case <-outDone:
	case <-time.After(time.Second):
		t.Fatal("feedback blocked by input reader")
	}
	select {
	case <-inDone:
		t.Fatal("IN completed without source")
	default:
	}
	cancel()
	client.Close()
	server.Close()
	select {
	case <-inDone:
	case <-time.After(time.Second):
		t.Fatal("IN cancellation blocked")
	}
	select {
	case <-streamDone:
	case <-time.After(time.Second):
		t.Fatal("stream close blocked")
	}
}

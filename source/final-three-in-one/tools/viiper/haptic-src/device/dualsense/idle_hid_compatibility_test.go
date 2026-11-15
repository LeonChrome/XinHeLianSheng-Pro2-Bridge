package dualsense

import (
	"context"
	"encoding/binary"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usbip"
	"testing"
	"time"
)

func TestSourcePacedIdleReadCompletesWithoutManufacturingSourceInput(t *testing.T) {
	d, err := New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if err != nil {
		t.Fatal(err)
	}
	state := *NewInputState()
	state.Buttons = 0x20
	state.GyroX = 1000
	state.AccelZ = 8192
	d.UpdateInputState(&state)
	first := d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	before := d.InputTransferStats()
	started := time.Now()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	idle := d.HandleTransfer(ctx, 4, usbip.DirIn, nil)
	if len(idle) != InputReportSize || time.Since(started) < sourcePacedIdleReadLimit/2 || time.Since(started) > 500*time.Millisecond {
		t.Fatalf("idle HID read did not complete within bounded compatibility wait: len=%d elapsed=%v", len(idle), time.Since(started))
	}
	if idle[8]&0x20 == 0 || binary.LittleEndian.Uint16(idle[16:]) != 0 || binary.LittleEndian.Uint16(idle[24:]) != 8192 {
		t.Fatal("idle compatibility lost held controls/gravity or replayed old rotation")
	}
	if step := binary.LittleEndian.Uint32(idle[28:]) - binary.LittleEndian.Uint32(first[28:]); step != 1 {
		t.Fatalf("idle manufactured elapsed sensor time: %d", step)
	}
	after := d.InputTransferStats()
	if after.Received != before.Received || after.Delivered != before.Delivered || after.Repeated != before.Repeated+1 {
		t.Fatalf("idle keepalive was counted as physical input: before=%+v after=%+v", before, after)
	}
}

func TestSourcePacedIdleCancellationDoesNotEmitCompatibilityReport(t *testing.T) {
	d, err := New(&device.CreateOptions{DeviceSpecific: `{"source_paced":true}`})
	if err != nil {
		t.Fatal(err)
	}
	d.HandleTransfer(context.Background(), 4, usbip.DirIn, nil)
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan []byte, 1)
	go func() { done <- d.HandleTransfer(ctx, 4, usbip.DirIn, nil) }()
	time.Sleep(5 * time.Millisecond)
	cancel()
	select {
	case report := <-done:
		if report != nil {
			t.Fatal("cancelled HID read returned a report")
		}
	case <-time.After(time.Second):
		t.Fatal("HID reader close/cancel stayed blocked")
	}
	if d.InputTransferStats().Repeated != 0 {
		t.Fatal("cancellation created an idle report")
	}
}

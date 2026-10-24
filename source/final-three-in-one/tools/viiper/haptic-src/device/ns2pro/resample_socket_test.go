package ns2pro

import (
	"context"
	"encoding/binary"
	viiperTesting "github.com/Alia5/VIIPER/_testing"
	"github.com/Alia5/VIIPER/virtualbus"
	"sync/atomic"
	"testing"
	"time"
)

func TestProtocolResamplerRealSocketClockAndUniqueCounts(t *testing.T) {
	d := enableClockTest(t, `{"source_paced":true,"protocol_clock_resample":true}`)
	server := viiperTesting.NewTestServer(t)
	defer server.UsbServer.Close()
	defer server.ApiServer.Close()
	bus, e := virtualbus.NewWithBusID(1)
	if e != nil {
		t.Fatal(e)
	}
	defer bus.Close()
	if _, e = bus.Add(d); e != nil {
		t.Fatal(e)
	}
	if e = server.UsbServer.AddBus(bus); e != nil {
		t.Fatal(e)
	}
	client := viiperTesting.NewUsbIpClient(t, server.UsbServer.Addr())
	devices, e := client.ListDevices()
	if e != nil || len(devices) != 1 {
		t.Fatal(devices, e)
	}
	imp, e := client.AttachDevice(devices[0].BusID)
	if e != nil {
		t.Fatal(e)
	}
	defer imp.Conn.Close()
	var received atomic.Uint32
	push := func() {
		n := received.Add(1)
		st := *NewInputState()
		st.LX = uint16(n)
		st.GyroZ = 1638
		st.AccelZ = 4096
		st.MotionTimestampUs = n * 7500
		d.UpdateInputState(st)
	}
	push()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan struct{})
	go func() {
		defer close(done)
		timer := time.NewTicker(time.Second / 133)
		defer timer.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-timer.C:
				push()
			}
		}
	}()
	started := time.Now()
	reports, unique, repeats, last := 0, 0, 0, uint16(0)
	var clocks []uint32
	for time.Since(started) < 2*time.Second {
		r, e := client.ReadInputReportWithTimeout(imp.Conn, time.Second)
		if e != nil {
			t.Fatal(e)
		}
		reports++
		marker := uint16(r[11]) | uint16(r[12]&15)<<8
		if marker == last {
			repeats++
		} else {
			unique++
			last = marker
		}
		ts := binary.LittleEndian.Uint32(r[0x2b:])
		if ts != 0 {
			clocks = append(clocks, ts)
		}
	}
	cancel()
	<-done
	if len(clocks) < 105 {
		t.Fatalf("not enough reports for SDL timestamp contract: %d", len(clocks))
	}
	coeff := uint64(1000) * uint64(clocks[104]-clocks[4]) / (100 * 4)
	if (coeff+100000)/200000 != 5 {
		t.Fatalf("host failed SDL native-clock timing: coefficient=%d actual_reports=%d", coeff, reports)
	}
	stats := d.InputTransferStats()
	if unique < 150 || repeats == 0 || stats.Repeated == 0 || stats.Overflow != 0 {
		t.Fatal(unique, repeats, stats)
	}
	if stats.Delivered+stats.Coalesced+stats.Overflow+uint64(stats.Depth) != stats.Received {
		t.Fatal("unaccounted sources", stats)
	}
	t.Logf("RESAMPLE_SOCKET seconds=%.3f wire_reports=%d source_received=%d unique_markers=%d held_repeat=%d delivered_sources=%d analog_coalesced=%d edge_drop=%d queue_max=%d SDL_ts_coeff=%d gyro_coeff=34.8", time.Since(started).Seconds(), reports, received.Load(), unique, repeats, stats.Delivered, stats.Coalesced, stats.Overflow, stats.MaxDepth, coeff)
}

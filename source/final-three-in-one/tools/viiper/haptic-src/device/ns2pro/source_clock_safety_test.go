package ns2pro

import (
	"context"
	"encoding/binary"
	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usbip"
	"testing"
	"time"
)

func enableClockTest(t *testing.T, metadata string) *NS2Pro {
	d, e := New(&device.CreateOptions{DeviceSpecific: metadata})
	if e != nil {
		t.Fatal(e)
	}
	d.HandleTransfer(context.Background(), 2, usbip.DirOut, featureCommand(0x02, FeatureIMU))
	d.HandleTransfer(context.Background(), 2, usbip.DirOut, featureCommand(0x04, FeatureIMU))
	d.HandleTransfer(context.Background(), 2, usbip.DirOut, selectReportCommand(ReportIDCommon))
	d.HandleTransfer(context.Background(), 2, usbip.DirOut, enableReportsCommand())
	return d
}
func TestStrictSourceClockAndDigitalEdges(t *testing.T) {
	d := enableClockTest(t, `{"source_paced":true}`)
	a := *NewInputState()
	a.Buttons = ButtonA
	a.MotionTimestampUs = 0xFFFFF000
	a.GyroX = 111
	b := a
	b.Buttons = 0
	b.MotionTimestampUs += 7500
	b.GyroX = 222
	d.UpdateInputState(a)
	d.UpdateInputState(b)
	first := d.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	second := d.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if int16(binary.LittleEndian.Uint16(first[0x37:])) != 111 || int16(binary.LittleEndian.Uint16(second[0x37:])) != 222 {
		t.Fatal("edge-associated IMU was overwritten")
	}
	ta, tb := binary.LittleEndian.Uint32(first[0x2b:]), binary.LittleEndian.Uint32(second[0x2b:])
	if ta != a.MotionTimestampUs || tb != b.MotionTimestampUs || tb-ta != 7500 {
		t.Fatal(ta, tb)
	}
	if got := binary.LittleEndian.Uint32(second[1:5]); got != 4294970 {
		t.Fatalf("millisecond counter reset at microsecond wrap: %d", got)
	}
	c, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	if d.HandleTransfer(c, 1, usbip.DirIn, nil) != nil {
		t.Fatal("strict source manufactured repeat")
	}
}
func TestResamplerWallClockMatchesSDL4msCalibrationWithoutBootstrap(t *testing.T) {
	var last uint64
	clocks := make([]uint64, 106)
	for i := 1; i <= 105; i++ {
		clocks[i] = protocolWallClock(time.Duration(i)*4*time.Millisecond, last, i > 1)
		last = clocks[i]
	}
	coeff := uint64(1000) * uint64(clocks[105]-clocks[5]) / (100 * 4)
	if coeff != 1000000 || (coeff+100000)/200000 != 5 {
		t.Fatalf("SDL native 34.8 branch not selected: %d", coeff)
	}
	// Wall gaps advance time, rather than hiding them by incrementing one report.
	if got := protocolWallClock(500*time.Millisecond, last, true); got != 500000 {
		t.Fatal(got)
	}
	if got := protocolWallClock(time.Duration(uint64(1)<<32)*time.Microsecond+4*time.Millisecond, 0xFFFFF000, true); uint32(got) != 704 || got != 4294968000 {
		t.Fatal("uint32 wrap", got)
	}
}
func TestResamplerSafetyBoundariesAndFreshRecovery(t *testing.T) {
	src := *NewInputState()
	src.Buttons = ButtonA
	src.LX = 3000
	src.GyroX = 500
	src.AccelZ = 4096
	for _, tt := range []struct {
		age   time.Duration
		stage uint8
	}{{99 * time.Millisecond, 0}, {100 * time.Millisecond, 1}, {499 * time.Millisecond, 1}, {500 * time.Millisecond, 2}} {
		st, stage := resampleSafetyState(src, tt.age)
		if stage != tt.stage {
			t.Fatal(stage, tt)
		}
		if stage >= 1 && (st.GyroX != 0 || st.AccelZ != src.AccelZ) {
			t.Fatal("unsafe motion")
		}
		if stage == 1 && (st.Buttons != src.Buttons || st.LX != src.LX) {
			t.Fatal("early digital release")
		}
		if stage == 2 && (st.Buttons != 0 || st.LX != StickCenter) {
			t.Fatal("stuck controls")
		}
	}
	d := enableClockTest(t, `{"source_paced":true,"protocol_clock_resample":true,"input_interval_ms":15}`)
	if d.SourcePacedInput() || d.inputIntervalMs != 4 {
		t.Fatal("wrong resample routing")
	}
	d.UpdateInputState(src)
	d.stateMu.Lock()
	d.lastSourceArrival = time.Now().Add(-600 * time.Millisecond)
	d.stateMu.Unlock()
	stale := d.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if binary.LittleEndian.Uint16(stale[0x37:]) != 0 {
		t.Fatal("stale gyro")
	}
	before := d.InputTransferStats()
	fresh := src
	fresh.Buttons = 0
	fresh.GyroX = 1234
	d.UpdateInputState(fresh)
	resumed := d.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	if int16(binary.LittleEndian.Uint16(resumed[0x37:])) != 1234 || d.staleStage != 0 {
		t.Fatal("fresh recovery failed")
	}
	after := d.InputTransferStats()
	if before.Delivered != 0 || after.Delivered != 1 || after.Received != 2 || after.Repeated != 1 || after.StaleDropped != 1 {
		t.Fatal(before, after)
	}
}

func TestResamplerMillisecondCounterDoesNotWrapAt71Minutes(t *testing.T) {
	d := enableClockTest(t, `{"source_paced":true,"protocol_clock_resample":true}`)
	d.clockBase = time.Now().Add(-time.Duration(uint64(1)<<32)*time.Microsecond - 4*time.Millisecond)
	r := d.HandleTransfer(context.Background(), 1, usbip.DirIn, nil)
	ms, us := binary.LittleEndian.Uint32(r[1:5]), binary.LittleEndian.Uint32(r[0x2b:0x2f])
	if ms < 4294967 || ms > 4294990 || us > 30000 {
		t.Fatalf("ms=%d micro_wrap=%d", ms, us)
	}
}

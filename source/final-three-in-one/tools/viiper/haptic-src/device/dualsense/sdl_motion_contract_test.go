package dualsense

import (
	"bytes"
	"context"
	"encoding/binary"
	"math"
	"testing"
	"time"

	"github.com/Alia5/VIIPER/usbip"
)

// These expectations are the host contract in SDL2/SDL3's SDL_hidapi_ps5.c,
// not a copy of the VIIPER encoder's own constants.
func TestSDLMotionCompositeHIDContract(t *testing.T) {
	dev, err := (&hapticHandler{}).CreateDevice(nil)
	if err != nil {
		t.Fatal(err)
	}
	desc := dev.GetDescriptor()
	if desc.Device.IDVendor != 0x054c || desc.Device.IDProduct != 0x0ce6 {
		t.Fatal("SDL's Sony DualSense sensor capability path cannot identify device")
	}
	if desc.NumInterfaces() != 4 {
		t.Fatalf("composite interface count = %d, want 4", desc.NumInterfaces())
	}
	hid, ok := desc.Interface(3)
	if !ok || hid.Descriptor.BInterfaceClass != 3 || hid.HID == nil {
		t.Fatal("composite HID interface 3 absent")
	}
	if len(hid.Endpoints) != 2 || hid.Endpoints[0].BEndpointAddress != 0x84 ||
		hid.Endpoints[0].WMaxPacketSize != 64 {
		t.Fatal("64-byte interrupt IN endpoint missing")
	}
	wire, err := hid.HID.ReportBytes()
	if err != nil {
		t.Fatal(err)
	}
	// Parse HID short items so counts are checked from the advertised descriptor,
	// independently of InputReportSize and feature response implementations.
	type key struct{ kind, id int }
	bits := map[key]int{}
	id, size, count := 0, 0, 0
	for offset := 0; offset < len(wire); {
		prefix := wire[offset]
		offset++
		if prefix == 0xfe {
			t.Fatal("unexpected HID long item")
		}
		n := int(prefix & 3)
		if n == 3 {
			n = 4
		}
		if offset+n > len(wire) {
			t.Fatal("truncated HID descriptor")
		}
		value := 0
		for i := 0; i < n; i++ {
			value |= int(wire[offset+i]) << (8 * i)
		}
		offset += n
		kind, tag := (prefix>>2)&3, prefix>>4
		if kind == 1 {
			switch tag {
			case 7:
				size = value
			case 8:
				id = value
			case 9:
				count = value
			}
		} else if kind == 0 && (tag == 8 || tag == 9 || tag == 11) {
			bits[key{int(tag), id}] += size * count
		}
	}
	for item, want := range map[key]int{{8, 1}: 63 * 8, {11, 5}: 40 * 8, {11, 9}: 19 * 8, {11, 0x20}: 63 * 8} {
		if bits[item] != want {
			t.Fatalf("report descriptor kind=%d id=%02x bits=%d want=%d", item.kind, item.id, bits[item], want)
		}
	}
}

func TestSDLMotionControlFeaturesOnCompositeInterface3(t *testing.T) {
	dev, err := (&hapticHandler{}).CreateDevice(nil)
	if err != nil {
		t.Fatal(err)
	}
	d := dev.(*DualSense)
	for _, request := range []struct{ id, length, want uint16 }{
		{5, 41, 41}, {5, 64, 41}, {5, 35, 35}, {9, 64, 20}, {0x20, 64, 64},
	} {
		response, handled := d.HandleControl(0xa1, 1, 0x0300|request.id, 3, request.length, nil)
		if !handled || len(response) != int(request.want) || response[0] != byte(request.id) {
			t.Fatalf("HID GetFeature id=%02x wLength=%d: handled=%t bytes=%d", request.id, request.length, handled, len(response))
		}
	}
	input, handled := d.HandleControl(0xa1, 1, 0x0101, 3, 64, nil)
	if !handled || len(input) != 64 || input[0] != 1 {
		t.Fatal("GetInputReport not available on composite HID interface")
	}
}

func TestSDLMotionCalibrationScalingMatchesBridgeUnits(t *testing.T) {
	d, err := New(nil)
	if err != nil {
		t.Fatal(err)
	}
	report, handled := d.HandleControl(0xa1, 1, 0x0305, 0, 64, nil)
	if !handled || len(report) < 35 {
		t.Fatal("SDL calibration read requires at least 35 bytes")
	}
	s16 := func(off int) float64 { return float64(int16(binary.LittleEndian.Uint16(report[off:]))) }
	gyroNumerator := (s16(19) + s16(21)) * 1024
	for axis := 0; axis < 3; axis++ {
		bias := s16(1 + axis*2)
		sensitivity := gyroNumerator / (s16(7+axis*4) - s16(9+axis*4))
		// SDL rejects large biases or a sensitivity too far from its nominal 64.
		if math.Abs(bias) > 1024 || math.Abs(1-sensitivity/64) > .5 {
			t.Fatalf("axis %d: SDL will reject hardware gyro calibration", axis)
		}
		radians := (16384 - bias) * sensitivity / 1024 * math.Pi / 180
		if math.Abs(radians-1000*math.Pi/180) > 1e-9 {
			t.Fatalf("gyro axis %d maps 16384 raw to %f rad/s", axis, radians)
		}
		plus, minus := s16(23+axis*4), s16(25+axis*4)
		accelBias := plus - (plus-minus)/2
		accelSensitivity := 2 * 8192 / (plus - minus)
		if math.Abs(accelBias) > 1024 || math.Abs(1-accelSensitivity) > .5 {
			t.Fatalf("axis %d: SDL will reject hardware accel calibration", axis)
		}
		metresPerSecond2 := (8192 - accelBias) * accelSensitivity / 8192 * 9.80665
		if math.Abs(metresPerSecond2-9.80665) > 1e-9 {
			t.Fatalf("accel axis %d calibration mismatch", axis)
		}
	}
}

func TestSDLMotionReportOffsetsAndSignedRange(t *testing.T) {
	d, err := New(nil)
	if err != nil {
		t.Fatal(err)
	}
	state := &InputState{
		GyroX: -32768, GyroY: 12345, GyroZ: 32767,
		AccelX: -8192, AccelY: 8192, AccelZ: -123,
	}
	inputWire, err := state.MarshalBinary()
	if err != nil {
		t.Fatal(err)
	}
	var decoded InputState
	if err := decoded.UnmarshalBinary(inputWire); err != nil {
		t.Fatal(err)
	}
	report := d.buildUSBInputReport(&decoded, d.metaState)
	if len(report) != 64 || report[0] != 1 {
		t.Fatal("SDL must receive USB full report, not 10-byte simple report")
	}
	for axis, want := range []int16{-32768, 12345, 32767, -8192, 8192, -123} {
		// SDL's common state starts one byte after report ID.
		got := int16(binary.LittleEndian.Uint16(report[1+15+axis*2:]))
		if got != want {
			t.Fatalf("SDL sensor axis %d = %d, want %d", axis, got, want)
		}
	}
}

func TestSDLMotionFirstInterruptReportIsFullWithoutManagerPush(t *testing.T) {
	dev, err := (&hapticHandler{}).CreateDevice(nil)
	if err != nil {
		t.Fatal(err)
	}
	started := time.Now()
	ctx, cancel := context.WithTimeout(context.Background(), 16*time.Millisecond)
	defer cancel()
	report := dev.HandleTransfer(ctx, 4, usbip.DirIn, nil)
	if len(report) != 64 || report[0] != 1 {
		t.Fatal("initial interrupt read would make SDL classify device as simple Bluetooth")
	}
	t.Logf("first initial HID report elapsed_us=%d bytes=%d", time.Since(started).Microseconds(), len(report))
}

func TestSDLMotionTimestampProgressAnd32BitRollover(t *testing.T) {
	d, err := New(nil)
	if err != nil {
		t.Fatal(err)
	}
	last := uint32(0)
	for i := 0; i < 8; i++ {
		time.Sleep(time.Millisecond)
		report := d.buildUSBInputReport(d.inputState, d.metaState)
		current := binary.LittleEndian.Uint32(report[28:32])
		if current <= last {
			t.Fatal("sensor timestamp did not advance")
		}
		last = current
	}
	// Simulate elapsed times immediately either side of the 32-bit timer wrap.
	// uint32 tick at 3 MHz wraps after about 23.9 minutes.
	wrapUS := int64(uint64(1)<<32) / 3
	d.timestampBase = time.Now().Add(-time.Duration(wrapUS-100000) * time.Microsecond)
	before := binary.LittleEndian.Uint32(d.buildUSBInputReport(d.inputState, d.metaState)[28:32])
	d.timestampBase = time.Now().Add(-time.Duration(wrapUS+100000) * time.Microsecond)
	after := binary.LittleEndian.Uint32(d.buildUSBInputReport(d.inputState, d.metaState)[28:32])
	if before < 0xf0000000 || after > 0x10000000 || uint32(after-before) > 700000 {
		t.Fatalf("timestamp rollover incompatible with SDL unsigned delta: before=%d after=%d", before, after)
	}
}

func TestSDLMotionStoppedOutputDoesNotGateInputSensors(t *testing.T) {
	d, err := New(nil)
	if err != nil {
		t.Fatal(err)
	}
	s := &InputState{GyroX: 100, GyroY: -200, GyroZ: 300, AccelY: 8192}
	first := d.buildUSBInputReport(s, d.metaState)
	d.HandleControl(0x21, 9, 0x0202, 0, 48, makeStoppedSDLReport())
	second := d.buildUSBInputReport(s, d.metaState)
	if !bytes.Equal(first[16:28], second[16:28]) {
		t.Fatal("ordinary effect control changed or disabled sensors")
	}
}

func makeStoppedSDLReport() []byte {
	report := make([]byte, 48)
	report[0] = 2
	return report
}

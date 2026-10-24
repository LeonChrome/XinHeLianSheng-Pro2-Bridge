package nspro

import (
	"bytes"
	"encoding/binary"
	"math"
	"testing"

	"github.com/Alia5/VIIPER/device/ns2pro"
)

// The C# protocol boundary supplies (source Y,-source X,source Z). This test
// independently applies SDL Switch 1's (-Y,+Z,-X) convention and advertised
// SPI sensitivity, rather than asserting that nonzero bytes imply motion.
func TestSDLPhysicalAxesAndCalibratedUnits(t *testing.T) {
	p := pad(t)
	read(t, p)
	command(t, p, 3, 0x30)
	command(t, p, 0x40, 1)
	for _, source := range [][3]int16{{1638, 0, 0}, {0, 1638, 0}, {0, 0, 1638}, {111, 222, 333}, {-4096, 2048, 3396}} {
		state := *ns2pro.NewInputState()
		state.AccelX, state.AccelY, state.AccelZ = source[1], -source[0], source[2]
		state.GyroX, state.GyroY, state.GyroZ = source[1], -source[0], source[2]
		p.UpdateInputState(state)
		r := read(t, p)
		for slot := 0; slot < 3; slot++ {
			o := 13 + 12*slot
			var acceleration, angular [3]float64
			for axis := 0; axis < 3; axis++ {
				acceleration[axis] = float64(int16(binary.LittleEndian.Uint16(r[o+axis*2:]))) * 4 / 16384
				angular[axis] = float64(int16(binary.LittleEndian.Uint16(r[o+6+axis*2:]))) * 936 / 13371
			}
			gotAccel := [3]float64{-acceleration[1], acceleration[2], -acceleration[0]}
			gotGyro := [3]float64{-angular[1], angular[2], -angular[0]}
			expected := [3]int16{source[0], source[2], -source[1]}
			for axis := 0; axis < 3; axis++ {
				if math.Abs(gotAccel[axis]-float64(expected[axis])/4096) > .000001 ||
					math.Abs(gotGyro[axis]-float64(expected[axis])/16.384) > 936.0/13371.0/2+.000001 {
					t.Fatalf("source=%v slot=%d axis=%d: accel=%v gyro=%v", source, slot, axis, gotAccel, gotGyro)
				}
			}
		}
	}
	// Calibration is exposed to the receiver, not only assumed by the sender.
	for axis := uint32(0); axis < 3; axis++ {
		accel := uint16(flashByte(0x6026+2*axis)) | uint16(flashByte(0x6027+2*axis))<<8
		gyro := uint16(flashByte(0x6032+2*axis)) | uint16(flashByte(0x6033+2*axis))<<8
		if accel != 16384 || gyro != 13371 {
			t.Fatalf("axis %d sensitivity mismatch: accel=%d gyro=%d", axis, accel, gyro)
		}
	}
}

func TestThreeIMUSlotsAreHeldSamplesNotExtraReports(t *testing.T) {
	p := pad(t)
	read(t, p)
	command(t, p, 3, 0x30)
	command(t, p, 0x40, 1)
	s := *ns2pro.NewInputState()
	s.AccelX, s.AccelY, s.AccelZ = 111, -222, 333
	s.GyroX, s.GyroY, s.GyroZ = -444, 555, -666
	s.MotionTimestampUs = 7500
	p.UpdateInputState(s)
	r := read(t, p)
	if !bytes.Equal(r[13:25], r[25:37]) || !bytes.Equal(r[13:25], r[37:49]) {
		t.Fatal("held physical sample unexpectedly interpolated into invented slot values")
	}
	noInput(t, p) // One source update emits one report, not three/250 Hz replay.
	if p.state.MotionTimestampUs != 7500 {
		t.Fatal("virtual protocol advanced the physical source timestamp")
	}
}

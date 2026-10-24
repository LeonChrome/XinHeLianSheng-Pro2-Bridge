package nspro

import (
	"math"
	"testing"
)

func readFactoryPair(offset uint32) (int, int) {
	a, b, c := flashByte(offset), flashByte(offset+1), flashByte(offset+2)
	return int(a) | int(b&15)<<8, int(b>>4) | int(c)<<4
}
func TestFactoryStickCalibrationCoversAll12BitSteps(t *testing.T) {
	for _, right := range []bool{false, true} {
		base := uint32(0x603d)
		if right {
			base += 9
		}
		maxOff, centerOff, minOff := base, base+3, base+6
		if right {
			centerOff, minOff, maxOff = base, base+3, base+6
		}
		maxX, maxY := readFactoryPair(maxOff)
		cx, cy := readFactoryPair(centerOff)
		minX, minY := readFactoryPair(minOff)
		if cx != 2048 || cy != 2048 || maxX != 2047 || maxY != 2047 || minX != 2048 || minY != 2048 {
			t.Fatal(right, cx, cy, maxX, maxY, minX, minY)
		}
		last := -2.0
		for raw := 0; raw <= 4095; raw++ {
			v := float64(raw - cx)
			if v < 0 {
				v /= float64(minX)
			} else {
				v /= float64(maxX)
			}
			if v < -1 || v > 1 || (raw > 0 && v <= last) {
				t.Fatal("clipped or duplicated stick step", right, raw, v, last)
			}
			last = v
		}
		if math.Abs(last-1) > 1e-9 {
			t.Fatal(last)
		}
	}
}

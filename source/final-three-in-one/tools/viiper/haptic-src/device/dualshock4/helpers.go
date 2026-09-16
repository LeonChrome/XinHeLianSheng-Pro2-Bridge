package dualshock4

import (
	"encoding/hex"
	"fmt"
	"math"
)

// GyroDpsToRaw converts a gyro angular velocity value in degrees/second (°/s)
// into the fixed-point raw int16 wire/report representation.
func GyroDpsToRaw(dps float64) int16 {
	return int16(min(max(math.Round(dps*GyroCountsPerDps), math.MinInt16), math.MaxInt16))
}

// GyroRawToDps converts a fixed-point raw gyro value into degrees/second (°/s).
func GyroRawToDps(raw int16) float64 {
	return float64(raw) / GyroCountsPerDps
}

// AccelMS2ToRaw converts an acceleration value in meters/second^2 (m/s²)
// into the fixed-point raw int16 wire/report representation.
func AccelMS2ToRaw(ms2 float64) int16 {
	return int16(min(max(math.Round(ms2*AccelCountsPerMS2), math.MinInt16), math.MaxInt16))
}

// AccelRawToMS2 converts a fixed-point raw accelerometer value into m/s².
func AccelRawToMS2(raw int16) float64 {
	return float64(raw) / AccelCountsPerMS2
}

// DefaultAccelRaw returns the default ("neutral") accelerometer vector for a
// controller lying flat on a table.
func DefaultAccelRaw() (x, y, z int16) {
	return DefaultAccelXRaw, DefaultAccelYRaw, DefaultAccelZRaw
}

func ds4FirmwareVersionString() string {
	return fmt.Sprintf("%04X.%04X", uint16(SoftwareVersionMajor), SoftwareVersionMinor)
}

func telemetryVoltageU16(voltage float64) uint16 {
	raw := int(math.Round(voltage * 1000.0 / 1.5625))
	raw = min(max(raw, 0), 0xFFF)
	return uint16(raw)
}

func telemetryTemperatureU16(temperature float64) uint16 {
	return uint16(min(max(math.Round((2470.0-temperature*26.0)/0.78125), 0), 4095))
}

func encodeTouchCoords(b []byte, x, y uint16) {
	x = min(x, TouchpadMaxX)
	y = min(y, TouchpadMaxY)
	b[0] = uint8(x & 0xFF)
	b[1] = uint8((x>>8)&0x0F) | uint8((y&0x0F)<<4)
	b[2] = uint8(y >> 4)
}

func serialStringToBytes(str string) [8]byte {
	var out [8]byte
	if len(str) != 16 {
		return out
	}
	n, err := hex.Decode(out[:], []byte(str))
	if err != nil || n != len(out) {
		return out
	}
	return out
}

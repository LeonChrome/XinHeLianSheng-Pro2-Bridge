package ns2pro

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"testing"

	"github.com/Alia5/VIIPER/usbip"
)

// These are synthetic protocol fixtures, not captures from a physical game.
// Preserve both motor headers and all six frames through the production handlers.
func TestNativeRumbleFidelityFixtures(t *testing.T) {
	for _, route := range []string{"interrupt", "control"} {
		dev, err := New(nil)
		if err != nil {
			t.Fatal(err)
		}
		var outputs [][]byte
		dev.SetOutputCallback(func(out OutputState) {
			wire, err := out.MarshalBinary()
			if err != nil {
				t.Fatal(err)
			}
			outputs = append(outputs, wire)
		})
		for _, kind := range []string{"start", "update", "stop"} {
			report := make([]byte, OutputReportSize)
			report[0], report[1], report[17] = 2, 0x5a, 0x53
			if kind != "stop" {
				for i := 2; i < 33; i++ {
					if i != 17 {
						report[i] = byte(i * 7)
					}
				}
				if kind == "update" {
					report[12] ^= 0x55
					report[31] ^= 0xaa
				}
			}
			before := len(outputs)
			if route == "interrupt" {
				dev.HandleTransfer(context.Background(), 1, usbip.DirOut, report)
			} else {
				_, handled := dev.HandleControl(0x21, 9, 0x0202, 0, uint16(len(report)), report)
				if !handled {
					t.Fatal("control output rejected")
				}
			}
			if len(outputs) != before+1 {
				t.Fatal("output missing or duplicated")
			}
			wire := outputs[len(outputs)-1]
			if len(wire) != OutputWireSize || !bytes.Equal(wire[:32], report[1:33]) || wire[32] != OutputFlagRumble {
				t.Fatalf("%s/%s altered native motor payload", route, kind)
			}
			if dir := os.Getenv("PRO2_NATIVE_FIXTURE_DIR"); dir != "" {
				if err := os.MkdirAll(dir, 0755); err != nil {
					t.Fatal(err)
				}
				name := filepath.Join(dir, route+"-"+kind)
				if err := os.WriteFile(name+"-usb.bin", report, 0644); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(name+"-feedback.bin", wire, 0644); err != nil {
					t.Fatal(err)
				}
			}
		}
	}
}

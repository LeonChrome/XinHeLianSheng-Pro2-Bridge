package dualsense

import (
	"context"
	"encoding/hex"
	"fmt"
	"testing"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usbip"
)

func TestFourNativeHapticIdentitiesAndCallbacks(t *testing.T) {
	var devices [4]*DualSense
	var raw, pcm [4]int
	identities := map[string]bool{}
	for i := range devices {
		dev, err := (&hapticHandler{}).CreateDevice(&device.CreateOptions{DeviceSpecific: fmt.Sprintf(
			`{"serial_number":"LC-V624-DS5-S%d","mac_address":"02:50:52:4F:35:%02X"}`, i+1, i+1)})
		if err != nil {
			t.Fatal(err)
		}
		d := dev.(*DualSense)
		// The physical native descriptor has no USB serial; keep that contract.
		if d.GetDescriptor().Device.ISerialNumber != 0 {
			t.Fatal("native PS5 descriptor unexpectedly changed")
		}
		feature, ok := d.HandleControl(0xa1, 1, 0x0309, 3, 64, nil)
		if !ok || len(feature) < 7 {
			t.Fatal("missing native identity feature")
		}
		key := hex.EncodeToString(feature[1:7])
		if identities[key] {
			t.Fatal("duplicate PS5 feature identity")
		}
		identities[key] = true
		slot := i
		d.SetRawOutputCallback(func(b []byte) {
			raw[slot]++
			if b[3] != byte(30+slot) {
				t.Errorf("raw feedback crossed slot %d", slot+1)
			}
		})
		d.SetHapticAudioCallback(func(b []byte) {
			pcm[slot]++
			if len(b) != 384 || b[0] != byte(slot+1) {
				t.Errorf("PCM crossed slot %d", slot+1)
			}
		})
		d.HandleControl(1, 0x0b, 1, 1, 0, nil)
		devices[i] = d
	}
	for slot, d := range devices {
		d.HandleTransfer(context.Background(), 3, usbip.DirOut, rumbleRoutingReport(48, byte(30+slot), 90))
		payload := make([]byte, 384)
		payload[0] = byte(slot + 1)
		d.HandleTransfer(context.Background(), 1, usbip.DirOut, payload)
		for i := range devices {
			want := 0
			if i <= slot {
				want = 1
			}
			if raw[i] != want || pcm[i] != want {
				t.Fatalf("callback isolation failed raw=%v pcm=%v", raw, pcm)
			}
		}
	}
}

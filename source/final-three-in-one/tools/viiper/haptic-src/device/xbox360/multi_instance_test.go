package xbox360

import (
	"context"
	"fmt"
	"sync"
	"testing"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/usbip"
)

func TestFourIndependentXboxIdentitiesAndFeedback(t *testing.T) {
	var devices [4]*Xbox360
	var counts [4]int
	serials := map[string]bool{}
	for i := range devices {
		serial := fmt.Sprintf("LC-V624-XBOX-S%d", i+1)
		d, err := New(&device.CreateOptions{DeviceSpecific: fmt.Sprintf(`{"serial_number":%q,"source_paced":true}`, serial)})
		if err != nil {
			t.Fatal(err)
		}
		actual := d.GetDescriptor().Strings[3]
		if actual != serial || serials[actual] {
			t.Fatalf("slot %d lost unique USB serial: %q", i+1, actual)
		}
		serials[actual] = true
		slot := i
		d.SetRumbleCallback(func(r XRumbleState) {
			counts[slot]++
			if r.LeftMotor != byte(80+slot) || r.RightMotor != byte(30+slot) {
				t.Errorf("feedback wrong for slot %d", slot+1)
			}
		})
		devices[i] = d
	}
	for slot, d := range devices {
		d.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0, 8, 0, byte(80 + slot), byte(30 + slot), 0, 0, 0})
		for i := range counts {
			want := 0
			if i <= slot {
				want = 1
			}
			if counts[i] != want {
				t.Fatalf("feedback crossed slots %v", counts)
			}
		}
	}
	baseline, _ := New(nil)
	if baseline.GetDescriptor().Strings[3] != "296013F" {
		t.Fatal("unspecified serial must preserve legacy default")
	}
}

func TestConcurrentRumbleCallbackReplacement(t *testing.T) {
	d, _ := New(nil)
	var workers sync.WaitGroup
	workers.Add(2)
	go func() {
		defer workers.Done()
		for i := 0; i < 1000; i++ {
			d.SetRumbleCallback(func(XRumbleState) {})
			d.SetRumbleCallback(nil)
		}
	}()
	go func() {
		defer workers.Done()
		for i := 0; i < 1000; i++ {
			d.HandleTransfer(context.Background(), 1, usbip.DirOut, []byte{0, 8, 0, 1, 2, 0, 0, 0})
		}
	}()
	workers.Wait()
}

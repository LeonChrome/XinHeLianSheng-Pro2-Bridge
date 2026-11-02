package ns2pro

import (
	"fmt"
	"github.com/Alia5/VIIPER/device"
	"testing"
)

func TestFourPro2NativeUsbIdentities(t *testing.T) {
	identities := map[string]bool{}
	for slot := 1; slot <= 4; slot++ {
		d, err := New(&device.CreateOptions{DeviceSpecific: fmt.Sprintf(`{"serial_number":"LC-V624-NS2PRO-S%d"}`, slot)})
		if err != nil {
			t.Fatal(err)
		}
		serial := d.GetDescriptor().Strings[3]
		if serial != fmt.Sprintf("S%d", slot) || identities[serial] {
			t.Fatalf("slot %d lost unique native serial: %q", slot, serial)
		}
		identities[serial] = true
	}
}

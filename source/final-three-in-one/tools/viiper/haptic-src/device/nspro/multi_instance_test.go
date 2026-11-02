package nspro

import (
	"fmt"
	"github.com/Alia5/VIIPER/device"
	"testing"
)

func TestFourPro1UsbAndProtocolIdentities(t *testing.T) {
	serials := map[string]bool{}
	macs := map[[6]byte]bool{}
	for slot := 1; slot <= 4; slot++ {
		serial := fmt.Sprintf("PRO2-NSPRO-S%d", slot)
		p, err := New(&device.CreateOptions{DeviceSpecific: fmt.Sprintf(`{"serial_number":%q}`, serial)})
		if err != nil {
			t.Fatal(err)
		}
		actual := p.GetDescriptor().Strings[3]
		if actual != serial || serials[actual] || macs[p.mac] {
			t.Fatalf("slot %d has duplicate USB or protocol identity", slot)
		}
		serials[actual] = true
		macs[p.mac] = true
	}
}

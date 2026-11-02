package ns2pro

import (
	"fmt"
	"sync"
	"testing"

	"github.com/Alia5/VIIPER/device"
)

func TestConcurrentIndependentSerialRegistration(t *testing.T) {
	var wg sync.WaitGroup
	for slot := 0; slot < 8; slot++ {
		wg.Add(1)
		go func(slot int) {
			defer wg.Done()
			for i := 0; i < 20; i++ {
				serial := fmt.Sprintf("PRO2-CONCURRENT-%d-%d", slot, i)
				_, err := (&handler{}).CreateDevice(&device.CreateOptions{DeviceSpecific: fmt.Sprintf(`{"serial_number":%q}`, serial)})
				if err != nil {
					t.Error(err)
				}
				// Releasing a stream can run concurrently with another slot's add.
				serialsMu.Lock()
				if _, ok := serials[serial]; !ok {
					t.Errorf("lost registration: %s", serial)
				}
				delete(serials, serial)
				serialsMu.Unlock()
			}
		}(slot)
	}
	wg.Wait()
}

package usb

import (
	"github.com/Alia5/VIIPER/usb"
	"testing"
	"time"
)

func TestInterruptIntervalUsesSpeedNotRawMilliseconds(t *testing.T) {
	desc := &usb.Descriptor{Device: usb.DeviceDescriptor{Speed: 3}, Interfaces: []usb.InterfaceConfig{{Endpoints: []usb.EndpointDescriptor{{BEndpointAddress: 0x84, BMAttributes: 3, BInterval: 6}}}}}
	if got := endpointInterval(desc, 4); got != 4*time.Millisecond {
		t.Fatalf("high speed6: %s", got)
	}
	desc.Device.Speed = 2
	if got := endpointInterval(desc, 4); got != 6*time.Millisecond {
		t.Fatalf("full speed6: %s", got)
	}
	desc.Device.Speed = 3
	desc.Interfaces[0].Endpoints[0].BInterval = 4
	if got := endpointInterval(desc, 4); got != time.Millisecond {
		t.Fatalf("high speed4: %s", got)
	}
}

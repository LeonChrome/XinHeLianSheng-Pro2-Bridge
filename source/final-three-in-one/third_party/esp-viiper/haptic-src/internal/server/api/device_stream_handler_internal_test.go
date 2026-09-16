package api

import "testing"

type namedDeviceType struct {
	name string
}

func (d namedDeviceType) DeviceType() string {
	return d.name
}

func TestInferDeviceTypePrefersExplicitDeviceType(t *testing.T) {
	got := inferDeviceType(namedDeviceType{name: "DualSenseHaptic"})
	if got != "dualsensehaptic" {
		t.Fatalf("inferDeviceType() = %q, want %q", got, "dualsensehaptic")
	}
}

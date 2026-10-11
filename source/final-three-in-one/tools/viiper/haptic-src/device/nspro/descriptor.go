// Package nspro implements the first-generation Switch Pro USB protocol.
// Protocol framing is based on this repository's switch1_protocol.cpp;
// USB initialization and IMU calibration were cross-checked against SDL.
package nspro

import (
	"github.com/Alia5/VIIPER/usb"
	"github.com/Alia5/VIIPER/usb/hid"
)

func makeDescriptor(serial string) usb.Descriptor {
	items := []hid.Item{
		hid.UsagePage{Page: 1}, hid.Usage{Usage: 5},
		hid.AnyItem{Type: hid.ItemTypeMain, Tag: 0xA, Data: hid.Data{1}},
		hid.UsagePage{Page: 0xFF01}, hid.LogicalMinimum{Min: 0}, hid.LogicalMaximum{Max: 255},
		hid.ReportSize{Bits: 8}, hid.ReportCount{Count: 63},
	}
	for _, id := range []uint8{0x21, 0x30, 0x81} {
		items = append(items, hid.ReportID{ID: id}, hid.Usage{Usage: uint16(id)}, hid.Input{Flags: 2})
	}
	// Standard HID fallback for programs that don't perform the Nintendo handshake.
	items = append(items,
		hid.ReportID{ID: 0x3F}, hid.UsagePage{Page: 9}, hid.UsageMinimum{Min: 1}, hid.UsageMaximum{Max: 16},
		hid.LogicalMinimum{Min: 0}, hid.LogicalMaximum{Max: 1}, hid.ReportSize{Bits: 1}, hid.ReportCount{Count: 16}, hid.Input{Flags: 2},
		hid.UsagePage{Page: 1}, hid.Usage{Usage: 0x39}, hid.LogicalMaximum{Max: 7},
		hid.ReportSize{Bits: 4}, hid.ReportCount{Count: 1}, hid.Input{Flags: 0x42}, hid.Input{Flags: 1},
		hid.Usage{Usage: 0x30}, hid.Usage{Usage: 0x31}, hid.Usage{Usage: 0x33}, hid.Usage{Usage: 0x34},
		hid.LogicalMaximum{Max: 65535}, hid.ReportSize{Bits: 16}, hid.ReportCount{Count: 4}, hid.Input{Flags: 2},
		hid.UsagePage{Page: 0xFF01}, hid.LogicalMaximum{Max: 255}, hid.ReportSize{Bits: 8}, hid.ReportCount{Count: 63},
	)
	for _, id := range []uint8{0x01, 0x10, 0x80} {
		items = append(items, hid.ReportID{ID: id}, hid.Usage{Usage: uint16(id)}, hid.Output{Flags: 2})
	}
	items = append(items, hid.AnyItem{Type: hid.ItemTypeMain, Tag: 0xC})
	return usb.Descriptor{
		Device: usb.DeviceDescriptor{BcdUSB: 0x0200, BMaxPacketSize0: 64,
			IDVendor: 0x057E, IDProduct: 0x2009, BcdDevice: 0x0200,
			IManufacturer: 1, IProduct: 2, ISerialNumber: 3, BNumConfigurations: 1, Speed: 2},
		Configuration: usb.ConfigurationDescriptor{BConfigurationValue: 1, BMAttributes: 0x80, BMaxPower: 50},
		Interfaces: []usb.InterfaceConfig{{
			Descriptor: usb.InterfaceDescriptor{BInterfaceNumber: 0, BNumEndpoints: 2, BInterfaceClass: 3},
			HID: &usb.HIDFunction{Descriptor: usb.HIDDescriptor{BcdHID: 0x0111,
				Descriptors: []usb.HIDSubDescriptor{{Type: usb.ReportDescType}}}, ReportDescriptor: hid.ReportDescriptor{Items: items}},
			Endpoints: []usb.EndpointDescriptor{
				{BEndpointAddress: 0x81, BMAttributes: 3, WMaxPacketSize: 64, BInterval: 1},
				{BEndpointAddress: 0x01, BMAttributes: 3, WMaxPacketSize: 64, BInterval: 1},
			},
		}},
		Strings: map[uint8]string{0: "\u0409", 1: "Nintendo", 2: "Pro Controller", 3: serial},
	}
}

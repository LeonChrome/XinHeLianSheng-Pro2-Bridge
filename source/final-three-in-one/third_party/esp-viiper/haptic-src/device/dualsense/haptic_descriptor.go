package dualsense

import "github.com/Alia5/VIIPER/usb"

const (
	HapticAudioOutEndpoint  = 0x01
	HapticAudioInEndpoint   = 0x82
	HapticAudioPacketSize   = 384
	HapticMicPacketSize     = 192
	HapticFeedbackFrameSize = 388
)

func hapticDescriptor() usb.Descriptor {
	hid := defaultDescriptor.Interfaces[0]
	hid.Descriptor.BInterfaceNumber = 3
	hid.Descriptor.IInterface = 4
	hid.Endpoints = append([]usb.EndpointDescriptor(nil), hid.Endpoints...)
	hid.Endpoints[0].BInterval = 6
	hid.Endpoints[1].BInterval = 6

	acDescriptors := []usb.ClassSpecificDescriptor{
		cs(0x24, 0x01, 0x00, 0x01, 0x4a, 0x00, 0x02, 0x01, 0x02),
		cs(0x24, 0x02, 0x01, 0x01, 0x01, 0x06, 0x04, 0x33, 0x00, 0x00, 0x00),
		cs(0x24, 0x06, 0x02, 0x01, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00),
		cs(0x24, 0x03, 0x03, 0x01, 0x03, 0x04, 0x02, 0x00),
		cs(0x24, 0x02, 0x04, 0x02, 0x04, 0x03, 0x02, 0x03, 0x00, 0x00, 0x00),
		cs(0x24, 0x06, 0x05, 0x04, 0x01, 0x03, 0x00, 0x00, 0x00),
		cs(0x24, 0x03, 0x06, 0x01, 0x01, 0x01, 0x05, 0x00),
	}

	outGeneral := cs(0x24, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00)
	outFormat := cs(0x24, 0x02, 0x01, 0x04, 0x02, 0x10, 0x01, 0x80, 0xbb, 0x00)
	inGeneral := cs(0x24, 0x01, 0x06, 0x01, 0x01, 0x00, 0x00)
	inFormat := cs(0x24, 0x02, 0x01, 0x02, 0x02, 0x10, 0x01, 0x80, 0xbb, 0x00)
	endpointGeneral := cs(0x25, 0x01, 0x00, 0x00, 0x00, 0x00)

	return usb.Descriptor{
		Device: usb.DeviceDescriptor{
			BcdUSB:             0x0200,
			BDeviceClass:       0x00,
			BDeviceSubClass:    0x00,
			BDeviceProtocol:    0x00,
			BMaxPacketSize0:    0x40,
			IDVendor:           DefaultVID,
			IDProduct:          DefaultPIDDS,
			BcdDevice:          0x0103,
			IManufacturer:      0x01,
			IProduct:           0x02,
			ISerialNumber:      0x00,
			BNumConfigurations: 0x01,
			Speed:              3,
		},
		Configuration: usb.ConfigurationDescriptor{
			BConfigurationValue: 1,
			BMAttributes:        0xc0,
			BMaxPower:           0xfa,
		},
		Interfaces: []usb.InterfaceConfig{
			{
				Descriptor: usb.InterfaceDescriptor{
					BInterfaceNumber: 0, BAlternateSetting: 0, BNumEndpoints: 0,
					BInterfaceClass: 0x01, BInterfaceSubClass: 0x01,
				},
				ClassDescriptors: acDescriptors,
			},
			{
				Descriptor: usb.InterfaceDescriptor{
					BInterfaceNumber: 1, BAlternateSetting: 0, BNumEndpoints: 0,
					BInterfaceClass: 0x01, BInterfaceSubClass: 0x02,
				},
			},
			{
				Descriptor: usb.InterfaceDescriptor{
					BInterfaceNumber: 1, BAlternateSetting: 1, BNumEndpoints: 1,
					BInterfaceClass: 0x01, BInterfaceSubClass: 0x02,
				},
				ClassDescriptors: []usb.ClassSpecificDescriptor{outGeneral, outFormat},
				Endpoints: []usb.EndpointDescriptor{{
					BEndpointAddress: HapticAudioOutEndpoint,
					BMAttributes:     0x09,
					WMaxPacketSize:   HapticAudioPacketSize,
					BInterval:        4,
					ClassDescriptors: []usb.ClassSpecificDescriptor{endpointGeneral},
				}},
			},
			{
				Descriptor: usb.InterfaceDescriptor{
					BInterfaceNumber: 2, BAlternateSetting: 0, BNumEndpoints: 0,
					BInterfaceClass: 0x01, BInterfaceSubClass: 0x02,
				},
			},
			{
				Descriptor: usb.InterfaceDescriptor{
					BInterfaceNumber: 2, BAlternateSetting: 1, BNumEndpoints: 1,
					BInterfaceClass: 0x01, BInterfaceSubClass: 0x02,
				},
				ClassDescriptors: []usb.ClassSpecificDescriptor{inGeneral, inFormat},
				Endpoints: []usb.EndpointDescriptor{{
					BEndpointAddress: HapticAudioInEndpoint,
					BMAttributes:     0x05,
					WMaxPacketSize:   HapticMicPacketSize,
					BInterval:        4,
					ClassDescriptors: []usb.ClassSpecificDescriptor{endpointGeneral},
				}},
			},
			hid,
		},
		Strings: map[uint8]string{
			0: "\u0409",
			1: "Sony Interactive Entertainment",
			2: "DualSense Wireless Controller",
			3: "Wireless Controller Audio",
			4: "Wireless Controller",
		},
	}
}

func cs(descriptorType uint8, payload ...uint8) usb.ClassSpecificDescriptor {
	return usb.ClassSpecificDescriptor{
		DescriptorType: descriptorType,
		Payload:        usb.Data(payload),
	}
}

#include "device_config.h"
#include "hid_report.h"
#include "usb_descriptors.h"

#define EPNUM_HID_OUT 0x01
#define EPNUM_HID 0x81
#define EPNUM_DUAL_HID_A_OUT 0x01
#define EPNUM_DUAL_HID_A_IN 0x81
#define EPNUM_DUAL_HID_B_OUT 0x02
#define EPNUM_DUAL_HID_B_IN 0x82
#define EPNUM_VENDOR_OUT 0x02
#define EPNUM_VENDOR_IN 0x82
#ifdef XINPUT_ELITE_EXPERIMENT
#define EPNUM_XINPUT_OUT 0x02
#define EPNUM_XINPUT_IN 0x82
#else
#define EPNUM_XINPUT_OUT 0x02
#define EPNUM_XINPUT_IN 0x81
#endif
#define ITF_NUM_HID 0
#define ITF_NUM_DUAL_HID_A 0
#define ITF_NUM_DUAL_HID_B 1
#define ITF_NUM_VENDOR USB_SWITCH2_VENDOR_INTERFACE
#define ITF_NUM_XINPUT 0
#define ITF_NUM_TOTAL_GENERIC 1
#define ITF_NUM_TOTAL_NINTENDO 2
#define ITF_NUM_TOTAL_SWITCH1 1
#define ITF_NUM_TOTAL_DUAL_HID 2
#define ITF_NUM_TOTAL_XINPUT 1
#define CONFIG_TOTAL_LEN_GENERIC (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN)
#define CONFIG_TOTAL_LEN_NINTENDO (TUD_CONFIG_DESC_LEN + TUD_HID_INOUT_DESC_LEN + TUD_VENDOR_DESC_LEN)
#define CONFIG_TOTAL_LEN_SWITCH1 (TUD_CONFIG_DESC_LEN + TUD_HID_Y700_INOUT_DESC_LEN)
#define TUD_HID_Y700_INOUT_DESC_LEN (9 + 9 + 7 + 7)
#define CONFIG_TOTAL_LEN_DUAL_HID (TUD_CONFIG_DESC_LEN + 2 * TUD_HID_Y700_INOUT_DESC_LEN)
#ifdef XINPUT_ELITE_EXPERIMENT
#define CONFIG_TOTAL_LEN_XINPUT (TUD_CONFIG_DESC_LEN + 9 + 2 * 7)
#else
#define CONFIG_TOTAL_LEN_XINPUT 0x30
#endif
#define HID_POLL_INTERVAL_MS 1
#define SWITCH1_HID_POLL_INTERVAL_MS 8
#define VENDOR_BULK_PACKET_SIZE 64
#ifdef XINPUT_ELITE_EXPERIMENT
#define USB_PID_XINPUT_ACTIVE USB_PID_XINPUT_ELITE_EXPERIMENT
#define USB_PRODUCT_XINPUT_ACTIVE "Xbox Elite Wireless Controller Series 2"
#define USB_MANUFACTURER_XINPUT_ACTIVE "Microsoft Corporation"
#define USB_SERIAL_XINPUT_ACTIVE "ELITE2-GIP-0512"
#define USB_BCD_DEVICE_XINPUT_ACTIVE 0x0512
#define USB_DEVICE_SUBCLASS_XINPUT_ACTIVE 0x47
#define USB_DEVICE_PROTOCOL_XINPUT_ACTIVE 0xd0
#define USB_INTERFACE_SUBCLASS_XINPUT_ACTIVE 0x47
#define USB_INTERFACE_PROTOCOL_XINPUT_ACTIVE 0xd0
#define XINPUT_PACKET_SIZE 64
#define XINPUT_OUT_INTERVAL_MS 4
#else
#define USB_PID_XINPUT_ACTIVE USB_PID_XINPUT_EXPERIMENT
#define USB_PRODUCT_XINPUT_ACTIVE "XINPUT CONTROLLER"
#define USB_MANUFACTURER_XINPUT_ACTIVE "GENERIC"
#define USB_SERIAL_XINPUT_ACTIVE "1.0"
#define USB_BCD_DEVICE_XINPUT_ACTIVE 0x0572
#define USB_DEVICE_SUBCLASS_XINPUT_ACTIVE 0xff
#define USB_DEVICE_PROTOCOL_XINPUT_ACTIVE 0xff
#define USB_INTERFACE_SUBCLASS_XINPUT_ACTIVE 0x5d
#define USB_INTERFACE_PROTOCOL_XINPUT_ACTIVE 0x01
#define XINPUT_PACKET_SIZE 32
#define XINPUT_OUT_INTERVAL_MS 8
#endif
#define XINPUT_IN_INTERVAL_MS 4
#define CONFIG_ATTR_NINTENDO 0
#define CONFIG_POWER_MA_NINTENDO 500

#define TUD_VENDOR_INOUT_DESCRIPTOR(_itfnum, _stridx, _epin, _epout, _epsize) \
    9, TUSB_DESC_INTERFACE, _itfnum, 0, 2, TUSB_CLASS_VENDOR_SPECIFIC, 0x00, 0x00, _stridx, \
    7, TUSB_DESC_ENDPOINT, _epin, TUSB_XFER_BULK, U16_TO_U8S_LE(_epsize), 0, \
    7, TUSB_DESC_ENDPOINT, _epout, TUSB_XFER_BULK, U16_TO_U8S_LE(_epsize), 0

#define TUD_HID_Y700_INOUT_DESCRIPTOR(_itfnum, _stridx, _boot_protocol, _report_desc_len, _epin, _epout, _epsize, _ep_interval) \
    9, TUSB_DESC_INTERFACE, _itfnum, 0, 2, TUSB_CLASS_HID, (uint8_t)((_boot_protocol) ? (uint8_t)HID_SUBCLASS_BOOT : 0), _boot_protocol, _stridx, \
    9, HID_DESC_TYPE_HID, U16_TO_U8S_LE(0x0101), 0, 1, HID_DESC_TYPE_REPORT, U16_TO_U8S_LE(_report_desc_len), \
    7, TUSB_DESC_ENDPOINT, _epin, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(_epsize), _ep_interval, \
    7, TUSB_DESC_ENDPOINT, _epout, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(_epsize), _ep_interval

enum {
    STRID_LANGID = 0,
    STRID_MANUFACTURER,
    STRID_PRODUCT,
    STRID_SERIAL,
    STRID_CONFIG,
    STRID_HID_INTERFACE,
    STRID_HID2_INTERFACE,
    STRID_VENDOR_INTERFACE,
};

const uint8_t desc_hid_report_generic[] = {
    TUD_HID_REPORT_DESC_GAMEPAD(HID_REPORT_ID(GENERIC_HID_REPORT_ID))
};

const uint8_t desc_hid_report_dual_gamepad[] = {
    TUD_HID_REPORT_DESC_GAMEPAD()
};

const uint8_t desc_hid_report_nintendo_experiment[] = {
    // Vendor/raw HID copied from Y700 v3:
    // input report 0x05 + 63 payload bytes, output report 0x02 + 63 payload bytes.
    // feature report 0x7f is a manager-only control channel and is ignored by Steam.
    0x06, 0x00, 0xff, 0x09, 0x01, 0xa1, 0x01, 0x15, 0x00, 0x26, 0xff, 0x00,
    0x75, 0x08, 0x85, NINTENDO_INPUT_REPORT_ID, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02, 0x85, 0x02,
    0x95, 0x3f, 0x09, 0x01, 0x91, 0x02, 0x85, MANAGER_FEATURE_REPORT_ID,
    0x95, 0x3f, 0x09, 0x01, 0xb1, 0x02, 0xc0,
};

const uint8_t desc_hid_report_switch_legacy[] = {
    0x05, 0x01, 0x09, 0x05, 0xa1, 0x01, 0x15, 0x00, 0x26, 0xff, 0x00, 0x75, 0x08,
    0x85, SWITCH_LEGACY_REPORT_ID_SUBCOMMAND_REPLY, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_FULL_STATE, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_FULL_STATE_MCU, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_SIMPLE_STATE, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_COMMAND_ACK, 0x95, 0x3f, 0x09, 0x01, 0x81, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_RUMBLE_SUBCOMMAND, 0x95, 0x3f, 0x09, 0x01, 0x91, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_RUMBLE, 0x95, 0x3f, 0x09, 0x01, 0x91, 0x02,
    0x85, SWITCH_LEGACY_REPORT_ID_PROPRIETARY, 0x95, 0x3f, 0x09, 0x01, 0x91, 0x02,
    0x85, MANAGER_FEATURE_REPORT_ID, 0x95, 0x3f, 0x09, 0x01, 0xb1, 0x02,
    0xc0,
};

// Byte-for-byte compatible with a wired Switch 1 Pro Controller HID contract.
// The console depends on these usage/report declarations in addition to VID/PID.
const uint8_t desc_hid_report_switch1_pro[] = {
    0x05,0x01,0x15,0x00,0x09,0x04,0xa1,0x01,0x85,0x30,0x05,0x01,0x05,0x09,
    0x19,0x01,0x29,0x0a,0x15,0x00,0x25,0x01,0x75,0x01,0x95,0x0a,0x55,0x00,
    0x65,0x00,0x81,0x02,0x05,0x09,0x19,0x0b,0x29,0x0e,0x15,0x00,0x25,0x01,
    0x75,0x01,0x95,0x04,0x81,0x02,0x75,0x01,0x95,0x02,0x81,0x03,0x0b,0x01,
    0x00,0x01,0x00,0xa1,0x00,0x0b,0x30,0x00,0x01,0x00,0x0b,0x31,0x00,0x01,
    0x00,0x0b,0x32,0x00,0x01,0x00,0x0b,0x35,0x00,0x01,0x00,0x15,0x00,0x27,
    0xff,0xff,0x00,0x00,0x75,0x10,0x95,0x04,0x81,0x02,0xc0,0x0b,0x39,0x00,
    0x01,0x00,0x15,0x00,0x25,0x07,0x35,0x00,0x46,0x3b,0x01,0x65,0x14,0x75,
    0x04,0x95,0x01,0x81,0x02,0x05,0x09,0x19,0x0f,0x29,0x12,0x15,0x00,0x25,
    0x01,0x75,0x01,0x95,0x04,0x81,0x02,0x75,0x08,0x95,0x34,0x81,0x03,0x06,
    0x00,0xff,0x85,0x21,0x09,0x01,0x75,0x08,0x95,0x3f,0x81,0x03,0x85,0x81,
    0x09,0x02,0x75,0x08,0x95,0x3f,0x81,0x03,0x85,0x01,0x09,0x03,0x75,0x08,
    0x95,0x3f,0x91,0x83,0x85,0x10,0x09,0x04,0x75,0x08,0x95,0x3f,0x91,0x83,
    0x85,0x80,0x09,0x05,0x75,0x08,0x95,0x3f,0x91,0x83,0x85,0x82,0x09,0x06,
    0x75,0x08,0x95,0x3f,0x91,0x83,0xc0,
};

_Static_assert(sizeof(desc_hid_report_switch1_pro) == 203,
               "Switch 1 Pro HID report descriptor must remain byte-compatible");

static const tusb_desc_device_t desc_device_generic = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID_GENERIC,
    .idProduct = USB_PID_GENERIC,
    .bcdDevice = 0x0100,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 0x01,
};

static const tusb_desc_device_t desc_device_nintendo = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0201,
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID_NINTENDO_EXPERIMENT,
    .idProduct = USB_PID_NINTENDO_EXPERIMENT,
    .bcdDevice = 0x0105,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 0x01,
};

static const tusb_desc_device_t desc_device_xinput = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = 0xff,
    .bDeviceSubClass = USB_DEVICE_SUBCLASS_XINPUT_ACTIVE,
    .bDeviceProtocol = USB_DEVICE_PROTOCOL_XINPUT_ACTIVE,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID_XINPUT_EXPERIMENT,
    .idProduct = USB_PID_XINPUT_ACTIVE,
    .bcdDevice = USB_BCD_DEVICE_XINPUT_ACTIVE,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 0x01,
};

static const tusb_desc_device_t desc_device_dual_hid = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID_NINTENDO_EXPERIMENT,
    .idProduct = USB_PID_NINTENDO_SWITCH_PRO_LEGACY,
    .bcdDevice = 0x0209,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 0x01,
};

static const tusb_desc_device_t desc_device_switch1_pro = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = 0x00,
    .bDeviceSubClass = 0x00,
    .bDeviceProtocol = 0x00,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID_NINTENDO_EXPERIMENT,
    .idProduct = USB_PID_NINTENDO_SWITCH_PRO_LEGACY,
    .bcdDevice = 0x0210,
    .iManufacturer = STRID_MANUFACTURER,
    .iProduct = STRID_PRODUCT,
    .iSerialNumber = STRID_SERIAL,
    .bNumConfigurations = 0x01,
};

static const uint8_t desc_configuration_generic[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_GENERIC, 0, CONFIG_TOTAL_LEN_GENERIC, 0, 100),
    TUD_HID_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report_generic), EPNUM_HID, sizeof(bridge_hid_gamepad_report_t) + 1, HID_POLL_INTERVAL_MS),
};

static const uint8_t desc_configuration_nintendo[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_NINTENDO, STRID_CONFIG, CONFIG_TOTAL_LEN_NINTENDO, CONFIG_ATTR_NINTENDO, CONFIG_POWER_MA_NINTENDO),
    TUD_HID_Y700_INOUT_DESCRIPTOR(ITF_NUM_HID, STRID_HID_INTERFACE, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report_nintendo_experiment), EPNUM_HID, EPNUM_HID_OUT, NINTENDO_REPORT_SIZE, HID_POLL_INTERVAL_MS),
    TUD_VENDOR_INOUT_DESCRIPTOR(ITF_NUM_VENDOR, STRID_VENDOR_INTERFACE, EPNUM_VENDOR_IN, EPNUM_VENDOR_OUT, VENDOR_BULK_PACKET_SIZE),
};

static const uint8_t desc_configuration_dual_hid[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_DUAL_HID, STRID_CONFIG, CONFIG_TOTAL_LEN_DUAL_HID, 0, 100),
    TUD_HID_Y700_INOUT_DESCRIPTOR(ITF_NUM_DUAL_HID_A, STRID_HID_INTERFACE, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report_switch_legacy), EPNUM_DUAL_HID_A_IN, EPNUM_DUAL_HID_A_OUT, SWITCH_LEGACY_REPORT_SIZE, HID_POLL_INTERVAL_MS),
    TUD_HID_Y700_INOUT_DESCRIPTOR(ITF_NUM_DUAL_HID_B, STRID_HID2_INTERFACE, HID_ITF_PROTOCOL_NONE, sizeof(desc_hid_report_switch_legacy), EPNUM_DUAL_HID_B_IN, EPNUM_DUAL_HID_B_OUT, SWITCH_LEGACY_REPORT_SIZE, HID_POLL_INTERVAL_MS),
};

static const uint8_t desc_configuration_switch1_pro[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_SWITCH1, STRID_CONFIG,
                          CONFIG_TOTAL_LEN_SWITCH1,
                          TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 500),
    TUD_HID_Y700_INOUT_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_NONE,
                                  sizeof(desc_hid_report_switch1_pro),
                                  EPNUM_HID, EPNUM_HID_OUT,
                                  SWITCH_LEGACY_REPORT_SIZE,
                                  SWITCH1_HID_POLL_INTERVAL_MS),
};

static const uint8_t desc_configuration_xinput[] = {
#ifdef XINPUT_ELITE_EXPERIMENT
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_XINPUT, 0, CONFIG_TOTAL_LEN_XINPUT, TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 500),
    9, TUSB_DESC_INTERFACE, ITF_NUM_XINPUT, 0, 2, TUSB_CLASS_VENDOR_SPECIFIC, USB_INTERFACE_SUBCLASS_XINPUT_ACTIVE, USB_INTERFACE_PROTOCOL_XINPUT_ACTIVE, 0,
    7, TUSB_DESC_ENDPOINT, EPNUM_XINPUT_IN, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(XINPUT_PACKET_SIZE), XINPUT_IN_INTERVAL_MS,
    7, TUSB_DESC_ENDPOINT, EPNUM_XINPUT_OUT, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(XINPUT_PACKET_SIZE), XINPUT_OUT_INTERVAL_MS,
#else
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL_XINPUT, 0, CONFIG_TOTAL_LEN_XINPUT, 0, 500),
    9, TUSB_DESC_INTERFACE, ITF_NUM_XINPUT, 0, 2, TUSB_CLASS_VENDOR_SPECIFIC, USB_INTERFACE_SUBCLASS_XINPUT_ACTIVE, USB_INTERFACE_PROTOCOL_XINPUT_ACTIVE, 0,
    0x10, 0x21, 0x10, 0x01, 0x01, 0x24, 0x81, 0x14,
    0x03, 0x00, 0x03, 0x13, 0x02, 0x00, 0x03, 0x00,
    7, TUSB_DESC_ENDPOINT, EPNUM_XINPUT_IN, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(XINPUT_PACKET_SIZE), XINPUT_IN_INTERVAL_MS,
    7, TUSB_DESC_ENDPOINT, EPNUM_XINPUT_OUT, TUSB_XFER_INTERRUPT, U16_TO_U8S_LE(XINPUT_PACKET_SIZE), XINPUT_OUT_INTERVAL_MS,
#endif
};

static const char *string_desc_generic[] = {
    "",
    "LeonChrome",
    "ESP32-S3 Generic HID Gamepad",
    "ESP32S3-GENERIC",
};

static const char *string_desc_nintendo[] = {
    "",
    "Nintendo Co., Ltd.",
    "Nintendo Switch Pro Controller",
    "HA2F83JF",
    "Nintendo Switch Pro Controller",
    "HID Interface",
    "",
    "Nintendo Switch 2 bulk",
};

static const char *string_desc_dual_hid[] = {
    "",
    "Nintendo Co., Ltd.",
    "Nintendo Switch Pro Controller",
    "Y700-DUAL-SWPRO-0209",
    "Nintendo Switch Pro Controller",
    "Nintendo Switch Pro Controller A",
    "Nintendo Switch Pro Controller B",
};

static const char *string_desc_switch1_pro[] = {
    "",
    "Nintendo Co., Ltd.",
    "Pro Controller",
    "000000000001",
    "Nintendo Switch Pro Controller",
};

static const char *string_desc_xinput[] = {
    "",
    USB_MANUFACTURER_XINPUT_ACTIVE,
    USB_PRODUCT_XINPUT_ACTIVE,
    USB_SERIAL_XINPUT_ACTIVE,
};

uint16_t usb_descriptors_current_vid(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return USB_VID_NINTENDO_EXPERIMENT;
    case DUAL_PRO2_EXPERIMENT_MODE:
    case SWITCH1_PRO_MODE:
        return USB_VID_NINTENDO_EXPERIMENT;
    case XINPUT_EXPERIMENT_MODE:
        return USB_VID_XINPUT_EXPERIMENT;
    default:
        return USB_VID_GENERIC;
    }
}

uint16_t usb_descriptors_current_pid(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return USB_PID_NINTENDO_EXPERIMENT;
    case DUAL_PRO2_EXPERIMENT_MODE:
    case SWITCH1_PRO_MODE:
        return USB_PID_NINTENDO_SWITCH_PRO_LEGACY;
    case XINPUT_EXPERIMENT_MODE:
        return USB_PID_XINPUT_ACTIVE;
    default:
        return USB_PID_GENERIC;
    }
}

const char *usb_descriptors_current_product(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return "Nintendo Switch Pro Controller";
    case DUAL_PRO2_EXPERIMENT_MODE:
        return "Nintendo Switch Pro Controller";
    case SWITCH1_PRO_MODE:
        return "Pro Controller";
    case XINPUT_EXPERIMENT_MODE:
        return USB_PRODUCT_XINPUT_ACTIVE;
    default:
        return "ESP32-S3 Generic HID Gamepad";
    }
}

const char *usb_descriptors_current_manufacturer(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return "Nintendo Co., Ltd.";
    case DUAL_PRO2_EXPERIMENT_MODE:
    case SWITCH1_PRO_MODE:
        return "Nintendo Co., Ltd.";
    case XINPUT_EXPERIMENT_MODE:
        return USB_MANUFACTURER_XINPUT_ACTIVE;
    default:
        return "LeonChrome";
    }
}

const tusb_desc_device_t *usb_descriptors_current_device(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return &desc_device_nintendo;
    case DUAL_PRO2_EXPERIMENT_MODE:
        return &desc_device_dual_hid;
    case SWITCH1_PRO_MODE:
        return &desc_device_switch1_pro;
    case XINPUT_EXPERIMENT_MODE:
        return &desc_device_xinput;
    default:
        return &desc_device_generic;
    }
}

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance)
{
    (void)instance;
    if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
        return desc_hid_report_switch_legacy;
    }
    if (device_config_get_mode() == SWITCH1_PRO_MODE) {
        return desc_hid_report_switch1_pro;
    }
    return device_config_get_mode() == NINTENDO_EXPERIMENT_MODE ?
        desc_hid_report_nintendo_experiment : desc_hid_report_generic;
}

const uint8_t *usb_descriptors_current_configuration(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return desc_configuration_nintendo;
    case DUAL_PRO2_EXPERIMENT_MODE:
        return desc_configuration_dual_hid;
    case SWITCH1_PRO_MODE:
        return desc_configuration_switch1_pro;
    case XINPUT_EXPERIMENT_MODE:
        return desc_configuration_xinput;
    default:
        return desc_configuration_generic;
    }
}

const char **usb_descriptors_current_strings(void)
{
    switch (device_config_get_mode()) {
    case NINTENDO_EXPERIMENT_MODE:
        return string_desc_nintendo;
    case DUAL_PRO2_EXPERIMENT_MODE:
        return string_desc_dual_hid;
    case SWITCH1_PRO_MODE:
        return string_desc_switch1_pro;
    case XINPUT_EXPERIMENT_MODE:
        return string_desc_xinput;
    default:
        return string_desc_generic;
    }
}

int usb_descriptors_current_string_count(void)
{
    if (device_config_get_mode() == NINTENDO_EXPERIMENT_MODE) {
        return 8;
    }
    if (device_config_get_mode() == DUAL_PRO2_EXPERIMENT_MODE) {
        return 7;
    }
    if (device_config_get_mode() == SWITCH1_PRO_MODE) {
        return 5;
    }
    return 4;
}

#include <string.h>

#include "bsp/board_api.h"
#include "tusb.h"

#define USB_VID 0xCAFE
#define USB_PID 0x4021
#define USB_BCD 0x0100

tusb_desc_device_t const desc_device = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bDeviceClass = TUSB_CLASS_MISC,
    .bDeviceSubClass = MISC_SUBCLASS_COMMON,
    .bDeviceProtocol = MISC_PROTOCOL_IAD,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = USB_VID,
    .idProduct = USB_PID,
    .bcdDevice = USB_BCD,
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = 3,
    .bNumConfigurations = 1,
};

uint8_t const *tud_descriptor_device_cb(void) {
    return (uint8_t const *)&desc_device;
}

enum {
    ITF_NUM_CDC = 0,
    ITF_NUM_CDC_DATA,
    ITF_NUM_TOTAL,
};

#define EPNUM_CDC_NOTIF 0x81
#define EPNUM_CDC_OUT 0x02
#define EPNUM_CDC_IN 0x82
#define CONFIG_TOTAL_LEN (TUD_CONFIG_DESC_LEN + TUD_CDC_DESC_LEN)

uint8_t const desc_configuration[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0x00, 100),
    TUD_CDC_DESCRIPTOR(ITF_NUM_CDC, 4, EPNUM_CDC_NOTIF, 8,
                       EPNUM_CDC_OUT, EPNUM_CDC_IN, 64),
};

uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return desc_configuration;
}

enum {
    STRID_LANGID = 0,
    STRID_MANUFACTURER,
    STRID_PRODUCT,
    STRID_SERIAL,
    STRID_CDC,
};

static char const *const string_desc_arr[] = {
    (const char[]){0x09, 0x04},
    "XinHeLianSheng",
    "Pico 2 W Pro2 Bridge Debug",
    NULL,
    "Bridge diagnostics",
};

static uint16_t desc_string[33];

uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    size_t count = 0;

    if (index == STRID_LANGID) {
        memcpy(&desc_string[1], string_desc_arr[0], 2);
        count = 1;
    } else if (index == STRID_SERIAL) {
        count = board_usb_get_serial(desc_string + 1, 32);
    } else {
        if (index >= sizeof(string_desc_arr) / sizeof(string_desc_arr[0]) ||
            string_desc_arr[index] == NULL) {
            return NULL;
        }
        const char *text = string_desc_arr[index];
        count = strlen(text);
        if (count > 32) {
            count = 32;
        }
        for (size_t i = 0; i < count; ++i) {
            desc_string[1 + i] = (uint8_t)text[i];
        }
    }

    desc_string[0] =
        (uint16_t)((TUSB_DESC_STRING << 8) | (2 * count + 2));
    return desc_string;
}

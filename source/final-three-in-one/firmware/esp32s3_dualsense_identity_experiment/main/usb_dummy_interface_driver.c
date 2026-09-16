#include <stdbool.h>
#include <stdint.h>

#include "device/usbd_pvt.h"
#include "dualsense_runtime_stats.h"
#include "esp_log.h"
#include "tusb.h"
#include "usb_dualsense_descriptor.h"

#ifndef DS5_ENABLE_DUMMY_INTERFACE
#define DS5_ENABLE_DUMMY_INTERFACE 0
#endif

#if DS5_ENABLE_DUMMY_INTERFACE

static const char *TAG = "v5.5_dummy";

static void dummy_init(void)
{
}

static bool dummy_deinit(void)
{
    return true;
}

static void dummy_reset(uint8_t rhport)
{
    (void)rhport;
    dualsense_runtime_usb_configuration_reset();
}

static uint16_t dummy_open(uint8_t rhport,
                           tusb_desc_interface_t const *itf_desc,
                           uint16_t max_len)
{
    (void)rhport;
    if (max_len < DUALSENSE_USB_DUMMY_INTERFACE_DESC_LEN ||
        itf_desc->bInterfaceClass != TUSB_CLASS_VENDOR_SPECIFIC ||
        itf_desc->bAlternateSetting != 0 ||
        itf_desc->bNumEndpoints != 0) {
        return 0;
    }

    ESP_LOGI(TAG,
             "[DS5_DUMMY] open=true interface=%u endpoints=0 desc_len=%u",
             (unsigned)itf_desc->bInterfaceNumber,
             (unsigned)DUALSENSE_USB_DUMMY_INTERFACE_DESC_LEN);
    return DUALSENSE_USB_DUMMY_INTERFACE_DESC_LEN;
}

static bool dummy_control_xfer_cb(uint8_t rhport,
                                  uint8_t stage,
                                  tusb_control_request_t const *request)
{
    (void)rhport;
    (void)stage;
    (void)request;
    return false;
}

static bool dummy_xfer_cb(uint8_t rhport,
                          uint8_t ep_addr,
                          xfer_result_t result,
                          uint32_t xferred_bytes)
{
    (void)rhport;
    (void)ep_addr;
    (void)result;
    (void)xferred_bytes;
    return false;
}

static usbd_class_driver_t const s_dummy_driver[] = {{
    .name = "v5.5_dummy",
    .init = dummy_init,
    .deinit = dummy_deinit,
    .reset = dummy_reset,
    .open = dummy_open,
    .control_xfer_cb = dummy_control_xfer_cb,
    .xfer_cb = dummy_xfer_cb,
    .xfer_isr = NULL,
    .sof = NULL,
}};

usbd_class_driver_t const *usbd_app_driver_get_cb(uint8_t *driver_count)
{
    *driver_count = 1;
    return s_dummy_driver;
}

#endif

#include <stdbool.h>
#include <stdint.h>

#include "class/audio/audio.h"
#include "dualsense_haptic_audio.h"
#include "dualsense_runtime_stats.h"
#include "esp_timer.h"
#include "device/usbd_pvt.h"
#include "esp_log.h"
#include "tusb.h"
#include "usb_dualsense_descriptor.h"

#ifndef DS5_ENABLE_UAC1_AUDIO
#define DS5_ENABLE_UAC1_AUDIO 0
#endif
#ifndef DS5_ENABLE_UAC1_CONTROL_ONLY
#define DS5_ENABLE_UAC1_CONTROL_ONLY 0
#endif
#ifndef DS5_ENABLE_UAC1_STREAMING_ALT0
#define DS5_ENABLE_UAC1_STREAMING_ALT0 0
#endif
#ifndef DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
#define DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY 0
#endif

#if DS5_ENABLE_UAC1_AUDIO || DS5_ENABLE_UAC1_CONTROL_ONLY || \
    DS5_ENABLE_UAC1_STREAMING_ALT0

#define DS5_UAC1_AC_INTERFACE 0
#define DS5_UAC1_AS_INTERFACE 1
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
#define DS5_UAC1_MIC_INTERFACE 2
#endif
#define DS5_UAC1_ALT_IDLE 0
#define DS5_UAC1_ALT_STREAMING 1
#define DS5_UAC1_MIC_PAYLOAD_BYTES DUALSENSE_USB_UAC1_MIC_PACKET_SIZE
#define DS5_UAC1_SPEAKER_FEATURE_UNIT 0x02
#define DS5_UAC1_MIC_FEATURE_UNIT 0x05

#define UAC1_SET_CUR 0x01
#define UAC1_GET_CUR 0x81
#define UAC1_GET_MIN 0x82
#define UAC1_GET_MAX 0x83
#define UAC1_GET_RES 0x84

static const char *TAG = "v5.5_uac1";
static uint8_t s_out_alt_setting;
static uint8_t s_feature_mute[2];
static int16_t s_feature_volume[2];
static uint8_t s_feature_control_buffer[2];
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
static uint8_t s_in_alt_setting;
#endif
#if DS5_ENABLE_UAC1_AUDIO
static uint32_t s_packet_count;
static uint8_t s_audio_out_buffer[DUALSENSE_USB_UAC1_PACKET_SIZE] TU_ATTR_ALIGNED(4);
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
static uint8_t s_audio_in_buffer[DS5_UAC1_MIC_PAYLOAD_BYTES] TU_ATTR_ALIGNED(4);
#endif

static const tusb_desc_endpoint_t s_uac1_audio_out_ep = {
    .bLength = sizeof(tusb_desc_endpoint_t),
    .bDescriptorType = TUSB_DESC_ENDPOINT,
    .bEndpointAddress = DUALSENSE_USB_AUDIO_EP_OUT,
    .bmAttributes = {
        .xfer = TUSB_XFER_ISOCHRONOUS,
        .sync = (TUSB_ISO_EP_ATT_ADAPTIVE >> 2),
        .usage = (TUSB_ISO_EP_ATT_DATA >> 4),
    },
    .wMaxPacketSize = DUALSENSE_USB_UAC1_PACKET_SIZE,
    .bInterval = 1,
};

_Static_assert(sizeof(s_audio_out_buffer) ==
                   DUALSENSE_USB_AUDIO_SAMPLE_RATE / 1000 *
                       DS5_AUDIO_CHANNELS *
                       DUALSENSE_USB_AUDIO_BYTES_PER_SAMPLE,
               "UAC1 OUT buffer must hold one fixed 1 ms audio frame");
#endif

static void uac1_init(void)
{
    s_out_alt_setting = DS5_UAC1_ALT_IDLE;
    memset(s_feature_mute, 0, sizeof(s_feature_mute));
    memset(s_feature_volume, 0, sizeof(s_feature_volume));
    memset(s_feature_control_buffer, 0, sizeof(s_feature_control_buffer));
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
    s_in_alt_setting = DS5_UAC1_ALT_IDLE;
    memset(s_audio_in_buffer, 0, sizeof(s_audio_in_buffer));
#endif
#if DS5_ENABLE_UAC1_AUDIO
    s_packet_count = 0;
    dualsense_haptic_audio_set_streaming(false, DS5_UAC1_ALT_IDLE);
#endif
}

static bool uac1_deinit(void)
{
    return true;
}

static void uac1_reset(uint8_t rhport)
{
    (void)rhport;
    dualsense_runtime_usb_configuration_reset();
    s_out_alt_setting = DS5_UAC1_ALT_IDLE;
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
    s_in_alt_setting = DS5_UAC1_ALT_IDLE;
#endif
#if DS5_ENABLE_UAC1_AUDIO
    s_packet_count = 0;
    dualsense_haptic_audio_set_streaming(false, DS5_UAC1_ALT_IDLE);
#endif
}

static uint16_t uac1_open(uint8_t rhport,
                          tusb_desc_interface_t const *itf_desc,
                          uint16_t max_len)
{
    (void)rhport;
    if (itf_desc->bInterfaceClass != TUSB_CLASS_AUDIO ||
        itf_desc->bInterfaceProtocol != AUDIO_INT_PROTOCOL_CODE_UNDEF) {
        return 0;
    }

    if (itf_desc->bInterfaceNumber == DS5_UAC1_AC_INTERFACE &&
        itf_desc->bInterfaceSubClass == AUDIO_SUBCLASS_CONTROL &&
        itf_desc->bAlternateSetting == 0) {
#if DS5_ENABLE_UAC1_CONTROL_ONLY
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_CONTROL_ONLY_AC_LEN;
#elif DS5_ENABLE_UAC1_STREAMING_ALT0
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_STREAMING_ALT0_AC_LEN;
#elif DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_DS5_AC_LEN;
#else
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_2CH_AC_LEN;
#endif
        if (max_len < descriptor_len) {
            return 0;
        }
        ESP_LOGI(TAG,
                 "[DS5_UAC1] open=true section=audio_control desc_len=%u",
                 (unsigned)descriptor_len);
        return descriptor_len;
    }

#if DS5_ENABLE_UAC1_STREAMING_ALT0 || DS5_ENABLE_UAC1_AUDIO
    if (itf_desc->bInterfaceNumber == DS5_UAC1_AS_INTERFACE &&
        itf_desc->bInterfaceSubClass == AUDIO_SUBCLASS_STREAMING &&
        itf_desc->bAlternateSetting == 0) {
#if DS5_ENABLE_UAC1_STREAMING_ALT0
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_STREAMING_ALT0_AS_LEN;
#elif DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_DS5_AS_OUT_LEN;
#else
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_2CH_AS_LEN;
#endif
        if (max_len < descriptor_len) {
            return 0;
        }
#if DS5_ENABLE_UAC1_AUDIO
        if (!usbd_edpt_iso_alloc(rhport,
                                 DUALSENSE_USB_AUDIO_EP_OUT,
                                 DUALSENSE_USB_UAC1_PACKET_SIZE)) {
            ESP_LOGW(TAG,
                     "[DS5_UAC1] iso_alloc=false ep=0x%02x fifo_bytes=%u",
                     DUALSENSE_USB_AUDIO_EP_OUT,
                     (unsigned)DUALSENSE_USB_UAC1_PACKET_SIZE);
            return 0;
        }
#endif
        ESP_LOGI(TAG,
                 "[DS5_UAC1] open=true section=audio_streaming desc_len=%u",
                 (unsigned)descriptor_len);
        return descriptor_len;
    }
#endif

#if DS5_ENABLE_UAC1_AUDIO && DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
    if (itf_desc->bInterfaceNumber == DS5_UAC1_MIC_INTERFACE &&
        itf_desc->bInterfaceSubClass == AUDIO_SUBCLASS_STREAMING &&
        itf_desc->bAlternateSetting == 0) {
        const uint16_t descriptor_len = DUALSENSE_USB_UAC1_DS5_AS_IN_LEN;
        if (max_len < descriptor_len) {
            return 0;
        }
        ESP_LOGI(TAG,
                 "[DS5_UAC1] open=true section=microphone_streaming desc_len=%u compat_stub=true reason=esp32s3_fifo_limit",
                 (unsigned)descriptor_len);
        return descriptor_len;
    }
#endif

    return 0;
}

#if DS5_ENABLE_UAC1_AUDIO
static bool uac1_start_stream(uint8_t rhport)
{
    bool activated = usbd_edpt_iso_activate(rhport, &s_uac1_audio_out_ep);
    if (!activated) {
        ESP_LOGW(TAG,
                 "[DS5_UAC1] iso_activate=false ep=0x%02x max_packet=%u dma=%s",
                 DUALSENSE_USB_AUDIO_EP_OUT,
                 (unsigned)DUALSENSE_USB_UAC1_PACKET_SIZE,
                 CFG_TUD_DWC2_DMA_ENABLE ? "true" : "false");
        return false;
    }
    s_packet_count = 0;
    dualsense_haptic_audio_set_streaming(true, DS5_UAC1_ALT_STREAMING);
    bool armed = usbd_edpt_xfer(rhport,
                                DUALSENSE_USB_AUDIO_EP_OUT,
                                s_audio_out_buffer,
                                sizeof(s_audio_out_buffer));
    ESP_LOGI(TAG,
             "[DS5_UAC1] streaming=%s ep=0x%02x max_packet=%u dma=%s armed=%s",
             armed ? "true" : "false",
             DUALSENSE_USB_AUDIO_EP_OUT,
             (unsigned)DUALSENSE_USB_UAC1_PACKET_SIZE,
             CFG_TUD_DWC2_DMA_ENABLE ? "true" : "false",
             armed ? "true" : "false");
    return armed;
}
#endif

#if DS5_ENABLE_UAC1_AUDIO && DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
static bool uac1_start_mic_stream(uint8_t rhport)
{
    (void)rhport;
    memset(s_audio_in_buffer, 0, sizeof(s_audio_in_buffer));
    ESP_LOGW(TAG,
             "[DS5_UAC1] microphone_streaming=false ep=0x%02x bytes=%u reason=esp32s3_fifo_budget_preserves_hid_and_4ch_haptics",
             DUALSENSE_USB_AUDIO_EP_IN,
             (unsigned)sizeof(s_audio_in_buffer));
    return false;
}
#endif

static int uac1_feature_index(uint8_t entity_id)
{
    if (entity_id == DS5_UAC1_SPEAKER_FEATURE_UNIT) {
        return 0;
    }
    if (entity_id == DS5_UAC1_MIC_FEATURE_UNIT) {
        return 1;
    }
    return -1;
}

static void uac1_write_le16(uint8_t *out, int16_t value)
{
    out[0] = (uint8_t)(value & 0xff);
    out[1] = (uint8_t)(((uint16_t)value >> 8) & 0xff);
}

static bool uac1_feature_control_setup(uint8_t rhport,
                                       tusb_control_request_t const *request)
{
    uint8_t interface_number = tu_u16_low(request->wIndex);
    uint8_t entity_id = tu_u16_high(request->wIndex);
    uint8_t channel = tu_u16_low(request->wValue);
    uint8_t selector = tu_u16_high(request->wValue);
    int feature_index = uac1_feature_index(entity_id);

    if (interface_number != DS5_UAC1_AC_INTERFACE ||
        feature_index < 0 ||
        channel != 0) {
        return false;
    }

    if (request->bmRequestType_bit.direction == TUSB_DIR_OUT) {
        if (request->bRequest != UAC1_SET_CUR) {
            return false;
        }
        uint16_t expected_len = selector == AUDIO_FU_CTRL_MUTE ? 1 :
                                selector == AUDIO_FU_CTRL_VOLUME ? 2 : 0;
        if (expected_len == 0 || request->wLength != expected_len) {
            return false;
        }
        return tud_control_xfer(rhport,
                                request,
                                s_feature_control_buffer,
                                expected_len);
    }

    if (selector == AUDIO_FU_CTRL_MUTE) {
        if (request->bRequest != UAC1_GET_CUR || request->wLength != 1) {
            return false;
        }
        s_feature_control_buffer[0] = s_feature_mute[feature_index];
        return tud_control_xfer(rhport,
                                request,
                                s_feature_control_buffer,
                                1);
    }

    if (selector != AUDIO_FU_CTRL_VOLUME || request->wLength != 2) {
        return false;
    }

    int16_t value;
    switch (request->bRequest) {
    case UAC1_GET_CUR:
        value = s_feature_volume[feature_index];
        break;
    case UAC1_GET_MIN:
        value = entity_id == DS5_UAC1_SPEAKER_FEATURE_UNIT
                    ? (int16_t)0x9c00
                    : 0x0000;
        break;
    case UAC1_GET_MAX:
        value = entity_id == DS5_UAC1_SPEAKER_FEATURE_UNIT
                    ? 0x0000
                    : 0x3000;
        break;
    case UAC1_GET_RES:
        value = entity_id == DS5_UAC1_SPEAKER_FEATURE_UNIT
                    ? 0x0100
                    : 0x007a;
        break;
    default:
        return false;
    }

    uac1_write_le16(s_feature_control_buffer, value);
    ESP_LOGI(TAG,
             "[DS5_UAC1] feature_get entity=%u selector=%u request=0x%02x value=%d",
             (unsigned)entity_id,
             (unsigned)selector,
             (unsigned)request->bRequest,
             (int)value);
    return tud_control_xfer(rhport,
                            request,
                            s_feature_control_buffer,
                            2);
}

static bool uac1_feature_control_data(tusb_control_request_t const *request)
{
    uint8_t entity_id = tu_u16_high(request->wIndex);
    uint8_t selector = tu_u16_high(request->wValue);
    int feature_index = uac1_feature_index(entity_id);
    if (feature_index < 0 || request->bRequest != UAC1_SET_CUR) {
        return false;
    }

    if (selector == AUDIO_FU_CTRL_MUTE && request->wLength == 1) {
        s_feature_mute[feature_index] = s_feature_control_buffer[0] ? 1 : 0;
        ESP_LOGI(TAG,
                 "[DS5_UAC1] feature_set entity=%u mute=%u",
                 (unsigned)entity_id,
                 (unsigned)s_feature_mute[feature_index]);
        return true;
    }

    if (selector == AUDIO_FU_CTRL_VOLUME && request->wLength == 2) {
        s_feature_volume[feature_index] =
            (int16_t)((uint16_t)s_feature_control_buffer[0] |
                      ((uint16_t)s_feature_control_buffer[1] << 8));
        ESP_LOGI(TAG,
                 "[DS5_UAC1] feature_set entity=%u volume_q8_8=%d",
                 (unsigned)entity_id,
                 (int)s_feature_volume[feature_index]);
        return true;
    }

    return false;
}

static bool uac1_control_xfer_cb(uint8_t rhport,
                                 uint8_t stage,
                                 tusb_control_request_t const *request)
{
    if (stage == CONTROL_STAGE_DATA) {
        if (request->bmRequestType_bit.type == TUSB_REQ_TYPE_CLASS &&
            request->bmRequestType_bit.recipient == TUSB_REQ_RCPT_INTERFACE &&
            request->bmRequestType_bit.direction == TUSB_DIR_OUT) {
            return uac1_feature_control_data(request);
        }
        return true;
    }
    if (stage == CONTROL_STAGE_ACK) {
        return true;
    }
    if (stage != CONTROL_STAGE_SETUP ||
        request->bmRequestType_bit.recipient != TUSB_REQ_RCPT_INTERFACE) {
        return false;
    }

    if (request->bmRequestType_bit.type == TUSB_REQ_TYPE_CLASS) {
        return uac1_feature_control_setup(rhport, request);
    }
    if (request->bmRequestType_bit.type != TUSB_REQ_TYPE_STANDARD) {
        return false;
    }

    uint8_t itf = tu_u16_low(request->wIndex);
    bool output_interface = itf == DS5_UAC1_AS_INTERFACE;
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
    bool input_interface = itf == DS5_UAC1_MIC_INTERFACE;
#else
    bool input_interface = false;
#endif
    if ((!output_interface && !input_interface) ||
        (!DS5_ENABLE_UAC1_STREAMING_ALT0 && !DS5_ENABLE_UAC1_AUDIO)) {
        return false;
    }

    if (request->bRequest == TUSB_REQ_GET_INTERFACE) {
        uint8_t *alt_setting =
            output_interface ? &s_out_alt_setting :
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
            &s_in_alt_setting;
#else
            &s_out_alt_setting;
#endif
        return tud_control_xfer(rhport, request, alt_setting, sizeof(*alt_setting));
    }

    if (request->bRequest == TUSB_REQ_SET_INTERFACE) {
#if DS5_ENABLE_UAC1_STREAMING_ALT0 || DS5_ENABLE_UAC1_AUDIO
        uint8_t alt = tu_u16_low(request->wValue);
#if DS5_ENABLE_UAC1_STREAMING_ALT0
        if (alt != DS5_UAC1_ALT_IDLE) {
            return false;
        }
        s_out_alt_setting = alt;
#elif DS5_ENABLE_UAC1_AUDIO
        if (alt > DS5_UAC1_ALT_STREAMING) {
            return false;
        }

        if (output_interface) {
            if (alt == DS5_UAC1_ALT_STREAMING) {
                s_out_alt_setting = alt;
                if (!uac1_start_stream(rhport)) {
                    s_out_alt_setting = DS5_UAC1_ALT_IDLE;
                    dualsense_runtime_uac1_note_set_interface(
                        false, alt, false);
                    return false;
                }
            } else {
                s_out_alt_setting = DS5_UAC1_ALT_IDLE;
                dualsense_haptic_audio_set_streaming(false, DS5_UAC1_ALT_IDLE);
            }
            dualsense_runtime_uac1_note_set_interface(false, alt, true);
#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
        } else if (input_interface) {
            if (alt == DS5_UAC1_ALT_STREAMING) {
                s_in_alt_setting = alt;
                if (!uac1_start_mic_stream(rhport)) {
                    s_in_alt_setting = DS5_UAC1_ALT_IDLE;
                    dualsense_runtime_uac1_note_set_interface(
                        true, alt, false);
                    return false;
                }
            } else {
                s_in_alt_setting = DS5_UAC1_ALT_IDLE;
            }
            dualsense_runtime_uac1_note_set_interface(true, alt, true);
#endif
        }
#else
        return false;
#endif

        ESP_LOGI(TAG,
                 "[DS5_UAC1] set_interface itf=%u alt=%u",
                 (unsigned)itf,
                 (unsigned)alt);
        return tud_control_status(rhport, request);
#else
        return false;
#endif
    }

    return false;
}

static bool uac1_xfer_cb(uint8_t rhport,
                         uint8_t ep_addr,
                         xfer_result_t result,
                         uint32_t xferred_bytes)
{
#if DS5_ENABLE_UAC1_AUDIO
    if (ep_addr == DUALSENSE_USB_AUDIO_EP_OUT) {
        if (result == XFER_RESULT_SUCCESS) {
            if (xferred_bytes > 0 && xferred_bytes <= sizeof(s_audio_out_buffer)) {
                dualsense_haptic_audio_submit_packet(
                    s_audio_out_buffer,
                    (uint16_t)xferred_bytes,
                    DUALSENSE_HAPTIC_AUDIO_CHANNELS,
                    esp_timer_get_time());
            }
            s_packet_count++;
            if (s_packet_count == 1 || (s_packet_count % 5000) == 0) {
                ESP_LOGI(TAG,
                         "[DS5_UAC1] out_packet len=%lu count=%lu",
                         (unsigned long)xferred_bytes,
                         (unsigned long)s_packet_count);
            }
        }

        if (s_out_alt_setting != DS5_UAC1_ALT_STREAMING) {
            dualsense_runtime_uac1_note_out_xfer(
                (int32_t)result, xferred_bytes, true);
            return true;
        }

        bool rearmed = usbd_edpt_xfer(rhport,
                                      DUALSENSE_USB_AUDIO_EP_OUT,
                                      s_audio_out_buffer,
                                      sizeof(s_audio_out_buffer));
        dualsense_runtime_uac1_note_out_xfer(
            (int32_t)result, xferred_bytes, rearmed);
        if (result != XFER_RESULT_SUCCESS || !rearmed) {
            ESP_LOGW(TAG,
                     "[DS5_UAC1] out_xfer result=%d bytes=%lu rearmed=%s",
                     (int)result,
                     (unsigned long)xferred_bytes,
                     rearmed ? "true" : "false");
        }
        return rearmed;
    }

#if DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY
    if (ep_addr == DUALSENSE_USB_AUDIO_EP_IN) {
        if (s_in_alt_setting != DS5_UAC1_ALT_STREAMING) {
            return true;
        }
        memset(s_audio_in_buffer, 0, sizeof(s_audio_in_buffer));
        return usbd_edpt_xfer(rhport,
                              DUALSENSE_USB_AUDIO_EP_IN,
                              s_audio_in_buffer,
                              sizeof(s_audio_in_buffer));
    }
#endif

    return false;
#else
    (void)rhport;
    (void)ep_addr;
    (void)result;
    (void)xferred_bytes;
    return false;
#endif
}

static usbd_class_driver_t const s_uac1_driver[] = {{
    .name = "v5.5_uac1",
    .init = uac1_init,
    .deinit = uac1_deinit,
    .reset = uac1_reset,
    .open = uac1_open,
    .control_xfer_cb = uac1_control_xfer_cb,
    .xfer_cb = uac1_xfer_cb,
    .xfer_isr = NULL,
    .sof = NULL,
}};

usbd_class_driver_t const *usbd_app_driver_get_cb(uint8_t *driver_count)
{
    *driver_count = 1;
    return s_uac1_driver;
}

#endif

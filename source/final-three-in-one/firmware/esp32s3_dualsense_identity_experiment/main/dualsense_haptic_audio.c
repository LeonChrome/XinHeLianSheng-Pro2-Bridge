#include "dualsense_haptic_audio.h"

#include <string.h>

#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "haptic_audio_to_raw02.h"
#include "tusb.h"
#include "usb_dualsense_descriptor.h"

#ifndef DS5_ENABLE_UAC2_AUDIO
#define DS5_ENABLE_UAC2_AUDIO 0
#endif
#ifndef DS5_ENABLE_UAC1_AUDIO
#define DS5_ENABLE_UAC1_AUDIO 0
#endif

#define DS5_AUDIO_ENTITY_INPUT_TERMINAL 0x01
#define DS5_AUDIO_ENTITY_FEATURE_UNIT 0x02
#define DS5_AUDIO_ENTITY_CLOCK 0x04
#define DS5_AUDIO_FRAME_BYTES(channels) \
    ((channels) * DUALSENSE_HAPTIC_AUDIO_BYTES_PER_SAMPLE)
#define DS5_AUDIO_READ_BUFFER_BYTES 512
#define DS5_AUDIO_QUEUE_DEPTH 32
#define DS5_AUDIO_QUEUE_PACKET_BYTES DUALSENSE_USB_UAC1_PACKET_SIZE
#define DS5_AUDIO_BATCH_PACKETS 8
#define DS5_AUDIO_ACTIVITY_ENV_THRESHOLD 512
#define DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD 2048
#define DS5_AUDIO_DIAGNOSTIC_LOW_ENV_THRESHOLD 64
#define DS5_AUDIO_DIAGNOSTIC_LOW_PEAK_THRESHOLD 256
#define DS5_AUDIO_TRANSIENT_ENV_THRESHOLD 900
#define DS5_AUDIO_TRANSIENT_PEAK_THRESHOLD 4096
#define DS5_AUDIO_HD_FRONT_ENV_MAX 192
#define DS5_AUDIO_HD_FRONT_PEAK_MAX 768
#define DS5_AUDIO_HD_REAR_FRONT_RATIO 4
#define DS5_AUDIO_HD_REAR_ENV_MIN 1024
#define DS5_AUDIO_LOG_INTERVAL_ACTIVE_US 5000000LL
#define DS5_AUDIO_LOG_INTERVAL_IDLE_US 10000000LL
#define DS5_HAPTIC_DOWNSAMPLE_FACTOR 16
#define DS5_HAPTIC_SPECTRAL_RATE 3000
#define DS5_HAPTIC_SPECTRAL_WINDOW 64
#define DS5_HAPTIC_SPECTRAL_HOP 36
#define DS5_HAPTIC_LOW_BIN_MIN 2
#define DS5_HAPTIC_LOW_BIN_MAX 5
#define DS5_HAPTIC_HIGH_BIN_MIN 6
#define DS5_HAPTIC_HIGH_BIN_MAX 13
#define DS5_HAPTIC_SPECTRAL_MIN_RMS 96

static const char *TAG = "v5.5_audio";
static portMUX_TYPE s_lock = portMUX_INITIALIZER_UNLOCKED;
static bool s_task_started;
#if DS5_ENABLE_UAC2_AUDIO
static uint32_t s_sample_rate = DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE;
static uint8_t s_clock_valid = 1;
#endif
static int8_t s_mute[DUALSENSE_HAPTIC_AUDIO_CHANNELS + 1];
static int16_t s_volume[DUALSENSE_HAPTIC_AUDIO_CHANNELS + 1];
static dualsense_haptic_audio_features_t s_last_features;
static uint32_t s_submitted_packet_count;
static uint32_t s_dropped_packet_count;
static int64_t s_next_queue_drop_log_us;
static uint8_t s_queue_depth;
static uint8_t s_queue_high_watermark;
static uint32_t s_queue_full_count;
static uint32_t s_process_batch_count;
static uint32_t s_process_last_us;
static uint32_t s_process_max_us;
static int64_t s_next_feature_log_us;
static int32_t s_downsample_sum_l;
static int32_t s_downsample_sum_r;
static uint8_t s_downsample_count;
static int16_t s_spectral_l[DS5_HAPTIC_SPECTRAL_WINDOW];
static int16_t s_spectral_r[DS5_HAPTIC_SPECTRAL_WINDOW];
static uint8_t s_spectral_count;
#if DS5_ENABLE_UAC1_AUDIO
typedef struct {
    int64_t timestamp_us;
    uint16_t len;
    uint8_t channels;
    uint8_t data[DS5_AUDIO_QUEUE_PACKET_BYTES];
} haptic_audio_packet_t;

static QueueHandle_t s_audio_queue;
static StaticQueue_t s_audio_queue_storage;
static uint8_t s_audio_queue_buffer[
    DS5_AUDIO_QUEUE_DEPTH * sizeof(haptic_audio_packet_t)];
static TaskHandle_t s_audio_task_handle;

_Static_assert(DS5_AUDIO_QUEUE_PACKET_BYTES ==
                   DUALSENSE_USB_AUDIO_SAMPLE_RATE / 1000 *
                       DUALSENSE_HAPTIC_AUDIO_CHANNELS *
                       DUALSENSE_HAPTIC_AUDIO_BYTES_PER_SAMPLE,
               "UAC1 queue packet size must hold one fixed 1 ms audio frame");
#endif

static const int16_t s_goertzel_coeff_q14[] = {
    0, 32610, 32138, 31357, 30274, 28899, 27246,
    25330, 23170, 20788, 18205, 15447, 12540, 9512,
};

static uint32_t isqrt_u64(uint64_t value)
{
    uint64_t bit = 1ULL << 62;
    uint32_t result = 0;

    while (bit > value) {
        bit >>= 2;
    }
    while (bit != 0) {
        if (value >= (uint64_t)result + bit) {
            value -= (uint64_t)result + bit;
            result = (uint32_t)(((uint64_t)result >> 1) + bit);
        } else {
            result >>= 1;
        }
        bit >>= 2;
    }
    return result;
}

static uint16_t abs_i16(int16_t value)
{
    if (value == INT16_MIN) {
        return 32768u;
    }
    return (uint16_t)(value < 0 ? -value : value);
}

static int16_t read_i16_le(const uint8_t *data)
{
    return (int16_t)((uint16_t)data[0] | ((uint16_t)data[1] << 8));
}

static uint16_t smooth_envelope(uint16_t previous, uint16_t value)
{
    uint32_t mixed;
    if (value > previous) {
        mixed = ((uint32_t)previous * 2u + (uint32_t)value * 6u + 4u) / 8u;
    } else {
        mixed = ((uint32_t)previous * 7u + (uint32_t)value + 4u) / 8u;
    }
    return (uint16_t)(mixed > UINT16_MAX ? UINT16_MAX : mixed);
}

static uint16_t positive_delta(uint16_t value, uint16_t previous)
{
    return value > previous ? (uint16_t)(value - previous) : 0;
}

static uint16_t max_u16(uint16_t a, uint16_t b)
{
    return a > b ? a : b;
}

static uint64_t spectral_bin_power(const int16_t *samples, uint8_t bin)
{
    if (!samples || bin == 0 ||
        bin >= sizeof(s_goertzel_coeff_q14) /
                   sizeof(s_goertzel_coeff_q14[0])) {
        return 0;
    }

    int64_t s1 = 0;
    int64_t s2 = 0;
    int32_t coeff = s_goertzel_coeff_q14[bin];
    for (uint16_t i = 0; i < DS5_HAPTIC_SPECTRAL_WINDOW; i++) {
        int64_t sample = samples[i] >> 4;
        int64_t s0 = sample + ((coeff * s1) >> 14) - s2;
        s2 = s1;
        s1 = s0;
    }

    int64_t power =
        s1 * s1 + s2 * s2 - ((coeff * s1 * s2) >> 14);
    return power > 0 ? (uint64_t)power : 0;
}

static void spectral_analyze_band(const int16_t *samples,
                                  uint8_t min_bin,
                                  uint8_t max_bin,
                                  uint16_t fallback_hz,
                                  uint16_t *frequency,
                                  uint16_t *rms)
{
    uint64_t band_power = 0;
    uint64_t peak_power = 0;
    uint8_t peak_bin = 0;
    for (uint8_t bin = min_bin; bin <= max_bin; bin++) {
        uint64_t power = spectral_bin_power(samples, bin);
        band_power += power;
        if (power > peak_power) {
            peak_power = power;
            peak_bin = bin;
        }
    }

    if (band_power == 0 || peak_bin == 0) {
        *frequency = fallback_hz;
        *rms = 0;
        return;
    }

    uint64_t rms_scaled =
        isqrt_u64(band_power * 2u) / DS5_HAPTIC_SPECTRAL_WINDOW;
    rms_scaled <<= 4;
    if (rms_scaled > UINT16_MAX) {
        rms_scaled = UINT16_MAX;
    }
    *rms = (uint16_t)rms_scaled;
    *frequency = (uint16_t)(
        ((uint32_t)peak_bin * DS5_HAPTIC_SPECTRAL_RATE +
         DS5_HAPTIC_SPECTRAL_WINDOW / 2) /
        DS5_HAPTIC_SPECTRAL_WINDOW);
}

static void spectral_analyze_side(const int16_t *input,
                                  uint16_t previous_low_freq,
                                  uint16_t previous_high_freq,
                                  uint16_t *low_freq,
                                  uint16_t *high_freq,
                                  uint16_t *low_rms,
                                  uint16_t *high_rms)
{
    int16_t centered[DS5_HAPTIC_SPECTRAL_WINDOW];
    int64_t sum = 0;
    for (uint16_t i = 0; i < DS5_HAPTIC_SPECTRAL_WINDOW; i++) {
        sum += input[i];
    }
    int32_t mean = (int32_t)(sum / DS5_HAPTIC_SPECTRAL_WINDOW);
    for (uint16_t i = 0; i < DS5_HAPTIC_SPECTRAL_WINDOW; i++) {
        centered[i] = (int16_t)((int32_t)input[i] - mean);
    }

    uint16_t detected_low;
    uint16_t detected_high;
    spectral_analyze_band(centered,
                          DS5_HAPTIC_LOW_BIN_MIN,
                          DS5_HAPTIC_LOW_BIN_MAX,
                          previous_low_freq ? previous_low_freq : 0x112,
                          &detected_low,
                          low_rms);
    spectral_analyze_band(centered,
                          DS5_HAPTIC_HIGH_BIN_MIN,
                          DS5_HAPTIC_HIGH_BIN_MAX,
                          previous_high_freq ? previous_high_freq : 0x187,
                          &detected_high,
                          high_rms);

    *low_freq = previous_low_freq ?
        (uint16_t)(((uint32_t)previous_low_freq + detected_low + 1u) / 2u) :
        detected_low;
    *high_freq = previous_high_freq ?
        (uint16_t)(((uint32_t)previous_high_freq + detected_high + 1u) / 2u) :
        detected_high;
}

static void spectral_accumulate(const uint8_t *data,
                                uint16_t frames,
                                uint16_t frame_bytes,
                                uint8_t left_offset,
                                uint8_t right_offset,
                                dualsense_haptic_audio_features_t *features)
{
    if (!data || !features) {
        return;
    }
    features->spectral_ready = false;

    for (uint16_t frame = 0; frame < frames; frame++) {
        const uint8_t *base = data + frame * frame_bytes;
        s_downsample_sum_l += read_i16_le(base + left_offset);
        s_downsample_sum_r += read_i16_le(base + right_offset);
        s_downsample_count++;
        if (s_downsample_count < DS5_HAPTIC_DOWNSAMPLE_FACTOR) {
            continue;
        }

        int16_t sample_l =
            (int16_t)(s_downsample_sum_l / DS5_HAPTIC_DOWNSAMPLE_FACTOR);
        int16_t sample_r =
            (int16_t)(s_downsample_sum_r / DS5_HAPTIC_DOWNSAMPLE_FACTOR);
        s_downsample_sum_l = 0;
        s_downsample_sum_r = 0;
        s_downsample_count = 0;

        if (s_spectral_count < DS5_HAPTIC_SPECTRAL_WINDOW) {
            s_spectral_l[s_spectral_count] = sample_l;
            s_spectral_r[s_spectral_count] = sample_r;
            s_spectral_count++;
        }
        if (s_spectral_count < DS5_HAPTIC_SPECTRAL_WINDOW) {
            continue;
        }

        spectral_analyze_side(s_spectral_l,
                              features->spectral_low_freq_l,
                              features->spectral_high_freq_l,
                              &features->spectral_low_freq_l,
                              &features->spectral_high_freq_l,
                              &features->spectral_low_rms_l,
                              &features->spectral_high_rms_l);
        spectral_analyze_side(s_spectral_r,
                              features->spectral_low_freq_r,
                              features->spectral_high_freq_r,
                              &features->spectral_low_freq_r,
                              &features->spectral_high_freq_r,
                              &features->spectral_low_rms_r,
                              &features->spectral_high_rms_r);
        features->spectral_ready =
            features->spectral_low_rms_l >= DS5_HAPTIC_SPECTRAL_MIN_RMS ||
            features->spectral_low_rms_r >= DS5_HAPTIC_SPECTRAL_MIN_RMS ||
            features->spectral_high_rms_l >= DS5_HAPTIC_SPECTRAL_MIN_RMS ||
            features->spectral_high_rms_r >= DS5_HAPTIC_SPECTRAL_MIN_RMS;

        memmove(s_spectral_l,
                s_spectral_l + DS5_HAPTIC_SPECTRAL_HOP,
                (DS5_HAPTIC_SPECTRAL_WINDOW - DS5_HAPTIC_SPECTRAL_HOP) *
                    sizeof(s_spectral_l[0]));
        memmove(s_spectral_r,
                s_spectral_r + DS5_HAPTIC_SPECTRAL_HOP,
                (DS5_HAPTIC_SPECTRAL_WINDOW - DS5_HAPTIC_SPECTRAL_HOP) *
                    sizeof(s_spectral_r[0]));
        s_spectral_count =
            DS5_HAPTIC_SPECTRAL_WINDOW - DS5_HAPTIC_SPECTRAL_HOP;
    }
}

const char *dualsense_haptic_audio_parser_string(dualsense_haptic_audio_parser_t mode)
{
    switch (mode) {
    case DUALSENSE_HAPTIC_AUDIO_PARSER_FRONT:
        return "front";
    case DUALSENSE_HAPTIC_AUDIO_PARSER_STRONGEST:
        return "strongest";
    case DUALSENSE_HAPTIC_AUDIO_PARSER_REAR:
    default:
        return "rear";
    }
}

bool dualsense_haptic_audio_parse_parser(const char *text,
                                         dualsense_haptic_audio_parser_t *out)
{
    if (!text || !out) {
        return false;
    }
    if (strcmp(text, "rear") == 0 || strcmp(text, "ch2_ch3") == 0) {
        *out = DUALSENSE_HAPTIC_AUDIO_PARSER_REAR;
        return true;
    }
    if (strcmp(text, "front") == 0 || strcmp(text, "ch0_ch1") == 0) {
        *out = DUALSENSE_HAPTIC_AUDIO_PARSER_FRONT;
        return true;
    }
    if (strcmp(text, "strongest") == 0 || strcmp(text, "auto_pair") == 0) {
        *out = DUALSENSE_HAPTIC_AUDIO_PARSER_STRONGEST;
        return true;
    }
    return false;
}

void dualsense_haptic_audio_set_parser(dualsense_haptic_audio_parser_t mode)
{
    portENTER_CRITICAL(&s_lock);
    s_last_features.parser_mode = (uint8_t)mode;
    portEXIT_CRITICAL(&s_lock);
}

void dualsense_haptic_audio_process_packet(const uint8_t *data,
                                           uint16_t len,
                                           uint8_t channels,
                                           int64_t now_us)
{
    if (!data || channels < 2) {
        portENTER_CRITICAL(&s_lock);
        s_last_features.overrun_count++;
        portEXIT_CRITICAL(&s_lock);
        return;
    }

    uint16_t frame_bytes = DS5_AUDIO_FRAME_BYTES(channels);
    if (frame_bytes == 0) {
        return;
    }
    uint16_t frames = len / frame_bytes;
    if (frames == 0) {
        return;
    }

    uint16_t aligned_len = (uint16_t)(frames * frame_bytes);
    bool partial_frame = aligned_len != len;
    uint8_t rear_left_offset = channels >= 4 ? 4 : 0;
    uint8_t rear_right_offset = channels >= 4 ? 6 : 2;
    uint64_t sum_sq_rear_l = 0;
    uint64_t sum_sq_rear_r = 0;
    uint64_t sum_sq_front_l = 0;
    uint64_t sum_sq_front_r = 0;
    uint64_t sum_abs_rear_l = 0;
    uint64_t sum_abs_rear_r = 0;
    uint64_t sum_abs_front_l = 0;
    uint64_t sum_abs_front_r = 0;
    uint16_t rear_peak_l = 0;
    uint16_t rear_peak_r = 0;
    uint16_t front_peak_l = 0;
    uint16_t front_peak_r = 0;

    for (uint16_t frame = 0; frame < frames; frame++) {
        const uint8_t *base = data + (frame * frame_bytes);
        uint16_t abs_front_l = abs_i16(read_i16_le(base + 0));
        uint16_t abs_front_r = abs_i16(read_i16_le(base + 2));
        uint16_t abs_rear_l = abs_i16(read_i16_le(base + rear_left_offset));
        uint16_t abs_rear_r = abs_i16(read_i16_le(base + rear_right_offset));
        sum_abs_front_l += abs_front_l;
        sum_abs_front_r += abs_front_r;
        sum_abs_rear_l += abs_rear_l;
        sum_abs_rear_r += abs_rear_r;
        sum_sq_front_l += (uint64_t)abs_front_l * abs_front_l;
        sum_sq_front_r += (uint64_t)abs_front_r * abs_front_r;
        sum_sq_rear_l += (uint64_t)abs_rear_l * abs_rear_l;
        sum_sq_rear_r += (uint64_t)abs_rear_r * abs_rear_r;
        if (abs_front_l > front_peak_l) {
            front_peak_l = abs_front_l;
        }
        if (abs_front_r > front_peak_r) {
            front_peak_r = abs_front_r;
        }
        if (abs_rear_l > rear_peak_l) {
            rear_peak_l = abs_rear_l;
        }
        if (abs_rear_r > rear_peak_r) {
            rear_peak_r = abs_rear_r;
        }
    }

    dualsense_haptic_audio_features_t features;
    dualsense_haptic_audio_features_t previous;
    dualsense_haptic_audio_parser_t parser =
        DUALSENSE_HAPTIC_AUDIO_PARSER_REAR;

    portENTER_CRITICAL(&s_lock);
    previous = s_last_features;
    parser = (dualsense_haptic_audio_parser_t)previous.parser_mode;
    portEXIT_CRITICAL(&s_lock);

    features = previous;
    features.spectral_ready = false;
    uint32_t packet_units =
        (frames + DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE / 1000 - 1) /
        (DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE / 1000);
    features.packet_count += packet_units;
    features.frame_count += frames;
    features.last_packet_len = len;
    features.source_channels = channels;
    bool choose_front = channels < 4;
    if (channels >= 4) {
        if (parser == DUALSENSE_HAPTIC_AUDIO_PARSER_FRONT) {
            choose_front = true;
        } else if (parser == DUALSENSE_HAPTIC_AUDIO_PARSER_STRONGEST) {
            uint32_t front_score =
                (uint32_t)(sum_abs_front_l / frames) +
                (uint32_t)(sum_abs_front_r / frames) +
                (uint32_t)front_peak_l + (uint32_t)front_peak_r;
            uint32_t rear_score =
                (uint32_t)(sum_abs_rear_l / frames) +
                (uint32_t)(sum_abs_rear_r / frames) +
                (uint32_t)rear_peak_l + (uint32_t)rear_peak_r;
            choose_front = front_score > rear_score;
        }
    }

    uint64_t selected_sum_sq_l = choose_front ? sum_sq_front_l : sum_sq_rear_l;
    uint64_t selected_sum_sq_r = choose_front ? sum_sq_front_r : sum_sq_rear_r;
    uint64_t selected_sum_abs_l = choose_front ? sum_abs_front_l : sum_abs_rear_l;
    uint64_t selected_sum_abs_r = choose_front ? sum_abs_front_r : sum_abs_rear_r;
    uint16_t selected_peak_l = choose_front ? front_peak_l : rear_peak_l;
    uint16_t selected_peak_r = choose_front ? front_peak_r : rear_peak_r;

    features.selected_front_pair = choose_front;
    features.rms_l = (uint16_t)isqrt_u64(selected_sum_sq_l / frames);
    features.rms_r = (uint16_t)isqrt_u64(selected_sum_sq_r / frames);
    features.front_rms_l = (uint16_t)isqrt_u64(sum_sq_front_l / frames);
    features.front_rms_r = (uint16_t)isqrt_u64(sum_sq_front_r / frames);
    features.mean_abs_l = (uint16_t)(selected_sum_abs_l / frames);
    features.mean_abs_r = (uint16_t)(selected_sum_abs_r / frames);
    features.front_mean_abs_l = (uint16_t)(sum_abs_front_l / frames);
    features.front_mean_abs_r = (uint16_t)(sum_abs_front_r / frames);
    uint16_t rear_mean_abs_l = (uint16_t)(sum_abs_rear_l / frames);
    uint16_t rear_mean_abs_r = (uint16_t)(sum_abs_rear_r / frames);
    features.peak_l = selected_peak_l;
    features.peak_r = selected_peak_r;
    features.front_peak_l = front_peak_l;
    features.front_peak_r = front_peak_r;
    features.envelope_l = smooth_envelope(previous.envelope_l, features.mean_abs_l);
    features.envelope_r = smooth_envelope(previous.envelope_r, features.mean_abs_r);
    features.front_envelope_l = smooth_envelope(previous.front_envelope_l, features.front_mean_abs_l);
    features.front_envelope_r = smooth_envelope(previous.front_envelope_r, features.front_mean_abs_r);
    uint16_t env_delta_l = positive_delta(features.envelope_l, previous.envelope_l);
    uint16_t env_delta_r = positive_delta(features.envelope_r, previous.envelope_r);
    uint16_t peak_delta_l = positive_delta(features.peak_l, previous.peak_l);
    uint16_t peak_delta_r = positive_delta(features.peak_r, previous.peak_r);
    features.transient_l = max_u16(env_delta_l, peak_delta_l);
    features.transient_r = max_u16(env_delta_r, peak_delta_r);
    features.activity = features.envelope_l >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
                        features.envelope_r >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
                        selected_peak_l >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD ||
                        selected_peak_r >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD;
    features.transient = env_delta_l >= DS5_AUDIO_TRANSIENT_ENV_THRESHOLD ||
                         env_delta_r >= DS5_AUDIO_TRANSIENT_ENV_THRESHOLD ||
                         peak_delta_l >= DS5_AUDIO_TRANSIENT_PEAK_THRESHOLD ||
                         peak_delta_r >= DS5_AUDIO_TRANSIENT_PEAK_THRESHOLD;
    uint16_t rear_env = max_u16(features.envelope_l, features.envelope_r);
    uint16_t rear_peak = max_u16(rear_peak_l, rear_peak_r);
    uint16_t front_env = max_u16(features.front_envelope_l, features.front_envelope_r);
    uint16_t front_peak = max_u16(features.front_peak_l, features.front_peak_r);
    bool front_quiet = front_env <= DS5_AUDIO_HD_FRONT_ENV_MAX &&
                       front_peak <= DS5_AUDIO_HD_FRONT_PEAK_MAX;
    uint32_t rear_front_required =
        (uint32_t)front_env * DS5_AUDIO_HD_REAR_FRONT_RATIO +
        DS5_AUDIO_HD_REAR_ENV_MIN;
    bool rear_dominates = rear_env >= DS5_AUDIO_HD_REAR_ENV_MIN &&
                          (uint32_t)rear_env >= rear_front_required;
    features.hd_candidate = channels >= 4 && features.activity &&
                            (front_quiet || rear_dominates) &&
                            rear_peak >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD;
    features.pcm_like = features.activity && !features.hd_candidate;
    bool front_activity =
        features.front_mean_abs_l >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
        features.front_mean_abs_r >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
        front_peak_l >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD ||
        front_peak_r >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD;
    bool rear_activity =
        rear_mean_abs_l >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
        rear_mean_abs_r >= DS5_AUDIO_ACTIVITY_ENV_THRESHOLD ||
        rear_peak_l >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD ||
        rear_peak_r >= DS5_AUDIO_ACTIVITY_PEAK_THRESHOLD;
    bool rear_low_energy =
        !rear_activity &&
        (rear_mean_abs_l >= DS5_AUDIO_DIAGNOSTIC_LOW_ENV_THRESHOLD ||
         rear_mean_abs_r >= DS5_AUDIO_DIAGNOSTIC_LOW_ENV_THRESHOLD ||
         rear_peak_l >= DS5_AUDIO_DIAGNOSTIC_LOW_PEAK_THRESHOLD ||
         rear_peak_r >= DS5_AUDIO_DIAGNOSTIC_LOW_PEAK_THRESHOLD);
    if (front_activity) {
        features.front_active_packet_count += packet_units;
    }
    if (rear_activity) {
        features.rear_active_packet_count += packet_units;
    }
    if (front_activity && rear_activity) {
        features.both_active_packet_count += packet_units;
    } else if (front_activity) {
        features.front_only_packet_count += packet_units;
    } else if (rear_activity) {
        features.rear_only_packet_count += packet_units;
    }
    if (rear_low_energy) {
        features.rear_low_energy_packet_count += packet_units;
    }
    if (features.activity) {
        features.active_packet_count += packet_units;
    } else {
        features.silence_packet_count += packet_units;
    }
    if (partial_frame) {
        features.overrun_count++;
    }
    features.parser_mode = (uint8_t)parser;
    spectral_accumulate(data,
                        frames,
                        frame_bytes,
                        choose_front ? 0 : rear_left_offset,
                        choose_front ? 2 : rear_right_offset,
                        &features);

    portENTER_CRITICAL(&s_lock);
    s_last_features = features;
    portEXIT_CRITICAL(&s_lock);

    haptic_audio_to_raw02_process_features(&features, now_us);

    int64_t interval_us = features.activity ?
        DS5_AUDIO_LOG_INTERVAL_ACTIVE_US : DS5_AUDIO_LOG_INTERVAL_IDLE_US;
    if (now_us >= s_next_feature_log_us &&
        (features.activity || features.packet_count == 1 ||
         (features.packet_count % 1000u) == 0u)) {
        s_next_feature_log_us = now_us + interval_us;
        ESP_LOGI(TAG,
                 "[DS5_HAPTIC_AUDIO] packet=%lu len=%u channels=%u parser=%s pair=%s frames=%lu active=%lu silence=%lu front_rms_l=%u front_rms_r=%u rear_rms_l=%u rear_rms_r=%u front_peak_l=%u front_peak_r=%u rear_peak_l=%u rear_peak_r=%u front_env_l=%u front_env_r=%u rear_env_l=%u rear_env_r=%u transient_l=%u transient_r=%u spectral=%s low_hz=(%u,%u) high_hz=(%u,%u) low_rms=(%u,%u) high_rms=(%u,%u) activity=%s transient=%s hd_candidate=%s pcm_like=%s overrun=%lu",
                 (unsigned long)features.packet_count,
                 (unsigned)features.last_packet_len,
                 (unsigned)features.source_channels,
                 dualsense_haptic_audio_parser_string(parser),
                 features.selected_front_pair ? "front" : "rear",
                 (unsigned long)features.frame_count,
                 (unsigned long)features.active_packet_count,
                 (unsigned long)features.silence_packet_count,
                 features.front_rms_l,
                 features.front_rms_r,
                 features.rms_l,
                 features.rms_r,
                 features.front_peak_l,
                 features.front_peak_r,
                 features.peak_l,
                 features.peak_r,
                 features.front_envelope_l,
                 features.front_envelope_r,
                 features.envelope_l,
                 features.envelope_r,
                 features.transient_l,
                 features.transient_r,
                 features.spectral_ready ? "true" : "false",
                 features.spectral_low_freq_l,
                 features.spectral_low_freq_r,
                 features.spectral_high_freq_l,
                 features.spectral_high_freq_r,
                 features.spectral_low_rms_l,
                 features.spectral_low_rms_r,
                 features.spectral_high_rms_l,
                 features.spectral_high_rms_r,
                 features.activity ? "true" : "false",
                 features.transient ? "true" : "false",
                 features.hd_candidate ? "true" : "false",
                 features.pcm_like ? "true" : "false",
                 (unsigned long)features.overrun_count);
    }
}

bool dualsense_haptic_audio_submit_packet(const uint8_t *data,
                                          uint16_t len,
                                          uint8_t channels,
                                          int64_t now_us)
{
#if DS5_ENABLE_UAC1_AUDIO
    if (!data || len == 0 || len > DS5_AUDIO_QUEUE_PACKET_BYTES ||
        !s_audio_queue) {
        portENTER_CRITICAL(&s_lock);
        s_dropped_packet_count++;
        portEXIT_CRITICAL(&s_lock);
        if (now_us >= s_next_queue_drop_log_us) {
            s_next_queue_drop_log_us = now_us + 1000000LL;
            ESP_LOGW(TAG,
                     "[DS5_AUDIO_QUEUE] accepted=false reason=%s len=%u max=%u queue_ready=%s",
                     !data || len == 0 ? "invalid_packet" :
                     len > DS5_AUDIO_QUEUE_PACKET_BYTES ? "oversize" :
                     "queue_not_ready",
                     (unsigned)len,
                     (unsigned)DS5_AUDIO_QUEUE_PACKET_BYTES,
                     s_audio_queue ? "true" : "false");
        }
        return false;
    }

    haptic_audio_packet_t packet = {
        .timestamp_us = now_us,
        .len = len,
        .channels = channels,
    };
    memcpy(packet.data, data, len);
    if (xQueueSend(s_audio_queue, &packet, 0) != pdPASS) {
        portENTER_CRITICAL(&s_lock);
        s_dropped_packet_count++;
        s_queue_full_count++;
        portEXIT_CRITICAL(&s_lock);
        return false;
    }

    UBaseType_t depth = uxQueueMessagesWaiting(s_audio_queue);
    portENTER_CRITICAL(&s_lock);
    s_submitted_packet_count++;
    s_queue_depth = depth > UINT8_MAX ? UINT8_MAX : (uint8_t)depth;
    if (s_queue_depth > s_queue_high_watermark) {
        s_queue_high_watermark = s_queue_depth;
    }
    portEXIT_CRITICAL(&s_lock);
    return true;
#else
    dualsense_haptic_audio_process_packet(data, len, channels, now_us);
    return true;
#endif
}

void dualsense_haptic_audio_set_streaming(bool streaming, uint8_t alt_setting)
{
    int64_t now_us = esp_timer_get_time();
    portENTER_CRITICAL(&s_lock);
    s_last_features.streaming = streaming;
    s_last_features.alt_setting = alt_setting;
    if (!streaming) {
        s_last_features.activity = false;
        s_last_features.transient = false;
        s_last_features.envelope_l = 0;
        s_last_features.envelope_r = 0;
        s_last_features.front_envelope_l = 0;
        s_last_features.front_envelope_r = 0;
        s_last_features.transient_l = 0;
        s_last_features.transient_r = 0;
        s_last_features.hd_candidate = false;
        s_last_features.pcm_like = false;
    }
    portEXIT_CRITICAL(&s_lock);

    if (!streaming) {
#if DS5_ENABLE_UAC1_AUDIO
        if (s_audio_queue) {
            xQueueReset(s_audio_queue);
        }
        portENTER_CRITICAL(&s_lock);
        s_queue_depth = 0;
        portEXIT_CRITICAL(&s_lock);
#endif
        s_downsample_sum_l = 0;
        s_downsample_sum_r = 0;
        s_downsample_count = 0;
        s_spectral_count = 0;
        haptic_audio_to_raw02_note_audio_stopped(now_us);
    }
    ESP_LOGI(TAG,
             "[DS5_HAPTIC_AUDIO] streaming=%s alt=%u",
             streaming ? "true" : "false",
             (unsigned)alt_setting);
}

#if DS5_ENABLE_UAC2_AUDIO
static void process_audio_packet_uac2(const uint8_t *data, uint16_t len)
{
    dualsense_haptic_audio_process_packet(data,
                                          len,
                                          DUALSENSE_HAPTIC_AUDIO_CHANNELS,
                                          esp_timer_get_time());
}

static void haptic_audio_task(void *arg)
{
    (void)arg;
    uint8_t buffer[DS5_AUDIO_READ_BUFFER_BYTES];

    while (true) {
        uint16_t available = tud_audio_available();
        while (available >= DS5_AUDIO_FRAME_BYTES(DUALSENSE_HAPTIC_AUDIO_CHANNELS)) {
            uint16_t read_len = available > sizeof(buffer) ? sizeof(buffer) : available;
            read_len = (uint16_t)(read_len - (read_len % DS5_AUDIO_FRAME_BYTES(DUALSENSE_HAPTIC_AUDIO_CHANNELS)));
            if (read_len == 0) {
                break;
            }
            uint16_t bytes_read = tud_audio_read(buffer, read_len);
            if (bytes_read == 0) {
                break;
            }
            if (available > sizeof(buffer)) {
                portENTER_CRITICAL(&s_lock);
                s_last_features.overrun_count++;
                portEXIT_CRITICAL(&s_lock);
            }
            process_audio_packet_uac2(buffer, bytes_read);
            available = tud_audio_available();
        }

        vTaskDelay(pdMS_TO_TICKS(1));
    }
}
#endif

#if DS5_ENABLE_UAC1_AUDIO
static void haptic_audio_queue_task(void *arg)
{
    (void)arg;
    haptic_audio_packet_t packet;
    uint8_t batch[DS5_AUDIO_BATCH_PACKETS * DS5_AUDIO_QUEUE_PACKET_BYTES];

    while (true) {
        if (xQueueReceive(s_audio_queue, &packet, portMAX_DELAY) != pdPASS) {
            continue;
        }

        uint16_t batch_len = packet.len;
        uint8_t batch_channels = packet.channels;
        int64_t batch_timestamp_us = packet.timestamp_us;
        int64_t process_start_us = esp_timer_get_time();
        memcpy(batch, packet.data, packet.len);

        for (uint8_t count = 1; count < DS5_AUDIO_BATCH_PACKETS; count++) {
            if (xQueueReceive(s_audio_queue, &packet, 0) != pdPASS) {
                break;
            }
            if (packet.channels != batch_channels ||
                batch_len + packet.len > sizeof(batch)) {
                dualsense_haptic_audio_process_packet(batch,
                                                      batch_len,
                                                      batch_channels,
                                                      batch_timestamp_us);
                batch_len = 0;
                batch_channels = packet.channels;
            }
            memcpy(batch + batch_len, packet.data, packet.len);
            batch_len += packet.len;
            batch_timestamp_us = packet.timestamp_us;
        }

        dualsense_haptic_audio_process_packet(batch,
                                              batch_len,
                                              batch_channels,
                                              batch_timestamp_us);
        int64_t process_elapsed_us = esp_timer_get_time() - process_start_us;
        UBaseType_t depth = uxQueueMessagesWaiting(s_audio_queue);
        portENTER_CRITICAL(&s_lock);
        s_queue_depth = depth > UINT8_MAX ? UINT8_MAX : (uint8_t)depth;
        s_process_batch_count++;
        s_process_last_us = process_elapsed_us > UINT32_MAX ?
            UINT32_MAX : (uint32_t)process_elapsed_us;
        if (s_process_last_us > s_process_max_us) {
            s_process_max_us = s_process_last_us;
        }
        portEXIT_CRITICAL(&s_lock);
    }
}
#endif

void dualsense_haptic_audio_init(void)
{
    memset(s_mute, 0, sizeof(s_mute));
    memset(s_volume, 0, sizeof(s_volume));
    memset(&s_last_features, 0, sizeof(s_last_features));
    s_submitted_packet_count = 0;
    s_dropped_packet_count = 0;
    s_next_queue_drop_log_us = 0;
    s_queue_depth = 0;
    s_queue_high_watermark = 0;
    s_queue_full_count = 0;
    s_process_batch_count = 0;
    s_process_last_us = 0;
    s_process_max_us = 0;
    s_last_features.parser_mode = (uint8_t)DUALSENSE_HAPTIC_AUDIO_PARSER_REAR;
    haptic_audio_to_raw02_init();

#if DS5_ENABLE_UAC1_AUDIO
    if (!s_audio_queue) {
        s_audio_queue = xQueueCreateStatic(
            DS5_AUDIO_QUEUE_DEPTH,
            sizeof(haptic_audio_packet_t),
            s_audio_queue_buffer,
            &s_audio_queue_storage);
        ESP_ERROR_CHECK(s_audio_queue ? ESP_OK : ESP_FAIL);
    }
    if (!s_task_started) {
        BaseType_t created = xTaskCreate(haptic_audio_queue_task,
                                         "ds5_audio",
                                         8192,
                                         NULL,
                                         5,
                                         &s_audio_task_handle);
        ESP_ERROR_CHECK(created == pdPASS ? ESP_OK : ESP_FAIL);
        s_task_started = true;
    }
#elif DS5_ENABLE_UAC2_AUDIO
    if (!s_task_started) {
        BaseType_t created = xTaskCreate(haptic_audio_task,
                                         "ds5_audio",
                                         4096,
                                         NULL,
                                         4,
                                         &s_audio_task_handle);
        ESP_ERROR_CHECK(created == pdPASS ? ESP_OK : ESP_FAIL);
        s_task_started = true;
    }
#else
    (void)s_task_started;
#endif

    ESP_LOGI(TAG,
             "[DS5_AUDIO] enabled=true sample_rate=%u channels=%u bytes_per_sample=%u parser=%s source_filter=hd_only",
             DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE,
             DUALSENSE_HAPTIC_AUDIO_CHANNELS,
             DUALSENSE_HAPTIC_AUDIO_BYTES_PER_SAMPLE,
             dualsense_haptic_audio_parser_string(
                 (dualsense_haptic_audio_parser_t)s_last_features.parser_mode));
}

bool dualsense_haptic_audio_snapshot(dualsense_haptic_audio_features_t *out)
{
    if (!out) {
        return false;
    }
    portENTER_CRITICAL(&s_lock);
    *out = s_last_features;
    out->submitted_packet_count = s_submitted_packet_count;
    out->dropped_packet_count = s_dropped_packet_count;
    out->queue_depth = s_queue_depth;
    out->queue_high_watermark = s_queue_high_watermark;
    out->queue_full_count = s_queue_full_count;
    out->process_batch_count = s_process_batch_count;
    out->process_last_us = s_process_last_us;
    out->process_max_us = s_process_max_us;
#if DS5_ENABLE_UAC1_AUDIO
    out->task_stack_high_watermark_bytes =
        s_audio_task_handle ? uxTaskGetStackHighWaterMark(s_audio_task_handle) : 0;
#else
    out->task_stack_high_watermark_bytes = 0;
#endif
    portEXIT_CRITICAL(&s_lock);
    return true;
}

#if DS5_ENABLE_UAC2_AUDIO
bool tud_audio_set_itf_cb(uint8_t rhport, tusb_control_request_t const *request)
{
    (void)rhport;
    uint8_t itf = tu_u16_low(tu_le16toh(request->wIndex));
    uint8_t alt = tu_u16_low(tu_le16toh(request->wValue));

    dualsense_haptic_audio_set_streaming(alt != 0, alt);

    ESP_LOGI(TAG,
             "[DS5_AUDIO] mounted=true interface=%u alt=%u sample_rate=%lu channels=%u",
             itf,
             alt,
             (unsigned long)s_sample_rate,
             DUALSENSE_HAPTIC_AUDIO_CHANNELS);
    return true;
}

bool tud_audio_set_itf_close_ep_cb(uint8_t rhport,
                                   tusb_control_request_t const *request)
{
    (void)rhport;
    (void)request;
    dualsense_haptic_audio_set_streaming(false, 0);
    ESP_LOGI(TAG, "[DS5_AUDIO] mounted=false");
    return true;
}

bool tud_audio_get_req_entity_cb(uint8_t rhport,
                                 tusb_control_request_t const *request_raw)
{
    audio_control_request_t const *request =
        (audio_control_request_t const *)request_raw;

    if (request->bEntityID == DS5_AUDIO_ENTITY_CLOCK) {
        if (request->bControlSelector == AUDIO_CS_CTRL_SAM_FREQ) {
            if (request->bRequest == AUDIO_CS_REQ_CUR) {
                audio_control_cur_4_t cur = {.bCur = (int32_t)s_sample_rate};
                return tud_audio_buffer_and_schedule_control_xfer(
                    rhport, request_raw, &cur, sizeof(cur));
            }
            if (request->bRequest == AUDIO_CS_REQ_RANGE) {
                audio_control_range_4_n_t(1) range = {
                    .wNumSubRanges = 1,
                    .subrange = {{
                        .bMin = DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE,
                        .bMax = DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE,
                        .bRes = 0,
                    }},
                };
                return tud_audio_buffer_and_schedule_control_xfer(
                    rhport, request_raw, &range, sizeof(range));
            }
        }
        if (request->bControlSelector == AUDIO_CS_CTRL_CLK_VALID &&
            request->bRequest == AUDIO_CS_REQ_CUR) {
            audio_control_cur_1_t cur = {.bCur = (int8_t)s_clock_valid};
            return tud_audio_buffer_and_schedule_control_xfer(
                rhport, request_raw, &cur, sizeof(cur));
        }
    }

    if (request->bEntityID == DS5_AUDIO_ENTITY_FEATURE_UNIT &&
        request->bChannelNumber <= DUALSENSE_HAPTIC_AUDIO_CHANNELS) {
        uint8_t channel = request->bChannelNumber;
        if (request->bControlSelector == AUDIO_FU_CTRL_MUTE &&
            request->bRequest == AUDIO_CS_REQ_CUR) {
            audio_control_cur_1_t cur = {.bCur = s_mute[channel]};
            return tud_audio_buffer_and_schedule_control_xfer(
                rhport, request_raw, &cur, sizeof(cur));
        }
        if (request->bControlSelector == AUDIO_FU_CTRL_VOLUME) {
            if (request->bRequest == AUDIO_CS_REQ_CUR) {
                audio_control_cur_2_t cur = {.bCur = s_volume[channel]};
                return tud_audio_buffer_and_schedule_control_xfer(
                    rhport, request_raw, &cur, sizeof(cur));
            }
            if (request->bRequest == AUDIO_CS_REQ_RANGE) {
                audio_control_range_2_n_t(1) range = {
                    .wNumSubRanges = 1,
                    .subrange = {{
                        .bMin = -50 * 256,
                        .bMax = 0,
                        .bRes = 256,
                    }},
                };
                return tud_audio_buffer_and_schedule_control_xfer(
                    rhport, request_raw, &range, sizeof(range));
            }
        }
    }

    ESP_LOGW(TAG,
             "[DS5_AUDIO] get_req unsupported entity=%u selector=%u request=%u",
             request->bEntityID,
             request->bControlSelector,
             request->bRequest);
    return false;
}

bool tud_audio_set_req_entity_cb(uint8_t rhport,
                                 tusb_control_request_t const *request_raw,
                                 uint8_t *buffer)
{
    (void)rhport;
    audio_control_request_t const *request =
        (audio_control_request_t const *)request_raw;

    if (request->bRequest != AUDIO_CS_REQ_CUR) {
        return false;
    }

    if (request->bEntityID == DS5_AUDIO_ENTITY_CLOCK &&
        request->bControlSelector == AUDIO_CS_CTRL_SAM_FREQ &&
        request->wLength == sizeof(audio_control_cur_4_t)) {
        uint32_t requested =
            (uint32_t)((audio_control_cur_4_t const *)buffer)->bCur;
        if (requested == DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE) {
            s_sample_rate = requested;
            ESP_LOGI(TAG, "[DS5_AUDIO] sample_rate=%lu", (unsigned long)s_sample_rate);
            return true;
        }
    }

    if (request->bEntityID == DS5_AUDIO_ENTITY_FEATURE_UNIT &&
        request->bChannelNumber <= DUALSENSE_HAPTIC_AUDIO_CHANNELS) {
        uint8_t channel = request->bChannelNumber;
        if (request->bControlSelector == AUDIO_FU_CTRL_MUTE &&
            request->wLength == sizeof(audio_control_cur_1_t)) {
            s_mute[channel] = ((audio_control_cur_1_t const *)buffer)->bCur;
            return true;
        }
        if (request->bControlSelector == AUDIO_FU_CTRL_VOLUME &&
            request->wLength == sizeof(audio_control_cur_2_t)) {
            s_volume[channel] =
                ((audio_control_cur_2_t const *)buffer)->bCur;
            return true;
        }
    }

    ESP_LOGW(TAG,
             "[DS5_AUDIO] set_req unsupported entity=%u selector=%u request=%u",
             request->bEntityID,
             request->bControlSelector,
             request->bRequest);
    return false;
}
#endif

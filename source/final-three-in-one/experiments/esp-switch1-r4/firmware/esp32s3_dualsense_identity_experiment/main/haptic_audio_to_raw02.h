#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "dualsense_haptic_audio.h"
#include "esp_err.h"

#define HAPTIC_RAW02_SIDE_BYTES 16
#define HAPTIC_RAW02_PAYLOAD_BYTES 64
#define HAPTIC_RAW02_HEX_LEFT_RIGHT_LEN 64
#define HAPTIC_RAW02_HEX_FULL_LEN 128
#define HAPTIC_RAW02_HEX_MAX_LEN HAPTIC_RAW02_HEX_FULL_LEN

typedef enum {
    HAPTIC_RAW02_MODE_AUTO = 0,
    HAPTIC_RAW02_MODE_SPECTRAL,
    HAPTIC_RAW02_MODE_TICK,
    HAPTIC_RAW02_MODE_PUNCH,
    HAPTIC_RAW02_MODE_CONTINUOUS,
    HAPTIC_RAW02_MODE_TEXTURE,
    HAPTIC_RAW02_MODE_SILENCE,
} haptic_raw02_mode_t;

typedef enum {
    HAPTIC_RAW02_SOURCE_HD_ONLY = 0,
    HAPTIC_RAW02_SOURCE_PCM,
} haptic_raw02_source_t;

typedef struct {
    bool live_forwarding;
    bool dry_run;
    bool ble_required;
    uint8_t max_intensity;
    float gain;
    float transient_gain;
    uint16_t min_interval_ms;
    uint16_t silence_timeout_ms;
    uint16_t activity_threshold;
    haptic_raw02_mode_t mode;
    haptic_raw02_source_t source;
    uint32_t feature_packets;
    uint32_t active_packets;
    uint32_t silence_packets;
    uint32_t hd_candidate_packets;
    uint32_t raw02_dry_packets;
    uint32_t raw02_live_packets;
    uint32_t dropped_rate;
    uint32_t dropped_no_ble;
    uint32_t dropped_silence;
    uint32_t dropped_pcm;
    uint32_t ble_writes;
    uint32_t ble_errors;
    char last_left_hex[HAPTIC_RAW02_SIDE_BYTES * 2 + 1];
    char last_right_hex[HAPTIC_RAW02_SIDE_BYTES * 2 + 1];
    char last_mode[16];
    char last_error[32];
} haptic_raw02_status_t;

void haptic_audio_to_raw02_init(void);
void haptic_audio_to_raw02_process_features(
    const dualsense_haptic_audio_features_t *features,
    int64_t now_us);
void haptic_audio_to_raw02_note_audio_stopped(int64_t now_us);
void haptic_audio_to_raw02_snapshot(haptic_raw02_status_t *out);
const char *haptic_audio_to_raw02_mode_string(haptic_raw02_mode_t mode);
bool haptic_audio_to_raw02_parse_mode(const char *text, haptic_raw02_mode_t *out_mode);
const char *haptic_audio_to_raw02_source_string(haptic_raw02_source_t source);
bool haptic_audio_to_raw02_parse_source(const char *text, haptic_raw02_source_t *out_source);
void haptic_audio_to_raw02_set_live_forwarding(bool enabled);
void haptic_audio_to_raw02_set_dry_run(bool enabled);
void haptic_audio_to_raw02_set_max_intensity(uint8_t value);
void haptic_audio_to_raw02_set_gain(float value);
void haptic_audio_to_raw02_set_transient_gain(float value);
void haptic_audio_to_raw02_set_min_interval_ms(uint16_t value);
void haptic_audio_to_raw02_set_silence_timeout_ms(uint16_t value);
void haptic_audio_to_raw02_set_activity_threshold(uint16_t value);
void haptic_audio_to_raw02_set_mode(haptic_raw02_mode_t mode);
void haptic_audio_to_raw02_set_source(haptic_raw02_source_t source);
void haptic_audio_to_raw02_defaults(void);
esp_err_t haptic_audio_to_raw02_send_test(const char *name, bool force_live);
esp_err_t haptic_audio_to_raw02_send_raw_hex(const char *hex,
                                             bool force_live,
                                             char *payload_hex,
                                             size_t payload_hex_len);

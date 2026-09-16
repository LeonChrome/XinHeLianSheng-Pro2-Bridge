#pragma once

#include <stdbool.h>
#include <stdint.h>

#define DUALSENSE_HAPTIC_AUDIO_SAMPLE_RATE 48000
#ifndef DS5_AUDIO_CHANNELS
#define DS5_AUDIO_CHANNELS 4
#endif
#if DS5_AUDIO_CHANNELS < 2
#define DUALSENSE_HAPTIC_AUDIO_CHANNELS 4
#else
#define DUALSENSE_HAPTIC_AUDIO_CHANNELS DS5_AUDIO_CHANNELS
#endif
#define DUALSENSE_HAPTIC_AUDIO_BYTES_PER_SAMPLE 2

typedef enum {
    DUALSENSE_HAPTIC_AUDIO_PARSER_REAR = 0,
    DUALSENSE_HAPTIC_AUDIO_PARSER_FRONT = 1,
    DUALSENSE_HAPTIC_AUDIO_PARSER_STRONGEST = 2,
} dualsense_haptic_audio_parser_t;

typedef struct {
    uint32_t submitted_packet_count;
    uint32_t dropped_packet_count;
    uint32_t packet_count;
    uint32_t active_packet_count;
    uint32_t silence_packet_count;
    uint32_t front_active_packet_count;
    uint32_t rear_active_packet_count;
    uint32_t front_only_packet_count;
    uint32_t rear_only_packet_count;
    uint32_t both_active_packet_count;
    uint32_t rear_low_energy_packet_count;
    uint32_t frame_count;
    uint32_t overrun_count;
    uint32_t queue_full_count;
    uint32_t process_batch_count;
    uint32_t process_last_us;
    uint32_t process_max_us;
    uint32_t task_stack_high_watermark_bytes;
    uint16_t last_packet_len;
    uint16_t rms_l;
    uint16_t rms_r;
    uint16_t front_rms_l;
    uint16_t front_rms_r;
    uint16_t peak_l;
    uint16_t peak_r;
    uint16_t front_peak_l;
    uint16_t front_peak_r;
    uint16_t mean_abs_l;
    uint16_t mean_abs_r;
    uint16_t front_mean_abs_l;
    uint16_t front_mean_abs_r;
    uint16_t envelope_l;
    uint16_t envelope_r;
    uint16_t front_envelope_l;
    uint16_t front_envelope_r;
    uint16_t transient_l;
    uint16_t transient_r;
    uint16_t spectral_low_freq_l;
    uint16_t spectral_low_freq_r;
    uint16_t spectral_high_freq_l;
    uint16_t spectral_high_freq_r;
    uint16_t spectral_low_rms_l;
    uint16_t spectral_low_rms_r;
    uint16_t spectral_high_rms_l;
    uint16_t spectral_high_rms_r;
    bool activity;
    bool transient;
    bool hd_candidate;
    bool pcm_like;
    bool spectral_ready;
    bool streaming;
    uint8_t alt_setting;
    uint8_t source_channels;
    uint8_t parser_mode;
    bool selected_front_pair;
    uint8_t queue_depth;
    uint8_t queue_high_watermark;
} dualsense_haptic_audio_features_t;

void dualsense_haptic_audio_init(void);
void dualsense_haptic_audio_set_streaming(bool streaming, uint8_t alt_setting);
bool dualsense_haptic_audio_submit_packet(const uint8_t *data,
                                          uint16_t len,
                                          uint8_t channels,
                                          int64_t now_us);
void dualsense_haptic_audio_process_packet(const uint8_t *data,
                                           uint16_t len,
                                           uint8_t channels,
                                           int64_t now_us);
bool dualsense_haptic_audio_snapshot(dualsense_haptic_audio_features_t *out);
const char *dualsense_haptic_audio_parser_string(dualsense_haptic_audio_parser_t mode);
bool dualsense_haptic_audio_parse_parser(const char *text,
                                         dualsense_haptic_audio_parser_t *out);
void dualsense_haptic_audio_set_parser(dualsense_haptic_audio_parser_t mode);

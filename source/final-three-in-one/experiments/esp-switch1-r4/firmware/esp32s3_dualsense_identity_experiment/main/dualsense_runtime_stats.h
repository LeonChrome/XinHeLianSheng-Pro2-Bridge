#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    bool mounted;
    bool suspended;
    bool configuration_ready;
    uint32_t mount_count;
    uint32_t umount_count;
    uint32_t bus_reset_count;
    uint32_t configuration_reset_count;
    uint32_t suspend_count;
    uint32_t resume_count;
    uint32_t report_sent;
    uint32_t report_completed;
    uint32_t report_failed;
    uint32_t report_submit_failed;
    uint32_t report_xfer_failed;
    uint32_t report_submit_failure_streak;
    uint32_t report_not_ready;
    uint32_t hid_endpoint_kick_count;
    uint32_t usb_recovery_count;
    uint32_t usb_recovery_inhibited_count;
    uint32_t usb_recovery_inhibit_reason;
    uint32_t output_count;
    uint32_t feature_get_count;
    uint32_t uac_out_xfer_success;
    uint32_t uac_out_xfer_errors;
    uint32_t uac_out_rearm_failures;
    uint32_t uac_set_interface_count;
    uint32_t uac_mic_alt1_attempts;
    uint32_t uac_mic_alt1_rejects;
    uint32_t uac_last_xfer_bytes;
    int32_t uac_last_xfer_result;
    uint32_t control_task_stack_high_watermark_bytes;
    uint32_t input_task_stack_high_watermark_bytes;
    uint32_t report_last_gap_us;
    uint32_t report_max_gap_us;
    int64_t last_report_us;
    int64_t first_report_submit_failure_us;
    int64_t last_report_submit_failure_us;
    int64_t last_configuration_reset_us;
    int64_t last_hid_endpoint_kick_us;
    int64_t last_usb_recovery_us;
    int64_t last_usb_recovery_inhibited_us;
    int64_t uac_last_xfer_us;
    int64_t last_output_us;
    int64_t last_usb_event_us;
} dualsense_runtime_stats_t;

void dualsense_runtime_stats_snapshot(dualsense_runtime_stats_t *out);
void dualsense_runtime_usb_configuration_reset(void);
void dualsense_runtime_uac1_note_out_xfer(int32_t result,
                                         uint32_t bytes,
                                         bool rearm_ok);
void dualsense_runtime_uac1_note_set_interface(bool microphone,
                                              uint8_t alt,
                                              bool accepted);

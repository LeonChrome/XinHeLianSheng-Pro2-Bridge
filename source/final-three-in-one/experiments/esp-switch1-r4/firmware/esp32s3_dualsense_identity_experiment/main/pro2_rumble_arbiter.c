#include "pro2_rumble_arbiter.h"

#include <string.h>

void pro2_rumble_arbiter_init(pro2_rumble_arbiter_t *arbiter)
{
    if (arbiter) {
        memset(arbiter, 0, sizeof(*arbiter));
        arbiter->host_mode = PRO2_RUMBLE_HOST_AUDIO_HAPTICS;
    }
}

void pro2_rumble_arbiter_set_host_mode(
    pro2_rumble_arbiter_t *arbiter,
    pro2_rumble_host_mode_t mode)
{
    if (!arbiter) {
        return;
    }

    if (mode == PRO2_RUMBLE_HOST_COMPATIBILITY) {
        arbiter->compatibility_updates++;
    } else {
        mode = PRO2_RUMBLE_HOST_AUDIO_HAPTICS;
        arbiter->audio_haptics_updates++;
    }

    if (arbiter->host_mode != mode) {
        arbiter->host_mode = mode;
        arbiter->host_mode_transitions++;
    }
}

void pro2_rumble_arbiter_update_ordinary(pro2_rumble_arbiter_t *arbiter,
                                         bool active,
                                         int64_t now_us,
                                         uint32_t hold_ms)
{
    if (!arbiter) {
        return;
    }
    arbiter->ordinary_active = active;
    arbiter->ordinary_until_us =
        active ? now_us + (int64_t)hold_ms * 1000LL : 0;
}

void pro2_rumble_arbiter_update_hd(pro2_rumble_arbiter_t *arbiter,
                                   bool active,
                                   int64_t now_us,
                                   uint32_t hold_ms)
{
    if (!arbiter) {
        return;
    }
    arbiter->hd_active = active;
    arbiter->hd_until_us =
        active ? now_us + (int64_t)hold_ms * 1000LL : 0;
    if (active &&
        arbiter->host_mode == PRO2_RUMBLE_HOST_COMPATIBILITY) {
        arbiter->hd_updates_blocked_by_compatibility++;
    }
}

pro2_rumble_arbiter_decision_t pro2_rumble_arbiter_tick(
    pro2_rumble_arbiter_t *arbiter,
    int64_t now_us,
    uint8_t stop_packet_count)
{
    pro2_rumble_arbiter_decision_t decision = {0};
    if (!arbiter) {
        return decision;
    }

    if (arbiter->hd_active && now_us > arbiter->hd_until_us) {
        arbiter->hd_active = false;
        arbiter->hd_until_us = 0;
    }
    if (arbiter->ordinary_active && now_us > arbiter->ordinary_until_us) {
        arbiter->ordinary_active = false;
        arbiter->ordinary_until_us = 0;
    }

    decision.previous_source = arbiter->selected_source;
    if (arbiter->host_mode == PRO2_RUMBLE_HOST_COMPATIBILITY) {
        decision.selected_source =
            arbiter->ordinary_active
                ? PRO2_RUMBLE_SOURCE_ORDINARY
                : PRO2_RUMBLE_SOURCE_NONE;
    } else {
        decision.selected_source =
            arbiter->hd_active
                ? PRO2_RUMBLE_SOURCE_HD
                : PRO2_RUMBLE_SOURCE_NONE;
    }
    decision.source_changed =
        decision.selected_source != decision.previous_source;

    if (decision.source_changed) {
        arbiter->source_transitions++;
        if (decision.selected_source == PRO2_RUMBLE_SOURCE_HD &&
            decision.previous_source == PRO2_RUMBLE_SOURCE_ORDINARY) {
            arbiter->hd_preemptions++;
        } else if (decision.selected_source ==
                       PRO2_RUMBLE_SOURCE_ORDINARY &&
                   decision.previous_source == PRO2_RUMBLE_SOURCE_HD) {
            arbiter->ordinary_fallbacks++;
        }

        if (decision.selected_source == PRO2_RUMBLE_SOURCE_NONE &&
            decision.previous_source != PRO2_RUMBLE_SOURCE_NONE) {
            arbiter->stop_packets_pending = stop_packet_count;
        } else {
            arbiter->stop_packets_pending = 0;
        }
        arbiter->selected_source = decision.selected_source;
    }

    if (decision.selected_source == PRO2_RUMBLE_SOURCE_NONE &&
        arbiter->stop_packets_pending > 0) {
        arbiter->stop_packets_pending--;
        decision.send_stop = true;
    }
    return decision;
}

const char *pro2_rumble_arbiter_host_mode_string(
    pro2_rumble_host_mode_t mode)
{
    return mode == PRO2_RUMBLE_HOST_COMPATIBILITY
               ? "compatibility"
               : "audio_haptics";
}

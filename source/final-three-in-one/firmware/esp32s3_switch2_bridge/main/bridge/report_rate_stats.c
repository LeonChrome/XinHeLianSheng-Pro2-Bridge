#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"
#include "esp_timer.h"
#include "report_rate_stats.h"

static portMUX_TYPE s_lock = portMUX_INITIALIZER_UNLOCKED;
static uint32_t s_sent_total;
static uint32_t s_failed_total;
static uint32_t s_window_sent;
static uint32_t s_actual_millihz;
static uint32_t s_last_gap_us;
static uint32_t s_max_gap_us;
static uint32_t s_window_max_gap_us;
static int64_t s_last_report_us;
static int64_t s_window_start_us;

void report_rate_stats_init(void)
{
    portENTER_CRITICAL(&s_lock);
    s_sent_total = 0;
    s_failed_total = 0;
    s_window_sent = 0;
    s_actual_millihz = 0;
    s_last_gap_us = 0;
    s_max_gap_us = 0;
    s_window_max_gap_us = 0;
    s_last_report_us = 0;
    s_window_start_us = 0;
    portEXIT_CRITICAL(&s_lock);
}

void report_rate_stats_record(bool sent)
{
    int64_t now_us = esp_timer_get_time();

    portENTER_CRITICAL(&s_lock);
    if (s_window_start_us == 0) {
        s_window_start_us = now_us;
    }

    if (s_last_report_us > 0 && now_us > s_last_report_us) {
        uint32_t gap_us = (uint32_t)(now_us - s_last_report_us);
        s_last_gap_us = gap_us;
        if (gap_us > s_window_max_gap_us) {
            s_window_max_gap_us = gap_us;
        }
    }
    s_last_report_us = now_us;

    if (sent) {
        s_sent_total++;
        s_window_sent++;
    } else {
        s_failed_total++;
    }

    int64_t elapsed_us = now_us - s_window_start_us;
    if (elapsed_us >= 1000000LL) {
        s_actual_millihz = (uint32_t)(((uint64_t)s_window_sent * 1000000000ULL + (uint64_t)(elapsed_us / 2)) /
                                      (uint64_t)elapsed_us);
        s_max_gap_us = s_window_max_gap_us;
        s_window_sent = 0;
        s_window_max_gap_us = 0;
        s_window_start_us = now_us;
    }
    portEXIT_CRITICAL(&s_lock);
}

void report_rate_stats_get(report_rate_stats_snapshot_t *out)
{
    if (!out) {
        return;
    }

    portENTER_CRITICAL(&s_lock);
    out->sent_total = s_sent_total;
    out->failed_total = s_failed_total;
    out->actual_millihz = s_actual_millihz;
    out->last_gap_us = s_last_gap_us;
    out->max_gap_us = s_max_gap_us;
    portEXIT_CRITICAL(&s_lock);
}

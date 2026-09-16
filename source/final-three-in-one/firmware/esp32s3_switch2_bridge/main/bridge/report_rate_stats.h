#pragma once

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    uint32_t sent_total;
    uint32_t failed_total;
    uint32_t actual_millihz;
    uint32_t last_gap_us;
    uint32_t max_gap_us;
} report_rate_stats_snapshot_t;

void report_rate_stats_init(void);
void report_rate_stats_record(bool sent);
void report_rate_stats_get(report_rate_stats_snapshot_t *out);

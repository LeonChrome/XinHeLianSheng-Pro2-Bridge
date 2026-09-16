#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "dualsense_rumble_intent.h"
#include "pro2_rumble_arbiter.h"

static void expect_source(pro2_rumble_arbiter_decision_t decision,
                          pro2_rumble_source_t source,
                          bool send_stop)
{
    assert(decision.selected_source == source);
    assert(decision.send_stop == send_stop);
}

static void test_dualsense_intent_parser(void)
{
    uint8_t payload[47] = {0};
    dualsense_rumble_intent_t intent;

    payload[0] = 0x03;
    payload[2] = 40;
    payload[3] = 80;
    assert(dualsense_rumble_intent_parse(
        payload, sizeof(payload), &intent));
    assert(intent.compatibility_selected);
    assert(intent.compatibility_v1);
    assert(!intent.compatibility_v2);
    assert(!intent.audio_haptics_allowed);
    assert(intent.ordinary_valid);
    assert(intent.ordinary_active);

    memset(payload, 0, sizeof(payload));
    payload[0] = 0x02;
    assert(dualsense_rumble_intent_parse(
        payload, sizeof(payload), &intent));
    assert(intent.compatibility_selected);
    assert(!intent.ordinary_valid);
    assert(!intent.ordinary_active);

    memset(payload, 0, sizeof(payload));
    payload[0] = 0x02;
    payload[2] = 15;
    payload[3] = 25;
    payload[38] = 0x04;
    assert(dualsense_rumble_intent_parse(
        payload, sizeof(payload), &intent));
    assert(intent.compatibility_selected);
    assert(!intent.compatibility_v1);
    assert(intent.compatibility_v2);
    assert(intent.ordinary_active);

    memset(payload, 0, sizeof(payload));
    payload[1] = 0x40;
    payload[2] = 90;
    payload[3] = 100;
    assert(dualsense_rumble_intent_parse(
        payload, sizeof(payload), &intent));
    assert(!intent.compatibility_selected);
    assert(intent.audio_haptics_allowed);
    assert(!intent.ordinary_valid);
    assert(!intent.ordinary_active);

    assert(!dualsense_rumble_intent_parse(payload, 3, &intent));
}

static void test_audio_haptics_is_default_route(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    assert(arbiter.host_mode == PRO2_RUMBLE_HOST_AUDIO_HAPTICS);
    pro2_rumble_arbiter_update_hd(&arbiter, true, 1000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 1000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);
}

static void test_compatibility_mode_blocks_hd_and_selects_ordinary(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    pro2_rumble_arbiter_update_hd(&arbiter, true, 1000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 1000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);

    pro2_rumble_arbiter_set_host_mode(
        &arbiter, PRO2_RUMBLE_HOST_COMPATIBILITY);
    pro2_rumble_arbiter_update_ordinary(&arbiter, true, 2000, 250);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2000, 3),
                  PRO2_RUMBLE_SOURCE_ORDINARY,
                  false);
    assert(arbiter.ordinary_fallbacks == 1);

    pro2_rumble_arbiter_update_hd(&arbiter, true, 3000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 3000, 3),
                  PRO2_RUMBLE_SOURCE_ORDINARY,
                  false);
    assert(arbiter.hd_updates_blocked_by_compatibility == 1);
}

static void test_audio_release_restores_recent_hd_without_ordinary_leak(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    pro2_rumble_arbiter_set_host_mode(
        &arbiter, PRO2_RUMBLE_HOST_COMPATIBILITY);
    pro2_rumble_arbiter_update_ordinary(&arbiter, true, 1000, 250);
    pro2_rumble_arbiter_update_hd(&arbiter, true, 2000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2000, 3),
                  PRO2_RUMBLE_SOURCE_ORDINARY,
                  false);

    pro2_rumble_arbiter_set_host_mode(
        &arbiter, PRO2_RUMBLE_HOST_AUDIO_HAPTICS);
    pro2_rumble_arbiter_update_ordinary(&arbiter, false, 3000, 250);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 3000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);
    assert(arbiter.hd_preemptions == 1);

    pro2_rumble_arbiter_update_hd(&arbiter, false, 4000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 4000, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
}

static void test_compatibility_guard_with_zero_motors_is_silent(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    pro2_rumble_arbiter_update_hd(&arbiter, true, 1000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 1000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);

    pro2_rumble_arbiter_set_host_mode(
        &arbiter, PRO2_RUMBLE_HOST_COMPATIBILITY);
    pro2_rumble_arbiter_update_ordinary(&arbiter, false, 2000, 250);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2000, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
}

static void test_stop_packets_are_bounded(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    pro2_rumble_arbiter_update_hd(&arbiter, true, 1000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 1000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);
    pro2_rumble_arbiter_update_hd(&arbiter, false, 2000, 120);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2000, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2010, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2020, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2030, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  false);
}

static void test_expiry_boundary_is_inclusive(void)
{
    pro2_rumble_arbiter_t arbiter;
    pro2_rumble_arbiter_init(&arbiter);

    pro2_rumble_arbiter_update_hd(&arbiter, true, 1000, 1);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2000, 3),
                  PRO2_RUMBLE_SOURCE_HD,
                  false);
    expect_source(pro2_rumble_arbiter_tick(&arbiter, 2001, 3),
                  PRO2_RUMBLE_SOURCE_NONE,
                  true);
}

int main(void)
{
    test_dualsense_intent_parser();
    test_audio_haptics_is_default_route();
    test_compatibility_mode_blocks_hd_and_selects_ordinary();
    test_audio_release_restores_recent_hd_without_ordinary_leak();
    test_compatibility_guard_with_zero_motors_is_silent();
    test_stop_packets_are_bounded();
    test_expiry_boundary_is_inclusive();
    puts("pro2_rumble_arbiter_test: passed");
    return 0;
}

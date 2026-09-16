#include "control_panel.hpp"

#include <array>
#include <cstdio>
#include <utility>

#include "bridge_mode.hpp"
#include "hardware/adc.h"
#include "mode_manager.hpp"
#include "pairing_feedback.hpp"
#include "pico/cyw43_arch.h"
#include "pico/stdlib.h"
#include "wireless_backend.hpp"

namespace control_panel {
namespace {

constexpr uint kButtonAdcGpio = 27;
constexpr uint kButtonAdcInput = 1;
constexpr uint64_t kDebounceUs = 30000;
constexpr uint64_t kReleaseArmUs = 150000;
constexpr uint64_t kPairHoldUs = 1500000;
constexpr uint64_t kPairRebootDelayUs = 2500000;

enum class Key : uint8_t {
    None,
    Pair,
    Ps5,
    Xbox,
    Edge,
    Pro2,
    Pro1,
};

Key g_candidate = Key::None;
Key g_stable = Key::None;
uint64_t g_candidate_since_us = 0;
uint64_t g_pair_pressed_at_us = 0;
uint64_t g_pair_reboot_at_us = 0;
bool g_pair_latched = false;
bool g_require_release = true;
bool g_input_armed = false;
uint64_t g_release_since_us = 0;
uint64_t g_led_tick_us = 0;
uint8_t g_led_phase = 0;

Key decode(uint16_t sample) {
    // Midpoints between the six measured/theoretical ladder codes:
    // 372, 738, 1310, 2048, 2816, 3375, and released 4095.
    if (sample < 555) {
        return Key::Pair;
    }
    if (sample < 1024) {
        return Key::Ps5;
    }
    if (sample < 1679) {
        return Key::Xbox;
    }
    if (sample < 2432) {
        return Key::Edge;
    }
    if (sample < 3096) {
        return Key::Pro2;
    }
    if (sample < 3735) {
        return Key::Pro1;
    }
    return Key::None;
}

uint16_t read_average() {
    uint32_t sum = 0;
    for (unsigned index = 0; index < 8; ++index) {
        sum += adc_read();
    }
    return static_cast<uint16_t>((sum + 4u) / 8u);
}

const char *key_name(Key key) {
    switch (key) {
    case Key::Pair:
        return "pair";
    case Key::Ps5:
        return "ps5";
    case Key::Xbox:
        return "xbox";
    case Key::Edge:
        return "edge";
    case Key::Pro2:
        return "pro2_quiet";
    case Key::Pro1:
        return "switch1";
    case Key::None:
        return "none";
    }
    return "unknown";
}

bool key_mode(Key key, bridge::Mode *mode) {
    if (mode == nullptr) {
        return false;
    }
    switch (key) {
    case Key::Ps5:
        *mode = bridge::Mode::DualSense;
        return true;
    case Key::Xbox:
        *mode = bridge::Mode::XboxBle;
        return true;
    case Key::Edge:
        *mode = bridge::Mode::DualSenseEdge;
        return true;
    case Key::Pro2:
        *mode = bridge::Mode::Pro2Quiet;
        return true;
    case Key::Pro1:
        *mode = bridge::Mode::SwitchProClassic;
        return true;
    case Key::Pair:
    case Key::None:
        return false;
    }
    return false;
}

uint8_t mode_pulses(bridge::Mode mode) {
    switch (mode) {
    case bridge::Mode::DualSense:
        return 1;
    case bridge::Mode::XboxBle:
        return 2;
    case bridge::Mode::DualSenseEdge:
        return 3;
    case bridge::Mode::Pro2Quiet:
        return 4;
    case bridge::Mode::SwitchProClassic:
        return 5;
    }
    return 1;
}

void update_led(uint64_t now) {
    if (now - g_led_tick_us < 120000) {
        return;
    }
    g_led_tick_us = now;
    g_led_phase = static_cast<uint8_t>((g_led_phase + 1u) % 16u);

    bool on = false;
    if (g_pair_reboot_at_us != 0 || g_stable == Key::Pair) {
        on = (g_led_phase & 1u) == 0;
    } else {
        const uint8_t pulses = mode_pulses(mode_manager::current());
        on = g_led_phase < static_cast<uint8_t>(pulses * 2u) &&
             (g_led_phase & 1u) == 0;
    }
    cyw43_arch_gpio_put(CYW43_WL_GPIO_LED_PIN, on ? 1 : 0);
}

void handle_press(Key key, uint16_t adc_raw, uint64_t now) {
    std::printf("[CONTROL_PANEL] key=%s adc=%u stable_ms=%llu\n",
                key_name(key), adc_raw,
                static_cast<unsigned long long>(kDebounceUs / 1000));
    if (key == Key::Pair) {
        g_pair_pressed_at_us = now;
        g_pair_latched = false;
        return;
    }

    bridge::Mode target = bridge::kDefaultMode;
    if (key_mode(key, &target)) {
        mode_manager::set_and_reboot(target);
    }
}

} // namespace

void init() {
    adc_init();
    adc_gpio_init(kButtonAdcGpio);
    // A bare Pico has no external 10k ladder pull-up. Keep GP27 at the
    // released code so an unpopulated control panel cannot cause a reboot
    // loop by being decoded as a held mode key.
    gpio_pull_up(kButtonAdcGpio);
    adc_select_input(kButtonAdcInput);
    g_candidate = Key::None;
    g_stable = Key::None;
    g_candidate_since_us = time_us_64();
    g_require_release = true;
    g_input_armed = false;
    g_release_since_us = 0;
    std::printf(
        "[CONTROL_PANEL] GP27/ADC1 six-key ladder ready "
        "keys=pair,ps5,xbox,edge,pro2_quiet,switch1 pair_hold_ms=%llu "
        "startup_guard=wait_for_release\n",
        static_cast<unsigned long long>(kPairHoldUs / 1000));
}

void task() {
    const uint64_t now = time_us_64();
    const uint16_t adc_raw = read_average();
    const Key decoded = decode(adc_raw);

    if (!g_input_armed) {
        if (decoded == Key::None) {
            if (g_release_since_us == 0) {
                g_release_since_us = now;
            } else if (now - g_release_since_us >= kReleaseArmUs) {
                g_input_armed = true;
                g_require_release = false;
                g_candidate = Key::None;
                g_stable = Key::None;
                g_candidate_since_us = now;
                std::printf(
                    "[CONTROL_PANEL] input armed adc=%u "
                    "released_stable_ms=%llu\n",
                    adc_raw,
                    static_cast<unsigned long long>(kReleaseArmUs / 1000));
            }
        } else {
            g_release_since_us = 0;
        }
        update_led(now);
        return;
    }

    if (decoded != g_candidate) {
        g_candidate = decoded;
        g_candidate_since_us = now;
    } else if (decoded != g_stable && now - g_candidate_since_us >= kDebounceUs) {
        g_stable = decoded;
        if (g_stable == Key::None) {
            g_require_release = false;
            g_pair_pressed_at_us = 0;
            g_pair_latched = false;
        } else if (!g_require_release) {
            g_require_release = true;
            handle_press(g_stable, adc_raw, now);
        }
    }

    if (g_stable == Key::Pair && g_pair_pressed_at_us != 0 &&
        !g_pair_latched && now - g_pair_pressed_at_us >= kPairHoldUs) {
        g_pair_latched = true;
        std::printf(
            "[CONTROL_PANEL] pair hold accepted; clearing bonds and rebooting\n");
        pairing_feedback::start("physical_pair_key");
        wireless_backend::forget_bonds();
        g_pair_reboot_at_us = now + kPairRebootDelayUs;
    }

    update_led(now);

    if (g_pair_reboot_at_us != 0 && now >= g_pair_reboot_at_us) {
        mode_manager::set_and_reboot(mode_manager::current());
    }
}

bool self_test() {
    const std::array<std::pair<uint16_t, Key>, 13> cases{{
        {372, Key::Pair}, {738, Key::Ps5}, {1310, Key::Xbox},
        {2048, Key::Edge}, {2816, Key::Pro2}, {3375, Key::Pro1},
        {4095, Key::None}, {554, Key::Pair}, {555, Key::Ps5},
        {1024, Key::Xbox}, {1679, Key::Edge}, {2432, Key::Pro2},
        {3735, Key::None},
    }};
    for (const auto &[sample, expected] : cases) {
        if (decode(sample) != expected) {
            return false;
        }
    }
    bridge::Mode mode = bridge::kDefaultMode;
    return key_mode(Key::Ps5, &mode) && mode == bridge::Mode::DualSense &&
           key_mode(Key::Xbox, &mode) && mode == bridge::Mode::XboxBle &&
           key_mode(Key::Edge, &mode) && mode == bridge::Mode::DualSenseEdge &&
           key_mode(Key::Pro2, &mode) && mode == bridge::Mode::Pro2Quiet &&
           key_mode(Key::Pro1, &mode) && mode == bridge::Mode::SwitchProClassic;
}

} // namespace control_panel

#include <cstdio>

#include "bsp/board_api.h"
#include "bridge_mode.hpp"
#include "bridge_state.hpp"
#include "control_panel.hpp"
#include "hardware/clocks.h"
#include "hardware/gpio.h"
#include "imu_converter.hpp"
#include "mode_manager.hpp"
#include "pairing_feedback.hpp"
#include "pico/cyw43_arch.h"
#include "pico/stdlib.h"
#include "pro2_host.hpp"
#include "pro2_protocol.hpp"
#include "pro2_rumble.hpp"
#include "tusb.h"
#include "usb_cdc_log.hpp"
#include "wireless_backend.hpp"

#ifndef XHLS_FIRMWARE_REVISION
#define XHLS_FIRMWARE_REVISION "unknown"
#endif

namespace {

constexpr uint32_t kPioUsbSystemClockKhz = 120000;
constexpr uint kHostEnableGpio = 3;
#ifdef XHLS_CLASSIC_BT_ONLY_DIAGNOSTIC
constexpr bool kClassicBtOnlyDiagnostic = true;
#else
constexpr bool kClassicBtOnlyDiagnostic = false;
#endif
constexpr uint64_t kSyntheticInputPeriodUs = 4000;
// A battery-powered mode switch has no CDC host. Do not hold the entire
// controller startup for 1.5 seconds just to wait for an optional log port.
constexpr uint32_t kNativeUsbBringupWindowMs = 250;

void service_cyw43_if_polled() {
#if PICO_CYW43_ARCH_POLL
    cyw43_arch_poll();
#endif
}

void init_native_usb() {
    tusb_rhport_init_t device_init{
        TUSB_ROLE_DEVICE,
        TUSB_SPEED_AUTO,
    };
    tusb_init(BOARD_TUD_RHPORT, &device_init);

    if (board_init_after_tusb) {
        board_init_after_tusb();
    }
}

void init_host_usb() {
    tusb_rhport_init_t host_init{
        TUSB_ROLE_HOST,
        TUSB_SPEED_AUTO,
    };
    tusb_init(BOARD_TUH_RHPORT, &host_init);
}

void service_debug_usb_for_ms(uint32_t duration_ms, bool host_initialized) {
    const uint64_t deadline_us =
        time_us_64() + static_cast<uint64_t>(duration_ms) * 1000u;
    do {
        tud_task();
        if (host_initialized) {
            tuh_task();
        }
        usb_cdc_log::task();
        sleep_us(250);
    } while (time_us_64() < deadline_us);
}

void wait_for_debug_usb_or_timeout(uint32_t timeout_ms) {
    const uint64_t deadline_us =
        time_us_64() + static_cast<uint64_t>(timeout_ms) * 1000u;
    while (!tud_cdc_connected() && time_us_64() < deadline_us) {
        tud_task();
        usb_cdc_log::task();
        sleep_us(250);
    }
    service_debug_usb_for_ms(100, false);
}

void boot_stage(const char *stage, bool host_initialized) {
    std::printf("[BOOT_STAGE] %s\n", stage);
    service_debug_usb_for_ms(20, host_initialized);
}

} // namespace

int main() {
    // Pico-PIO-USB generates its USB timing from clk_sys. Keep it on an exact
    // multiple of 12 MHz before either TinyUSB root port is initialized.
    set_sys_clock_khz(kPioUsbSystemClockKhz, true);
    board_init();
    bridge::state_init();

    // Bring the native diagnostic port up before reading mode storage,
    // starting PIO USB host, or initializing CYW43/BTstack. A later startup
    // failure remains observable instead of looking like a dead board.
    init_native_usb();
    usb_cdc_log::init();
    std::printf(
        "\n[XHLS_BOOT] Pico 2 W Pro2 wireless bridge Rev B "
        XHLS_FIRMWARE_REVISION " "
        "sdk=2.3.0\n");
    boot_stage("native_usb_ready", false);
    wait_for_debug_usb_or_timeout(kNativeUsbBringupWindowMs);

    mode_manager::init();
    const bool bridge_active =
        mode_manager::current() != bridge::Mode::Pro2Quiet;
    const bool host_usb_active =
        bridge_active && !kClassicBtOnlyDiagnostic;

    // Default low prevents a boot transient from powering the Pro2 port.  In
    // quiet mode GP0/GP1 are also left unclaimed because the PIO host root port
    // is never initialized.
    gpio_init(kHostEnableGpio);
    gpio_set_dir(kHostEnableGpio, GPIO_OUT);
    gpio_put(kHostEnableGpio, host_usb_active ? 1 : 0);

    boot_stage("mode_and_host_power_ready", false);
    if (host_usb_active) {
        // Prepare the Pro2 state machine before TinyUSB can dispatch mount and
        // interface callbacks. Initializing it after init_host_usb() can erase
        // a fast device enumeration while CYW43 is still starting.
        pro2_host::init();
        boot_stage("pro2_state_ready_before_pio_usb", false);
        boot_stage("pio_usb_host_init_begin", false);
        init_host_usb();
        boot_stage("pio_usb_host_init_done", true);
    }
    std::printf(
        "[XHLS_BOOT] mode=%s\n",
        bridge::mode_name(mode_manager::current()));
    std::printf(
        "[USB_ROLE] native_micro_usb=CDC_debug pio_usb_host=%s "
        "host_5v=%s bt_only_diagnostic=%u cyw43_arch=%s clk_sys_hz=%lu "
        "timing_requirement=multiple_of_12MHz\n",
        host_usb_active ? "GP0_D+_GP1_D-" : "disabled_high_z",
        host_usb_active ? "enabled" : "disabled",
        kClassicBtOnlyDiagnostic,
#if PICO_CYW43_ARCH_THREADSAFE_BACKGROUND
        "threadsafe_background",
#else
        "poll",
#endif
        static_cast<unsigned long>(clock_get_hz(clk_sys)));

    boot_stage("self_test_begin", host_usb_active);
    const bool parser_ok = pro2::self_test();
    const bool mapper_ok = wireless_backend::self_test();
    const bool rumble_ok = pro2_rumble::self_test();
    const bool imu_ok = imu::self_test();
    const bool mode_ok = mode_manager::self_test();
    const bool panel_ok = control_panel::self_test();
    std::printf(
                "[SELF_TEST] pro2_parser=%s wireless_mapper=%s "
                "pro2_rumble=%s imu=%s mode_manager=%s six_key_panel=%s\n",
                parser_ok ? "pass" : "fail",
                mapper_ok ? "pass" : "fail",
                rumble_ok ? "pass" : "fail",
                imu_ok ? "pass" : "fail",
                mode_ok ? "pass" : "fail",
                panel_ok ? "pass" : "fail");
    boot_stage("self_test_done", host_usb_active);
    if (!parser_ok || !mapper_ok || !rumble_ok || !imu_ok || !mode_ok ||
        !panel_ok) {
        std::printf("[FATAL] self-test failed; bridge output is disabled\n");
        while (true) {
            tud_task();
            if (host_usb_active) {
                tuh_task();
            }
            usb_cdc_log::task();
            tight_loop_contents();
        }
    }

    boot_stage("cyw43_init_begin", host_usb_active);
    const int cyw43_result = cyw43_arch_init();
    if (cyw43_result != 0) {
        std::printf("[FATAL] cyw43_arch_init failed result=%d\n", cyw43_result);
        while (true) {
            tud_task();
            if (host_usb_active) {
                tuh_task();
            }
            usb_cdc_log::task();
            tight_loop_contents();
        }
    }
    boot_stage("cyw43_init_done", host_usb_active);
    control_panel::init();
    boot_stage("control_panel_ready", host_usb_active);

    if (!bridge_active) {
        usb_cdc_log::set_runtime_ready(true);
        std::printf(
            "[READY] pro2_quiet: Pro2 host 5V off, GP0/GP1 high-Z, "
            "wireless HID advertising off; press a mode key to resume\n");
        while (true) {
            tud_task();
            usb_cdc_log::task();
            service_cyw43_if_polled();
            control_panel::task();
            mode_manager::task();
            tight_loop_contents();
        }
    }

    boot_stage(host_usb_active ? "pro2_usb_host_ready"
                               : "bt_only_synthetic_source_ready",
               host_usb_active);

    wireless_backend::configure_address(mode_manager::current());

    boot_stage("wireless_backend_init_begin", host_usb_active);
    if (!wireless_backend::init(mode_manager::current())) {
        std::printf("[FATAL] wireless backend init failed mode=%s\n",
                    bridge::mode_name(mode_manager::current()));
        while (true) {
            tud_task();
            if (host_usb_active) {
                tuh_task();
            }
            usb_cdc_log::task();
            tight_loop_contents();
        }
    }
    boot_stage("wireless_backend_init_done", host_usb_active);
    usb_cdc_log::set_runtime_ready(true);
    std::printf(
        "[READY] connect Pro2 by USB host; wireless_identity=\"%s\" "
            "ordinary_rumble=on hd_haptics=off mode_button=GP2_hold_1.5s "
            "controller_chord=GL+GR+ZL+ZR+(A/B/X/Y)_hold_1.5s "
            "pair_chord=GL+GR+ZL+ZR+Plus+Minus_hold_2s "
            "panel=pair,ps5,xbox,edge,pro2,switch1\n",
        bridge::mode_display_name(mode_manager::current()));

    uint64_t next_synthetic_input_us = time_us_64();
    uint32_t synthetic_sequence = 0;
    while (true) {
        tud_task();
        if (host_usb_active) {
            tuh_task();
        }
        usb_cdc_log::task();
        service_cyw43_if_polled();
        if (host_usb_active) {
            pro2_host::task();
        } else if (kClassicBtOnlyDiagnostic) {
            const uint64_t now = time_us_64();
            if (now >= next_synthetic_input_us) {
                pro2::InputState synthetic{};
                synthetic.sequence = ++synthetic_sequence;
                synthetic.received_at_us = now;
                bridge::publish_input(synthetic);
                do {
                    next_synthetic_input_us += kSyntheticInputPeriodUs;
                } while (next_synthetic_input_us <= now);
            }
        }
        wireless_backend::task();
        pairing_feedback::task();
        control_panel::task();
        mode_manager::task();
        tight_loop_contents();
    }
}

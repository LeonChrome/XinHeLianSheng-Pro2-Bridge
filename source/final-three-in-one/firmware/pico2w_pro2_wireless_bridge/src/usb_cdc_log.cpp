#include "usb_cdc_log.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>

#include "pico/bootrom.h"
#include "pico/time.h"
#include "pico/stdio/driver.h"
#include "bridge_state.hpp"
#include "imu_converter.hpp"
#include "mode_manager.hpp"
#include "pro2_host.hpp"
#include "switch1_protocol.hpp"
#include "tusb.h"
#include "wireless_backend.hpp"

#ifndef XHLS_FIRMWARE_REVISION
#define XHLS_FIRMWARE_REVISION "unknown"
#endif

#ifndef XHLS_FIRMWARE_VERSION
#define XHLS_FIRMWARE_VERSION "unknown"
#endif

namespace {

constexpr size_t kBacklogCapacity = 8192;
constexpr size_t kCommandCapacity = 64;

std::array<uint8_t, kBacklogCapacity> backlog{};
size_t backlog_size = 0;
size_t backlog_offset = 0;
std::array<char, kCommandCapacity> command{};
size_t command_length = 0;
bool runtime_ready = false;

void append_backlog(const char *buffer, size_t length) {
    if (backlog_offset == backlog_size) {
        backlog_offset = 0;
        backlog_size = 0;
    }
    const size_t available = backlog.size() - backlog_size;
    const size_t copy_length = std::min(length, available);
    if (copy_length > 0) {
        std::memcpy(backlog.data() + backlog_size, buffer, copy_length);
        backlog_size += copy_length;
    }
}

void output_chars(const char *buffer, int length) {
    if (buffer == nullptr || length <= 0) {
        return;
    }

    if (!tud_cdc_connected() || backlog_offset < backlog_size) {
        append_backlog(buffer, static_cast<size_t>(length));
        return;
    }

    int offset = 0;
    while (offset < length) {
        const uint32_t available = tud_cdc_write_available();
        if (available == 0) {
            break;
        }
        const uint32_t chunk = std::min<uint32_t>(
            available, static_cast<uint32_t>(length - offset));
        const uint32_t written = tud_cdc_write(buffer + offset, chunk);
        if (written == 0) {
            break;
        }
        offset += static_cast<int>(written);
    }
    tud_cdc_write_flush();
}

void output_flush() {
    if (tud_cdc_connected()) {
        tud_cdc_write_flush();
    }
}

stdio_driver_t cdc_stdio_driver{};

void flush_backlog() {
    if (!tud_cdc_connected()) {
        return;
    }
    while (backlog_offset < backlog_size) {
        const uint32_t available = tud_cdc_write_available();
        if (available == 0) {
            break;
        }
        const uint32_t chunk = std::min<uint32_t>(
            available, static_cast<uint32_t>(backlog_size - backlog_offset));
        const uint32_t written =
            tud_cdc_write(backlog.data() + backlog_offset, chunk);
        if (written == 0) {
            break;
        }
        backlog_offset += written;
    }
    tud_cdc_write_flush();
    if (backlog_offset == backlog_size) {
        backlog_offset = 0;
        backlog_size = 0;
    }
}

void execute_command() {
    command[command_length] = '\0';
    if (std::strcmp(command.data(), "help") == 0) {
        std::printf(
            "[CDC_CMD] commands: help | version | mode | "
            "mode xbox|ps5|edge|switch1|pro2 "
            "| wireless status | wireless forget | imu status | imu calibrate | "
            "input status | rumble status | rumble test | "
            "rumble test switch | rumble stop "
            "| bootsel\n");
    } else if (std::strcmp(command.data(), "version") == 0) {
        std::printf(
            "[CDC_CMD] firmware_revision=%s firmware_version=%s "
            "board=pico2_w target=rp2350 runtime_ready=%s\n",
            XHLS_FIRMWARE_REVISION, XHLS_FIRMWARE_VERSION,
            runtime_ready ? "true" : "false");
    } else if (std::strcmp(command.data(), "mode") == 0) {
        std::printf("[CDC_CMD] mode=%s display=\"%s\"\n",
                    bridge::mode_name(mode_manager::current()),
                    bridge::mode_display_name(mode_manager::current()));
    } else if (std::strncmp(command.data(), "mode ", 5) == 0) {
        bridge::Mode mode;
        if (mode_manager::parse(command.data() + 5, &mode)) {
            mode_manager::set_and_reboot(mode);
        } else {
            std::printf(
                "[CDC_CMD] invalid mode; use xbox|ps5|edge|switch1|pro2\n");
        }
    } else if (std::strcmp(command.data(), "wireless forget") == 0 ||
               std::strcmp(command.data(), "ble forget") == 0) {
        std::printf("[CDC_CMD] wireless forget requested\n");
        wireless_backend::forget_bonds();
    } else if (std::strcmp(command.data(), "wireless status") == 0) {
        const bridge::Mode mode = mode_manager::current();
        std::printf(
            "[CDC_CMD] wireless mode=%s backend=%s connected=%u "
            "max_active_hosts=1 simultaneous_multi_mode=off\n",
            bridge::mode_name(mode),
            mode == bridge::Mode::XboxBle ? "ble_hids" :
            mode == bridge::Mode::Pro2Quiet ? "off" : "classic_hid",
            wireless_backend::connected());
        wireless_backend::print_status();
    } else if (std::strcmp(command.data(), "imu status") == 0) {
        const imu::BiasStatus status = imu::bias_status();
        std::printf(
            "[CDC_CMD] imu calibrated=%u samples=%lu "
            "bias_raw=%.3f,%.3f,%.3f\n",
            status.calibrated,
            static_cast<unsigned long>(status.samples),
            status.gyro_x, status.gyro_y, status.gyro_z);
        pro2::InputState input;
        uint32_t generation = 0;
        if (bridge::read_latest_input(&input, &generation)) {
            const imu::RawAxes dualsense = imu::to_dualsense(input);
            const uint64_t now = time_us_64();
            const double age_ms = now >= input.received_at_us
                                      ? (now - input.received_at_us) / 1000.0
                                      : 0.0;
            std::printf(
                "[CDC_CMD] imu source=live generation=%lu age_ms=%.2f "
                "pro2_accel_raw=%d,%d,%d pro2_gyro_raw=%d,%d,%d "
                "dualsense_accel_raw=%d,%d,%d dualsense_gyro_raw=%d,%d,%d\n",
                static_cast<unsigned long>(generation), age_ms,
                input.accel_x, input.accel_y, input.accel_z,
                input.gyro_x, input.gyro_y, input.gyro_z,
                dualsense.accel_x, dualsense.accel_y, dualsense.accel_z,
                dualsense.gyro_x, dualsense.gyro_y, dualsense.gyro_z);
        } else {
            std::printf("[CDC_CMD] imu source=offline\n");
        }
    } else if (std::strcmp(command.data(), "imu calibrate") == 0) {
        imu::reset_bias();
        std::printf(
            "[CDC_CMD] imu bias reset; keep controller still for 1 second\n");
    } else if (std::strcmp(command.data(), "input status") == 0 ||
               std::strcmp(command.data(), "stick status") == 0) {
        pro2::InputState input;
        uint32_t generation = 0;
        if (bridge::read_latest_input(&input, &generation)) {
            std::printf(
                "[CDC_CMD] input generation=%lu buttons=%08lx "
                "stick_raw={lx:%u,ly:%u,rx:%u,ry:%u} "
                "stick_normalized={lx:%.4f,ly:%.4f,rx:%.4f,ry:%.4f} "
                "stick_raw12_out={lx:%u,ly:%u,rx:%u,ry:%u} "
                "center=%u deadzone=%u full_scale_range=%u\n",
                static_cast<unsigned long>(generation),
                static_cast<unsigned long>(input.buttons),
                input.left_x, input.left_y, input.right_x, input.right_y,
                pro2::normalize_stick(input.left_x),
                pro2::normalize_stick(input.left_y),
                pro2::normalize_stick(input.right_x),
                pro2::normalize_stick(input.right_y),
                pro2::normalize_stick_to_raw12(input.left_x),
                pro2::normalize_stick_to_raw12(input.left_y),
                pro2::normalize_stick_to_raw12(input.right_x),
                pro2::normalize_stick_to_raw12(input.right_y),
                pro2::kStickCenter, pro2::kStickDeadzone,
                pro2::kStickFullScaleRange);
        } else {
            std::printf("[CDC_CMD] input source=offline\n");
        }
        const pro2_host::Telemetry usb = pro2_host::telemetry();
        std::printf(
            "[CDC_CMD] usb_source connected=%u reports=%u phase=%s "
            "incidents=%lu aborts=%lu rearms=%lu bus_resets=%lu recovered=%lu\n",
            usb.device_connected, usb.reports_running,
            usb.source_recovery_phase,
            static_cast<unsigned long>(usb.source_recovery_incidents),
            static_cast<unsigned long>(usb.source_recovery_aborts),
            static_cast<unsigned long>(usb.source_recovery_rearms),
            static_cast<unsigned long>(usb.source_recovery_bus_resets),
            static_cast<unsigned long>(usb.source_recoveries));
    } else if (std::strcmp(command.data(), "rumble status") == 0) {
        const pro2_host::Telemetry status = pro2_host::telemetry();
        std::printf(
            "[CDC_CMD] rumble active=%u pending=%u weak=%u strong=%u "
            "updates=%lu reports=%lu submit_errors=%lu transfer_errors=%lu\n",
            status.rumble_active, status.rumble_transfer_pending,
            status.rumble_weak, status.rumble_strong,
            static_cast<unsigned long>(status.rumble_updates),
            static_cast<unsigned long>(status.rumble_reports),
            static_cast<unsigned long>(status.rumble_submit_errors),
            static_cast<unsigned long>(status.rumble_transfer_errors));
    } else if (std::strcmp(command.data(), "rumble test") == 0) {
        pro2_rumble::XboxRumbleCommand rumble;
        rumble.enabled_mask = 0x0f;
        rumble.strong = 96;
        rumble.weak = 64;
        std::printf(
            "[CDC_CMD] rumble test requested strong=96 weak=64 duration_ms=700\n");
        pro2_host::pulse_rumble(rumble, 700);
    } else if (std::strcmp(command.data(), "rumble test switch") == 0) {
        const uint8_t body[] = {
            0x00, 0x00, 0xc8, 0x72, 0x00,
            0x00, 0xc8, 0x72, 0x00};
        const auto rumble = switch1::decode_rumble(body, sizeof(body));
        std::printf(
            "[CDC_CMD] switch rumble path test requested "
            "decoded_left=%u decoded_right=%u active=%u\n",
            rumble.left_trigger, rumble.right_trigger,
            pro2_rumble::active(rumble));
        pro2_host::pulse_rumble(rumble, 700);
    } else if (std::strcmp(command.data(), "rumble stop") == 0) {
        std::printf("[CDC_CMD] rumble stop requested\n");
        pro2_host::stop_rumble();
    } else if (std::strcmp(command.data(), "bootsel") == 0) {
        std::printf("[CDC_CMD] rebooting to BOOTSEL\n");
        tud_cdc_write_flush();
        sleep_ms(50);
        reset_usb_boot(0, 0);
    } else if (command_length != 0) {
        std::printf("[CDC_CMD] unknown=\"%s\"; type help\n", command.data());
    }
    command_length = 0;
}

void consume_input(const uint8_t *data, size_t length) {
    for (size_t index = 0; index < length; ++index) {
        const char value = static_cast<char>(data[index]);
        if (value == '\r' || value == '\n') {
            if (command_length != 0) {
                execute_command();
            }
            continue;
        }
        if (value == '\b' || value == 0x7f) {
            if (command_length != 0) {
                --command_length;
            }
            continue;
        }
        if (command_length + 1 < command.size()) {
            command[command_length++] = value;
        }
    }
}

} // namespace

namespace usb_cdc_log {

void init() {
    cdc_stdio_driver.out_chars = output_chars;
    cdc_stdio_driver.out_flush = output_flush;
    stdio_set_driver_enabled(&cdc_stdio_driver, true);
}

void task() {
    flush_backlog();

    uint8_t input[64];
    while (tud_cdc_available()) {
        const uint32_t count = tud_cdc_read(input, sizeof(input));
        consume_input(input, count);
    }
}

void set_runtime_ready(bool ready) {
    runtime_ready = ready;
}

} // namespace usb_cdc_log

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <tuple>

#include "bridge_mode.hpp"
#include "dualsense_protocol.hpp"
#include "imu_converter.hpp"
#include "mode_chord.hpp"
#include "pairing_chord.hpp"
#include "pro2_protocol.hpp"
#include "pro2_rumble.hpp"
#include "source_recovery_policy.hpp"
#include "switch1_protocol.hpp"

namespace {

int g_failures = 0;

void check(bool condition, const char *name) {
    std::printf("[%s] %s\n", condition ? "PASS" : "FAIL", name);
    if (!condition) {
        ++g_failures;
    }
}

int16_t read_i16(const uint8_t *data) {
    return static_cast<int16_t>(
        static_cast<uint16_t>(data[0]) |
        (static_cast<uint16_t>(data[1]) << 8));
}

uint32_t read_u32(const uint8_t *data) {
    return static_cast<uint32_t>(data[0]) |
           (static_cast<uint32_t>(data[1]) << 8) |
           (static_cast<uint32_t>(data[2]) << 16) |
           (static_cast<uint32_t>(data[3]) << 24);
}

uint32_t crc32_update(uint32_t crc, const uint8_t *data, size_t size) {
    for (size_t index = 0; index < size; ++index) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320u : 0u);
        }
    }
    return crc;
}

bool dualsense_crc_valid(const dualsense::InputReport &report) {
    const uint8_t seed = 0xa1;
    uint32_t crc = crc32_update(0xffffffffu, &seed, 1);
    crc = crc32_update(crc, report.data(), report.size() - 4);
    return read_u32(report.data() + report.size() - 4) == ~crc;
}

using DescriptorKey = std::tuple<uint8_t, uint8_t>;

std::map<DescriptorKey, uint32_t> descriptor_sizes(
    const uint8_t *descriptor, size_t size) {
    std::map<DescriptorKey, uint32_t> bits;
    uint8_t report_id = 0;
    uint32_t report_size = 0;
    uint32_t report_count = 0;
    for (size_t index = 0; index < size;) {
        const uint8_t prefix = descriptor[index++];
        if (prefix == 0xfe) {
            if (index + 2 > size) break;
            const size_t long_size = descriptor[index];
            index += 2 + long_size;
            continue;
        }
        const size_t data_size =
            (prefix & 0x03) == 3 ? 4 : (prefix & 0x03);
        if (index + data_size > size) break;
        uint32_t value = 0;
        for (size_t byte = 0; byte < data_size; ++byte) {
            value |= static_cast<uint32_t>(descriptor[index + byte])
                     << (byte * 8);
        }
        index += data_size;
        const uint8_t tag = prefix & 0xfc;
        if (tag == 0x84) {
            report_id = static_cast<uint8_t>(value);
        } else if (tag == 0x74) {
            report_size = value;
        } else if (tag == 0x94) {
            report_count = value;
        } else if (tag == 0x80 || tag == 0x90 || tag == 0xb0) {
            const uint8_t type =
                tag == 0x80 ? 1 : tag == 0x90 ? 2 : 3;
            bits[{type, report_id}] += report_size * report_count;
        }
    }
    return bits;
}

bool descriptor_has_only_known_main_items(const uint8_t *descriptor,
                                          size_t size) {
    for (size_t index = 0; index < size;) {
        const uint8_t prefix = descriptor[index++];
        if (prefix == 0xfe) {
            if (index + 2 > size) return false;
            const size_t long_size = descriptor[index];
            index += 2 + long_size;
            if (index > size) return false;
            continue;
        }
        const size_t data_size =
            (prefix & 0x03) == 3 ? 4 : (prefix & 0x03);
        if (index + data_size > size) return false;
        const uint8_t type = (prefix >> 2) & 0x03;
        const uint8_t tag = (prefix >> 4) & 0x0f;
        if (type == 0 && tag != 0x08 && tag != 0x09 &&
            tag != 0x0a && tag != 0x0b && tag != 0x0c) {
            return false;
        }
        index += data_size;
    }
    return true;
}

void test_dualsense() {
    check(dualsense::report_descriptor_size() == 279 &&
              dualsense::report_descriptor()[278] == 0xc0,
          "DualSense SDP descriptor excludes trailing zero pad");
    check(descriptor_has_only_known_main_items(
              dualsense::report_descriptor(),
              dualsense::report_descriptor_size()),
          "DualSense descriptor has no reserved HID Main item");
    const auto sizes = descriptor_sizes(
        dualsense::report_descriptor(),
        dualsense::report_descriptor_size());
    check(sizes.at(DescriptorKey{uint8_t{1}, uint8_t{0x31}}) == 77u * 8u,
          "DualSense descriptor input 0x31 is 77 bytes");
    check(sizes.at(DescriptorKey{uint8_t{2}, uint8_t{0x31}}) == 77u * 8u,
          "DualSense descriptor output 0x31 is 77 bytes");
    check(sizes.at(DescriptorKey{uint8_t{3}, uint8_t{0x05}}) == 40u * 8u,
          "DualSense calibration feature is 40 bytes");

    pro2::InputState input;
    input.gyro_x = 14247;
    input.gyro_y = 14247;
    input.gyro_z = 14247;
    input.accel_x = 4096;
    input.accel_y = 4096;
    input.accel_z = 4096;
    input.buttons = pro2::ButtonGL | pro2::ButtonGR;
    input.received_at_us = 123456;
    const auto report = dualsense::make_input_report(input, true, 7);
    const uint8_t *common = report.data() + 2;
    check(common[0] == 128 && common[1] == 128 &&
              common[2] == 128 && common[3] == 128,
          "DualSense neutral stick center is exactly 128");
    pro2::InputState endpoint_input;
    endpoint_input.left_x =
        pro2::kStickCenter + pro2::kStickFullScaleRange;
    endpoint_input.left_y =
        pro2::kStickCenter - pro2::kStickFullScaleRange;
    const auto endpoint_report =
        dualsense::make_input_report(endpoint_input, false, 0);
    check(endpoint_report[2] == 255 && endpoint_report[3] == 255,
          "DualSense physical Pro2 gate reaches both positive endpoints");
    check(read_i16(common + 15) == 16384 &&
              read_i16(common + 17) == 16384 &&
              read_i16(common + 19) == -16384,
          "DualSense gyro map is +X,+Z,-Y at 16.384 raw/dps");
    check(read_i16(common + 21) == 8192 &&
              read_i16(common + 23) == 8192 &&
              read_i16(common + 25) == -8192,
          "DualSense accel map is +X,+Z,-Y at 8192 raw/g");
    check((common[9] & 0xc0) == 0xc0,
          "DualSense Edge GL/GR map to independent paddles");
    check(dualsense_crc_valid(report),
          "DualSense Bluetooth input CRC32 is valid");

    std::array<uint8_t, 64> feature{};
    const std::array<uint8_t, 6> address = {1, 2, 3, 4, 5, 6};
    const size_t feature_size = dualsense::make_feature_report(
        0x05, address, false, feature.data(), feature.size());
    check(feature_size == 40 &&
              read_i16(feature.data() + 6) == 16384 &&
              read_i16(feature.data() + 8) == -16384 &&
              read_i16(feature.data() + 22) == 8192 &&
              read_i16(feature.data() + 24) == -8192,
          "DualSense feature calibration matches final raw scales");
}

void test_switch1() {
    const auto sizes = descriptor_sizes(
        switch1::report_descriptor(),
        switch1::report_descriptor_size());
    check(sizes.at(DescriptorKey{uint8_t{1}, uint8_t{0x30}}) == 48u * 8u,
          "Switch 1 input 0x30 is 48 bytes");
    check(sizes.at(DescriptorKey{uint8_t{1}, uint8_t{0x21}}) == 48u * 8u,
          "Switch 1 subcommand reply 0x21 is 48 bytes");
    check(sizes.at(DescriptorKey{uint8_t{2}, uint8_t{0x01}}) == 48u * 8u,
          "Switch 1 output 0x01 is 48 bytes");
    check(sizes.at(DescriptorKey{uint8_t{2}, uint8_t{0x10}}) == 9u * 8u,
          "Switch 1 rumble-only output 0x10 is 9 bytes");

    std::array<pro2::InputState, 3> samples{};
    samples[0].gyro_x = 14247 / 10;
    samples[1].gyro_x = 14247 / 5;
    samples[2].gyro_x = 14247 / 2;
    samples[2].accel_z = 4096;
    switch1::RuntimeState runtime;
    check(runtime.input_mode == 0x3f && !runtime.imu_enabled &&
              !runtime.vibration_enabled,
          "Switch 1 starts in simple HID mode before host handshake");
    runtime.input_mode = 0x30;
    runtime.imu_enabled = true;
    const auto report = switch1::make_input_report(
        samples.data(), samples.size(), runtime);
    check(report[0] == 0xa1 && report[1] == 0x30 &&
              read_i16(report.data() + 20) <
                  read_i16(report.data() + 32) &&
              read_i16(report.data() + 32) <
                  read_i16(report.data() + 44),
          "Switch 1 frame carries three distinct real IMU samples");
    check(read_i16(report.data() + 42) == 4096,
          "Switch 1 accel remains in Nintendo 4096 raw/g scale");
    pro2::InputState endpoint;
    endpoint.left_x =
        pro2::kStickCenter + pro2::kStickFullScaleRange;
    endpoint.left_y =
        pro2::kStickCenter - pro2::kStickFullScaleRange;
    endpoint.buttons = pro2::ButtonA | pro2::ButtonY |
                       pro2::ButtonZL | pro2::ButtonPlus |
                       pro2::ButtonUp;
    const auto endpoint_report = switch1::make_input_report(
        &endpoint, 1, runtime);
    check(endpoint_report[7] == 0xff &&
              (endpoint_report[8] & 0x0f) == 0x0f &&
              endpoint_report[9] == 0x00,
          "Switch 1 physical Pro2 gate reaches 12-bit endpoints");
    const auto simple = switch1::make_simple_input_report(endpoint);
    check(simple[0] == 0xa1 && simple[1] == 0x3f &&
              simple[2] == 0x46 && simple[3] == 0x02 &&
              simple[4] == 0x00 &&
              simple[5] == 0xff && simple[6] == 0xff,
          "Switch 1 pre-handshake 0x3F buttons, hat and axes match real Pro");

    switch1::RuntimeState handshake;
    std::array<uint8_t, 11> set_full_mode{};
    set_full_mode[9] = 0x03;
    set_full_mode[10] = 0x30;
    switch1::Report mode_reply{};
    const std::array<uint8_t, 6> address = {1, 2, 3, 4, 5, 6};
    check(switch1::make_subcommand_reply(
              set_full_mode.data(), set_full_mode.size(), address, nullptr,
              &handshake, &mode_reply) &&
              handshake.input_mode == 0x30 && mode_reply[14] == 0x80,
          "Switch 1 enters full report mode only after subcommand 0x03");

    std::array<uint8_t, 10> info_request{};
    info_request[9] = 0x02;
    switch1::Report info_reply{};
    check(switch1::make_subcommand_reply(
              info_request.data(), info_request.size(), address, nullptr,
              &handshake, &info_reply) &&
              info_reply[14] == 0x82 && info_reply[15] == 0x02 &&
              info_reply[16] == 0x03 && info_reply[17] == 0x48 &&
              info_reply[18] == 0x03 && info_reply[19] == 0x02 &&
              info_reply[26] == 0x01 && info_reply[27] == 0x02,
          "Switch 1 device-info reply matches the mature Pro handshake");

    const uint8_t max_rumble[] = {
        0, 0, 0xc8, 0x72, 0, 0, 0xc8, 0x72, 0};
    const auto decoded =
        switch1::decode_rumble(max_rumble, sizeof(max_rumble));
    check(decoded.left_trigger == 255 && decoded.right_trigger == 255 &&
              decoded.enabled_mask == 0x03,
          "Switch 1 HD amplitude is reduced to full ordinary rumble");
}

void test_source_recovery() {
    using source_recovery::Action;
    using source_recovery::Controller;

    Controller recovery;
    constexpr uint64_t started = 1000000;
    check(recovery.tick(started + 1900000, true, true, started, 0) ==
              Action::None,
          "USB source recovery ignores a healthy report gap");
    check(recovery.tick(started + 2000000, true, true, started, 0) ==
              Action::AbortInput,
          "USB source recovery aborts a stale HID input transfer first");
    check(recovery.tick(started + 2020000, true, true, started, 0) ==
              Action::SubmitReceive,
          "USB source recovery rearms HID input after abort settle");
    check(recovery.tick(started + 3020000, true, true, started, 0) ==
              Action::BeginBusReset,
          "USB source recovery escalates to a root-port reset");
    check(recovery.tick(started + 3040000, true, true, started, 0) ==
              Action::EndBusReset,
          "USB source recovery ends the reset pulse after 20 ms");
    check(recovery.note_report(),
          "A real input report completes USB source recovery");
    const auto recovered = recovery.snapshot();
    check(recovered.incidents == 1 && recovered.abort_attempts == 1 &&
              recovered.receive_rearms == 1 && recovered.bus_resets == 1 &&
              recovered.recoveries == 1,
          "USB source recovery telemetry counts each escalation once");

    recovery.tick(started + 5000000, true, true, started, 0);
    recovery.reset_runtime();
    check(recovery.snapshot().phase == source_recovery::Phase::Monitoring,
          "USB unmount resets only the active recovery phase");

    Controller remounted;
    constexpr uint64_t remount_started = 20000000;
    check(remounted.tick(remount_started + 100000, true, true,
                         remount_started, started + 500000) == Action::None,
          "USB source recovery ignores a stale timestamp from the prior mount");
}

} // namespace

int main() {
    check(bridge::kDefaultMode == bridge::Mode::DualSenseEdge,
          "Pico product default mode is PS5 Edge");
    check(pro2::self_test(), "Pro2 input parser");
    check(pro2_rumble::self_test(), "Pro2 ordinary rumble packer");
    check(imu::self_test(), "IMU physical conversion");
    check(mode_chord::self_test(),
          "Pro2 guarded mode chord detector");
    check(pairing_chord::self_test(),
          "Pro2 guarded re-pair chord detector");
    check(dualsense::self_test(), "DualSense mapper built-in self-test");
    check(switch1::self_test(), "Switch 1 mapper built-in self-test");
    test_dualsense();
    test_switch1();
    test_source_recovery();
    std::printf("[RESULT] failures=%d\n", g_failures);
    return g_failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}

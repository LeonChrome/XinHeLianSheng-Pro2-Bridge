#include "mode_manager.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>

#include "hardware/flash.h"
#include "hardware/gpio.h"
#include "hardware/sync.h"
#include "pico/flash.h"
#include "pico/stdlib.h"
#include "pico/unique_id.h"
#include "hardware/watchdog.h"
#include "bridge_state.hpp"
#include "mode_chord.hpp"
#include "pairing_chord.hpp"
#include "pairing_feedback.hpp"
#include "pro2_host.hpp"
#include "wireless_backend.hpp"

namespace mode_manager {
namespace {

constexpr uint32_t kMagic = 0x58484c53; // "XHLS"
constexpr uint32_t kVersion = 3;
#ifndef XHLS_PAIRING_RECOVERY_TOKEN
#define XHLS_PAIRING_RECOVERY_TOKEN 0
#endif
constexpr uint32_t kRequestedPairingRecoveryToken =
    XHLS_PAIRING_RECOVERY_TOKEN;
constexpr uint kModeButtonGpio = 2;
constexpr uint64_t kModeButtonHoldUs = 1500000;
constexpr uint64_t kControllerSourceFreshUs = 100000;
constexpr uint32_t kStorageOffset =
    PICO_FLASH_SIZE_BYTES - (10u * FLASH_SECTOR_SIZE);

struct LegacyConfigRecord {
    uint32_t magic;
    uint32_t version;
    uint8_t mode;
    uint8_t reserved[3];
    uint32_t checksum;
};

struct ConfigRecord {
    uint32_t magic;
    uint32_t version;
    uint8_t mode;
    uint8_t reserved[3];
    uint32_t pairing_recovery_token;
    uint32_t checksum;
};

static_assert(sizeof(ConfigRecord) <= FLASH_PAGE_SIZE,
              "Mode record must fit in one flash page");

bridge::Mode g_mode = bridge::kDefaultMode;
uint64_t g_button_pressed_at_us = 0;
bool g_button_latched = false;
mode_chord::Detector g_chord_detector;
bool g_chord_notice_active = false;
bridge::Mode g_chord_notice_target = bridge::kDefaultMode;
pairing_chord::Detector g_pairing_detector;
bool g_pairing_notice_active = false;
uint32_t g_applied_pairing_recovery_token = 0;
bool g_pairing_recovery_pending = false;
alignas(4) std::array<uint8_t, FLASH_PAGE_SIZE> g_flash_page{};

uint32_t checksum_bytes(const void *record, size_t length) {
    const uint8_t *bytes = static_cast<const uint8_t *>(record);
    uint32_t hash = 2166136261u;
    for (size_t index = 0; index < length; ++index) {
        hash ^= bytes[index];
        hash *= 16777619u;
    }
    return hash;
}

uint32_t checksum(const LegacyConfigRecord &record) {
    return checksum_bytes(&record, offsetof(LegacyConfigRecord, checksum));
}

uint32_t checksum(const ConfigRecord &record) {
    return checksum_bytes(&record, offsetof(ConfigRecord, checksum));
}

bool valid_mode(uint8_t value) {
    return value <= static_cast<uint8_t>(bridge::Mode::Pro2Quiet);
}

void flash_mutation(void *) {
    flash_range_erase(kStorageOffset, FLASH_SECTOR_SIZE);
    flash_range_program(kStorageOffset, g_flash_page.data(), FLASH_PAGE_SIZE);
}

void save(bridge::Mode mode) {
    g_flash_page.fill(0xff);
    ConfigRecord record{};
    record.magic = kMagic;
    record.version = kVersion;
    record.mode = static_cast<uint8_t>(mode);
    record.pairing_recovery_token =
        g_applied_pairing_recovery_token;
    record.checksum = checksum(record);
    std::memcpy(g_flash_page.data(), &record, sizeof(record));
    const int result =
        flash_safe_execute(flash_mutation, nullptr, UINT32_MAX);
    std::printf("[MODE_STORE] mode=%s result=%d offset=0x%08lx\n",
                bridge::mode_name(mode), result,
                static_cast<unsigned long>(kStorageOffset));
}

} // namespace

void init() {
    gpio_init(kModeButtonGpio);
    gpio_set_dir(kModeButtonGpio, GPIO_IN);
    gpio_pull_up(kModeButtonGpio);

    const auto *record = reinterpret_cast<const ConfigRecord *>(
        XIP_BASE + kStorageOffset);
    const auto *legacy = reinterpret_cast<const LegacyConfigRecord *>(
        XIP_BASE + kStorageOffset);
    const bool record_valid =
        record->magic == kMagic && record->version == kVersion &&
        valid_mode(record->mode) && record->checksum == checksum(*record);
    const bool legacy_valid =
        legacy->magic == kMagic && legacy->version < kVersion &&
        valid_mode(legacy->mode) && legacy->checksum == checksum(*legacy);
    if (record_valid) {
        g_mode = static_cast<bridge::Mode>(record->mode);
        g_applied_pairing_recovery_token =
            record->pairing_recovery_token;
        std::printf(
            "[MODE_STORE] loaded=%s pairing_recovery_token=%lu\n",
            bridge::mode_name(g_mode),
            static_cast<unsigned long>(
                g_applied_pairing_recovery_token));
    } else if (legacy_valid && legacy->version >= 2) {
        g_mode = static_cast<bridge::Mode>(legacy->mode);
        g_applied_pairing_recovery_token = 0;
        std::printf(
            "[MODE_STORE] migrate version=%lu->%lu mode=%s "
            "pairing_recovery_token=0 bonds_preserved=1\n",
            static_cast<unsigned long>(legacy->version),
            static_cast<unsigned long>(kVersion),
            bridge::mode_name(g_mode));
        save(g_mode);
    } else if (legacy_valid) {
        // r31 changes the product default to PS5 Edge. Migrate the old mode
        // selector once without touching the separate per-mode bond banks.
        g_mode = bridge::kDefaultMode;
        g_applied_pairing_recovery_token = 0;
        std::printf(
            "[MODE_STORE] migrate version=%lu->%lu old=%s default=%s "
            "bonds_preserved=1\n",
            static_cast<unsigned long>(legacy->version),
            static_cast<unsigned long>(kVersion),
            bridge::mode_name(static_cast<bridge::Mode>(legacy->mode)),
            bridge::mode_name(g_mode));
        save(g_mode);
    } else {
        g_mode = bridge::kDefaultMode;
        g_applied_pairing_recovery_token = 0;
        std::printf("[MODE_STORE] no valid record; default=%s\n",
                    bridge::mode_name(g_mode));
    }

    g_pairing_recovery_pending =
        kRequestedPairingRecoveryToken != 0 &&
        g_applied_pairing_recovery_token !=
            kRequestedPairingRecoveryToken;
    if (g_pairing_recovery_pending) {
        const bridge::Mode previous = g_mode;
        g_mode = bridge::Mode::DualSenseEdge;
        save(g_mode);
        std::printf(
            "[PAIRING_RECOVERY] pending token=%lu previous_mode=%s "
            "forced_mode=%s action=clear_edge_bonds_once_when_bt_ready\n",
            static_cast<unsigned long>(kRequestedPairingRecoveryToken),
            bridge::mode_name(previous), bridge::mode_name(g_mode));
    }
}

void task() {
    const uint64_t now = time_us_64();
    const bool pressed = gpio_get(kModeButtonGpio) == 0;
    if (!pressed) {
        g_button_pressed_at_us = 0;
        g_button_latched = false;
    } else if (g_button_pressed_at_us == 0) {
        g_button_pressed_at_us = now;
    } else if (!g_button_latched &&
               now - g_button_pressed_at_us >= kModeButtonHoldUs) {
        g_button_latched = true;
        set_and_reboot(next(g_mode));
    }

    pro2::InputState input;
    uint32_t generation = 0;
    const bool have_input = bridge::read_latest_input(&input, &generation);
    const bool source_fresh =
        have_input && now >= input.received_at_us &&
        now - input.received_at_us <= kControllerSourceFreshUs;

    const bool pairing_valid =
        source_fresh && pairing_chord::decode(input.buttons);
    if (pairing_valid && !g_pairing_notice_active) {
        g_pairing_notice_active = true;
        std::printf(
            "[PAIR_CHORD] armed mode=%s hold_ms=%llu "
            "combo=GL+GR+ZL+ZR+Plus+Minus\n",
            bridge::mode_name(g_mode),
            static_cast<unsigned long long>(
                pairing_chord::kHoldDurationUs / 1000));
    } else if (!pairing_valid && g_pairing_notice_active) {
        std::printf("[PAIR_CHORD] canceled mode=%s\n",
                    bridge::mode_name(g_mode));
        g_pairing_notice_active = false;
    }

    if (g_pairing_detector.update(input.buttons, source_fresh, now)) {
        std::printf(
            "[PAIR_CHORD] triggered mode=%s source_age_ms=%.1f "
            "action=clear_current_mode_bonds_and_reopen_pairing "
            "pairing_timeout=none\n",
            bridge::mode_name(g_mode),
            static_cast<double>(now - input.received_at_us) / 1000.0);
        pairing_feedback::start("controller_pair_chord");
        wireless_backend::forget_bonds();
        g_chord_detector.reset();
        return;
    }

    bridge::Mode decoded = bridge::kDefaultMode;
    const bool chord_valid =
        source_fresh && mode_chord::decode(input.buttons, &decoded);
    if (chord_valid &&
        (!g_chord_notice_active || decoded != g_chord_notice_target)) {
        g_chord_notice_active = true;
        g_chord_notice_target = decoded;
        std::printf(
            "[MODE_CHORD] armed target=%s hold_ms=%llu "
            "guard=GL+GR+ZL+ZR selector=%s\n",
            bridge::mode_name(decoded),
            static_cast<unsigned long long>(
                mode_chord::kHoldDurationUs / 1000),
            decoded == bridge::Mode::DualSense
                ? "A"
                : decoded == bridge::Mode::DualSenseEdge
                      ? "B"
                      : decoded == bridge::Mode::XboxBle ? "X" : "Y");
    } else if (!chord_valid && g_chord_notice_active) {
        std::printf("[MODE_CHORD] canceled target=%s\n",
                    bridge::mode_name(g_chord_notice_target));
        g_chord_notice_active = false;
    }

    bridge::Mode requested = bridge::kDefaultMode;
    if (!g_chord_detector.update(input.buttons, source_fresh, now,
                                 &requested)) {
        return;
    }

    std::printf("[MODE_CHORD] triggered target=%s source_age_ms=%.1f\n",
                bridge::mode_name(requested),
                source_fresh
                    ? static_cast<double>(now - input.received_at_us) / 1000.0
                    : -1.0);
    if (requested == g_mode) {
        std::printf(
            "[MODE_CHORD] target already active; release combo to rearm\n");
        return;
    }
    set_and_reboot(requested);
}

bridge::Mode current() {
    return g_mode;
}

bridge::Mode next(bridge::Mode mode) {
    switch (mode) {
    case bridge::Mode::XboxBle:
        return bridge::Mode::DualSense;
    case bridge::Mode::DualSense:
        return bridge::Mode::DualSenseEdge;
    case bridge::Mode::DualSenseEdge:
        return bridge::Mode::SwitchProClassic;
    case bridge::Mode::SwitchProClassic:
        return bridge::Mode::Pro2Quiet;
    case bridge::Mode::Pro2Quiet:
        return bridge::Mode::XboxBle;
    }
    return bridge::kDefaultMode;
}

bool parse(const char *text, bridge::Mode *mode) {
    if (text == nullptr || mode == nullptr) {
        return false;
    }
    if (std::strcmp(text, "xbox") == 0 ||
        std::strcmp(text, "xbox_ble") == 0) {
        *mode = bridge::Mode::XboxBle;
        return true;
    }
    if (std::strcmp(text, "ps5") == 0 ||
        std::strcmp(text, "dualsense") == 0) {
        *mode = bridge::Mode::DualSense;
        return true;
    }
    if (std::strcmp(text, "edge") == 0 ||
        std::strcmp(text, "ps5_edge") == 0 ||
        std::strcmp(text, "dualsense_edge") == 0) {
        *mode = bridge::Mode::DualSenseEdge;
        return true;
    }
    if (std::strcmp(text, "switch1") == 0 ||
        std::strcmp(text, "ns1") == 0 ||
        std::strcmp(text, "switch_pro") == 0) {
        *mode = bridge::Mode::SwitchProClassic;
        return true;
    }
    if (std::strcmp(text, "pro2") == 0 ||
        std::strcmp(text, "quiet") == 0 ||
        std::strcmp(text, "pro2_quiet") == 0) {
        *mode = bridge::Mode::Pro2Quiet;
        return true;
    }
    return false;
}

void set_and_reboot(bridge::Mode mode) {
    if (!valid_mode(static_cast<uint8_t>(mode))) {
        return;
    }
    std::printf("[MODE_SWITCH] current=%s requested=%s action=save_and_reboot\n",
                bridge::mode_name(g_mode), bridge::mode_name(mode));
    // Do not carry the last host effect through the watchdog reset. The
    // physical Pro2 remains powered while the wireless identity changes.
    pro2_host::stop_rumble();
    save(mode);
    sleep_ms(100);
    watchdog_reboot(0, 0, 20);
    while (true) {
        tight_loop_contents();
    }
}

bool pairing_recovery_required() {
    return g_pairing_recovery_pending;
}

void mark_pairing_recovery_applied() {
    if (!g_pairing_recovery_pending) {
        return;
    }
    g_applied_pairing_recovery_token =
        kRequestedPairingRecoveryToken;
    g_pairing_recovery_pending = false;
    save(g_mode);
    std::printf(
        "[PAIRING_RECOVERY] applied token=%lu mode=%s "
        "next_boot_preserves_new_bond=1\n",
        static_cast<unsigned long>(g_applied_pairing_recovery_token),
        bridge::mode_name(g_mode));
}

std::array<uint8_t, 6> bluetooth_address(bridge::Mode mode) {
    pico_unique_board_id_t id{};
    pico_get_unique_board_id(&id);
    std::array<uint8_t, 6> address{};
    if (mode == bridge::Mode::SwitchProClassic) {
        address = {0x7c, 0xbb, 0x8a, id.id[5], id.id[6], id.id[7]};
    } else {
        address = {
            0x02,
            static_cast<uint8_t>(0x58 + static_cast<uint8_t>(mode)),
            id.id[3],
            id.id[5],
            id.id[6],
            id.id[7],
        };
    }
    address[5] ^= static_cast<uint8_t>(mode) * 0x2du;
    // r10 Classic test identity revision. A fresh address prevents Windows'
    // discovery cache from reusing the pre-r9 half-paired endpoint while the
    // pairing and SDP path is audited. Keep each mode deterministic.
    if (mode != bridge::Mode::XboxBle) {
        address[5] ^= 0x10u;
    }
    return address;
}

bool self_test() {
    return bridge::kDefaultMode == bridge::Mode::DualSenseEdge &&
           next(bridge::Mode::XboxBle) == bridge::Mode::DualSense &&
           next(bridge::Mode::DualSense) == bridge::Mode::DualSenseEdge &&
           next(bridge::Mode::DualSenseEdge) ==
               bridge::Mode::SwitchProClassic &&
           next(bridge::Mode::SwitchProClassic) == bridge::Mode::Pro2Quiet &&
           next(bridge::Mode::Pro2Quiet) == bridge::Mode::XboxBle &&
           mode_chord::self_test() && pairing_chord::self_test();
}

} // namespace mode_manager

extern "C" uint32_t xhls_btstack_storage_offset() {
    const uint32_t mode_index =
        static_cast<uint32_t>(mode_manager::current());
    const uint32_t banks_after =
        4u - std::min<uint32_t>(mode_index, 3u);
    return PICO_FLASH_SIZE_BYTES - FLASH_SECTOR_SIZE -
           banks_after * 2u * FLASH_SECTOR_SIZE;
}

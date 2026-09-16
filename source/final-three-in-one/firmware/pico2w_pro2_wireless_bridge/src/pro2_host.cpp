#include "pro2_host.hpp"

#include <array>
#include <cstdio>
#include <cstring>

#include "bridge_state.hpp"
#include "bsp/board_api.h"
#include "hardware/gpio.h"
#include "pico/time.h"
#include "pro2_protocol.hpp"
#include "source_recovery_policy.hpp"
#include "tusb.h"
#include "host/usbh_pvt.h"

namespace {

constexpr size_t kBulkBufferSize = 128;
constexpr uint64_t kInitRetryDelayUs = 500000;
constexpr uint64_t kInitTransferTimeoutUs = 1000000;
constexpr uint64_t kHealthPeriodUs = 15000000;
constexpr uint64_t kLineSamplePeriodUs = 1000;
constexpr uint64_t kRumblePeriodUs = 12000;
constexpr uint8_t kRumbleStopReports = 3;
constexpr uint8_t kUsbDpPin = 0;
constexpr uint8_t kUsbDmPin = 1;

enum class InitPhase : uint8_t {
    Idle,
    SendingCommand,
    WaitingResponse,
    Complete,
    RetryWait,
};

struct DriverState {
    uint8_t dev_addr = 0;
    uint8_t hid_instance = 0xff;
    uint8_t vendor_interface = 0xff;
    uint8_t bulk_in_ep = 0;
    uint8_t bulk_out_ep = 0;
    bool device_connected = false;
    bool hid_ready = false;
    bool vendor_ready = false;
    bool reports_running = false;
    size_t init_index = 0;
    InitPhase init_phase = InitPhase::Idle;
    uint64_t retry_at_us = 0;
    uint64_t transfer_deadline_us = 0;
    uint32_t parsed_reports = 0;
    uint32_t rejected_reports = 0;
    uint32_t duplicate_sequences = 0;
    uint32_t disconnects = 0;
    uint32_t init_failures = 0;
    uint64_t last_report_us = 0;
    uint64_t reports_started_us = 0;
    uint32_t last_sequence = 0;
    bool have_sequence = false;
    uint64_t last_health_us = 0;
    uint32_t health_window_reports = 0;
    uint64_t last_line_sample_us = 0;
    uint32_t line_se0_samples = 0;
    uint32_t line_fs_j_samples = 0;
    uint32_t line_ls_j_samples = 0;
    uint32_t line_se1_samples = 0;
    pro2_rumble::XboxRumbleCommand rumble_command{};
    bool rumble_active = false;
    bool rumble_transfer_pending = false;
    uint8_t rumble_counter = 0;
    uint8_t rumble_stop_reports_remaining = 0;
    uint64_t rumble_deadline_us = 0;
    uint64_t next_rumble_report_us = 0;
    uint64_t rumble_transfer_started_us = 0;
    uint64_t last_rumble_update_us = 0;
    uint64_t last_rumble_change_us = 0;
    uint32_t rumble_updates = 0;
    uint32_t rumble_reports = 0;
    uint32_t rumble_stop_reports = 0;
    uint32_t rumble_submit_errors = 0;
    uint32_t rumble_transfer_errors = 0;
    uint32_t rumble_coalesced_updates = 0;
    uint32_t rumble_suppressed_updates = 0;
    uint32_t logged_rumble_submit_errors = 0;
    uint32_t logged_rumble_transfer_errors = 0;
    uint32_t rumble_trace_count = 0;
};

DriverState g_driver;
source_recovery::Controller g_source_recovery;
bool g_initialized = false;
CFG_TUH_MEM_SECTION TU_ATTR_ALIGNED(4) std::array<uint8_t, kBulkBufferSize> g_bulk_out{};
CFG_TUH_MEM_SECTION TU_ATTR_ALIGNED(4) std::array<uint8_t, kBulkBufferSize> g_bulk_in{};
CFG_TUH_MEM_SECTION TU_ATTR_ALIGNED(4)
std::array<uint8_t, pro2_rumble::kReportSize> g_rumble_report{};

bool descriptor_has_output_report(const uint8_t *descriptor, uint16_t length,
                                  uint8_t wanted_report_id) {
    uint8_t report_id = 0;
    uint16_t offset = 0;
    while (descriptor != nullptr && offset < length) {
        const uint8_t prefix = descriptor[offset++];
        if (prefix == 0xfe) {
            if (offset + 2 > length) {
                return false;
            }
            const uint8_t long_size = descriptor[offset];
            offset = static_cast<uint16_t>(offset + 2 + long_size);
            continue;
        }

        const uint8_t size_code = prefix & 0x03;
        const uint8_t item_size = size_code == 3 ? 4 : size_code;
        const uint8_t item_type = (prefix >> 2) & 0x03;
        const uint8_t item_tag = (prefix >> 4) & 0x0f;
        if (offset + item_size > length) {
            return false;
        }
        if (item_type == 1 && item_tag == 8 && item_size == 1) {
            report_id = descriptor[offset];
        } else if (item_type == 0 && item_tag == 9 &&
                   report_id == wanted_report_id) {
            return true;
        }
        offset = static_cast<uint16_t>(offset + item_size);
    }
    return false;
}

bool is_pro2(uint8_t dev_addr) {
    uint16_t vid = 0;
    uint16_t pid = 0;
    tuh_vid_pid_get(dev_addr, &vid, &pid);
    return vid == pro2::kVendorId && pid == pro2::kProductId;
}

void reset_runtime_state(bool count_disconnect) {
    if (count_disconnect && g_driver.device_connected) {
        ++g_driver.disconnects;
    }
    g_driver.dev_addr = 0;
    g_driver.hid_instance = 0xff;
    g_driver.vendor_interface = 0xff;
    g_driver.bulk_in_ep = 0;
    g_driver.bulk_out_ep = 0;
    g_driver.device_connected = false;
    g_driver.hid_ready = false;
    g_driver.vendor_ready = false;
    g_driver.reports_running = false;
    g_driver.reports_started_us = 0;
    g_driver.init_index = 0;
    g_driver.init_phase = InitPhase::Idle;
    g_driver.transfer_deadline_us = 0;
    g_driver.have_sequence = false;
    g_driver.rumble_command = {};
    g_driver.rumble_active = false;
    g_driver.rumble_transfer_pending = false;
    g_driver.rumble_stop_reports_remaining = 0;
    g_driver.rumble_deadline_us = 0;
    g_driver.next_rumble_report_us = 0;
    g_driver.rumble_transfer_started_us = 0;
    g_source_recovery.reset_runtime();
    bridge::clear_input();
}

bool submit_bulk(uint8_t ep_addr, uint8_t *buffer, uint16_t size) {
    if (!usbh_edpt_claim(g_driver.dev_addr, ep_addr)) {
        return false;
    }
    if (!usbh_edpt_xfer(g_driver.dev_addr, ep_addr, buffer, size)) {
        usbh_edpt_release(g_driver.dev_addr, ep_addr);
        return false;
    }
    return true;
}

void fail_initialization(const char *stage, xfer_result_t result) {
    ++g_driver.init_failures;
    g_driver.reports_running = false;
    g_driver.init_phase = InitPhase::RetryWait;
    g_driver.transfer_deadline_us = 0;
    g_driver.retry_at_us = time_us_64() + kInitRetryDelayUs;
    bridge::clear_input();
    std::printf("[PRO2_INIT] failed step=%u stage=%s result=%u; retry_ms=500\n",
                static_cast<unsigned>(g_driver.init_index + 1), stage,
                static_cast<unsigned>(result));
}

void submit_init_command() {
    if (!g_driver.device_connected || !g_driver.hid_ready || !g_driver.vendor_ready) {
        return;
    }

    const auto &commands = pro2::initialization_commands();
    if (g_driver.init_index >= commands.size()) {
        g_driver.init_phase = InitPhase::Complete;
        g_driver.reports_running = true;
        g_driver.reports_started_us = time_us_64();
        std::printf("[PRO2_INIT] complete report=0x05 hid_ep=0x81\n");
        if (!tuh_hid_receive_report(g_driver.dev_addr, g_driver.hid_instance)) {
            std::printf("[USB_INPUT] first receive submit failed\n");
        }
        return;
    }

    const pro2::InitCommand &command = commands[g_driver.init_index];
    g_bulk_out.fill(0);
    std::memcpy(g_bulk_out.data(), command.data, command.size);
    std::printf("[PRO2_INIT] step=%u/%u name=%s tx_len=%u\n",
                static_cast<unsigned>(g_driver.init_index + 1),
                static_cast<unsigned>(commands.size()), command.name,
                static_cast<unsigned>(command.size));
    g_driver.init_phase = InitPhase::SendingCommand;
    if (!submit_bulk(g_driver.bulk_out_ep, g_bulk_out.data(),
                     static_cast<uint16_t>(command.size))) {
        fail_initialization("submit_bulk_out", XFER_RESULT_FAILED);
    } else {
        g_driver.transfer_deadline_us = time_us_64() + kInitTransferTimeoutUs;
    }
}

void maybe_start_initialization() {
    if (!g_driver.device_connected || !g_driver.hid_ready || !g_driver.vendor_ready ||
        g_driver.init_phase != InitPhase::Idle) {
        return;
    }
    g_driver.init_index = 0;
    std::printf("[USB_LINK] Pro2 ready HID0+Vendor1; ten-step initialization starting\n");
    submit_init_command();
}

bool vendor_init() {
    return true;
}

bool vendor_deinit() {
    return true;
}

bool vendor_open(uint8_t rhport, uint8_t dev_addr,
                 const tusb_desc_interface_t *interface_descriptor, uint16_t max_len) {
    (void)rhport;
    if (interface_descriptor == nullptr ||
        interface_descriptor->bInterfaceClass != TUSB_CLASS_VENDOR_SPECIFIC ||
        interface_descriptor->bInterfaceNumber != pro2::kVendorInterface ||
        !is_pro2(dev_addr)) {
        return false;
    }

    uint8_t bulk_in = 0;
    uint8_t bulk_out = 0;
    uint16_t consumed = interface_descriptor->bLength;
    const uint8_t *cursor = tu_desc_next(interface_descriptor);
    while (consumed + 2 <= max_len) {
        const uint8_t descriptor_length = tu_desc_len(cursor);
        if (descriptor_length == 0 || consumed + descriptor_length > max_len) {
            break;
        }
        const uint8_t descriptor_type = tu_desc_type(cursor);
        if (descriptor_type == TUSB_DESC_INTERFACE) {
            break;
        }
        if (descriptor_type == TUSB_DESC_ENDPOINT) {
            const auto *endpoint = reinterpret_cast<const tusb_desc_endpoint_t *>(cursor);
            if (endpoint->bmAttributes.xfer == TUSB_XFER_BULK) {
                if (!tuh_edpt_open(dev_addr, endpoint)) {
                    std::printf("[USB_ENUM] vendor endpoint open failed ep=%02x\n",
                                endpoint->bEndpointAddress);
                    return false;
                }
                if (tu_edpt_dir(endpoint->bEndpointAddress) == TUSB_DIR_IN) {
                    bulk_in = endpoint->bEndpointAddress;
                } else {
                    bulk_out = endpoint->bEndpointAddress;
                }
            }
        }
        consumed = static_cast<uint16_t>(consumed + descriptor_length);
        cursor = tu_desc_next(cursor);
    }

    if (bulk_in != pro2::kBulkInEndpoint || bulk_out != pro2::kBulkOutEndpoint) {
        std::printf("[USB_ENUM] vendor endpoint mismatch in=%02x out=%02x expected=82/02\n",
                    bulk_in, bulk_out);
        return false;
    }

    g_driver.dev_addr = dev_addr;
    g_driver.vendor_interface = interface_descriptor->bInterfaceNumber;
    g_driver.bulk_in_ep = bulk_in;
    g_driver.bulk_out_ep = bulk_out;
    std::printf("[USB_ENUM] claimed vendor interface=%u bulk_in=%02x bulk_out=%02x\n",
                static_cast<unsigned>(g_driver.vendor_interface), bulk_in, bulk_out);
    return true;
}

bool vendor_set_config(uint8_t dev_addr, uint8_t interface_number) {
    if (dev_addr != g_driver.dev_addr || interface_number != g_driver.vendor_interface) {
        return false;
    }
    g_driver.vendor_ready = true;
    usbh_driver_set_config_complete(dev_addr, interface_number);
    maybe_start_initialization();
    return true;
}

bool vendor_xfer(uint8_t dev_addr, uint8_t ep_addr, xfer_result_t result,
                 uint32_t transferred_bytes) {
    if (dev_addr != g_driver.dev_addr) {
        return false;
    }

    if (result != XFER_RESULT_SUCCESS) {
        g_driver.transfer_deadline_us = 0;
        fail_initialization(ep_addr == g_driver.bulk_out_ep ? "bulk_out" : "bulk_in", result);
        return true;
    }

    if (ep_addr == g_driver.bulk_out_ep &&
        g_driver.init_phase == InitPhase::SendingCommand) {
        g_driver.transfer_deadline_us = 0;
        g_bulk_in.fill(0);
        g_driver.init_phase = InitPhase::WaitingResponse;
        if (!submit_bulk(g_driver.bulk_in_ep, g_bulk_in.data(),
                         static_cast<uint16_t>(g_bulk_in.size()))) {
            fail_initialization("submit_bulk_in", XFER_RESULT_FAILED);
        } else {
            g_driver.transfer_deadline_us =
                time_us_64() + kInitTransferTimeoutUs;
        }
        return true;
    }

    if (ep_addr == g_driver.bulk_in_ep &&
        g_driver.init_phase == InitPhase::WaitingResponse) {
        g_driver.transfer_deadline_us = 0;
        if (transferred_bytes == 0) {
            fail_initialization("bulk_in_empty", XFER_RESULT_FAILED);
            return true;
        }
        const auto &commands = pro2::initialization_commands();
        std::printf("[PRO2_INIT] step=%u/%u name=%s response_len=%lu\n",
                    static_cast<unsigned>(g_driver.init_index + 1),
                    static_cast<unsigned>(commands.size()),
                    commands[g_driver.init_index].name,
                    static_cast<unsigned long>(transferred_bytes));
        ++g_driver.init_index;
        submit_init_command();
        return true;
    }

    std::printf("[USB_VENDOR] unexpected transfer ep=%02x phase=%u bytes=%lu\n",
                ep_addr, static_cast<unsigned>(g_driver.init_phase),
                static_cast<unsigned long>(transferred_bytes));
    return true;
}

void vendor_close(uint8_t dev_addr) {
    if (dev_addr == g_driver.dev_addr) {
        std::printf("[USB_LINK] vendor interface closed\n");
        reset_runtime_state(true);
    }
}

const usbh_class_driver_t kVendorDriver{
    "Pro2Vendor",
    vendor_init,
    vendor_deinit,
    vendor_open,
    vendor_set_config,
    vendor_xfer,
    vendor_close,
};

} // namespace

extern "C" const usbh_class_driver_t *usbh_app_driver_get_cb(uint8_t *driver_count) {
    *driver_count = 1;
    return &kVendorDriver;
}

extern "C" void tuh_mount_cb(uint8_t dev_addr) {
    uint16_t vid = 0;
    uint16_t pid = 0;
    tuh_vid_pid_get(dev_addr, &vid, &pid);
    std::printf("[USB_MOUNT] addr=%u vid=%04x pid=%04x\n",
                static_cast<unsigned>(dev_addr), vid, pid);
    if (vid == pro2::kVendorId && pid == pro2::kProductId) {
        g_driver.dev_addr = dev_addr;
        g_driver.device_connected = true;
        maybe_start_initialization();
    }
}

extern "C" void tuh_umount_cb(uint8_t dev_addr) {
    std::printf("[USB_UNMOUNT] addr=%u\n", static_cast<unsigned>(dev_addr));
    if (dev_addr == g_driver.dev_addr) {
        reset_runtime_state(true);
    }
}

extern "C" void tuh_hid_mount_cb(uint8_t dev_addr, uint8_t instance,
                                  const uint8_t *report_descriptor,
                                  uint16_t descriptor_length) {
    if (!is_pro2(dev_addr)) {
        return;
    }

    g_driver.dev_addr = dev_addr;
    g_driver.hid_instance = instance;
    g_driver.hid_ready = true;
    const bool rumble_output =
        descriptor_has_output_report(report_descriptor, descriptor_length,
                                     pro2_rumble::kReportId);
    std::printf(
        "[USB_ENUM] claimed HID instance=%u report_desc_len=%u "
        "output_report_02=%u output_transport=interrupt_out\n",
        static_cast<unsigned>(instance), static_cast<unsigned>(descriptor_length),
        rumble_output);
    maybe_start_initialization();
}

extern "C" void tuh_hid_umount_cb(uint8_t dev_addr, uint8_t instance) {
    if (dev_addr == g_driver.dev_addr && instance == g_driver.hid_instance) {
        std::printf("[USB_LINK] HID interface closed instance=%u\n",
                    static_cast<unsigned>(instance));
        reset_runtime_state(true);
    }
}

extern "C" void tuh_hid_report_received_cb(uint8_t dev_addr, uint8_t instance,
                                            const uint8_t *report, uint16_t length) {
    if (dev_addr != g_driver.dev_addr || instance != g_driver.hid_instance ||
        !g_driver.reports_running) {
        return;
    }

    pro2::InputState state;
    const uint64_t now = time_us_64();
    if (pro2::parse_common_input_report(report, length, now, &state)) {
        if (g_source_recovery.note_report()) {
            std::printf("[USB_SOURCE_RECOVERY] recovered source_age_ms=0\n");
        }
        if (g_driver.have_sequence && state.sequence == g_driver.last_sequence) {
            ++g_driver.duplicate_sequences;
        }
        g_driver.have_sequence = true;
        g_driver.last_sequence = state.sequence;
        ++g_driver.parsed_reports;
        ++g_driver.health_window_reports;
        g_driver.last_report_us = now;
        bridge::publish_input(state);
    } else {
        ++g_driver.rejected_reports;
        if (g_driver.rejected_reports <= 5) {
            std::printf("[USB_INPUT] rejected len=%u report_id=%02x\n",
                        static_cast<unsigned>(length), length > 0 ? report[0] : 0);
        }
    }

    if (!tuh_hid_receive_report(dev_addr, instance)) {
        std::printf("[USB_INPUT] receive resubmit failed\n");
    }
}

extern "C" void tuh_hid_report_sent_cb(
    uint8_t dev_addr, uint8_t instance, const uint8_t *report,
    uint16_t length) {
    if (dev_addr != g_driver.dev_addr || instance != g_driver.hid_instance ||
        report == nullptr || report[0] != pro2_rumble::kReportId) {
        return;
    }

    g_driver.rumble_transfer_pending = false;
    g_driver.rumble_transfer_started_us = 0;
    if (length == pro2_rumble::kReportSize) {
        ++g_driver.rumble_reports;
    } else {
        ++g_driver.rumble_transfer_errors;
        std::printf("[PRO2_RUMBLE] transfer failed length=%u\n",
                    static_cast<unsigned>(length));
    }
}

namespace pro2_host {

void init() {
    if (g_initialized) {
        std::printf(
            "[USB_HOST] duplicate init ignored connected=%u hid=%u vendor=%u reports=%u\n",
            g_driver.device_connected ? 1u : 0u, g_driver.hid_ready ? 1u : 0u,
            g_driver.vendor_ready ? 1u : 0u, g_driver.reports_running ? 1u : 0u);
        return;
    }

    g_initialized = true;
    const bool callbacks_already_seen =
        g_driver.device_connected || g_driver.hid_ready || g_driver.vendor_ready;
    if (!callbacks_already_seen) {
        reset_runtime_state(false);
    }
    g_driver.last_health_us = time_us_64();
    std::printf(
        "[USB_HOST] PIO-USB host GP0=D+ GP1=D- expected=057E:2069 "
        "state_machine_ready=1 callbacks_seen_before_init=%u\n",
        callbacks_already_seen ? 1u : 0u);
}

void task() {
    const uint64_t now = time_us_64();
    if (now - g_driver.last_line_sample_us >= kLineSamplePeriodUs) {
        g_driver.last_line_sample_us = now;
        // Pico-PIO-USB configures the GPIO input override as inverted.
        // Undo that override here so diagnostics report physical D+/D- levels.
        const bool dp = !gpio_get(kUsbDpPin);
        const bool dm = !gpio_get(kUsbDmPin);
        if (dp && dm) {
            ++g_driver.line_se1_samples;
        } else if (dp) {
            ++g_driver.line_fs_j_samples;
        } else if (dm) {
            ++g_driver.line_ls_j_samples;
        } else {
            ++g_driver.line_se0_samples;
        }
    }

    if ((g_driver.init_phase == InitPhase::SendingCommand ||
         g_driver.init_phase == InitPhase::WaitingResponse) &&
        g_driver.transfer_deadline_us != 0 &&
        now >= g_driver.transfer_deadline_us) {
        const uint8_t endpoint =
            g_driver.init_phase == InitPhase::SendingCommand
                ? g_driver.bulk_out_ep
                : g_driver.bulk_in_ep;
        const bool aborted =
            endpoint != 0 && tuh_edpt_abort_xfer(g_driver.dev_addr, endpoint);
        ++g_driver.init_failures;
        g_driver.reports_running = false;
        g_driver.init_phase = InitPhase::RetryWait;
        g_driver.transfer_deadline_us = 0;
        g_driver.retry_at_us = now + kInitRetryDelayUs;
        bridge::clear_input();
        std::printf(
            "[PRO2_INIT] timeout step=%u ep=%02x abort=%u; restart_from_step_1_ms=500\n",
            static_cast<unsigned>(g_driver.init_index + 1), endpoint, aborted);
    }

    if (g_driver.init_phase == InitPhase::RetryWait && now >= g_driver.retry_at_us) {
        g_driver.init_phase = InitPhase::Idle;
        maybe_start_initialization();
    }

    if (g_driver.reports_running && g_driver.dev_addr != 0 &&
        g_driver.hid_instance != 0xff &&
        g_source_recovery.snapshot().phase ==
            source_recovery::Phase::Monitoring &&
        tuh_hid_receive_ready(g_driver.dev_addr, g_driver.hid_instance) &&
        !tuh_hid_receive_report(g_driver.dev_addr, g_driver.hid_instance)) {
        std::printf("[USB_INPUT] receive recovery submit failed\n");
    }

    const source_recovery::Action recovery_action = g_source_recovery.tick(
        now, g_driver.device_connected && g_driver.hid_ready,
        g_driver.reports_running, g_driver.reports_started_us,
        g_driver.last_report_us);
    if (recovery_action == source_recovery::Action::AbortInput) {
        bridge::clear_input();
        const bool aborted = tuh_edpt_abort_xfer(
            g_driver.dev_addr, pro2::kHidInEndpoint);
        std::printf(
            "[USB_SOURCE_RECOVERY] stale_input action=abort_hid_in "
            "ep=%02x result=%u\n",
            pro2::kHidInEndpoint, aborted);
    } else if (recovery_action == source_recovery::Action::SubmitReceive) {
        const bool ready = tuh_hid_receive_ready(
            g_driver.dev_addr, g_driver.hid_instance);
        const bool submitted = ready && tuh_hid_receive_report(
            g_driver.dev_addr, g_driver.hid_instance);
        std::printf(
            "[USB_SOURCE_RECOVERY] action=rearm_hid_in ready=%u "
            "submitted=%u grace_ms=1000\n",
            ready, submitted);
    } else if (recovery_action == source_recovery::Action::BeginBusReset) {
        const bool reset = tuh_rhport_reset_bus(BOARD_TUH_RHPORT, true);
        std::printf(
            "[USB_SOURCE_RECOVERY] action=begin_bus_reset result=%u "
            "pulse_ms=20\n",
            reset);
    } else if (recovery_action == source_recovery::Action::EndBusReset) {
        const bool reset = tuh_rhport_reset_bus(BOARD_TUH_RHPORT, false);
        std::printf(
            "[USB_SOURCE_RECOVERY] action=end_bus_reset result=%u "
            "grace_ms=2000\n",
            reset);
    }

    if (g_driver.rumble_active && g_driver.rumble_deadline_us > 0 &&
        now >= g_driver.rumble_deadline_us) {
        g_driver.rumble_active = false;
        g_driver.rumble_command = {};
        g_driver.rumble_stop_reports_remaining = kRumbleStopReports;
        g_driver.next_rumble_report_us = now;
        std::printf("[PRO2_RUMBLE] command expired; stop_reports=%u\n",
                    kRumbleStopReports);
    }

    const bool rumble_report_due =
        g_driver.rumble_active || g_driver.rumble_stop_reports_remaining > 0;
    if (g_driver.reports_running && g_driver.dev_addr != 0 &&
        g_driver.hid_instance != 0xff && rumble_report_due &&
        !g_driver.rumble_transfer_pending &&
        now >= g_driver.next_rumble_report_us) {
        const pro2_rumble::XboxRumbleCommand command =
            g_driver.rumble_active ? g_driver.rumble_command
                                   : pro2_rumble::XboxRumbleCommand{};
        g_rumble_report =
            pro2_rumble::build_report(g_driver.rumble_counter, command);

        // Control transfers are asynchronous. Arm the flag before submission
        // so an implementation that completes inline cannot leave it stuck.
        g_driver.rumble_transfer_pending = true;
        g_driver.rumble_transfer_started_us = now;
        const bool submitted = tuh_hid_send_report(
            g_driver.dev_addr, g_driver.hid_instance,
            pro2_rumble::kReportId, g_rumble_report.data() + 1,
            static_cast<uint16_t>(g_rumble_report.size() - 1));
        if (!submitted) {
            g_driver.rumble_transfer_pending = false;
            g_driver.rumble_transfer_started_us = 0;
            ++g_driver.rumble_submit_errors;
            g_driver.next_rumble_report_us = now + 2000;
        } else {
            g_driver.rumble_counter =
                static_cast<uint8_t>((g_driver.rumble_counter + 1) & 0x0f);
            g_driver.next_rumble_report_us = now + kRumblePeriodUs;
            if (!g_driver.rumble_active &&
                g_driver.rumble_stop_reports_remaining > 0) {
                --g_driver.rumble_stop_reports_remaining;
                ++g_driver.rumble_stop_reports;
            }
        }
    }

    if (now - g_driver.last_health_us >= kHealthPeriodUs) {
        const float elapsed =
            static_cast<float>(now - g_driver.last_health_us) / 1000000.0f;
        const float source_hz = g_driver.health_window_reports / elapsed;
        const float age_ms =
            g_driver.last_report_us > 0 && now >= g_driver.last_report_us
                ? static_cast<float>(now - g_driver.last_report_us) / 1000.0f
                : -1.0f;
        const bool dp = !gpio_get(kUsbDpPin);
        const bool dm = !gpio_get(kUsbDmPin);
        const char *line_now =
            dp && dm ? "SE1"
                     : dp ? "FS_J"
                          : dm ? "LS_J"
                               : "SE0";
        std::printf(
            "[USB_HEALTH] connected=%u hid=%u vendor=%u reports=%u source_hz=%.1f "
            "age_ms=%.1f parsed=%lu rejected=%lu duplicates=%lu init_failures=%lu "
            "recovery={phase:%s,incidents:%lu,aborts:%lu,rearms:%lu,bus_resets:%lu,recovered:%lu}\n",
            g_driver.device_connected, g_driver.hid_ready, g_driver.vendor_ready,
            g_driver.reports_running, source_hz, age_ms,
            static_cast<unsigned long>(g_driver.parsed_reports),
            static_cast<unsigned long>(g_driver.rejected_reports),
            static_cast<unsigned long>(g_driver.duplicate_sequences),
            static_cast<unsigned long>(g_driver.init_failures),
            source_recovery::Controller::phase_name(
                g_source_recovery.snapshot().phase),
            static_cast<unsigned long>(g_source_recovery.snapshot().incidents),
            static_cast<unsigned long>(g_source_recovery.snapshot().abort_attempts),
            static_cast<unsigned long>(g_source_recovery.snapshot().receive_rearms),
            static_cast<unsigned long>(g_source_recovery.snapshot().bus_resets),
            static_cast<unsigned long>(g_source_recovery.snapshot().recoveries));
        if (g_driver.device_connected &&
            (g_driver.line_se0_samples > 50 ||
             g_driver.line_ls_j_samples > 50 ||
             g_driver.line_se1_samples > 0)) {
            std::printf(
                "[USB_PHY_WARN] dp=%u dm=%u now=%s "
                "samples_15s={SE0:%lu,FS_J:%lu,LS_J:%lu,SE1:%lu}\n",
                dp, dm, line_now,
                static_cast<unsigned long>(
                    g_driver.line_se0_samples),
                static_cast<unsigned long>(
                    g_driver.line_fs_j_samples),
                static_cast<unsigned long>(
                    g_driver.line_ls_j_samples),
                static_cast<unsigned long>(
                    g_driver.line_se1_samples));
        }
        const float rumble_pending_age_ms =
            g_driver.rumble_transfer_pending &&
                    g_driver.rumble_transfer_started_us > 0 &&
                    now >= g_driver.rumble_transfer_started_us
                ? static_cast<float>(
                      now - g_driver.rumble_transfer_started_us) /
                      1000.0f
                : 0.0f;
        const float rumble_update_age_ms =
            g_driver.last_rumble_update_us > 0 &&
                    now >= g_driver.last_rumble_update_us
                ? static_cast<float>(now - g_driver.last_rumble_update_us) /
                      1000.0f
                : -1.0f;
        const float rumble_change_age_ms =
            g_driver.last_rumble_change_us > 0 &&
                    now >= g_driver.last_rumble_change_us
                ? static_cast<float>(
                      now - g_driver.last_rumble_change_us) /
                      1000.0f
                : -1.0f;
        const bool recent_rumble_update =
            rumble_change_age_ms >= 0.0f &&
            rumble_change_age_ms <=
                static_cast<float>(kHealthPeriodUs) / 1000.0f;
        const bool new_rumble_error =
            g_driver.rumble_submit_errors !=
                g_driver.logged_rumble_submit_errors ||
            g_driver.rumble_transfer_errors !=
                g_driver.logged_rumble_transfer_errors;
        if (g_driver.rumble_active ||
            g_driver.rumble_transfer_pending ||
            recent_rumble_update || new_rumble_error) {
            std::printf(
                "[PRO2_RUMBLE_HEALTH] active=%u pending=%u "
                "pending_age_ms=%.1f last_update_age_ms=%.1f "
                "last_change_age_ms=%.1f "
                "updates=%lu suppressed=%lu reports=%lu stops=%lu "
                "coalesced=%lu submit_errors=%lu transfer_errors=%lu "
                "transport=hid_interrupt_out_02 period_ms=12\n",
                g_driver.rumble_active,
                g_driver.rumble_transfer_pending,
                rumble_pending_age_ms, rumble_update_age_ms,
                rumble_change_age_ms,
                static_cast<unsigned long>(
                    g_driver.rumble_updates),
                static_cast<unsigned long>(
                    g_driver.rumble_suppressed_updates),
                static_cast<unsigned long>(
                    g_driver.rumble_reports),
                static_cast<unsigned long>(
                    g_driver.rumble_stop_reports),
                static_cast<unsigned long>(
                    g_driver.rumble_coalesced_updates),
                static_cast<unsigned long>(
                    g_driver.rumble_submit_errors),
                static_cast<unsigned long>(
                    g_driver.rumble_transfer_errors));
        }
        g_driver.logged_rumble_submit_errors =
            g_driver.rumble_submit_errors;
        g_driver.logged_rumble_transfer_errors =
            g_driver.rumble_transfer_errors;
        g_driver.last_health_us = now;
        g_driver.health_window_reports = 0;
        g_driver.line_se0_samples = 0;
        g_driver.line_fs_j_samples = 0;
        g_driver.line_ls_j_samples = 0;
        g_driver.line_se1_samples = 0;
    }
}

void set_rumble_internal(const pro2_rumble::XboxRumbleCommand &command,
                         uint64_t hold_us) {
    const uint64_t now = time_us_64();
    const bool command_active = pro2_rumble::active(command);
    ++g_driver.rumble_updates;
    g_driver.last_rumble_update_us = now;
    if (pro2_rumble::equivalent(g_driver.rumble_command, command)) {
        ++g_driver.rumble_suppressed_updates;
        if (command_active) {
            g_driver.rumble_active = true;
            g_driver.rumble_deadline_us = hold_us > 0 ? now + hold_us : 0;
        }
        return;
    }
    if (g_driver.rumble_transfer_pending) {
        ++g_driver.rumble_coalesced_updates;
    }
    g_driver.rumble_command = command;
    g_driver.last_rumble_change_us = now;
    g_driver.rumble_active = command_active;
    if (g_driver.rumble_trace_count < 12) {
        std::printf(
            "[PRO2_RUMBLE_CMD] active=%u mask=%02x weak=%u strong=%u "
            "left_trigger=%u right_trigger=%u reports_running=%u\n",
            command_active, command.enabled_mask, command.weak,
            command.strong, command.left_trigger, command.right_trigger,
            g_driver.reports_running);
        ++g_driver.rumble_trace_count;
    }
    g_driver.rumble_deadline_us = g_driver.rumble_active && hold_us > 0
                                     ? now + hold_us
                                     : 0;
    g_driver.rumble_stop_reports_remaining =
        g_driver.rumble_active ? 0 : kRumbleStopReports;
    g_driver.next_rumble_report_us = now;
}

void set_rumble(const pro2_rumble::XboxRumbleCommand &command) {
    set_rumble_internal(command, 0);
}

void pulse_rumble(const pro2_rumble::XboxRumbleCommand &command,
                  uint32_t duration_ms) {
    set_rumble_internal(command,
                        static_cast<uint64_t>(duration_ms) * 1000u);
}

void stop_rumble() {
    pro2_rumble::XboxRumbleCommand stop;
    stop.enabled_mask = 0x0f;
    set_rumble(stop);
}

Telemetry telemetry() {
    Telemetry result;
    result.device_connected = g_driver.device_connected;
    result.hid_ready = g_driver.hid_ready;
    result.vendor_ready = g_driver.vendor_ready;
    result.reports_running = g_driver.reports_running;
    result.parsed_reports = g_driver.parsed_reports;
    result.rejected_reports = g_driver.rejected_reports;
    result.duplicate_sequences = g_driver.duplicate_sequences;
    result.disconnects = g_driver.disconnects;
    result.init_failures = g_driver.init_failures;
    const source_recovery::Snapshot recovery = g_source_recovery.snapshot();
    result.source_recovery_incidents = recovery.incidents;
    result.source_recovery_aborts = recovery.abort_attempts;
    result.source_recovery_rearms = recovery.receive_rearms;
    result.source_recovery_bus_resets = recovery.bus_resets;
    result.source_recoveries = recovery.recoveries;
    result.source_recovery_phase =
        source_recovery::Controller::phase_name(recovery.phase);
    result.rumble_updates = g_driver.rumble_updates;
    result.rumble_reports = g_driver.rumble_reports;
    result.rumble_submit_errors = g_driver.rumble_submit_errors;
    result.rumble_transfer_errors = g_driver.rumble_transfer_errors;
    result.rumble_active = g_driver.rumble_active;
    result.rumble_transfer_pending = g_driver.rumble_transfer_pending;
    result.rumble_weak = g_driver.rumble_command.weak;
    result.rumble_strong = g_driver.rumble_command.strong;
    result.last_report_us = g_driver.last_report_us;
    return result;
}

} // namespace pro2_host

/* An isolated host for the original NX Macro Controller btkeyLib Windows port.
 * The selected adapter, user controls and the metadata observer are the only
 * additions. Upstream owns all SDP, HID, timing, pairing and link policy. */
#include <windows.h>
#include <io.h>
#include <stdio.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdarg.h>
#include <wchar.h>
#include "btstack_stdin_windows.h"
#include "btkeyLib.h"
#include "comparison_target.h"
#ifdef COMPARISON_REALTEK
#include "relay_stdio.h"
#include "relay_nfc.h"
#include "classic/hid_device.h"
#include <mmsystem.h>
#endif

static bool interactive;
static void init_comparison_stdin(void) {
    if (interactive) btstack_stdin_windows_init();
}

int comparison_windows_main(int argc, const char *argv[]);
#define main comparison_windows_main
#define btstack_main comparison_start
#define btstack_stdin_windows_init init_comparison_stdin
#include "main.c"
#undef main
#undef btstack_main
#undef btstack_stdin_windows_init

int btstack_main(int argc, const char *argv[]);
int comparison_matches_path(const char *path);
void comparison_usb_open(int succeeded, DWORD error);

enum { CMD_A, CMD_LEFT, CMD_RIGHT, CMD_LR, CMD_HOME, CMD_STOP, COMMANDS };
static const struct { const wchar_t *name; uint32_t mask; } controls[COMMANDS] = {
    {L"a", 0x08}, {L"left", 0x080000}, {L"right", 0x040000},
    {L"lr", 0x400040}, {L"home", 0x001000}, {L"stop", 0}
};
static HANDLE control_events[COMMANDS];
static FILE *report, *display;
static btstack_timer_source_t timer;
static btstack_packet_callback_registration_t state_callback;
static uint64_t started, connected_at, active_at, ended_at, release_at, stop_at, next_status;
static uint16_t link_handle = UINT16_MAX, local_interrupt, peer_interrupt;
static unsigned reports, host_output, reports_at_active, max_report_gap, last_mask = UINT32_MAX;
static uint64_t last_report;
static bool stack_ready, was_sniff, handshake, remote_close, had_connection;
static bool key_query, key_reply, pc_disconnect;
static bool post_active_left, post_active_right;
static int outcome = 9;
static bool relay_stdio_enabled;
static bool relay_home_probe;
#ifdef COMPARISON_REALTEK
static bool timer_period_requested;
static bool relay_rumble_enabled;
static const uint8_t relay_rumble_stop[8] = {0,1,0x40,0x40,0,1,0x40,0x40};

static const uint8_t *relay_rumble_report(const uint8_t *hid, unsigned size) {
    if (size < 10 || (hid[0] != 0x01 && hid[0] != 0x10 && hid[0] != 0x11)) return NULL;
    bool was_enabled = relay_rumble_enabled;
    if (hid[0] == 0x01 && size >= 12 && hid[10] == 0x48 && hid[11] <= 1) {
        relay_rumble_enabled = hid[11] != 0;
        if (!relay_rumble_enabled) return relay_rumble_stop;
    }
    return was_enabled ? hid + 2 : NULL;
}
static uint64_t radio_ready_at;
static bool version_requested, version_received, firmware_verified;
static bd_addr_t connected_peer;
static bd_addr_t saved_receiver;
enum { SAVED_PAGE_MAX_ATTEMPTS = 3, INCOMING_QUIET_MS = 8000 };
struct saved_page_policy {
    unsigned attempts;
    bool selected, pending, finished;
    uint64_t next_at;
};
static struct saved_page_policy saved_page;
static uint64_t incoming_request_at;
#endif

enum { HOME_DELAY_MS = 5000, BUTTON_PULSE_MS = 180, HOME_OBSERVATION_MS = 20000,
       RELAY_HOME_OBSERVATION_MS = 60000 };
static struct {
    bool enabled;
    uint64_t paired_at, requested_at, pressed_report_at, release_requested_at, released_report_at;
    unsigned reports_before, host_output_before;
    unsigned long long physical_frames_before;
} home_test;

static uint64_t now_ms(void) { return GetTickCount64(); }

static void note(const char *fmt, ...) {
    va_list args;
    FILE *targets[] = {report, display};
    for (unsigned n = 0; n < 2; n++) {
        if (!targets[n]) continue;
        fprintf(targets[n], "%llu ms  ", (unsigned long long)(now_ms() - started));
        va_start(args, fmt);
        vfprintf(targets[n], fmt, args);
        va_end(args);
        fputc('\n', targets[n]);
        fflush(targets[n]);
    }
}

void comparison_usb_open(int succeeded, DWORD error) {
    note("TARGET_OPEN instance=%s ok=%d win32=%lu", comparison_target_label(), succeeded, error);
}

static uint16_t le16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }

#ifdef COMPARISON_REALTEK
void relay_nfc_event(const char *event, unsigned gen, const char *fields) {
    note("%s generation=%u %s",event,gen,fields);
}
static void release_timer_period(void) {
    if (!timer_period_requested) return;
    MMRESULT status = timeEndPeriod(1);
    timer_period_requested = false;
    note("RELAY_TIMER_RESOLUTION released_ms=1 status=%u", (unsigned)status);
}

static void peer_file(char path[64]) {
    bd_addr_t own;
    gap_local_bd_addr(own);
    snprintf(path, 64, "btstack/switch-%02x%02x%02x%02x%02x%02x.addr",
             own[0], own[1], own[2], own[3], own[4], own[5]);
}

static void remember_handshake_peer(void) {
    char path[64], tmp[72];
    peer_file(path);
    snprintf(tmp, sizeof(tmp), "%s.tmp", path);
    FILE *f = fopen(tmp, "wb");
    if (!f) return;
    bool written = fwrite(connected_peer, 1, 6, f) == 6;
    if (fclose(f) == 0 && written && MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING)) return;
    DeleteFileA(tmp);
}

/* Migration for a user-selected, private, previously successful test session
 * made before the per-radio last-receiver address was saved. */
static bool prior_home_receiver(bd_addr_t receiver) {
    const char *opt = getenv("PRO2_REALTEK_RESUME_HOME_SESSION");
    if (!opt || strcmp(opt, "1") || strcmp(comparison_target_label(),COMPARISON_TARGET_LABEL)) return false;
    FILE *session = fopen("../comparison.log", "rb");
    if (!session) return false;
    char line[1024];
    bool same_radio = fgets(line, sizeof(line), session) &&
        strstr(line, "START upstream=4b49cb955d08dc80a82673083fc0463cbc0c2540 instance=" COMPARISON_TARGET_LABEL " ");
    bool passed = false;
    while (same_radio && fgets(line, sizeof(line), session)) {
        if (strstr(line, " END reason=home_transport_observed_20s paired_seen=1 initial_link_down=0 peer_closed_interrupt=0 "))
            passed = true;
    }
    fclose(session);
    if (!passed) return false;
    btstack_link_key_iterator_t it;
    if (!gap_link_key_iterator_init(&it)) return false;
    link_key_type_t type;
    link_key_t scratch = {0};
    bd_addr_t another;
    bool single = gap_link_key_iterator_get_next(&it, receiver, scratch, &type) &&
                 !gap_link_key_iterator_get_next(&it, another, scratch, &type);
    SecureZeroMemory(scratch, sizeof(scratch));
    gap_link_key_iterator_done(&it);
    return single;
}

static const char *saved_bond_source;
static bool select_saved_receiver(bd_addr_t receiver) {
    char path[64];
    peer_file(path);
    FILE *f = fopen(path, "rb");
    if (f) {
        bool valid = fread(receiver, 1, sizeof(bd_addr_t), f) == sizeof(bd_addr_t) && fgetc(f) == EOF;
        fclose(f);
        if (valid) saved_bond_source = "saved_handshake";
        return valid;
    }
    if (!prior_home_receiver(receiver)) return false;
    saved_bond_source = "prior_home_proof";
    return true;
}

static bool page_due(const struct saved_page_policy *state, uint64_t now, uint64_t last_incoming) {
    return !state->pending && !state->finished && state->attempts < SAVED_PAGE_MAX_ATTEMPTS &&
           now >= state->next_at && (!last_incoming || now - last_incoming >= INCOMING_QUIET_MS);
}

static bool page_completed(struct saved_page_policy *state, bool same_peer, uint8_t status, uint64_t now) {
    if (!same_peer || !state->pending) return false;
    state->pending = false;
    if (status != ERROR_CODE_PAGE_TIMEOUT || state->attempts >= SAVED_PAGE_MAX_ATTEMPTS) {
        state->finished = true;
        state->next_at = 0;
    } else {
        state->next_at = now + (state->attempts == 1 ? 4000 : 10000);
    }
    return true;
}

static bool saved_page_fixture(void) {
    struct saved_page_policy s = {.attempts=1, .pending=true};
    if (page_completed(&s, false, ERROR_CODE_PAGE_TIMEOUT, 10000) || !s.pending ||
        !page_completed(&s, true, ERROR_CODE_PAGE_TIMEOUT, 10000) ||
        page_due(&s, 13999, 0) || !page_due(&s, 14000, 0) || page_due(&s, 14000, 13000)) return false;
    s.pending=true; s.attempts++;
    if (!page_completed(&s, true, ERROR_CODE_PAGE_TIMEOUT, 21000) || page_due(&s, 30999, 0) ||
        !page_due(&s, 31000, 21000)) return false;
    s.pending=true; s.attempts++;
    if (!page_completed(&s, true, ERROR_CODE_PAGE_TIMEOUT, 41000) || page_due(&s, 99999, 0)) return false;
    s = (struct saved_page_policy){.attempts=1, .pending=true};
    if (!page_completed(&s, true, 0x05, 999) || !s.finished) return false;
    s = (struct saved_page_policy){.attempts=1, .pending=true};
    return page_completed(&s, true, ERROR_CODE_SUCCESS, 999) && s.finished;
}

static void try_saved_receiver(void) {
    if (!saved_page.selected) {
        saved_page.selected = select_saved_receiver(saved_receiver);
        if (!saved_page.selected) { saved_page.finished = true; return; }
    }
    if (hci_connection_for_bd_addr_and_type(saved_receiver, BD_ADDR_TYPE_ACL)) return;
    link_key_t key = {0};
    link_key_type_t type;
    bool bonded = gap_get_link_key_for_bd_addr(saved_receiver, key, &type);
    SecureZeroMemory(key, sizeof(key));
    if (!bonded) { saved_page.finished = true; return; }
    uint16_t cid = 0;
    ++saved_page.attempts;
    saved_page.pending = true;
    uint8_t status = hid_device_connect(saved_receiver, &cid);
    if (status) { saved_page.finished = true; saved_page.pending = false; }
    note("SAVED_RECEIVER_CONNECT status=0x%02x attempt=%u limit=%d bond_source=%s",
         status, saved_page.attempts, SAVED_PAGE_MAX_ATTEMPTS, saved_bond_source);
}
#endif

static bool home_request_due(uint64_t t, bool paired) {
#ifdef COMPARISON_REALTEK
    if (!firmware_verified) return false;
#endif
    return home_test.enabled && paired && home_test.paired_at && !home_test.requested_at &&
           link_handle != UINT16_MAX && !release_at && !ended_at && !remote_close &&
           t - home_test.paired_at >= HOME_DELAY_MS;
}

static void observe_home_report(uint64_t t, unsigned mask) {
    if (!home_test.requested_at || stop_at) return;
    bool pressed = relay_home_probe ? (mask & controls[CMD_HOME].mask) != 0 : mask == controls[CMD_HOME].mask;
    bool released = relay_home_probe ? (mask & controls[CMD_HOME].mask) == 0 : mask == 0;
    if (!home_test.pressed_report_at && pressed) {
        home_test.pressed_report_at = t;
        note("HOME_TEST_REPORT pressed=1 elapsed_ms=%llu stage=outgoing_acl_observer",
             (unsigned long long)(t - home_test.requested_at));
    } else if (home_test.pressed_report_at && home_test.release_requested_at &&
               !home_test.released_report_at && released) {
        home_test.released_report_at = t;
        note("HOME_TEST_REPORT pressed=0 elapsed_ms=%llu stage=outgoing_acl_observer",
             (unsigned long long)(t - home_test.requested_at));
    }
}

static void observe_signal(bool incoming, const uint8_t *p, unsigned size) {
    while (size >= 4) {
        unsigned code = p[0], len = le16(p + 2);
        if (len > size - 4) return;
        const uint8_t *data = p + 4;
        if (code == 2 && len == 4) {
            unsigned psm = le16(data), scid = le16(data + 2);
            note("CHANNEL_REQUEST from=%s psm=0x%02x", incoming ? "peer" : "pc", psm);
            if (psm == 0x13) {
                if (incoming) peer_interrupt = scid; else local_interrupt = scid;
            }
        } else if (code == 3 && len == 8) {
            uint16_t dcid = le16(data), scid = le16(data + 2);
            if (le16(data + 4) == 0) {
                if (incoming && scid == local_interrupt) peer_interrupt = dcid;
                if (!incoming && scid == peer_interrupt) local_interrupt = dcid;
            }
        } else if (code == 6 && len == 4) {
            uint16_t dcid = le16(data), scid = le16(data + 2);
            bool is_interrupt = incoming ? (local_interrupt && dcid == local_interrupt)
                                         : (peer_interrupt && dcid == peer_interrupt);
            note("CHANNEL_CLOSE_REQUEST from=%s dcid=%u scid=%u hid_interrupt=%d since_active_ms=%lld",
                 incoming ? "peer" : "pc", dcid, scid, is_interrupt,
                 active_at ? (long long)(now_ms() - active_at) : -1LL);
            if (incoming && is_interrupt && !stop_at) remote_close = true;
        }
        p += 4 + len;
        size -= 4 + len;
    }
}

static void observe_packet(uint8_t type, uint8_t incoming, uint8_t *p, uint16_t size) {
    if (type == HCI_COMMAND_DATA_PACKET && !incoming && size >= 3) {
        if (le16(p) == 0x1408 && size == 5 && le16(p + 3) == link_handle) {
            key_query = true;
            note("READ_ENCRYPTION_KEY_SIZE requested=1");
        }
        if (le16(p) == 0x0406 && size == 6 && le16(p + 3) == link_handle) {
            pc_disconnect = true;
            note("PC_HCI_DISCONNECT reason=0x%02x cleanup=%d", p[5], stop_at != 0);
        }
        if (le16(p) == 0x0c1a && size == 4) note("SCAN_ENABLE requested=%u", p[3]);
        return;
    }
    if (type == HCI_EVENT_PACKET && incoming && size >= 2 && size >= 2u + p[1]) {
        size = (uint16_t)(2u + p[1]);
#ifdef COMPARISON_REALTEK
        if (version_requested && !version_received && p[0] == HCI_EVENT_COMMAND_COMPLETE && size >= 5 &&
            le16(p + 3) == HCI_OPCODE_HCI_READ_LOCAL_VERSION_INFORMATION) {
            int status = size >= 6 ? p[5] : -1;
            int manufacturer = size >= 14 ? le16(p + 10) : -1;
            int subversion = size >= 14 ? le16(p + 12) : -1;
            version_received = true;
            firmware_verified = status == 0 && size >= 14 && (comparison_uses_0852_firmware() ?
                manufacturer == 93 && subversion != 0x8852 : manufacturer != 93 && manufacturer != 0xffff);
            note(comparison_uses_0852_firmware() ?
                 "REALTEK_FIRMWARE_READBACK status=%d bytes=%u manufacturer=%d lmp_subversion=%d verified=%d" :
                 "STANDARD_HCI_READBACK status=%d bytes=%u manufacturer=%d lmp_subversion=%d verified=%d",
                 status, size, manufacturer, subversion, firmware_verified);
        }
#endif
#ifdef COMPARISON_REALTEK
        if (p[0] == HCI_EVENT_CONNECTION_REQUEST && size >= 12 && p[11] == 1) {
            incoming_request_at = now_ms();
            bd_addr_t incoming_peer;
            hci_event_connection_request_get_bd_addr(p, incoming_peer);
            if (saved_page.pending && bd_addr_cmp(incoming_peer, saved_receiver) == 0) {
                saved_page.pending = false;
                saved_page.finished = true;
                note("SAVED_RECEIVER_YIELD attempt=%u reason=peer_initiated_connection", saved_page.attempts);
            }
        }
#endif
        if (p[0] == HCI_EVENT_CONNECTION_COMPLETE && size >= 13 && p[11] == 1) {
            note("CLASSIC_ACL status=0x%02x", p[2]);
#ifdef COMPARISON_REALTEK
            bd_addr_t completed_peer;
            hci_event_connection_complete_get_bd_addr(p, completed_peer);
            uint64_t event_at = now_ms();
            if (page_completed(&saved_page, bd_addr_cmp(completed_peer, saved_receiver) == 0, p[2], event_at)) {
                note("SAVED_RECEIVER_PAGE_RESULT attempt=%u status=0x%02x retry_scheduled=%d next_in_ms=%llu exhausted=%d",
                     saved_page.attempts, p[2], !saved_page.finished,
                     (unsigned long long)(saved_page.finished ? 0 : saved_page.next_at - event_at),
                     saved_page.attempts == SAVED_PAGE_MAX_ATTEMPTS && p[2] == ERROR_CODE_PAGE_TIMEOUT);
            }
#endif
            if (p[2] == 0 && !had_connection) {
                link_handle = le16(p + 3);
#ifdef COMPARISON_REALTEK
                relay_rumble_enabled = false;
#endif
                had_connection = true;
                connected_at = now_ms();
#ifdef COMPARISON_REALTEK
                hci_event_connection_complete_get_bd_addr(p, connected_peer);
#endif
            }
        } else if (p[0] == HCI_EVENT_COMMAND_STATUS && size >= 6 &&
                   (le16(p + 4) == 0x1408 || le16(p + 4) == 0x0406)) {
            note("COMMAND_STATUS opcode=0x%04x status=0x%02x credits=%u", le16(p + 4), p[2], p[3]);
        } else if (p[0] == HCI_EVENT_COMMAND_COMPLETE && size >= 6 && le16(p + 3) == 0x0c1a) {
            note("SCAN_ENABLE status=0x%02x", p[5]);
        } else if (p[0] == HCI_EVENT_COMMAND_COMPLETE && size >= 6 && le16(p + 3) == 0x1408) {
            key_reply = true;
            note("READ_ENCRYPTION_KEY_SIZE status=0x%02x octets=%d", p[5],
                 !p[5] && size >= 9 ? p[8] : -1);
        } else if (p[0] == HCI_EVENT_AUTHENTICATION_COMPLETE && size >= 5 && le16(p + 3) == link_handle) {
            note("AUTH_COMPLETE status=0x%02x", p[2]);
        } else if (p[0] == HCI_EVENT_ENCRYPTION_CHANGE_V2 && size >= 7 && le16(p + 3) == link_handle) {
            note("ENCRYPTION_V2 status=0x%02x enabled=%u octets=%u", p[2], p[5], p[6]);
        } else if (p[0] == HCI_EVENT_ENCRYPTION_CHANGE && size >= 6 && le16(p + 3) == link_handle) {
            note("ENCRYPTION status=0x%02x enabled=%u", p[2], p[5]);
        } else if (p[0] == HCI_EVENT_MODE_CHANGE && size >= 8 && le16(p + 3) == link_handle) {
            note("LINK_MODE status=0x%02x mode=%u interval_slots=%u", p[2], p[5], le16(p + 6));
            if (!p[2] && p[5] == 2) was_sniff = true;
            if (!p[2] && !p[5] && was_sniff && !active_at) {
                active_at = now_ms();
                reports_at_active = reports;
            }
        } else if (p[0] == HCI_EVENT_DISCONNECTION_COMPLETE && size >= 6 && le16(p + 3) == link_handle) {
            note("ACL_DOWN status=0x%02x reason=0x%02x shutdown_already_requested=%d", p[2], p[5], stop_at != 0);
            ended_at = now_ms();
            link_handle = UINT16_MAX;
        }
        return;
    }
#ifdef COMPARISON_REALTEK
    if (relay_stdio_enabled && type == HCI_ACL_DATA_PACKET && !incoming)
        relay_nfc_observe_acl(p,size,link_handle,peer_interrupt);
#endif
    if (type != HCI_ACL_DATA_PACKET || size < 8 || (le16(p) & 0xfff) != link_handle ||
            (le16(p) & 0x3000) == 0x1000 || le16(p + 2) != size - 4) return;
    unsigned len = le16(p + 4), cid = le16(p + 6);
#ifdef COMPARISON_REALTEK
    if (relay_stdio_enabled && size >= 15 && len >= 7 && !incoming &&
        peer_interrupt && cid == peer_interrupt && p[8] == 0xa1 && (p[9] == 0x30 || p[9] == 0x31))
        relay_stdio_observe_input(p[12] | ((uint32_t)p[13] << 8) | ((uint32_t)p[14] << 16));
#endif
    if (cid == 1 && len == size - 8) {
        observe_signal(incoming != 0, p + 8, len);
    } else if (size >= 10 && len >= 2 && incoming && local_interrupt && cid == local_interrupt && p[8] == 0xa2) {
        ++host_output;
#ifdef COMPARISON_REALTEK
        if (relay_stdio_enabled && display && len == size - 8) {
            const uint8_t *rumble = relay_rumble_report(p + 9, len - 1);
            if (!rumble) return;
            fprintf(display, "UPSTREAM_RUMBLE hex=%02x%02x%02x%02x%02x%02x%02x%02x\n",
                    rumble[0], rumble[1], rumble[2], rumble[3], rumble[4], rumble[5], rumble[6], rumble[7]);
            fflush(display);
        }
#endif
    } else if (size >= 15 && len >= 7 && !incoming && peer_interrupt && cid == peer_interrupt &&
               p[8] == 0xa1 && p[9] == 0x30) {
        uint64_t t = now_ms();
        if (last_report && t - last_report > max_report_gap) max_report_gap = (unsigned)(t - last_report);
        last_report = t;
        ++reports;
        unsigned mask = p[12] | (p[13] << 8) | (p[14] << 16);
        observe_home_report(t, mask);
        if (mask != last_mask) {
            note("INPUT_30_TO_TRANSPORT buttons=0x%06x", mask);
            last_mask = mask;
        }
        if (active_at && mask == controls[CMD_LEFT].mask) post_active_left = true;
        if (active_at && mask == controls[CMD_RIGHT].mask) post_active_right = true;
    }
}

static void ignore_message(int level, const char *fmt, va_list args) {
    (void)level; (void)fmt; (void)args;
}
static const hci_dump_t observer = {.log_packet = observe_packet, .log_message = ignore_message};

static void state_event(uint8_t type, uint16_t channel, uint8_t *p, uint16_t size) {
    (void)channel;
    if (type == HCI_EVENT_PACKET && size >= 6 && p[0] == GAP_EVENT_SECURITY_LEVEL &&
            gap_event_security_level_get_handle(p) == link_handle) {
        note("GAP_SECURITY level=%u status=0x%02x", gap_event_security_level_get_security_level(p), gap_event_security_level_get_status(p));
    }
    if (type == HCI_EVENT_PACKET && size >= 3 && p[0] == BTSTACK_EVENT_STATE &&
            p[2] == HCI_STATE_WORKING && !stack_ready) {
        stack_ready = true;
#ifdef COMPARISON_REALTEK
        radio_ready_at = now_ms();
#endif
        if (!hci_classic_supported()) {
            note("RADIO_CAPABILITY classic=0 reason=br_edr_required");
            return;
        }
        note("RADIO_READY name=Pro_Controller mode=classic_hid");
    }
}

static void finish(int code, const char *why) {
    if (stop_at) return;
    stop_at = now_ms();
    outcome = code;
#ifdef COMPARISON_REALTEK
    release_timer_period();
    if (relay_home_probe) relay_stdio_probe_home(false, stop_at);
#endif
    note("TRACE_SUMMARY key_length_requested=%d key_length_completion_seen=%d pc_disconnect_before_cleanup=%d",
         key_query, key_reply, pc_disconnect);
    if (home_test.enabled) {
        bool requested = home_test.requested_at != 0;
        note("HOME_TEST_RESULT requested=%d press_report=%d release_requested=%d release_report=%d "
             "elapsed_after_request_ms=%llu acl_up=%d hid_paired=%d peer_closed_interrupt=%d "
             "outgoing_reports_after_request=%u host_output_after_request=%u receiver_ui=not_observed_by_pc",
             requested, home_test.pressed_report_at != 0, home_test.release_requested_at != 0,
             home_test.released_report_at != 0, requested ? (unsigned long long)(stop_at - home_test.requested_at) : 0ULL,
             link_handle != UINT16_MAX, gamepad_paired(), remote_close,
             requested ? reports - home_test.reports_before : 0,
             requested ? host_output - home_test.host_output_before : 0);
    }
    note("END reason=%s paired_seen=%d initial_link_down=%d peer_closed_interrupt=%d reports30=%u peer_output=%u active_ms=%lld max_report_gap_ms=%u left_after_active=%d right_after_active=%d receiver_ui=not_observed_by_pc",
         why, handshake, ended_at != 0, remote_close, reports, host_output,
         active_at ? (long long)((ended_at ? ended_at : stop_at) - active_at) : -1LL,
         max_report_gap, post_active_left, post_active_right);
    send_button(0, 0);
    shutdown_gamepad();
}

static void command(int id) {
    if (id == CMD_STOP) { finish(6, "operator_stop"); return; }
    if (!gamepad_paired() || release_at || stop_at || ended_at) {
        note("BUTTON_IGNORED id=%d ready=%d pulse_busy=%d", id, gamepad_paired(), release_at != 0);
        return;
    }
    send_button(controls[id].mask, 0);
    release_at = now_ms() + BUTTON_PULSE_MS;
    note("BUTTON_REQUEST id=%d mask=0x%06x duration_ms=%d", id, controls[id].mask, BUTTON_PULSE_MS);
}

static void keyboard(char c) {
    if (home_test.enabled && c != 'q' && c != 'Q') return;
    switch (c) {
        case 'a': case 'A': command(CMD_A); break;
        case 'j': case 'J': command(CMD_LEFT); break;
        case 'l': case 'L': command(CMD_RIGHT); break;
        case 'p': case 'P': command(CMD_LR); break;
        case 'h': case 'H': command(CMD_HOME); break;
        case 'q': case 'Q': command(CMD_STOP); break;
    }
}

static void tick(btstack_timer_source_t *ts) {
    uint64_t t = now_ms();
    if (stop_at && t - stop_at >= 4000) {
        btstack_run_loop_trigger_exit();
        return;
    }
    if (!stop_at) {
#ifdef COMPARISON_REALTEK
        if (radio_ready_at && !hci_classic_supported()) {
            finish(24, "classic_hci_required");
            goto reschedule;
        }
        if (radio_ready_at && !version_requested && hci_can_send_command_packet_now()) {
            version_requested = true;
            if (hci_send_cmd(&hci_read_local_version_information)) {
                version_received = true;
                note("HCI_VERSION_READBACK command_submission_failed=1");
            }
        }
        if (version_received && !firmware_verified) {
            finish(7, comparison_uses_0852_firmware() ? "realtek_firmware_readback_failed" : "standard_hci_readback_failed");
            goto reschedule;
        }
        if (radio_ready_at && !version_received && t - radio_ready_at >= 6000) {
            finish(7, comparison_uses_0852_firmware() ? "realtek_firmware_readback_timeout" : "standard_hci_readback_timeout");
            goto reschedule;
        }
#endif
        bool paired = gamepad_paired();
#ifdef COMPARISON_REALTEK
        if (relay_stdio_enabled) {
            if (handshake && (remote_close || !paired)) {
                finish(3, "physical_relay_hid_ended");
                goto reschedule;
            }
            enum relay_pipe_state pipe_state = relay_stdio_tick(t, paired && firmware_verified && !ended_at && !remote_close);
            if (pipe_state != RELAY_PIPE_OPEN) {
                finish(pipe_state == RELAY_PIPE_CLOSED ? 0 : 8,
                       pipe_state == RELAY_PIPE_CLOSED ? "physical_input_closed" : "invalid_physical_input_pipe");
                goto reschedule;
            }
            if (firmware_verified && !had_connection && t - radio_ready_at >= 8000 &&
                page_due(&saved_page, t, incoming_request_at))
                try_saved_receiver();
        }
#endif
        if (paired && !handshake) {
            handshake = true;
#ifdef COMPARISON_REALTEK
            if (relay_stdio_enabled) {
                MMRESULT status = timeBeginPeriod(1);
                timer_period_requested = status == TIMERR_NOERROR;
                note("RELAY_TIMER_RESOLUTION requested_ms=1 status=%u", (unsigned)status);
            }
            remember_handshake_peer();
#endif
            note("UPSTREAM_GAMEPAD_PAIRED");
            if (home_test.enabled) {
                home_test.paired_at = t;
                note("HOME_TEST_ARMED delay_ms=%d", HOME_DELAY_MS);
            }
        }
        if (release_at && t >= release_at) {
            if (home_test.requested_at) home_test.release_requested_at = t;
#ifdef COMPARISON_REALTEK
            if (relay_home_probe) relay_stdio_probe_home(false, t);
            else
#endif
            send_button(0, 0);
            release_at = 0;
        }
        for (int i = 0; i < COMMANDS; ++i) {
            if (WaitForSingleObject(control_events[i], 0) == WAIT_OBJECT_0 &&
                ((!home_test.enabled && !relay_stdio_enabled) || i == CMD_STOP)) command(i);
        }
        unsigned observation_ms = relay_home_probe ? RELAY_HOME_OBSERVATION_MS : HOME_OBSERVATION_MS;
        if (!stop_at && home_request_due(t, paired)
#ifdef COMPARISON_REALTEK
                && (!relay_home_probe || relay_stdio_probe_home(true, t))
#endif
                ) {
            home_test.requested_at = t;
            home_test.reports_before = reports;
            home_test.host_output_before = host_output;
#ifdef COMPARISON_REALTEK
            if (relay_home_probe) home_test.physical_frames_before = relay_stdio_frames();
#endif
            note("HOME_TEST_REQUEST hold_ms=%d observation_ms=%u", BUTTON_PULSE_MS, observation_ms);
            if (relay_home_probe) release_at = t + BUTTON_PULSE_MS;
            else command(CMD_HOME);
        }
        uint64_t home_observation_start = home_test.pressed_report_at ?
            home_test.pressed_report_at : home_test.requested_at;
        if (ended_at && t - ended_at >= 1500) {
            finish(3, home_test.requested_at ? "acl_ended_after_home_request" : "initial_link_ended");
        } else if (!stack_ready && t - started >= 12000) finish(7, "radio_not_ready");
        else if (!had_connection && t - started >= 180000) finish(2, "no_peer");
        else if (home_observation_start && t >= home_observation_start &&
                 t - home_observation_start >= observation_ms) {
            if (home_test.pressed_report_at && home_test.released_report_at && paired && !remote_close &&
                link_handle != UINT16_MAX && last_report > home_test.released_report_at && t - last_report < 1000 &&
                (!relay_home_probe || host_output > home_test.host_output_before)
#ifdef COMPARISON_REALTEK
                && (!relay_home_probe || (relay_stdio_input_live(t) && relay_stdio_frames() > home_test.physical_frames_before))
#endif
                )
                finish(0, relay_home_probe ? "physical_relay_home_observed_60s" : "home_transport_observed_20s");
            else finish(4, remote_close ? "peer_closed_hid_after_home_request" : "home_transport_observation_incomplete");
        } else if (!home_test.enabled && !relay_stdio_enabled && active_at && t - active_at >= 60000 && paired && !remote_close &&
                 reports > reports_at_active && last_report && t - last_report < 500 &&
                 post_active_left && post_active_right) finish(0, "60s_after_active_input_observed_locally");
        else if (!relay_stdio_enabled && !home_observation_start && connected_at && t - connected_at >= 150000)
            finish(4, home_test.enabled ? "home_handshake_not_completed" : "observation_incomplete");
        if (!stop_at && t >= next_status) {
            next_status = t + 10000;
            note("STATUS radio_ready=%d acl=%d gamepad_paired=%d mode_return_seen=%d reports30=%u peer_output=%u", stack_ready,
                 link_handle != UINT16_MAX, gamepad_paired(), active_at != 0, reports, host_output);
#ifdef COMPARISON_REALTEK
            if (relay_stdio_enabled) note("RELAY_INPUT relay_frames=%llu edge_resyncs=%llu",
                relay_stdio_frames(), relay_stdio_coalesced_edges());
#endif
        }
    }
#ifdef COMPARISON_REALTEK
reschedule:
#endif
    btstack_run_loop_set_timer(ts, relay_stdio_enabled ? 4 : 50);
    btstack_run_loop_add_timer(ts);
}

int comparison_start(int argc, const char *argv[]) {
    hci_dump_init(&observer);
    state_callback.callback = state_event;
    hci_add_event_handler(&state_callback);
    btstack_run_loop_set_timer_handler(&timer, tick);
    btstack_run_loop_set_timer(&timer, 100);
    btstack_run_loop_add_timer(&timer);
    if (interactive) btstack_stdin_setup(keyboard);
    return btstack_main(argc, argv);
}

static void event_name(int id, wchar_t out[80]) {
    swprintf(out, 80, L"Local\\PRO2.UpstreamComparison.4b49cb9.%ls", controls[id].name);
}

static int send_command(const wchar_t *arg) {
    for (int i = 0; i < COMMANDS; ++i) {
        if (_wcsicmp(arg, controls[i].name)) continue;
        wchar_t name[80];
        event_name(i, name);
        HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
        if (!event) { fputs("No test process in this Windows session.\n", stderr); return 10; }
        BOOL signalled = SetEvent(event);
        CloseHandle(event);
        return signalled ? 0 : 11;
    }
    return 64;
}

static int check_fixture(void) {
#ifdef COMPARISON_REALTEK
    uint8_t vibration[] = {1, 0, 0xfc, 0x90, 0x11, 0x74, 0, 1, 0x40, 0x40, 0x48, 1};
    if (relay_rumble_report(vibration, sizeof(vibration)) != NULL || !relay_rumble_enabled) return 1;
    vibration[0] = 0x11;
    if (relay_rumble_report(vibration, 10) != vibration + 2) return 1;
    vibration[0] = 1; vibration[11] = 0;
    if (relay_rumble_report(vibration, sizeof(vibration)) != relay_rumble_stop || relay_rumble_enabled) return 1;
    vibration[0] = 0x10;
    if (relay_rumble_report(vibration, 10) != NULL) return 1;
#endif
    if (!comparison_matches_path(COMPARISON_USB_TEST_PATH) ||
        comparison_matches_path(COMPARISON_USB_WRONG_SUFFIX) ||
        comparison_matches_path(COMPARISON_USB_OTHER_PATH)) return 1;
#ifdef COMPARISON_REALTEK
    if (!comparison_target_fixture() || !comparison_firmware_fixture() || !relay_stdio_fixture() || !saved_page_fixture() || !relay_nfc_fixture(NULL)) return 1;
    uint8_t version[] = {0x0e,12,1,0x01,0x10,0,0x0c,0,0,0x0c,93,0,0x52,0x88};
    version_requested = true;
    observe_packet(HCI_EVENT_PACKET, 1, version, sizeof(version));
    if (!version_received || firmware_verified) return 1;
    version_received = false;
    version[12] = 0x71;
    observe_packet(HCI_EVENT_PACKET, 1, version, sizeof(version));
    if (!version_received || !firmware_verified) return 1;
#endif
    link_handle = 1;
    uint8_t open[] = {2, 1, 4, 0, 0x13, 0, 0x51, 0};
    uint8_t reply[] = {3, 1, 8, 0, 0x41, 0, 0x51, 0, 0, 0, 0, 0};
    observe_signal(true, open, sizeof(open));
    observe_signal(false, reply, sizeof(reply));
    uint8_t input[] = {1,0x20,11,0,7,0,0x51,0,0xa1,0x30,0,0,8,0,0};
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    input[6] = 0x52;
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    if (local_interrupt != 0x41 || peer_interrupt != 0x51 || reports != 1 || last_mask != 8) return 1;

    home_test.enabled = true;
    home_test.paired_at = 1000;
    if (home_request_due(5999, true) || home_request_due(6000, false) ||
        !home_request_due(6000, true)) return 1;
    home_test.requested_at = now_ms();
    if (home_request_due(6000, true)) return 1;
    input[12] = 0;
    input[13] = 0x10;
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    if (home_test.pressed_report_at || reports != 1) return 1;
    input[6] = 0x51;
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    if (!home_test.pressed_report_at || reports != 2 || last_mask != 0x001000) return 1;
    input[13] = 0;
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    if (home_test.released_report_at) return 1;
    home_test.release_requested_at = now_ms();
    observe_packet(HCI_ACL_DATA_PACKET, 0, input, sizeof(input));
    if (!home_test.released_report_at || reports != 4 || last_mask) return 1;

    uint8_t close[] = {6,2,4,0,0x41,0,0x51,0};
    observe_signal(true, close, sizeof(close));
    if (!remote_close) return 1;
    puts("PASS exact-instance filter, interrupt direction, HOME delay, press and release reports, peer close");
    return 0;
}

static bool directory(const wchar_t *path) {
    if (CreateDirectoryW(path, NULL)) return true;
    DWORD attrs = GetFileAttributesW(path);
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

int wmain(int argc, wchar_t **argv) {
#ifdef COMPARISON_REALTEK
    if (argc == 2 && !wcscmp(argv[1], L"--protocol-info")) {
        puts("{\"relay_protocol\":4,\"exact_instance\":true,\"readonly_ntag215\":true}");
        return 0;
    }
    if (argc == 3 && !wcscmp(argv[1], L"--verify-nfc-frames")) {
        bool ok=relay_nfc_fixture(argv[2]);
        puts(ok ? "NFC_INTEROP PASS tag_bytes=540 chunks=2 crc=verified fragmented_acl=verified scan_read_release=verified radio_opened=false" : "NFC_INTEROP FAIL");
        return ok ? 0 : 1;
    }
#endif
    if (argc == 2 && !wcscmp(argv[1], L"--self-test")) return check_fixture();
    if (argc == 3 && !wcscmp(argv[1], L"--press")) return send_command(argv[2]);
    const wchar_t *requested_log = NULL;
#ifdef COMPARISON_REALTEK
    const wchar_t *requested_instance = NULL;
#endif
    for (int i = 1; i < argc; ++i) {
        if (!home_test.enabled && !relay_stdio_enabled && !wcscmp(argv[i], L"--home-test")) home_test.enabled = true;
#ifdef COMPARISON_REALTEK
        else if (!requested_instance && !wcscmp(argv[i],L"--instance") && i+1<argc) requested_instance=argv[++i];
        else if (!relay_stdio_enabled && !home_test.enabled && !wcscmp(argv[i], L"--relay-stdio")) relay_stdio_enabled = true;
        else if (!relay_home_probe && !wcscmp(argv[i], L"--probe-home")) relay_home_probe = true;
#endif
        else if (!requested_log && !wcscmp(argv[i], L"--log") && i + 1 < argc &&
                 wcsncmp(argv[i + 1], L"--", 2)) requested_log = argv[++i];
        else {
            puts("Usage: [--log FILE] [--home-test], --press lr|a|left|right|home|stop, or --self-test.");
            return 64;
        }
    }
#ifdef COMPARISON_REALTEK
    if ((relay_stdio_enabled && !requested_instance) ||
        (requested_instance && !comparison_select_instance(requested_instance))) {
        fputs("Select an exact supported USB radio instance before starting the Switch relay.\n", stderr);
        return 64;
    }
    if (!comparison_uses_0852_firmware() && is_realtek_controller(comparison_vendor(),comparison_product())) {
        fputs("No matching packaged Realtek firmware for this model. Radio not opened.\n", stderr);
        return 65;
    }
    if(requested_instance)comparison_select_interface_class(requested_instance);
    if (relay_home_probe && !relay_stdio_enabled) {
        fputs("--probe-home requires --relay-stdio.\n", stderr);
        return 64;
    }
    if (relay_home_probe) home_test.enabled = true;
    if (relay_stdio_enabled) relay_nfc_start();
    if (relay_stdio_enabled && !relay_stdio_open(GetStdHandle(STD_INPUT_HANDLE))) {
        fputs("Physical relay requires an inherited stdin pipe.\n", stderr);
        return 64;
    }
#endif
    wchar_t private_dir[MAX_PATH], local[MAX_PATH], log_path[MAX_PATH];
    DWORD env_size = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    if (!env_size || env_size >= MAX_PATH || swprintf(private_dir, MAX_PATH, L"%ls\\PRO2-Upstream-Comparison", local) < 0 ||
        !directory(private_dir)) return 70;
    if (requested_log) {
        DWORD path_size = GetFullPathNameW(requested_log, MAX_PATH, log_path, NULL);
        if (!path_size || path_size >= MAX_PATH) return 70;
    } else if (swprintf(log_path, MAX_PATH, L"%ls\\session-%lu-%llu.log", private_dir, GetCurrentProcessId(),
                        (unsigned long long)GetTickCount64()) < 0) return 70;
    HANDLE single = CreateMutexW(NULL, FALSE, L"Local\\PRO2.UpstreamComparison.4b49cb9");
    if (!single || GetLastError() == ERROR_ALREADY_EXISTS) { fputs("A comparison is already running.\n", stderr); return 12; }
    /* Upstream writes its own bond using a narrow relative path beneath this
       private directory, including on installations with a Unicode user name. */
    if (!SetCurrentDirectoryW(private_dir) || !directory(L"btstack") ||
        _putenv_s("LOCALAPPDATA", ".") || _putenv_s("BTSTACK_HCI_DUMP", "0")) return 70;
    report = _wfopen(log_path, L"wx");
    if (!report) { fwprintf(stderr, L"Cannot create a new log at %ls\n", log_path); return 71; }
    for (int i = 0; i < COMMANDS; ++i) {
        wchar_t name[80];
        event_name(i, name);
        control_events[i] = CreateEventW(NULL, FALSE, FALSE, name);
        if (!control_events[i]) return 72;
    }
    DWORD console_mode;
    interactive = GetConsoleMode(GetStdHandle(STD_INPUT_HANDLE), &console_mode) != 0;
    int mirror = _dup(_fileno(stdout));
    if (mirror >= 0) display = _fdopen(mirror, "w");
    if (!freopen("NUL", "w", stdout) || !freopen("NUL", "w", stderr)) return 73;
    started = now_ms();
    note("START upstream=4b49cb955d08dc80a82673083fc0463cbc0c2540 instance=%s driver_changes=0 registry_changes=0 windows_pairing_imported=false raw_dump=false keys_logged=false", comparison_target_label());
    if (relay_stdio_enabled) note("RELAY_START source=physical_pipe framed_bytes=28 home_probe=%d", relay_home_probe);
    else if (home_test.enabled) note("HOME_TEST_START mode=automatic receiver_ui=not_observed_by_pc");
    else note("Open Switch Controllers > Change Grip/Order. PC keys: P=L+R A=confirm H=HOME J=left L=right Q=stop.");
    start_gamepad();
#ifdef COMPARISON_REALTEK
    relay_nfc_clear_session();
    release_timer_period();
#endif
    for (int i = 0; i < COMMANDS; ++i) CloseHandle(control_events[i]);
    CloseHandle(single);
    if (!stop_at) note("END reason=upstream_returned_before_observation");
    fclose(report);
    if (display) fclose(display);
    return outcome;
}

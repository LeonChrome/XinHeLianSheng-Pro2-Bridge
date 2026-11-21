#ifndef RELAY_BTKEY_ADAPTER_H
#define RELAY_BTKEY_ADAPTER_H
#include <stdint.h>
#include "btstack.h"
#define RELAY_BTKEY_SOURCE_COMMIT "4b49cb955d08dc80a82673083fc0463cbc0c2540"
typedef struct {
    /* Synchronous BTstack-thread callback. Message already includes A1.
     * Return zero only when the complete message was accepted for submission. */
    int (*send)(uint16_t cid, const uint8_t *message, uint16_t bytes, int startup_repeat, void *context);
    void (*request_can_send)(uint16_t cid, void *context);
    void *context;
} relay_btkey_callbacks_t;
typedef struct {
    double requested_hz, effective_hz;
    uint64_t snapshots, invalid_snapshots, send_attempts, send_successes, send_failures;
    uint64_t fresh_reports, subcommand_replies, nfc_reports, can_send_requests;
    uint64_t no_tag_guards, invalid_tags, overwritten_snapshot_reports;
    uint64_t command_overflows;
    uint64_t startup_repeats;
    unsigned command_queue_peak;
    int connected, handshake_done, tag_loaded, last_send_status;
} relay_btkey_stats_t;
/* Per link: full reports count only after the current host requested them
 * and the first report was submitted. */
enum { RELAY_PRO1_SIMPLE, RELAY_PRO1_CONFIRMED };
int relay_btkey_delivery_state(void);
/* Call after hci_init/l2cap setup context exists, before power-on.
 * Registers upstream HID/SDP/GAP; does not register the outer event or OUT callbacks.
 * Root owns all power changes and forwards events/OUT through the functions below.
 * requested_hz is capped at the mature engine's 60Hz baseline, not a measured rate. */
int relay_btkey_init(const relay_btkey_callbacks_t *callbacks, double requested_hz);
void relay_btkey_packet_handler(uint8_t type,uint16_t channel,uint8_t *packet,uint16_t size);
void relay_btkey_link_mode(uint8_t mode);
/* Simple HID body, excluding its transaction header and report ID. */
void relay_btkey_build_simple_input(uint8_t body[11]);
void relay_btkey_handle_report(uint16_t cid,hid_report_type_t type,uint16_t report_id,int len,uint8_t *body);
/* A 49-byte normalized 0x30 snapshot, including the report ID but excluding A1.
 * Byte 2 carries the battery level in bits 7:5 and source power flags below it. */
int relay_btkey_observe_snapshot(const uint8_t *report,uint16_t len);
void relay_btkey_set_local_address(const uint8_t address[6]);
int relay_btkey_set_tag540(const uint8_t *tag,uint16_t len);
void relay_btkey_clear_tag(void);
void relay_btkey_get_stats(relay_btkey_stats_t *out);
void relay_btkey_shutdown(void);
int relay_btkey_self_test(void);
#endif

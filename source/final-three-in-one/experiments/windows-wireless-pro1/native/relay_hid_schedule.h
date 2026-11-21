#ifndef PRO2_RELAY_HID_SCHEDULE_H
#define PRO2_RELAY_HID_SCHEDULE_H
#include <stdint.h>

/* The host may renegotiate Sniff/SetTsi around a mode change. A HID write
 * immediately afterwards can make Switch close both channels. Use a bounded
 * quiet window, not Exit Sniff or a sleep on the BTstack event thread.
 * Reference: TeamJCD/JoyConDroid commit 5e81a21b93fcbc84d22427f3a5d47ced1ca45b06.
 * One in-flight HID report also bounds bytes already queued in the controller;
 * host signaling and HCI commands never pass through this application gate. */
enum { RELAY_HID_HOLD_QUIET=1, RELAY_HID_HOLD_CREDIT=2 };
typedef struct {
    int observed,mode;
    uint16_t interval;
    double changed_at,quiet_until;
    unsigned quiet_ms;
    uint64_t transitions;
} relay_hid_schedule_t;
static inline void relay_hid_schedule_reset(relay_hid_schedule_t *state){
    *state=(relay_hid_schedule_t){0};
}
static inline int relay_hid_schedule_mode_window(relay_hid_schedule_t *state,int status,int mode,uint16_t interval,double stamp,unsigned quiet_ms){
    if(status||(state->observed&&state->mode==mode&&state->interval==interval))return 0;
    state->observed=1;state->mode=mode;state->interval=interval;
    state->changed_at=stamp;state->quiet_ms=quiet_ms;state->quiet_until=stamp+quiet_ms/1000.;state->transitions++;
    return 1;
}
static inline int relay_hid_schedule_mode(relay_hid_schedule_t *state,int status,int mode,uint16_t interval,double stamp){
    return relay_hid_schedule_mode_window(state,status,mode,interval,stamp,75);
}
static inline unsigned relay_hid_schedule_wait_ms(const relay_hid_schedule_t *state,double stamp){
    double ms=(state->quiet_until-stamp)*1000;
    if(ms<=0)return 0;
    unsigned wait=(unsigned)ms;
    return wait<ms?wait+1:wait;
}
static inline int relay_hid_schedule_hold(const relay_hid_schedule_t *state,int enabled,double stamp,unsigned in_flight){
    if(!enabled)return 0;
    return (stamp<state->quiet_until?RELAY_HID_HOLD_QUIET:0)|(in_flight?RELAY_HID_HOLD_CREDIT:0);
}
enum { RELAY_HID_FRAGMENT_START=1, RELAY_HID_FRAGMENT_END=2,
       RELAY_HID_FRESH_START=4, RELAY_HID_FRESH_END=8 };
typedef struct { unsigned remaining; int fresh; } relay_hid_fragments_t;
/* ACL completion counts fragments, not logical HID reports. Remember the
 * final fragment even when BTstack sends it asynchronously after l2cap_send
 * returns (notably the larger 0x31 NFC reports). */
static inline int relay_hid_fragment(relay_hid_fragments_t *state,int first,int application_hid,int fresh,unsigned total,unsigned bytes){
    int flags=0;
    if(first){
        if(!application_hid)return 0;
        state->remaining=total;state->fresh=fresh;
        flags=RELAY_HID_FRAGMENT_START|(fresh?RELAY_HID_FRESH_START:0);
    }else if(!state->remaining)return 0;
    state->remaining=bytes>=state->remaining?0:state->remaining-bytes;
    if(!state->remaining)flags|=RELAY_HID_FRAGMENT_END|(state->fresh?RELAY_HID_FRESH_END:0);
    return flags;
}
#endif

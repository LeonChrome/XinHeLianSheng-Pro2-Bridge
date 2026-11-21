#ifndef PRO2_RELAY_RECOVERY_H
#define PRO2_RELAY_RECOVERY_H

enum relay_recovery_phase {
    RELAY_RECOVERY_IDLE,
    RELAY_RECOVERY_WAIT_ACL,
    RELAY_RECOVERY_LISTEN,
    RELAY_RECOVERY_DIALING,
    RELAY_RECOVERY_WAIT_REPORT,
    RELAY_RECOVERY_CONFIRMING
};
enum relay_recovery_action { RELAY_RECOVERY_NONE, RELAY_RECOVERY_DIAL, RELAY_RECOVERY_EXPIRED, RELAY_RECOVERY_STABLE };
enum { RELAY_RECOVERY_DIAL_LIMIT=2 };

typedef struct {
    enum relay_recovery_phase phase;
    unsigned attempts;
    int handoff, requires_full;
    double due, deadline, late_dial, stable_at;
} relay_recovery_t;

/* The caller owns the selected peer and the transport. A recovery episode
 * retains its clock and attempt budget across brief HID reopens. */
static void relay_recovery_lost(relay_recovery_t *recovery,double stamp,int handoff,int requires_full){
    if(recovery->phase==RELAY_RECOVERY_IDLE){
        *recovery=(relay_recovery_t){.handoff=handoff,.requires_full=requires_full,.deadline=stamp+20};
    }
    recovery->phase=RELAY_RECOVERY_WAIT_ACL;
}

static void relay_recovery_released(relay_recovery_t *recovery,double stamp){
    if(recovery->phase!=RELAY_RECOVERY_WAIT_ACL)return;
    recovery->due=stamp+1.5;
    if(recovery->handoff && !recovery->late_dial){
        recovery->late_dial=stamp+26;
        recovery->deadline=recovery->late_dial+12;
    }
    if(recovery->attempts && recovery->late_dial>recovery->due)recovery->due=recovery->late_dial;
    recovery->phase=RELAY_RECOVERY_LISTEN;
}

static void relay_recovery_dial_failed(relay_recovery_t *recovery,double stamp){
    if(recovery->phase!=RELAY_RECOVERY_DIALING)return;
    recovery->phase=RELAY_RECOVERY_WAIT_ACL;
    relay_recovery_released(recovery,stamp);
}

static void relay_recovery_hid_opened(relay_recovery_t *recovery,double stamp){
    if(recovery->phase==RELAY_RECOVERY_IDLE)return;
    recovery->phase=recovery->requires_full?RELAY_RECOVERY_WAIT_REPORT:RELAY_RECOVERY_CONFIRMING;
    recovery->stable_at=stamp+2;
}

static void relay_recovery_report(relay_recovery_t *recovery,double stamp,int full){
    if(recovery->phase==RELAY_RECOVERY_WAIT_REPORT && full){
        recovery->phase=RELAY_RECOVERY_CONFIRMING;
        recovery->stable_at=stamp+2;
    }else if(recovery->phase==RELAY_RECOVERY_CONFIRMING && recovery->requires_full && !full){
        recovery->phase=RELAY_RECOVERY_WAIT_REPORT;
    }
}

static enum relay_recovery_action relay_recovery_poll(relay_recovery_t *recovery,double stamp,int acl_present){
    if(recovery->phase==RELAY_RECOVERY_IDLE)return RELAY_RECOVERY_NONE;
    if(recovery->phase==RELAY_RECOVERY_CONFIRMING && stamp>=recovery->stable_at &&
       recovery->stable_at<=recovery->deadline){
        *recovery=(relay_recovery_t){0};
        return RELAY_RECOVERY_STABLE;
    }
    if(stamp>=recovery->deadline)return RELAY_RECOVERY_EXPIRED;
    if(recovery->phase!=RELAY_RECOVERY_LISTEN || acl_present || stamp<recovery->due ||
       recovery->attempts>=RELAY_RECOVERY_DIAL_LIMIT)return RELAY_RECOVERY_NONE;
    recovery->attempts++;
    recovery->phase=RELAY_RECOVERY_DIALING;
    return RELAY_RECOVERY_DIAL;
}

/* Time-limited runs qualify the current session and have a separate wall limit. */
static int relay_observation(double stamp,double first_open,double qualified_since,unsigned duration){
    if(!duration || !first_open)return 0;
    if(qualified_since && stamp-qualified_since>=duration)return 1;
    return stamp-first_open>=duration+90 ? -1 : 0;
}
#endif

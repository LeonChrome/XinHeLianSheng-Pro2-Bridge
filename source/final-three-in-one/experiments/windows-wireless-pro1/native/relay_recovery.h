#ifndef PRO2_RELAY_RECOVERY_H
#define PRO2_RELAY_RECOVERY_H
/* Bounded, same-peer recovery. Never open a replacement while the old ACL
 * still exists, and never reset the retry budget after a brief reconnection. */
typedef struct { int active,acl_down,connecting; unsigned attempts; double due,deadline; } relay_recovery_t;
static int relay_recovery_begin(relay_recovery_t *r,double stamp,int acl_down){
    if(r->active)return 1;
    if(r->attempts>=2)return 0;
    r->active=1;r->acl_down=acl_down;r->connecting=0;r->due=stamp+.5;r->deadline=stamp+20;return 1;
}
static void relay_recovery_acl_down(relay_recovery_t *r,double stamp){
    if(!r->active)return;
    r->acl_down=1;r->due=stamp+.5;
}
/* 1: initiate one connection, -1: terminate this recovery, 0: wait. */
static int relay_recovery_poll(relay_recovery_t *r,double stamp){
    if(!r->active)return 0;
    if(stamp>=r->deadline)return -1;
    if(!r->acl_down||r->connecting||stamp<r->due)return 0;
    r->attempts++;r->connecting=1;return 1;
}
static void relay_recovery_opened(relay_recovery_t *r){r->active=0;r->connecting=0;}
#endif

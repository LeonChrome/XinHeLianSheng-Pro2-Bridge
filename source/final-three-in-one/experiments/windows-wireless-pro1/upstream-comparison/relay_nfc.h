#ifndef PRO1_RELAY_NFC_H
#define PRO1_RELAY_NFC_H
#include <stdbool.h>
#include <stdint.h>
#include <wchar.h>

void relay_nfc_start(void);
void relay_nfc_clear_session(void);
void relay_nfc_record(const uint8_t frame[28]);
void relay_nfc_observe_acl(const uint8_t *packet, unsigned size, uint16_t handle, uint16_t interrupt_cid);
void relay_nfc_event(const char *event, unsigned generation, const char *fields);
bool relay_nfc_fixture(const wchar_t *frames_path);
#endif

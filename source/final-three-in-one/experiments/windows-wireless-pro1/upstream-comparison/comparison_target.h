#ifndef COMPARISON_TARGET_H
#define COMPARISON_TARGET_H

#ifdef COMPARISON_REALTEK
#define COMPARISON_TARGET_LABEL "0BDA:0852/00E04C000001"
#define COMPARISON_USB_PREFIX "\\\\?\\USB#VID_0BDA&PID_0852#00E04C000001"
#define COMPARISON_USB_OTHER_PATH "\\\\?\\USB#VID_10D7&PID_B012#ACTIONS1234#{guid}"
int comparison_firmware_fixture(void);
#else
#define COMPARISON_TARGET_LABEL "10D7:B012/ACTIONS1234"
#define COMPARISON_USB_PREFIX "\\\\?\\USB#VID_10D7&PID_B012#ACTIONS1234"
#define COMPARISON_USB_OTHER_PATH "\\\\?\\USB#VID_0BDA&PID_0852#00E04C000001#{guid}"
#endif

#define COMPARISON_USB_TEST_PATH COMPARISON_USB_PREFIX "#{guid}"
#define COMPARISON_USB_WRONG_SUFFIX COMPARISON_USB_PREFIX "5#{guid}"
#ifdef COMPARISON_REALTEK
#include <wchar.h>
int comparison_select_instance(const wchar_t *instance);
void comparison_select_interface_class(const wchar_t *instance);
int comparison_target_fixture(void);
int comparison_uses_0852_firmware(void);
unsigned comparison_vendor(void);
unsigned comparison_product(void);
const char *comparison_target_label(void);
#else
#define comparison_target_label() COMPARISON_TARGET_LABEL
#endif
#endif

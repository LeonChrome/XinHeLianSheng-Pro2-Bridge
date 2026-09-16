/*
 * TinyUSB configuration for the Pico 2 W bridge.
 *
 * Root hub port 0 is the Pico's native USB controller and is exposed only as
 * a CDC debug port. Root hub port 1 is Pico-PIO-USB host on GP0/GP1.
 */
#ifndef XHLS_PICO2W_TUSB_CONFIG_H
#define XHLS_PICO2W_TUSB_CONFIG_H

#ifdef __cplusplus
extern "C" {
#endif

#ifndef BOARD_TUD_RHPORT
#define BOARD_TUD_RHPORT 0
#endif

#ifndef BOARD_TUH_RHPORT
#define BOARD_TUH_RHPORT 1
#endif

#ifndef BOARD_TUD_MAX_SPEED
#define BOARD_TUD_MAX_SPEED OPT_MODE_DEFAULT_SPEED
#endif

#ifndef BOARD_TUH_MAX_SPEED
#define BOARD_TUH_MAX_SPEED OPT_MODE_DEFAULT_SPEED
#endif

#ifndef CFG_TUSB_MCU
#error CFG_TUSB_MCU must be defined
#endif

#ifndef CFG_TUSB_OS
#define CFG_TUSB_OS OPT_OS_NONE
#endif

#ifndef CFG_TUSB_DEBUG
#define CFG_TUSB_DEBUG 0
#endif

#define CFG_TUD_ENABLED 1
#define CFG_TUD_MAX_SPEED BOARD_TUD_MAX_SPEED
#define CFG_TUH_ENABLED 1
#define CFG_TUH_MAX_SPEED BOARD_TUH_MAX_SPEED
#define CFG_TUH_RPI_PIO_USB 1

#ifndef CFG_TUD_MEM_SECTION
#define CFG_TUD_MEM_SECTION
#endif
#ifndef CFG_TUD_MEM_ALIGN
#define CFG_TUD_MEM_ALIGN __attribute__((aligned(4)))
#endif
#ifndef CFG_TUH_MEM_SECTION
#define CFG_TUH_MEM_SECTION
#endif
#ifndef CFG_TUH_MEM_ALIGN
#define CFG_TUH_MEM_ALIGN __attribute__((aligned(4)))
#endif

#define CFG_TUD_ENDPOINT0_SIZE 64
#define CFG_TUD_CDC 1
#define CFG_TUD_CDC_RX_BUFSIZE 256
#define CFG_TUD_CDC_TX_BUFSIZE 1024
#define CFG_TUD_CDC_EP_BUFSIZE 64

#define CFG_TUH_ENUMERATION_BUFSIZE 512
#define CFG_TUH_HUB 0
#define CFG_TUH_DEVICE_MAX 1
#define CFG_TUH_HID 1
#define CFG_TUH_HID_EPIN_BUFSIZE 64
#define CFG_TUH_HID_EPOUT_BUFSIZE 64

#ifdef __cplusplus
}
#endif

#endif

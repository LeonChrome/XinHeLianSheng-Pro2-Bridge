#pragma once

#define ENABLE_LOG_ERROR
#define ENABLE_LOG_INFO
#define ENABLE_PRINTF_HEXDUMP

#ifdef ENABLE_BLE
#define ENABLE_GATT_CLIENT_PAIRING
#define ENABLE_LE_PERIPHERAL
#define ENABLE_LE_SECURE_CONNECTIONS
#endif

#define HCI_ACL_CHUNK_SIZE_ALIGNMENT 4
// A Bluetooth DualSense input message is 79 bytes including its HIDP header.
// R44-R48 restricted the ACL buffer to 83 bytes to force a one-slot 3-DH1
// link, but live HCI completion telemetry showed multi-second TX stalls even
// with a strong RSSI and an active (non-sniff) link. Expose the CYW43's full
// 3-DH5 payload size so BR/EDR can negotiate one-, three-, or five-slot packet
// types instead of excluding multi-slot EDR at connection creation.
#define HCI_ACL_PAYLOAD_SIZE 1021
#define HCI_OUTGOING_PRE_BUFFER_SIZE 4

// Bound both directions of the CYW43 HCI transport. Without controller-to-host
// flow control, frequent HID output/configuration reports can occupy the shared
// SDIO transport while outgoing Number Of Completed Packets events are delayed.
// These values follow the Pico SDK high-rate Classic HID guidance and keep the
// controller's advertised eight outgoing ACL credits in sync with eight host
// receive buffers.
#define MAX_NR_CONTROLLER_ACL_BUFFERS 8
#define ENABLE_HCI_CONTROLLER_TO_HOST_FLOW_CONTROL
#define HCI_HOST_ACL_PACKET_LEN HCI_ACL_PAYLOAD_SIZE
#define HCI_HOST_ACL_PACKET_NUM 8
#define HCI_HOST_SCO_PACKET_LEN 0
#define HCI_HOST_SCO_PACKET_NUM 0

#define MAX_NR_GATT_CLIENTS 1
#define MAX_NR_HCI_CONNECTIONS 2
#define MAX_NR_L2CAP_CHANNELS 8
#define MAX_NR_L2CAP_SERVICES 4
#define MAX_NR_LE_DEVICE_DB_ENTRIES 8
#define MAX_NR_SM_LOOKUP_ENTRIES 3
#define MAX_NR_SERVICE_RECORD_ITEMS 4
#define MAX_NR_WHITELIST_ENTRIES 8

#define NVM_NUM_DEVICE_DB_ENTRIES 8
#define NVM_NUM_LINK_KEYS 8

#define MAX_ATT_DB_SIZE 1024
#define HAVE_EMBEDDED_TIME_MS
#define HAVE_ASSERT
#define HCI_RESET_RESEND_TIMEOUT_MS 100
#define ENABLE_MICRO_ECC_FOR_LE_SECURE_CONNECTIONS
#define ENABLE_SOFTWARE_AES128

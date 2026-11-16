#ifndef BTSTACK_CONFIG_H
#define BTSTACK_CONFIG_H
/* Optional local-personal experiment, never a production driver dependency. */
#define HAVE_ASSERT
#define HAVE_MALLOC
#define HAVE_POSIX_FILE_IO
#define HAVE_POSIX_TIME
#define ENABLE_CLASSIC
#define ENABLE_L2CAP_ENHANCED_RETRANSMISSION_MODE
#define ENABLE_LOG_ERROR
#define HCI_ACL_PAYLOAD_SIZE (1021 + 4)
#define HCI_INCOMING_PRE_BUFFER_SIZE 14
#define NVM_NUM_LINK_KEYS 4
#endif

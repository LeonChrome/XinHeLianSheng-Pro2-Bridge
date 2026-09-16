cmake_minimum_required(VERSION 3.17)

set(PICO_BOARD pico2_w CACHE STRING "Target board")

if(NOT DEFINED PICO_SDK_PATH)
    if(DEFINED ENV{PICO_SDK_PATH})
        set(PICO_SDK_PATH $ENV{PICO_SDK_PATH})
    else()
        get_filename_component(
            PICO_SDK_PATH
            "${CMAKE_CURRENT_LIST_DIR}/../../.toolchain/pico-sdk-2.3.0"
            ABSOLUTE
        )
    endif()
endif()

include(${PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(xhls_pico2w_pro2_wireless_bridge C CXX ASM)

set(XHLS_FIRMWARE_REVISION "r55" CACHE STRING "Firmware package revision")
option(
    XHLS_CLASSIC_BT_ONLY_DIAGNOSTIC
    "Disable PIO USB Host and publish synthetic 250 Hz input"
    OFF
)
set(
    XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ "0"
    CACHE STRING "Classic HID report rate limit (0, 66, 125, or 250 Hz)"
)
if(NOT XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ MATCHES "^(0|66|125|250)$")
    message(FATAL_ERROR "XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ must be 0, 66, 125, or 250")
endif()
set(
    XHLS_PAIRING_RECOVERY_TOKEN "0"
    CACHE STRING
    "Non-zero token forces Edge and clears its bond bank once per token"
)
if(NOT XHLS_PAIRING_RECOVERY_TOKEN MATCHES "^[0-9]+$")
    message(FATAL_ERROR "XHLS_PAIRING_RECOVERY_TOKEN must be an unsigned integer")
endif()
option(
    XHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC
    "Send the 9-byte DualSense basic report instead of the full BT report"
    OFF
)
if(NOT XHLS_FIRMWARE_REVISION MATCHES "^r[0-9]+$")
    message(FATAL_ERROR "XHLS_FIRMWARE_REVISION must look like r<number>")
endif()
set(
    XHLS_FIRMWARE_VERSION
    "0.6.0-revB-${XHLS_FIRMWARE_REVISION}-candidate"
)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

pico_sdk_init()

set(XHLS_PICO2W_SOURCES
    src/bridge_state.cpp
    src/classic_gamepad.cpp
    src/control_panel.cpp
    src/dualsense_protocol.cpp
    src/imu_converter.cpp
    src/main.cpp
    src/mode_chord.cpp
    src/mode_manager.cpp
    src/pairing_chord.cpp
    src/pairing_feedback.cpp
    src/pro2_host.cpp
    src/pro2_protocol.cpp
    src/pro2_rumble.cpp
    src/source_recovery_policy.cpp
    src/switch1_protocol.cpp
    src/usb_cdc_log.cpp
    src/usb_descriptors.c
    src/wireless_backend.cpp
    src/xbox_ble.cpp
)

add_executable(xhls_pico2w_pro2_wireless_bridge ${XHLS_PICO2W_SOURCES})

target_include_directories(xhls_pico2w_pro2_wireless_bridge PRIVATE
    ${CMAKE_CURRENT_LIST_DIR}/src
)

target_compile_definitions(xhls_pico2w_pro2_wireless_bridge PRIVATE
    CYW43_LWIP=0
    PICO_DEFAULT_PIO_USB_DP_PIN=0
    PIO_USB_DP_PIN_DEFAULT=0
    PICO_BTSTACK_CYW43_MAX_HCI_PROCESS_LOOP_COUNT=8
    pico_flash_bank_get_storage_offset_func=xhls_btstack_storage_offset
    XHLS_FIRMWARE_REVISION="${XHLS_FIRMWARE_REVISION}"
    XHLS_FIRMWARE_VERSION="${XHLS_FIRMWARE_VERSION}"
    XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ=${XHLS_CLASSIC_REPORT_RATE_LIMIT_HZ}
    XHLS_PAIRING_RECOVERY_TOKEN=${XHLS_PAIRING_RECOVERY_TOKEN}
)
if(XHLS_CLASSIC_BT_ONLY_DIAGNOSTIC)
    target_compile_definitions(
        xhls_pico2w_pro2_wireless_bridge PRIVATE
        XHLS_CLASSIC_BT_ONLY_DIAGNOSTIC=1
    )
endif()
if(XHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC)
    target_compile_definitions(
        xhls_pico2w_pro2_wireless_bridge PRIVATE
        XHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC=1
    )
endif()

# The CYW43 transport installs the board MAC when HCI opens. Keep the
# selected controller identity authoritative at that late initialization.
target_link_options(xhls_pico2w_pro2_wireless_bridge PRIVATE
    "-Wl,--wrap=hci_set_bd_addr"
    "-Wl,--wrap=l2cap_register_service"
)

set_source_files_properties(${XHLS_PICO2W_SOURCES} PROPERTIES
    COMPILE_OPTIONS "-Wall;-Wextra"
)

target_link_libraries(xhls_pico2w_pro2_wireless_bridge PRIVATE
    pico_stdlib
    hardware_clocks
    hardware_adc
    hardware_flash
    pico_sync
    pico_flash
    pico_unique_id
    pico_btstack_ble
    pico_btstack_classic
    pico_btstack_cyw43
    pico_btstack_flash_bank
    pico_cyw43_arch_poll
    tinyusb_board
    tinyusb_device
    tinyusb_host
    tinyusb_pico_pio_usb
)

pico_btstack_make_gatt_header(
    xhls_pico2w_pro2_wireless_bridge
    PRIVATE
    "${CMAKE_CURRENT_LIST_DIR}/src/xbox_ble.gatt"
)

pico_set_program_name(
    xhls_pico2w_pro2_wireless_bridge
    "XinHeLianSheng Pico 2 W Pro2 Bridge"
)
pico_set_program_version(
    xhls_pico2w_pro2_wireless_bridge
    "${XHLS_FIRMWARE_VERSION}"
)
pico_set_program_description(
    xhls_pico2w_pro2_wireless_bridge
    "Six-key USB-host Pro2 to PS5, PS5 Edge, Xbox BLE and Switch 1 Pro wireless HID"
)

pico_enable_stdio_usb(xhls_pico2w_pro2_wireless_bridge 0)
pico_enable_stdio_uart(xhls_pico2w_pro2_wireless_bridge 0)
pico_add_extra_outputs(xhls_pico2w_pro2_wireless_bridge)

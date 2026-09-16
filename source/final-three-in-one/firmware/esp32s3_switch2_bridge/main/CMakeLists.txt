idf_component_register(
    SRCS
        "app_main.c"
        "usb/usb_descriptors.c"
        "usb/hid_report.c"
        "usb/usb_hid_device.c"
        "usb/usb_switch2_vendor.c"
        "usb/usb_xinput_device.c"
        "ble/ble_central.c"
        "ble/ble_dual_probe.c"
        "ble/switch2_gatt.c"
        "bridge/switch2_state.c"
        "bridge/internal_gamepad_state.c"
        "bridge/gamepad_axis_math.c"
        "bridge/normalized_rumble.c"
        "bridge/report_mapper.c"
        "bridge/report_rate_stats.c"
        "bridge/xbox_paddle_mapper.c"
        "control/control_protocol.c"
        "log/app_log.c"
        "config/device_config.c"
    INCLUDE_DIRS
        "."
        "usb"
        "ble"
        "bridge"
        "control"
        "log"
        "config"
    REQUIRES
        esp_tinyusb
        bt
        nvs_flash
        esp_timer
)

if(DEFINED DEVICE_DEFAULT_MODE AND NOT "${DEVICE_DEFAULT_MODE}" STREQUAL "")
    target_compile_definitions(
        ${COMPONENT_LIB}
        PRIVATE
            DEVICE_DEFAULT_MODE=${DEVICE_DEFAULT_MODE}
            DEVICE_PROFILE_LOCKED_MODE=1
    )
endif()

if(DEFINED XINPUT_ELITE_EXPERIMENT AND XINPUT_ELITE_EXPERIMENT)
    target_compile_definitions(${COMPONENT_LIB} PRIVATE XINPUT_ELITE_EXPERIMENT=1)
endif()

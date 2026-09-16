if(NOT DEFINED V5_5_DS5_PROFILE)
    set(V5_5_DS5_PROFILE "hid_audio_uac2_4ch")
endif()

set(DS5_ENABLE_USB_AUDIO 0)
set(DS5_ENABLE_USB_COMPOSITE 0)
set(DS5_DEVICE_CLASS_IAD 0)
set(DS5_ENABLE_DUMMY_INTERFACE 0)
set(DS5_ENABLE_UAC1_CONTROL_ONLY 0)
set(DS5_ENABLE_UAC1_STREAMING_ALT0 0)
set(DS5_ENABLE_UAC1_AUDIO 0)
set(DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY 0)
set(DS5_ENABLE_UAC2_AUDIO 0)
set(DS5_AUDIO_CHANNELS 0)
set(DS5_HID_INTERFACE_NUMBER 0)
set(DS5_INTERFACE_COUNT 1)
set(DS5_PROFILE_SERIAL "V55HIDONLY")
set(DS5_USB_PID "0x0ce6")
set(DS5_USB_PRODUCT "DualSense Wireless Controller")
set(main_sources
    "app_main.c"
    "usb_dualsense_descriptor.c"
    "dualsense_report.c"
    "dualsense_report_mapper.c"
    "dualsense_rumble_intent.c"
    "dualsense_haptic_audio.c"
    "haptic_audio_to_raw02.c"
    "pro2_input_backend.c"
    "pro2_rumble_arbiter.c"
    "pro2_rumble_backend.c"
    "v55_control_protocol.c"
)

if(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac2")
    message(WARNING "hid_audio_uac2 is an alias for hid_audio_uac2_4ch")
    set(V5_5_DS5_PROFILE "hid_audio_uac2_4ch")
endif()

if(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac1_fallback")
    message(WARNING "hid_audio_uac1_fallback is an alias for hid_audio_uac1_2ch")
    set(V5_5_DS5_PROFILE "hid_audio_uac1_2ch")
endif()

if(V5_5_DS5_PROFILE STREQUAL "hid_composite_dummy_interface_class_00")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_DUMMY_INTERFACE 1)
    set(DS5_HID_INTERFACE_NUMBER 1)
    set(DS5_INTERFACE_COUNT 2)
    set(DS5_PROFILE_SERIAL "V55DUMMY00")
    list(APPEND main_sources
        "usb_dummy_interface_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_composite_dummy_interface_class_ef")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_DEVICE_CLASS_IAD 1)
    set(DS5_ENABLE_DUMMY_INTERFACE 1)
    set(DS5_HID_INTERFACE_NUMBER 1)
    set(DS5_INTERFACE_COUNT 2)
    set(DS5_PROFILE_SERIAL "V55DUMMYEF")
    list(APPEND main_sources
        "usb_dummy_interface_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_control_only")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC1_CONTROL_ONLY 1)
    set(DS5_HID_INTERFACE_NUMBER 1)
    set(DS5_INTERFACE_COUNT 2)
    set(DS5_PROFILE_SERIAL "V55ACONLY")
    list(APPEND main_sources
        "usb_uac1_audio_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_streaming_alt0_only")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC1_STREAMING_ALT0 1)
    set(DS5_HID_INTERFACE_NUMBER 2)
    set(DS5_INTERFACE_COUNT 3)
    set(DS5_PROFILE_SERIAL "V55ASALT0")
    list(APPEND main_sources
        "usb_uac1_audio_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac1_2ch")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC1_AUDIO 1)
    set(DS5_AUDIO_CHANNELS 2)
    set(DS5_HID_INTERFACE_NUMBER 2)
    set(DS5_INTERFACE_COUNT 3)
    set(DS5_PROFILE_SERIAL "V55UAC1_2CH")
    list(APPEND main_sources
        "usb_uac1_audio_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac1_4ch_dualsense" OR
       V5_5_DS5_PROFILE STREQUAL "hid_audio_uac1_4ch_ds5like")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC1_AUDIO 1)
    set(DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY 1)
    set(DS5_AUDIO_CHANNELS 4)
    set(DS5_HID_INTERFACE_NUMBER 3)
    set(DS5_INTERFACE_COUNT 4)
    set(DS5_PROFILE_SERIAL "V5915PS5STD")
    set(DS5_USB_PID "0x0ce6")
    set(DS5_USB_PRODUCT "DualSense Wireless Controller")
    list(APPEND main_sources
        "usb_uac1_audio_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac1_4ch_edge")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC1_AUDIO 1)
    set(DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY 1)
    set(DS5_AUDIO_CHANNELS 4)
    set(DS5_HID_INTERFACE_NUMBER 3)
    set(DS5_INTERFACE_COUNT 4)
    set(DS5_PROFILE_SERIAL "V5915PS5EDGE")
    set(DS5_USB_PID "0x0df2")
    set(DS5_USB_PRODUCT "DualSense Edge Wireless Controller")
    list(APPEND main_sources
        "usb_uac1_audio_driver.c"
    )
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac2_2ch")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_DEVICE_CLASS_IAD 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC2_AUDIO 1)
    set(DS5_AUDIO_CHANNELS 2)
    set(DS5_HID_INTERFACE_NUMBER 2)
    set(DS5_INTERFACE_COUNT 3)
    set(DS5_PROFILE_SERIAL "V55UAC2_2CH")
elseif(V5_5_DS5_PROFILE STREQUAL "hid_audio_uac2_4ch")
    set(DS5_ENABLE_USB_COMPOSITE 1)
    set(DS5_DEVICE_CLASS_IAD 1)
    set(DS5_ENABLE_USB_AUDIO 1)
    set(DS5_ENABLE_UAC2_AUDIO 1)
    set(DS5_AUDIO_CHANNELS 4)
    set(DS5_HID_INTERFACE_NUMBER 2)
    set(DS5_INTERFACE_COUNT 3)
    set(DS5_PROFILE_SERIAL "V55UAC2_4CH")
elseif(V5_5_DS5_PROFILE STREQUAL "hid_only")
    set(DS5_PROFILE_SERIAL "V55HIDONLY")
else()
    message(FATAL_ERROR "Unknown V5_5_DS5_PROFILE: ${V5_5_DS5_PROFILE}")
endif()

idf_component_register(
    SRCS
        ${main_sources}
    INCLUDE_DIRS
        "."
        "tinyusb_config"
    REQUIRES
        esp_tinyusb
        pro2_bridge_reuse
        nvs_flash
        esp_timer
        esp_hw_support
    WHOLE_ARCHIVE
)

target_compile_definitions(${COMPONENT_LIB} PRIVATE
    DS5_PROFILE_NAME=\"${V5_5_DS5_PROFILE}\"
    DS5_PROFILE_SERIAL=\"${DS5_PROFILE_SERIAL}\"
    DS5_USB_PID=${DS5_USB_PID}
    DS5_USB_PRODUCT=\"${DS5_USB_PRODUCT}\"
    DS5_ENABLE_USB_AUDIO=${DS5_ENABLE_USB_AUDIO}
    DS5_ENABLE_USB_COMPOSITE=${DS5_ENABLE_USB_COMPOSITE}
    DS5_DEVICE_CLASS_IAD=${DS5_DEVICE_CLASS_IAD}
    DS5_ENABLE_DUMMY_INTERFACE=${DS5_ENABLE_DUMMY_INTERFACE}
    DS5_ENABLE_UAC1_CONTROL_ONLY=${DS5_ENABLE_UAC1_CONTROL_ONLY}
    DS5_ENABLE_UAC1_STREAMING_ALT0=${DS5_ENABLE_UAC1_STREAMING_ALT0}
    DS5_ENABLE_UAC1_AUDIO=${DS5_ENABLE_UAC1_AUDIO}
    DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY=${DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY}
    DS5_ENABLE_UAC2_AUDIO=${DS5_ENABLE_UAC2_AUDIO}
    DS5_AUDIO_CHANNELS=${DS5_AUDIO_CHANNELS}
    DS5_HID_INTERFACE_NUMBER=${DS5_HID_INTERFACE_NUMBER}
    DS5_INTERFACE_COUNT=${DS5_INTERFACE_COUNT}
)

idf_component_get_property(tinyusb_lib espressif__tinyusb COMPONENT_LIB)
if(tinyusb_lib)
    target_include_directories(${tinyusb_lib} BEFORE PRIVATE
        "${CMAKE_CURRENT_LIST_DIR}/tinyusb_config")
    target_compile_definitions(${tinyusb_lib} PRIVATE
        DS5_ENABLE_USB_AUDIO=${DS5_ENABLE_USB_AUDIO}
        DS5_ENABLE_USB_COMPOSITE=${DS5_ENABLE_USB_COMPOSITE}
        DS5_DEVICE_CLASS_IAD=${DS5_DEVICE_CLASS_IAD}
        DS5_ENABLE_DUMMY_INTERFACE=${DS5_ENABLE_DUMMY_INTERFACE}
        DS5_ENABLE_UAC1_CONTROL_ONLY=${DS5_ENABLE_UAC1_CONTROL_ONLY}
        DS5_ENABLE_UAC1_STREAMING_ALT0=${DS5_ENABLE_UAC1_STREAMING_ALT0}
        DS5_ENABLE_UAC1_AUDIO=${DS5_ENABLE_UAC1_AUDIO}
        DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY=${DS5_ENABLE_UAC1_DUALSENSE_TOPOLOGY}
        DS5_ENABLE_UAC2_AUDIO=${DS5_ENABLE_UAC2_AUDIO}
        DS5_AUDIO_CHANNELS=${DS5_AUDIO_CHANNELS}
        DS5_HID_INTERFACE_NUMBER=${DS5_HID_INTERFACE_NUMBER}
        DS5_INTERFACE_COUNT=${DS5_INTERFACE_COUNT}
    )
endif()



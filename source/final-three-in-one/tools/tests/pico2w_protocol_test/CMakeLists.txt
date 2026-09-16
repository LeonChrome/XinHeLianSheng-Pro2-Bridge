cmake_minimum_required(VERSION 3.17)

project(pico2w_protocol_test CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(REPO_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../..")
set(FIRMWARE_SRC "${REPO_ROOT}/firmware/pico2w_pro2_wireless_bridge/src")

add_executable(pico2w_protocol_test
    main.cpp
    "${FIRMWARE_SRC}/dualsense_protocol.cpp"
    "${FIRMWARE_SRC}/imu_converter.cpp"
    "${FIRMWARE_SRC}/mode_chord.cpp"
    "${FIRMWARE_SRC}/pairing_chord.cpp"
    "${FIRMWARE_SRC}/pro2_protocol.cpp"
    "${FIRMWARE_SRC}/pro2_rumble.cpp"
    "${FIRMWARE_SRC}/source_recovery_policy.cpp"
    "${FIRMWARE_SRC}/switch1_protocol.cpp"
)

target_include_directories(pico2w_protocol_test PRIVATE "${FIRMWARE_SRC}")

if(MSVC)
    target_compile_options(pico2w_protocol_test PRIVATE /W4 /permissive-)
else()
    target_compile_options(pico2w_protocol_test PRIVATE -Wall -Wextra -Werror)
endif()

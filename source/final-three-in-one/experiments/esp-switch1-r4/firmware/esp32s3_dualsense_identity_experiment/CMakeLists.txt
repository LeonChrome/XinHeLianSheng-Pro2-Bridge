cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED V5_5_DS5_PROFILE)
    set(V5_5_DS5_PROFILE "hid_audio_uac2_4ch")
endif()

set(COMPONENTS main)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
set(PROJECT_VER "5.9.15-${V5_5_DS5_PROFILE}")
project(esp32s3_dualsense_identity_experiment)




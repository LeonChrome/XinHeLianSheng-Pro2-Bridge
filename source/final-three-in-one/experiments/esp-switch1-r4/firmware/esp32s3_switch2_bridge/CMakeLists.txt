cmake_minimum_required(VERSION 3.16)

set(EXTRA_COMPONENT_DIRS "")
set(COMPONENTS main)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
set(PROJECT_VER "5.9.15")
project(esp32s3_switch2_bridge)


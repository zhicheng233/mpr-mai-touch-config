cmake_minimum_required(VERSION 3.20)
project(firmware_host_tests LANGUAGES CXX)

include(CTest)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(firmware_host_tests
    test_main.cpp
    ../src/protocol.cpp
    ../src/config.cpp
    ../src/eeprom_store.cpp
    ../src/mpr121.cpp
    ../src/touch_state.cpp
    ../src/calibration.cpp
    ../src/firmware_state.cpp
)
target_include_directories(firmware_host_tests PRIVATE ../src)
if(NOT MSVC)
    target_compile_options(firmware_host_tests PRIVATE -Wall -Wextra -Werror)
endif()

add_test(NAME firmware_host_tests COMMAND firmware_host_tests)
set_tests_properties(firmware_host_tests PROPERTIES LABELS "firmware;host")

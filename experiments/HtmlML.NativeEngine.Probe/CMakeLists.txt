cmake_minimum_required(VERSION 3.20)
project(HtmlMLNativeEngineProbe LANGUAGES CXX)

include(CTest)

add_library(htmlml_native_engine SHARED
    native/htmlml_native_engine.cpp
    native/htmlml_native_dom.cpp)

target_compile_features(htmlml_native_engine PRIVATE cxx_std_20)
target_compile_definitions(htmlml_native_engine PRIVATE HTMLML_NATIVE_ENGINE_BUILD)
target_include_directories(htmlml_native_engine PUBLIC native)

option(HTMLML_NATIVE_ENGINE_ENABLE_V8 "Link the engine directly to the pinned V8 monolith" OFF)
set(HTMLML_V8_ROOT
    "${CMAKE_CURRENT_SOURCE_DIR}/../../third-party/clearscript/V8/build/v8"
    CACHE PATH
    "Path to the pinned V8 source/build root")

string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" HTMLML_SYSTEM_PROCESSOR_LOWER)
string(TOLOWER "${CMAKE_GENERATOR_PLATFORM}" HTMLML_GENERATOR_PLATFORM_LOWER)
if(HTMLML_GENERATOR_PLATFORM_LOWER STREQUAL "arm64")
    set(HTMLML_V8_CPU arm64)
elseif(HTMLML_GENERATOR_PLATFORM_LOWER STREQUAL "x64")
    set(HTMLML_V8_CPU x64)
elseif(HTMLML_SYSTEM_PROCESSOR_LOWER MATCHES "^(arm64|aarch64)$")
    set(HTMLML_V8_CPU arm64)
elseif(HTMLML_SYSTEM_PROCESSOR_LOWER MATCHES "^(x86_64|amd64)$")
    set(HTMLML_V8_CPU x64)
else()
    set(HTMLML_V8_CPU "${CMAKE_SYSTEM_PROCESSOR}")
endif()

if(MSVC)
    set(HTMLML_V8_MONOLITH
        "${HTMLML_V8_ROOT}/out/${HTMLML_V8_CPU}/Release/obj/v8_monolith.lib")
else()
    set(HTMLML_V8_MONOLITH
        "${HTMLML_V8_ROOT}/out/${HTMLML_V8_CPU}/Release/obj/libv8_monolith.a")
endif()
set(HTMLML_V8_ICU_DATA
    "${HTMLML_V8_ROOT}/out/${HTMLML_V8_CPU}/Release/icudtl.dat")

if(HTMLML_NATIVE_ENGINE_ENABLE_V8)
    if(NOT EXISTS "${HTMLML_V8_ROOT}/include/v8.h")
        message(FATAL_ERROR "HtmlML native engine: V8 headers not found below ${HTMLML_V8_ROOT}")
    endif()
    if(NOT EXISTS "${HTMLML_V8_MONOLITH}")
        message(FATAL_ERROR "HtmlML native engine: V8 monolith not found at ${HTMLML_V8_MONOLITH}")
    endif()
    if(NOT EXISTS "${HTMLML_V8_ICU_DATA}")
        message(FATAL_ERROR "HtmlML native engine: ICU data not found at ${HTMLML_V8_ICU_DATA}")
    endif()

    target_sources(htmlml_native_engine PRIVATE native/htmlml_v8_runtime.cpp)
    target_include_directories(htmlml_native_engine SYSTEM PRIVATE "${HTMLML_V8_ROOT}/include")
    target_compile_definitions(htmlml_native_engine PRIVATE
        HTMLML_NATIVE_ENGINE_WITH_V8
        HTMLML_V8_ICU_DATA_FILENAME="icudtl.dat")
    target_link_libraries(htmlml_native_engine PRIVATE "${HTMLML_V8_MONOLITH}")
    if(APPLE)
        target_link_libraries(htmlml_native_engine PRIVATE "-framework CoreFoundation")
    elseif(WIN32)
        target_link_libraries(htmlml_native_engine PRIVATE dbghelp winmm advapi32)
    elseif(UNIX)
        target_link_libraries(htmlml_native_engine PRIVATE dl rt)
    endif()
    message(STATUS "HtmlML native engine: V8 enabled (${HTMLML_V8_MONOLITH})")

    if(BUILD_TESTING)
        add_executable(htmlml_native_engine_tests
            tests/native_v8_runtime_tests.cpp)
        target_compile_features(htmlml_native_engine_tests PRIVATE cxx_std_20)
        target_link_libraries(htmlml_native_engine_tests PRIVATE htmlml_native_engine)
        add_test(NAME htmlml_native_engine_tests COMMAND htmlml_native_engine_tests)
    endif()
else()
    message(STATUS "HtmlML native engine: V8 intentionally disabled")
endif()

if(MSVC)
    target_compile_options(htmlml_native_engine PRIVATE /W4 /permissive- /GR- /wd4996)
else()
    target_compile_options(htmlml_native_engine PRIVATE
        -Wall -Wextra -Wpedantic -fno-rtti -Wno-deprecated-declarations)
endif()

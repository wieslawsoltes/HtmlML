cmake_minimum_required(VERSION 3.20)
project(HtmlMLNativeEngineProbe LANGUAGES CXX)

add_library(htmlml_native_engine SHARED
    native/htmlml_native_engine.cpp
    native/htmlml_native_dom.cpp)

target_compile_features(htmlml_native_engine PRIVATE cxx_std_20)
target_compile_definitions(htmlml_native_engine PRIVATE HTMLML_NATIVE_ENGINE_BUILD)
target_include_directories(htmlml_native_engine PUBLIC native)

option(HTMLML_NATIVE_ENGINE_ENABLE_V8 "Link the probe directly to the pinned V8 monolith" ON)
set(HTMLML_V8_ROOT
    "${CMAKE_CURRENT_SOURCE_DIR}/../../third-party/clearscript/V8/build/v8"
    CACHE PATH
    "Path to the pinned V8 source/build root")

if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64)$")
    set(HTMLML_V8_CPU arm64)
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$")
    set(HTMLML_V8_CPU x64)
else()
    set(HTMLML_V8_CPU "${CMAKE_SYSTEM_PROCESSOR}")
endif()

set(HTMLML_V8_MONOLITH
    "${HTMLML_V8_ROOT}/out/${HTMLML_V8_CPU}/Release/obj/libv8_monolith.a")
if(HTMLML_NATIVE_ENGINE_ENABLE_V8 AND EXISTS "${HTMLML_V8_ROOT}/include/v8.h" AND EXISTS "${HTMLML_V8_MONOLITH}")
    target_sources(htmlml_native_engine PRIVATE native/htmlml_v8_runtime.cpp)
    target_include_directories(htmlml_native_engine SYSTEM PRIVATE "${HTMLML_V8_ROOT}/include")
    target_compile_definitions(htmlml_native_engine PRIVATE
        HTMLML_NATIVE_ENGINE_WITH_V8
        HTMLML_V8_ICU_DATA_PATH="${HTMLML_V8_ROOT}/out/${HTMLML_V8_CPU}/Release/icudtl.dat")
    target_compile_options(htmlml_native_engine PRIVATE -fno-rtti -Wno-deprecated-declarations)
    target_link_libraries(htmlml_native_engine PRIVATE "${HTMLML_V8_MONOLITH}")
    if(APPLE)
        target_link_libraries(htmlml_native_engine PRIVATE "-framework CoreFoundation")
    elseif(UNIX)
        target_link_libraries(htmlml_native_engine PRIVATE dl rt)
    endif()
    message(STATUS "HtmlML native probe: V8 enabled (${HTMLML_V8_MONOLITH})")
elseif(HTMLML_NATIVE_ENGINE_ENABLE_V8)
    message(WARNING "HtmlML native probe: V8 disabled; monolith not found at ${HTMLML_V8_MONOLITH}")
else()
    message(STATUS "HtmlML native probe: V8 intentionally disabled")
endif()

if(MSVC)
    target_compile_options(htmlml_native_engine PRIVATE /W4 /permissive-)
else()
    target_compile_options(htmlml_native_engine PRIVATE -Wall -Wextra -Wpedantic)
endif()

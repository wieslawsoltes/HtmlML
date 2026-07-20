#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(HTMLML_NATIVE_ENGINE_BUILD)
#    define HTMLML_API __declspec(dllexport)
#  else
#    define HTMLML_API __declspec(dllimport)
#  endif
#else
#  define HTMLML_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct htmlml_engine htmlml_engine;
typedef struct htmlml_scene_view htmlml_scene_view;

typedef enum htmlml_input_kind {
    HTMLML_INPUT_POINTER_MOVE = 1,
    HTMLML_INPUT_POINTER_DOWN = 2,
    HTMLML_INPUT_POINTER_UP = 3,
    HTMLML_INPUT_WHEEL = 4,
    HTMLML_INPUT_FRAME = 5,
    HTMLML_INPUT_RESIZE = 6
} htmlml_input_kind;

typedef struct htmlml_input_event {
    uint32_t kind;
    uint32_t flags;
    uint64_t sequence;
    double x;
    double y;
    double delta_x;
    double delta_y;
} htmlml_input_event;

typedef struct htmlml_scene_header {
    uint64_t revision;
    uint64_t base_revision;
    uint64_t consumed_input_sequence;
    float viewport_width;
    float viewport_height;
    uint32_t command_count;
    uint32_t canvas_layer_count;
    uint32_t damage_rect_count;
    uint32_t flags;
    uint64_t content_hash;
} htmlml_scene_header;

typedef struct htmlml_scene_command {
    uint32_t kind;
    uint32_t flags;
    float x;
    float y;
    float width;
    float height;
    uint32_t rgba;
    uint32_t node_id;
    float radius_top_left;
    float radius_top_right;
    float radius_bottom_right;
    float radius_bottom_left;
    float stroke_width;
} htmlml_scene_command;

typedef struct htmlml_canvas_layout {
    uint32_t node_id;
    uint32_t flags;
    float x;
    float y;
    float width;
    float height;
    uint32_t bitmap_width;
    uint32_t bitmap_height;
} htmlml_canvas_layout;

typedef struct htmlml_canvas_layer {
    uint32_t node_id;
    uint32_t flags;
    uint32_t command_offset;
    uint32_t command_count;
    uint32_t string_offset;
    uint32_t string_count;
    uint32_t reserved;
    float x;
    float y;
    float width;
    float height;
    uint32_t bitmap_width;
    uint32_t bitmap_height;
    uint64_t generation;
} htmlml_canvas_layer;

typedef union htmlml_canvas_command_data {
    double values[8];
    struct {
        double x;
        double y;
    } point;
    struct {
        double x;
        double y;
        double width;
        double height;
    } rect;
    struct {
        double a;
        double b;
        double c;
        double d;
        double e;
        double f;
    } transform;
    struct {
        double x1;
        double y1;
        double x2;
        double y2;
        double x3;
        double y3;
    } curve;
} htmlml_canvas_command_data;

/*
 * Fixed-layout native draw operation. resource_id addresses the owning
 * layer's string/resource table when the command kind requires one. Managed
 * renderers traverse this array in-place; there is no packet decoding step.
 */
typedef struct htmlml_canvas_command {
    uint32_t kind;
    uint32_t flags;
    uint32_t resource_id;
    uint32_t reserved;
    htmlml_canvas_command_data data;
} htmlml_canvas_command;

typedef struct htmlml_scene_string {
    uint32_t byte_offset;
    uint32_t byte_length;
} htmlml_scene_string;

typedef struct htmlml_damage_rect {
    float x;
    float y;
    float width;
    float height;
} htmlml_damage_rect;

/*
 * The acquired pointer is the immutable scene. Every pointer below remains
 * valid until htmlml_scene_release is called. A renderer can construct spans
 * over the arrays directly without further native calls or copying.
 */
struct htmlml_scene_view {
    uint32_t struct_size;
    uint32_t abi_version;
    htmlml_scene_header header;
    const htmlml_scene_command* commands;
    const htmlml_canvas_layer* canvas_layers;
    const htmlml_canvas_command* canvas_commands;
    const htmlml_scene_string* strings;
    const char* string_bytes;
    const htmlml_damage_rect* damage_rects;
    const void* lease_token;
    uint32_t canvas_command_count;
    uint32_t string_count;
    uint32_t string_byte_count;
    uint32_t reserved;
};

typedef struct htmlml_engine_options {
    uint32_t struct_size;
    uint32_t simulated_chart_command_count;
    const char* compilation_cache_directory;
    size_t compilation_cache_directory_length;
} htmlml_engine_options;

typedef struct htmlml_engine_metrics {
    uint64_t enqueued_inputs;
    uint64_t dropped_inputs;
    uint64_t consumed_inputs;
    uint64_t published_scenes;
    uint64_t acquired_scenes;
    uint64_t executed_scripts;
    uint64_t script_errors;
    uint64_t dom_nodes;
    uint64_t layout_passes;
    uint64_t iframe_nodes;
    uint64_t iframe_html_bytes;
    uint64_t frame_scripts_executed;
    uint64_t frame_script_errors;
    uint64_t canvas_nodes;
    uint64_t component_ready;
    uint64_t compilation_requests;
    uint64_t compilation_memory_hits;
    uint64_t compilation_persistent_hits;
    uint64_t compilation_persistent_misses;
    uint64_t compilation_cache_rejections;
    uint64_t compilation_cache_bytes_read;
    uint64_t compilation_cache_bytes_written;
    uint64_t compilation_time_nanoseconds;
    uint64_t input_events_dispatched;
    uint64_t input_callbacks_invoked;
    uint64_t busiest_canvas_width_milli;
    uint64_t busiest_canvas_height_milli;
    uint64_t coalesced_resize_inputs;
    uint64_t applied_resize_inputs;
    uint64_t last_resize_dispatch_nanoseconds;
    uint64_t last_scene_publication_nanoseconds;
    uint64_t last_resize_outer_listeners_nanoseconds;
    uint64_t last_resize_frame_listeners_nanoseconds;
    uint64_t last_resize_layout_nanoseconds;
    uint64_t last_resize_observers_nanoseconds;
    uint64_t coalesced_pointer_move_inputs;
    uint64_t coalesced_wheel_inputs;
    uint64_t applied_pointer_move_inputs;
    uint64_t applied_wheel_inputs;
} htmlml_engine_metrics;

/*
 * Pays the process-wide native runtime initialization cost without creating a
 * document, isolate, or chart. This does not read or mutate compilation caches.
 */
HTMLML_API uint32_t htmlml_engine_get_abi_version(void);
HTMLML_API uint8_t htmlml_engine_prewarm(void);
HTMLML_API htmlml_engine* htmlml_engine_create(uint32_t simulated_chart_command_count);
HTMLML_API htmlml_engine* htmlml_engine_create_with_options(const htmlml_engine_options* options);
HTMLML_API void htmlml_engine_destroy(htmlml_engine* engine);
HTMLML_API uint8_t htmlml_engine_set_resource_root(
    htmlml_engine* engine,
    const char* resource_root,
    size_t resource_root_length);
HTMLML_API uint8_t htmlml_engine_enqueue(htmlml_engine* engine, const htmlml_input_event* event);
HTMLML_API uint8_t htmlml_engine_execute_script(
    htmlml_engine* engine,
    const char* source,
    size_t source_length,
    const char* document_name,
    size_t document_name_length);
HTMLML_API size_t htmlml_engine_evaluate_json(
    htmlml_engine* engine,
    const char* source,
    size_t source_length,
    const char* document_name,
    size_t document_name_length,
    char* destination,
    size_t destination_capacity,
    uint32_t timeout_milliseconds);
/*
 * Removes one actual managed-datafeed request from the native V8 bridge. The
 * payload is UTF-8 JSON. A too-small/null destination reports the required
 * size without consuming the request; a successful full copy consumes it.
 */
HTMLML_API size_t htmlml_engine_take_host_request(
    htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
/*
 * Removes one V8 console entry. The UTF-8 payload is `<level>\n<message>`;
 * querying with a null/short destination reports the required byte count
 * without consuming the entry.
 */
HTMLML_API size_t htmlml_engine_take_console_message(
    htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
HTMLML_API size_t htmlml_engine_copy_last_error(
    const htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
HTMLML_API size_t htmlml_engine_copy_first_iframe_html(
    const htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
HTMLML_API size_t htmlml_engine_copy_scene_diagnostics(
    const htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
HTMLML_API size_t htmlml_engine_copy_canvas_layouts(
    const htmlml_engine* engine,
    htmlml_canvas_layout* destination,
    size_t destination_capacity);
/*
 * Starts a new immutable-scene diff chain with a complete checkpoint. Call
 * this after the previous scene consumer has stopped, before attaching a new
 * renderer (for example after compositor/context recreation).
 */
HTMLML_API uint8_t htmlml_engine_request_scene_checkpoint(htmlml_engine* engine);
HTMLML_API const htmlml_scene_view* htmlml_engine_acquire_latest_scene(htmlml_engine* engine);
HTMLML_API uint8_t htmlml_scene_acknowledge(const htmlml_scene_view* scene);
HTMLML_API void htmlml_scene_release(const htmlml_scene_view* scene);
HTMLML_API uint8_t htmlml_scene_get_header(
    const htmlml_scene_view* scene,
    htmlml_scene_header* header);
HTMLML_API const htmlml_scene_command* htmlml_scene_get_commands(
    const htmlml_scene_view* scene,
    uint32_t* count);
HTMLML_API void htmlml_engine_get_metrics(
    const htmlml_engine* engine,
    htmlml_engine_metrics* metrics);

#ifdef __cplusplus
}
#endif

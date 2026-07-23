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
    // x carries the host compositor's monotonic timestamp in milliseconds.
    HTMLML_INPUT_FRAME = 5,
    HTMLML_INPUT_RESIZE = 6,
    // x carries a DOM-compatible key code for keyboard events.
    HTMLML_INPUT_KEY_DOWN = 7,
    HTMLML_INPUT_KEY_UP = 8,
    // x carries one Unicode scalar value. Hosts enqueue one event per scalar.
    HTMLML_INPUT_TEXT = 9
} htmlml_input_kind;

typedef enum htmlml_cursor_kind {
    HTMLML_CURSOR_DEFAULT = 0,
    HTMLML_CURSOR_POINTER = 1,
    HTMLML_CURSOR_TEXT = 2,
    HTMLML_CURSOR_CROSSHAIR = 3,
    HTMLML_CURSOR_WAIT = 4,
    HTMLML_CURSOR_MOVE = 5,
    HTMLML_CURSOR_NOT_ALLOWED = 6,
    HTMLML_CURSOR_HELP = 7
} htmlml_cursor_kind;

enum {
    HTMLML_INPUT_MODIFIER_SHIFT = 1U << 0U,
    HTMLML_INPUT_MODIFIER_CONTROL = 1U << 1U,
    HTMLML_INPUT_MODIFIER_ALT = 1U << 2U,
    HTMLML_INPUT_MODIFIER_META = 1U << 3U,
    HTMLML_INPUT_KEY_REPEAT = 1U << 4U,
    // Pointer flags reserve low bits for DOM `buttons` and bits 8-15 for the
    // changed button. Keep keyboard-compatible modifiers in their own lane.
    HTMLML_INPUT_POINTER_MODIFIER_SHIFT = 1U << 16U,
    HTMLML_INPUT_POINTER_MODIFIER_CONTROL = 1U << 17U,
    HTMLML_INPUT_POINTER_MODIFIER_ALT = 1U << 18U,
    HTMLML_INPUT_POINTER_MODIFIER_META = 1U << 19U
};

typedef struct htmlml_input_event {
    uint32_t kind;
    uint32_t flags;
    uint64_t sequence;
    double x;
    double y;
    // For HTMLML_INPUT_RESIZE, delta_x carries the positive host device scale
    // factor. Zero retains ABI-v2 compatibility with hosts that predate scale
    // reporting and is interpreted as 1.0.
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

enum {
    HTMLML_CANVAS_COMMAND_FLAG_EVEN_ODD = 1U << 16U
};

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

typedef enum htmlml_resource_kind {
    HTMLML_RESOURCE_DOCUMENT = 0,
    HTMLML_RESOURCE_SCRIPT = 1,
    HTMLML_RESOURCE_STYLESHEET = 2,
    // Text-backed SVG image resources used by CSS background-image. Binary
    // image formats require a future byte-resource envelope.
    HTMLML_RESOURCE_IMAGE = 3
} htmlml_resource_kind;

/*
 * Synchronous text-resource callback used by the native DOM runtime. The
 * callback follows the other copy APIs: a null/short destination reports the
 * required response-envelope byte count. The envelope contains status,
 * cacheability/freshness metadata, validators, and UTF-8 content. Returning
 * zero reports a load failure. The URL is already absolute and normalized by
 * HtmlML.
 */
typedef size_t (*htmlml_resource_load_callback)(
    void* user_data,
    uint32_t kind,
    const char* url,
    size_t url_length,
    const char* entity_tag,
    size_t entity_tag_length,
    int64_t last_modified_unix_seconds,
    char* destination,
    size_t destination_capacity);

/*
 * Asynchronous notification emitted after an immutable scene has been
 * published. Consumers use this edge to schedule a compositor paint; they
 * still acquire and acknowledge the scene through the normal scene API.
 * The callback runs on the engine worker and must not block.
 */
typedef void (*htmlml_scene_published_callback)(
    void* user_data,
    uint64_t revision,
    uint64_t consumed_input_sequence,
    float viewport_width,
    float viewport_height);

typedef struct htmlml_text_metrics {
    uint32_t struct_size;
    float advance_width;
    float ascent;
    float descent;
    float leading;
} htmlml_text_metrics;

/*
 * Synchronous host text shaper used by native layout. Layout and paint must
 * consume the same glyph advances; otherwise kerning, combining marks, font
 * weight, and fallback faces can make inline boxes clip or drift. The callback
 * runs on the engine worker and must not block or call back into the engine.
 */
typedef uint8_t (*htmlml_text_measure_callback)(
    void* user_data,
    const char* text,
    size_t text_length,
    const char* font_family,
    size_t font_family_length,
    float font_size,
    int32_t font_weight,
    float letter_spacing,
    float word_spacing,
    htmlml_text_metrics* metrics);

typedef struct htmlml_engine_options {
    uint32_t struct_size;
    uint32_t simulated_chart_command_count;
    const char* compilation_cache_directory;
    size_t compilation_cache_directory_length;
    htmlml_resource_load_callback resource_load_callback;
    void* resource_load_user_data;
    htmlml_scene_published_callback scene_published_callback;
    void* scene_published_user_data;
    htmlml_text_measure_callback text_measure_callback;
    void* text_measure_user_data;
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
    uint64_t applied_animation_frames;
    uint64_t coalesced_animation_frames;
    uint64_t last_animation_advance_nanoseconds;
    uint64_t last_layout_nanoseconds;
    uint64_t last_scene_build_nanoseconds;
    uint64_t maximum_scene_publication_nanoseconds;
} htmlml_engine_metrics;

typedef struct htmlml_input_dispatch_metrics {
    uint32_t struct_size;
    uint32_t reserved;
    uint64_t last_dispatch_nanoseconds;
    uint64_t maximum_dispatch_nanoseconds;
    uint64_t last_dispatch_sequence;
} htmlml_input_dispatch_metrics;

typedef struct htmlml_resource_cache_metrics {
    uint32_t struct_size;
    uint32_t reserved;
    uint64_t requests;
    uint64_t hits;
    uint64_t misses;
    uint64_t rejections;
    uint64_t bytes_read;
    uint64_t bytes_written;
} htmlml_resource_cache_metrics;

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
HTMLML_API uint8_t htmlml_engine_load_url(
    htmlml_engine* engine,
    const char* url,
    size_t url_length);
HTMLML_API uint8_t htmlml_engine_enqueue(htmlml_engine* engine, const htmlml_input_event* event);
/* Returns the CSS cursor resolved at the latest hit-tested pointer position. */
HTMLML_API uint32_t htmlml_engine_get_cursor(const htmlml_engine* engine);
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
/*
 * Removes one failed asynchronous input dispatch. The UTF-8 payload is
 * `<sequence>\n<kind>\n<error>` so consumers can attribute the JavaScript
 * exception to the exact native input event. A null/short destination reports
 * the required size without consuming the failure.
 */
HTMLML_API size_t htmlml_engine_take_input_dispatch_failure(
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
/*
 * Copies a UTF-8 htmlml-native-feature-use-v2 JSON snapshot. Feature and
 * composition observations are counted at native decision points;
 * `complete:false` means one or more inventory categories remain uninstrumented.
 */
HTMLML_API size_t htmlml_engine_copy_feature_use(
    const htmlml_engine* engine,
    char* destination,
    size_t destination_capacity);
/* Copies registered element listener target/type inventory as stable UTF-8 JSON. */
HTMLML_API size_t htmlml_engine_copy_event_listener_inventory(
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
HTMLML_API uint8_t htmlml_engine_get_input_dispatch_metrics(
    const htmlml_engine* engine,
    htmlml_input_dispatch_metrics* metrics);
HTMLML_API uint8_t htmlml_engine_get_resource_cache_metrics(
    const htmlml_engine* engine,
    htmlml_resource_cache_metrics* metrics);

#ifdef __cplusplus
}
#endif

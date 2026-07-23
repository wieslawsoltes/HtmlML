#pragma once

#include "htmlml_native_engine.h"

#include <cstdint>
#include <functional>
#include <limits>
#include <memory>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace htmlml_native {

enum class length_unit : uint8_t {
    automatic,
    pixels,
    percent,
    em,
    rem,
    viewport_width,
    viewport_height,
    max_content,
    min_content,
    fit_content
};

struct css_length final {
    float value{0};
    length_unit unit{length_unit::automatic};
    // CSS calc() can combine a percentage with an absolute offset, for example
    // calc(50% - 32px). Keep both terms until the containing size is known.
    float pixel_offset{0};
};

struct layout_rect final {
    float x{0};
    float y{0};
    float width{0};
    float height{0};
};

enum class display_mode : uint8_t {
    block,
    flex,
    inline_flow,
    inline_block,
    inline_flex,
    grid,
    inline_grid,
    table,
    inline_table,
    table_row_group,
    table_header_group,
    table_footer_group,
    table_row,
    table_cell,
    table_column_group,
    table_column,
    table_caption,
    list_item,
    contents,
    none
};

enum class position_mode : uint8_t {
    normal,
    relative,
    absolute,
    fixed
};

enum class flex_direction : uint8_t {
    row,
    column
};

enum class align_mode : uint8_t {
    stretch,
    start,
    center,
    end,
    baseline
};

enum class justify_mode : uint8_t {
    start,
    center,
    end,
    space_between,
    space_around,
    space_evenly
};

enum class overflow_mode : uint8_t {
    visible,
    hidden,
    clip,
    automatic,
    scroll
};

enum class float_mode : uint8_t {
    none,
    left,
    right
};

struct node_style final {
    struct opacity_keyframe final {
        float offset{0};
        float opacity{1};
    };
    struct rotation_keyframe final {
        float offset{0};
        float degrees{0};
    };

    struct transition_timing final {
        float duration_ms{0};
        float delay_ms{0};
        float x1{0.25F};
        float y1{0.1F};
        float x2{0.25F};
        float y2{1.0F};
    };

    struct pseudo_element final {
        css_length width{};
        css_length height{};
        css_length left{};
        css_length top{};
        css_length right{};
        css_length bottom{};
        css_length margin_left{};
        css_length margin_top{};
        css_length margin_right{};
        css_length margin_bottom{};
        css_length border_top_left_radius{};
        css_length border_top_right_radius{};
        css_length border_bottom_right_radius{};
        css_length border_bottom_left_radius{};
        layout_rect layout{};
        display_mode display{display_mode::inline_flow};
        position_mode position{position_mode::normal};
        align_mode align_self{align_mode::stretch};
        int32_t z_index{0};
        float font_size{-1};
        float line_height{-1};
        float opacity{1};
        uint32_t background_rgba{0};
        uint32_t foreground_rgba{0};
        bool background_current_color{false};
        std::string content;
        bool generated{false};
        bool display_none{false};
        bool visibility_hidden{false};
        bool align_self_specified{false};
    };

    css_length width{};
    css_length height{};
    css_length min_width{};
    css_length min_height{};
    css_length max_width{};
    css_length max_height{};
    css_length left{};
    css_length top{};
    css_length right{};
    css_length bottom{};
    css_length padding_left{};
    css_length padding_top{};
    css_length padding_right{};
    css_length padding_bottom{};
    css_length margin_left{};
    css_length margin_top{};
    css_length margin_right{};
    css_length margin_bottom{};
    css_length row_gap{};
    css_length column_gap{};
    std::vector<css_length> grid_template_columns;
    std::vector<css_length> grid_template_rows;
    css_length border_left_width{};
    css_length border_top_width{};
    css_length border_right_width{};
    css_length border_bottom_width{};
    css_length outline_width{};
    css_length border_top_left_radius{};
    css_length border_top_right_radius{};
    css_length border_bottom_right_radius{};
    css_length border_bottom_left_radius{};
    css_length transform_translate_x{};
    css_length transform_translate_y{};
    css_length transform_origin_x{50, length_unit::percent};
    css_length transform_origin_y{50, length_unit::percent};
    float transform_scale_x{1};
    float transform_scale_y{1};
    float transform_rotate_degrees{0};
    bool transform_specified{false};
    // Retain whether transform-origin won the cascade independently from its
    // computed value. The initial 50% 50% value is otherwise indistinguishable
    // from an explicitly authored origin when detecting CSS compositions.
    bool transform_origin_specified{false};
    // Retain the winning transition longhands so comma-list cycling and
    // shorthand/longhand cascade order can be resolved as a unit.
    std::string transition_property_value{"all"};
    std::string transition_duration_value{"0s"};
    std::string transition_delay_value{"0s"};
    std::string transition_timing_function_value{"ease"};
    transition_timing transform_transition{};
    transition_timing opacity_transition{};
    transition_timing color_transition{};
    // Bounded CSS Animations slice used by component loading indicators. The
    // authored longhands stay available to getComputedStyle while the runtime
    // resolves a named @keyframes rule into opacity stops for the compositor.
    std::string animation_name_value{"none"};
    std::string animation_duration_value{"0s"};
    std::string animation_delay_value{"0s"};
    std::string animation_timing_function_value{"ease"};
    std::string animation_iteration_count_value{"1"};
    std::string opacity_keyframe_animation_signature;
    std::vector<opacity_keyframe> opacity_keyframes;
    std::string rotation_keyframe_animation_signature;
    std::vector<rotation_keyframe> rotation_keyframes;
    float opacity_keyframe_duration_ms{0};
    float opacity_keyframe_delay_ms{0};
    float opacity_keyframe_iterations{1};
    float opacity_keyframe_x1{0.25F};
    float opacity_keyframe_y1{0.1F};
    float opacity_keyframe_x2{0.25F};
    float opacity_keyframe_y2{1.0F};
    display_mode display{display_mode::block};
    position_mode position{position_mode::normal};
    float_mode floating{float_mode::none};
    flex_direction direction{flex_direction::row};
    align_mode align_items{align_mode::stretch};
    align_mode align_self{align_mode::stretch};
    justify_mode justify_content{justify_mode::start};
    overflow_mode overflow_x{overflow_mode::visible};
    overflow_mode overflow_y{overflow_mode::visible};
    float flex_grow{0};
    float flex_shrink{1};
    css_length flex_basis{};
    float opacity{1};
    uint32_t background_rgba{0};
    // The first CSS background image layer. The current connected-component
    // slice deliberately supports URL-backed SVG artwork because component
    // badges and icons use that composition; additional image formats and
    // multiple layers remain explicit capability gaps.
    std::string background_image_value{"none"};
    std::string background_image_markup;
    std::string background_image_view_box;
    std::string background_repeat{"repeat"};
    std::string background_position_x{"0%"};
    std::string background_position_y{"0%"};
    std::string background_size_x{"auto"};
    std::string background_size_y{"auto"};
    uint32_t foreground_rgba{0};
    uint32_t border_left_rgba{0};
    uint32_t border_top_rgba{0};
    uint32_t border_right_rgba{0};
    uint32_t border_bottom_rgba{0};
    uint32_t outline_rgba{0};
    float box_shadow_offset_x{0};
    float box_shadow_offset_y{0};
    float box_shadow_blur_radius{0};
    float box_shadow_spread_radius{0};
    uint32_t box_shadow_rgba{0};
    bool box_shadow_present{false};
    // Negative means unspecified/inherited. Zero is a valid CSS value and is
    // used by visually hidden accessibility content.
    float font_size{-1};
    float line_height{-1};
    int32_t font_weight{0};
    float letter_spacing{0};
    float word_spacing{0};
    bool letter_spacing_specified{false};
    bool word_spacing_specified{false};
    int32_t z_index{0};
    // Keep the computed `auto` keyword distinct from its paint stacking level.
    // Collapsing both to integer zero made CSSOM unable to distinguish an
    // unspecified/root-inherited z-index from an authored `z-index: 0`.
    bool z_index_auto{true};
    std::string font_family;
    std::string text_align;
    std::string vertical_align;
    std::string text_transform;
    std::string white_space;
    // Authored cursor token. Cursor is inherited, so an empty value means the
    // host projection resolves the nearest ancestor declaration or `auto`.
    std::string cursor;
    // SVG presentation-paint properties applied through the CSS cascade.
    // Values remain as CSS tokens so currentColor can resolve against the
    // live inherited foreground when the immutable SVG scene is serialized.
    std::string svg_fill;
    std::string svg_stroke;
    std::string list_style_position;
    std::string list_style_type;
    // CSS custom properties participate in the cascade and inherit.  Keeping
    // them on the native node lets component-scoped `var()` declarations be
    // resolved without crossing the managed boundary.
    std::unordered_map<std::string, std::string> custom_properties;
    std::unordered_set<std::string> important_custom_properties;
    uint64_t inline_property_mask{0};
    uint64_t important_property_mask{0};
    bool clip{false};
    bool scroll_x_enabled{false};
    bool scroll_y_enabled{false};
    bool visibility_hidden{false};
    bool visibility_specified{false};
    bool pointer_events_none{false};
    bool pointer_events_specified{false};
    bool flex_wrap{false};
    bool flex_reverse{false};
    bool align_self_specified{false};
    bool border_box{false};
    bool margin_left_auto{false};
    bool margin_top_auto{false};
    bool margin_right_auto{false};
    bool margin_bottom_auto{false};
    bool grid_two_columns{false};
    bool grid_fractional_rows{false};
    bool grid_span_all{false};
    int32_t grid_column_start{0};
    std::string grid_area_value{"auto"};
    std::string grid_row_value{"auto"};
    std::string grid_row_start_value{"auto"};
    std::string grid_row_end_value{"auto"};
    std::string grid_column_value{"auto"};
    std::string grid_column_start_value{"auto"};
    std::string grid_column_end_value{"auto"};
    bool table_layout_fixed{false};
    pseudo_element before{};
    pseudo_element after{};
};

struct canvas_rect_command final {
    float x{0};
    float y{0};
    float width{0};
    float height{0};
    uint32_t rgba{0};
};

struct canvas_line_command final {
    float x1{0};
    float y1{0};
    float x2{0};
    float y2{0};
    float line_width{1};
    uint32_t rgba{0};
};

struct text_layout_fragment final {
    float x{0};
    float y{0};
    float width{0};
    float height{0};
    std::string text;
};

struct dom_node final {
    uint32_t id{0};
    std::string tag;
    std::string id_attribute;
    std::string class_name;
    std::string text_content;
    std::string form_value;
    size_t selection_start{0};
    size_t selection_end{0};
    std::string selection_direction{"none"};
    // XML documents preserve qualified/tag and attribute name case. HTML nodes
    // continue to apply the ASCII case-insensitive name rules at the binding.
    bool xml_mode{false};
    // Option selectedness is live state and must not overwrite the authored
    // `selected` attribute, which is the reset/default source.
    bool selectedness_initialized{false};
    bool selectedness{false};
    // A select assigned a value/index with no match has no implicit first
    // option until selection is changed or the form is reset.
    bool selection_explicitly_empty{false};
    // Checkedness is live control state; the authored `checked` attribute is
    // retained separately as the default/reset source.
    bool checkedness_initialized{false};
    bool checkedness{false};
    std::unordered_map<std::string, std::string> attributes;
    // Authored inline declarations are retained independently from computed
    // style so HTMLElement.style reflects specified values and mutations.
    std::unordered_map<std::string, std::string> inline_style_declarations;
    std::unordered_set<std::string> inline_important_declarations;
    dom_node* parent{nullptr};
    std::vector<dom_node*> children;
    node_style style{};
    layout_rect layout{};
    layout_rect list_marker_layout{};
    // Highest stacking level represented by this paint subtree. A node with
    // its own z-index remains the root of that stacking context; transparent
    // wrappers inherit the highest positive descendant for scene ordering.
    int32_t paint_z_index{0};
    bool paints_after_retained_canvas{false};
    std::vector<canvas_rect_command> canvas_rects;
    std::vector<canvas_line_command> canvas_lines;
    std::vector<text_layout_fragment> text_layout_fragments;
    // Resolved table geometry is projected onto the semantic table boxes so
    // row groups and rows can arrange against one shared column grid.
    std::vector<float> table_column_widths;
    size_t table_column_index{0};
    size_t table_column_span{1};
    size_t table_row_span{1};
    float table_row_height{0};
    float table_cell_height{0};
    uint64_t canvas_generation{1};
    std::vector<htmlml_canvas_command> canvas_commands;
    std::vector<std::string> canvas_strings;
    std::unordered_map<std::string, uint32_t> canvas_string_indices;
    uint64_t canvas_fill_rect_calls{0};
    uint64_t canvas_probable_volume_fill_rect_calls{0};
    std::unordered_map<uint64_t, uint64_t> canvas_probable_volume_by_generation;
    uint64_t canvas_fill_calls{0};
    uint64_t canvas_path_argument_fill_calls{0};
    uint64_t canvas_draw_image_calls{0};
    uint64_t canvas_canvas_draw_image_calls{0};
    uint64_t canvas_self_draw_image_calls{0};
    uint64_t canvas_fill_text_calls{0};
    uint64_t canvas_stroke_text_calls{0};
    uint64_t canvas_clear_rect_calls{0};
    uint64_t canvas_full_clear_calls{0};
    uint64_t canvas_full_clear_reset_calls{0};
    uint64_t canvas_full_clear_current_clip_calls{0};
    uint64_t canvas_full_clear_saved_clip_calls{0};
    uint64_t canvas_clear_bounds_rejected_calls{0};
    uint64_t canvas_max_clear_stack_depth{0};
    std::unordered_map<uint32_t, uint64_t> canvas_fill_rect_color_calls;
    css_length painted_transform_translate_x{};
    css_length painted_transform_translate_y{};
    css_length transform_animation_from_translate_x{};
    css_length transform_animation_from_translate_y{};
    css_length transform_animation_target_translate_x{};
    css_length transform_animation_target_translate_y{};
    float painted_transform_scale_x{1};
    float painted_transform_scale_y{1};
    float transform_animation_from_scale_x{1};
    float transform_animation_from_scale_y{1};
    float transform_animation_target_scale_x{1};
    float transform_animation_target_scale_y{1};
    float painted_transform_rotate_degrees{0};
    float transform_animation_from_degrees{0};
    float transform_animation_target_degrees{0};
    float transform_animation_duration_ms{0};
    float transform_animation_delay_ms{0};
    float transform_animation_x1{0.25F};
    float transform_animation_y1{0.1F};
    float transform_animation_x2{0.25F};
    float transform_animation_y2{1.0F};
    double transform_animation_started_ms{0};
    bool transform_animation_initialized{false};
    bool transform_animation_active{false};
    bool transform_animation_start_event_sent{false};
    float painted_opacity{1};
    float opacity_animation_from{1};
    float opacity_animation_target{1};
    float opacity_animation_duration_ms{0};
    float opacity_animation_delay_ms{0};
    float opacity_animation_x1{0.25F};
    float opacity_animation_y1{0.1F};
    float opacity_animation_x2{0.25F};
    float opacity_animation_y2{1.0F};
    double opacity_animation_started_ms{0};
    bool opacity_animation_initialized{false};
    bool opacity_animation_active{false};
    bool opacity_animation_start_event_sent{false};
    std::string opacity_keyframe_animation_signature;
    double opacity_keyframe_animation_started_ms{0};
    bool opacity_keyframe_animation_active{false};
    std::string rotation_keyframe_animation_signature;
    double rotation_keyframe_animation_started_ms{0};
    bool rotation_keyframe_animation_active{false};
    uint32_t painted_foreground_rgba{0};
    uint32_t color_animation_from_rgba{0};
    uint32_t color_animation_target_rgba{0};
    float color_animation_duration_ms{0};
    float color_animation_delay_ms{0};
    float color_animation_x1{0.25F};
    float color_animation_y1{0.1F};
    float color_animation_x2{0.25F};
    float color_animation_y2{1.0F};
    double color_animation_started_ms{0};
    bool color_animation_initialized{false};
    bool color_animation_active{false};
    bool color_animation_start_event_sent{false};
    bool form_value_initialized{false};
    bool input_focused{false};
    bool caret_visible{false};
    float scroll_left{0};
    float scroll_top{0};
    float scroll_content_width{0};
    float scroll_content_height{0};
    float scroll_viewport_width{0};
    float scroll_viewport_height{0};
    bool visible{true};
};

class native_document final {
public:
    struct transition_event_record final {
        uint32_t node_id{0};
        std::string type;
        std::string property_name;
        float elapsed_time_seconds{0};
    };

    explicit native_document(
        htmlml_text_measure_callback text_measure_callback = nullptr,
        void* text_measure_user_data = nullptr);

    dom_node& body() noexcept;
    const dom_node& body() const noexcept;
    dom_node& create_element(std::string tag);
    bool append_child(dom_node& parent, dom_node& child);
    void remove_all_children(dom_node& parent);
    size_t erase_detached_subtree(dom_node& root);
    dom_node* find_by_native_id(uint32_t id) noexcept;
    dom_node* find_by_id(const std::string& id) noexcept;
    std::vector<dom_node*> query_selector_all(dom_node& root, const std::string& selector);
    dom_node* hit_test(dom_node& root, float x, float y);
    void clear();
    void layout(float viewport_width, float viewport_height);
    void build_scene(
        std::vector<htmlml_scene_command>& commands,
        std::vector<htmlml_scene_string>& strings,
        std::vector<char>& string_bytes) const;
    void build_canvas_layouts(std::vector<htmlml_canvas_layout>& layouts) const;
    void build_canvas_display_lists(
        std::vector<htmlml_canvas_layer>& layers,
        std::vector<htmlml_canvas_command>& canvas_commands,
        std::vector<htmlml_scene_string>& strings,
        std::vector<char>& string_bytes) const;

    uint64_t layout_passes() const noexcept;
    size_t node_count() const noexcept;
    size_t count_tag(const std::string& tag) const noexcept;
    size_t sum_attribute_bytes(const std::string& tag, const std::string& attribute) const noexcept;
    std::string first_attribute(const std::string& tag, const std::string& attribute) const;
    std::string describe_busiest_canvas() const;
    layout_rect busiest_canvas_layout() const noexcept;
    bool dirty() const noexcept;
    void mark_dirty() noexcept;
    void signal_animation_frame(double timestamp_ms) noexcept;
    void update_style_animations(dom_node& node);
    bool advance_animations() noexcept;
    bool has_active_animations() const noexcept;
    std::vector<transition_event_record> take_transition_events();

    static css_length parse_length(const std::string& value);
    static void parse_transform_translate(
        const std::string& value,
        css_length& translate_x,
        css_length& translate_y,
        float& scale_x,
        float& scale_y,
        float& rotate_degrees);
    static void parse_transform_origin(
        const std::string& value,
        css_length& origin_x,
        css_length& origin_y);
    static uint32_t parse_color(const std::string& value);

private:
    struct text_measurement_key final {
        std::string text;
        std::string family;
        float font_size{0};
        int32_t font_weight{0};
        float letter_spacing{0};
        float word_spacing{0};

        bool operator==(const text_measurement_key&) const = default;
    };

    struct text_measurement_key_hash final {
        size_t operator()(const text_measurement_key& value) const noexcept
        {
            auto result = std::hash<std::string>{}(value.text);
            const auto mix = [&result](size_t next) {
                result ^= next + 0x9e3779b9U + (result << 6U) + (result >> 2U);
            };
            mix(std::hash<std::string>{}(value.family));
            mix(std::hash<float>{}(value.font_size));
            mix(std::hash<int32_t>{}(value.font_weight));
            mix(std::hash<float>{}(value.letter_spacing));
            mix(std::hash<float>{}(value.word_spacing));
            return result;
        }
    };

    float measure_text_width(
        std::string_view value,
        const dom_node& node) const;
    std::vector<std::string> wrap_text_lines(
        const std::string& value,
        float available_width,
        const dom_node& node,
        bool allow_wrap) const;
    float resolve_length(
        const dom_node& context,
        css_length value,
        float available,
        float fallback) const;
    float resolve_length(css_length value, float available, float fallback) const;
    static bool is_specified(css_length value);
    float intrinsic_size(
        const dom_node& node,
        bool horizontal,
        float available);
    void layout_children(dom_node& parent);
    void layout_child(dom_node& child, const layout_rect& available, layout_rect assigned);
    void append_scene(
        const dom_node& node,
        std::vector<htmlml_scene_command>& commands,
        std::vector<htmlml_scene_string>& strings,
        std::vector<char>& string_bytes,
        bool inherited_visibility_hidden,
        bool defer_fixed_descendants) const;
    static bool matches_selector(const dom_node& node, const std::string& selector);
    static void collect_matches(
        dom_node& node,
        const std::string& selector,
        std::vector<dom_node*>& result);
    static dom_node* hit_test_node(
        dom_node& node,
        float x,
        float y,
        bool inherited_visibility_hidden,
        bool inherited_pointer_events_none,
        bool ignore_own_clip = false) noexcept;
    bool is_connected(const dom_node& node) const noexcept;

    std::vector<std::unique_ptr<dom_node>> nodes_;
    dom_node* body_{nullptr};
    float viewport_width_{1};
    float viewport_height_{1};
    uint32_t next_node_id_{1};
    uint64_t layout_passes_{0};
    double animation_frame_timestamp_ms_{0};
    double last_animation_advance_timestamp_ms_{
        std::numeric_limits<double>::quiet_NaN()};
    std::vector<transition_event_record> transition_events_;
    htmlml_text_measure_callback text_measure_callback_{nullptr};
    void* text_measure_user_data_{nullptr};
    mutable std::unordered_map<
        text_measurement_key,
        float,
        text_measurement_key_hash> text_measurement_cache_;
    bool dirty_{true};
};

} // namespace htmlml_native

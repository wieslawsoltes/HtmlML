#pragma once

#include "htmlml_native_engine.h"

#include <cstdint>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace htmlml_native {

enum class length_unit : uint8_t {
    automatic,
    pixels,
    percent
};

struct css_length final {
    float value{0};
    length_unit unit{length_unit::automatic};
};

enum class display_mode : uint8_t {
    block,
    flex,
    inline_block,
    inline_flex,
    grid,
    inline_grid,
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
    end
};

enum class justify_mode : uint8_t {
    start,
    center,
    end,
    space_between
};

struct node_style final {
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
        position_mode position{position_mode::normal};
        float font_size{-1};
        float line_height{-1};
        uint32_t background_rgba{0};
        uint32_t foreground_rgba{0};
        std::string content;
        bool generated{false};
        bool display_none{false};
        bool visibility_hidden{false};
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
    css_length border_left_width{};
    css_length border_top_width{};
    css_length border_right_width{};
    css_length border_bottom_width{};
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
    float transform_transition_duration_ms{0};
    float transform_transition_x1{0.25F};
    float transform_transition_y1{0.1F};
    float transform_transition_x2{0.25F};
    float transform_transition_y2{1.0F};
    display_mode display{display_mode::block};
    position_mode position{position_mode::normal};
    flex_direction direction{flex_direction::row};
    align_mode align_items{align_mode::stretch};
    align_mode align_self{align_mode::stretch};
    justify_mode justify_content{justify_mode::start};
    float flex_grow{0};
    float flex_shrink{1};
    float opacity{1};
    uint32_t background_rgba{0};
    uint32_t foreground_rgba{0};
    uint32_t border_left_rgba{0};
    uint32_t border_top_rgba{0};
    uint32_t border_right_rgba{0};
    uint32_t border_bottom_rgba{0};
    // Negative means unspecified/inherited. Zero is a valid CSS value and is
    // used by visually hidden accessibility content.
    float font_size{-1};
    float line_height{-1};
    int32_t font_weight{0};
    int32_t z_index{0};
    std::string font_family;
    std::string text_align;
    std::string white_space;
    // CSS custom properties participate in the cascade and inherit.  Keeping
    // them on the native node lets component-scoped `var()` declarations be
    // resolved without crossing the managed boundary.
    std::unordered_map<std::string, std::string> custom_properties;
    uint64_t inline_property_mask{0};
    uint64_t important_property_mask{0};
    bool clip{false};
    bool visibility_hidden{false};
    bool visibility_specified{false};
    bool pointer_events_none{false};
    bool pointer_events_specified{false};
    bool flex_wrap{false};
    bool flex_reverse{false};
    bool align_self_specified{false};
    bool border_box{false};
    bool grid_two_columns{false};
    bool grid_span_all{false};
    pseudo_element before{};
    pseudo_element after{};
};

struct layout_rect final {
    float x{0};
    float y{0};
    float width{0};
    float height{0};
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

struct dom_node final {
    uint32_t id{0};
    std::string tag;
    std::string id_attribute;
    std::string class_name;
    std::string text_content;
    std::unordered_map<std::string, std::string> attributes;
    dom_node* parent{nullptr};
    std::vector<dom_node*> children;
    node_style style{};
    layout_rect layout{};
    std::vector<canvas_rect_command> canvas_rects;
    std::vector<canvas_line_command> canvas_lines;
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
    float painted_transform_rotate_degrees{0};
    float transform_animation_from_degrees{0};
    float transform_animation_target_degrees{0};
    float transform_animation_duration_ms{0};
    float transform_animation_x1{0.25F};
    float transform_animation_y1{0.1F};
    float transform_animation_x2{0.25F};
    float transform_animation_y2{1.0F};
    int64_t transform_animation_started_nanoseconds{0};
    bool transform_animation_initialized{false};
    bool transform_animation_active{false};
    bool visible{true};
};

class native_document final {
public:
    native_document();

    dom_node& body() noexcept;
    const dom_node& body() const noexcept;
    dom_node& create_element(std::string tag);
    bool append_child(dom_node& parent, dom_node& child);
    void remove_all_children(dom_node& parent);
    dom_node* find_by_id(const std::string& id) noexcept;
    std::vector<dom_node*> query_selector_all(dom_node& root, const std::string& selector);
    dom_node* hit_test(dom_node& root, float x, float y) noexcept;
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
    void update_transform_animation(dom_node& node, float previous_target_degrees);
    bool advance_animations() noexcept;
    bool has_active_animations() const noexcept;

    static css_length parse_length(const std::string& value);
    static void parse_transform_translate(
        const std::string& value,
        css_length& translate_x,
        css_length& translate_y,
        float& scale_x,
        float& scale_y,
        float& rotate_degrees);
    static uint32_t parse_color(const std::string& value);

private:
    static float resolve_length(css_length value, float available, float fallback);
    static bool is_specified(css_length value);
    static float intrinsic_size(
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
        bool inherited_visibility_hidden) const;
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
        bool inherited_pointer_events_none) noexcept;

    std::vector<std::unique_ptr<dom_node>> nodes_;
    dom_node* body_{nullptr};
    uint32_t next_node_id_{1};
    uint64_t layout_passes_{0};
    bool dirty_{true};
};

} // namespace htmlml_native

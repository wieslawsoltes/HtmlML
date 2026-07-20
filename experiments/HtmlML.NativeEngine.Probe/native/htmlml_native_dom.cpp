#include "htmlml_native_dom.h"

#include <algorithm>
#include <charconv>
#include <chrono>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <limits>
#include <optional>
#include <sstream>
#include <string_view>
#include <tuple>

namespace htmlml_native {
namespace {

bool is_out_of_flow(position_mode position) noexcept
{
    return position == position_mode::absolute || position == position_mode::fixed;
}

bool is_flex_container(display_mode display) noexcept
{
    return display == display_mode::flex || display == display_mode::inline_flex;
}

bool is_grid_container(display_mode display) noexcept
{
    return display == display_mode::grid || display == display_mode::inline_grid;
}

bool is_inline_level(display_mode display) noexcept
{
    return display == display_mode::inline_block
        || display == display_mode::inline_flex
        || display == display_mode::inline_grid;
}

float parse_number(std::string_view value, float fallback = 0)
{
    std::string copy(value);
    char* end = nullptr;
    const auto result = std::strtof(copy.c_str(), &end);
    return end != copy.c_str() && std::isfinite(result) ? result : fallback;
}

enum class calc_unit : uint8_t { number, pixels, percent, invalid };

struct calc_value final {
    double value{0};
    calc_unit unit{calc_unit::invalid};
};

class calc_parser final {
public:
    explicit calc_parser(std::string_view source) : source_(source) {}

    std::optional<calc_value> parse()
    {
        auto result = expression();
        whitespace();
        return result.unit != calc_unit::invalid && position_ == source_.size()
            ? std::optional<calc_value>(result)
            : std::nullopt;
    }

private:
    calc_value expression()
    {
        auto result = term();
        while (result.unit != calc_unit::invalid) {
            whitespace();
            if (!consume('+') && !consume('-')) break;
            const auto operation = source_[position_ - 1U];
            auto right = term();
            result = add(result, right, operation == '-' ? -1.0 : 1.0);
        }
        return result;
    }

    calc_value term()
    {
        auto result = factor();
        while (result.unit != calc_unit::invalid) {
            whitespace();
            if (!consume('*') && !consume('/')) break;
            const auto operation = source_[position_ - 1U];
            auto right = factor();
            result = operation == '*' ? multiply(result, right) : divide(result, right);
        }
        return result;
    }

    calc_value factor()
    {
        whitespace();
        if (consume('+')) return factor();
        if (consume('-')) {
            auto value = factor();
            value.value = -value.value;
            return value;
        }
        if (consume('(')) {
            auto value = expression();
            whitespace();
            return consume(')') ? value : invalid();
        }
        if (position_ < source_.size()
            && (std::isalpha(static_cast<unsigned char>(source_[position_]))
                || source_[position_] == '-')) {
            const auto start = position_;
            while (position_ < source_.size()
                && (std::isalnum(static_cast<unsigned char>(source_[position_]))
                    || source_[position_] == '-')) ++position_;
            const auto name = source_.substr(start, position_ - start);
            whitespace();
            if (!consume('(')) return invalid();
            if (name == "calc") {
                auto value = expression();
                whitespace();
                return consume(')') ? value : invalid();
            }
            if (name == "max" || name == "min") {
                auto result = expression();
                whitespace();
                while (consume(',')) {
                    auto next = expression();
                    if (!compatible(result, next)) return invalid();
                    if (result.unit == calc_unit::number && result.value == 0
                        && next.unit != calc_unit::number) result.unit = next.unit;
                    if (next.unit == calc_unit::number && next.value == 0
                        && result.unit != calc_unit::number) next.unit = result.unit;
                    result.value = name == "max"
                        ? std::max(result.value, next.value)
                        : std::min(result.value, next.value);
                    whitespace();
                }
                return consume(')') ? result : invalid();
            }
            return invalid();
        }

        const auto start = position_;
        char* end = nullptr;
        std::string remaining(source_.substr(position_));
        const auto number = std::strtod(remaining.c_str(), &end);
        if (end == remaining.c_str() || !std::isfinite(number)) return invalid();
        position_ += static_cast<size_t>(end - remaining.c_str());
        auto unit = calc_unit::number;
        if (source_.substr(position_).starts_with("px")) {
            position_ += 2U;
            unit = calc_unit::pixels;
        } else if (position_ < source_.size() && source_[position_] == '%') {
            ++position_;
            unit = calc_unit::percent;
        }
        static_cast<void>(start);
        return {number, unit};
    }

    static bool compatible(calc_value left, calc_value right)
    {
        return left.unit == right.unit
            || (left.unit == calc_unit::number && left.value == 0)
            || (right.unit == calc_unit::number && right.value == 0);
    }

    static calc_value add(calc_value left, calc_value right, double sign)
    {
        if (!compatible(left, right)) return invalid();
        if (left.unit == calc_unit::number && left.value == 0) left.unit = right.unit;
        if (right.unit == calc_unit::number && right.value == 0) right.unit = left.unit;
        left.value += right.value * sign;
        return left;
    }

    static calc_value multiply(calc_value left, calc_value right)
    {
        if (left.unit != calc_unit::number && right.unit != calc_unit::number) return invalid();
        return left.unit == calc_unit::number
            ? calc_value{left.value * right.value, right.unit}
            : calc_value{left.value * right.value, left.unit};
    }

    static calc_value divide(calc_value left, calc_value right)
    {
        if (right.unit != calc_unit::number || std::abs(right.value) < 1e-12) return invalid();
        left.value /= right.value;
        return left;
    }

    static calc_value invalid() { return {0, calc_unit::invalid}; }

    void whitespace()
    {
        while (position_ < source_.size()
            && std::isspace(static_cast<unsigned char>(source_[position_]))) ++position_;
    }

    bool consume(char expected)
    {
        whitespace();
        if (position_ >= source_.size() || source_[position_] != expected) return false;
        ++position_;
        return true;
    }

    std::string_view source_;
    size_t position_{0};
};

uint8_t parse_hex_pair(char high, char low)
{
    const auto digit = [](char value) -> uint8_t {
        if (value >= '0' && value <= '9') return static_cast<uint8_t>(value - '0');
        if (value >= 'a' && value <= 'f') return static_cast<uint8_t>(value - 'a' + 10);
        if (value >= 'A' && value <= 'F') return static_cast<uint8_t>(value - 'A' + 10);
        return 0;
    };
    return static_cast<uint8_t>((digit(high) << 4U) | digit(low));
}

uint32_t resolved_foreground(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if ((current->style.foreground_rgba & 0xFFU) != 0U) {
            return current->style.foreground_rgba;
        }
    }
    return 0xD1D4DCFFU;
}

float resolved_transform_rotation(const dom_node& node)
{
    float result = 0;
    for (auto* current = &node; current != nullptr; current = current->parent) {
        result += current->painted_transform_rotate_degrees;
    }
    return result;
}

void append_xml_escaped(std::string_view value, std::string& output, bool attribute)
{
    for (const auto character : value) {
        switch (character) {
        case '&': output += "&amp;"; break;
        case '<': output += "&lt;"; break;
        case '>': output += "&gt;"; break;
        case '"': output += attribute ? "&quot;" : "\""; break;
        case '\'': output += attribute ? "&apos;" : "'"; break;
        default: output.push_back(character); break;
        }
    }
}

void serialize_svg_subtree(const dom_node& node, std::string& output, bool root)
{
    if (node.tag == "#text") {
        append_xml_escaped(node.text_content, output, false);
        return;
    }
    if (node.tag.empty() || node.tag.front() == '#') return;

    output.push_back('<');
    output += node.tag;
    bool has_xmlns = false;
    bool has_id = false;
    bool has_class = false;
    bool has_color = false;
    for (const auto& [name, value] : node.attributes) {
        if (name == "xmlns") has_xmlns = true;
        else if (name == "id") has_id = true;
        else if (name == "class") has_class = true;
        else if (name == "color") {
            has_color = true;
            if (root) continue;
        }
        if (name.starts_with("frame-") || name.starts_with("object-")) continue;
        output.push_back(' ');
        output += name;
        output += "=\"";
        append_xml_escaped(value, output, true);
        output.push_back('"');
    }
    if (root && !has_xmlns) output += " xmlns=\"http://www.w3.org/2000/svg\"";
    if (!has_id && !node.id_attribute.empty()) {
        output += " id=\"";
        append_xml_escaped(node.id_attribute, output, true);
        output.push_back('"');
    }
    if (!has_class && !node.class_name.empty()) {
        output += " class=\"";
        append_xml_escaped(node.class_name, output, true);
        output.push_back('"');
    }
    if (root) {
        const auto rgba = resolved_foreground(node);
        char color[10]{};
        std::snprintf(
            color,
            sizeof(color),
            "#%02x%02x%02x",
            static_cast<unsigned>((rgba >> 24U) & 0xFFU),
            static_cast<unsigned>((rgba >> 16U) & 0xFFU),
            static_cast<unsigned>((rgba >> 8U) & 0xFFU));
        output += " color=\"";
        output += color;
        output.push_back('"');
    } else if (has_color) {
        // The authored non-root color was already emitted above.
    }
    output.push_back('>');
    if (!node.text_content.empty()) append_xml_escaped(node.text_content, output, false);
    for (const auto* child : node.children) serialize_svg_subtree(*child, output, false);
    output += "</";
    output += node.tag;
    output.push_back('>');
}

float resolved_font_size(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (current->style.font_size >= 0) return current->style.font_size;
    }
    return 14.0F;
}

float resolved_line_height(const dom_node& node, float font_size)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (current->style.line_height >= 0) return current->style.line_height;
    }
    return font_size * 1.2F;
}

std::string resolved_font_family(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (!current->style.font_family.empty()) return current->style.font_family;
    }
    return "sans-serif";
}

int32_t resolved_font_weight(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (current->style.font_weight > 0) return current->style.font_weight;
    }
    return 400;
}

std::string resolved_text_align(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (!current->style.text_align.empty()) return current->style.text_align;
    }
    return "start";
}

bool resolved_white_space_wraps(const dom_node& node)
{
    for (auto* current = &node; current != nullptr; current = current->parent) {
        if (current->style.white_space.empty()) continue;
        return current->style.white_space != "nowrap"
            && current->style.white_space != "pre";
    }
    return true;
}

bool has_visible_text(const std::string& value)
{
    return std::any_of(value.begin(), value.end(), [](unsigned char character) {
        return !std::isspace(character);
    });
}

std::vector<std::string> wrap_text_lines(
    const std::string& value,
    float available_width,
    float font_size,
    bool allow_wrap)
{
    if (!has_visible_text(value)) return {};
    if (!allow_wrap) {
        std::istringstream source(value);
        std::string collapsed;
        for (std::string word; source >> word;) {
            if (!collapsed.empty()) collapsed.push_back(' ');
            collapsed += word;
        }
        return collapsed.empty() ? std::vector<std::string>{} : std::vector<std::string>{collapsed};
    }
    const auto character_width = std::max(1.0F, font_size * 0.56F);
    const auto maximum_characters = static_cast<size_t>(std::max(
        1.0F,
        std::floor(std::max(0.0F, available_width) / character_width)));
    std::istringstream source(value);
    std::vector<std::string> lines;
    std::string current;
    std::string word;
    while (source >> word) {
        while (word.size() > maximum_characters) {
            if (!current.empty()) {
                lines.push_back(std::move(current));
                current.clear();
            }
            lines.push_back(word.substr(0, maximum_characters));
            word.erase(0, maximum_characters);
        }
        if (word.empty()) continue;
        if (current.empty()) {
            current = std::move(word);
        } else if (current.size() + 1U + word.size() <= maximum_characters) {
            current.push_back(' ');
            current += word;
        } else {
            lines.push_back(std::move(current));
            current = std::move(word);
        }
    }
    if (!current.empty()) lines.push_back(std::move(current));
    if (lines.empty()) lines.emplace_back();
    return lines;
}

uint32_t append_scene_string(
    const std::string& value,
    std::vector<htmlml_scene_string>& strings,
    std::vector<char>& bytes)
{
    const auto index = static_cast<uint32_t>(strings.size());
    const auto offset = static_cast<uint32_t>(bytes.size());
    bytes.insert(bytes.end(), value.begin(), value.end());
    strings.push_back(htmlml_scene_string{offset, static_cast<uint32_t>(value.size())});
    return index;
}

} // namespace

native_document::native_document()
{
    clear();
}

dom_node& native_document::body() noexcept
{
    return *body_;
}

const dom_node& native_document::body() const noexcept
{
    return *body_;
}

dom_node& native_document::create_element(std::string tag)
{
    auto node = std::make_unique<dom_node>();
    node->id = next_node_id_++;
    node->tag = std::move(tag);
    if (node->tag == "#text") {
        node->style.display = display_mode::inline_block;
    } else if (node->tag == "style" || node->tag == "script" || node->tag == "head"
        || node->tag == "meta" || node->tag == "link" || node->tag == "title"
        || node->tag == "base" || node->tag == "template") {
        // HTML metadata and scripting nodes do not generate boxes. Keep this
        // in the DOM layer as well as the V8 UA defaults so native callers and
        // reparents cannot accidentally turn their text into scene commands.
        node->style.display = display_mode::none;
    }
    auto* result = node.get();
    nodes_.push_back(std::move(node));
    dirty_ = true;
    return *result;
}

bool native_document::append_child(dom_node& parent, dom_node& child)
{
    if (&parent == &child || child.parent != nullptr) {
        return false;
    }
    child.parent = &parent;
    parent.children.push_back(&child);
    dirty_ = true;
    return true;
}

void native_document::remove_all_children(dom_node& parent)
{
    for (auto* child : parent.children) {
        child->parent = nullptr;
        child->visible = false;
    }
    parent.children.clear();
    dirty_ = true;
}

dom_node* native_document::find_by_id(const std::string& id) noexcept
{
    const auto match = std::find_if(nodes_.begin(), nodes_.end(), [&id](const auto& node) {
        return node->id_attribute == id;
    });
    return match == nodes_.end() ? nullptr : match->get();
}

std::vector<dom_node*> native_document::query_selector_all(
    dom_node& root,
    const std::string& selector)
{
    std::vector<dom_node*> result;
    collect_matches(root, selector, result);
    return result;
}

dom_node* native_document::hit_test(dom_node& root, float x, float y) noexcept
{
    return hit_test_node(root, x, y, false, false);
}

dom_node* native_document::hit_test_node(
    dom_node& node,
    float x,
    float y,
    bool inherited_visibility_hidden,
    bool inherited_pointer_events_none) noexcept
{
    if (!node.visible) return nullptr;
    const auto visibility_hidden = node.style.visibility_specified
        ? node.style.visibility_hidden
        : inherited_visibility_hidden;
    const auto pointer_events_none = node.style.pointer_events_specified
        ? node.style.pointer_events_none
        : inherited_pointer_events_none;
    const auto origin_x = node.layout.x + resolve_length(
        node.style.transform_origin_x,
        node.layout.width,
        node.layout.width / 2.0F);
    const auto origin_y = node.layout.y + resolve_length(
        node.style.transform_origin_y,
        node.layout.height,
        node.layout.height / 2.0F);
    const auto transformed_left = origin_x
        + (node.layout.x - origin_x) * node.style.transform_scale_x;
    const auto transformed_right = origin_x
        + (node.layout.x + node.layout.width - origin_x) * node.style.transform_scale_x;
    const auto transformed_top = origin_y
        + (node.layout.y - origin_y) * node.style.transform_scale_y;
    const auto transformed_bottom = origin_y
        + (node.layout.y + node.layout.height - origin_y) * node.style.transform_scale_y;
    const auto left = std::min(transformed_left, transformed_right);
    const auto right = std::max(transformed_left, transformed_right);
    const auto top = std::min(transformed_top, transformed_bottom);
    const auto bottom = std::max(transformed_top, transformed_bottom);
    const auto inside = x >= left && y >= top && x <= right && y <= bottom;
    if (inside || !node.style.clip) {
        // Within a stacking context, larger z-index descendants are painted
        // and hit-tested above later zero-z DOM siblings. Component libraries place
        // its pane legend (including the Volume expander) at z-index:6 while
        // the chart canvas is a later sibling at z-index:0.
        auto upper_bound = std::numeric_limits<int32_t>::max();
        while (true) {
            auto next_z = std::numeric_limits<int32_t>::min();
            for (const auto* child : node.children) {
                if (child->style.z_index < upper_bound) {
                    next_z = std::max(next_z, child->style.z_index);
                }
            }
            if (next_z == std::numeric_limits<int32_t>::min()) break;
            for (auto iterator = node.children.rbegin(); iterator != node.children.rend(); ++iterator) {
                if ((*iterator)->style.z_index != next_z) continue;
                if (auto* hit = hit_test_node(
                        **iterator,
                        x,
                        y,
                        visibility_hidden,
                        pointer_events_none);
                    hit != nullptr) {
                    return hit;
                }
            }
            upper_bound = next_z;
        }
    }
    return inside && !visibility_hidden && !pointer_events_none ? &node : nullptr;
}

void native_document::clear()
{
    nodes_.clear();
    next_node_id_ = 1;
    body_ = &create_element("body");
    body_->style.width = {100, length_unit::percent};
    body_->style.height = {100, length_unit::percent};
    body_->style.background_rgba = 0x131722FFU;
    dirty_ = true;
}

void native_document::layout(float viewport_width, float viewport_height)
{
    body_->layout = {0, 0, std::max(1.0F, viewport_width), std::max(1.0F, viewport_height)};
    body_->visible = true;
    layout_children(*body_);
    ++layout_passes_;
    dirty_ = false;
}

void native_document::layout_children(dom_node& parent)
{
    if (parent.children.empty()) {
        return;
    }

    const auto border_left = resolve_length(parent.style.border_left_width, parent.layout.width, 0);
    const auto border_right = resolve_length(parent.style.border_right_width, parent.layout.width, 0);
    const auto border_top = resolve_length(parent.style.border_top_width, parent.layout.height, 0);
    const auto border_bottom = resolve_length(parent.style.border_bottom_width, parent.layout.height, 0);
    const auto padding_left = resolve_length(parent.style.padding_left, parent.layout.width, 0);
    const auto padding_right = resolve_length(parent.style.padding_right, parent.layout.width, 0);
    const auto padding_top = resolve_length(parent.style.padding_top, parent.layout.height, 0);
    const auto padding_bottom = resolve_length(parent.style.padding_bottom, parent.layout.height, 0);
    const layout_rect content{
        parent.layout.x + border_left + padding_left,
        parent.layout.y + border_top + padding_top,
        std::max(0.0F, parent.layout.width
            - border_left - border_right - padding_left - padding_right),
        std::max(0.0F, parent.layout.height
            - border_top - border_bottom - padding_top - padding_bottom)};
    const auto outer_authored_size = [&](const dom_node& node, bool horizontal_axis, float available) {
        const auto authored = horizontal_axis ? node.style.width : node.style.height;
        auto size = resolve_length(authored, available, 0);
        if (!node.style.border_box) {
            size += horizontal_axis
                ? resolve_length(node.style.padding_left, available, 0)
                    + resolve_length(node.style.padding_right, available, 0)
                    + resolve_length(node.style.border_left_width, available, 0)
                    + resolve_length(node.style.border_right_width, available, 0)
                : resolve_length(node.style.padding_top, available, 0)
                    + resolve_length(node.style.padding_bottom, available, 0)
                    + resolve_length(node.style.border_top_width, available, 0)
                    + resolve_length(node.style.border_bottom_width, available, 0);
        }
        return size;
    };
    if (is_grid_container(parent.style.display) && parent.style.grid_two_columns) {
        struct grid_row final {
            dom_node* first{nullptr};
            dom_node* second{nullptr};
            bool spanning{false};
        };
        std::vector<grid_row> rows;
        dom_node* pending = nullptr;
        for (auto* child : parent.children) {
            child->visible = parent.visible && child->style.display != display_mode::none;
            if (!child->visible) {
                child->layout = {};
                continue;
            }
            if (is_out_of_flow(child->style.position)) continue;
            if (child->style.grid_span_all) {
                if (pending != nullptr) rows.push_back({pending, nullptr, false});
                pending = nullptr;
                rows.push_back({child, nullptr, true});
            } else if (pending == nullptr) {
                pending = child;
            } else {
                rows.push_back({pending, child, false});
                pending = nullptr;
            }
        }
        if (pending != nullptr) rows.push_back({pending, nullptr, false});

        auto first_column_width = 0.0F;
        for (const auto& row : rows) {
            if (row.spanning || row.first == nullptr) continue;
            const auto margins = resolve_length(row.first->style.margin_left, content.width, 0)
                + resolve_length(row.first->style.margin_right, content.width, 0);
            first_column_width = std::max(
                first_column_width,
                intrinsic_size(*row.first, true, content.width) + margins);
        }
        first_column_width = std::clamp(
            first_column_width,
            0.0F,
            content.width * 0.6F);
        const auto second_column_width = std::max(0.0F, content.width - first_column_width);
        auto row_y = content.y;
        for (const auto& row : rows) {
            auto row_height = 0.0F;
            const auto measure_height = [&](const dom_node* child) {
                if (child == nullptr) return 0.0F;
                const auto margins = resolve_length(child->style.margin_top, content.height, 0)
                    + resolve_length(child->style.margin_bottom, content.height, 0);
                const auto authored = is_specified(child->style.height)
                    ? outer_authored_size(*child, false, content.height)
                    : intrinsic_size(*child, false, content.height);
                return authored + margins;
            };
            row_height = std::max(measure_height(row.first), measure_height(row.second));
            const auto arrange = [&](dom_node* child, float x, float available_width) {
                if (child == nullptr) return;
                const auto margin_left = resolve_length(child->style.margin_left, available_width, 0);
                const auto margin_right = resolve_length(child->style.margin_right, available_width, 0);
                const auto margin_top = resolve_length(child->style.margin_top, row_height, 0);
                const auto margin_bottom = resolve_length(child->style.margin_bottom, row_height, 0);
                auto width = is_specified(child->style.width)
                    ? outer_authored_size(*child, true, available_width)
                    : std::max(0.0F, available_width - margin_left - margin_right);
                auto height = is_specified(child->style.height)
                    ? outer_authored_size(*child, false, row_height)
                    : std::max(0.0F, row_height - margin_top - margin_bottom);
                layout_child(*child, parent.layout, {
                    x + margin_left,
                    row_y + margin_top,
                    width,
                    height});
            };
            if (row.spanning) {
                arrange(row.first, content.x, content.width);
            } else {
                arrange(row.first, content.x, first_column_width);
                arrange(row.second, content.x + first_column_width, second_column_width);
            }
            row_y += row_height;
        }
        return;
    }
    const auto flex_container = is_flex_container(parent.style.display);
    const auto inline_flow = !flex_container && std::all_of(
        parent.children.begin(),
        parent.children.end(),
        [](const dom_node* child) {
            return child->style.display == display_mode::none
                || is_out_of_flow(child->style.position)
                || is_inline_level(child->style.display);
        });
    const auto horizontal = (flex_container && parent.style.direction == flex_direction::row)
        || inline_flow;
    const auto main_available = horizontal ? content.width : content.height;
    const auto constrain_size = [&](const dom_node& node, bool horizontal_axis, float size, float available) {
        const auto& minimum = horizontal_axis ? node.style.min_width : node.style.min_height;
        const auto& maximum = horizontal_axis ? node.style.max_width : node.style.max_height;
        if (is_specified(maximum)) size = std::min(size, resolve_length(maximum, available, size));
        if (is_specified(minimum)) size = std::max(size, resolve_length(minimum, available, 0));
        return std::max(0.0F, size);
    };
    std::vector<dom_node*> ordered_children = parent.children;
    if (flex_container && parent.style.flex_reverse) {
        std::reverse(ordered_children.begin(), ordered_children.end());
    }
    float fixed = 0;
    float grow = 0;
    float automatic_count = 0;
    std::unordered_map<const dom_node*, float> flex_base_main_sizes;

    for (const auto* child : ordered_children) {
        if (child->style.display == display_mode::none || is_out_of_flow(child->style.position)) {
            continue;
        }
        const auto margin_start = resolve_length(
            horizontal ? child->style.margin_left : child->style.margin_top,
            main_available,
            0);
        const auto margin_end = resolve_length(
            horizontal ? child->style.margin_right : child->style.margin_bottom,
            main_available,
            0);
        fixed += margin_start + margin_end;
        const auto main = horizontal ? child->style.width : child->style.height;
        if (is_specified(main)) {
            const auto basis = constrain_size(
                *child,
                horizontal,
                outer_authored_size(*child, horizontal, main_available),
                main_available);
            flex_base_main_sizes.emplace(child, basis);
            fixed += basis;
        } else {
            const auto intrinsic = constrain_size(
                *child,
                horizontal,
                intrinsic_size(*child, horizontal, main_available),
                main_available);
            if (intrinsic > 0) {
                flex_base_main_sizes.emplace(child, intrinsic);
                fixed += intrinsic;
                grow += std::max(0.0F, child->style.flex_grow);
                continue;
            }
            const auto child_grow = std::max(0.0F, child->style.flex_grow);
            grow += child_grow;
            // Auto-width inline boxes shrink to their contents. In particular,
            // a portal host span whose only child is position:fixed has a zero
            // width box; stretching it across the row makes it intercept input
            // outside the visible popup.
            if (child_grow == 0 && !is_inline_level(child->style.display)) {
                automatic_count += 1.0F;
            }
            flex_base_main_sizes.emplace(child, 0.0F);
        }
    }

    float cursor = 0;
    const auto remaining = std::max(0.0F, main_available - fixed);
    const auto overflow = flex_container ? std::max(0.0F, fixed - main_available) : 0.0F;
    std::unordered_map<const dom_node*, float> flex_shrunk_main_sizes;
    if (overflow > 0) {
        struct shrink_item final {
            const dom_node* node;
            float base;
            float target;
            float minimum;
            float weight;
            bool frozen;
        };
        std::vector<shrink_item> items;
        for (const auto* child : ordered_children) {
            if (child->style.display == display_mode::none
                || is_out_of_flow(child->style.position)) continue;
            const auto known = flex_base_main_sizes.find(child);
            const auto base = known == flex_base_main_sizes.end() ? 0.0F : known->second;
            const auto& minimum_length = horizontal
                ? child->style.min_width : child->style.min_height;
            const auto minimum = is_specified(minimum_length)
                ? resolve_length(minimum_length, main_available, 0)
                : 0.0F;
            const auto weight = std::max(0.0F, child->style.flex_shrink) * base;
            items.push_back({
                child,
                base,
                base,
                std::min(base, std::max(0.0F, minimum)),
                weight,
                weight <= 0 || base <= minimum + 0.001F});
        }

        auto remaining_overflow = overflow;
        for (size_t pass = 0; pass <= items.size() && remaining_overflow > 0.001F; ++pass) {
            auto active_weight = 0.0F;
            for (const auto& item : items) {
                if (!item.frozen) active_weight += item.weight;
            }
            if (active_weight <= 0) break;

            auto consumed = 0.0F;
            auto froze_item = false;
            for (auto& item : items) {
                if (item.frozen) continue;
                const auto requested = remaining_overflow * item.weight / active_weight;
                const auto available_reduction = std::max(0.0F, item.target - item.minimum);
                const auto reduction = std::min(requested, available_reduction);
                item.target -= reduction;
                consumed += reduction;
                if (reduction + 0.001F < requested
                    || item.target <= item.minimum + 0.001F) {
                    item.target = item.minimum;
                    item.frozen = true;
                    froze_item = true;
                }
            }
            remaining_overflow = std::max(0.0F, remaining_overflow - consumed);
            if (!froze_item || consumed <= 0.001F) break;
        }
        for (const auto& item : items) {
            flex_shrunk_main_sizes.emplace(item.node, item.target);
        }
    }
    const auto justify_free = flex_container && grow == 0 && automatic_count == 0
        ? remaining : 0.0F;
    float justify_gap = 0;
    if (parent.style.justify_content == justify_mode::center) cursor = justify_free * 0.5F;
    else if (parent.style.justify_content == justify_mode::end) cursor = justify_free;
    else if (parent.style.justify_content == justify_mode::space_between) {
        const auto flow_count = std::count_if(
            ordered_children.begin(),
            ordered_children.end(),
            [](const dom_node* child) {
                return child->style.display != display_mode::none
                    && !is_out_of_flow(child->style.position);
            });
        if (flow_count > 1) justify_gap = justify_free / static_cast<float>(flow_count - 1);
    }
    for (auto* child : ordered_children) {
        child->visible = parent.visible
            && child->style.display != display_mode::none;
        if (!child->visible) {
            child->layout = {};
            continue;
        }

        if (is_out_of_flow(child->style.position)) {
            const auto& containing = child->style.position == position_mode::fixed
                ? body_->layout
                : parent.layout;
            layout_rect assigned{};
            const auto has_left = is_specified(child->style.left);
            const auto has_top = is_specified(child->style.top);
            const auto has_right = is_specified(child->style.right);
            const auto has_bottom = is_specified(child->style.bottom);
            const auto left = has_left
                ? resolve_length(child->style.left, containing.width, 0)
                : 0;
            const auto top = has_top
                ? resolve_length(child->style.top, containing.height, 0)
                : 0;
            const auto right = has_right
                ? resolve_length(child->style.right, containing.width, 0)
                : 0;
            const auto bottom = has_bottom
                ? resolve_length(child->style.bottom, containing.height, 0)
                : 0;
            const auto intrinsic_width = intrinsic_size(*child, true, containing.width);
            const auto intrinsic_height = intrinsic_size(*child, false, containing.height);
            assigned.width = is_specified(child->style.width)
                ? outer_authored_size(*child, true, containing.width)
                : has_left && has_right
                    ? std::max(0.0F, containing.width - left - right)
                    : intrinsic_width;
            assigned.height = is_specified(child->style.height)
                ? outer_authored_size(*child, false, containing.height)
                : has_top && has_bottom
                    ? std::max(0.0F, containing.height - top - bottom)
                    : intrinsic_height;
            assigned.x = containing.x + (has_left
                ? left
                : has_right ? containing.width - right - assigned.width
                : content.x - containing.x + (horizontal ? cursor : 0));
            assigned.y = containing.y + (has_top
                ? top
                : has_bottom ? containing.height - bottom - assigned.height
                : content.y - containing.y + (horizontal ? 0 : cursor));
            layout_child(*child, containing, assigned);
            continue;
        }

        layout_rect assigned{};
        auto vertical_margin_bottom = 0.0F;
        if (horizontal) {
            const auto margin_left = resolve_length(child->style.margin_left, content.width, 0);
            const auto margin_right = resolve_length(child->style.margin_right, content.width, 0);
            const auto margin_top = resolve_length(child->style.margin_top, content.height, 0);
            const auto margin_bottom = resolve_length(child->style.margin_bottom, content.height, 0);
            const auto intrinsic_width = intrinsic_size(*child, true, content.width);
            const auto intrinsic_height = intrinsic_size(*child, false, content.height);
            auto width = is_specified(child->style.width)
                ? outer_authored_size(*child, true, content.width)
                : intrinsic_width > 0
                    ? intrinsic_width + (grow > 0
                        ? remaining * std::max(0.0F, child->style.flex_grow) / grow
                        : 0.0F)
                : grow > 0
                    ? remaining * std::max(0.0F, child->style.flex_grow) / grow
                    : is_inline_level(child->style.display) ? 0.0F
                    : automatic_count > 0 ? remaining / automatic_count : remaining;
            if (inline_flow && !flex_container) {
                width = std::min(
                    width,
                    std::max(
                        0.0F,
                        content.width - cursor - margin_left - margin_right));
            }
            width = constrain_size(*child, true, width, content.width);
            if (const auto shrunk = flex_shrunk_main_sizes.find(child);
                shrunk != flex_shrunk_main_sizes.end()) {
                width = constrain_size(*child, true, shrunk->second, content.width);
            }
            auto height = is_specified(child->style.height)
                ? outer_authored_size(*child, false, content.height)
                : (child->style.align_self_specified ? child->style.align_self : parent.style.align_items)
                        == align_mode::stretch || intrinsic_height <= 0
                    ? std::max(0.0F, content.height - margin_top - margin_bottom)
                    : std::min(
                        std::max(0.0F, content.height - margin_top - margin_bottom),
                        intrinsic_height);
            height = constrain_size(*child, false, height, content.height);
            const auto alignment = child->style.align_self_specified
                ? child->style.align_self : parent.style.align_items;
            const auto y = alignment == align_mode::center
                ? content.y + margin_top
                    + (content.height - margin_top - margin_bottom - height) * 0.5F
                : alignment == align_mode::end
                    ? content.y + content.height - margin_bottom - height
                    : content.y + margin_top;
            assigned = {
                content.x + cursor + margin_left,
                y,
                width,
                height};
            cursor += margin_left + width + margin_right + justify_gap;
        } else {
            const auto margin_left = resolve_length(child->style.margin_left, content.width, 0);
            const auto margin_right = resolve_length(child->style.margin_right, content.width, 0);
            const auto margin_top = resolve_length(child->style.margin_top, content.height, 0);
            const auto margin_bottom = resolve_length(child->style.margin_bottom, content.height, 0);
            vertical_margin_bottom = margin_bottom;
            const auto intrinsic_height = intrinsic_size(*child, false, content.height);
            auto height = is_specified(child->style.height)
                ? outer_authored_size(*child, false, content.height)
                : intrinsic_height > 0
                    ? intrinsic_height + (flex_container && grow > 0
                        ? remaining * std::max(0.0F, child->style.flex_grow) / grow
                        : 0.0F)
                : !flex_container
                    ? 0.0F
                : grow > 0
                    ? remaining * std::max(0.0F, child->style.flex_grow) / grow
                    : automatic_count > 0 ? remaining / automatic_count : remaining;
            height = constrain_size(*child, false, height, content.height);
            if (const auto shrunk = flex_shrunk_main_sizes.find(child);
                shrunk != flex_shrunk_main_sizes.end()) {
                height = constrain_size(*child, false, shrunk->second, content.height);
            }
            auto width = is_specified(child->style.width)
                ? outer_authored_size(*child, true, content.width)
                : std::max(0.0F, content.width - margin_left - margin_right);
            width = constrain_size(*child, true, width, content.width);
            const auto alignment = child->style.align_self_specified
                ? child->style.align_self : parent.style.align_items;
            const auto x = alignment == align_mode::center
                ? content.x + margin_left
                    + (content.width - margin_left - margin_right - width) * 0.5F
                : alignment == align_mode::end
                    ? content.x + content.width - margin_right - width
                    : content.x + margin_left;
            assigned = {
                x,
                content.y + cursor + margin_top,
                width,
                height};
        }
        auto positioned = assigned;
        if (child->style.position == position_mode::relative) {
            if (is_specified(child->style.left)) {
                positioned.x += resolve_length(child->style.left, content.width, 0);
            } else if (is_specified(child->style.right)) {
                positioned.x -= resolve_length(child->style.right, content.width, 0);
            }
            if (is_specified(child->style.top)) {
                positioned.y += resolve_length(child->style.top, content.height, 0);
            } else if (is_specified(child->style.bottom)) {
                positioned.y -= resolve_length(child->style.bottom, content.height, 0);
            }
        }
        layout_child(*child, parent.layout, positioned);
        if (!horizontal) {
            cursor = assigned.y - content.y
                + child->layout.height
                + vertical_margin_bottom
                + justify_gap;
        }
    }
}

float native_document::intrinsic_size(
    const dom_node& node,
    bool horizontal,
    float available)
{
    const auto constrain = [&](float size) {
        const auto& minimum = horizontal ? node.style.min_width : node.style.min_height;
        const auto& maximum = horizontal ? node.style.max_width : node.style.max_height;
        if (is_specified(maximum)) size = std::min(size, resolve_length(maximum, available, size));
        if (is_specified(minimum)) size = std::max(size, resolve_length(minimum, available, 0));
        return std::max(0.0F, size);
    };
    const auto padding = horizontal
        ? resolve_length(node.style.padding_left, available, 0)
            + resolve_length(node.style.padding_right, available, 0)
            + resolve_length(node.style.border_left_width, available, 0)
            + resolve_length(node.style.border_right_width, available, 0)
        : resolve_length(node.style.padding_top, available, 0)
            + resolve_length(node.style.padding_bottom, available, 0)
            + resolve_length(node.style.border_top_width, available, 0)
            + resolve_length(node.style.border_bottom_width, available, 0);
    const auto authored = horizontal ? node.style.width : node.style.height;
    // A percentage size is indefinite while shrink-to-fitting an auto-sized
    // ancestor. Resolving it against the viewport makes fixed popups with
    // descendants such as `width: 100%` or `height: 100%` measure as the whole
    // viewport. Use the descendant content contribution in that case; once the
    // ancestor has a definite box, layout_children resolves the percentage.
    if (authored.unit == length_unit::pixels) {
        return constrain(resolve_length(authored, available, 0)
            + (node.style.border_box ? 0.0F : padding));
    }
    if (node.tag == "svg" || node.tag == "img" || node.tag == "canvas") {
        const auto attribute = node.attributes.find(horizontal ? "width" : "height");
        if (attribute != node.attributes.end()) {
            const auto intrinsic = parse_number(attribute->second, 0);
            if (intrinsic > 0) return constrain(intrinsic + padding);
        }
        if (node.tag == "svg") {
            const auto view_box = node.attributes.find("viewBox");
            if (view_box != node.attributes.end()) {
                std::istringstream values(view_box->second);
                float x = 0;
                float y = 0;
                float width = 0;
                float height = 0;
                if (values >> x >> y >> width >> height) {
                    const auto intrinsic = horizontal ? width : height;
                    if (intrinsic > 0) return constrain(intrinsic + padding);
                }
            }
        }
    }
    if (node.children.empty()) {
        if (node.text_content.empty()) return constrain(0);
        const auto font_size = resolved_font_size(node);
        return constrain(padding + (horizontal
            ? static_cast<float>(node.text_content.size()) * font_size * 0.56F
            : resolved_line_height(node, font_size)));
    }

    if (is_grid_container(node.style.display) && node.style.grid_two_columns) {
        float first_column = 0;
        float second_column = 0;
        float row_height = 0;
        float total_height = 0;
        bool awaiting_second = false;
        for (const auto* child : node.children) {
            if (child->style.display == display_mode::none
                || is_out_of_flow(child->style.position)) continue;
            const auto child_width = intrinsic_size(*child, true, available)
                + resolve_length(child->style.margin_left, available, 0)
                + resolve_length(child->style.margin_right, available, 0);
            const auto child_height = intrinsic_size(*child, false, available)
                + resolve_length(child->style.margin_top, available, 0)
                + resolve_length(child->style.margin_bottom, available, 0);
            if (child->style.grid_span_all) {
                if (awaiting_second) {
                    total_height += row_height;
                    row_height = 0;
                    awaiting_second = false;
                }
                total_height += child_height;
                first_column = std::max(first_column, child_width);
                continue;
            }
            if (!awaiting_second) {
                first_column = std::max(first_column, child_width);
                row_height = child_height;
                awaiting_second = true;
            } else {
                second_column = std::max(second_column, child_width);
                row_height = std::max(row_height, child_height);
                total_height += row_height;
                row_height = 0;
                awaiting_second = false;
            }
        }
        if (awaiting_second) total_height += row_height;
        return constrain(padding + (horizontal
            ? first_column + second_column
            : total_height));
    }

    const auto flex_container = is_flex_container(node.style.display);
    const auto inline_flow = !flex_container && std::all_of(
        node.children.begin(),
        node.children.end(),
        [](const dom_node* child) {
            return child->style.display == display_mode::none
                || is_out_of_flow(child->style.position)
                || is_inline_level(child->style.display);
        });
    const auto children_horizontal = (flex_container && node.style.direction == flex_direction::row)
        || inline_flow;
    float result = 0;
    for (const auto* child : node.children) {
        if (child->style.display == display_mode::none
            || is_out_of_flow(child->style.position)) continue;
        const auto child_size = intrinsic_size(*child, horizontal, available);
        const auto child_margin = horizontal
            ? resolve_length(child->style.margin_left, available, 0)
                + resolve_length(child->style.margin_right, available, 0)
            : resolve_length(child->style.margin_top, available, 0)
                + resolve_length(child->style.margin_bottom, available, 0);
        const auto accumulates = horizontal == children_horizontal;
        result = accumulates
            ? result + child_size + child_margin
            : std::max(result, child_size + child_margin);
    }
    return constrain(result + padding);
}

void native_document::layout_child(dom_node& child, const layout_rect&, layout_rect assigned)
{
    // A CSS transform changes the painted and hit-test box, not the space the
    // element occupies in its parent's flow. Percentage translations resolve
    // against the transformed element's own border box.
    assigned.x += resolve_length(child.style.transform_translate_x, assigned.width, 0);
    assigned.y += resolve_length(child.style.transform_translate_y, assigned.height, 0);
    child.layout = {
        assigned.x,
        assigned.y,
        std::max(0.0F, assigned.width),
        std::max(0.0F, assigned.height)};
    const auto automatic_height = !is_specified(child.style.height);
    if (automatic_height && child.tag == "#text" && has_visible_text(child.text_content)) {
        const auto font_size = resolved_font_size(child);
        const auto line_height = resolved_line_height(child, font_size);
        const auto lines = wrap_text_lines(
            child.text_content,
            child.layout.width,
            font_size,
            resolved_white_space_wraps(child));
        child.layout.height = std::max(
            child.layout.height,
            static_cast<float>(lines.size()) * line_height);
    }
    layout_children(child);
    if (automatic_height && !child.children.empty()) {
        auto content_bottom = child.layout.y;
        for (const auto* descendant : child.children) {
            if (descendant == nullptr || !descendant->visible
                || is_out_of_flow(descendant->style.position)) continue;
            content_bottom = std::max(
                content_bottom,
                descendant->layout.y + descendant->layout.height
                    + resolve_length(
                        descendant->style.margin_bottom,
                        child.layout.height,
                        0));
        }
        const auto padding_bottom = resolve_length(
            child.style.padding_bottom,
            child.layout.height,
            0);
        const auto border_bottom = resolve_length(
            child.style.border_bottom_width,
            child.layout.height,
            0);
        child.layout.height = std::max(
            child.layout.height,
            content_bottom - child.layout.y + padding_bottom + border_bottom);
    }
}

void native_document::build_scene(
    std::vector<htmlml_scene_command>& commands,
    std::vector<htmlml_scene_string>& strings,
    std::vector<char>& string_bytes) const
{
    commands.clear();
    commands.reserve(nodes_.size());
    strings.clear();
    string_bytes.clear();
    append_scene(*body_, commands, strings, string_bytes, false);
}

void native_document::build_canvas_layouts(
    std::vector<htmlml_canvas_layout>& layouts) const
{
    layouts.clear();
    for (const auto& node : nodes_) {
        if (node->tag != "canvas" || node->parent == nullptr || !node->visible) continue;
        const auto width = node->attributes.find("width");
        const auto height = node->attributes.find("height");
        layouts.push_back(htmlml_canvas_layout{
            node->id,
            0U,
            node->layout.x,
            node->layout.y,
            node->layout.width,
            node->layout.height,
            static_cast<uint32_t>(std::max(
                0.0F,
                width == node->attributes.end() ? 0.0F : parse_number(width->second))),
            static_cast<uint32_t>(std::max(
                0.0F,
                height == node->attributes.end() ? 0.0F : parse_number(height->second)))});
    }
}

void native_document::build_canvas_display_lists(
    std::vector<htmlml_canvas_layer>& layers,
    std::vector<htmlml_canvas_command>& canvas_commands,
    std::vector<htmlml_scene_string>& strings,
    std::vector<char>& string_bytes) const
{
    layers.clear();
    canvas_commands.clear();
    strings.clear();
    string_bytes.clear();

    uint32_t z_order = 0;
    for (const auto& node : nodes_) {
        if (node->tag != "canvas"
            || node->parent == nullptr
            || !node->visible
            || node->canvas_commands.empty()) {
            continue;
        }

        const auto width = node->attributes.find("width");
        const auto height = node->attributes.find("height");
        const auto command_offset = static_cast<uint32_t>(canvas_commands.size());
        const auto string_offset = static_cast<uint32_t>(strings.size());
        canvas_commands.insert(
            canvas_commands.end(),
            node->canvas_commands.begin(),
            node->canvas_commands.end());
        for (const auto& value : node->canvas_strings) {
            const auto byte_offset = static_cast<uint32_t>(string_bytes.size());
            string_bytes.insert(string_bytes.end(), value.begin(), value.end());
            strings.push_back(htmlml_scene_string{
                byte_offset,
                static_cast<uint32_t>(value.size())});
        }

        layers.push_back(htmlml_canvas_layer{
            node->id,
            z_order++,
            command_offset,
            static_cast<uint32_t>(node->canvas_commands.size()),
            string_offset,
            static_cast<uint32_t>(node->canvas_strings.size()),
            0U,
            node->layout.x,
            node->layout.y,
            node->layout.width,
            node->layout.height,
            static_cast<uint32_t>(std::max(
                0.0F,
                width == node->attributes.end() ? 300.0F : parse_number(width->second, 300.0F))),
            static_cast<uint32_t>(std::max(
                0.0F,
                height == node->attributes.end() ? 150.0F : parse_number(height->second, 150.0F))),
            node->canvas_generation});
    }
}

void native_document::append_scene(
    const dom_node& node,
    std::vector<htmlml_scene_command>& commands,
    std::vector<htmlml_scene_string>& strings,
    std::vector<char>& string_bytes,
    bool inherited_visibility_hidden) const
{
    auto opacity = 1.0F;
    for (auto* current = &node; current != nullptr; current = current->parent) {
        opacity *= std::clamp(current->style.opacity, 0.0F, 1.0F);
    }
    if (!node.visible || opacity <= 0.001F) {
        return;
    }
    const auto with_opacity = [opacity](uint32_t rgba) {
        const auto alpha = static_cast<uint32_t>(std::clamp(
            std::lround(static_cast<float>(rgba & 0xFFU) * opacity),
            0L,
            255L));
        return (rgba & 0xFFFFFF00U) | alpha;
    };
    // Unlike display:none, visibility:hidden does not prune descendants. A child
    // may explicitly restore visibility:visible; component libraries use that contract
    // to switch between its collapsed and expanded responsive toolbar wrappers.
    const auto visibility_hidden = node.style.visibility_specified
        ? node.style.visibility_hidden
        : inherited_visibility_hidden;
    const auto paint_self = !visibility_hidden;
    const auto paint_box_in_foreground = [&] {
        for (auto* current = &node; current != nullptr; current = current->parent) {
            // A fixed popup participates in the elevated overlay layer even
            // when a backdrop child uses z-index:-1 relative to that popup.
            if (current->style.position == position_mode::fixed) return true;
            if (current->style.z_index < 0) return false;
            if (current->style.z_index > 0) return true;
            // Fixed-position dialogs and menus form an elevated paint layer in
            // component UI even when its authored z-index is auto. Its DOM
            // backgrounds must be emitted after retained chart canvases, just
            // like their text and SVG foreground commands.
        }
        return false;
    }();
    struct resolved_radii final {
        float top_left;
        float top_right;
        float bottom_right;
        float bottom_left;

        bool any() const noexcept
        {
            return top_left > 0 || top_right > 0 || bottom_right > 0 || bottom_left > 0;
        }
    };
    const auto resolve_radii = [&](css_length top_left, css_length top_right,
                                   css_length bottom_right, css_length bottom_left,
                                   float width, float height) {
        const auto limit = std::max(0.0F, std::min(width, height) * 0.5F);
        const auto reference = std::min(width, height);
        return resolved_radii{
            std::clamp(resolve_length(top_left, reference, 0), 0.0F, limit),
            std::clamp(resolve_length(top_right, reference, 0), 0.0F, limit),
            std::clamp(resolve_length(bottom_right, reference, 0), 0.0F, limit),
            std::clamp(resolve_length(bottom_left, reference, 0), 0.0F, limit)};
    };
    const auto append_pseudo = [&](const node_style::pseudo_element& pseudo) {
        if (!paint_self || !pseudo.generated || pseudo.display_none || pseudo.visibility_hidden) return;
        const auto margin_left = resolve_length(pseudo.margin_left, node.layout.width, 0);
        const auto margin_right = resolve_length(pseudo.margin_right, node.layout.width, 0);
        const auto margin_top = resolve_length(pseudo.margin_top, node.layout.height, 0);
        const auto margin_bottom = resolve_length(pseudo.margin_bottom, node.layout.height, 0);
        const auto inherited_font_size = resolved_font_size(node);
        const auto font_size = pseudo.font_size >= 0 ? pseudo.font_size : inherited_font_size;
        auto line_height = pseudo.line_height >= 0
            ? pseudo.line_height
            : resolved_line_height(node, font_size);
        if (line_height > 0 && line_height <= 4) line_height *= font_size;
        auto width = resolve_length(
            pseudo.width,
            node.layout.width,
            std::max(0.0F, node.layout.width - margin_left - margin_right));
        auto height = resolve_length(
            pseudo.height,
            node.layout.height,
            pseudo.content.empty()
                ? std::max(0.0F, node.layout.height - margin_top - margin_bottom)
                : line_height);
        if (!is_specified(pseudo.width)
            && is_specified(pseudo.left)
            && is_specified(pseudo.right)) {
            width = std::max(
                0.0F,
                node.layout.width
                    - resolve_length(pseudo.left, node.layout.width, 0)
                    - resolve_length(pseudo.right, node.layout.width, 0));
        }
        if (!is_specified(pseudo.height)
            && is_specified(pseudo.top)
            && is_specified(pseudo.bottom)) {
            height = std::max(
                0.0F,
                node.layout.height
                    - resolve_length(pseudo.top, node.layout.height, 0)
                    - resolve_length(pseudo.bottom, node.layout.height, 0));
        }
        auto x = node.layout.x + margin_left;
        auto y = node.layout.y + margin_top;
        if (pseudo.position == position_mode::absolute || pseudo.position == position_mode::fixed) {
            if (is_specified(pseudo.left)) {
                x = node.layout.x + resolve_length(pseudo.left, node.layout.width, 0);
            } else if (is_specified(pseudo.right)) {
                x = node.layout.x + node.layout.width
                    - resolve_length(pseudo.right, node.layout.width, 0) - width;
            }
            if (is_specified(pseudo.top)) {
                y = node.layout.y + resolve_length(pseudo.top, node.layout.height, 0);
            } else if (is_specified(pseudo.bottom)) {
                y = node.layout.y + node.layout.height
                    - resolve_length(pseudo.bottom, node.layout.height, 0) - height;
            }
        }
        if ((pseudo.background_rgba & 0xFFU) != 0U && width > 0 && height > 0) {
            const auto radii = resolve_radii(
                pseudo.border_top_left_radius,
                pseudo.border_top_right_radius,
                pseudo.border_bottom_right_radius,
                pseudo.border_bottom_left_radius,
                width,
                height);
            commands.push_back(htmlml_scene_command{
                radii.any()
                    ? paint_box_in_foreground ? 10U : 7U
                    : paint_box_in_foreground ? 9U : 1U,
                0U,
                x,
                y,
                width,
                height,
                with_opacity(pseudo.background_rgba),
                node.id,
                radii.top_left,
                radii.top_right,
                radii.bottom_right,
                radii.bottom_left,
                0});
        }
        if (has_visible_text(pseudo.content)) {
            std::ostringstream resource;
            resource << font_size << '\t' << line_height << '\t'
                << resolved_font_weight(node) << '\t' << resolved_text_align(node) << '\t'
                << resolved_font_family(node) << '\t' << pseudo.content;
            commands.push_back(htmlml_scene_command{
                3U,
                append_scene_string(resource.str(), strings, string_bytes),
                x,
                y,
                width,
                height,
                (pseudo.foreground_rgba & 0xFFU) != 0U
                    ? with_opacity(pseudo.foreground_rgba)
                    : with_opacity(resolved_foreground(node)),
                node.id});
        }
    };
    const auto radii = resolve_radii(
        node.style.border_top_left_radius,
        node.style.border_top_right_radius,
        node.style.border_bottom_right_radius,
        node.style.border_bottom_left_radius,
        node.layout.width,
        node.layout.height);
    const auto pack_round_rect = [](float packed_radius, float stroke_width) {
        const auto radius_bits = static_cast<uint32_t>(std::clamp(
            std::lround(packed_radius * 100.0F), 0L, 65535L));
        const auto width_bits = static_cast<uint32_t>(std::clamp(
            std::lround(stroke_width * 100.0F), 0L, 65535L));
        return (radius_bits << 16U) | width_bits;
    };
    if (paint_self
        && (node.style.background_rgba & 0xFFU) != 0U
        && node.layout.width > 0
        && node.layout.height > 0) {
        commands.push_back(htmlml_scene_command{
            radii.any()
                ? paint_box_in_foreground ? 10U : 7U
                : paint_box_in_foreground ? 9U : 1U,
            node.style.clip ? 1U : 0U,
            node.layout.x,
            node.layout.y,
            node.layout.width,
            node.layout.height,
            with_opacity(node.style.background_rgba),
            node.id,
            radii.top_left,
            radii.top_right,
            radii.bottom_right,
            radii.bottom_left,
            0});
    }
    const auto append_border_rect = [&](float x, float y, float width, float height, uint32_t rgba) {
        if ((rgba & 0xFFU) == 0U || width <= 0 || height <= 0) return;
        commands.push_back(htmlml_scene_command{
            paint_box_in_foreground ? 9U : 1U,
            0U,
            x,
            y,
            width,
            height,
            with_opacity(rgba),
            node.id});
    };
    if (paint_self && node.layout.width > 0 && node.layout.height > 0) {
        const auto top = std::clamp(
            resolve_length(node.style.border_top_width, node.layout.height, 0),
            0.0F,
            node.layout.height);
        const auto right = std::clamp(
            resolve_length(node.style.border_right_width, node.layout.width, 0),
            0.0F,
            node.layout.width);
        const auto bottom = std::clamp(
            resolve_length(node.style.border_bottom_width, node.layout.height, 0),
            0.0F,
            node.layout.height);
        const auto left = std::clamp(
            resolve_length(node.style.border_left_width, node.layout.width, 0),
            0.0F,
            node.layout.width);
        const auto uniform_width = std::abs(top - right) < 0.01F
            && std::abs(top - bottom) < 0.01F
            && std::abs(top - left) < 0.01F;
        const auto uniform_color = node.style.border_top_rgba == node.style.border_right_rgba
            && node.style.border_top_rgba == node.style.border_bottom_rgba
            && node.style.border_top_rgba == node.style.border_left_rgba;
        if (radii.any() && top > 0 && uniform_width && uniform_color
            && (node.style.border_top_rgba & 0xFFU) != 0U) {
            const auto inset = top * 0.5F;
            commands.push_back(htmlml_scene_command{
                paint_box_in_foreground ? 11U : 8U,
                pack_round_rect(0, top),
                node.layout.x + inset,
                node.layout.y + inset,
                std::max(0.0F, node.layout.width - top),
                std::max(0.0F, node.layout.height - top),
                with_opacity(node.style.border_top_rgba),
                node.id,
                std::max(0.0F, radii.top_left - inset),
                std::max(0.0F, radii.top_right - inset),
                std::max(0.0F, radii.bottom_right - inset),
                std::max(0.0F, radii.bottom_left - inset),
                top});
        } else {
            append_border_rect(
                node.layout.x,
                node.layout.y,
                node.layout.width,
                top,
                node.style.border_top_rgba);
            append_border_rect(
                node.layout.x + node.layout.width - right,
                node.layout.y,
                right,
                node.layout.height,
                node.style.border_right_rgba);
            append_border_rect(
                node.layout.x,
                node.layout.y + node.layout.height - bottom,
                node.layout.width,
                bottom,
                node.style.border_bottom_rgba);
            append_border_rect(
                node.layout.x,
                node.layout.y,
                left,
                node.layout.height,
                node.style.border_left_rgba);
        }
    }
    const auto clip_contents = node.style.clip
        && node.layout.width > 0
        && node.layout.height > 0;
    if (clip_contents) {
        // Kinds 12/13 bracket the element's pseudo/content/descendant paint.
        // The box background and border intentionally remain outside its own
        // overflow clip, matching CSS background/border painting semantics.
        commands.push_back(htmlml_scene_command{
            12U,
            0U,
            node.layout.x,
            node.layout.y,
            node.layout.width,
            node.layout.height,
            0U,
            node.id,
            radii.top_left,
            radii.top_right,
            radii.bottom_right,
            radii.bottom_left,
            0});
    }
    append_pseudo(node.style.before);
    if (paint_self
        && node.tag == "canvas"
        && node.canvas_commands.empty()
        && (!node.canvas_rects.empty() || !node.canvas_lines.empty())) {
        const auto width_iterator = node.attributes.find("width");
        const auto height_iterator = node.attributes.find("height");
        const auto bitmap_width = width_iterator == node.attributes.end()
            ? 300.0F
            : std::max(1.0F, parse_number(width_iterator->second, 300.0F));
        const auto bitmap_height = height_iterator == node.attributes.end()
            ? 150.0F
            : std::max(1.0F, parse_number(height_iterator->second, 150.0F));
        const auto display_width = node.layout.width > 0 ? node.layout.width : bitmap_width;
        const auto display_height = node.layout.height > 0 ? node.layout.height : bitmap_height;
        const auto scale_x = display_width / bitmap_width;
        const auto scale_y = display_height / bitmap_height;
        for (const auto& rect : node.canvas_rects) {
            if ((rect.rgba & 0xFFU) == 0U || rect.width <= 0 || rect.height <= 0) continue;
            commands.push_back(htmlml_scene_command{
                1U,
                0U,
                node.layout.x + rect.x * scale_x,
                node.layout.y + rect.y * scale_y,
                rect.width * scale_x,
                rect.height * scale_y,
                with_opacity(rect.rgba),
                node.id});
        }
        for (const auto& line : node.canvas_lines) {
            if ((line.rgba & 0xFFU) == 0U) continue;
            commands.push_back(htmlml_scene_command{
                2U,
                static_cast<uint32_t>(std::max(1.0F, line.line_width * 100.0F)),
                node.layout.x + line.x1 * scale_x,
                node.layout.y + line.y1 * scale_y,
                node.layout.x + line.x2 * scale_x,
                node.layout.y + line.y2 * scale_y,
                with_opacity(line.rgba),
                node.id});
        }
    }

    if (paint_self && has_visible_text(node.text_content)) {
        const auto font_size = resolved_font_size(node);
        const auto line_height = resolved_line_height(node, font_size);
        auto x = node.layout.x;
        auto y = node.layout.y;
        auto width = node.layout.width;
        auto height = node.layout.height;
        if (width <= 0) {
            width = static_cast<float>(node.text_content.size()) * font_size * 0.56F;
        }
        if (height <= 0 && node.tag == "#text" && node.parent != nullptr) {
            y = node.parent->layout.y;
            height = node.parent->layout.height;
        }
        if (height <= 0) height = line_height;
        const auto lines = wrap_text_lines(
            node.text_content,
            width,
            font_size,
            resolved_white_space_wraps(node));
        for (size_t line_index = 0; line_index < lines.size(); ++line_index) {
            std::ostringstream resource;
            resource << font_size << '\t' << line_height << '\t'
                << resolved_font_weight(node) << '\t' << resolved_text_align(node) << '\t'
                << resolved_font_family(node) << '\t'
                << lines[line_index];
            commands.push_back(htmlml_scene_command{
                3U,
                append_scene_string(resource.str(), strings, string_bytes),
                x,
                y + static_cast<float>(line_index) * line_height,
                width,
                line_height,
                with_opacity(resolved_foreground(node)),
                node.id});
        }
    }

    if (node.tag == "svg" && paint_self) {
        auto box = node.layout;
        if ((box.width <= 0 || box.height <= 0) && node.parent != nullptr) {
            box = node.parent->layout;
        }
        if (box.width > 0 && box.height > 0) {
            const auto view_box = node.attributes.contains("viewBox")
                ? node.attributes.at("viewBox")
                : std::string("0 0 ") + std::to_string(std::max(1.0F, box.width))
                    + " " + std::to_string(std::max(1.0F, box.height));
            std::string markup;
            markup.reserve(256U);
            serialize_svg_subtree(node, markup, true);
            const auto resource_index = append_scene_string(
                view_box + '\t' + markup,
                strings,
                string_bytes);
            commands.push_back(htmlml_scene_command{
                6U,
                resource_index,
                box.x,
                box.y,
                box.width,
                box.height,
                with_opacity(resolved_foreground(node)),
                node.id,
                0,
                0,
                0,
                0,
                resolved_transform_rotation(node)});
        }
        if (clip_contents) {
            commands.push_back(htmlml_scene_command{13U, 0U, 0, 0, 0, 0, 0U, node.id});
        }
        return;
    }

    if (paint_self && node.tag == "path" && node.attributes.contains("d")) {
        const dom_node* svg = node.parent;
        while (svg != nullptr && svg->tag != "svg") svg = svg->parent;
        if (svg != nullptr) {
            auto box = svg->layout;
            if ((box.width <= 0 || box.height <= 0) && svg->parent != nullptr) {
                box = svg->parent->layout;
            }
            const auto view_box = svg->attributes.contains("viewBox")
                ? svg->attributes.at("viewBox")
                : std::string("0 0 ") + std::to_string(std::max(1.0F, box.width))
                    + " " + std::to_string(std::max(1.0F, box.height));
            const auto transform = node.attributes.contains("transform")
                ? node.attributes.at("transform") : std::string{};
            const auto stroke_width = node.attributes.contains("stroke-width")
                ? node.attributes.at("stroke-width") : std::string("1");
            std::string resource = view_box + '\t' + stroke_width + '\t'
                + transform + '\t' + node.attributes.at("d");
            const auto resource_index = append_scene_string(resource, strings, string_bytes);
            const auto fill = node.attributes.contains("fill")
                ? node.attributes.at("fill")
                : svg->attributes.contains("fill") ? svg->attributes.at("fill") : "currentColor";
            if (fill != "none") {
                const auto color = fill == "currentColor"
                    ? resolved_foreground(node)
                    : parse_color(fill);
                commands.push_back(htmlml_scene_command{
                    4U,
                    resource_index,
                    box.x,
                    box.y,
                    box.width,
                    box.height,
                    with_opacity(color == 0 ? resolved_foreground(node) : color),
                    node.id,
                    0,
                    0,
                    0,
                    0,
                    resolved_transform_rotation(node)});
            }
            const auto stroke = node.attributes.contains("stroke")
                ? node.attributes.at("stroke")
                : svg->attributes.contains("stroke") ? svg->attributes.at("stroke") : std::string{};
            if (!stroke.empty() && stroke != "none") {
                const auto color = stroke == "currentColor"
                    ? resolved_foreground(node)
                    : parse_color(stroke);
                commands.push_back(htmlml_scene_command{
                    5U,
                    resource_index,
                    box.x,
                    box.y,
                    box.width,
                    box.height,
                    with_opacity(color == 0 ? resolved_foreground(node) : color),
                    node.id,
                    0,
                    0,
                    0,
                    0,
                    resolved_transform_rotation(node)});
            }
        }
    }
    for (const auto* child : node.children) {
        append_scene(*child, commands, strings, string_bytes, visibility_hidden);
    }
    append_pseudo(node.style.after);
    if (clip_contents) {
        commands.push_back(htmlml_scene_command{13U, 0U, 0, 0, 0, 0, 0U, node.id});
    }
}

uint64_t native_document::layout_passes() const noexcept
{
    return layout_passes_;
}

size_t native_document::node_count() const noexcept
{
    return nodes_.size();
}

size_t native_document::count_tag(const std::string& tag) const noexcept
{
    return static_cast<size_t>(std::count_if(nodes_.begin(), nodes_.end(), [&tag](const auto& node) {
        return node->tag == tag && node->parent != nullptr;
    }));
}

size_t native_document::sum_attribute_bytes(
    const std::string& tag,
    const std::string& attribute) const noexcept
{
    size_t result = 0;
    for (const auto& node : nodes_) {
        if (node->tag != tag || node->parent == nullptr) continue;
        const auto value = node->attributes.find(attribute);
        if (value != node->attributes.end()) result += value->second.size();
    }
    return result;
}

std::string native_document::first_attribute(
    const std::string& tag,
    const std::string& attribute) const
{
    for (const auto& node : nodes_) {
        if (node->tag != tag || node->parent == nullptr) continue;
        const auto value = node->attributes.find(attribute);
        if (value != node->attributes.end() && !value->second.empty()) return value->second;
    }
    return {};
}

std::string native_document::describe_busiest_canvas() const
{
    const dom_node* busiest = nullptr;
    size_t maximum_commands = 0;
    size_t attached_rects = 0;
    size_t attached_lines = 0;
    size_t detached_rects = 0;
    size_t detached_lines = 0;
    uint64_t fill_rect_calls = 0;
    uint64_t probable_volume_fill_rect_calls = 0;
    uint64_t fill_calls = 0;
    uint64_t path_argument_fill_calls = 0;
    uint64_t draw_image_calls = 0;
    uint64_t canvas_draw_image_calls = 0;
    uint64_t self_draw_image_calls = 0;
    uint64_t fill_text_calls = 0;
    uint64_t stroke_text_calls = 0;
    uint64_t clear_rect_calls = 0;
    uint64_t full_clear_calls = 0;
    uint64_t full_clear_reset_calls = 0;
    uint64_t full_clear_current_clip_calls = 0;
    uint64_t full_clear_saved_clip_calls = 0;
    uint64_t clear_bounds_rejected_calls = 0;
    uint64_t maximum_clear_stack_depth = 0;
    std::unordered_map<uint32_t, uint64_t> fill_rect_color_calls;
    std::vector<std::tuple<uint32_t, uint64_t, uint64_t>> volume_generations;
    for (const auto& node : nodes_) {
        if (node->tag != "canvas") continue;
        const auto commands = node->canvas_rects.size() + node->canvas_lines.size();
        auto& rect_count = node->parent == nullptr ? detached_rects : attached_rects;
        auto& line_count = node->parent == nullptr ? detached_lines : attached_lines;
        rect_count += node->canvas_rects.size();
        line_count += node->canvas_lines.size();
        fill_rect_calls += node->canvas_fill_rect_calls;
        probable_volume_fill_rect_calls += node->canvas_probable_volume_fill_rect_calls;
        for (const auto& [generation, count] : node->canvas_probable_volume_by_generation) {
            volume_generations.emplace_back(node->id, generation, count);
        }
        fill_calls += node->canvas_fill_calls;
        path_argument_fill_calls += node->canvas_path_argument_fill_calls;
        draw_image_calls += node->canvas_draw_image_calls;
        canvas_draw_image_calls += node->canvas_canvas_draw_image_calls;
        self_draw_image_calls += node->canvas_self_draw_image_calls;
        fill_text_calls += node->canvas_fill_text_calls;
        stroke_text_calls += node->canvas_stroke_text_calls;
        clear_rect_calls += node->canvas_clear_rect_calls;
        full_clear_calls += node->canvas_full_clear_calls;
        full_clear_reset_calls += node->canvas_full_clear_reset_calls;
        full_clear_current_clip_calls += node->canvas_full_clear_current_clip_calls;
        full_clear_saved_clip_calls += node->canvas_full_clear_saved_clip_calls;
        clear_bounds_rejected_calls += node->canvas_clear_bounds_rejected_calls;
        maximum_clear_stack_depth = std::max(
            maximum_clear_stack_depth,
            node->canvas_max_clear_stack_depth);
        for (const auto& [color, count] : node->canvas_fill_rect_color_calls) {
            fill_rect_color_calls[color] += count;
        }
        if (commands > maximum_commands) {
            maximum_commands = commands;
            busiest = node.get();
        }
    }
    std::vector<const dom_node*> ancestry;
    for (auto* node = busiest; node != nullptr; node = node->parent) ancestry.push_back(node);
    std::reverse(ancestry.begin(), ancestry.end());
    std::ostringstream result;
    result << "canvas ops: fillRect=" << fill_rect_calls
        << "(volume-like=" << probable_volume_fill_rect_calls << ')'
        << ", fill=" << fill_calls
        << "(Path2D=" << path_argument_fill_calls << ')'
        << ", drawImage=" << draw_image_calls
        << "(canvas=" << canvas_draw_image_calls
        << ", self=" << self_draw_image_calls << ')'
        << ", fillText=" << fill_text_calls
        << ", strokeText=" << stroke_text_calls
        << ", clearRect=" << clear_rect_calls
        << "(full=" << full_clear_calls
        << ", reset=" << full_clear_reset_calls
        << ", currentClip=" << full_clear_current_clip_calls
        << ", savedClip=" << full_clear_saved_clip_calls
        << ", boundsRejected=" << clear_bounds_rejected_calls
        << ", maxStack=" << maximum_clear_stack_depth << ')'
        << ", volumeGenerations=[";
    std::sort(volume_generations.begin(), volume_generations.end());
    constexpr size_t reported_volume_generations = 32U;
    const auto first_volume_generation = volume_generations.size() > reported_volume_generations
        ? volume_generations.size() - reported_volume_generations
        : 0U;
    if (first_volume_generation > 0U) result << "...";
    for (size_t index = first_volume_generation; index < volume_generations.size(); ++index) {
        if (index != first_volume_generation || first_volume_generation > 0U) result << ',';
        const auto [node_id, generation, count] = volume_generations[index];
        result << node_id << 'g' << generation << ':' << count;
    }
    result << "]"
        << ", fillRectColors=[";
    std::vector<std::pair<uint32_t, uint64_t>> ordered_fill_rect_colors(
        fill_rect_color_calls.begin(),
        fill_rect_color_calls.end());
    std::sort(
        ordered_fill_rect_colors.begin(),
        ordered_fill_rect_colors.end(),
        [](const auto& left, const auto& right) { return left.second > right.second; });
    for (size_t index = 0; index < std::min<size_t>(8U, ordered_fill_rect_colors.size()); ++index) {
        if (index != 0) result << ',';
        result << "0x" << std::hex << ordered_fill_rect_colors[index].first
            << std::dec << ':' << ordered_fill_rect_colors[index].second;
    }
    result << ']'
        << "; retained attached=" << attached_rects << "R/" << attached_lines << "L"
        << ", detached=" << detached_rects << "R/" << detached_lines << "L"
        << ", DOM graphics=" << count_tag("svg") << "svg/"
        << count_tag("path") << "path/" << count_tag("circle") << "circle"
        << " | busiest canvas commands=" << maximum_commands;
    for (const auto* node : ancestry) {
        result << " -> " << node->tag << '#' << node->id;
        if (!node->id_attribute.empty()) result << "[id=" << node->id_attribute << ']';
        if (!node->class_name.empty()) result << "[class=" << node->class_name << ']';
        result << " layout=" << node->layout.x << ',' << node->layout.y << ','
            << node->layout.width << ',' << node->layout.height
            << " display=" << static_cast<int>(node->style.display)
            << " position=" << static_cast<int>(node->style.position);
    }
    result << " | canvases:";
    for (const auto& node : nodes_) {
        if (node->tag != "canvas" || node->parent == nullptr) continue;
        const auto width = node->attributes.find("width");
        const auto height = node->attributes.find("height");
        result << ' ' << node->id << '@' << node->layout.x << ',' << node->layout.y
            << ',' << node->layout.width << ',' << node->layout.height
            << "[bitmap=" << (width == node->attributes.end() ? "" : width->second)
            << 'x' << (height == node->attributes.end() ? "" : height->second) << ']';
    }
    result << " | reachable-svg:";
    for (const auto& node : nodes_) {
        if (node->tag != "svg") continue;
        auto* root = node.get();
        while (root->parent != nullptr) root = root->parent;
        if (root != body_) continue;
        const auto width = node->attributes.find("width");
        const auto height = node->attributes.find("height");
        result << ' ' << node->id << '@' << node->layout.x << ',' << node->layout.y
            << ',' << node->layout.width << ',' << node->layout.height
            << "[attr=" << (width == node->attributes.end() ? "" : width->second)
            << 'x' << (height == node->attributes.end() ? "" : height->second)
            << ",visible=" << node->visible << ",parent="
            << (node->parent == nullptr ? 0U : node->parent->id) << ']';
    }
    result << " | detached-roots:";
    for (const auto& node : nodes_) {
        if (node.get() == body_ || node->parent != nullptr || node->children.empty()) continue;
        result << " {" << node->tag << '#' << node->id;
        if (!node->class_name.empty()) result << "[class=" << node->class_name << ']';
        result << " children=" << node->children.size() << '}';
    }
    for (const auto& node : nodes_) {
        if (node->tag != "body" || node->parent == nullptr) continue;
        result << " | frame-body-children:";
        for (const auto* child : node->children) {
            result << " {" << child->tag << '#' << child->id;
            if (!child->id_attribute.empty()) result << "[id=" << child->id_attribute << ']';
            if (!child->class_name.empty()) result << "[class=" << child->class_name << ']';
            result << " layout=" << child->layout.x << ',' << child->layout.y << ','
                << child->layout.width << ',' << child->layout.height
                << " display=" << static_cast<int>(child->style.display)
                << " position=" << static_cast<int>(child->style.position)
                << " font=" << child->style.font_size
                << " line=" << child->style.line_height
                << " children=" << child->children.size()
                << " text=" << child->text_content.size();
            if (!child->attributes.empty()) {
                result << " attrs=";
                for (const auto& [name, value] : child->attributes) {
                    result << name << '=' << value.substr(0, 80) << ';';
                }
            }
            if (child->id == 27U) {
                result << " descendants=";
                for (const auto* grandchild : child->children) {
                    result << grandchild->tag << '#' << grandchild->id
                        << '(' << grandchild->layout.width << 'x' << grandchild->layout.height
                        << ",font=" << grandchild->style.font_size
                        << ",line=" << grandchild->style.line_height
                        << ",text=" << grandchild->text_content.size()
                        << ",children=" << grandchild->children.size() << ");";
                }
            }
            result << '}';
        }
        break;
    }
    const dom_node* markup_root = nullptr;
    for (const auto& node : nodes_) {
        if (node->class_name.find("chart-markup-table") != std::string::npos) {
            markup_root = node.get();
            break;
        }
    }
    if (markup_root != nullptr) {
        result << " | markup-tree:";
        std::function<void(const dom_node&, int)> append_tree =
            [&](const dom_node& node, int depth) {
                if (depth > 5) return;
                result << " {" << depth << ':' << node.tag << '#' << node.id;
                if (!node.class_name.empty()) result << "[class=" << node.class_name << ']';
                result << " layout=" << node.layout.x << ',' << node.layout.y << ','
                    << node.layout.width << ',' << node.layout.height
                    << " display=" << static_cast<int>(node.style.display)
                    << " position=" << static_cast<int>(node.style.position)
                    << " children=" << node.children.size() << '}';
                for (const auto* child : node.children) append_tree(*child, depth + 1);
            };
        append_tree(*markup_root, 0);
    }
    for (const auto& node : nodes_) {
        if (node->class_name.find("js-rootresizer__contents") == std::string::npos) continue;
        result << " | layout-tree:";
        std::function<void(const dom_node&, int)> append_layout_tree =
            [&](const dom_node& current, int depth) {
                if (depth > 4) return;
                result << " {" << depth << ':' << current.tag << '#' << current.id;
                if (!current.class_name.empty()) result << "[class=" << current.class_name << ']';
                result << " layout=" << current.layout.x << ',' << current.layout.y << ','
                    << current.layout.width << ',' << current.layout.height
                    << " position=" << static_cast<int>(current.style.position)
                    << " pointer-none=" << current.style.pointer_events_none
                    << " children=" << current.children.size() << '}';
                for (const auto* child : current.children) append_layout_tree(*child, depth + 1);
            };
        append_layout_tree(*node, 0);
        break;
    }
    result << " | aria-controls:";
    for (const auto& node : nodes_) {
        const auto label = node->attributes.find("aria-label");
        if (label == node->attributes.end() || label->second.empty()
            || node->parent == nullptr || !node->visible
            || node->layout.width <= 0 || node->layout.height <= 0) {
            continue;
        }
        result << " {" << node->tag << '#' << node->id
            << " label=" << label->second.substr(0, 48);
        if (!node->class_name.empty()) result << " class=" << node->class_name.substr(0, 96);
        result << " layout=" << node->layout.x << ',' << node->layout.y << ','
            << node->layout.width << ',' << node->layout.height
            << " display=" << static_cast<int>(node->style.display)
            << " position=" << static_cast<int>(node->style.position)
            << " children=" << node->children.size() << '}';
    }
    result << " | end-aria";
    return result.str();
}

layout_rect native_document::busiest_canvas_layout() const noexcept
{
    const dom_node* busiest = nullptr;
    size_t maximum_commands = 0;
    for (const auto& node : nodes_) {
        if (node->tag != "canvas") continue;
        const auto commands = node->canvas_rects.size() + node->canvas_lines.size();
        if (commands > maximum_commands) {
            maximum_commands = commands;
            busiest = node.get();
        }
    }
    return busiest == nullptr ? layout_rect{} : busiest->layout;
}

bool native_document::dirty() const noexcept
{
    return dirty_;
}

void native_document::mark_dirty() noexcept
{
    dirty_ = true;
}

void native_document::update_transform_animation(
    dom_node& node,
    float previous_target_degrees)
{
    const auto target = node.style.transform_rotate_degrees;
    if (!node.transform_animation_initialized) {
        node.transform_animation_initialized = true;
        node.painted_transform_rotate_degrees = target;
        node.transform_animation_target_degrees = target;
        return;
    }
    if (std::abs(target - previous_target_degrees) < 0.001F) return;

    if (node.style.transform_transition_duration_ms <= 0) {
        node.painted_transform_rotate_degrees = target;
        node.transform_animation_target_degrees = target;
        node.transform_animation_active = false;
        return;
    }
    node.transform_animation_from_degrees = node.painted_transform_rotate_degrees;
    node.transform_animation_target_degrees = target;
    node.transform_animation_duration_ms = node.style.transform_transition_duration_ms;
    node.transform_animation_x1 = node.style.transform_transition_x1;
    node.transform_animation_y1 = node.style.transform_transition_y1;
    node.transform_animation_x2 = node.style.transform_transition_x2;
    node.transform_animation_y2 = node.style.transform_transition_y2;
    node.transform_animation_started_nanoseconds =
        std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    node.transform_animation_active = true;
}

bool native_document::advance_animations() noexcept
{
    const auto cubic = [](float t, float first, float second) {
        const auto inverse = 1.0F - t;
        return 3.0F * inverse * inverse * t * first
            + 3.0F * inverse * t * t * second
            + t * t * t;
    };
    const auto cubic_derivative = [](float t, float first, float second) {
        const auto inverse = 1.0F - t;
        return 3.0F * inverse * inverse * first
            + 6.0F * inverse * t * (second - first)
            + 3.0F * t * t * (1.0F - second);
    };
    const auto timing_value = [&](float progress, const dom_node& node) {
        // CSS cubic-bezier timing is parameterized by x, so solve x(t) for the
        // elapsed-time progress before sampling y(t). Newton converges quickly
        // for ordinary curves; bisection keeps unusual but valid curves stable.
        auto parameter = progress;
        for (auto iteration = 0; iteration < 5; ++iteration) {
            const auto error = cubic(
                parameter,
                node.transform_animation_x1,
                node.transform_animation_x2) - progress;
            const auto derivative = cubic_derivative(
                parameter,
                node.transform_animation_x1,
                node.transform_animation_x2);
            if (std::abs(error) < 0.0001F || std::abs(derivative) < 0.0001F) break;
            parameter = std::clamp(parameter - error / derivative, 0.0F, 1.0F);
        }
        auto lower = 0.0F;
        auto upper = 1.0F;
        for (auto iteration = 0; iteration < 8; ++iteration) {
            const auto sampled = cubic(
                parameter,
                node.transform_animation_x1,
                node.transform_animation_x2);
            if (std::abs(sampled - progress) < 0.0001F) break;
            if (sampled < progress) lower = parameter;
            else upper = parameter;
            parameter = (lower + upper) * 0.5F;
        }
        return cubic(
            parameter,
            node.transform_animation_y1,
            node.transform_animation_y2);
    };
    const auto now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    auto advanced = false;
    for (auto& owner : nodes_) {
        auto& node = *owner;
        if (!node.transform_animation_active) continue;
        const auto elapsed_ms = static_cast<float>(
            now - node.transform_animation_started_nanoseconds) / 1'000'000.0F;
        const auto progress = std::clamp(
            elapsed_ms / std::max(0.001F, node.transform_animation_duration_ms),
            0.0F,
            1.0F);
        const auto eased = timing_value(progress, node);
        node.painted_transform_rotate_degrees =
            node.transform_animation_from_degrees
            + (node.transform_animation_target_degrees
                - node.transform_animation_from_degrees) * eased;
        advanced = true;
        if (progress >= 1.0F) node.transform_animation_active = false;
    }
    return advanced;
}

bool native_document::has_active_animations() const noexcept
{
    return std::any_of(nodes_.begin(), nodes_.end(), [](const auto& node) {
        return node->transform_animation_active;
    });
}

css_length native_document::parse_length(const std::string& value)
{
    if (value.empty() || value == "auto") {
        return {};
    }
    if (value.starts_with("calc(") || value.starts_with("min(") || value.starts_with("max(")) {
        if (const auto calculated = calc_parser(value).parse(); calculated.has_value()) {
            return {
                static_cast<float>(calculated->value),
                calculated->unit == calc_unit::percent
                    ? length_unit::percent
                    : length_unit::pixels};
        }
        return {};
    }
    if (value.size() > 1 && value.back() == '%') {
        return {parse_number(std::string_view(value).substr(0, value.size() - 1)), length_unit::percent};
    }
    auto length = value.size();
    if (length >= 2 && value[length - 2] == 'p' && value[length - 1] == 'x') {
        length -= 2;
    }
    return {parse_number(std::string_view(value).substr(0, length)), length_unit::pixels};
}

void native_document::parse_transform_translate(
    const std::string& value,
    css_length& translate_x,
    css_length& translate_y,
    float& scale_x,
    float& scale_y,
    float& rotate_degrees)
{
    translate_x = {};
    translate_y = {};
    scale_x = 1;
    scale_y = 1;
    rotate_degrees = 0;
    if (value.empty() || value == "none") return;

    const auto parse_arguments = [&](std::string_view function) {
        const auto open = value.find(std::string(function) + "(");
        if (open == std::string::npos) return std::vector<std::string>{};
        const auto start = open + function.size() + 1U;
        const auto close = value.find(')', start);
        if (close == std::string::npos) return std::vector<std::string>{};
        auto arguments = value.substr(start, close - start);
        std::replace(arguments.begin(), arguments.end(), ',', ' ');
        std::istringstream stream(arguments);
        std::vector<std::string> result;
        for (std::string argument; stream >> argument;) result.push_back(std::move(argument));
        return result;
    };

    if (const auto arguments = parse_arguments("translate"); !arguments.empty()) {
        translate_x = parse_length(arguments[0]);
        translate_y = arguments.size() > 1 ? parse_length(arguments[1]) : css_length{};
    } else {
        if (const auto arguments = parse_arguments("translateX"); !arguments.empty()) {
            translate_x = parse_length(arguments[0]);
        }
        if (const auto arguments = parse_arguments("translateY"); !arguments.empty()) {
            translate_y = parse_length(arguments[0]);
        }
    }

    if (const auto arguments = parse_arguments("scale"); !arguments.empty()) {
        scale_x = parse_number(arguments[0]);
        scale_y = arguments.size() > 1 ? parse_number(arguments[1]) : scale_x;
    } else {
        if (const auto arguments = parse_arguments("scaleX"); !arguments.empty()) {
            scale_x = parse_number(arguments[0]);
        }
        if (const auto arguments = parse_arguments("scaleY"); !arguments.empty()) {
            scale_y = parse_number(arguments[0]);
        }
    }

    if (const auto arguments = parse_arguments("rotate"); !arguments.empty()) {
        auto angle = arguments[0];
        if (angle.ends_with("deg")) angle.resize(angle.size() - 3U);
        else if (angle.ends_with("turn")) {
            angle.resize(angle.size() - 4U);
            rotate_degrees = parse_number(angle) * 360.0F;
            return;
        }
        rotate_degrees = parse_number(angle);
    }
}

uint32_t native_document::parse_color(const std::string& value)
{
    if (value == "transparent" || value.empty()) return 0;
    if (value == "black") return 0x000000FFU;
    if (value == "white") return 0xFFFFFFFFU;
    if (value == "red") return 0xFF0000FFU;
    if (value == "green") return 0x008000FFU;
    if (value == "blue") return 0x0000FFFFU;
    if (value == "yellow") return 0xFFFF00FFU;
    if (value == "cyan" || value == "aqua") return 0x00FFFFFFU;
    if (value == "magenta" || value == "fuchsia") return 0xFF00FFFFU;
    if (value == "gray" || value == "grey") return 0x808080FFU;
    if (value.starts_with("rgb(")) {
        int red = 0;
        int green = 0;
        int blue = 0;
        if (std::sscanf(value.c_str(), "rgb(%d, %d, %d)", &red, &green, &blue) == 3
            || std::sscanf(value.c_str(), "rgb(%d,%d,%d)", &red, &green, &blue) == 3) {
            return static_cast<uint32_t>(std::clamp(red, 0, 255)) << 24U
                | static_cast<uint32_t>(std::clamp(green, 0, 255)) << 16U
                | static_cast<uint32_t>(std::clamp(blue, 0, 255)) << 8U
                | 0xFFU;
        }
    }
    if (value.starts_with("rgba(")) {
        int red = 0;
        int green = 0;
        int blue = 0;
        float alpha = 0;
        if (std::sscanf(value.c_str(), "rgba(%d, %d, %d, %f)", &red, &green, &blue, &alpha) == 4
            || std::sscanf(value.c_str(), "rgba(%d,%d,%d,%f)", &red, &green, &blue, &alpha) == 4) {
            return static_cast<uint32_t>(std::clamp(red, 0, 255)) << 24U
                | static_cast<uint32_t>(std::clamp(green, 0, 255)) << 16U
                | static_cast<uint32_t>(std::clamp(blue, 0, 255)) << 8U
                | static_cast<uint32_t>(std::clamp(alpha, 0.0F, 1.0F) * 255.0F);
        }
    }
    if ((value.size() == 4 || value.size() == 5) && value.front() == '#') {
        const auto expand = [](char digit) {
            return parse_hex_pair(digit, digit);
        };
        return static_cast<uint32_t>(expand(value[1])) << 24U
            | static_cast<uint32_t>(expand(value[2])) << 16U
            | static_cast<uint32_t>(expand(value[3])) << 8U
            | (value.size() == 5 ? expand(value[4]) : 0xFFU);
    }
    if (value.size() == 7 && value.front() == '#') {
        return static_cast<uint32_t>(parse_hex_pair(value[1], value[2])) << 24U
            | static_cast<uint32_t>(parse_hex_pair(value[3], value[4])) << 16U
            | static_cast<uint32_t>(parse_hex_pair(value[5], value[6])) << 8U
            | 0xFFU;
    }
    if (value.size() == 9 && value.front() == '#') {
        return static_cast<uint32_t>(parse_hex_pair(value[1], value[2])) << 24U
            | static_cast<uint32_t>(parse_hex_pair(value[3], value[4])) << 16U
            | static_cast<uint32_t>(parse_hex_pair(value[5], value[6])) << 8U
            | parse_hex_pair(value[7], value[8]);
    }
    return 0;
}

float native_document::resolve_length(css_length value, float available, float fallback)
{
    switch (value.unit) {
    case length_unit::pixels:
        return value.value;
    case length_unit::percent:
        return available * value.value / 100.0F;
    case length_unit::automatic:
    default:
        return fallback;
    }
}

bool native_document::is_specified(css_length value)
{
    return value.unit != length_unit::automatic;
}

bool native_document::matches_selector(const dom_node& node, const std::string& selector)
{
    if (selector.empty()) return false;
    const auto selector_end = selector.find_first_of(" [:");
    const auto simple = selector.substr(0, selector_end);
    if (simple.empty() || simple == "*") return true;
    if (simple.front() == '#') return node.id_attribute == simple.substr(1);
    if (simple.front() == '.') {
        const auto wanted = simple.substr(1);
        size_t start = 0;
        while (start < node.class_name.size()) {
            const auto end = node.class_name.find(' ', start);
            const auto count = end == std::string::npos ? std::string::npos : end - start;
            if (node.class_name.substr(start, count) == wanted) return true;
            if (end == std::string::npos) break;
            start = end + 1;
        }
        return false;
    }
    return node.tag == simple;
}

void native_document::collect_matches(
    dom_node& node,
    const std::string& selector,
    std::vector<dom_node*>& result)
{
    if (matches_selector(node, selector)) result.push_back(&node);
    for (auto* child : node.children) {
        collect_matches(*child, selector, result);
    }
}

} // namespace htmlml_native

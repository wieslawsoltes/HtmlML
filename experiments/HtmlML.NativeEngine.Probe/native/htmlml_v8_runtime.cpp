#include "htmlml_v8_runtime.h"

#include "htmlml_native_dom.h"

#include <libplatform/libplatform.h>
#include <v8.h>
#include <v8-profiler.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <fstream>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <limits>
#include <mutex>
#include <sstream>
#include <string_view>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#include <process.h>
#else
#include <unistd.h>
#endif

namespace htmlml_native {
namespace {

enum class binding_category : size_t {
    canvas_draw,
    canvas_path,
    canvas_transform,
    canvas_state,
    canvas_backing_store,
    dom_geometry,
    dom_property,
    dom_mutation,
    dom_query,
    style,
    count
};

constexpr size_t binding_category_count = static_cast<size_t>(binding_category::count);
constexpr std::array<const char*, binding_category_count> binding_category_names{
    "canvas-draw",
    "canvas-path",
    "canvas-transform",
    "canvas-state",
    "canvas-backing-store",
    "dom-geometry",
    "dom-property",
    "dom-mutation",
    "dom-query",
    "style"};

struct binding_callback_stats final {
    uint64_t calls{0};
    uint64_t nanoseconds{0};
};

class binding_callback_timer final {
public:
    explicit binding_callback_timer(binding_callback_stats* stats) noexcept
        : stats_(stats)
    {
        if (stats_ != nullptr) started_ = std::chrono::steady_clock::now();
    }

    ~binding_callback_timer()
    {
        if (stats_ == nullptr) return;
        ++stats_->calls;
        stats_->nanoseconds += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - started_).count());
    }

private:
    binding_callback_stats* stats_;
    std::chrono::steady_clock::time_point started_{};
};

enum inline_style_property : uint64_t {
    inline_width = 1ULL << 0U,
    inline_height = 1U << 1U,
    inline_left = 1U << 2U,
    inline_top = 1U << 3U,
    inline_right = 1U << 4U,
    inline_bottom = 1U << 5U,
    inline_display = 1U << 6U,
    inline_position = 1U << 7U,
    inline_flex_direction = 1U << 8U,
    inline_flex_grow = 1U << 9U,
    inline_background = 1U << 10U,
    inline_overflow = 1U << 11U,
    inline_color = 1U << 12U,
    inline_font_size = 1U << 13U,
    inline_font_family = 1U << 14U,
    inline_font_weight = 1U << 15U,
    inline_line_height = 1U << 16U,
    inline_text_align = 1U << 17U,
    inline_visibility = 1U << 18U,
    inline_pointer_events = 1U << 19U,
    inline_padding = 1U << 20U,
    inline_margin = 1U << 21U,
    inline_align_items = 1U << 22U,
    inline_opacity = 1U << 23U,
    inline_flex_wrap = 1U << 24U,
    inline_flex_shrink = 1U << 25U,
    inline_align_self = 1U << 26U,
    inline_min_max_size = 1U << 27U,
    inline_justify_content = 1U << 28U,
    inline_box_sizing = 1U << 29U,
    inline_border_radius = 1U << 30U,
    inline_transform = 1ULL << 31U,
    inline_white_space = 1ULL << 32U
};

std::once_flag v8_initialize_once;
std::unique_ptr<v8::Platform> v8_platform;

display_mode default_display_for_tag(std::string_view tag)
{
    constexpr std::array<std::string_view, 8> non_rendered_tags{
        "base", "head", "link", "meta", "script", "style", "template", "title"};
    if (std::find(non_rendered_tags.begin(), non_rendered_tags.end(), tag)
        != non_rendered_tags.end()) {
        return display_mode::none;
    }
    // Compact HTML UA defaults. Component libraries rely on phrasing elements (most
    // visibly the dialog submit-button span) remaining inline when no author
    // display declaration is present.
    constexpr std::array<std::string_view, 31> inline_tags{
        "a", "abbr", "b", "button", "cite", "code", "em", "i", "img",
        "input", "kbd", "label", "mark", "q", "s", "samp", "select",
        "small", "span", "strong", "sub", "sup", "svg", "textarea", "time",
        "u", "var", "path", "use", "circle", "rect"};
    return std::find(inline_tags.begin(), inline_tags.end(), tag) != inline_tags.end()
        ? display_mode::inline_block
        : display_mode::block;
}

void initialize_v8_process()
{
    std::call_once(v8_initialize_once, [] {
        constexpr const char* executable_name = "htmlml_native_engine";
#if defined(HTMLML_V8_ICU_DATA_PATH)
        v8::V8::InitializeICU(HTMLML_V8_ICU_DATA_PATH);
#else
        v8::V8::InitializeICUDefaultLocation(executable_name);
#endif
        v8::V8::InitializeExternalStartupData(executable_name);
        v8_platform = v8::platform::NewDefaultPlatform();
        v8::V8::InitializePlatform(v8_platform.get());
        v8::V8::Initialize();
    });
}

std::string to_utf8(v8::Isolate* isolate, v8::Local<v8::Value> value)
{
    v8::String::Utf8Value text(isolate, value);
    return *text == nullptr ? std::string{} : std::string(*text, text.length());
}

v8::Local<v8::String> js_string(v8::Isolate* isolate, const char* value)
{
    return v8::String::NewFromUtf8(isolate, value, v8::NewStringType::kNormal)
        .ToLocalChecked();
}

constexpr std::array<uint32_t, 64> sha256_round_constants{
    0x428a2f98U, 0x71374491U, 0xb5c0fbcfU, 0xe9b5dba5U, 0x3956c25bU, 0x59f111f1U, 0x923f82a4U, 0xab1c5ed5U,
    0xd807aa98U, 0x12835b01U, 0x243185beU, 0x550c7dc3U, 0x72be5d74U, 0x80deb1feU, 0x9bdc06a7U, 0xc19bf174U,
    0xe49b69c1U, 0xefbe4786U, 0x0fc19dc6U, 0x240ca1ccU, 0x2de92c6fU, 0x4a7484aaU, 0x5cb0a9dcU, 0x76f988daU,
    0x983e5152U, 0xa831c66dU, 0xb00327c8U, 0xbf597fc7U, 0xc6e00bf3U, 0xd5a79147U, 0x06ca6351U, 0x14292967U,
    0x27b70a85U, 0x2e1b2138U, 0x4d2c6dfcU, 0x53380d13U, 0x650a7354U, 0x766a0abbU, 0x81c2c92eU, 0x92722c85U,
    0xa2bfe8a1U, 0xa81a664bU, 0xc24b8b70U, 0xc76c51a3U, 0xd192e819U, 0xd6990624U, 0xf40e3585U, 0x106aa070U,
    0x19a4c116U, 0x1e376c08U, 0x2748774cU, 0x34b0bcb5U, 0x391c0cb3U, 0x4ed8aa4aU, 0x5b9cca4fU, 0x682e6ff3U,
    0x748f82eeU, 0x78a5636fU, 0x84c87814U, 0x8cc70208U, 0x90befffaU, 0xa4506cebU, 0xbef9a3f7U, 0xc67178f2U};

uint32_t rotate_right(uint32_t value, uint32_t count)
{
    return (value >> count) | (value << (32U - count));
}

std::array<uint8_t, 32> sha256(const uint8_t* data, size_t length)
{
    std::array<uint32_t, 8> state{
        0x6a09e667U, 0xbb67ae85U, 0x3c6ef372U, 0xa54ff53aU,
        0x510e527fU, 0x9b05688cU, 0x1f83d9abU, 0x5be0cd19U};
    const auto bit_length = static_cast<uint64_t>(length) * 8U;
    const auto padded_length = ((length + 9U + 63U) / 64U) * 64U;
    std::vector<uint8_t> padded(padded_length, 0U);
    if (length > 0) std::memcpy(padded.data(), data, length);
    padded[length] = 0x80U;
    for (size_t index = 0; index < 8U; ++index) {
        padded[padded_length - 1U - index] = static_cast<uint8_t>(bit_length >> (index * 8U));
    }

    for (size_t offset = 0; offset < padded.size(); offset += 64U) {
        std::array<uint32_t, 64> words{};
        for (size_t index = 0; index < 16U; ++index) {
            const auto position = offset + index * 4U;
            words[index] = static_cast<uint32_t>(padded[position]) << 24U
                | static_cast<uint32_t>(padded[position + 1U]) << 16U
                | static_cast<uint32_t>(padded[position + 2U]) << 8U
                | static_cast<uint32_t>(padded[position + 3U]);
        }
        for (size_t index = 16; index < words.size(); ++index) {
            const auto s0 = rotate_right(words[index - 15U], 7U)
                ^ rotate_right(words[index - 15U], 18U) ^ (words[index - 15U] >> 3U);
            const auto s1 = rotate_right(words[index - 2U], 17U)
                ^ rotate_right(words[index - 2U], 19U) ^ (words[index - 2U] >> 10U);
            words[index] = words[index - 16U] + s0 + words[index - 7U] + s1;
        }

        auto a = state[0];
        auto b = state[1];
        auto c = state[2];
        auto d = state[3];
        auto e = state[4];
        auto f = state[5];
        auto g = state[6];
        auto h = state[7];
        for (size_t index = 0; index < words.size(); ++index) {
            const auto sum1 = rotate_right(e, 6U) ^ rotate_right(e, 11U) ^ rotate_right(e, 25U);
            const auto choice = (e & f) ^ (~e & g);
            const auto temporary1 = h + sum1 + choice + sha256_round_constants[index] + words[index];
            const auto sum0 = rotate_right(a, 2U) ^ rotate_right(a, 13U) ^ rotate_right(a, 22U);
            const auto majority = (a & b) ^ (a & c) ^ (b & c);
            const auto temporary2 = sum0 + majority;
            h = g;
            g = f;
            f = e;
            e = d + temporary1;
            d = c;
            c = b;
            b = a;
            a = temporary1 + temporary2;
        }
        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
        state[4] += e;
        state[5] += f;
        state[6] += g;
        state[7] += h;
    }

    std::array<uint8_t, 32> result{};
    for (size_t index = 0; index < state.size(); ++index) {
        result[index * 4U] = static_cast<uint8_t>(state[index] >> 24U);
        result[index * 4U + 1U] = static_cast<uint8_t>(state[index] >> 16U);
        result[index * 4U + 2U] = static_cast<uint8_t>(state[index] >> 8U);
        result[index * 4U + 3U] = static_cast<uint8_t>(state[index]);
    }
    return result;
}

std::string to_hex(const std::array<uint8_t, 32>& digest)
{
    std::ostringstream stream;
    stream << std::hex << std::setfill('0');
    for (const auto value : digest) stream << std::setw(2) << static_cast<unsigned>(value);
    return stream.str();
}

std::array<uint8_t, 32> compilation_key_digest(
    const std::string& document_name,
    const std::string& source)
{
    std::vector<uint8_t> value;
    value.reserve(document_name.size() + source.size() + 1U);
    value.insert(value.end(), document_name.begin(), document_name.end());
    value.push_back(0U);
    value.insert(value.end(), source.begin(), source.end());
    return sha256(value.data(), value.size());
}

uint64_t current_process_id() noexcept
{
#if defined(_WIN32)
    return static_cast<uint64_t>(_getpid());
#else
    return static_cast<uint64_t>(getpid());
#endif
}

} // namespace

void prewarm_v8_process()
{
    initialize_v8_process();
}

struct v8_dom_runtime::implementation final {
    struct timer_task final {
        uint32_t id;
        bool animation_frame;
        bool repeating;
        std::chrono::steady_clock::time_point deadline;
        std::chrono::duration<double, std::milli> interval;
        v8::Global<v8::Context> context;
        v8::Global<v8::Function> callback;
        std::vector<v8::Global<v8::Value>> arguments;
    };

    struct pending_promise_rejection final {
        v8::Global<v8::Promise> promise;
        std::string error;
    };

    struct frame_script final {
        std::string source;
        std::string code;
        bool defer{false};
        size_t index{0};
    };

    struct css_declaration final {
        std::string name;
        std::string value;
        bool important{false};
    };

    struct css_rule final {
        std::string selector;
        std::vector<css_declaration> declarations;
    };

    struct connected_resource_task final {
        dom_node* node;
        v8::Global<v8::Context> context;
        v8::Global<v8::Object> wrapper;
    };

    struct resize_observer_state final {
        v8::Global<v8::Context> context;
        v8::Global<v8::Function> callback;
        std::vector<dom_node*> nodes;
        std::unordered_map<uint32_t, std::pair<float, float>> delivered_sizes;
    };

    struct canvas_path_segment final {
        double x1;
        double y1;
        double x2;
        double y2;
    };

    struct canvas_transform final {
        double a{1};
        double b{0};
        double c{0};
        double d{1};
        double e{0};
        double f{0};
    };

    struct canvas_snapshot final {
        canvas_transform transform;
        std::string fill_style{"#000000"};
        std::string stroke_style{"#000000"};
        std::string global_composite_operation{"source-over"};
        std::string line_cap{"butt"};
        std::string line_join{"miter"};
        std::string font{"10px sans-serif"};
        std::string text_align{"start"};
        std::string text_baseline{"alphabetic"};
        std::string image_smoothing_quality{"low"};
        std::string shadow_color{"rgba(0, 0, 0, 0)"};
        double line_width{1};
        double global_alpha{1};
        double miter_limit{10};
        double line_dash_offset{0};
        double shadow_blur{0};
        double shadow_offset_x{0};
        double shadow_offset_y{0};
        bool image_smoothing_enabled{true};
        bool has_clip{false};
        std::vector<double> line_dash;
    };

    struct canvas_state final {
        dom_node* node{nullptr};
        double current_x{0};
        double current_y{0};
        double start_x{0};
        double start_y{0};
        bool has_current{false};
        canvas_transform transform;
        std::vector<canvas_snapshot> stack;
        std::vector<canvas_path_segment> path;
        std::vector<double> line_dash;
        bool has_clip{false};
        canvas_snapshot emitted_paint_state;
        bool has_emitted_paint_state{false};
    };

    struct compilation_cache_entry final {
        v8::Global<v8::UnboundScript> script;
        size_t source_bytes{0};
    };

    implementation(
        native_document& document_value,
        std::function<std::pair<float, float>()> viewport_provider_value,
        std::string compilation_cache_directory_value)
        : document(document_value)
        , viewport_provider(std::move(viewport_provider_value))
        , compilation_cache_directory(std::move(compilation_cache_directory_value))
        , profile_bindings(std::getenv("HTMLML_PROBE_PROFILE_BINDINGS") != nullptr)
        , profile_startup(std::getenv("HTMLML_PROBE_PROFILE_STARTUP") != nullptr)
    {
    }

    ~implementation()
    {
        resize_listeners.clear();
        timers.clear();
        pending_promise_rejections.clear();
        connected_resources.clear();
        resize_observers.clear();
        frame_documents.clear();
        frame_windows.clear();
        frame_load_listeners.clear();
        frame_event_listeners.clear();
        frame_event_listener_contexts.clear();
        frame_event_listener_targets.clear();
        frame_event_listener_captures.clear();
        frame_event_listener_once.clear();
        actual_frame_windows.clear();
        actual_frame_documents.clear();
        canvas_contexts.clear();
        canvas_states.clear();
        for (auto& [key, entry] : compilation_cache) {
            static_cast<void>(key);
            entry.script.Reset();
        }
        compilation_cache.clear();
        compilation_cache_order.clear();
        computed_style_wrappers.clear();
        style_wrappers.clear();
        node_wrappers.clear();
        document_object.Reset();
        style_template.Reset();
        frame_document_template.Reset();
        frame_window_template.Reset();
        document_template.Reset();
        element_template.Reset();
        frame_context.Reset();
        context.Reset();
        if (cpu_profiler != nullptr) {
            cpu_profiler->Dispose();
            cpu_profiler = nullptr;
        }
        if (isolate != nullptr) {
            isolate->Dispose();
            isolate = nullptr;
        }
        delete allocator;
        allocator = nullptr;
    }

    static implementation* current(v8::Isolate* isolate)
    {
        return static_cast<implementation*>(isolate->GetData(0));
    }

    static v8::Intercepted get_window_named_property(
        v8::Local<v8::Name> property,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        if (!property->IsString()) return v8::Intercepted::kNo;
        auto* self = current(info.GetIsolate());
        if (self == nullptr) return v8::Intercepted::kNo;
        const auto name = to_utf8(info.GetIsolate(), property.As<v8::Value>());
        if (name.empty()) return v8::Intercepted::kNo;
        const auto find_named = [&](const auto& recurse, dom_node& root) -> dom_node* {
            if (root.id_attribute == name) return &root;
            const auto named = root.attributes.find("name");
            if (named != root.attributes.end() && named->second == name) return &root;
            for (auto* child : root.children) {
                if (child == nullptr) continue;
                if (auto* match = recurse(recurse, *child); match != nullptr) return match;
            }
            return nullptr;
        };
        auto* match = find_named(find_named, self->active_root());
        if (match == nullptr) return v8::Intercepted::kNo;
        info.GetReturnValue().Set(self->wrap_node(*match));
        return v8::Intercepted::kYes;
    }

    binding_callback_stats* profile(binding_category category) noexcept
    {
        return profile_bindings
            ? &binding_totals[static_cast<size_t>(category)]
            : nullptr;
    }

    static dom_node* unwrap_node(v8::Local<v8::Object> object)
    {
        return object->InternalFieldCount() < 1
            ? nullptr
            : static_cast<dom_node*>(object->GetAlignedPointerFromInternalField(
                0,
                v8::kEmbedderDataTypeTagDefault));
    }

    bool initialize()
    {
        prune_persistent_compilation_cache();
        initialize_v8_process();
        allocator = v8::ArrayBuffer::Allocator::NewDefaultAllocator();
        v8::Isolate::CreateParams params;
        params.array_buffer_allocator = allocator;
        isolate = v8::Isolate::New(params);
        if (isolate == nullptr) {
            last_error = "V8 isolate creation failed";
            return false;
        }
        isolate->SetData(0, this);
        isolate->SetMicrotasksPolicy(v8::MicrotasksPolicy::kExplicit);
        isolate->SetPromiseRejectCallback(promise_rejected);
        if (profile_bindings) {
            cpu_profiler = v8::CpuProfiler::New(isolate);
            cpu_profiler->SetSamplingInterval(250);
        }

        v8::Isolate::Scope isolate_scope(isolate);
        v8::HandleScope handle_scope(isolate);
        auto global_template = v8::ObjectTemplate::New(isolate);
        global_template->SetHandler(v8::NamedPropertyHandlerConfiguration(
            get_window_named_property,
            nullptr,
            nullptr,
            nullptr,
            nullptr,
            {},
            v8::PropertyHandlerFlags::kNonMasking));
        auto local_context = v8::Context::New(isolate, nullptr, global_template);
        local_context->SetSecurityToken(js_string(isolate, "htmlml-native-origin"));
        local_context->SetAlignedPointerInEmbedderData(
            1,
            &document.body(),
            v8::kEmbedderDataTypeTagDefault);
        context.Reset(isolate, local_context);
        v8::Context::Scope context_scope(local_context);
        install_templates(local_context);
        install_globals(local_context);
        return true;
    }

    void install_templates(v8::Local<v8::Context> local_context)
    {
        auto element = v8::FunctionTemplate::New(isolate);
        element->SetClassName(js_string(isolate, "HTMLElement"));
        element->InstanceTemplate()->SetInternalFieldCount(1);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "style"), get_style);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "id"), get_id, set_id);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "className"), get_class_name, set_class_name);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "tagName"), get_tag_name);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "nodeName"), get_tag_name);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "localName"), get_local_name);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "nodeType"), get_element_node_type);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "nodeValue"), get_text_content, set_text_content);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "textContent"), get_text_content, set_text_content);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "innerText"), get_text_content, set_text_content);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "namespaceURI"), get_namespace_uri);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "children"), get_children);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "childNodes"), get_children);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "parentNode"), get_parent_node);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "parentElement"), get_parent_node);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "firstChild"), get_first_element_child);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "lastChild"), get_last_child);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "nextSibling"), get_next_sibling);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "previousSibling"), get_previous_sibling);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "isConnected"), get_is_connected);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "firstElementChild"), get_first_element_child);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "ownerSVGElement"), get_owner_svg_element);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "dataset"), get_dataset);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "attributes"), get_attributes);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "ownerDocument"), get_owner_document);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "classList"), get_class_list);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "clientWidth"), get_client_width);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "clientHeight"), get_client_height);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "offsetWidth"), get_client_width);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "offsetHeight"), get_client_height);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "offsetLeft"), get_offset_left);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "offsetTop"), get_offset_top);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "offsetParent"), get_offset_parent);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "width"), get_element_width, set_element_width);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "height"), get_element_height, set_element_height);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "type"), get_element_type, set_element_type);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "checked"), get_checked, set_checked);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "selected"), get_selected, set_selected);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "disabled"), get_disabled, set_disabled);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "src"), get_element_url, set_element_src);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "href"), get_element_url, set_element_href);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "innerHTML"), get_inner_html, set_inner_html);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "contentWindow"), get_content_window);
        element->InstanceTemplate()->SetNativeDataProperty(js_string(isolate, "contentDocument"), get_content_document);
        element->PrototypeTemplate()->Set(
            js_string(isolate, "appendChild"),
            v8::FunctionTemplate::New(isolate, append_child));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "append"),
            v8::FunctionTemplate::New(isolate, append_child));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "prepend"),
            v8::FunctionTemplate::New(isolate, prepend_child));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "before"),
            v8::FunctionTemplate::New(isolate, insert_before_self));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "after"),
            v8::FunctionTemplate::New(isolate, insert_after_self));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "removeChild"),
            v8::FunctionTemplate::New(isolate, remove_child));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "replaceChildren"),
            v8::FunctionTemplate::New(isolate, replace_children));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "remove"),
            v8::FunctionTemplate::New(isolate, remove_element));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "insertAdjacentElement"),
            v8::FunctionTemplate::New(isolate, insert_adjacent_element));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "insertBefore"),
            v8::FunctionTemplate::New(isolate, insert_before));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "cloneNode"),
            v8::FunctionTemplate::New(isolate, clone_node));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getBoundingClientRect"),
            v8::FunctionTemplate::New(isolate, get_bounding_client_rect));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getClientRects"),
            v8::FunctionTemplate::New(isolate, get_client_rects));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "setAttribute"),
            v8::FunctionTemplate::New(isolate, set_attribute));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "setAttributeNS"),
            v8::FunctionTemplate::New(isolate, set_attribute_ns));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "removeAttribute"),
            v8::FunctionTemplate::New(isolate, remove_attribute));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getAttribute"),
            v8::FunctionTemplate::New(isolate, get_attribute));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "hasAttribute"),
            v8::FunctionTemplate::New(isolate, has_attribute));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "querySelectorAll"),
            v8::FunctionTemplate::New(isolate, element_query_selector_all));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "querySelector"),
            v8::FunctionTemplate::New(isolate, element_query_selector));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getElementsByTagName"),
            v8::FunctionTemplate::New(isolate, element_get_elements_by_tag_name));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getElementsByClassName"),
            v8::FunctionTemplate::New(isolate, element_get_elements_by_class_name));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "contains"),
            v8::FunctionTemplate::New(isolate, element_contains));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "matches"),
            v8::FunctionTemplate::New(isolate, element_matches));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "closest"),
            v8::FunctionTemplate::New(isolate, element_closest));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "addEventListener"),
            v8::FunctionTemplate::New(isolate, add_event_listener));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "removeEventListener"),
            v8::FunctionTemplate::New(isolate, remove_event_listener));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "dispatchEvent"),
            v8::FunctionTemplate::New(isolate, dispatch_event));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "getContext"),
            v8::FunctionTemplate::New(isolate, canvas_get_context));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "decode"),
            v8::FunctionTemplate::New(isolate, image_decode));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "setPointerCapture"),
            v8::FunctionTemplate::New(isolate, set_pointer_capture));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "releasePointerCapture"),
            v8::FunctionTemplate::New(isolate, release_pointer_capture));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "hasPointerCapture"),
            v8::FunctionTemplate::New(isolate, has_pointer_capture));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "focus"),
            v8::FunctionTemplate::New(isolate, element_focus));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "blur"),
            v8::FunctionTemplate::New(isolate, element_blur));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "select"),
            v8::FunctionTemplate::New(isolate, element_select));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "setSelectionRange"),
            v8::FunctionTemplate::New(isolate, element_set_selection_range));
        element->PrototypeTemplate()->Set(
            js_string(isolate, "click"),
            v8::FunctionTemplate::New(isolate, element_click));
        element_template.Reset(isolate, element);

        auto style = v8::ObjectTemplate::New(isolate);
        style->SetInternalFieldCount(2);
        const char* properties[] = {
            "width", "height", "minWidth", "minHeight", "maxWidth", "maxHeight",
            "left", "top", "right", "bottom",
            "display", "position", "flexDirection", "flexGrow", "flexShrink", "flexWrap",
            "alignItems", "alignSelf", "justifyContent", "boxSizing", "borderRadius", "transform", "transformOrigin",
            "zIndex", "opacity",
            "background", "backgroundColor", "borderTopWidth", "borderTopColor", "overflow", "color",
            "fontSize", "fontFamily", "fontWeight", "lineHeight", "textAlign", "whiteSpace",
            "visibility", "pointerEvents"
        };
        for (const auto* property : properties) {
            style->SetNativeDataProperty(js_string(isolate, property), get_style_property, set_style_property);
        }
        style->Set(
            js_string(isolate, "setProperty"),
            v8::FunctionTemplate::New(isolate, style_set_property));
        style->Set(
            js_string(isolate, "getPropertyValue"),
            v8::FunctionTemplate::New(isolate, style_get_property));
        style->Set(
            js_string(isolate, "removeProperty"),
            v8::FunctionTemplate::New(isolate, style_remove_property));
        style_template.Reset(isolate, style);

        auto frame_document = v8::ObjectTemplate::New(isolate);
        frame_document->SetInternalFieldCount(1);
        frame_document->Set(
            js_string(isolate, "open"),
            v8::FunctionTemplate::New(isolate, frame_document_open));
        frame_document->Set(
            js_string(isolate, "write"),
            v8::FunctionTemplate::New(isolate, frame_document_write));
        frame_document->Set(
            js_string(isolate, "close"),
            v8::FunctionTemplate::New(isolate, frame_document_close));
        // The owner realm can inspect contentDocument before the real frame
        // context has hydrated. Keep the provisional document API-shaped so
        // geometry probes see an empty document instead of a TypeError.
        frame_document->Set(
            js_string(isolate, "querySelectorAll"),
            v8::FunctionTemplate::New(isolate, empty_array));
        frame_document->Set(
            js_string(isolate, "querySelector"),
            v8::FunctionTemplate::New(isolate, return_null));
        frame_document->Set(
            js_string(isolate, "getElementById"),
            v8::FunctionTemplate::New(isolate, return_null));
        frame_document_template.Reset(isolate, frame_document);

        auto frame_window = v8::ObjectTemplate::New(isolate);
        frame_window->SetInternalFieldCount(1);
        frame_window->Set(
            js_string(isolate, "addEventListener"),
            v8::FunctionTemplate::New(isolate, frame_window_add_event_listener));
        frame_window->Set(
            js_string(isolate, "removeEventListener"),
            v8::FunctionTemplate::New(isolate, remove_event_listener));
        frame_window_template.Reset(isolate, frame_window);

        auto document_template = v8::ObjectTemplate::New(isolate);
        document_template->SetInternalFieldCount(1);
        document_template->SetNativeDataProperty(js_string(isolate, "body"), get_body);
        document_template->SetNativeDataProperty(js_string(isolate, "documentElement"), get_body);
        document_template->SetNativeDataProperty(js_string(isolate, "head"), get_body);
        document_template->SetNativeDataProperty(js_string(isolate, "nodeType"), get_document_node_type);
        document_template->SetNativeDataProperty(js_string(isolate, "defaultView"), get_default_view);
        document_template->SetNativeDataProperty(js_string(isolate, "activeElement"), get_active_element);
        document_template->Set(
            js_string(isolate, "createElement"),
            v8::FunctionTemplate::New(isolate, create_element));
        document_template->Set(
            js_string(isolate, "createElementNS"),
            v8::FunctionTemplate::New(isolate, create_element_ns));
        document_template->Set(
            js_string(isolate, "createTextNode"),
            v8::FunctionTemplate::New(isolate, create_text_node));
        document_template->Set(
            js_string(isolate, "createDocumentFragment"),
            v8::FunctionTemplate::New(isolate, create_document_fragment));
        document_template->Set(
            js_string(isolate, "getElementById"),
            v8::FunctionTemplate::New(isolate, get_element_by_id));
        document_template->Set(
            js_string(isolate, "querySelectorAll"),
            v8::FunctionTemplate::New(isolate, document_query_selector_all));
        document_template->Set(
            js_string(isolate, "querySelector"),
            v8::FunctionTemplate::New(isolate, document_query_selector));
        document_template->Set(
            js_string(isolate, "elementFromPoint"),
            v8::FunctionTemplate::New(isolate, document_element_from_point));
        document_template->Set(
            js_string(isolate, "getElementsByTagName"),
            v8::FunctionTemplate::New(isolate, document_get_elements_by_tag_name));
        document_template->Set(
            js_string(isolate, "getElementsByClassName"),
            v8::FunctionTemplate::New(isolate, document_get_elements_by_class_name));
        document_template->Set(
            js_string(isolate, "createRange"),
            v8::FunctionTemplate::New(isolate, create_range));
        document_template->Set(
            js_string(isolate, "addEventListener"),
            v8::FunctionTemplate::New(isolate, add_event_listener));
        document_template->Set(
            js_string(isolate, "removeEventListener"),
            v8::FunctionTemplate::New(isolate, remove_event_listener));
        document_template->Set(
            js_string(isolate, "dispatchEvent"),
            v8::FunctionTemplate::New(isolate, dispatch_event));
        document_template->Set(
            js_string(isolate, "hasFocus"),
            v8::FunctionTemplate::New(isolate, document_has_focus));
        document_template->Set(
            js_string(isolate, "getSelection"),
            v8::FunctionTemplate::New(isolate, get_selection));
        this->document_template.Reset(isolate, document_template);
        auto document_value = document_template->NewInstance(local_context).ToLocalChecked();
        document_value->SetAlignedPointerInInternalField(
            0,
            this,
            v8::kEmbedderDataTypeTagDefault);
        document_object.Reset(isolate, document_value);
    }

    void install_globals(v8::Local<v8::Context> local_context)
    {
        auto global = local_context->Global();
        global->Set(local_context, js_string(isolate, "window"), global).Check();
        global->Set(local_context, js_string(isolate, "self"), global).Check();
        global->Set(
            local_context,
            js_string(isolate, "document"),
            document_object.Get(isolate)).Check();
        global->Set(
            local_context,
            js_string(isolate, "getComputedStyle"),
            v8::Function::New(local_context, get_computed_style).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "requestAnimationFrame"),
            v8::Function::New(local_context, request_animation_frame).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "cancelAnimationFrame"),
            v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "setTimeout"),
            v8::Function::New(local_context, set_timeout).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "clearTimeout"),
            v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "setInterval"),
            v8::Function::New(local_context, set_interval).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "clearInterval"),
            v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "addEventListener"),
            v8::Function::New(local_context, add_event_listener).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "dispatchEvent"),
            v8::Function::New(local_context, dispatch_event).ToLocalChecked()).Check();
        auto event_template = v8::FunctionTemplate::New(isolate, event_constructor);
        global->Set(
            local_context,
            js_string(isolate, "Event"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "CustomEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        auto element_constructor = element_template.Get(isolate)->GetFunction(local_context).ToLocalChecked();
        element_constructor->Set(
            local_context,
            js_string(isolate, "ELEMENT_NODE"),
            v8::Integer::New(isolate, 1)).Check();
        element_constructor->Set(
            local_context,
            js_string(isolate, "TEXT_NODE"),
            v8::Integer::New(isolate, 3)).Check();
        element_constructor->Set(
            local_context,
            js_string(isolate, "DOCUMENT_NODE"),
            v8::Integer::New(isolate, 9)).Check();
        element_constructor->Set(
            local_context,
            js_string(isolate, "DOCUMENT_FRAGMENT_NODE"),
            v8::Integer::New(isolate, 11)).Check();
        global->Set(local_context, js_string(isolate, "Node"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Element"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLButtonElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLCanvasElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLIFrameElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLImageElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLInputElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "SVGElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Document"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "DocumentFragment"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Window"), element_constructor).Check();
        global->Set(
            local_context,
            js_string(isolate, "getSelection"),
            v8::Function::New(local_context, get_selection).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "__htmlMlCreateObjectUrl"),
            v8::Function::New(local_context, create_object_url).ToLocalChecked()).Check();
        global->SetNativeDataProperty(
            local_context,
            js_string(isolate, "innerWidth"),
            get_inner_width).Check();
        global->SetNativeDataProperty(
            local_context,
            js_string(isolate, "innerHeight"),
            get_inner_height).Check();
        global->Set(local_context, js_string(isolate, "devicePixelRatio"), v8::Number::New(isolate, 1)).Check();
        global->Set(local_context, js_string(isolate, "parent"), global).Check();
        global->Set(local_context, js_string(isolate, "top"), global).Check();
        global->Set(local_context, js_string(isolate, "opener"), v8::Null(isolate)).Check();

        auto performance = v8::Object::New(isolate);
        performance->Set(
            local_context,
            js_string(isolate, "now"),
            v8::Function::New(local_context, performance_now).ToLocalChecked()).Check();
        performance->Set(
            local_context,
            js_string(isolate, "getEntriesByName"),
            v8::Function::New(local_context, performance_get_entries_by_name).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "mark"),
            v8::Function::New(local_context, performance_entry).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "measure"),
            v8::Function::New(local_context, performance_entry).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "clearMarks"),
            v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "clearMeasures"),
            v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "performance"), performance).Check();
        global->Set(local_context, js_string(isolate, "Image"),
            v8::FunctionTemplate::New(isolate, image_constructor)
                ->GetFunction(local_context).ToLocalChecked()).Check();

        auto location = v8::Object::New(isolate);
        location->Set(
            local_context,
            js_string(isolate, "href"),
            js_string(isolate, "http://127.0.0.1/")).Check();
        location->Set(local_context, js_string(isolate, "protocol"), js_string(isolate, "http:")).Check();
        location->Set(local_context, js_string(isolate, "pathname"), js_string(isolate, "/")).Check();
        location->Set(local_context, js_string(isolate, "search"), js_string(isolate, "")).Check();
        location->Set(local_context, js_string(isolate, "hash"), js_string(isolate, "")).Check();
        global->Set(local_context, js_string(isolate, "location"), location).Check();

        auto navigator = v8::Object::New(isolate);
        navigator->Set(local_context, js_string(isolate, "userAgent"), js_string(isolate, "HtmlML.Native/V8")).Check();
        navigator->Set(local_context, js_string(isolate, "language"), js_string(isolate, "en-US")).Check();
        global->Set(local_context, js_string(isolate, "navigator"), navigator).Check();

        auto console = v8::Object::New(isolate);
        console->Set(local_context, js_string(isolate, "log"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 0)).ToLocalChecked()).Check();
        console->Set(local_context, js_string(isolate, "warn"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 1)).ToLocalChecked()).Check();
        console->Set(local_context, js_string(isolate, "error"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 2)).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "console"), console).Check();
        install_host_bridge(local_context);
    }

    void install_host_bridge(v8::Local<v8::Context> local_context)
    {
        auto bridge = v8::Object::New(isolate);
        const std::array<std::pair<const char*, int32_t>, 3> methods{{
            {"GetBars", 1},
            {"SubscribeBars", 2},
            {"UnsubscribeBars", 3}}};
        for (const auto& [name, kind] : methods) {
            bridge->Set(
                local_context,
                js_string(isolate, name),
                v8::Function::New(
                    local_context,
                    host_bridge_call,
                    v8::Integer::New(isolate, kind)).ToLocalChecked()).Check();
        }
        local_context->Global()->Set(
            local_context,
            js_string(isolate, "dotnetBridge"),
            bridge).Check();
    }

    bool try_take_host_request(std::string& request)
    {
        std::lock_guard lock(host_request_mutex);
        if (host_requests.empty()) return false;
        request = std::move(host_requests.front());
        host_requests.pop_front();
        return true;
    }

    bool try_take_console_message(std::string& message)
    {
        std::lock_guard lock(console_message_mutex);
        if (console_messages.empty()) return false;
        message = std::move(console_messages.front());
        console_messages.pop_front();
        return true;
    }

    static void host_bridge_call(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto local_context = info.GetIsolate()->GetCurrentContext();
        const auto kind = info.Data()->IsInt32()
            ? info.Data().As<v8::Int32>()->Value()
            : 0;
        auto request = v8::Object::New(info.GetIsolate());
        const auto set_string = [&](const char* name, int argument) {
            request->Set(
                local_context,
                js_string(info.GetIsolate(), name),
                argument < info.Length()
                    ? js_string(info.GetIsolate(), to_utf8(info.GetIsolate(), info[argument]).c_str())
                    : js_string(info.GetIsolate(), "")).Check();
        };
        request->Set(
            local_context,
            js_string(info.GetIsolate(), "requestId"),
            v8::Number::New(
                info.GetIsolate(),
                static_cast<double>(++self->next_host_request_id))).Check();
        if (kind == 1) {
            request->Set(local_context, js_string(info.GetIsolate(), "kind"), js_string(info.GetIsolate(), "getBars")).Check();
            set_string("symbol", 0);
            set_string("resolution", 1);
            request->Set(
                local_context,
                js_string(info.GetIsolate(), "from"),
                info.Length() > 2 ? info[2] : v8::Undefined(info.GetIsolate())).Check();
            request->Set(
                local_context,
                js_string(info.GetIsolate(), "to"),
                info.Length() > 3 ? info[3] : v8::Undefined(info.GetIsolate())).Check();
        } else if (kind == 2) {
            request->Set(local_context, js_string(info.GetIsolate(), "kind"), js_string(info.GetIsolate(), "subscribeBars")).Check();
            set_string("symbol", 0);
            set_string("resolution", 1);
            set_string("subscriberUid", 2);
        } else if (kind == 3) {
            request->Set(local_context, js_string(info.GetIsolate(), "kind"), js_string(info.GetIsolate(), "unsubscribeBars")).Check();
            set_string("subscriberUid", 0);
        } else {
            info.GetIsolate()->ThrowException(v8::Exception::Error(
                js_string(info.GetIsolate(), "Unknown HtmlML managed bridge operation")));
            return;
        }

        v8::Local<v8::String> json;
        if (!v8::JSON::Stringify(local_context, request).ToLocal(&json)) {
            info.GetIsolate()->ThrowException(v8::Exception::Error(
                js_string(info.GetIsolate(), "HtmlML managed bridge request serialization failed")));
            return;
        }
        {
            std::lock_guard lock(self->host_request_mutex);
            constexpr size_t maximum_host_requests = 1024U;
            if (self->host_requests.size() >= maximum_host_requests) {
                info.GetIsolate()->ThrowException(v8::Exception::Error(
                    js_string(info.GetIsolate(), "HtmlML managed bridge queue is full")));
                return;
            }
            self->host_requests.push_back(to_utf8(info.GetIsolate(), json));
        }
        info.GetReturnValue().Set(v8::True(info.GetIsolate()));
    }

    bool execute(const std::string& source, const std::string& document_name)
    {
        v8::Isolate::Scope isolate_scope(isolate);
        v8::HandleScope handle_scope(isolate);
        auto local_context = context.Get(isolate);
        if (!execute_in_context(local_context, source, document_name, last_error)) {
            return false;
        }
        if (!drain_tasks()) {
            return false;
        }
        if (!promote_pending_promise_error()) return false;
        last_error.clear();
        return true;
    }

    bool evaluate_json(
        const std::string& source,
        const std::string& document_name,
        std::string& result)
    {
        v8::Isolate::Scope isolate_scope(isolate);
        v8::HandleScope handle_scope(isolate);
        auto local_context = context.Get(isolate);
        v8::Context::Scope context_scope(local_context);
        v8::TryCatch try_catch(isolate);
        v8::Local<v8::Script> script;
        v8::Local<v8::Value> value;
        if (!compile_script(source, document_name, script)
            || !script->Run(local_context).ToLocal(&value)) {
            last_error = describe_exception(try_catch, local_context);
            return false;
        }
        isolate->PerformMicrotaskCheckpoint();
        if (!promote_pending_promise_error()) return false;
        if (value->IsUndefined()) value = v8::Null(isolate);
        v8::Local<v8::String> json;
        if (!v8::JSON::Stringify(local_context, value).ToLocal(&json)) {
            last_error = describe_exception(try_catch, local_context);
            return false;
        }
        result = to_utf8(isolate, json);
        last_error.clear();
        return true;
    }

    bool promote_pending_promise_error()
    {
        if (pending_promise_rejections.empty()) return true;
        last_error = std::move(pending_promise_rejections.front().error);
        pending_promise_rejections.clear();
        return false;
    }

    bool drain_tasks()
    {
        if (!pending_frame_hydrations.empty()) {
            auto* frame = pending_frame_hydrations.front();
            pending_frame_hydrations.erase(pending_frame_hydrations.begin());
            if (frame != nullptr && !hydrate_frame(*frame)) {
                last_error = frame_last_error_value;
                return false;
            }
            return true;
        }
        // Dynamically connected resources and zero-delay timers are separate
        // browser task sources. Do not starve React/datafeed timers behind an
        // entire Webpack resource wave; alternate when both are ready.
        if (!connected_resources.empty()
            && (!has_due_timer() || !prefer_timer_task)) {
            prefer_timer_task = true;
            if (profile_startup) ++startup_connected_resources;
            auto resource = std::move(connected_resources.front());
            connected_resources.erase(connected_resources.begin());
            auto resource_context = resource.context.Get(isolate);
            auto wrapper = resource.wrapper.Get(isolate);
            if (resource.node == nullptr || resource_context.IsEmpty() || wrapper.IsEmpty()) return true;
            v8::Context::Scope resource_scope(resource_context);
            if (!load_connected_resource(*resource.node, wrapper)) {
                last_error = frame_last_error_value;
                return false;
            }
            isolate->PerformMicrotaskCheckpoint();
            return true;
        }
        if (resize_observers_pending) {
            resize_observers_pending = false;
            ensure_layout();
            return dispatch_resize_observers();
        }
        const auto now_time = std::chrono::steady_clock::now();
        const auto next = std::min_element(
            timers.begin(),
            timers.end(),
            [](const auto& left, const auto& right) { return left.deadline < right.deadline; });
        if (next == timers.end() || next->deadline > now_time) return true;
        auto task = std::move(*next);
        timers.erase(next);
        prefer_timer_task = false;
        if (profile_startup) {
            if (task.animation_frame) ++startup_raf_executed;
            else ++startup_timer_executed;
        }
        auto task_context = task.context.Get(isolate);
        if (task_context.IsEmpty()) return true;
        v8::Context::Scope task_context_scope(task_context);
        auto callback = task.callback.Get(isolate);
        if (callback.IsEmpty()) return true;
        v8::TryCatch try_catch(isolate);
        std::vector<v8::Local<v8::Value>> arguments;
        if (task.animation_frame) {
            const auto timestamp = std::chrono::duration<double, std::milli>(
                now_time.time_since_epoch()).count();
            arguments.push_back(v8::Number::New(isolate, timestamp));
        } else {
            arguments.reserve(task.arguments.size());
            for (auto& argument : task.arguments) arguments.push_back(argument.Get(isolate));
        }
        active_timer_id = task.id;
        active_timer_cancelled = false;
        binding_callback_timer callback_timer(
            profile_startup ? &startup_task_callbacks : nullptr);
        if (callback->Call(
                task_context,
                task_context->Global(),
                static_cast<int>(arguments.size()),
                arguments.empty() ? nullptr : arguments.data()).IsEmpty()) {
            active_timer_id = 0;
            last_error = std::string(task.animation_frame
                    ? "requestAnimationFrame task failed: "
                    : "timer task failed: ")
                + describe_exception(try_catch, task_context);
            return false;
        }
        isolate->PerformMicrotaskCheckpoint();
        const auto cancelled = active_timer_cancelled;
        active_timer_id = 0;
        active_timer_cancelled = false;
        if (task.repeating && !cancelled) {
            const auto current = std::chrono::steady_clock::now();
            do {
                task.deadline += std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                    task.interval);
            } while (task.deadline <= current);
            timers.push_back(std::move(task));
        }
        return true;
    }

    bool has_due_timer() const noexcept
    {
        const auto now = std::chrono::steady_clock::now();
        return std::any_of(
            timers.begin(),
            timers.end(),
            [now](const auto& timer) { return timer.deadline <= now; });
    }

    void summarize_resize_cpu_profile(const v8::CpuProfile& profile)
    {
        std::unordered_map<std::string, uint64_t> hits;
        std::vector<std::string> path;
        const auto visit = [&](const auto& self, const v8::CpuProfileNode* node) -> void {
            if (node == nullptr) return;
            auto name = std::string(node->GetFunctionNameStr());
            if (name.empty()) name = "(anonymous)";
            const auto resource = std::string(node->GetScriptResourceNameStr());
            if (!resource.empty()) {
                const auto leaf = std::filesystem::path(resource).filename().string();
                name += "@" + (leaf.empty() ? resource : leaf);
            }
            name += "#" + std::to_string(node->GetScriptId())
                + ":" + std::to_string(node->GetLineNumber())
                + "/t" + std::to_string(static_cast<int>(node->GetSourceType()));
            path.push_back(std::move(name));
            const auto hit_count = node->GetHitCount();
            if (hit_count != 0) {
                std::ostringstream key;
                const auto begin = path.size() > 5U ? path.size() - 5U : 0U;
                for (size_t index = begin; index < path.size(); ++index) {
                    if (index != begin) key << '>';
                    key << path[index];
                }
                hits[std::move(key).str()] += hit_count;
            }
            for (int index = 0; index < node->GetChildrenCount(); ++index) {
                self(self, node->GetChild(index));
            }
            path.pop_back();
        };
        visit(visit, profile.GetTopDownRoot());
        std::vector<std::pair<std::string, uint64_t>> ordered(hits.begin(), hits.end());
        std::sort(ordered.begin(), ordered.end(), [](const auto& left, const auto& right) {
            return left.second > right.second;
        });
        std::ostringstream result;
        result << "samples=" << profile.GetSamplesCount() << "/top{";
        const auto limit = std::min<size_t>(12U, ordered.size());
        for (size_t index = 0; index < limit; ++index) {
            if (index != 0) result << ';';
            result << ordered[index].first << ':' << ordered[index].second;
        }
        result << '}';
        last_resize_cpu_profile = std::move(result).str();
    }

    bool dispatch_resize()
    {
        const auto resize_started = std::chrono::steady_clock::now();
        v8::Isolate::Scope isolate_scope(isolate);
        v8::HandleScope handle_scope(isolate);
        {
            auto local_context = context.Get(isolate);
            v8::Context::Scope context_scope(local_context);
            auto global = local_context->Global();
            for (auto& listener : resize_listeners) {
                auto callback = listener.Get(isolate);
                if (callback.IsEmpty()) continue;
                v8::TryCatch try_catch(isolate);
                if (callback->Call(local_context, global, 0, nullptr).IsEmpty()) {
                    last_error = describe_exception(try_catch);
                    return false;
                }
            }
            isolate->PerformMicrotaskCheckpoint();
        }
        const auto outer_listeners_finished = std::chrono::steady_clock::now();
        last_resize_outer_listeners_ns = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                outer_listeners_finished - resize_started).count());

        if (frame_context.IsEmpty()) return true;
        auto local_frame_context = frame_context.Get(isolate);
        v8::Context::Scope frame_scope(local_frame_context);
        v8::Local<v8::String> cpu_profile_title;
        auto cpu_profile_started = false;
        if (cpu_profiler != nullptr) {
            cpu_profile_title = js_string(isolate, "htmlml-resize-listener");
            cpu_profile_started = cpu_profiler->StartProfiling(cpu_profile_title, true)
                == v8::CpuProfilingStatus::kStarted;
        }
        const auto stop_cpu_profile = [&] {
            if (!cpu_profile_started) return;
            if (auto* profile = cpu_profiler->StopProfiling(cpu_profile_title); profile != nullptr) {
                summarize_resize_cpu_profile(*profile);
                profile->Delete();
            }
            cpu_profile_started = false;
        };
        const auto binding_profile_before = binding_totals;
        auto listeners = frame_event_listeners.find("resize");
        if (listeners != frame_event_listeners.end()) {
            auto event = v8::Object::New(isolate);
            event->Set(local_frame_context, js_string(isolate, "type"), js_string(isolate, "resize")).Check();
            event->Set(local_frame_context, js_string(isolate, "target"), local_frame_context->Global()).Check();
            v8::Local<v8::Value> arguments[] = {event};
            ++input_event_dispatch_count;
            for (auto& listener : listeners->second) {
                auto callback = listener.Get(isolate);
                if (callback.IsEmpty()) continue;
                v8::TryCatch try_catch(isolate);
                if (callback->Call(
                        local_frame_context,
                        local_frame_context->Global(),
                        1,
                        arguments).IsEmpty()) {
                    stop_cpu_profile();
                    last_error = describe_exception(try_catch, local_frame_context);
                    return false;
                }
                ++input_callback_invocation_count;
            }
        }
        isolate->PerformMicrotaskCheckpoint();
        stop_cpu_profile();
        for (size_t index = 0; index < binding_category_count; ++index) {
            last_resize_binding_profile[index] = binding_callback_stats{
                binding_totals[index].calls - binding_profile_before[index].calls,
                binding_totals[index].nanoseconds - binding_profile_before[index].nanoseconds};
        }
        const auto frame_listeners_finished = std::chrono::steady_clock::now();
        last_resize_frame_listeners_ns = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                frame_listeners_finished - outer_listeners_finished).count());
        ensure_layout();
        const auto layout_finished = std::chrono::steady_clock::now();
        last_resize_layout_ns = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                layout_finished - frame_listeners_finished).count());
        const auto result = dispatch_resize_observers();
        last_resize_observers_ns = static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - layout_finished).count());
        return result;
    }

    bool dispatch_resize_observers()
    {
        std::vector<resize_observer_state*> pending;
        pending.reserve(resize_observers.size());
        for (const auto& observer : resize_observers) {
            if (observer != nullptr) pending.push_back(observer.get());
        }
        for (auto* observer : pending) {
            if (observer == nullptr || observer->nodes.empty()) continue;
            std::vector<dom_node*> changed_nodes;
            changed_nodes.reserve(observer->nodes.size());
            for (auto* node : observer->nodes) {
                if (node == nullptr) continue;
                const auto size = std::pair{node->layout.width, node->layout.height};
                const auto previous = observer->delivered_sizes.find(node->id);
                if (previous == observer->delivered_sizes.end() || previous->second != size) {
                    changed_nodes.push_back(node);
                    observer->delivered_sizes[node->id] = size;
                }
            }
            if (changed_nodes.empty()) continue;
            auto observer_context = observer->context.Get(isolate);
            auto callback = observer->callback.Get(isolate);
            if (observer_context.IsEmpty() || callback.IsEmpty()) continue;
            v8::Context::Scope observer_scope(observer_context);
            auto entries = v8::Array::New(isolate, static_cast<int>(changed_nodes.size()));
            for (uint32_t index = 0; index < changed_nodes.size(); ++index) {
                auto* node = changed_nodes[index];
                if (node == nullptr) continue;
                auto rect = v8::Object::New(isolate);
                const auto set = [&](const char* name, double value) {
                    rect->Set(
                        observer_context,
                        js_string(isolate, name),
                        v8::Number::New(isolate, value)).Check();
                };
                set("x", node->layout.x);
                set("y", node->layout.y);
                set("width", node->layout.width);
                set("height", node->layout.height);
                set("top", node->layout.y);
                set("left", node->layout.x);
                set("right", node->layout.x + node->layout.width);
                set("bottom", node->layout.y + node->layout.height);
                auto entry = v8::Object::New(isolate);
                entry->Set(observer_context, js_string(isolate, "target"), wrap_node(*node)).Check();
                entry->Set(observer_context, js_string(isolate, "contentRect"), rect).Check();
                auto box_size = v8::Object::New(isolate);
                box_size->Set(
                    observer_context,
                    js_string(isolate, "inlineSize"),
                    v8::Number::New(isolate, node->layout.width)).Check();
                box_size->Set(
                    observer_context,
                    js_string(isolate, "blockSize"),
                    v8::Number::New(isolate, node->layout.height)).Check();
                const auto make_box_array = [&] {
                    auto values = v8::Array::New(isolate, 1);
                    values->Set(observer_context, 0, box_size).Check();
                    return values;
                };
                entry->Set(
                    observer_context,
                    js_string(isolate, "contentBoxSize"),
                    make_box_array()).Check();
                entry->Set(
                    observer_context,
                    js_string(isolate, "borderBoxSize"),
                    make_box_array()).Check();
                entry->Set(
                    observer_context,
                    js_string(isolate, "devicePixelContentBoxSize"),
                    make_box_array()).Check();
                entries->Set(observer_context, index, entry).Check();
            }
            v8::Local<v8::Value> arguments[] = {entries, v8::Undefined(isolate)};
            v8::TryCatch try_catch(isolate);
            if (callback->Call(
                    observer_context,
                    observer_context->Global(),
                    2,
                    arguments).IsEmpty()) {
                last_error = describe_exception(try_catch, observer_context);
                return false;
            }
            ++input_callback_invocation_count;
            isolate->PerformMicrotaskCheckpoint();
        }
        return true;
    }

    static void event_prevent_default(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto context = info.GetIsolate()->GetCurrentContext();
        v8::Local<v8::Value> cancelable;
        if (!info.This()->Get(
                context,
                js_string(info.GetIsolate(), "cancelable")).ToLocal(&cancelable)
            || !cancelable->BooleanValue(info.GetIsolate())) return;
        info.This()->Set(
            context,
            js_string(info.GetIsolate(), "defaultPrevented"),
            v8::True(info.GetIsolate())).Check();
    }

    static void event_stop_propagation(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.This()->Set(
            info.GetIsolate()->GetCurrentContext(),
            js_string(info.GetIsolate(), "__htmlmlPropagationStopped"),
            v8::True(info.GetIsolate())).Check();
    }

    static void event_stop_immediate_propagation(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto context = info.GetIsolate()->GetCurrentContext();
        info.This()->Set(
            context,
            js_string(info.GetIsolate(), "__htmlmlPropagationStopped"),
            v8::True(info.GetIsolate())).Check();
        info.This()->Set(
            context,
            js_string(info.GetIsolate(), "__htmlmlImmediatePropagationStopped"),
            v8::True(info.GetIsolate())).Check();
    }

    bool dispatch_input_event_type(
        const htmlml_input_event& input,
        const char* type,
        dom_node& target)
    {
        ++event_dispatch_counts[type];
        ++event_dispatch_target_counts[type][target.id];
        const auto callback_count_before = input_callback_invocation_count;
        auto local_context = frame_context.IsEmpty()
            ? context.Get(isolate)
            : frame_context.Get(isolate);
        auto event = v8::Object::New(isolate);
        auto target_wrapper = wrap_node(target);
        const auto set_number = [&](const char* name, double value) {
            event->Set(local_context, js_string(isolate, name), v8::Number::New(isolate, value)).Check();
        };
        event->Set(local_context, js_string(isolate, "type"), js_string(isolate, type)).Check();
        event->Set(local_context, js_string(isolate, "target"), target_wrapper).Check();
        event->Set(local_context, js_string(isolate, "srcElement"), target_wrapper).Check();
        event->Set(local_context, js_string(isolate, "view"), local_context->Global()).Check();
        event->Set(local_context, js_string(isolate, "bubbles"), v8::True(isolate)).Check();
        event->Set(local_context, js_string(isolate, "cancelable"), v8::True(isolate)).Check();
        event->Set(local_context, js_string(isolate, "defaultPrevented"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "pointerType"), js_string(isolate, "mouse")).Check();
        event->Set(local_context, js_string(isolate, "isPrimary"), v8::True(isolate)).Check();
        event->Set(
            local_context,
            js_string(isolate, "relatedTarget"),
            current_related_target == nullptr
                ? v8::Local<v8::Value>(v8::Null(isolate))
                : v8::Local<v8::Value>(wrap_node(*current_related_target))).Check();
        event->Set(
            local_context,
            js_string(isolate, "preventDefault"),
            v8::Function::New(local_context, event_prevent_default).ToLocalChecked()).Check();
        event->Set(
            local_context,
            js_string(isolate, "stopPropagation"),
            v8::Function::New(local_context, event_stop_propagation).ToLocalChecked()).Check();
        event->Set(
            local_context,
            js_string(isolate, "stopImmediatePropagation"),
            v8::Function::New(local_context, event_stop_immediate_propagation).ToLocalChecked()).Check();
        set_number("clientX", input.x);
        set_number("clientY", input.y);
        set_number("pageX", input.x);
        set_number("pageY", input.y);
        set_number("screenX", input.x);
        set_number("screenY", input.y);
        set_number("offsetX", input.x - target.layout.x);
        set_number("offsetY", input.y - target.layout.y);
        set_number("movementX", current_movement_x);
        set_number("movementY", current_movement_y);
        set_number("deltaX", input.delta_x);
        set_number("deltaY", input.delta_y);
        set_number("deltaMode", 0);
        set_number("button", 0);
        set_number("buttons", input.kind == HTMLML_INPUT_POINTER_UP ? 0 : (input.flags & 1U));
        set_number("pointerId", 1);
        set_number("detail", std::string_view(type) == "click" ? 1 : 0);
        // MouseEvent.which identifies the changed button and remains 1 for a
        // primary-button mouseup/click even though `buttons` has returned to 0.
        // Release and long-press paths in component libraries still consult this legacy
        // field; reporting 0 made a genuine release look like a hover event.
        const auto mouse_button_event = std::string_view(type) == "pointerdown"
            || std::string_view(type) == "mousedown"
            || std::string_view(type) == "pointerup"
            || std::string_view(type) == "mouseup"
            || std::string_view(type) == "click";
        // Legacy MouseEvent.which also reports the currently held primary
        // button on mousemove. Drag and long-press normalization
        // still reads it in addition to `buttons`; reporting zero here makes
        // an ordered pressed move look like a hover on that path.
        const auto primary_button_held = (input.flags & 1U) != 0U;
        set_number("which", mouse_button_event || primary_button_held ? 1 : 0);
        event->Set(local_context, js_string(isolate, "altKey"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "ctrlKey"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "metaKey"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "shiftKey"), v8::False(isolate)).Check();
        set_number("timeStamp", std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now().time_since_epoch()).count());
        set_number("sequence", static_cast<double>(input.sequence));

        std::vector<uint32_t> ancestry;
        for (auto* node = &target; node != nullptr; node = node->parent) ancestry.push_back(node->id);
        if (std::string_view(type) == "mousemove") {
            std::ostringstream path;
            for (size_t index = 0; index < ancestry.size(); ++index) {
                if (index != 0) path << '>';
                path << ancestry[index];
            }
            last_mousemove_ancestry = path.str();
        }
        ++input_event_dispatch_count;
        v8::Local<v8::Value> arguments[] = {event};
        const auto listeners = frame_event_listeners.find(type);
        const auto listener_contexts = frame_event_listener_contexts.find(type);
        const auto targets = frame_event_listener_targets.find(type);
        const auto captures = frame_event_listener_captures.find(type);
        const auto once_flags = frame_event_listener_once.find(type);
        const auto invoke_listeners = [&](
            uint32_t registered_target,
            v8::Local<v8::Object> current_target,
            bool capture,
            int event_phase) {
            if (listeners == frame_event_listeners.end()) return true;
            event->Set(local_context, js_string(isolate, "currentTarget"), current_target).Check();
            set_number("eventPhase", event_phase);
            for (size_t index = 0; index < listeners->second.size(); ++index) {
                if (listener_contexts != frame_event_listener_contexts.end()
                    && index < listener_contexts->second.size()
                    && listener_contexts->second[index].Get(isolate) != local_context) continue;
                const auto listener_target = targets != frame_event_listener_targets.end()
                    && index < targets->second.size() ? targets->second[index] : 0U;
                const auto listener_capture = captures != frame_event_listener_captures.end()
                    && index < captures->second.size() ? captures->second[index] : false;
                if (listener_target != registered_target || listener_capture != capture) continue;
                auto callback = listeners->second[index].Get(isolate);
                if (callback.IsEmpty()) continue;
                if (callback->Call(local_context, current_target, 1, arguments).IsEmpty()) return false;
                ++input_callback_invocation_count;
                ++event_callback_target_counts[type][registered_target];
                ++event_callback_index_counts[type][index];
                const auto invoke_once = once_flags != frame_event_listener_once.end()
                    && index < once_flags->second.size() && once_flags->second[index];
                if (invoke_once) listeners->second[index].Reset();
            }
            return true;
        };

        const auto property_name = std::string("on") + type;
        const auto invoke_property = [&](dom_node& node, int event_phase) {
            auto wrapper = wrap_node(node);
            v8::Local<v8::Value> handler;
            if (!wrapper->Get(local_context, js_string(isolate, property_name.c_str())).ToLocal(&handler)
                || !handler->IsFunction()) return true;
            event->Set(local_context, js_string(isolate, "currentTarget"), wrapper).Check();
            set_number("eventPhase", event_phase);
            if (handler.As<v8::Function>()->Call(local_context, wrapper, 1, arguments).IsEmpty()) {
                return false;
            }
            ++input_callback_invocation_count;
            ++event_callback_target_counts[type][node.id];
            return true;
        };

        // Preserve browser listener phase/ancestry order. Propagation flags
        // remain advisory until frame Document/Window nodes are represented
        // separately; treating the current flattened nodes as exact browser
        // boundaries can incorrectly block a component's root drag listener.
        auto global = local_context->Global();
        if (!invoke_listeners(0U, global, true, 1)) return false;
        for (size_t cursor = ancestry.size(); cursor-- > 0;) {
            dom_node* node = &target;
            while (node != nullptr && node->id != ancestry[cursor]) node = node->parent;
            if (node == nullptr) continue;
            const auto phase = cursor == 0 ? 2 : 1;
            if (!invoke_listeners(node->id, wrap_node(*node), true, phase)) return false;
        }
        for (size_t cursor = 0; cursor < ancestry.size(); ++cursor) {
            dom_node* node = &target;
            while (node != nullptr && node->id != ancestry[cursor]) node = node->parent;
            if (node == nullptr) continue;
            const auto phase = cursor == 0 ? 2 : 3;
            if (!invoke_listeners(node->id, wrap_node(*node), false, phase)) return false;
            if (!invoke_property(*node, phase)) return false;
        }
        if (!invoke_listeners(0U, global, false, 3)) return false;
        set_number("eventPhase", 0);
        event->Set(local_context, js_string(isolate, "currentTarget"), v8::Null(isolate)).Check();
        isolate->PerformMicrotaskCheckpoint();
        event_callback_counts[type] +=
            input_callback_invocation_count - callback_count_before;

        // removeEventListener is commonly called by component code from inside
        // its root mouseup callback. Removal tombstones the callback so the
        // active dispatch remains stable; compact the synchronized metadata
        // vectors only after every callback for this event has completed.
        if (listeners != frame_event_listeners.end()) {
            auto& callbacks = listeners->second;
            auto& listener_contexts = frame_event_listener_contexts[type];
            auto& listener_targets = frame_event_listener_targets[type];
            auto& listener_captures = frame_event_listener_captures[type];
            auto& listener_once = frame_event_listener_once[type];
            auto& listener_names = frame_event_listener_names[type];
            auto& listener_sequences = frame_event_listener_registration_sequences[type];
            for (size_t index = callbacks.size(); index-- > 0;) {
                if (!callbacks[index].IsEmpty()) continue;
                callbacks.erase(callbacks.begin() + static_cast<std::ptrdiff_t>(index));
                if (index < listener_contexts.size()) {
                    listener_contexts.erase(
                        listener_contexts.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < listener_targets.size()) {
                    listener_targets.erase(listener_targets.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < listener_captures.size()) {
                    listener_captures.erase(listener_captures.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < listener_once.size()) {
                    listener_once.erase(listener_once.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < listener_names.size()) {
                    listener_names.erase(listener_names.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < listener_sequences.size()) {
                    listener_sequences.erase(listener_sequences.begin() + static_cast<std::ptrdiff_t>(index));
                }
            }
        }
        return true;
    }

    bool apply_form_control_click_default(dom_node& target)
    {
        if (target.tag != "input" || target.attributes.contains("disabled")) return false;
        const auto type_iterator = target.attributes.find("type");
        auto type = type_iterator == target.attributes.end()
            ? std::string("text")
            : type_iterator->second;
        std::transform(type.begin(), type.end(), type.begin(), [](unsigned char value) {
            return static_cast<char>(std::tolower(value));
        });
        if (type != "checkbox" && type != "radio") return false;

        auto local_context = frame_context.IsEmpty()
            ? context.Get(isolate)
            : frame_context.Get(isolate);
        auto target_wrapper = wrap_node(target);
        v8::Local<v8::Value> current_value;
        const auto was_checked = target_wrapper->Get(
                local_context,
                js_string(isolate, "checked"))
            .ToLocal(&current_value)
            && current_value->BooleanValue(isolate);
        const auto checked = type == "radio" ? true : !was_checked;
        if (type == "radio" && checked) {
            const auto name_iterator = target.attributes.find("name");
            const auto name = name_iterator == target.attributes.end()
                ? std::string{}
                : name_iterator->second;
            if (!name.empty()) {
                for (auto* candidate : document.query_selector_all(active_root(), "input")) {
                    if (candidate == &target) continue;
                    const auto candidate_type = candidate->attributes.find("type");
                    const auto candidate_name = candidate->attributes.find("name");
                    if (candidate_type == candidate->attributes.end()
                        || candidate_type->second != "radio"
                        || candidate_name == candidate->attributes.end()
                        || candidate_name->second != name) {
                        continue;
                    }
                    wrap_node(*candidate)->Set(
                        local_context,
                        js_string(isolate, "checked"),
                        v8::False(isolate)).Check();
                }
            }
        }
        target_wrapper->Set(
            local_context,
            js_string(isolate, "checked"),
            checked ? v8::Local<v8::Value>(v8::True(isolate))
                    : v8::Local<v8::Value>(v8::False(isolate))).Check();
        return checked != was_checked;
    }

    dom_node* label_control_for_click(dom_node& target)
    {
        if (target.tag == "input") return &target;
        auto* label = &target;
        while (label != nullptr && label->tag != "label") label = label->parent;
        if (label == nullptr) return nullptr;

        const auto explicit_control = label->attributes.find("for");
        if (explicit_control != label->attributes.end() && !explicit_control->second.empty()) {
            auto matches = document.query_selector_all(
                active_root(),
                "#" + explicit_control->second);
            if (!matches.empty() && matches.front()->tag == "input") return matches.front();
        }
        auto descendants = document.query_selector_all(*label, "input");
        return descendants.empty() ? nullptr : descendants.front();
    }

    bool dispatch_input(const htmlml_input_event& input)
    {
        current_input_sequence = input.sequence;
        v8::Isolate::Scope isolate_scope(isolate);
        v8::HandleScope handle_scope(isolate);
        auto local_context = frame_context.IsEmpty()
            ? context.Get(isolate)
            : frame_context.Get(isolate);
        v8::Context::Scope context_scope(local_context);
        ensure_layout();
        auto* hit_target = document.hit_test(
            active_root(),
            static_cast<float>(input.x),
            static_cast<float>(input.y));
        if (hit_target == nullptr) hit_target = &active_root();
        auto* target = pointer_capture_target != nullptr
            && input.kind != HTMLML_INPUT_POINTER_DOWN
            ? pointer_capture_target
            : hit_target;
        current_movement_x = has_pointer_position ? input.x - last_pointer_x : 0;
        current_movement_y = has_pointer_position ? input.y - last_pointer_y : 0;
        if (input.kind != HTMLML_INPUT_WHEEL) {
            last_pointer_x = input.x;
            last_pointer_y = input.y;
            has_pointer_position = true;
        }
        v8::TryCatch try_catch(isolate);
        if ((input.kind == HTMLML_INPUT_POINTER_MOVE
                || input.kind == HTMLML_INPUT_POINTER_DOWN)
            && hit_target != hover_target) {
            auto* previous_hover_target = hover_target;
            // Browsers update :hover before dispatching the boundary events.
            // Event handlers are therefore allowed to observe the new
            // computed style synchronously from mouseover/pointerover.
            hover_target = hit_target;
            recascade_connected_subtree(active_root());
            if (previous_hover_target != nullptr) {
                current_related_target = hit_target;
                constexpr const char* exit_types[] = {
                    "pointerout", "mouseout", "pointerleave", "mouseleave"};
                for (const auto* type : exit_types) {
                    if (!dispatch_input_event_type(input, type, *previous_hover_target)) {
                        last_error = "Native input transition dispatch failed: "
                            + describe_exception(try_catch, local_context);
                        return false;
                    }
                }
            }
            current_related_target = previous_hover_target;
            constexpr const char* enter_types[] = {
                "pointerover", "mouseover", "pointerenter", "mouseenter"};
            for (const auto* type : enter_types) {
                if (!dispatch_input_event_type(input, type, *hit_target)) {
                    last_error = "Native input transition dispatch failed: "
                        + describe_exception(try_catch, local_context);
                    return false;
                }
            }
            current_related_target = nullptr;

            // Hover handlers are allowed to replace the element under the
            // pointer (several component libraries do this for icon buttons). The
            // subsequent pointerdown must target the post-hover DOM, not the
            // now-detached node selected before pointerover/mouseenter ran.
            // Keeping the stale target can make a modal disappear through an
            // outside-click handler without invoking its Close/Cancel action,
            // leaving the toolbar opener permanently in its `isOpened` state.
            ensure_layout();
            hit_target = document.hit_test(
                active_root(),
                static_cast<float>(input.x),
                static_cast<float>(input.y));
            if (hit_target == nullptr) hit_target = &active_root();
            if (pointer_capture_target == nullptr
                || input.kind == HTMLML_INPUT_POINTER_DOWN) {
                target = hit_target;
            }
            if (hover_target != hit_target) {
                hover_target = hit_target;
                recascade_connected_subtree(active_root());
            }
        }
        const char* types[3]{};
        size_t type_count = 0;
        bool form_control_changed = false;
        dom_node* activated_form_control = nullptr;
        bool activate_label_control_after_click = false;
        dom_node* click_target = nullptr;
        switch (input.kind) {
        case HTMLML_INPUT_POINTER_MOVE:
            types[type_count++] = "pointermove";
            types[type_count++] = "mousemove";
            break;
        case HTMLML_INPUT_POINTER_DOWN:
            pointer_down_target = target;
            pointer_down_x = input.x;
            pointer_down_y = input.y;
            types[type_count++] = "pointerdown";
            types[type_count++] = "mousedown";
            break;
        case HTMLML_INPUT_POINTER_UP:
            types[type_count++] = "pointerup";
            types[type_count++] = "mouseup";
            if (pointer_down_target != nullptr
                && std::hypot(input.x - pointer_down_x, input.y - pointer_down_y) <= 4.0) {
                // UI Events targets click at the nearest common inclusive
                // ancestor of the press and release targets. Interactive rows
                // reveal/replace icon children on hover, so requiring the same
                // leaf suppressed an otherwise valid click between down/up.
                click_target = pointer_down_target;
                while (click_target != nullptr) {
                    auto* release_ancestor = hit_target;
                    while (release_ancestor != nullptr
                        && release_ancestor != click_target) {
                        release_ancestor = release_ancestor->parent;
                    }
                    if (release_ancestor == click_target) break;
                    click_target = click_target->parent;
                }
                if (click_target == nullptr) click_target = &active_root();
                activated_form_control = label_control_for_click(*click_target);
                activate_label_control_after_click = activated_form_control != nullptr
                    && activated_form_control != click_target;
                if (activated_form_control != nullptr && !activate_label_control_after_click) {
                    // For a direct input activation the checked state changes
                    // before the input's click listeners run, matching HTML.
                    form_control_changed = apply_form_control_click_default(
                        *activated_form_control);
                }
            }
            break;
        case HTMLML_INPUT_WHEEL:
            types[type_count++] = "wheel";
            break;
        default:
            return true;
        }
        for (size_t index = 0; index < type_count; ++index) {
            if (!dispatch_input_event_type(input, types[index], *target)) {
                last_error = "Native input dispatch failed: " + describe_exception(try_catch, local_context);
                return false;
            }
        }
        if (click_target != nullptr
            && !dispatch_input_event_type(input, "click", *click_target)) {
            last_error = "Native click dispatch failed: "
                + describe_exception(try_catch, local_context);
            return false;
        }
        if (activate_label_control_after_click) {
            // A label owns the visual hit area for many custom switches;
            // their native input is intentionally zero-width. HTML label
            // activation dispatches a second click at the associated control.
            form_control_changed = apply_form_control_click_default(*activated_form_control);
            if (!dispatch_input_event_type(input, "click", *activated_form_control)) {
                last_error = "Native label-control activation failed: "
                    + describe_exception(try_catch, local_context);
                return false;
            }
        }
        if (form_control_changed) {
            if (!dispatch_input_event_type(input, "input", *activated_form_control)
                || !dispatch_input_event_type(input, "change", *activated_form_control)) {
                last_error = "Native form-control dispatch failed: "
                    + describe_exception(try_catch, local_context);
                return false;
            }
        }
        if (input.kind == HTMLML_INPUT_POINTER_UP) {
            pointer_capture_target = nullptr;
            pointer_down_target = nullptr;
        }
        return true;
    }

    std::filesystem::path resolve_resource_path(std::string value) const
    {
        if (resource_root.empty() || value.empty()) return {};
        if (const auto suffix = value.find_first_of("?#"); suffix != std::string::npos) {
            value.resize(suffix);
        }
        std::replace(value.begin(), value.end(), '\\', '/');
        if (const auto scheme = value.find("://"); scheme != std::string::npos) {
            const auto path_start = value.find('/', scheme + 3U);
            value = path_start == std::string::npos ? std::string{} : value.substr(path_start + 1U);
        } else {
            while (value.starts_with('/')) value.erase(value.begin());
            while (value.starts_with("./")) value.erase(0U, 2U);
        }
        const auto root_name = resource_root.filename().generic_string();
        if (!root_name.empty()) {
            const auto marker = root_name + '/';
            if (const auto offset = value.find(marker); offset != std::string::npos) {
                value.erase(0U, offset + marker.size());
            }
        }
        const auto relative = std::filesystem::path(value).lexically_normal();
        if (relative.empty() || relative.is_absolute()) return {};
        for (const auto& part : relative) {
            if (part == "..") return {};
        }
        return resource_root / relative;
    }

    bool load_connected_resource(dom_node& node, v8::Local<v8::Object> wrapper)
    {
        auto local_context = isolate->GetCurrentContext();
        if (node.tag == "link") {
            const auto href_iterator = node.attributes.find("href");
            if (href_iterator != node.attributes.end()) {
                const auto path = resolve_resource_path(href_iterator->second);
                if (!path.empty()) {
                    std::string stylesheet;
                    if (!profiled_read_text_file(path, stylesheet)) {
                        frame_last_error_value = "Unable to load connected stylesheet: " + path.string();
                        ++frame_script_error_count;
                        return false;
                    }
                    const auto first_new_rule = add_stylesheet(std::move(stylesheet));
                    binding_callback_timer recascade_timer(
                        profile_startup ? &startup_stylesheet_recascade : nullptr);
                    for (auto* existing : document.query_selector_all(document.body(), "*")) {
                        if (profile_startup) ++startup_stylesheet_recascade_nodes;
                        apply_appended_css_rules(*existing, first_new_rule);
                    }
                }
            }
        } else if (node.tag == "script") {
            const auto source_iterator = node.attributes.find("src");
            if (source_iterator == node.attributes.end() || source_iterator->second.empty()) return true;
            const auto path = resolve_resource_path(source_iterator->second);
            if (path.empty()) return true;
            std::string source;
            if (!profiled_read_text_file(path, source)) {
                frame_last_error_value = "Unable to load connected script: " + path.string();
                ++frame_script_error_count;
                return false;
            }
            std::string error;
            // A dynamically inserted external script and its load event are one
            // browser task. Promise reactions must not run between evaluating
            // the script and dispatching load, otherwise Webpack can re-enter a
            // module graph while an export is still in its TDZ.
            if (!execute_in_context(local_context, source, path.string(), error, false)) {
                frame_last_error_value = "Connected script failed: " + error;
                ++frame_script_error_count;
                return false;
            }
            loaded_resource_names.push_back(path.filename().generic_string());
            ++frame_script_execution_count;
        }

        v8::Local<v8::Value> onload;
        if (wrapper->Get(local_context, js_string(isolate, "onload")).ToLocal(&onload)
            && onload->IsFunction()) {
            auto callback = onload.As<v8::Function>();
            v8::TryCatch try_catch(isolate);
            auto event = v8::Object::New(isolate);
            event->Set(local_context, js_string(isolate, "type"), js_string(isolate, "load")).Check();
            event->Set(local_context, js_string(isolate, "target"), wrapper).Check();
            v8::Local<v8::Value> arguments[] = {event};
            if (callback->Call(local_context, wrapper, 1, arguments).IsEmpty()) {
                frame_last_error_value = "Connected resource onload failed: "
                    + describe_exception(try_catch, local_context);
                ++frame_script_error_count;
                return false;
            }
        }
        return true;
    }

    std::string describe_exception(
        v8::TryCatch& try_catch,
        v8::Local<v8::Context> exception_context = {})
    {
        std::ostringstream result;
        result << to_utf8(isolate, try_catch.Exception());
        auto message = try_catch.Message();
        if (!message.IsEmpty()) {
            result << " at " << to_utf8(isolate, message->GetScriptOrigin().ResourceName());
            auto local_context = exception_context.IsEmpty()
                ? isolate->GetCurrentContext()
                : exception_context;
            const auto line = message->GetLineNumber(local_context).FromMaybe(0);
            if (line > 0) result << ':' << line;
        }
        auto stack = try_catch.StackTrace(exception_context.IsEmpty()
            ? isolate->GetCurrentContext()
            : exception_context);
        v8::Local<v8::Value> stack_value;
        if (stack.ToLocal(&stack_value) && stack_value->IsString()) {
            const auto stack_text = to_utf8(isolate, stack_value);
            if (!stack_text.empty()) result << "\n" << stack_text;
        }
        return result.str();
    }

    void ensure_layout()
    {
        if (!document.dirty()) return;
        binding_callback_timer timer(profile_startup ? &startup_layout : nullptr);
        const auto [width, height] = viewport_provider();
        document.layout(width, height);
    }

    dom_node& active_root()
    {
        auto local_context = isolate->GetCurrentContext();
        auto* root = static_cast<dom_node*>(local_context->GetAlignedPointerFromEmbedderData(
            1,
            v8::kEmbedderDataTypeTagDefault));
        return root == nullptr ? document.body() : *root;
    }

    bool in_frame_context() const
    {
        if (frame_context.IsEmpty()) return false;
        return isolate->GetCurrentContext() == frame_context.Get(isolate);
    }

    uint64_t wrapper_key(const dom_node& node) const
    {
        return static_cast<uint64_t>(node.id) | (in_frame_context() ? (1ULL << 63U) : 0ULL);
    }

    v8::Local<v8::Object> wrap_node(dom_node& node)
    {
        const auto key = wrapper_key(node);
        auto known = node_wrappers.find(key);
        if (known != node_wrappers.end()) {
            return known->second.Get(isolate);
        }
        auto local_context = isolate->GetCurrentContext();
        auto object = element_template.Get(isolate)
            ->InstanceTemplate()
            ->NewInstance(local_context)
            .ToLocalChecked();
        object->SetAlignedPointerInInternalField(
            0,
            &node,
            v8::kEmbedderDataTypeTagDefault);
        node_wrappers[key].Reset(isolate, object);
        return object;
    }

    v8::Local<v8::Object> wrap_style(dom_node& node)
    {
        const auto key = wrapper_key(node);
        auto known = style_wrappers.find(key);
        if (known != style_wrappers.end()) {
            return known->second.Get(isolate);
        }
        auto object = style_template.Get(isolate)
            ->NewInstance(isolate->GetCurrentContext())
            .ToLocalChecked();
        object->SetAlignedPointerInInternalField(
            0,
            &node,
            v8::kEmbedderDataTypeTagDefault);
        object->SetInternalField(1, v8::False(isolate));
        style_wrappers[key].Reset(isolate, object);
        return object;
    }

    v8::Local<v8::Object> wrap_computed_style(dom_node& node)
    {
        const auto key = wrapper_key(node);
        auto known = computed_style_wrappers.find(key);
        if (known != computed_style_wrappers.end()) {
            return known->second.Get(isolate);
        }
        auto object = style_template.Get(isolate)
            ->NewInstance(isolate->GetCurrentContext())
            .ToLocalChecked();
        object->SetAlignedPointerInInternalField(
            0,
            &node,
            v8::kEmbedderDataTypeTagDefault);
        object->SetInternalField(1, v8::True(isolate));
        computed_style_wrappers[key].Reset(isolate, object);
        return object;
    }

    void enqueue_connected_resource_if_needed(
        dom_node& node,
        v8::Local<v8::Object> wrapper,
        v8::Local<v8::Context> resource_context)
    {
        auto resource_element = node.tag == "link" || node.tag == "script";
        v8::Local<v8::Value> onload;
        const auto has_onload = wrapper->Get(
                resource_context,
                js_string(isolate, "onload")).ToLocal(&onload)
            && onload->IsFunction();
        if (!resource_element && !has_onload) return;
        connected_resources.push_back(connected_resource_task{
            &node,
            v8::Global<v8::Context>(isolate, resource_context),
            v8::Global<v8::Object>(isolate, wrapper)});
    }

    v8::Local<v8::Object> frame_document(dom_node& node)
    {
        auto known = frame_documents.find(node.id);
        if (known != frame_documents.end()) return known->second.Get(isolate);
        auto local_context = isolate->GetCurrentContext();
        auto object = frame_document_template.Get(isolate)->NewInstance(local_context).ToLocalChecked();
        object->SetAlignedPointerInInternalField(
            0,
            &node,
            v8::kEmbedderDataTypeTagDefault);
        object->Set(local_context, js_string(isolate, "readyState"), js_string(isolate, "complete")).Check();
        frame_documents[node.id].Reset(isolate, object);
        return object;
    }

    v8::Local<v8::Object> frame_window(dom_node& node)
    {
        auto known = frame_windows.find(node.id);
        if (known != frame_windows.end()) return known->second.Get(isolate);
        auto local_context = context.Get(isolate);
        auto object = frame_window_template.Get(isolate)->NewInstance(local_context).ToLocalChecked();
        object->SetAlignedPointerInInternalField(
            0,
            &node,
            v8::kEmbedderDataTypeTagDefault);
        auto location = v8::Object::New(isolate);
        const auto source = node.attributes.contains("src") ? node.attributes["src"] : "about:blank";
        location->Set(local_context, js_string(isolate, "href"), js_string(isolate, source.c_str())).Check();
        object->Set(local_context, js_string(isolate, "location"), location).Check();
        object->Set(local_context, js_string(isolate, "document"), frame_document(node)).Check();
        object->Set(local_context, js_string(isolate, "window"), object).Check();
        object->Set(local_context, js_string(isolate, "self"), object).Check();
        object->Set(local_context, js_string(isolate, "parent"), local_context->Global()).Check();
        frame_windows[node.id].Reset(isolate, object);
        return object;
    }

    void collect_selector_matches(
        dom_node& node,
        std::string_view selector,
        bool include_node,
        std::vector<dom_node*>& result) const
    {
        if (include_node && css_selector_list_matches(node, selector)) result.push_back(&node);
        for (auto* child : node.children) {
            if (child != nullptr) collect_selector_matches(*child, selector, true, result);
        }
    }

    std::vector<dom_node*> query_selector_nodes(
        dom_node& root,
        std::string_view selector,
        bool include_root) const
    {
        std::vector<dom_node*> result;
        collect_selector_matches(root, selector, include_root, result);
        return result;
    }

    v8::Local<v8::Array> selector_results(
        dom_node& root,
        const std::string& selector,
        bool include_root = true)
    {
        auto matches = query_selector_nodes(root, selector, include_root);
        auto result = v8::Array::New(isolate, static_cast<int>(matches.size()));
        auto local_context = isolate->GetCurrentContext();
        for (uint32_t index = 0; index < matches.size(); ++index) {
            result->Set(local_context, index, wrap_node(*matches[index])).Check();
        }
        return result;
    }

    v8::Local<v8::Array> class_results(dom_node& root, const std::string& names)
    {
        std::vector<std::string> required;
        std::istringstream stream(names);
        for (std::string name; stream >> name;) required.push_back(std::move(name));
        auto matches = document.query_selector_all(root, "*");
        std::erase_if(matches, [&required](const auto* node) {
            return node == nullptr || required.empty()
                || !std::all_of(required.begin(), required.end(), [node](const auto& name) {
                    return has_class(*node, name);
                });
        });
        auto result = v8::Array::New(isolate, static_cast<int>(matches.size()));
        auto local_context = isolate->GetCurrentContext();
        for (uint32_t index = 0; index < matches.size(); ++index) {
            result->Set(local_context, index, wrap_node(*matches[index])).Check();
        }
        result->Set(
            local_context,
            js_string(isolate, "item"),
            v8::Function::New(local_context, collection_item).ToLocalChecked()).Check();
        return result;
    }

    static void collection_item(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto local_context = info.GetIsolate()->GetCurrentContext();
        const auto index = info.Length() > 0
            ? info[0]->Uint32Value(local_context).FromMaybe(0)
            : 0U;
        v8::Local<v8::Value> value;
        info.GetReturnValue().Set(
            info.This()->Get(local_context, index).ToLocal(&value)
                && !value->IsUndefined()
                ? value
                : v8::Local<v8::Value>(v8::Null(info.GetIsolate())));
    }

    static void get_body(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        info.GetReturnValue().Set(self->wrap_node(self->active_root()));
    }

    static void create_element(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto tag = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string("div");
        auto& node = self->document.create_element(std::move(tag));
        self->apply_css_rules(node);
        info.GetReturnValue().Set(self->wrap_node(node));
    }

    static void create_element_ns(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto tag = info.Length() > 1 ? to_utf8(info.GetIsolate(), info[1]) : std::string("svg");
        auto& node = self->document.create_element(std::move(tag));
        node.attributes["namespace"] = info.Length() > 0
            ? to_utf8(info.GetIsolate(), info[0])
            : "http://www.w3.org/2000/svg";
        self->apply_css_rules(node);
        info.GetReturnValue().Set(self->wrap_node(node));
    }

    static void create_text_node(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto& node = self->document.create_element("#text");
        node.text_content = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->wrap_node(node));
    }

    static void create_document_fragment(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto& node = self->document.create_element("#document-fragment");
        node.visible = false;
        info.GetReturnValue().Set(self->wrap_node(node));
    }

    static void clone_node(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* source = unwrap_node(info.This());
        if (source == nullptr) {
            info.GetIsolate()->ThrowException(v8::Exception::TypeError(
                js_string(info.GetIsolate(), "cloneNode requires a native DOM node")));
            return;
        }
        const auto deep = info.Length() > 0 && info[0]->BooleanValue(info.GetIsolate());
        const auto clone_subtree = [&](const auto& recurse, const dom_node& original) -> dom_node& {
            auto& clone = self->document.create_element(original.tag);
            clone.id_attribute = original.id_attribute;
            clone.class_name = original.class_name;
            clone.text_content = original.text_content;
            clone.attributes = original.attributes;
            clone.style = original.style;
            clone.visible = true;
            if (deep) {
                for (const auto* child : original.children) {
                    if (child == nullptr) continue;
                    auto& child_clone = recurse(recurse, *child);
                    self->document.append_child(clone, child_clone);
                }
            }
            return clone;
        };
        info.GetReturnValue().Set(self->wrap_node(clone_subtree(clone_subtree, *source)));
    }

    static void get_element_by_id(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        const auto id = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto matches = self->query_selector_nodes(self->active_root(), "#" + id, true);
        auto* node = matches.empty() ? nullptr : matches.front();
        if (node == nullptr) {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
        } else {
            info.GetReturnValue().Set(self->wrap_node(*node));
        }
    }

    static void document_query_selector_all(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(self->active_root(), selector));
    }

    static void document_query_selector(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto matches = self->query_selector_nodes(self->active_root(), selector, true);
        info.GetReturnValue().Set(matches.empty()
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*matches.front())));
    }

    static void document_element_from_point(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        self->ensure_layout();
        const auto local_context = info.GetIsolate()->GetCurrentContext();
        const auto x = info.Length() > 0
            ? static_cast<float>(info[0]->NumberValue(local_context).FromMaybe(0)) : 0;
        const auto y = info.Length() > 1
            ? static_cast<float>(info[1]->NumberValue(local_context).FromMaybe(0)) : 0;
        auto* node = self->document.hit_test(self->active_root(), x, y);
        info.GetReturnValue().Set(node == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*node)));
    }

    static void document_get_elements_by_tag_name(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        const auto tag = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(self->active_root(), tag));
    }

    static void document_get_elements_by_class_name(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        const auto names = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->class_results(self->active_root(), names));
    }

    static void element_query_selector_all(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        auto* root = unwrap_node(info.This());
        if (root == nullptr) return;
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(*root, selector, false));
    }

    static void element_query_selector(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        auto* root = unwrap_node(info.This());
        if (root == nullptr) return;
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto matches = self->query_selector_nodes(*root, selector, false);
        info.GetReturnValue().Set(matches.empty()
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*matches.front())));
    }

    static void element_get_elements_by_tag_name(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* root = unwrap_node(info.This());
        if (root == nullptr) return;
        const auto tag = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(*root, tag, false));
    }

    static void element_get_elements_by_class_name(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_query));
        auto* root = unwrap_node(info.This());
        if (root == nullptr) return;
        const auto names = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->class_results(*root, names));
    }

    static void element_contains(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* root = unwrap_node(info.This());
        auto* candidate = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        for (auto* node = candidate; root != nullptr && node != nullptr; node = node->parent) {
            if (node == root) {
                info.GetReturnValue().Set(v8::True(info.GetIsolate()));
                return;
            }
        }
        info.GetReturnValue().Set(v8::False(info.GetIsolate()));
    }

    static void element_matches(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 1) {
            info.GetReturnValue().Set(v8::False(info.GetIsolate()));
            return;
        }
        const auto selector = to_utf8(info.GetIsolate(), info[0]);
        info.GetReturnValue().Set(v8::Boolean::New(
            info.GetIsolate(),
            self->css_selector_list_matches(*node, selector)));
    }

    static void element_closest(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 1) {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
            return;
        }
        const auto selector = to_utf8(info.GetIsolate(), info[0]);
        for (auto* candidate = node; candidate != nullptr; candidate = candidate->parent) {
            if (self->css_selector_list_matches(*candidate, selector)) {
                info.GetReturnValue().Set(self->wrap_node(*candidate));
                return;
            }
        }
        info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
    }

    static void element_focus(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node != nullptr) self->active_element = node;
    }

    static void element_blur(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (self->active_element == node) self->active_element = nullptr;
    }

    static void element_click(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || node->attributes.contains("disabled")) return;
        if (node->tag == "input") {
            self->apply_form_control_click_default(*node);
        } else if (node->tag == "option") {
            if (node->parent != nullptr) {
                for (auto* sibling : node->parent->children) {
                    if (sibling != nullptr && sibling->tag == "option") {
                        sibling->attributes.erase("selected");
                    }
                }
            }
            node->attributes["selected"] = std::string{};
            self->recascade_connected_subtree(*node);
        }
        htmlml_input_event synthetic{};
        synthetic.kind = HTMLML_INPUT_POINTER_UP;
        synthetic.x = node->layout.x + node->layout.width / 2.0;
        synthetic.y = node->layout.y + node->layout.height / 2.0;
        self->dispatch_input_event_type(synthetic, "click", *node);
    }

    static void element_set_selection_range(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto local_context = info.GetIsolate()->GetCurrentContext();
        const auto start = info.Length() > 0 && info[0]->IsNumber()
            ? info[0]->Int32Value(local_context).FromMaybe(0)
            : 0;
        const auto end = info.Length() > 1 && info[1]->IsNumber()
            ? info[1]->Int32Value(local_context).FromMaybe(start)
            : start;
        info.This()->Set(
            local_context,
            js_string(info.GetIsolate(), "selectionStart"),
            v8::Integer::New(info.GetIsolate(), std::max(0, start))).Check();
        info.This()->Set(
            local_context,
            js_string(info.GetIsolate(), "selectionEnd"),
            v8::Integer::New(info.GetIsolate(), std::max(start, end))).Check();
        if (info.Length() > 2) {
            info.This()->Set(
                local_context,
                js_string(info.GetIsolate(), "selectionDirection"),
                info[2]).Check();
        }
    }

    static void element_select(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node != nullptr) self->active_element = node;
        auto local_context = info.GetIsolate()->GetCurrentContext();
        v8::Local<v8::Value> value;
        auto length = 0;
        if (info.This()->Get(
                local_context,
                js_string(info.GetIsolate(), "value")).ToLocal(&value)
            && !value->IsNullOrUndefined()) {
            length = static_cast<int>(to_utf8(info.GetIsolate(), value).size());
        }
        v8::Local<v8::Value> arguments[] = {
            v8::Integer::New(info.GetIsolate(), 0),
            v8::Integer::New(info.GetIsolate(), length)};
        info.This()->Set(
            local_context,
            js_string(info.GetIsolate(), "selectionStart"),
            arguments[0]).Check();
        info.This()->Set(
            local_context,
            js_string(info.GetIsolate(), "selectionEnd"),
            arguments[1]).Check();
        info.This()->Set(
            local_context,
            js_string(info.GetIsolate(), "selectionDirection"),
            js_string(info.GetIsolate(), "none")).Check();
    }

    static void get_active_element(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = self->active_element == nullptr
            ? &self->active_root()
            : self->active_element;
        info.GetReturnValue().Set(self->wrap_node(*node));
    }

    static void document_has_focus(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::True(info.GetIsolate()));
    }

    static void return_empty_string(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(js_string(info.GetIsolate(), ""));
    }

    static void return_null(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
    }

    static void get_selection(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto context = info.GetIsolate()->GetCurrentContext();
        auto selection = v8::Object::New(info.GetIsolate());
        auto no_op = v8::Function::New(context, console_log).ToLocalChecked();
        selection->Set(context, js_string(info.GetIsolate(), "removeAllRanges"), no_op).Check();
        selection->Set(context, js_string(info.GetIsolate(), "empty"), no_op).Check();
        selection->Set(context, js_string(info.GetIsolate(), "collapse"), no_op).Check();
        selection->Set(
            context,
            js_string(info.GetIsolate(), "toString"),
            v8::Function::New(context, return_empty_string).ToLocalChecked()).Check();
        selection->Set(context, js_string(info.GetIsolate(), "rangeCount"), v8::Integer::New(info.GetIsolate(), 0)).Check();
        info.GetReturnValue().Set(selection);
    }

    static void append_child(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* parent = unwrap_node(info.This());
        auto* child = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        if (parent != nullptr && child != nullptr && child->tag == "#document-fragment") {
            auto children = std::move(child->children);
            child->children.clear();
            for (auto* fragment_child : children) {
                if (fragment_child == nullptr) continue;
                fragment_child->parent = nullptr;
                fragment_child->visible = true;
                if (!self->document.append_child(*parent, *fragment_child)) continue;
                self->recascade_connected_subtree(*fragment_child);
                self->activate_connected_stylesheet(*fragment_child);
                self->enqueue_connected_resource_if_needed(
                    *fragment_child,
                    self->wrap_node(*fragment_child),
                    info.GetIsolate()->GetCurrentContext());
            }
            self->document.mark_dirty();
            info.GetReturnValue().Set(info[0]);
            return;
        }
        if (child != nullptr && child->parent != nullptr) {
            std::erase(child->parent->children, child);
            child->parent = nullptr;
        }
        if (parent == nullptr || child == nullptr || !self->document.append_child(*parent, *child)) {
            info.GetIsolate()->ThrowException(v8::Exception::TypeError(
                js_string(info.GetIsolate(), "appendChild requires a detached native node")));
            return;
        }
        self->recascade_connected_subtree(*child);
        self->activate_connected_stylesheet(*child);
        self->enqueue_connected_resource_if_needed(
            *child,
            info[0].As<v8::Object>(),
            info.GetIsolate()->GetCurrentContext());
        info.GetReturnValue().Set(info[0]);
    }

    static void remove_child(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* parent = unwrap_node(info.This());
        auto* child = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        if (parent == nullptr || child == nullptr || child->parent != parent) return;
        std::erase(parent->children, child);
        child->parent = nullptr;
        self->document.mark_dirty();
        info.GetReturnValue().Set(info[0]);
    }

    static void replace_children(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* parent = unwrap_node(info.This());
        if (parent == nullptr) return;
        self->document.remove_all_children(*parent);
        for (int index = 0; index < info.Length(); ++index) {
            if (!info[index]->IsObject()) continue;
            auto* child = unwrap_node(info[index].As<v8::Object>());
            if (child == nullptr) continue;
            if (child->parent != nullptr) {
                std::erase(child->parent->children, child);
                child->parent = nullptr;
            }
            self->document.append_child(*parent, *child);
        }
        self->recascade_connected_subtree(*parent);
    }

    static void remove_element(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || node->parent == nullptr) return;
        std::erase(node->parent->children, node);
        node->parent = nullptr;
        node->visible = false;
        self->document.mark_dirty();
    }

    static void insert_adjacent_element(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* reference = unwrap_node(info.This());
        auto* child = info.Length() > 1 && info[1]->IsObject()
            ? unwrap_node(info[1].As<v8::Object>())
            : nullptr;
        if (reference == nullptr || child == nullptr || reference == child || info.Length() < 1) return;
        const auto position = to_utf8(info.GetIsolate(), info[0]);
        dom_node* parent = nullptr;
        size_t index = 0;
        if (position == "afterbegin" || position == "beforeend") {
            parent = reference;
            index = position == "afterbegin" ? 0U : parent->children.size();
        } else if ((position == "beforebegin" || position == "afterend")
            && reference->parent != nullptr) {
            parent = reference->parent;
            const auto iterator = std::find(parent->children.begin(), parent->children.end(), reference);
            if (iterator == parent->children.end()) return;
            index = static_cast<size_t>(iterator - parent->children.begin())
                + (position == "afterend" ? 1U : 0U);
        } else {
            return;
        }
        if (child->parent != nullptr) {
            auto* old_parent = child->parent;
            const auto old = std::find(old_parent->children.begin(), old_parent->children.end(), child);
            if (old != old_parent->children.end()) {
                const auto old_index = static_cast<size_t>(old - old_parent->children.begin());
                old_parent->children.erase(old);
                if (old_parent == parent && old_index < index) --index;
            }
            child->parent = nullptr;
        }
        index = std::min(index, parent->children.size());
        child->parent = parent;
        parent->children.insert(parent->children.begin() + static_cast<std::ptrdiff_t>(index), child);
        self->document.mark_dirty();
        self->recascade_connected_subtree(*child);
        info.GetReturnValue().Set(info[1]);
    }

    static void prepend_child(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* parent = unwrap_node(info.This());
        auto* child = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>()) : nullptr;
        if (parent == nullptr || child == nullptr || parent == child) return;
        if (child->parent != nullptr) {
            std::erase(child->parent->children, child);
            child->parent = nullptr;
        }
        child->parent = parent;
        parent->children.insert(parent->children.begin(), child);
        self->document.mark_dirty();
        self->recascade_connected_subtree(*child);
        info.GetReturnValue().Set(info[0]);
    }

    static void insert_sibling(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        bool after)
    {
        auto* self = current(info.GetIsolate());
        auto* reference = unwrap_node(info.This());
        auto* child = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>()) : nullptr;
        if (reference == nullptr || reference->parent == nullptr || child == nullptr
            || reference == child) return;
        auto* parent = reference->parent;
        if (child->parent != nullptr) {
            std::erase(child->parent->children, child);
            child->parent = nullptr;
        }
        const auto position = std::find(parent->children.begin(), parent->children.end(), reference);
        if (position == parent->children.end()) return;
        child->parent = parent;
        parent->children.insert(position + (after ? 1 : 0), child);
        self->document.mark_dirty();
        self->recascade_connected_subtree(*child);
    }

    static void insert_before_self(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        insert_sibling(info, false);
    }

    static void insert_after_self(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        insert_sibling(info, true);
    }

    static void insert_before(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* parent = unwrap_node(info.This());
        auto* child = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        auto* before = info.Length() > 1 && info[1]->IsObject()
            ? unwrap_node(info[1].As<v8::Object>())
            : nullptr;
        if (parent == nullptr || child == nullptr) return;
        if (child->tag == "#document-fragment") {
            auto position = before == nullptr
                ? parent->children.end()
                : std::find(parent->children.begin(), parent->children.end(), before);
            if (before != nullptr && position == parent->children.end()) {
                info.GetIsolate()->ThrowException(v8::Exception::TypeError(
                    js_string(info.GetIsolate(), "insertBefore reference is not a child")));
                return;
            }
            auto insertion_index = static_cast<size_t>(position - parent->children.begin());
            auto children = std::move(child->children);
            child->children.clear();
            for (auto* fragment_child : children) {
                if (fragment_child == nullptr) continue;
                fragment_child->parent = parent;
                fragment_child->visible = true;
                parent->children.insert(
                    parent->children.begin() + static_cast<std::ptrdiff_t>(insertion_index++),
                    fragment_child);
                self->recascade_connected_subtree(*fragment_child);
            }
            self->document.mark_dirty();
            info.GetReturnValue().Set(info[0]);
            return;
        }
        if (child->parent != nullptr) {
            std::erase(child->parent->children, child);
            child->parent = nullptr;
        }
        if (before == nullptr) {
            if (self->document.append_child(*parent, *child)) {
                self->recascade_connected_subtree(*child);
                info.GetReturnValue().Set(info[0]);
            }
            return;
        }
        const auto position = std::find(parent->children.begin(), parent->children.end(), before);
        if (position == parent->children.end()) {
            info.GetIsolate()->ThrowException(v8::Exception::TypeError(
                js_string(info.GetIsolate(), "insertBefore reference is not a child")));
            return;
        }
        child->parent = parent;
        parent->children.insert(position, child);
        self->document.mark_dirty();
        self->recascade_connected_subtree(*child);
        info.GetReturnValue().Set(info[0]);
    }

    static void get_style(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(self->wrap_style(*node));
    }

    static void get_id(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(js_string(info.GetIsolate(), node->id_attribute.c_str()));
    }

    static void set_id(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) {
            node->id_attribute = to_utf8(info.GetIsolate(), value);
            self->recascade_connected_subtree(*node);
        }
    }

    static void get_class_name(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(js_string(info.GetIsolate(), node->class_name.c_str()));
    }

    static void set_class_name(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) {
            node->class_name = to_utf8(info.GetIsolate(), value);
            self->recascade_connected_subtree(*node);
        }
    }

    static void get_tag_name(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        auto tag = node->tag;
        auto* ancestor = node;
        while (ancestor != nullptr && ancestor->tag != "svg") ancestor = ancestor->parent;
        const auto svg = node->attributes.contains("namespace") || ancestor != nullptr;
        if (!svg) {
            std::transform(tag.begin(), tag.end(), tag.begin(), [](unsigned char value) {
                return static_cast<char>(std::toupper(value));
            });
        }
        info.GetReturnValue().Set(js_string(info.GetIsolate(), tag.c_str()));
    }

    static void get_local_name(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(js_string(info.GetIsolate(), node->tag.c_str()));
    }

    static void get_element_node_type(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        const auto type = node != nullptr && node->tag == "#text"
            ? 3
            : node != nullptr && (node->tag == "#document-fragment" || node->tag == "#fragment")
                ? 11
                : 1;
        info.GetReturnValue().Set(v8::Integer::New(
            info.GetIsolate(),
            type));
    }

    static void append_node_text(const dom_node& node, std::string& value)
    {
        value += node.text_content;
        for (const auto* child : node.children) append_node_text(*child, value);
    }

    static void get_text_content(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        std::string value;
        append_node_text(*node, value);
        info.GetReturnValue().Set(js_string(info.GetIsolate(), value.c_str()));
    }

    static void set_text_content(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> raw_value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        if (node->tag != "#text") self->document.remove_all_children(*node);
        node->text_content = raw_value->IsNullOrUndefined()
            ? std::string{}
            : to_utf8(info.GetIsolate(), raw_value);
        self->activate_connected_stylesheet(*node);
        self->document.mark_dirty();
    }

    static void get_document_node_type(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::Integer::New(info.GetIsolate(), 9));
    }

    static void get_default_view(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(info.GetIsolate()->GetCurrentContext()->Global());
    }

    static void get_namespace_uri(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        auto* current_node = node;
        while (current_node != nullptr && current_node->tag != "svg") current_node = current_node->parent;
        const auto svg = node != nullptr
            && (node->attributes.contains("namespace") || current_node != nullptr);
        info.GetReturnValue().Set(js_string(
            info.GetIsolate(),
            svg ? "http://www.w3.org/2000/svg" : "http://www.w3.org/1999/xhtml"));
    }

    static void get_attributes(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        std::vector<std::pair<std::string, std::string>> attributes;
        if (!node->id_attribute.empty()) attributes.emplace_back("id", node->id_attribute);
        if (!node->class_name.empty()) attributes.emplace_back("class", node->class_name);
        for (const auto& attribute : node->attributes) {
            if (attribute.first == "id" || attribute.first == "class") continue;
            attributes.push_back(attribute);
        }
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto result = v8::Array::New(info.GetIsolate(), static_cast<int>(attributes.size()));
        for (uint32_t index = 0; index < attributes.size(); ++index) {
            auto attribute = v8::Object::New(info.GetIsolate());
            attribute->Set(
                local_context,
                js_string(info.GetIsolate(), "name"),
                js_string(info.GetIsolate(), attributes[index].first.c_str())).Check();
            attribute->Set(
                local_context,
                js_string(info.GetIsolate(), "localName"),
                js_string(info.GetIsolate(), attributes[index].first.c_str())).Check();
            attribute->Set(
                local_context,
                js_string(info.GetIsolate(), "value"),
                js_string(info.GetIsolate(), attributes[index].second.c_str())).Check();
            result->Set(local_context, index, attribute).Check();
        }
        result->Set(
            local_context,
            js_string(info.GetIsolate(), "item"),
            v8::Function::New(local_context, collection_item).ToLocalChecked()).Check();
        info.GetReturnValue().Set(result);
    }

    static void get_children(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        auto result = v8::Array::New(info.GetIsolate(), static_cast<int>(node->children.size()));
        auto local_context = info.GetIsolate()->GetCurrentContext();
        for (uint32_t index = 0; index < node->children.size(); ++index) {
            result->Set(local_context, index, self->wrap_node(*node->children[index])).Check();
        }
        info.GetReturnValue().Set(result);
    }

    static void get_parent_node(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        info.GetReturnValue().Set(node == nullptr || node->parent == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*node->parent)));
    }

    static void get_first_element_child(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        info.GetReturnValue().Set(node == nullptr || node->children.empty()
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*node->children.front())));
    }

    static void get_last_child(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        info.GetReturnValue().Set(node == nullptr || node->children.empty()
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*node->children.back())));
    }

    static dom_node* sibling_of(dom_node* node, int offset)
    {
        if (node == nullptr || node->parent == nullptr) return nullptr;
        const auto position = std::find(
            node->parent->children.begin(), node->parent->children.end(), node);
        if (position == node->parent->children.end()) return nullptr;
        if (offset < 0) return position == node->parent->children.begin() ? nullptr : *(position - 1);
        return position + 1 == node->parent->children.end() ? nullptr : *(position + 1);
    }

    static void get_next_sibling(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* sibling = sibling_of(unwrap_node(info.Holder()), 1);
        info.GetReturnValue().Set(sibling == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*sibling)));
    }

    static void get_previous_sibling(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* sibling = sibling_of(unwrap_node(info.Holder()), -1);
        info.GetReturnValue().Set(sibling == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*sibling)));
    }

    static void get_is_connected(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        auto* root = node;
        while (root != nullptr && root->parent != nullptr) root = root->parent;
        info.GetReturnValue().Set(v8::Boolean::New(
            info.GetIsolate(), root != nullptr && root == &current(info.GetIsolate())->document.body()));
    }

    static void get_owner_svg_element(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        auto* parent = node == nullptr ? nullptr : node->parent;
        while (parent != nullptr && parent->tag != "svg") parent = parent->parent;
        info.GetReturnValue().Set(parent == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*parent)));
    }

    static std::string dataset_attribute_name(v8::Isolate* isolate, v8::Local<v8::Name> property)
    {
        const auto key = to_utf8(isolate, property);
        std::string name = "data-";
        name.reserve(key.size() + 5U);
        for (const auto character : key) {
            if (std::isupper(static_cast<unsigned char>(character))) {
                name.push_back('-');
                name.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(character))));
            } else {
                name.push_back(character);
            }
        }
        return name;
    }

    static std::string dataset_property_name(std::string_view attribute)
    {
        std::string property;
        bool uppercase_next = false;
        for (const auto character : attribute.substr(5U)) {
            if (character == '-') {
                uppercase_next = true;
            } else if (uppercase_next) {
                property.push_back(static_cast<char>(std::toupper(static_cast<unsigned char>(character))));
                uppercase_next = false;
            } else {
                property.push_back(character);
            }
        }
        return property;
    }

    static v8::Intercepted dataset_getter(
        v8::Local<v8::Name> property,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || property->IsSymbol()) return v8::Intercepted::kNo;
        const auto match = node->attributes.find(dataset_attribute_name(info.GetIsolate(), property));
        if (match != node->attributes.end()) {
            info.GetReturnValue().Set(js_string(info.GetIsolate(), match->second.c_str()));
        }
        return v8::Intercepted::kYes;
    }

    static v8::Intercepted dataset_setter(
        v8::Local<v8::Name> property,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || property->IsSymbol()) return v8::Intercepted::kNo;
        node->attributes[dataset_attribute_name(info.GetIsolate(), property)] =
            to_utf8(info.GetIsolate(), value);
        self->recascade_connected_subtree(*node);
        return v8::Intercepted::kYes;
    }

    static v8::Intercepted dataset_query(
        v8::Local<v8::Name> property,
        const v8::PropertyCallbackInfo<v8::Integer>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || property->IsSymbol()) return v8::Intercepted::kNo;
        if (!node->attributes.contains(dataset_attribute_name(info.GetIsolate(), property))) {
            return v8::Intercepted::kNo;
        }
        info.GetReturnValue().Set(v8::Integer::New(info.GetIsolate(), v8::None));
        return v8::Intercepted::kYes;
    }

    static v8::Intercepted dataset_deleter(
        v8::Local<v8::Name> property,
        const v8::PropertyCallbackInfo<v8::Boolean>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || property->IsSymbol()) return v8::Intercepted::kNo;
        node->attributes.erase(dataset_attribute_name(info.GetIsolate(), property));
        self->recascade_connected_subtree(*node);
        info.GetReturnValue().Set(true);
        return v8::Intercepted::kYes;
    }

    static void dataset_enumerator(const v8::PropertyCallbackInfo<v8::Array>& info)
    {
        auto* node = unwrap_node(info.Holder());
        auto result = v8::Array::New(info.GetIsolate());
        if (node != nullptr) {
            auto context = info.GetIsolate()->GetCurrentContext();
            uint32_t index = 0;
            for (const auto& [name, value] : node->attributes) {
                static_cast<void>(value);
                if (!name.starts_with("data-")) continue;
                result->Set(
                    context,
                    index++,
                    js_string(info.GetIsolate(), dataset_property_name(name).c_str())).Check();
            }
        }
        info.GetReturnValue().Set(result);
    }

    static void get_dataset(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto dataset = v8::ObjectTemplate::New(isolate);
        dataset->SetInternalFieldCount(1);
        dataset->SetHandler(v8::NamedPropertyHandlerConfiguration(
            dataset_getter,
            dataset_setter,
            dataset_query,
            dataset_deleter,
            dataset_enumerator,
            v8::Local<v8::Value>(),
            v8::PropertyHandlerFlags::kOnlyInterceptStrings));
        auto result = dataset->NewInstance(local_context).ToLocalChecked();
        result->SetAlignedPointerInInternalField(
            0,
            node,
            v8::kEmbedderDataTypeTagDefault);
        info.GetReturnValue().Set(result);
    }

    static void get_owner_document(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto isolate = info.GetIsolate();
        v8::Local<v8::Value> document_value;
        if (isolate->GetCurrentContext()->Global()->Get(
                isolate->GetCurrentContext(),
                js_string(isolate, "document")).ToLocal(&document_value)) {
            info.GetReturnValue().Set(document_value);
        }
    }

    static dom_node* class_list_node(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        return info.Data()->IsExternal()
            ? static_cast<dom_node*>(info.Data().As<v8::External>()->Value(v8::kExternalPointerTypeTagDefault))
            : nullptr;
    }

    static bool has_class(const dom_node& node, std::string_view value)
    {
        const std::string_view classes(node.class_name);
        size_t cursor = 0;
        while (cursor < classes.size()) {
            while (cursor < classes.size()
                && std::isspace(static_cast<unsigned char>(classes[cursor]))) ++cursor;
            const auto start = cursor;
            while (cursor < classes.size()
                && !std::isspace(static_cast<unsigned char>(classes[cursor]))) ++cursor;
            if (classes.substr(start, cursor - start) == value) return true;
        }
        return false;
    }

    static bool remove_class(dom_node& node, const std::string& removed)
    {
        if (!has_class(node, removed)) return false;
        std::istringstream classes(node.class_name);
        std::ostringstream result;
        std::string item;
        while (classes >> item) {
            if (item == removed) continue;
            if (result.tellp() > 0) result << ' ';
            result << item;
        }
        node.class_name = std::move(result).str();
        return true;
    }

    static void class_list_add(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* node = class_list_node(info);
        if (node == nullptr) return;
        auto changed = false;
        for (int index = 0; index < info.Length(); ++index) {
            const auto value = to_utf8(info.GetIsolate(), info[index]);
            if (!value.empty() && !has_class(*node, value)) {
                if (!node->class_name.empty()) node->class_name.push_back(' ');
                node->class_name += value;
                changed = true;
            }
        }
        if (changed) self->recascade_connected_subtree(*node);
    }

    static void class_list_remove(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* node = class_list_node(info);
        if (node == nullptr || info.Length() < 1) return;
        const auto removed = to_utf8(info.GetIsolate(), info[0]);
        if (remove_class(*node, removed)) self->recascade_connected_subtree(*node);
    }

    static void class_list_contains(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = class_list_node(info);
        const auto value = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(v8::Boolean::New(
            info.GetIsolate(),
            node != nullptr && has_class(*node, value)));
    }

    static void class_list_toggle(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* node = class_list_node(info);
        if (node == nullptr || info.Length() < 1) return;
        const auto value = to_utf8(info.GetIsolate(), info[0]);
        const auto present = has_class(*node, value);
        const auto enabled = info.Length() > 1 ? info[1]->BooleanValue(info.GetIsolate()) : !present;
        auto changed = false;
        if (enabled && !present) {
            if (!node->class_name.empty()) node->class_name.push_back(' ');
            node->class_name += value;
            changed = true;
        } else if (!enabled && present) {
            changed = remove_class(*node, value);
        }
        if (changed) self->recascade_connected_subtree(*node);
        info.GetReturnValue().Set(v8::Boolean::New(info.GetIsolate(), enabled));
    }

    static void get_class_list(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto data = v8::External::New(isolate, node, v8::kExternalPointerTypeTagDefault);
        auto result = v8::Object::New(isolate);
        result->Set(local_context, js_string(isolate, "add"), v8::Function::New(local_context, class_list_add, data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "remove"), v8::Function::New(local_context, class_list_remove, data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "contains"), v8::Function::New(local_context, class_list_contains, data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "toggle"), v8::Function::New(local_context, class_list_toggle, data).ToLocalChecked()).Check();
        info.GetReturnValue().Set(result);
    }

    static void range_select_node_contents(const v8::FunctionCallbackInfo<v8::Value>&)
    {
    }

    static void fragment_remove_child(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() > 0) info.GetReturnValue().Set(info[0]);
    }

    static void range_create_contextual_fragment(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        const auto html = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto& fragment_root = self->document.create_element("#fragment");
        self->parse_inner_html(fragment_root, html);
        // A contextual fragment is a real queryable DOM node. Reuse the native
        // node wrapper so querySelector(All), children and removeChild all have
        // the same behavior as the rest of the native DOM.
        info.GetReturnValue().Set(self->wrap_node(fragment_root));
    }

    static void create_range(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto range = v8::Object::New(info.GetIsolate());
        range->Set(
            local_context,
            js_string(info.GetIsolate(), "selectNodeContents"),
            v8::Function::New(local_context, range_select_node_contents).ToLocalChecked()).Check();
        range->Set(
            local_context,
            js_string(info.GetIsolate(), "createContextualFragment"),
            v8::Function::New(local_context, range_create_contextual_fragment).ToLocalChecked()).Check();
        info.GetReturnValue().Set(range);
    }

    static void get_client_width(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(v8::Number::New(info.GetIsolate(), node->layout.width));
    }

    static void get_client_height(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(v8::Number::New(info.GetIsolate(), node->layout.height));
    }

    static dom_node* find_offset_parent(dom_node& node)
    {
        if (node.style.position == position_mode::fixed) return nullptr;
        for (auto* ancestor = node.parent; ancestor != nullptr; ancestor = ancestor->parent) {
            if (ancestor->tag == "body" || ancestor->style.position != position_mode::normal) {
                return ancestor;
            }
        }
        return nullptr;
    }

    static void get_offset_left(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto* offset_parent = find_offset_parent(*node);
        const auto parent_x = offset_parent == nullptr ? 0.0F : offset_parent->layout.x;
        info.GetReturnValue().Set(v8::Number::New(
            info.GetIsolate(),
            node->layout.x - parent_x));
    }

    static void get_offset_top(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto* offset_parent = find_offset_parent(*node);
        const auto parent_y = offset_parent == nullptr ? 0.0F : offset_parent->layout.y;
        info.GetReturnValue().Set(v8::Number::New(
            info.GetIsolate(),
            node->layout.y - parent_y));
    }

    static void get_offset_parent(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        auto* offset_parent = node == nullptr ? nullptr : find_offset_parent(*node);
        info.GetReturnValue().Set(offset_parent == nullptr
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*offset_parent)));
    }

    static double element_dimension(dom_node& node, const char* name, double fallback)
    {
        const auto iterator = node.attributes.find(name);
        if (iterator == node.attributes.end()) return fallback;
        try {
            return std::stod(iterator->second);
        } catch (...) {
            return fallback;
        }
    }

    static void advance_canvas_generation(dom_node& node)
    {
        ++node.canvas_generation;
        constexpr uint64_t retained_diagnostic_generations = 64U;
        if (node.canvas_generation <= retained_diagnostic_generations) return;
        const auto oldest = node.canvas_generation - retained_diagnostic_generations;
        std::erase_if(
            node.canvas_probable_volume_by_generation,
            [oldest](const auto& entry) { return entry.first < oldest; });
    }

    static void get_element_width(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_backing_store));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(v8::Number::New(
            info.GetIsolate(),
            element_dimension(*node, "width", node->tag == "canvas" ? 300 : node->layout.width)));
    }

    static void reset_canvas_backing_store(implementation& self, dom_node& node)
    {
        if (node.tag != "canvas") return;
        node.canvas_rects.clear();
        node.canvas_lines.clear();
        advance_canvas_generation(node);
        node.canvas_commands.clear();
        node.canvas_strings.clear();
        node.canvas_string_indices.clear();
        const auto state = self.canvas_states.find(self.wrapper_key(node));
        if (state == self.canvas_states.end() || state->second == nullptr) return;
        state->second->current_x = 0;
        state->second->current_y = 0;
        state->second->start_x = 0;
        state->second->start_y = 0;
        state->second->has_current = false;
        state->second->transform = {};
        state->second->stack.clear();
        state->second->path.clear();
        state->second->line_dash.clear();
        state->second->has_clip = false;
        state->second->has_emitted_paint_state = false;
    }

    static void set_element_width(v8::Local<v8::Name>, v8::Local<v8::Value> value, const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_backing_store));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) {
            node->attributes["width"] = to_utf8(info.GetIsolate(), value);
            reset_canvas_backing_store(*self, *node);
        }
    }

    static void get_element_height(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_backing_store));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) info.GetReturnValue().Set(v8::Number::New(
            info.GetIsolate(),
            element_dimension(*node, "height", node->tag == "canvas" ? 150 : node->layout.height)));
    }

    static void set_element_height(v8::Local<v8::Name>, v8::Local<v8::Value> value, const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_backing_store));
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) {
            node->attributes["height"] = to_utf8(info.GetIsolate(), value);
            reset_canvas_backing_store(*self, *node);
        }
    }

    static void get_element_type(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto iterator = node->attributes.find("type");
        const auto value = iterator != node->attributes.end()
            ? iterator->second
            : node->tag == "input" ? std::string("text")
            : node->tag == "button" ? std::string("submit")
            : std::string{};
        info.GetReturnValue().Set(js_string(info.GetIsolate(), value.c_str()));
    }

    static void set_element_type(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) node->attributes["type"] = to_utf8(info.GetIsolate(), value);
    }

    static void get_boolean_attribute(
        const v8::PropertyCallbackInfo<v8::Value>& info,
        const char* name)
    {
        auto* node = unwrap_node(info.Holder());
        info.GetReturnValue().Set(v8::Boolean::New(
            info.GetIsolate(),
            node != nullptr && node->attributes.contains(name)));
    }

    static void set_boolean_attribute(
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info,
        const char* name)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        if (value->BooleanValue(info.GetIsolate())) node->attributes[name] = std::string{};
        else node->attributes.erase(name);
        self->recascade_connected_subtree(*node);
    }

    static void get_checked(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        get_boolean_attribute(info, "checked");
    }

    static void set_checked(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        set_boolean_attribute(value, info, "checked");
    }

    static void get_selected(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        get_boolean_attribute(info, "selected");
    }

    static void set_selected(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        if (!value->BooleanValue(info.GetIsolate())) {
            node->attributes.erase("selected");
            self->recascade_connected_subtree(*node);
            return;
        }
        auto* select = node->parent;
        while (select != nullptr && select->tag != "select") select = select->parent;
        if (select != nullptr) {
            const auto clear_selected = [&](const auto& recurse, dom_node& root) -> void {
                if (root.tag == "option") root.attributes.erase("selected");
                for (auto* child : root.children) {
                    if (child != nullptr) recurse(recurse, *child);
                }
            };
            clear_selected(clear_selected, *select);
        }
        node->attributes["selected"] = std::string{};
        self->recascade_connected_subtree(*node);
    }

    static void get_disabled(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        get_boolean_attribute(info, "disabled");
    }

    static void set_disabled(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        set_boolean_attribute(value, info, "disabled");
    }

    static void get_element_url(v8::Local<v8::Name> property, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), property);
        const auto iterator = node->attributes.find(name);
        info.GetReturnValue().Set(js_string(
            info.GetIsolate(),
            iterator == node->attributes.end() ? "" : iterator->second.c_str()));
    }

    static void set_element_src(v8::Local<v8::Name>, v8::Local<v8::Value> value, const v8::PropertyCallbackInfo<void>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        node->attributes["src"] = to_utf8(info.GetIsolate(), value);
        if (node->tag != "img") return;
        auto local_context = info.GetIsolate()->GetCurrentContext();
        info.Holder()->Set(local_context, js_string(info.GetIsolate(), "complete"), v8::True(info.GetIsolate())).Check();
        v8::Local<v8::Value> onload;
        if (info.Holder()->Get(local_context, js_string(info.GetIsolate(), "onload")).ToLocal(&onload)
            && onload->IsFunction()) {
            onload.As<v8::Function>()->Call(local_context, info.Holder(), 0, nullptr).ToLocalChecked();
        }
    }

    static void set_element_href(v8::Local<v8::Name>, v8::Local<v8::Value> value, const v8::PropertyCallbackInfo<void>& info)
    {
        auto* node = unwrap_node(info.Holder());
        if (node != nullptr) node->attributes["href"] = to_utf8(info.GetIsolate(), value);
    }

    static void canvas_no_op(const v8::FunctionCallbackInfo<v8::Value>&)
    {
    }

    static void canvas_text_no_op(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        ++state->node->canvas_fill_text_calls;
        if (info.Length() < 3) return;
        canvas_emit_paint_state(info, *state);
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto text = to_utf8(info.GetIsolate(), info[0]);
        canvas_append_resource_command(
            *state->node,
            canvas_command_kind::fill_text,
            canvas_intern_string(*state->node, text),
            {info[1]->NumberValue(context).FromMaybe(0),
             info[2]->NumberValue(context).FromMaybe(0)});
    }

    static void canvas_stroke_text_no_op(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        ++state->node->canvas_stroke_text_calls;
        if (info.Length() < 3) return;
        canvas_emit_paint_state(info, *state);
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto text = to_utf8(info.GetIsolate(), info[0]);
        canvas_append_resource_command(
            *state->node,
            canvas_command_kind::stroke_text,
            canvas_intern_string(*state->node, text),
            {info[1]->NumberValue(context).FromMaybe(0),
             info[2]->NumberValue(context).FromMaybe(0)});
    }

    static canvas_state* canvas_path_state(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        return info.Data()->IsExternal()
            ? static_cast<canvas_state*>(info.Data().As<v8::External>()->Value(v8::kExternalPointerTypeTagDefault))
            : nullptr;
    }

    enum class canvas_command_kind : uint32_t {
        save = 1,
        restore = 2,
        reset_transform = 3,
        set_transform = 4,
        transform = 5,
        translate = 6,
        scale = 7,
        rotate = 8,
        begin_path = 9,
        close_path = 10,
        move_to = 11,
        line_to = 12,
        bezier_curve_to = 13,
        quadratic_curve_to = 14,
        arc = 15,
        arc_to = 16,
        rect = 17,
        clip = 18,
        set_line_dash = 19,
        stroke = 20,
        fill = 21,
        fill_rect = 22,
        stroke_rect = 23,
        clear_rect = 24,
        fill_text = 25,
        stroke_text = 26,
        draw_canvas = 27,
        fill_svg_path = 28,
        stroke_svg_path = 29,
        fill_style = 40,
        stroke_style = 41,
        line_width = 42,
        line_cap = 43,
        line_join = 44,
        miter_limit = 45,
        global_alpha = 46,
        line_dash_offset = 47,
        font = 48,
        text_align = 49,
        text_baseline = 50,
        image_smoothing_enabled = 51,
        image_smoothing_quality = 52,
        global_composite_operation = 53,
        shadow_color = 54,
        shadow_blur = 55,
        shadow_offset_x = 56,
        shadow_offset_y = 57
    };

    static void canvas_append_command(
        dom_node& node,
        canvas_command_kind kind,
        std::initializer_list<double> arguments = {})
    {
        htmlml_canvas_command command{};
        command.kind = static_cast<uint32_t>(kind);
        command.flags = static_cast<uint32_t>(arguments.size());
        size_t index = 0;
        for (const auto argument : arguments) {
            if (index == 8U) break;
            command.data.values[index++] = argument;
        }
        node.canvas_commands.push_back(command);
    }

    static void canvas_append_resource_command(
        dom_node& node,
        canvas_command_kind kind,
        uint32_t resource_id,
        std::initializer_list<double> arguments = {})
    {
        htmlml_canvas_command command{};
        command.kind = static_cast<uint32_t>(kind);
        command.flags = static_cast<uint32_t>(arguments.size());
        command.resource_id = resource_id;
        size_t index = 0;
        for (const auto argument : arguments) {
            if (index == 8U) break;
            command.data.values[index++] = argument;
        }
        node.canvas_commands.push_back(command);
    }

    static uint32_t canvas_intern_string(dom_node& node, const std::string& value)
    {
        if (const auto known = node.canvas_string_indices.find(value);
            known != node.canvas_string_indices.end()) {
            return known->second;
        }
        const auto index = static_cast<uint32_t>(node.canvas_strings.size());
        node.canvas_strings.push_back(value);
        node.canvas_string_indices.emplace(value, index);
        return index;
    }

    static double canvas_number_property(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        const char* name,
        double fallback)
    {
        const auto context = info.GetIsolate()->GetCurrentContext();
        v8::Local<v8::Value> value;
        if (!info.This()->Get(context, js_string(info.GetIsolate(), name)).ToLocal(&value)) {
            return fallback;
        }
        const auto result = value->NumberValue(context).FromMaybe(fallback);
        return std::isfinite(result) ? result : fallback;
    }

    static bool canvas_boolean_property(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        const char* name,
        bool fallback)
    {
        v8::Local<v8::Value> value;
        return info.This()->Get(
                info.GetIsolate()->GetCurrentContext(),
                js_string(info.GetIsolate(), name)).ToLocal(&value)
            ? value->BooleanValue(info.GetIsolate())
            : fallback;
    }

    static std::pair<double, double> canvas_transform_point(
        const canvas_state& state,
        double x,
        double y)
    {
        return {
            state.transform.a * x + state.transform.c * y + state.transform.e,
            state.transform.b * x + state.transform.d * y + state.transform.f};
    }

    static void canvas_multiply_transform(canvas_state& state, const canvas_transform& value)
    {
        const auto current = state.transform;
        state.transform = {
            current.a * value.a + current.c * value.b,
            current.b * value.a + current.d * value.b,
            current.a * value.c + current.c * value.d,
            current.b * value.c + current.d * value.d,
            current.a * value.e + current.c * value.f + current.e,
            current.b * value.e + current.d * value.f + current.f};
    }

    static std::string canvas_string_property(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        const char* name,
        const char* fallback)
    {
        v8::Local<v8::Value> value;
        return info.This()->Get(
                info.GetIsolate()->GetCurrentContext(),
                js_string(info.GetIsolate(), name)).ToLocal(&value)
            && value->IsString()
            ? to_utf8(info.GetIsolate(), value)
            : std::string(fallback);
    }

    static void canvas_emit_string_property(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        canvas_state& state,
        canvas_command_kind opcode,
        const char* name,
        const char* fallback)
    {
        const auto value = canvas_string_property(info, name, fallback);
        canvas_append_resource_command(
            *state.node,
            opcode,
            canvas_intern_string(*state.node, value));
    }

    static void canvas_emit_number_property(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        canvas_state& state,
        canvas_command_kind opcode,
        const char* name,
        double fallback)
    {
        canvas_append_command(
            *state.node,
            opcode,
            {canvas_number_property(info, name, fallback)});
    }

    static void canvas_emit_paint_state(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        canvas_state& state)
    {
        if (state.node == nullptr) return;
        const auto first = !state.has_emitted_paint_state;
        auto& emitted = state.emitted_paint_state;
        const auto emit_string = [&](
            canvas_command_kind kind,
            const char* name,
            const char* fallback,
            std::string& previous) {
            auto value = canvas_string_property(info, name, fallback);
            if (first || value != previous) {
                canvas_append_resource_command(
                    *state.node,
                    kind,
                    canvas_intern_string(*state.node, value));
                previous = std::move(value);
            }
        };
        const auto emit_number = [&](
            canvas_command_kind kind,
            const char* name,
            double fallback,
            double& previous) {
            const auto value = canvas_number_property(info, name, fallback);
            if (first || value != previous) {
                canvas_append_command(*state.node, kind, {value});
                previous = value;
            }
        };

        emit_string(canvas_command_kind::fill_style, "fillStyle", "#000000", emitted.fill_style);
        emit_string(canvas_command_kind::stroke_style, "strokeStyle", "#000000", emitted.stroke_style);
        emit_number(canvas_command_kind::line_width, "lineWidth", 1, emitted.line_width);
        emit_string(canvas_command_kind::line_cap, "lineCap", "butt", emitted.line_cap);
        emit_string(canvas_command_kind::line_join, "lineJoin", "miter", emitted.line_join);
        emit_number(canvas_command_kind::miter_limit, "miterLimit", 10, emitted.miter_limit);
        emit_number(canvas_command_kind::global_alpha, "globalAlpha", 1, emitted.global_alpha);
        emit_number(
            canvas_command_kind::line_dash_offset,
            "lineDashOffset",
            0,
            emitted.line_dash_offset);
        emit_string(canvas_command_kind::font, "font", "10px sans-serif", emitted.font);
        emit_string(canvas_command_kind::text_align, "textAlign", "start", emitted.text_align);
        emit_string(
            canvas_command_kind::text_baseline,
            "textBaseline",
            "alphabetic",
            emitted.text_baseline);
        const auto smoothing = canvas_boolean_property(info, "imageSmoothingEnabled", true);
        if (first || smoothing != emitted.image_smoothing_enabled) {
            canvas_append_command(
                *state.node,
                canvas_command_kind::image_smoothing_enabled,
                {smoothing ? 1.0 : 0.0});
            emitted.image_smoothing_enabled = smoothing;
        }
        emit_string(
            canvas_command_kind::image_smoothing_quality,
            "imageSmoothingQuality",
            "low",
            emitted.image_smoothing_quality);
        emit_string(
            canvas_command_kind::global_composite_operation,
            "globalCompositeOperation",
            "source-over",
            emitted.global_composite_operation);
        emit_string(
            canvas_command_kind::shadow_color,
            "shadowColor",
            "rgba(0, 0, 0, 0)",
            emitted.shadow_color);
        emit_number(canvas_command_kind::shadow_blur, "shadowBlur", 0, emitted.shadow_blur);
        emit_number(
            canvas_command_kind::shadow_offset_x,
            "shadowOffsetX",
            0,
            emitted.shadow_offset_x);
        emit_number(
            canvas_command_kind::shadow_offset_y,
            "shadowOffsetY",
            0,
            emitted.shadow_offset_y);
        state.has_emitted_paint_state = true;
    }

    static void canvas_emit_snapshot_state(dom_node& node, const canvas_snapshot& snapshot)
    {
        const auto emit_string = [&](canvas_command_kind kind, const std::string& value) {
            canvas_append_resource_command(
                node,
                kind,
                canvas_intern_string(node, value));
        };
        const auto emit_number = [&](canvas_command_kind kind, double value) {
            canvas_append_command(node, kind, {value});
        };

        canvas_append_command(
            node,
            canvas_command_kind::set_transform,
            {snapshot.transform.a, snapshot.transform.b, snapshot.transform.c,
             snapshot.transform.d, snapshot.transform.e, snapshot.transform.f});
        emit_string(canvas_command_kind::fill_style, snapshot.fill_style);
        emit_string(canvas_command_kind::stroke_style, snapshot.stroke_style);
        emit_number(canvas_command_kind::line_width, snapshot.line_width);
        emit_string(canvas_command_kind::line_cap, snapshot.line_cap);
        emit_string(canvas_command_kind::line_join, snapshot.line_join);
        emit_number(canvas_command_kind::miter_limit, snapshot.miter_limit);
        emit_number(canvas_command_kind::global_alpha, snapshot.global_alpha);
        emit_number(canvas_command_kind::line_dash_offset, snapshot.line_dash_offset);
        emit_string(canvas_command_kind::font, snapshot.font);
        emit_string(canvas_command_kind::text_align, snapshot.text_align);
        emit_string(canvas_command_kind::text_baseline, snapshot.text_baseline);
        emit_number(
            canvas_command_kind::image_smoothing_enabled,
            snapshot.image_smoothing_enabled ? 1.0 : 0.0);
        emit_string(
            canvas_command_kind::image_smoothing_quality,
            snapshot.image_smoothing_quality);
        emit_string(
            canvas_command_kind::global_composite_operation,
            snapshot.global_composite_operation);
        emit_string(canvas_command_kind::shadow_color, snapshot.shadow_color);
        emit_number(canvas_command_kind::shadow_blur, snapshot.shadow_blur);
        emit_number(canvas_command_kind::shadow_offset_x, snapshot.shadow_offset_x);
        emit_number(canvas_command_kind::shadow_offset_y, snapshot.shadow_offset_y);
    }

    static void canvas_compact_full_overwrite(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        canvas_state& state)
    {
        auto& node = *state.node;
        node.canvas_rects.clear();
        node.canvas_lines.clear();
        advance_canvas_generation(node);
        node.canvas_commands.clear();
        node.canvas_strings.clear();
        node.canvas_string_indices.clear();
        state.has_emitted_paint_state = false;

        // Preserve the command-side save stack after discarding dead pixels.
        // Clipped stacks are excluded by callers because clip paths are not yet
        // reconstructible from canvas_snapshot.
        for (const auto& snapshot : state.stack) {
            canvas_emit_snapshot_state(node, snapshot);
            canvas_append_command(node, canvas_command_kind::save);
        }
        canvas_emit_paint_state(info, state);
        canvas_append_command(
            node,
            canvas_command_kind::set_transform,
            {state.transform.a, state.transform.b, state.transform.c,
             state.transform.d, state.transform.e, state.transform.f});
    }

    static void canvas_save(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_state));
        auto* state = canvas_path_state(info);
        if (state == nullptr) return;
        canvas_emit_paint_state(info, *state);
        canvas_append_command(*state->node, canvas_command_kind::save);
        canvas_snapshot snapshot;
        snapshot.transform = state->transform;
        snapshot.fill_style = canvas_string_property(info, "fillStyle", "#000000");
        snapshot.stroke_style = canvas_string_property(info, "strokeStyle", "#000000");
        snapshot.global_composite_operation = canvas_string_property(info, "globalCompositeOperation", "source-over");
        snapshot.line_cap = canvas_string_property(info, "lineCap", "butt");
        snapshot.line_join = canvas_string_property(info, "lineJoin", "miter");
        snapshot.font = canvas_string_property(info, "font", "10px sans-serif");
        snapshot.text_align = canvas_string_property(info, "textAlign", "start");
        snapshot.text_baseline = canvas_string_property(info, "textBaseline", "alphabetic");
        snapshot.image_smoothing_quality = canvas_string_property(info, "imageSmoothingQuality", "low");
        snapshot.shadow_color = canvas_string_property(info, "shadowColor", "rgba(0, 0, 0, 0)");
        snapshot.line_width = canvas_number_property(info, "lineWidth", 1);
        snapshot.global_alpha = canvas_number_property(info, "globalAlpha", 1);
        snapshot.miter_limit = canvas_number_property(info, "miterLimit", 10);
        snapshot.line_dash_offset = canvas_number_property(info, "lineDashOffset", 0);
        snapshot.shadow_blur = canvas_number_property(info, "shadowBlur", 0);
        snapshot.shadow_offset_x = canvas_number_property(info, "shadowOffsetX", 0);
        snapshot.shadow_offset_y = canvas_number_property(info, "shadowOffsetY", 0);
        snapshot.image_smoothing_enabled = canvas_boolean_property(info, "imageSmoothingEnabled", true);
        snapshot.has_clip = state->has_clip;
        snapshot.line_dash = state->line_dash;
        state->stack.push_back(std::move(snapshot));
    }

    static void canvas_restore(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_state));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->stack.empty()) return;
        auto snapshot = std::move(state->stack.back());
        state->stack.pop_back();
        canvas_append_command(*state->node, canvas_command_kind::restore);
        state->transform = snapshot.transform;
        state->line_dash = snapshot.line_dash;
        state->has_clip = snapshot.has_clip;
        const auto context = info.GetIsolate()->GetCurrentContext();
        info.This()->Set(context, js_string(info.GetIsolate(), "fillStyle"), js_string(info.GetIsolate(), snapshot.fill_style.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "strokeStyle"), js_string(info.GetIsolate(), snapshot.stroke_style.c_str())).Check();
        info.This()->Set(
            context,
            js_string(info.GetIsolate(), "globalCompositeOperation"),
            js_string(info.GetIsolate(), snapshot.global_composite_operation.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "lineWidth"), v8::Number::New(info.GetIsolate(), snapshot.line_width)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "globalAlpha"), v8::Number::New(info.GetIsolate(), snapshot.global_alpha)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "lineCap"), js_string(info.GetIsolate(), snapshot.line_cap.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "lineJoin"), js_string(info.GetIsolate(), snapshot.line_join.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "miterLimit"), v8::Number::New(info.GetIsolate(), snapshot.miter_limit)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "lineDashOffset"), v8::Number::New(info.GetIsolate(), snapshot.line_dash_offset)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "font"), js_string(info.GetIsolate(), snapshot.font.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "textAlign"), js_string(info.GetIsolate(), snapshot.text_align.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "textBaseline"), js_string(info.GetIsolate(), snapshot.text_baseline.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "imageSmoothingEnabled"), v8::Boolean::New(info.GetIsolate(), snapshot.image_smoothing_enabled)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "imageSmoothingQuality"), js_string(info.GetIsolate(), snapshot.image_smoothing_quality.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "shadowColor"), js_string(info.GetIsolate(), snapshot.shadow_color.c_str())).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "shadowBlur"), v8::Number::New(info.GetIsolate(), snapshot.shadow_blur)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "shadowOffsetX"), v8::Number::New(info.GetIsolate(), snapshot.shadow_offset_x)).Check();
        info.This()->Set(context, js_string(info.GetIsolate(), "shadowOffsetY"), v8::Number::New(info.GetIsolate(), snapshot.shadow_offset_y)).Check();
        state->emitted_paint_state = snapshot;
        state->has_emitted_paint_state = true;
    }

    static void canvas_scale(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 2) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(1);
        const auto y = info[1]->NumberValue(context).FromMaybe(1);
        canvas_append_command(*state->node, canvas_command_kind::scale, {x, y});
        canvas_multiply_transform(*state, canvas_transform{
            x, 0, 0, y, 0, 0});
    }

    static void canvas_translate(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 2) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(0);
        const auto y = info[1]->NumberValue(context).FromMaybe(0);
        canvas_append_command(*state->node, canvas_command_kind::translate, {x, y});
        canvas_multiply_transform(*state, canvas_transform{
            1, 0, 0, 1, x, y});
    }

    static void canvas_rotate(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 1) return;
        const auto angle = info[0]->NumberValue(info.GetIsolate()->GetCurrentContext()).FromMaybe(0);
        canvas_append_command(*state->node, canvas_command_kind::rotate, {angle});
        const auto cosine = std::cos(angle);
        const auto sine = std::sin(angle);
        canvas_multiply_transform(*state, canvas_transform{cosine, sine, -sine, cosine, 0, 0});
    }

    static canvas_transform canvas_transform_arguments(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        const auto context = info.GetIsolate()->GetCurrentContext();
        if (info.Length() >= 6) {
            return {
                info[0]->NumberValue(context).FromMaybe(1),
                info[1]->NumberValue(context).FromMaybe(0),
                info[2]->NumberValue(context).FromMaybe(0),
                info[3]->NumberValue(context).FromMaybe(1),
                info[4]->NumberValue(context).FromMaybe(0),
                info[5]->NumberValue(context).FromMaybe(0)};
        }
        return {};
    }

    static void canvas_transform_method(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr) return;
        const auto value = canvas_transform_arguments(info);
        canvas_append_command(*state->node, canvas_command_kind::transform, {
            value.a, value.b, value.c, value.d, value.e, value.f});
        canvas_multiply_transform(*state, value);
    }

    static void canvas_set_transform(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr) return;
        state->transform = canvas_transform_arguments(info);
        canvas_append_command(*state->node, canvas_command_kind::set_transform, {
            state->transform.a, state->transform.b, state->transform.c,
            state->transform.d, state->transform.e, state->transform.f});
    }

    static void canvas_reset_transform(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_transform));
        auto* state = canvas_path_state(info);
        if (state == nullptr) return;
        state->transform = {};
        canvas_append_command(*state->node, canvas_command_kind::reset_transform);
    }

    static void canvas_begin_path(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr) return;
        canvas_append_command(*state->node, canvas_command_kind::begin_path);
        state->path.clear();
        state->has_current = false;
    }

    static void canvas_move_to(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 2) return;
        auto context = info.GetIsolate()->GetCurrentContext();
        const auto authored_x = info[0]->NumberValue(context).FromMaybe(0);
        const auto authored_y = info[1]->NumberValue(context).FromMaybe(0);
        canvas_append_command(*state->node, canvas_command_kind::move_to, {authored_x, authored_y});
        const auto [x, y] = canvas_transform_point(
            *state,
            authored_x,
            authored_y);
        state->current_x = x;
        state->current_y = y;
        state->start_x = state->current_x;
        state->start_y = state->current_y;
        state->has_current = true;
    }

    static void canvas_line_to(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 2) return;
        auto context = info.GetIsolate()->GetCurrentContext();
        const auto authored_x = info[0]->NumberValue(context).FromMaybe(0);
        const auto authored_y = info[1]->NumberValue(context).FromMaybe(0);
        canvas_append_command(*state->node, canvas_command_kind::line_to, {authored_x, authored_y});
        const auto [x, y] = canvas_transform_point(
            *state,
            authored_x,
            authored_y);
        if (!state->has_current) {
            state->current_x = state->start_x = x;
            state->current_y = state->start_y = y;
            state->has_current = true;
            return;
        }
        state->path.push_back({state->current_x, state->current_y, x, y});
        state->current_x = x;
        state->current_y = y;
    }

    static void canvas_close_path(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || !state->has_current) return;
        canvas_append_command(*state->node, canvas_command_kind::close_path);
        state->path.push_back({state->current_x, state->current_y, state->start_x, state->start_y});
        state->current_x = state->start_x;
        state->current_y = state->start_y;
    }

    static void canvas_path_rect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 4) return;
        auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(0);
        const auto y = info[1]->NumberValue(context).FromMaybe(0);
        const auto width = info[2]->NumberValue(context).FromMaybe(0);
        const auto height = info[3]->NumberValue(context).FromMaybe(0);
        canvas_append_command(*state->node, canvas_command_kind::rect, {x, y, width, height});
        const auto top_left = canvas_transform_point(*state, x, y);
        const auto top_right = canvas_transform_point(*state, x + width, y);
        const auto bottom_right = canvas_transform_point(*state, x + width, y + height);
        const auto bottom_left = canvas_transform_point(*state, x, y + height);
        state->path.push_back({top_left.first, top_left.second, top_right.first, top_right.second});
        state->path.push_back({top_right.first, top_right.second, bottom_right.first, bottom_right.second});
        state->path.push_back({bottom_right.first, bottom_right.second, bottom_left.first, bottom_left.second});
        state->path.push_back({bottom_left.first, bottom_left.second, top_left.first, top_left.second});
        state->current_x = state->start_x = top_left.first;
        state->current_y = state->start_y = top_left.second;
        state->has_current = true;
    }

    static void append_canvas_curve_point(canvas_state& state, double x, double y)
    {
        const auto [transformed_x, transformed_y] = canvas_transform_point(state, x, y);
        if (!state.has_current) {
            state.current_x = state.start_x = transformed_x;
            state.current_y = state.start_y = transformed_y;
            state.has_current = true;
            return;
        }
        state.path.push_back({state.current_x, state.current_y, transformed_x, transformed_y});
        state.current_x = transformed_x;
        state.current_y = transformed_y;
    }

    static void canvas_quadratic_curve_to(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 4 || !state->has_current) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto control_x = info[0]->NumberValue(context).FromMaybe(0);
        const auto control_y = info[1]->NumberValue(context).FromMaybe(0);
        const auto end_x = info[2]->NumberValue(context).FromMaybe(0);
        const auto end_y = info[3]->NumberValue(context).FromMaybe(0);
        canvas_append_command(
            *state->node,
            canvas_command_kind::quadratic_curve_to,
            {control_x, control_y, end_x, end_y});
        const auto start_x = state->current_x;
        const auto start_y = state->current_y;
        const auto transformed_control = canvas_transform_point(*state, control_x, control_y);
        const auto transformed_end = canvas_transform_point(*state, end_x, end_y);
        for (int index = 1; index <= 16; ++index) {
            const auto t = static_cast<double>(index) / 16.0;
            const auto one_minus = 1.0 - t;
            const auto x = one_minus * one_minus * start_x
                + 2.0 * one_minus * t * transformed_control.first
                + t * t * transformed_end.first;
            const auto y = one_minus * one_minus * start_y
                + 2.0 * one_minus * t * transformed_control.second
                + t * t * transformed_end.second;
            state->path.push_back({state->current_x, state->current_y, x, y});
            state->current_x = x;
            state->current_y = y;
        }
    }

    static void canvas_bezier_curve_to(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 6 || !state->has_current) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto authored_control1_x = info[0]->NumberValue(context).FromMaybe(0);
        const auto authored_control1_y = info[1]->NumberValue(context).FromMaybe(0);
        const auto authored_control2_x = info[2]->NumberValue(context).FromMaybe(0);
        const auto authored_control2_y = info[3]->NumberValue(context).FromMaybe(0);
        const auto authored_end_x = info[4]->NumberValue(context).FromMaybe(0);
        const auto authored_end_y = info[5]->NumberValue(context).FromMaybe(0);
        canvas_append_command(
            *state->node,
            canvas_command_kind::bezier_curve_to,
            {authored_control1_x, authored_control1_y,
             authored_control2_x, authored_control2_y,
             authored_end_x, authored_end_y});
        const auto control1 = canvas_transform_point(
            *state,
            authored_control1_x,
            authored_control1_y);
        const auto control2 = canvas_transform_point(
            *state,
            authored_control2_x,
            authored_control2_y);
        const auto end = canvas_transform_point(
            *state,
            authored_end_x,
            authored_end_y);
        const auto start_x = state->current_x;
        const auto start_y = state->current_y;
        for (int index = 1; index <= 20; ++index) {
            const auto t = static_cast<double>(index) / 20.0;
            const auto one_minus = 1.0 - t;
            const auto x = one_minus * one_minus * one_minus * start_x
                + 3.0 * one_minus * one_minus * t * control1.first
                + 3.0 * one_minus * t * t * control2.first
                + t * t * t * end.first;
            const auto y = one_minus * one_minus * one_minus * start_y
                + 3.0 * one_minus * one_minus * t * control1.second
                + 3.0 * one_minus * t * t * control2.second
                + t * t * t * end.second;
            state->path.push_back({state->current_x, state->current_y, x, y});
            state->current_x = x;
            state->current_y = y;
        }
    }

    static void canvas_arc(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 5) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto center_x = info[0]->NumberValue(context).FromMaybe(0);
        const auto center_y = info[1]->NumberValue(context).FromMaybe(0);
        const auto radius = std::abs(info[2]->NumberValue(context).FromMaybe(0));
        auto start = info[3]->NumberValue(context).FromMaybe(0);
        auto end = info[4]->NumberValue(context).FromMaybe(0);
        const auto anticlockwise = info.Length() > 5 && info[5]->BooleanValue(info.GetIsolate());
        canvas_append_command(
            *state->node,
            canvas_command_kind::arc,
            {center_x, center_y, radius, start, end, anticlockwise ? 1.0 : 0.0});
        constexpr double full_circle = 6.28318530717958647692;
        if (!anticlockwise && end < start) end += full_circle;
        if (anticlockwise && end > start) end -= full_circle;
        const auto steps = std::max(4, static_cast<int>(std::ceil(std::abs(end - start) / full_circle * 32.0)));
        for (int index = 0; index <= steps; ++index) {
            const auto angle = start + (end - start) * static_cast<double>(index) / steps;
            const auto point = canvas_transform_point(
                *state,
                center_x + std::cos(angle) * radius,
                center_y + std::sin(angle) * radius);
            if (!state->has_current) {
                state->current_x = state->start_x = point.first;
                state->current_y = state->start_y = point.second;
                state->has_current = true;
            } else {
                state->path.push_back({state->current_x, state->current_y, point.first, point.second});
                state->current_x = point.first;
                state->current_y = point.second;
            }
        }
    }

    static void canvas_arc_to(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || info.Length() < 5) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto authored_x1 = info[0]->NumberValue(context).FromMaybe(0);
        const auto authored_y1 = info[1]->NumberValue(context).FromMaybe(0);
        const auto authored_x2 = info[2]->NumberValue(context).FromMaybe(0);
        const auto authored_y2 = info[3]->NumberValue(context).FromMaybe(0);
        const auto authored_radius = info[4]->NumberValue(context).FromMaybe(0);
        if (authored_radius < 0) {
            info.GetIsolate()->ThrowException(v8::Exception::RangeError(
                js_string(info.GetIsolate(), "arcTo radius must be non-negative")));
            return;
        }
        canvas_append_command(
            *state->node,
            canvas_command_kind::arc_to,
            {authored_x1, authored_y1, authored_x2, authored_y2, authored_radius});

        const auto point1 = canvas_transform_point(*state, authored_x1, authored_y1);
        const auto point2 = canvas_transform_point(*state, authored_x2, authored_y2);
        if (!state->has_current) {
            state->current_x = state->start_x = point1.first;
            state->current_y = state->start_y = point1.second;
            state->has_current = true;
            return;
        }

        const auto scale_x = std::hypot(state->transform.a, state->transform.b);
        const auto scale_y = std::hypot(state->transform.c, state->transform.d);
        const auto radius = authored_radius * (scale_x + scale_y) * 0.5;
        const auto v1_x = state->current_x - point1.first;
        const auto v1_y = state->current_y - point1.second;
        const auto v2_x = point2.first - point1.first;
        const auto v2_y = point2.second - point1.second;
        const auto length1 = std::hypot(v1_x, v1_y);
        const auto length2 = std::hypot(v2_x, v2_y);
        constexpr double epsilon = 1e-9;
        const auto cross = v1_x * v2_y - v1_y * v2_x;
        if (radius <= epsilon || length1 <= epsilon || length2 <= epsilon
            || std::abs(cross) <= epsilon) {
            state->path.push_back({
                state->current_x, state->current_y, point1.first, point1.second});
            state->current_x = point1.first;
            state->current_y = point1.second;
            return;
        }

        const auto unit1_x = v1_x / length1;
        const auto unit1_y = v1_y / length1;
        const auto unit2_x = v2_x / length2;
        const auto unit2_y = v2_y / length2;
        const auto dot = std::clamp(unit1_x * unit2_x + unit1_y * unit2_y, -1.0, 1.0);
        const auto angle = std::acos(dot);
        const auto tangent = std::tan(angle * 0.5);
        if (std::abs(tangent) <= epsilon) {
            state->path.push_back({
                state->current_x, state->current_y, point1.first, point1.second});
            state->current_x = point1.first;
            state->current_y = point1.second;
            return;
        }
        const auto tangent_distance = radius / tangent;
        const auto tangent1_x = point1.first + unit1_x * tangent_distance;
        const auto tangent1_y = point1.second + unit1_y * tangent_distance;
        const auto tangent2_x = point1.first + unit2_x * tangent_distance;
        const auto tangent2_y = point1.second + unit2_y * tangent_distance;
        const auto bisector_x = unit1_x + unit2_x;
        const auto bisector_y = unit1_y + unit2_y;
        const auto bisector_length = std::hypot(bisector_x, bisector_y);
        if (bisector_length <= epsilon) {
            state->path.push_back({
                state->current_x, state->current_y, point1.first, point1.second});
            state->current_x = point1.first;
            state->current_y = point1.second;
            return;
        }
        const auto center_distance = radius / std::sin(angle * 0.5);
        const auto center_x = point1.first + bisector_x / bisector_length * center_distance;
        const auto center_y = point1.second + bisector_y / bisector_length * center_distance;
        state->path.push_back({
            state->current_x, state->current_y, tangent1_x, tangent1_y});
        state->current_x = tangent1_x;
        state->current_y = tangent1_y;
        auto start_angle = std::atan2(tangent1_y - center_y, tangent1_x - center_x);
        auto end_angle = std::atan2(tangent2_y - center_y, tangent2_x - center_x);
        constexpr double full_circle = 6.28318530717958647692;
        if (cross < 0) {
            while (end_angle < start_angle) end_angle += full_circle;
        } else {
            while (end_angle > start_angle) end_angle -= full_circle;
        }
        for (int index = 1; index <= 12; ++index) {
            const auto angle_at = start_angle
                + (end_angle - start_angle) * static_cast<double>(index) / 12.0;
            const auto x = center_x + std::cos(angle_at) * radius;
            const auto y = center_y + std::sin(angle_at) * radius;
            state->path.push_back({state->current_x, state->current_y, x, y});
            state->current_x = x;
            state->current_y = y;
        }
    }

    static void canvas_stroke(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        if (info.Length() > 0 && info[0]->IsObject()) {
            v8::Local<v8::Value> path_data;
            if (info[0].As<v8::Object>()->Get(
                    context,
                    js_string(info.GetIsolate(), "__htmlmlSvgPathData")).ToLocal(&path_data)
                && path_data->IsString()) {
                canvas_emit_paint_state(info, *state);
                canvas_append_resource_command(
                    *state->node,
                    canvas_command_kind::stroke_svg_path,
                    canvas_intern_string(*state->node, to_utf8(info.GetIsolate(), path_data)));
                return;
            }
        }
        if (state->path.empty()) return;
        canvas_emit_paint_state(info, *state);
        canvas_append_command(*state->node, canvas_command_kind::stroke);
        v8::Local<v8::Value> line_width_value;
        const auto line_width = info.This()->Get(context, js_string(info.GetIsolate(), "lineWidth")).ToLocal(&line_width_value)
            ? line_width_value->NumberValue(context).FromMaybe(1)
            : 1;
        const auto color = canvas_color(info, "strokeStyle", 0x000000FFU);
        constexpr size_t maximum_canvas_lines = 200000;
        auto& lines = state->node->canvas_lines;
        if (lines.size() + state->path.size() > maximum_canvas_lines) lines.clear();
        for (const auto& segment : state->path) {
            lines.push_back(canvas_line_command{
                static_cast<float>(segment.x1),
                static_cast<float>(segment.y1),
                static_cast<float>(segment.x2),
                static_cast<float>(segment.y2),
                static_cast<float>(line_width),
                color});
        }
    }

    static uint32_t canvas_color(const v8::FunctionCallbackInfo<v8::Value>& info, const char* property, uint32_t fallback)
    {
        v8::Local<v8::Value> value;
        if (!info.This()->Get(
                info.GetIsolate()->GetCurrentContext(),
                js_string(info.GetIsolate(), property)).ToLocal(&value)
            || !value->IsString()) {
            return fallback;
        }
        const auto parsed = native_document::parse_color(to_utf8(info.GetIsolate(), value));
        auto color = parsed == 0 ? fallback : parsed;
        v8::Local<v8::Value> global_alpha_value;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto global_alpha = info.This()->Get(
                context,
                js_string(info.GetIsolate(), "globalAlpha")).ToLocal(&global_alpha_value)
            ? std::clamp(global_alpha_value->NumberValue(context).FromMaybe(1), 0.0, 1.0)
            : 1.0;
        const auto alpha = static_cast<uint32_t>(std::lround(
            static_cast<double>(color & 0xFFU) * global_alpha));
        return (color & 0xFFFFFF00U) | std::min(255U, alpha);
    }

    static void append_canvas_rect(dom_node& node, double x, double y, double width, double height, uint32_t rgba)
    {
        constexpr size_t maximum_canvas_rects = 100000;
        if (node.canvas_rects.size() >= maximum_canvas_rects) {
            node.canvas_rects.erase(
                node.canvas_rects.begin(),
                node.canvas_rects.begin() + static_cast<std::ptrdiff_t>(maximum_canvas_rects / 2));
        }
        node.canvas_rects.push_back(canvas_rect_command{
            static_cast<float>(x),
            static_cast<float>(y),
            static_cast<float>(width),
            static_cast<float>(height),
            rgba});
    }

    static uint32_t canvas_apply_global_alpha(
        const v8::FunctionCallbackInfo<v8::Value>& info,
        uint32_t color)
    {
        const auto context = info.GetIsolate()->GetCurrentContext();
        v8::Local<v8::Value> global_alpha_value;
        const auto global_alpha = info.This()->Get(
                context,
                js_string(info.GetIsolate(), "globalAlpha")).ToLocal(&global_alpha_value)
            ? std::clamp(global_alpha_value->NumberValue(context).FromMaybe(1), 0.0, 1.0)
            : 1.0;
        const auto alpha = static_cast<uint32_t>(std::lround(
            static_cast<double>(color & 0xFFU) * global_alpha));
        return (color & 0xFFFFFF00U) | std::min(255U, alpha);
    }

    static void canvas_draw_image(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        ++state->node->canvas_draw_image_calls;
        if (info.Length() < 3 || !info[0]->IsObject()) {
            return;
        }
        auto* source = unwrap_node(info[0].As<v8::Object>());
        if (source == nullptr || source->tag != "canvas") return;
        if (source == state->node) {
            ++state->node->canvas_self_draw_image_calls;
            return;
        }
        ++state->node->canvas_canvas_draw_image_calls;

        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto source_width = element_dimension(*source, "width", 300);
        const auto source_height = element_dimension(*source, "height", 150);
        double source_x = 0;
        double source_y = 0;
        double source_draw_width = source_width;
        double source_draw_height = source_height;
        double destination_x = 0;
        double destination_y = 0;
        double destination_width = source_width;
        double destination_height = source_height;
        if (info.Length() >= 9) {
            source_x = info[1]->NumberValue(context).FromMaybe(0);
            source_y = info[2]->NumberValue(context).FromMaybe(0);
            source_draw_width = info[3]->NumberValue(context).FromMaybe(source_width);
            source_draw_height = info[4]->NumberValue(context).FromMaybe(source_height);
            destination_x = info[5]->NumberValue(context).FromMaybe(0);
            destination_y = info[6]->NumberValue(context).FromMaybe(0);
            destination_width = info[7]->NumberValue(context).FromMaybe(source_draw_width);
            destination_height = info[8]->NumberValue(context).FromMaybe(source_draw_height);
        } else if (info.Length() >= 5) {
            destination_x = info[1]->NumberValue(context).FromMaybe(0);
            destination_y = info[2]->NumberValue(context).FromMaybe(0);
            destination_width = info[3]->NumberValue(context).FromMaybe(source_width);
            destination_height = info[4]->NumberValue(context).FromMaybe(source_height);
        } else {
            destination_x = info[1]->NumberValue(context).FromMaybe(0);
            destination_y = info[2]->NumberValue(context).FromMaybe(0);
        }
        if (std::abs(source_draw_width) < 0.001 || std::abs(source_draw_height) < 0.001) return;

        canvas_emit_paint_state(info, *state);
        canvas_append_resource_command(
            *state->node,
            canvas_command_kind::draw_canvas,
            source->id,
            {source_x, source_y, source_draw_width, source_draw_height,
             destination_x, destination_y, destination_width, destination_height});
        state->node->canvas_commands.back().flags =
            static_cast<uint32_t>(source->canvas_commands.size());
        state->node->canvas_commands.back().reserved =
            static_cast<uint32_t>(source->canvas_generation);

        const auto source_left = std::min(source_x, source_x + source_draw_width);
        const auto source_top = std::min(source_y, source_y + source_draw_height);
        const auto source_right = std::max(source_x, source_x + source_draw_width);
        const auto source_bottom = std::max(source_y, source_y + source_draw_height);
        const auto scale_x = destination_width / source_draw_width;
        const auto scale_y = destination_height / source_draw_height;
        const auto map_point = [&](double x, double y) {
            return canvas_transform_point(
                *state,
                destination_x + (x - source_x) * scale_x,
                destination_y + (y - source_y) * scale_y);
        };

        for (const auto& rect : source->canvas_rects) {
            const auto clipped_left = std::max<double>(rect.x, source_left);
            const auto clipped_top = std::max<double>(rect.y, source_top);
            const auto clipped_right = std::min<double>(rect.x + rect.width, source_right);
            const auto clipped_bottom = std::min<double>(rect.y + rect.height, source_bottom);
            if (clipped_right <= clipped_left || clipped_bottom <= clipped_top) continue;
            const auto first = map_point(clipped_left, clipped_top);
            const auto second = map_point(clipped_right, clipped_bottom);
            append_canvas_rect(
                *state->node,
                std::min(first.first, second.first),
                std::min(first.second, second.second),
                std::abs(second.first - first.first),
                std::abs(second.second - first.second),
                canvas_apply_global_alpha(info, rect.rgba));
        }

        constexpr size_t maximum_canvas_lines = 200000;
        auto& destination_lines = state->node->canvas_lines;
        for (const auto& line : source->canvas_lines) {
            const auto left = std::min<double>(line.x1, line.x2);
            const auto top = std::min<double>(line.y1, line.y2);
            const auto right = std::max<double>(line.x1, line.x2);
            const auto bottom = std::max<double>(line.y1, line.y2);
            if (right < source_left || bottom < source_top
                || left > source_right || top > source_bottom) {
                continue;
            }
            if (destination_lines.size() >= maximum_canvas_lines) break;
            const auto first = map_point(line.x1, line.y1);
            const auto second = map_point(line.x2, line.y2);
            destination_lines.push_back(canvas_line_command{
                static_cast<float>(first.first),
                static_cast<float>(first.second),
                static_cast<float>(second.first),
                static_cast<float>(second.second),
                static_cast<float>(
                    line.line_width * std::max(std::abs(scale_x), std::abs(scale_y))),
                canvas_apply_global_alpha(info, line.rgba)});
        }
    }

    static void canvas_fill(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        ++state->node->canvas_fill_calls;
        if (info.Length() > 0 && info[0]->IsObject()) {
            ++state->node->canvas_path_argument_fill_calls;
            v8::Local<v8::Value> path_data;
            if (info[0].As<v8::Object>()->Get(
                    info.GetIsolate()->GetCurrentContext(),
                    js_string(info.GetIsolate(), "__htmlmlSvgPathData")).ToLocal(&path_data)
                && path_data->IsString()) {
                canvas_emit_paint_state(info, *state);
                canvas_append_resource_command(
                    *state->node,
                    canvas_command_kind::fill_svg_path,
                    canvas_intern_string(*state->node, to_utf8(info.GetIsolate(), path_data)));
                return;
            }
        }
        if (state->path.empty()) return;
        canvas_emit_paint_state(info, *state);
        canvas_append_command(*state->node, canvas_command_kind::fill);
        const auto color = canvas_color(info, "fillStyle", 0x000000FFU);
        constexpr double epsilon = 0.01;

        const auto fill_subpath = [&](size_t begin, size_t end) {
            if (begin >= end) return;
            const auto count = end - begin;
            auto minimum_x = std::numeric_limits<double>::infinity();
            auto minimum_y = std::numeric_limits<double>::infinity();
            auto maximum_x = -std::numeric_limits<double>::infinity();
            auto maximum_y = -std::numeric_limits<double>::infinity();
            bool axis_aligned = true;
            std::vector<std::pair<double, double>> vertices;
            vertices.reserve(count + 1U);
            vertices.emplace_back(state->path[begin].x1, state->path[begin].y1);
            for (auto index = begin; index < end; ++index) {
                const auto& segment = state->path[index];
                minimum_x = std::min({minimum_x, segment.x1, segment.x2});
                minimum_y = std::min({minimum_y, segment.y1, segment.y2});
                maximum_x = std::max({maximum_x, segment.x1, segment.x2});
                maximum_y = std::max({maximum_y, segment.y1, segment.y2});
                axis_aligned = axis_aligned
                    && (std::abs(segment.x1 - segment.x2) <= epsilon
                        || std::abs(segment.y1 - segment.y2) <= epsilon);
                vertices.emplace_back(segment.x2, segment.y2);
            }

            const auto closed = std::abs(vertices.front().first - vertices.back().first) <= epsilon
                && std::abs(vertices.front().second - vertices.back().second) <= epsilon;
            if (count == 4U && closed && axis_aligned) {
                append_canvas_rect(
                    *state->node,
                    minimum_x,
                    minimum_y,
                    maximum_x - minimum_x,
                    maximum_y - minimum_y,
                    color);
                return;
            }

            // Canvas closes open subpaths for fill. Rasterize other polygons into
            // one-device-pixel horizontal spans so arbitrary component fills can
            // still be represented by the probe's immutable rectangle command.
            if (!closed) vertices.push_back(vertices.front());
            const auto first_row = static_cast<int>(std::floor(minimum_y));
            const auto last_row = static_cast<int>(std::ceil(maximum_y));
            if (last_row <= first_row || last_row - first_row > 8192) return;
            std::vector<double> intersections;
            intersections.reserve(vertices.size());
            for (auto row = first_row; row < last_row; ++row) {
                const auto scan_y = static_cast<double>(row) + 0.5;
                intersections.clear();
                for (size_t index = 1; index < vertices.size(); ++index) {
                    const auto [x1, y1] = vertices[index - 1U];
                    const auto [x2, y2] = vertices[index];
                    if ((y1 <= scan_y && y2 > scan_y)
                        || (y2 <= scan_y && y1 > scan_y)) {
                        intersections.push_back(
                            x1 + (scan_y - y1) * (x2 - x1) / (y2 - y1));
                    }
                }
                std::sort(intersections.begin(), intersections.end());
                for (size_t index = 1; index < intersections.size(); index += 2U) {
                    const auto left = intersections[index - 1U];
                    const auto right = intersections[index];
                    if (right > left) {
                        append_canvas_rect(
                            *state->node,
                            left,
                            static_cast<double>(row),
                            right - left,
                            1,
                            color);
                    }
                }
            }
        };

        size_t subpath_begin = 0;
        for (size_t index = 1; index < state->path.size(); ++index) {
            const auto& previous = state->path[index - 1U];
            const auto& current = state->path[index];
            if (std::abs(previous.x2 - current.x1) > epsilon
                || std::abs(previous.y2 - current.y1) > epsilon) {
                fill_subpath(subpath_begin, index);
                subpath_begin = index;
            }
        }
        fill_subpath(subpath_begin, state->path.size());
    }

    static void canvas_clip(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_path));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr) return;
        canvas_append_command(*state->node, canvas_command_kind::clip);
        state->has_clip = true;
    }

    static void canvas_fill_rect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr || info.Length() < 4) return;
        ++state->node->canvas_fill_rect_calls;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(0);
        const auto y = info[1]->NumberValue(context).FromMaybe(0);
        const auto width = info[2]->NumberValue(context).FromMaybe(0);
        const auto height = info[3]->NumberValue(context).FromMaybe(0);
        const auto first = canvas_transform_point(*state, x, y);
        const auto second = canvas_transform_point(*state, x + width, y + height);
        const auto transformed_left = std::min(first.first, second.first);
        const auto transformed_top = std::min(first.second, second.second);
        const auto transformed_right = std::max(first.first, second.first);
        const auto transformed_bottom = std::max(first.second, second.second);
        const auto bitmap_width = element_dimension(*state->node, "width", 300);
        const auto bitmap_height = element_dimension(*state->node, "height", 150);
        const auto color = canvas_color(info, "fillStyle", 0x000000FFU);
        const auto composite = canvas_string_property(
            info,
            "globalCompositeOperation",
            "source-over");
        const auto saved_clip = std::any_of(
            state->stack.begin(),
            state->stack.end(),
            [](const canvas_snapshot& snapshot) { return snapshot.has_clip; });
        const auto covers_backing_store = transformed_left <= 0
            && transformed_top <= 0
            && transformed_right >= bitmap_width
            && transformed_bottom >= bitmap_height;
        const auto replaces_every_pixel = composite == "copy"
            || (composite == "source-over" && (color & 0xFFU) == 0xFFU);
        if (!state->has_clip && !saved_clip
            && covers_backing_store && replaces_every_pixel) {
            canvas_compact_full_overwrite(info, *state);
        }
        canvas_emit_paint_state(info, *state);
        canvas_append_command(
            *state->node,
            canvas_command_kind::fill_rect,
            {x, y, width, height});
        if (std::abs(second.second - bitmap_height) <= 2
            && std::abs(second.first - first.first) >= 1
            && std::abs(second.first - first.first) <= 12
            && std::abs(second.second - first.second) >= 8) {
            ++state->node->canvas_probable_volume_fill_rect_calls;
            ++state->node->canvas_probable_volume_by_generation[state->node->canvas_generation];
        }
        ++state->node->canvas_fill_rect_color_calls[color];
        if (composite == "copy") {
            std::erase_if(state->node->canvas_rects, [&](const auto& rect) {
                return rect.x < transformed_right
                    && rect.x + rect.width > transformed_left
                    && rect.y < transformed_bottom
                    && rect.y + rect.height > transformed_top;
            });
            std::erase_if(state->node->canvas_lines, [&](const auto& line) {
                const auto left = std::min(line.x1, line.x2);
                const auto right = std::max(line.x1, line.x2);
                const auto top = std::min(line.y1, line.y2);
                const auto bottom = std::max(line.y1, line.y2);
                return left < transformed_right && right > transformed_left
                    && top < transformed_bottom && bottom > transformed_top;
            });
        }
        append_canvas_rect(
            *state->node,
            transformed_left,
            transformed_top,
            transformed_right - transformed_left,
            transformed_bottom - transformed_top,
            color);
    }

    static void canvas_stroke_rect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr || info.Length() < 4) return;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(0);
        const auto y = info[1]->NumberValue(context).FromMaybe(0);
        const auto width = info[2]->NumberValue(context).FromMaybe(0);
        const auto height = info[3]->NumberValue(context).FromMaybe(0);
        canvas_emit_paint_state(info, *state);
        canvas_append_command(
            *state->node,
            canvas_command_kind::stroke_rect,
            {x, y, width, height});
        v8::Local<v8::Value> line_width_value;
        const auto line_width = info.This()->Get(context, js_string(info.GetIsolate(), "lineWidth")).ToLocal(&line_width_value)
            ? line_width_value->NumberValue(context).FromMaybe(1)
            : 1;
        const auto color = canvas_color(info, "strokeStyle", 0x000000FFU);
        const auto top_left = canvas_transform_point(*state, x, y);
        const auto bottom_right = canvas_transform_point(*state, x + width, y + height);
        const auto transformed_x = std::min(top_left.first, bottom_right.first);
        const auto transformed_y = std::min(top_left.second, bottom_right.second);
        const auto transformed_width = std::abs(bottom_right.first - top_left.first);
        const auto transformed_height = std::abs(bottom_right.second - top_left.second);
        append_canvas_rect(*state->node, transformed_x, transformed_y, transformed_width, line_width, color);
        append_canvas_rect(*state->node, transformed_x, transformed_y + transformed_height - line_width, transformed_width, line_width, color);
        append_canvas_rect(*state->node, transformed_x, transformed_y, line_width, transformed_height, color);
        append_canvas_rect(*state->node, transformed_x + transformed_width - line_width, transformed_y, line_width, transformed_height, color);
    }

    static void canvas_clear_rect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_draw));
        auto* state = canvas_path_state(info);
        if (state == nullptr || state->node == nullptr || info.Length() < 4) return;
        ++state->node->canvas_clear_rect_calls;
        auto* node = state->node;
        const auto context = info.GetIsolate()->GetCurrentContext();
        const auto x = info[0]->NumberValue(context).FromMaybe(0);
        const auto y = info[1]->NumberValue(context).FromMaybe(0);
        const auto width = info[2]->NumberValue(context).FromMaybe(0);
        const auto height = info[3]->NumberValue(context).FromMaybe(0);
        const auto first = canvas_transform_point(*state, x, y);
        const auto second = canvas_transform_point(*state, x + width, y + height);
        const auto clear_left = std::min(first.first, second.first);
        const auto clear_top = std::min(first.second, second.second);
        const auto clear_right = std::max(first.first, second.first);
        const auto clear_bottom = std::max(first.second, second.second);
        const auto bitmap_width = element_dimension(*node, "width", 300);
        const auto bitmap_height = element_dimension(*node, "height", 150);
        const auto covers_backing_store = clear_left <= 0 && clear_top <= 0
            && clear_right >= bitmap_width && clear_bottom >= bitmap_height;
        const auto saved_clip = std::any_of(
            state->stack.begin(),
            state->stack.end(),
            [](const canvas_snapshot& snapshot) { return snapshot.has_clip; });
        node->canvas_max_clear_stack_depth = std::max<uint64_t>(
            node->canvas_max_clear_stack_depth,
            state->stack.size());
        if (covers_backing_store) {
            ++node->canvas_full_clear_calls;
            if (state->has_clip) ++node->canvas_full_clear_current_clip_calls;
            if (saved_clip) ++node->canvas_full_clear_saved_clip_calls;
        } else {
            ++node->canvas_clear_bounds_rejected_calls;
        }
        if (!state->has_clip && !saved_clip && covers_backing_store) {
            ++node->canvas_full_clear_reset_calls;
            // Canvas libraries commonly wrap an entire paint pass in save()/restore().
            // A full backing-store clear still makes every earlier draw command dead,
            // even when that save is active. Rebuild the lightweight state stack so
            // subsequent restores retain browser semantics without retaining every
            // obsolete frame in the display list. A clipped stack is deliberately
            // excluded because the clip path itself cannot yet be reconstructed.
            canvas_compact_full_overwrite(info, *state);
            canvas_append_command(
                *node,
                canvas_command_kind::clear_rect,
                {x, y, width, height});
            return;
        }
        canvas_append_command(
            *node,
            canvas_command_kind::clear_rect,
            {x, y, width, height});
        std::erase_if(node->canvas_rects, [&](const auto& rect) {
            return rect.x < clear_right && rect.x + rect.width > clear_left
                && rect.y < clear_bottom && rect.y + rect.height > clear_top;
        });
        std::erase_if(node->canvas_lines, [&](const auto& line) {
            const auto left = std::min(line.x1, line.x2);
            const auto right = std::max(line.x1, line.x2);
            const auto top = std::min(line.y1, line.y2);
            const auto bottom = std::max(line.y1, line.y2);
            return left < clear_right && right > clear_left
                && top < clear_bottom && bottom > clear_top;
        });
    }

    static void canvas_true(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::False(info.GetIsolate()));
    }

    static void canvas_get_line_dash(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::Array::New(info.GetIsolate()));
    }

    static void canvas_measure_text(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        const auto text = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto result = v8::Object::New(info.GetIsolate());
        result->Set(
            info.GetIsolate()->GetCurrentContext(),
            js_string(info.GetIsolate(), "width"),
            v8::Number::New(info.GetIsolate(), static_cast<double>(text.size()) * 7.0)).Check();
        info.GetReturnValue().Set(result);
    }

    static void canvas_gradient(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto result = v8::Object::New(info.GetIsolate());
        result->Set(
            local_context,
            js_string(info.GetIsolate(), "addColorStop"),
            v8::Function::New(local_context, canvas_no_op).ToLocalChecked()).Check();
        info.GetReturnValue().Set(result);
    }

    static void canvas_image_data(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        const auto width = info.Length() > 0 ? info[0]->Int32Value(info.GetIsolate()->GetCurrentContext()).FromMaybe(1) : 1;
        const auto height = info.Length() > 1 ? info[1]->Int32Value(info.GetIsolate()->GetCurrentContext()).FromMaybe(1) : 1;
        const auto byte_count = static_cast<size_t>(std::max(1, width) * std::max(1, height) * 4);
        auto buffer = v8::ArrayBuffer::New(info.GetIsolate(), byte_count);
        auto result = v8::Object::New(info.GetIsolate());
        auto local_context = info.GetIsolate()->GetCurrentContext();
        result->Set(local_context, js_string(info.GetIsolate(), "width"), v8::Integer::New(info.GetIsolate(), width)).Check();
        result->Set(local_context, js_string(info.GetIsolate(), "height"), v8::Integer::New(info.GetIsolate(), height)).Check();
        result->Set(local_context, js_string(info.GetIsolate(), "data"), v8::Uint8ClampedArray::New(buffer, 0, byte_count)).Check();
        info.GetReturnValue().Set(result);
    }

    static void install_canvas_methods(
        v8::Isolate* isolate,
        v8::Local<v8::Context> local_context,
        v8::Local<v8::Object> result,
        canvas_state& state)
    {
        const char* no_ops[] = {
            "roundRect", "ellipse",
            "setLineDash", "putImageData"};
        for (const auto* name : no_ops) {
            result->Set(local_context, js_string(isolate, name), v8::Function::New(local_context, canvas_no_op).ToLocalChecked()).Check();
        }
        auto path_data = v8::External::New(isolate, &state, v8::kExternalPointerTypeTagDefault);
        result->Set(local_context, js_string(isolate, "fillText"), v8::Function::New(local_context, canvas_text_no_op, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "strokeText"), v8::Function::New(local_context, canvas_stroke_text_no_op, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "fillRect"), v8::Function::New(local_context, canvas_fill_rect, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "strokeRect"), v8::Function::New(local_context, canvas_stroke_rect, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "clearRect"), v8::Function::New(local_context, canvas_clear_rect, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "drawImage"), v8::Function::New(local_context, canvas_draw_image, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "save"), v8::Function::New(local_context, canvas_save, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "restore"), v8::Function::New(local_context, canvas_restore, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "scale"), v8::Function::New(local_context, canvas_scale, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "rotate"), v8::Function::New(local_context, canvas_rotate, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "translate"), v8::Function::New(local_context, canvas_translate, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "transform"), v8::Function::New(local_context, canvas_transform_method, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "setTransform"), v8::Function::New(local_context, canvas_set_transform, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "resetTransform"), v8::Function::New(local_context, canvas_reset_transform, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "beginPath"), v8::Function::New(local_context, canvas_begin_path, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "moveTo"), v8::Function::New(local_context, canvas_move_to, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "lineTo"), v8::Function::New(local_context, canvas_line_to, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "quadraticCurveTo"), v8::Function::New(local_context, canvas_quadratic_curve_to, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "bezierCurveTo"), v8::Function::New(local_context, canvas_bezier_curve_to, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "arc"), v8::Function::New(local_context, canvas_arc, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "arcTo"), v8::Function::New(local_context, canvas_arc_to, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "rect"), v8::Function::New(local_context, canvas_path_rect, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "closePath"), v8::Function::New(local_context, canvas_close_path, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "fill"), v8::Function::New(local_context, canvas_fill, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "stroke"), v8::Function::New(local_context, canvas_stroke, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "clip"), v8::Function::New(local_context, canvas_clip, path_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "measureText"), v8::Function::New(local_context, canvas_measure_text).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "createLinearGradient"), v8::Function::New(local_context, canvas_gradient).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "createRadialGradient"), v8::Function::New(local_context, canvas_gradient).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "createConicGradient"), v8::Function::New(local_context, canvas_gradient).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "getLineDash"), v8::Function::New(local_context, canvas_get_line_dash).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "isPointInPath"), v8::Function::New(local_context, canvas_true).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "isPointInStroke"), v8::Function::New(local_context, canvas_true).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "createImageData"), v8::Function::New(local_context, canvas_image_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "getImageData"), v8::Function::New(local_context, canvas_image_data).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "canvas"), v8::Null(isolate)).Check();
        result->Set(local_context, js_string(isolate, "font"), js_string(isolate, "10px sans-serif")).Check();
        result->Set(local_context, js_string(isolate, "fillStyle"), js_string(isolate, "#000000")).Check();
        result->Set(local_context, js_string(isolate, "strokeStyle"), js_string(isolate, "#000000")).Check();
        result->Set(local_context, js_string(isolate, "lineWidth"), v8::Number::New(isolate, 1)).Check();
        result->Set(local_context, js_string(isolate, "globalAlpha"), v8::Number::New(isolate, 1)).Check();
        result->Set(
            local_context,
            js_string(isolate, "globalCompositeOperation"),
            js_string(isolate, "source-over")).Check();
    }

    static void canvas_get_context(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::canvas_state));
        auto* node = unwrap_node(info.This());
        if (node == nullptr || node->tag != "canvas") {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
            return;
        }
        const auto key = self->wrapper_key(*node);
        const auto known = self->canvas_contexts.find(key);
        if (known != self->canvas_contexts.end()) {
            info.GetReturnValue().Set(known->second.Get(info.GetIsolate()));
            return;
        }
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto result = v8::Object::New(info.GetIsolate());
        auto state = std::make_unique<canvas_state>();
        state->node = node;
        auto* state_pointer = state.get();
        self->canvas_states[key] = std::move(state);
        install_canvas_methods(info.GetIsolate(), local_context, result, *state_pointer);
        result->Set(local_context, js_string(info.GetIsolate(), "canvas"), info.This()).Check();
        self->canvas_contexts[key].Reset(info.GetIsolate(), result);
        info.GetReturnValue().Set(result);
    }

    static void path_2d_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto result = info.IsConstructCall() ? info.This() : v8::Object::New(info.GetIsolate());
        auto local_context = info.GetIsolate()->GetCurrentContext();
        const char* methods[] = {
            "addPath", "closePath", "moveTo", "lineTo", "bezierCurveTo", "quadraticCurveTo",
            "arc", "arcTo", "ellipse", "rect", "roundRect"};
        for (const auto* name : methods) {
            result->Set(local_context, js_string(info.GetIsolate(), name), v8::Function::New(local_context, canvas_no_op).ToLocalChecked()).Check();
        }
        if (info.Length() > 0 && info[0]->IsString()) {
            result->Set(
                local_context,
                js_string(info.GetIsolate(), "__htmlmlSvgPathData"),
                info[0]).Check();
        }
        info.GetReturnValue().Set(result);
    }

    static void dom_parser_parse(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        const auto xml = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto& root = self->document.create_element("svg");
        const auto svg_open = xml.find("<svg");
        const auto svg_end = svg_open == std::string::npos ? std::string::npos : xml.find('>', svg_open);
        if (svg_end != std::string::npos) {
            const auto opening = xml.substr(svg_open, svg_end - svg_open + 1U);
            root.attributes["viewBox"] = extract_html_attribute(opening, "viewBox");
            root.attributes["width"] = extract_html_attribute(opening, "width");
            root.attributes["height"] = extract_html_attribute(opening, "height");
        }
        size_t cursor = svg_end == std::string::npos ? 0 : svg_end + 1U;
        while (cursor < xml.size()) {
            const auto open = xml.find('<', cursor);
            if (open == std::string::npos || open + 1U >= xml.size()) break;
            if (xml[open + 1U] == '/' || xml[open + 1U] == '!' || xml[open + 1U] == '?') {
                cursor = open + 2U;
                continue;
            }
            const auto end = xml.find('>', open + 1U);
            if (end == std::string::npos) break;
            auto name_end = open + 1U;
            while (name_end < end && !std::isspace(static_cast<unsigned char>(xml[name_end]))
                && xml[name_end] != '/') ++name_end;
            const auto tag = xml.substr(open + 1U, name_end - open - 1U);
            if (!tag.empty() && tag != "svg") {
                auto& child = self->document.create_element(tag);
                const auto opening = xml.substr(open, end - open + 1U);
                const char* attributes[] = {
                    "id", "class", "d", "fill", "stroke", "stroke-width", "viewBox", "transform",
                    "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry", "points", "style"};
                for (const auto* attribute : attributes) {
                    const auto value = extract_html_attribute(opening, attribute);
                    if (!value.empty()) child.attributes[attribute] = value;
                }
                child.id_attribute = child.attributes["id"];
                child.class_name = child.attributes["class"];
                self->document.append_child(root, child);
            }
            cursor = end + 1U;
        }
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto parsed = v8::Object::New(info.GetIsolate());
        auto data = v8::External::New(info.GetIsolate(), &root, v8::kExternalPointerTypeTagDefault);
        parsed->Set(local_context, js_string(info.GetIsolate(), "documentElement"), self->wrap_node(root)).Check();
        parsed->Set(
            local_context,
            js_string(info.GetIsolate(), "getElementsByTagName"),
            v8::Function::New(local_context, parsed_document_get_elements, data).ToLocalChecked()).Check();
        parsed->Set(
            local_context,
            js_string(info.GetIsolate(), "querySelectorAll"),
            v8::Function::New(local_context, parsed_document_query_selector_all, data).ToLocalChecked()).Check();
        parsed->Set(
            local_context,
            js_string(info.GetIsolate(), "querySelector"),
            v8::Function::New(local_context, parsed_document_query_selector, data).ToLocalChecked()).Check();
        info.GetReturnValue().Set(parsed);
    }

    static void parsed_document_get_elements(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* root = info.Data()->IsExternal()
            ? static_cast<dom_node*>(info.Data().As<v8::External>()->Value(v8::kExternalPointerTypeTagDefault))
            : nullptr;
        if (root == nullptr) return;
        const auto tag = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(*root, tag));
    }

    static void parsed_document_query_selector_all(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* root = info.Data()->IsExternal()
            ? static_cast<dom_node*>(info.Data().As<v8::External>()->Value(v8::kExternalPointerTypeTagDefault))
            : nullptr;
        if (root == nullptr) return;
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        info.GetReturnValue().Set(self->selector_results(*root, selector));
    }

    static void parsed_document_query_selector(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* root = info.Data()->IsExternal()
            ? static_cast<dom_node*>(info.Data().As<v8::External>()->Value(v8::kExternalPointerTypeTagDefault))
            : nullptr;
        if (root == nullptr) return;
        const auto selector = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto matches = self->query_selector_nodes(*root, selector, true);
        info.GetReturnValue().Set(matches.empty()
            ? v8::Local<v8::Value>(v8::Null(info.GetIsolate()))
            : v8::Local<v8::Value>(self->wrap_node(*matches.front())));
    }

    static void return_this(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(info.This());
    }

    static void dom_matrix_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto result = info.IsConstructCall() ? info.This() : v8::Object::New(isolate);
        const char* ones[] = {"a", "d", "m11", "m22", "m33", "m44"};
        for (const auto* name : ones) {
            result->Set(local_context, js_string(isolate, name), v8::Number::New(isolate, 1)).Check();
        }
        const char* zeros[] = {"b", "c", "e", "f", "m12", "m13", "m14", "m21", "m23", "m24", "m31", "m32", "m34", "m41", "m42", "m43"};
        for (const auto* name : zeros) {
            result->Set(local_context, js_string(isolate, name), v8::Number::New(isolate, 0)).Check();
        }
        result->Set(local_context, js_string(isolate, "is2D"), v8::True(isolate)).Check();
        result->Set(local_context, js_string(isolate, "isIdentity"), v8::True(isolate)).Check();
        const char* methods[] = {
            "multiply", "preMultiplySelf", "multiplySelf", "translate", "translateSelf", "scale", "scale3d",
            "scaleSelf", "rotate", "rotateAxisAngle", "rotateSelf", "skewX", "skewY", "invertSelf", "inverse"};
        for (const auto* name : methods) {
            result->Set(local_context, js_string(isolate, name), v8::Function::New(local_context, return_this).ToLocalChecked()).Check();
        }
        info.GetReturnValue().Set(result);
    }

    static void get_bounding_client_rect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.This());
        if (node == nullptr) return;
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto result = v8::Object::New(info.GetIsolate());
        const auto set = [&](const char* name, double value) {
            result->Set(local_context, js_string(info.GetIsolate(), name), v8::Number::New(info.GetIsolate(), value)).Check();
        };
        set("x", node->layout.x);
        set("y", node->layout.y);
        set("left", node->layout.x);
        set("top", node->layout.y);
        set("width", node->layout.width);
        set("height", node->layout.height);
        set("right", node->layout.x + node->layout.width);
        set("bottom", node->layout.y + node->layout.height);
        info.GetReturnValue().Set(result);
    }

    static void get_client_rects(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        self->ensure_layout();
        auto* node = unwrap_node(info.This());
        if (node == nullptr) return;
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto rect = v8::Object::New(info.GetIsolate());
        const auto set = [&](const char* name, double value) {
            rect->Set(local_context, js_string(info.GetIsolate(), name), v8::Number::New(info.GetIsolate(), value)).Check();
        };
        set("x", node->layout.x);
        set("y", node->layout.y);
        set("left", node->layout.x);
        set("top", node->layout.y);
        set("width", node->layout.width);
        set("height", node->layout.height);
        set("right", node->layout.x + node->layout.width);
        set("bottom", node->layout.y + node->layout.height);
        auto result = v8::Array::New(info.GetIsolate(), 1);
        result->Set(local_context, 0, rect).Check();
        info.GetReturnValue().Set(result);
    }

    static void set_attribute(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 2) return;
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        const auto value = to_utf8(info.GetIsolate(), info[1]);
        node->attributes[name] = value;
        if (name == "id") node->id_attribute = value;
        else if (name == "class") node->class_name = value;
        self->recascade_connected_subtree(*node);
    }

    static void set_attribute_ns(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 3) return;
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), info[1]);
        const auto value = to_utf8(info.GetIsolate(), info[2]);
        node->attributes[name] = value;
        if (name == "id") node->id_attribute = value;
        else if (name == "class") node->class_name = value;
        self->recascade_connected_subtree(*node);
    }

    static void remove_attribute(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 1) return;
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        node->attributes.erase(name);
        if (name == "id") node->id_attribute.clear();
        else if (name == "class") node->class_name.clear();
        self->recascade_connected_subtree(*node);
    }

    static void get_attribute(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 1) return;
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        const auto match = node->attributes.find(name);
        if (match == node->attributes.end()) {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
        } else {
            info.GetReturnValue().Set(js_string(info.GetIsolate(), match->second.c_str()));
        }
    }

    static void has_attribute(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        auto* node = unwrap_node(info.This());
        const auto name = node != nullptr && info.Length() > 0
            ? to_utf8(info.GetIsolate(), info[0])
            : std::string{};
        info.GetReturnValue().Set(v8::Boolean::New(
            info.GetIsolate(),
            node != nullptr && node->attributes.contains(name)));
    }

    static std::string extract_html_attribute(const std::string& html, const std::string& name)
    {
        const auto marker = name + "=";
        const auto position = html.find(marker);
        if (position == std::string::npos) return {};
        auto value_start = position + marker.size();
        if (value_start >= html.size()) return {};
        const auto quote = html[value_start];
        if (quote != '\'' && quote != '"') return {};
        ++value_start;
        const auto value_end = html.find(quote, value_start);
        return value_end == std::string::npos
            ? std::string{}
            : html.substr(value_start, value_end - value_start);
    }

    static std::vector<frame_script> parse_frame_scripts(const std::string& html)
    {
        std::vector<frame_script> result;
        size_t cursor = 0;
        size_t index = 0;
        while (true) {
            const auto open = html.find("<script", cursor);
            if (open == std::string::npos) break;
            const auto open_end = html.find('>', open);
            if (open_end == std::string::npos) break;
            const auto close = html.find("</script>", open_end + 1U);
            if (close == std::string::npos) break;
            const auto attributes = html.substr(open, open_end - open + 1U);
            result.push_back(frame_script{
                extract_html_attribute(attributes, "src"),
                html.substr(open_end + 1U, close - open_end - 1U),
                attributes.find("defer") != std::string::npos,
                index++});
            cursor = close + 9U;
        }
        return result;
    }

    static bool read_text_file(const std::filesystem::path& path, std::string& result)
    {
        std::ifstream stream(path, std::ios::binary | std::ios::ate);
        if (!stream) return false;
        const auto end = stream.tellg();
        if (end < 0) return false;
        result.resize(static_cast<size_t>(end));
        stream.seekg(0, std::ios::beg);
        if (!result.empty()) {
            stream.read(result.data(), static_cast<std::streamsize>(result.size()));
        }
        return stream.good() || stream.eof();
    }

    bool profiled_read_text_file(const std::filesystem::path& path, std::string& result)
    {
        binding_callback_timer timer(profile_startup ? &startup_resource_read : nullptr);
        return read_text_file(path, result);
    }

    static std::string_view trim_css_view(std::string_view value)
    {
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front()))) value.remove_prefix(1U);
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back()))) value.remove_suffix(1U);
        return value;
    }

    static std::string trim_css(std::string_view value)
    {
        return std::string(trim_css_view(value));
    }

    static size_t matching_css_brace(const std::string& css, size_t open, size_t end)
    {
        size_t depth = 1U;
        char quote = 0;
        for (size_t index = open + 1U; index < end; ++index) {
            const auto character = css[index];
            if (quote != 0) {
                if (character == quote && (index == 0 || css[index - 1U] != '\\')) quote = 0;
                continue;
            }
            if (character == '\'' || character == '"') quote = character;
            else if (character == '{') ++depth;
            else if (character == '}' && --depth == 0U) return index;
        }
        return std::string::npos;
    }

    static std::vector<css_declaration> parse_css_declarations(std::string_view body)
    {
        std::vector<css_declaration> result;
        size_t cursor = 0;
        while (cursor < body.size()) {
            const auto separator = body.find(':', cursor);
            if (separator == std::string_view::npos) break;
            auto end = body.find(';', separator + 1U);
            if (end == std::string_view::npos) end = body.size();
            auto name = trim_css(body.substr(cursor, separator - cursor));
            auto value = trim_css(body.substr(separator + 1U, end - separator - 1U));
            const auto important_marker = value.find("!important");
            const auto important = important_marker != std::string::npos;
            if (important) value = trim_css(std::string_view(value).substr(0, important_marker));
            if (!name.empty() && !value.empty()) {
                result.push_back({std::move(name), std::move(value), important});
            }
            cursor = end + 1U;
        }
        return result;
    }

    void index_css_rule(size_t index)
    {
        const auto& selector = css_rules[index].selector;
        size_t compound_begin = 0;
        int bracket_depth = 0;
        int parenthesis_depth = 0;
        for (size_t offset = selector.size(); offset > 0; --offset) {
            const auto position = offset - 1U;
            const auto character = selector[position];
            if (character == ']') ++bracket_depth;
            else if (character == '[') --bracket_depth;
            else if (character == ')') ++parenthesis_depth;
            else if (character == '(') --parenthesis_depth;
            else if (bracket_depth == 0 && parenthesis_depth == 0
                && (std::isspace(static_cast<unsigned char>(character))
                    || character == '>' || character == '+' || character == '~')) {
                compound_begin = position + 1U;
                break;
            }
        }
        auto compound = std::string_view(selector).substr(compound_begin);
        while (!compound.empty()
            && std::isspace(static_cast<unsigned char>(compound.front()))) {
            compound.remove_prefix(1U);
        }

        size_t pseudo = compound.size();
        bracket_depth = 0;
        for (size_t position = 0; position < compound.size(); ++position) {
            if (compound[position] == '[') ++bracket_depth;
            else if (compound[position] == ']') --bracket_depth;
            else if (compound[position] == ':' && bracket_depth == 0) {
                pseudo = position;
                break;
            }
        }
        compound = compound.substr(0, pseudo);

        const auto token_end = [&](size_t start) {
            auto end = start;
            while (end < compound.size()
                && compound[end] != '.' && compound[end] != '#'
                && compound[end] != '[' && compound[end] != ':') ++end;
            return end;
        };
        bracket_depth = 0;
        for (size_t position = 0; position < compound.size(); ++position) {
            if (compound[position] == '[') ++bracket_depth;
            else if (compound[position] == ']') --bracket_depth;
            else if (bracket_depth == 0
                && (compound[position] == '#' || compound[position] == '.')) {
                const auto end = token_end(position + 1U);
                const auto key = std::string(compound.substr(position + 1U, end - position - 1U));
                if (!key.empty()) {
                    auto& target = compound[position] == '#'
                        ? css_rules_by_id[key]
                        : css_rules_by_class[key];
                    target.push_back(index);
                    return;
                }
            }
        }
        if (!compound.empty() && std::isalpha(static_cast<unsigned char>(compound.front()))) {
            size_t end = 1U;
            while (end < compound.size()
                && (std::isalnum(static_cast<unsigned char>(compound[end]))
                    || compound[end] == '-')) ++end;
            css_rules_by_tag[std::string(compound.substr(0, end))].push_back(index);
            return;
        }
        unindexed_css_rules.push_back(index);
    }

    void append_css_rule(std::string selector, const std::vector<css_declaration>& declarations)
    {
        const auto index = css_rules.size();
        css_rules.push_back({std::move(selector), declarations});
        index_css_rule(index);
    }

    void parse_css_rules(const std::string& css, size_t begin, size_t end)
    {
        size_t cursor = begin;
        while (cursor < end) {
            const auto open = css.find('{', cursor);
            if (open == std::string::npos || open >= end) break;
            const auto close = matching_css_brace(css, open, end);
            if (close == std::string::npos) break;
            const auto prelude = trim_css(std::string_view(css).substr(cursor, open - cursor));
            if (prelude.starts_with("@media") || prelude.starts_with("@supports")
                || prelude.starts_with("@layer") || prelude.starts_with("@container")) {
                parse_css_rules(css, open + 1U, close);
            } else if (!prelude.empty() && prelude.front() != '@') {
                auto declarations = parse_css_declarations(
                    std::string_view(css).substr(open + 1U, close - open - 1U));
                size_t selector_cursor = 0;
                while (selector_cursor < prelude.size()) {
                    auto separator = prelude.find(',', selector_cursor);
                    if (separator == std::string::npos) separator = prelude.size();
                    auto selector = trim_css(std::string_view(prelude).substr(
                        selector_cursor,
                        separator - selector_cursor));
                    if (!selector.empty()) append_css_rule(std::move(selector), declarations);
                    selector_cursor = separator + 1U;
                }
            }
            cursor = close + 1U;
        }
    }

    size_t add_stylesheet(std::string css)
    {
        binding_callback_timer timer(profile_startup ? &startup_css_parse : nullptr);
        const auto first_new_rule = css_rules.size();
        size_t comment = 0;
        while ((comment = css.find("/*", comment)) != std::string::npos) {
            const auto end = css.find("*/", comment + 2U);
            if (end == std::string::npos) {
                css.erase(comment);
                break;
            }
            css.erase(comment, end - comment + 2U);
        }
        parse_css_rules(css, 0, css.size());
        for (auto index = first_new_rule; index < css_rules.size(); ++index) {
            const auto& rule = css_rules[index];
            if (rule.selector != ":root" && rule.selector != "html") continue;
            for (const auto& declaration : rule.declarations) {
                if (declaration.name.starts_with("--")) css_variables[declaration.name] = declaration.value;
            }
        }
        return first_new_rule;
    }

    std::string resolve_css_value(const dom_node& node, std::string value) const
    {
        // Component-library dimensions frequently expand through several
        // custom-property layers and then repeat those variables across five
        // size terms. A fixed substitution limit truncated large component stylesheets'
        // button height before it reached a numeric calc() expression.
        for (int depth = 0; depth < 128; ++depth) {
            const auto start = value.find("var(");
            if (start == std::string::npos) break;
            size_t close = std::string::npos;
            int parenthesis_depth = 1;
            for (size_t index = start + 4U; index < value.size(); ++index) {
                if (value[index] == '(') ++parenthesis_depth;
                else if (value[index] == ')' && --parenthesis_depth == 0) {
                    close = index;
                    break;
                }
            }
            if (close == std::string::npos) break;
            const auto content = value.substr(start + 4U, close - start - 4U);
            size_t comma = std::string::npos;
            parenthesis_depth = 0;
            for (size_t index = 0; index < content.size(); ++index) {
                if (content[index] == '(') ++parenthesis_depth;
                else if (content[index] == ')') --parenthesis_depth;
                else if (content[index] == ',' && parenthesis_depth == 0) {
                    comma = index;
                    break;
                }
            }
            const auto name = trim_css(std::string_view(content).substr(0, comma));
            const std::string* known_value = nullptr;
            for (auto* current = &node; current != nullptr; current = current->parent) {
                const auto known = current->style.custom_properties.find(name);
                if (known != current->style.custom_properties.end()) {
                    known_value = &known->second;
                    break;
                }
            }
            if (known_value == nullptr) {
                const auto known = css_variables.find(name);
                if (known != css_variables.end()) known_value = &known->second;
            }
            const auto replacement = known_value != nullptr
                ? *known_value
                : comma == std::string::npos ? std::string{} : trim_css(std::string_view(content).substr(comma + 1U));
            value.replace(start, close - start + 1U, replacement);
        }
        return trim_css(value);
    }

    bool css_compound_selector_matches(const dom_node& node, std::string_view selector) const
    {
        selector = trim_css_view(selector);
        if (selector.empty()) return false;
        // The probe intentionally collapses each browsing context's HTML and
        // BODY boxes into a single native root node.  Browser CSS still targets
        // that box with selectors such as `html.theme-dark`, so let the virtual
        // root participate as both tags.  A frame document is parented beneath
        // its owning iframe in the unified native tree.
        const auto is_document_root = node.tag == "body"
            && (node.parent == nullptr || node.parent->tag == "iframe");
        if (selector == ":root") return is_document_root;
        // A pseudo-element is a different CSS box, not the originating DOM
        // element.  Treating it as the element makes rules such as
        // `.scroller::-webkit-scrollbar { display: none }` hide the scroller
        // itself, which is catastrophic for toolbar layout.
        if (selector.find("::") != std::string::npos
            || selector.find(":before") != std::string::npos
            || selector.find(":after") != std::string::npos
            || selector.find(":first-letter") != std::string::npos
            || selector.find(":first-line") != std::string::npos) {
            return false;
        }

        size_t pseudo = std::string::npos;
        int bracket_depth = 0;
        for (size_t index = 0; index < selector.size(); ++index) {
            if (selector[index] == '[') ++bracket_depth;
            else if (selector[index] == ']') --bracket_depth;
            else if (selector[index] == ':' && bracket_depth == 0) {
                pseudo = index;
                break;
            }
        }
        const auto pseudo_suffix = pseudo == std::string::npos
            ? std::string_view{}
            : selector.substr(pseudo);
        if (pseudo != std::string::npos) selector = selector.substr(0, pseudo);

        size_t cursor = 0;
        if (selector.empty()) {
            // A bare structural pseudo-class has an implicit universal
            // selector.
        } else if (selector[cursor] == '*') {
            ++cursor;
        } else if (std::isalpha(static_cast<unsigned char>(selector[cursor]))) {
            const auto start = cursor;
            while (cursor < selector.size()
                && (std::isalnum(static_cast<unsigned char>(selector[cursor])) || selector[cursor] == '-')) ++cursor;
            const auto wanted_tag = selector.substr(start, cursor - start);
            if (wanted_tag != node.tag && !(wanted_tag == "html" && is_document_root)) return false;
        }
        while (cursor < selector.size()) {
            const auto marker = selector[cursor++];
            if (marker == '.' || marker == '#') {
                const auto start = cursor;
                while (cursor < selector.size() && selector[cursor] != '.' && selector[cursor] != '#'
                    && selector[cursor] != '[' && selector[cursor] != ':') ++cursor;
                const auto wanted = selector.substr(start, cursor - start);
                if (marker == '#') {
                    if (node.id_attribute != wanted) return false;
                } else if (!has_class(node, wanted)) {
                    return false;
                }
            } else if (marker == '[') {
                const auto close = selector.find(']', cursor);
                if (close == std::string::npos) return false;
                const auto condition = selector.substr(cursor, close - cursor);
                const auto equal = condition.find('=');
                const auto name = trim_css(std::string_view(condition).substr(0, equal));
                const auto attribute = node.attributes.find(name);
                if (attribute == node.attributes.end()) return false;
                if (equal != std::string::npos) {
                    auto wanted = trim_css(std::string_view(condition).substr(equal + 1U));
                    if (wanted.size() >= 2U && (wanted.front() == '\'' || wanted.front() == '"')) {
                        wanted = wanted.substr(1U, wanted.size() - 2U);
                    }
                    if (attribute->second != wanted) return false;
                }
                cursor = close + 1U;
            } else {
                break;
            }
        }

        cursor = 0;
        while (cursor < pseudo_suffix.size()) {
            if (pseudo_suffix[cursor] != ':') return false;
            const auto name_start = ++cursor;
            while (cursor < pseudo_suffix.size()
                && (std::isalnum(static_cast<unsigned char>(pseudo_suffix[cursor]))
                    || pseudo_suffix[cursor] == '-')) ++cursor;
            const auto name = pseudo_suffix.substr(name_start, cursor - name_start);
            std::string_view argument;
            if (cursor < pseudo_suffix.size() && pseudo_suffix[cursor] == '(') {
                const auto argument_start = ++cursor;
                int depth = 1;
                while (cursor < pseudo_suffix.size() && depth > 0) {
                    if (pseudo_suffix[cursor] == '(') ++depth;
                    else if (pseudo_suffix[cursor] == ')') --depth;
                    if (depth > 0) ++cursor;
                }
                if (depth != 0) return false;
                argument = pseudo_suffix.substr(argument_start, cursor - argument_start);
                ++cursor;
            }

            const auto is_element = [](const dom_node* candidate) {
                return candidate != nullptr && !candidate->tag.starts_with('#');
            };
            const auto form_control = node.tag == "button" || node.tag == "input"
                || node.tag == "select" || node.tag == "textarea"
                || node.tag == "option" || node.tag == "optgroup" || node.tag == "fieldset";
            const auto element_siblings = [&] {
                std::vector<const dom_node*> result;
                if (node.parent == nullptr) return result;
                for (const auto* child : node.parent->children) {
                    if (is_element(child)) result.push_back(child);
                }
                return result;
            }();
            const auto position = std::find(element_siblings.begin(), element_siblings.end(), &node);
            if (name == "first-child") {
                if (node.parent == nullptr || position != element_siblings.begin()) return false;
            } else if (name == "last-child") {
                if (node.parent == nullptr || position == element_siblings.end()
                    || position + 1 != element_siblings.end()) return false;
            } else if (name == "only-child") {
                if (node.parent == nullptr || element_siblings.size() != 1U) return false;
            } else if (name == "first-of-type" || name == "last-of-type"
                || name == "only-of-type") {
                if (node.parent == nullptr) return false;
                std::vector<const dom_node*> same_type;
                for (const auto* sibling : element_siblings) {
                    if (sibling->tag == node.tag) same_type.push_back(sibling);
                }
                const auto same_position = std::find(same_type.begin(), same_type.end(), &node);
                if (name == "first-of-type" && same_position != same_type.begin()) return false;
                if (name == "last-of-type"
                    && (same_position == same_type.end() || same_position + 1 != same_type.end())) return false;
                if (name == "only-of-type" && same_type.size() != 1U) return false;
            } else if (name == "empty") {
                const auto has_text = std::any_of(
                    node.text_content.begin(),
                    node.text_content.end(),
                    [](unsigned char character) { return !std::isspace(character); });
                if (!node.children.empty() || has_text) return false;
            } else if (name == "enabled") {
                if (!form_control || node.attributes.contains("disabled")) return false;
            } else if (name == "disabled") {
                if (!form_control || !node.attributes.contains("disabled")) return false;
            } else if (name == "checked") {
                const auto input_type = node.attributes.find("type");
                const auto checkable_input = node.tag == "input"
                    && input_type != node.attributes.end()
                    && (input_type->second == "checkbox" || input_type->second == "radio");
                const auto selected_option = node.tag == "option"
                    && node.attributes.contains("selected");
                if ((!checkable_input || !node.attributes.contains("checked"))
                    && !selected_option) return false;
            } else if (name == "hover") {
                auto* hovered = hover_target;
                bool matches_hover = false;
                for (; hovered != nullptr; hovered = hovered->parent) {
                    if (hovered == &node) {
                        matches_hover = true;
                        break;
                    }
                }
                if (!matches_hover) return false;
            } else if (name == "focus") {
                if (active_element != &node) return false;
            } else if (name == "not") {
                size_t start = 0;
                while (start <= argument.size()) {
                    auto end = argument.find(',', start);
                    if (end == std::string::npos) end = argument.size();
                    if (css_compound_selector_matches(
                            node,
                            trim_css_view(argument.substr(start, end - start)))) {
                        return false;
                    }
                    if (end == argument.size()) break;
                    start = end + 1U;
                }
            } else if (name == "is" || name == "where") {
                bool any = false;
                size_t start = 0;
                while (start <= argument.size()) {
                    auto end = argument.find(',', start);
                    if (end == std::string::npos) end = argument.size();
                    any = any || css_compound_selector_matches(
                        node,
                        trim_css_view(argument.substr(start, end - start)));
                    if (end == argument.size()) break;
                    start = end + 1U;
                }
                if (!any) return false;
            } else {
                // Stateful and vendor pseudo-classes are not active unless the
                // native DOM explicitly models that state.  Matching them as
                // the base element applies hover/focus/active CSS constantly.
                return false;
            }
        }
        return true;
    }

    struct css_selector_split final {
        std::string_view left;
        std::string_view right;
        char combinator{' '};
        bool found{false};
    };

    static css_selector_split split_css_selector(std::string_view selector)
    {
        selector = trim_css_view(selector);
        int bracket_depth = 0;
        int parenthesis_depth = 0;
        for (size_t offset = selector.size(); offset > 0; --offset) {
            const auto index = offset - 1U;
            const auto value = selector[index];
            if (value == ']') {
                ++bracket_depth;
                continue;
            }
            if (value == '[') {
                --bracket_depth;
                continue;
            }
            if (value == ')') {
                ++parenthesis_depth;
                continue;
            }
            if (value == '(') {
                --parenthesis_depth;
                continue;
            }
            if (bracket_depth != 0 || parenthesis_depth != 0) continue;
            if (value == '>' || value == '+' || value == '~') {
                return {
                    trim_css_view(selector.substr(0, index)),
                    trim_css_view(selector.substr(index + 1U)),
                    value,
                    true};
            }
            if (!std::isspace(static_cast<unsigned char>(value))) continue;
            auto before = index;
            while (before > 0
                && std::isspace(static_cast<unsigned char>(selector[before - 1U]))) {
                --before;
            }
            if (before > 0) {
                const auto explicit_combinator = selector[before - 1U];
                if (explicit_combinator == '>' || explicit_combinator == '+'
                    || explicit_combinator == '~') {
                    return {
                        trim_css_view(selector.substr(0, before - 1U)),
                        trim_css_view(selector.substr(before)),
                        explicit_combinator,
                        true};
                }
            }
            return {
                trim_css_view(selector.substr(0, before)),
                trim_css_view(selector.substr(index + 1U)),
                ' ',
                true};
        }
        return {{}, selector, ' ', false};
    }

    static const dom_node* previous_sibling(const dom_node& node)
    {
        if (node.parent == nullptr) return nullptr;
        const auto position = std::find(
            node.parent->children.begin(),
            node.parent->children.end(),
            &node);
        return position == node.parent->children.begin()
                || position == node.parent->children.end()
            ? nullptr
            : *(position - 1);
    }

    bool css_selector_matches(const dom_node& node, std::string_view selector) const
    {
        const auto split = split_css_selector(selector);
        if (!css_compound_selector_matches(node, split.right)) return false;
        if (!split.found || split.left.empty()) return !split.found;

        if (split.combinator == '>') {
            return node.parent != nullptr
                && css_selector_matches(*node.parent, split.left);
        }
        if (split.combinator == '+') {
            const auto* sibling = previous_sibling(node);
            return sibling != nullptr && css_selector_matches(*sibling, split.left);
        }
        if (split.combinator == '~') {
            for (auto* sibling = previous_sibling(node); sibling != nullptr;
                sibling = previous_sibling(*sibling)) {
                if (css_selector_matches(*sibling, split.left)) return true;
            }
            return false;
        }
        for (auto* ancestor = node.parent; ancestor != nullptr; ancestor = ancestor->parent) {
            if (css_selector_matches(*ancestor, split.left)) return true;
        }
        return false;
    }

    bool css_selector_list_matches(const dom_node& node, std::string_view selector) const
    {
        size_t start = 0;
        int bracket_depth = 0;
        int parenthesis_depth = 0;
        char quote = 0;
        for (size_t index = 0; index <= selector.size(); ++index) {
            const auto at_end = index == selector.size();
            const auto character = at_end ? ',' : selector[index];
            if (!at_end && quote != 0) {
                if (character == quote && (index == 0 || selector[index - 1U] != '\\')) quote = 0;
                continue;
            }
            if (!at_end && (character == '\'' || character == '"')) {
                quote = character;
                continue;
            }
            if (!at_end && character == '[') ++bracket_depth;
            else if (!at_end && character == ']') --bracket_depth;
            else if (!at_end && character == '(') ++parenthesis_depth;
            else if (!at_end && character == ')') --parenthesis_depth;
            else if (character == ',' && bracket_depth == 0 && parenthesis_depth == 0) {
                if (css_selector_matches(node, selector.substr(start, index - start))) return true;
                start = index + 1U;
            }
        }
        return false;
    }

    static int split_pseudo_element_selector(const std::string& selector, std::string& origin)
    {
        const auto split_suffix = [&](std::string_view suffix, int kind) {
            if (!selector.ends_with(suffix)) return 0;
            origin = trim_css(std::string_view(selector).substr(0, selector.size() - suffix.size()));
            return kind;
        };
        if (const auto kind = split_suffix("::before", 1); kind != 0) return kind;
        if (const auto kind = split_suffix("::after", 2); kind != 0) return kind;
        if (const auto kind = split_suffix(":before", 1); kind != 0) return kind;
        return split_suffix(":after", 2);
    }

    static void append_utf8_codepoint(std::string& result, uint32_t codepoint)
    {
        if (codepoint <= 0x7FU) {
            result.push_back(static_cast<char>(codepoint));
        } else if (codepoint <= 0x7FFU) {
            result.push_back(static_cast<char>(0xC0U | (codepoint >> 6U)));
            result.push_back(static_cast<char>(0x80U | (codepoint & 0x3FU)));
        } else if (codepoint <= 0xFFFFU) {
            result.push_back(static_cast<char>(0xE0U | (codepoint >> 12U)));
            result.push_back(static_cast<char>(0x80U | ((codepoint >> 6U) & 0x3FU)));
            result.push_back(static_cast<char>(0x80U | (codepoint & 0x3FU)));
        } else {
            result.push_back(static_cast<char>(0xF0U | (codepoint >> 18U)));
            result.push_back(static_cast<char>(0x80U | ((codepoint >> 12U) & 0x3FU)));
            result.push_back(static_cast<char>(0x80U | ((codepoint >> 6U) & 0x3FU)));
            result.push_back(static_cast<char>(0x80U | (codepoint & 0x3FU)));
        }
    }

    static std::string decode_css_content(std::string value)
    {
        if (value == "none" || value == "normal") return {};
        if (value.size() >= 2U
            && (value.front() == '\'' || value.front() == '"')
            && value.back() == value.front()) {
            value = value.substr(1U, value.size() - 2U);
        }
        std::string result;
        for (size_t index = 0; index < value.size(); ++index) {
            if (value[index] != '\\' || index + 1U >= value.size()) {
                result.push_back(value[index]);
                continue;
            }
            size_t end = index + 1U;
            while (end < value.size() && end - index <= 6U
                && std::isxdigit(static_cast<unsigned char>(value[end]))) ++end;
            if (end > index + 1U) {
                const auto codepoint = static_cast<uint32_t>(std::strtoul(
                    value.substr(index + 1U, end - index - 1U).c_str(), nullptr, 16));
                append_utf8_codepoint(result, codepoint);
                if (end < value.size() && std::isspace(static_cast<unsigned char>(value[end]))) ++end;
                index = end - 1U;
            } else {
                result.push_back(value[++index]);
            }
        }
        return result;
    }

    static bool apply_corner_radius_declaration(
        std::string_view name,
        const std::string& value,
        css_length& top_left,
        css_length& top_right,
        css_length& bottom_right,
        css_length& bottom_left)
    {
        const auto assign = [&](css_length& target) {
            target = native_document::parse_length(value);
        };
        if (name == "border-top-left-radius" || name == "border-start-start-radius") {
            assign(top_left);
            return true;
        }
        if (name == "border-top-right-radius" || name == "border-start-end-radius") {
            assign(top_right);
            return true;
        }
        if (name == "border-bottom-right-radius" || name == "border-end-end-radius") {
            assign(bottom_right);
            return true;
        }
        if (name == "border-bottom-left-radius" || name == "border-end-start-radius") {
            assign(bottom_left);
            return true;
        }
        if (name != "border-radius") return false;

        std::vector<css_length> radii;
        size_t token_start = std::string::npos;
        int parenthesis_depth = 0;
        for (size_t index = 0; index <= value.size(); ++index) {
            const auto character = index < value.size() ? value[index] : ' ';
            if (character == '(') ++parenthesis_depth;
            else if (character == ')' && parenthesis_depth > 0) --parenthesis_depth;
            const auto separator = parenthesis_depth == 0
                && (std::isspace(static_cast<unsigned char>(character)) || character == '/');
            if (!separator && token_start == std::string::npos) token_start = index;
            if (separator && token_start != std::string::npos) {
                radii.push_back(native_document::parse_length(
                    value.substr(token_start, index - token_start)));
                token_start = std::string::npos;
                if (radii.size() == 4U) break;
            }
            // Elliptical vertical radii follow the top-level slash. The scene
            // ABI currently carries one radius per corner, so retain the
            // horizontal radii instead of accidentally tokenizing the calc()
            // expression at its internal whitespace.
            if (character == '/' && parenthesis_depth == 0) break;
        }
        if (radii.empty()) return true;
        top_left = radii[0];
        top_right = radii.size() > 1U ? radii[1] : radii[0];
        bottom_right = radii.size() > 2U ? radii[2] : radii[0];
        bottom_left = radii.size() > 3U ? radii[3]
            : radii.size() > 1U ? radii[1] : radii[0];
        return true;
    }

    void apply_pseudo_css_declaration(
        dom_node& node,
        node_style::pseudo_element& pseudo,
        const css_declaration& declaration)
    {
        const auto value = resolve_css_value(node, declaration.value);
        if (value.empty() && declaration.value.find("var(") != std::string::npos) return;
        const auto& name = declaration.name;
        if (name == "content") {
            pseudo.generated = value != "none" && value != "normal";
            pseudo.content = decode_css_content(value);
        } else if (name == "display") {
            pseudo.display_none = value == "none";
        } else if (name == "visibility") {
            pseudo.visibility_hidden = value == "hidden" || value == "collapse";
        } else if (name == "position") {
            pseudo.position = value == "absolute" ? position_mode::absolute
                : value == "fixed" ? position_mode::fixed
                : value == "relative" ? position_mode::relative
                : position_mode::normal;
        } else if (name == "inset") {
            const auto length = native_document::parse_length(value);
            pseudo.left = length;
            pseudo.top = length;
            pseudo.right = length;
            pseudo.bottom = length;
        } else if (name == "width") pseudo.width = native_document::parse_length(value);
        else if (name == "height") pseudo.height = native_document::parse_length(value);
        else if (name == "left" || name == "inset-inline-start") {
            pseudo.left = native_document::parse_length(value);
        } else if (name == "right" || name == "inset-inline-end") {
            pseudo.right = native_document::parse_length(value);
        } else if (name == "top" || name == "inset-block-start") {
            pseudo.top = native_document::parse_length(value);
        } else if (name == "bottom" || name == "inset-block-end") {
            pseudo.bottom = native_document::parse_length(value);
        } else if (name == "margin") {
            std::vector<css_length> values;
            std::istringstream stream(value);
            for (std::string token; stream >> token;) {
                values.push_back(native_document::parse_length(token));
            }
            if (!values.empty()) {
                pseudo.margin_top = values[0];
                pseudo.margin_right = values.size() > 1 ? values[1] : values[0];
                pseudo.margin_bottom = values.size() > 2 ? values[2] : values[0];
                pseudo.margin_left = values.size() > 3 ? values[3]
                    : values.size() > 1 ? values[1] : values[0];
            }
        } else if (name == "margin-left" || name == "margin-inline-start") {
            pseudo.margin_left = native_document::parse_length(value);
        } else if (name == "margin-right" || name == "margin-inline-end") {
            pseudo.margin_right = native_document::parse_length(value);
        } else if (name == "margin-top" || name == "margin-block-start") {
            pseudo.margin_top = native_document::parse_length(value);
        } else if (name == "margin-bottom" || name == "margin-block-end") {
            pseudo.margin_bottom = native_document::parse_length(value);
        } else if (apply_corner_radius_declaration(
            name,
            value,
            pseudo.border_top_left_radius,
            pseudo.border_top_right_radius,
            pseudo.border_bottom_right_radius,
            pseudo.border_bottom_left_radius)) {
        } else if (name == "background" || name == "background-color") {
            pseudo.background_rgba = native_document::parse_color(value);
        } else if (name == "color") {
            pseudo.foreground_rgba = native_document::parse_color(value);
        } else if (name == "font-size") {
            pseudo.font_size = std::max(0.0F, native_document::parse_length(value).value);
        } else if (name == "line-height" && value != "inherit" && value != "normal") {
            pseudo.line_height = std::max(0.0F, native_document::parse_length(value).value);
        }
    }

    void apply_css_declaration(dom_node& node, const css_declaration& declaration)
    {
        const auto& name = declaration.name;
        if (name.starts_with("--")) {
            node.style.custom_properties[name] = declaration.value;
            return;
        }
        const auto value = resolve_css_value(node, declaration.value);
        // An unresolved var() without a fallback invalidates the declaration;
        // it does not become numeric zero or an empty keyword.
        if (value.empty() && declaration.value.find("var(") != std::string::npos) return;
        const auto property_mask = inline_style_mask(name);
        if (declaration.important && property_mask != 0U) {
            node.style.important_property_mask |= property_mask;
        }
        const auto is_inline = [&](uint64_t property) {
            return !declaration.important
                && ((node.style.inline_property_mask | node.style.important_property_mask)
                    & property) != 0U;
        };
        const auto parse_border = [&](css_length& width, uint32_t& color) {
            std::istringstream stream(value);
            for (std::string token; stream >> token;) {
                if (token == "none") {
                    width = {};
                    color = 0;
                } else if (token == "thin") {
                    width = {1, length_unit::pixels};
                } else if (token == "medium") {
                    width = {3, length_unit::pixels};
                } else if (token == "thick") {
                    width = {5, length_unit::pixels};
                } else if (std::isdigit(static_cast<unsigned char>(token.front()))
                    || token.front() == '.' || token.front() == '-') {
                    width = native_document::parse_length(token);
                } else {
                    const auto parsed = native_document::parse_color(token);
                    if (parsed != 0U || token == "transparent") color = parsed;
                }
            }
        };
        const auto parse_four_border_values = [&](bool colors) {
            std::vector<std::string> values;
            std::istringstream stream(value);
            for (std::string token; stream >> token;) values.push_back(std::move(token));
            if (values.empty()) return;
            const auto top = values[0];
            const auto right = values.size() > 1 ? values[1] : values[0];
            const auto bottom = values.size() > 2 ? values[2] : values[0];
            const auto left = values.size() > 3 ? values[3]
                : values.size() > 1 ? values[1] : values[0];
            if (colors) {
                node.style.border_top_rgba = native_document::parse_color(top);
                node.style.border_right_rgba = native_document::parse_color(right);
                node.style.border_bottom_rgba = native_document::parse_color(bottom);
                node.style.border_left_rgba = native_document::parse_color(left);
            } else {
                node.style.border_top_width = native_document::parse_length(top);
                node.style.border_right_width = native_document::parse_length(right);
                node.style.border_bottom_width = native_document::parse_length(bottom);
                node.style.border_left_width = native_document::parse_length(left);
            }
        };
        if (name == "border-radius"
            || name == "border-top-left-radius"
            || name == "border-top-right-radius"
            || name == "border-bottom-right-radius"
            || name == "border-bottom-left-radius"
            || name == "border-start-start-radius"
            || name == "border-start-end-radius"
            || name == "border-end-end-radius"
            || name == "border-end-start-radius") {
            if (!is_inline(inline_border_radius)) {
                apply_corner_radius_declaration(
                    name,
                    value,
                    node.style.border_top_left_radius,
                    node.style.border_top_right_radius,
                    node.style.border_bottom_right_radius,
                    node.style.border_bottom_left_radius);
            }
            return;
        } else if (name == "transform") {
            if (!is_inline(inline_transform)) {
                native_document::parse_transform_translate(
                    value,
                    node.style.transform_translate_x,
                    node.style.transform_translate_y,
                    node.style.transform_scale_x,
                    node.style.transform_scale_y,
                    node.style.transform_rotate_degrees);
                node.style.transform_specified = true;
            }
            return;
        } else if (name == "transform-origin") {
            if (!is_inline(inline_transform)) {
                std::istringstream stream(value);
                std::string x;
                std::string y;
                stream >> x >> y;
                node.style.transform_origin_x = native_document::parse_length(x);
                node.style.transform_origin_y = native_document::parse_length(y.empty() ? x : y);
            }
            return;
        } else if (name == "transition" || name == "transition-duration") {
            if (name == "transition-duration" || value.find("transform") != std::string::npos) {
                std::istringstream stream(value);
                for (std::string token; stream >> token;) {
                    if (token.ends_with("ms")) {
                        token.resize(token.size() - 2U);
                        node.style.transform_transition_duration_ms =
                            std::max(0.0F, std::strtof(token.c_str(), nullptr));
                        break;
                    }
                    if (token.ends_with('s')) {
                        token.pop_back();
                        node.style.transform_transition_duration_ms =
                            std::max(0.0F, std::strtof(token.c_str(), nullptr) * 1000.0F);
                        break;
                    }
                }
                const auto set_timing = [&](float x1, float y1, float x2, float y2) {
                    node.style.transform_transition_x1 = x1;
                    node.style.transform_transition_y1 = y1;
                    node.style.transform_transition_x2 = x2;
                    node.style.transform_transition_y2 = y2;
                };
                const auto bezier = value.find("cubic-bezier(");
                if (bezier != std::string::npos) {
                    const auto start = bezier + std::string_view("cubic-bezier(").size();
                    const auto end = value.find(')', start);
                    if (end != std::string::npos) {
                        auto points = value.substr(start, end - start);
                        std::replace(points.begin(), points.end(), ',', ' ');
                        std::istringstream point_stream(points);
                        float x1 = 0.25F;
                        float y1 = 0.1F;
                        float x2 = 0.25F;
                        float y2 = 1.0F;
                        if (point_stream >> x1 >> y1 >> x2 >> y2) {
                            set_timing(
                                std::clamp(x1, 0.0F, 1.0F), y1,
                                std::clamp(x2, 0.0F, 1.0F), y2);
                        }
                    }
                } else if (value.find("linear") != std::string::npos) {
                    set_timing(0.0F, 0.0F, 1.0F, 1.0F);
                } else if (value.find("ease-in-out") != std::string::npos) {
                    set_timing(0.42F, 0.0F, 0.58F, 1.0F);
                } else if (value.find("ease-in") != std::string::npos) {
                    set_timing(0.42F, 0.0F, 1.0F, 1.0F);
                } else if (value.find("ease-out") != std::string::npos) {
                    set_timing(0.0F, 0.0F, 0.58F, 1.0F);
                }
            }
            return;
        } else if (name == "border") {
            parse_border(node.style.border_top_width, node.style.border_top_rgba);
            parse_border(node.style.border_right_width, node.style.border_right_rgba);
            parse_border(node.style.border_bottom_width, node.style.border_bottom_rgba);
            parse_border(node.style.border_left_width, node.style.border_left_rgba);
            return;
        } else if (name == "border-top") {
            parse_border(node.style.border_top_width, node.style.border_top_rgba);
            return;
        } else if (name == "border-right") {
            parse_border(node.style.border_right_width, node.style.border_right_rgba);
            return;
        } else if (name == "border-bottom") {
            parse_border(node.style.border_bottom_width, node.style.border_bottom_rgba);
            return;
        } else if (name == "border-left") {
            parse_border(node.style.border_left_width, node.style.border_left_rgba);
            return;
        } else if (name == "border-color") {
            parse_four_border_values(true);
            return;
        } else if (name == "border-width") {
            parse_four_border_values(false);
            return;
        } else if (name == "border-style" && value == "none") {
            node.style.border_top_width = {};
            node.style.border_right_width = {};
            node.style.border_bottom_width = {};
            node.style.border_left_width = {};
            return;
        } else if (name == "border-top-color") {
            node.style.border_top_rgba = native_document::parse_color(value);
            return;
        } else if (name == "border-right-color") {
            node.style.border_right_rgba = native_document::parse_color(value);
            return;
        } else if (name == "border-bottom-color") {
            node.style.border_bottom_rgba = native_document::parse_color(value);
            return;
        } else if (name == "border-left-color") {
            node.style.border_left_rgba = native_document::parse_color(value);
            return;
        } else if (name == "border-top-width") {
            node.style.border_top_width = native_document::parse_length(value);
            return;
        } else if (name == "border-right-width") {
            node.style.border_right_width = native_document::parse_length(value);
            return;
        } else if (name == "border-bottom-width") {
            node.style.border_bottom_width = native_document::parse_length(value);
            return;
        } else if (name == "border-left-width") {
            node.style.border_left_width = native_document::parse_length(value);
            return;
        }
        if (name == "width" && !is_inline(inline_width)) {
            node.style.width = native_document::parse_length(value);
        } else if (name == "height" && !is_inline(inline_height)) {
            node.style.height = native_document::parse_length(value);
        } else if (name == "min-width" && !is_inline(inline_min_max_size)) {
            node.style.min_width = native_document::parse_length(value);
        } else if (name == "min-height" && !is_inline(inline_min_max_size)) {
            node.style.min_height = native_document::parse_length(value);
        } else if (name == "max-width" && !is_inline(inline_min_max_size)) {
            node.style.max_width = value == "none" || value == "fit-content"
                || value == "max-content" || value == "min-content"
                ? css_length{} : native_document::parse_length(value);
        } else if (name == "max-height" && !is_inline(inline_min_max_size)) {
            node.style.max_height = value == "none" || value == "fit-content"
                || value == "max-content" || value == "min-content"
                ? css_length{} : native_document::parse_length(value);
        } else if ((name == "left" || name == "inset-inline-start") && !is_inline(inline_left)) {
            node.style.left = native_document::parse_length(value);
        } else if ((name == "top" || name == "inset-block-start") && !is_inline(inline_top)) {
            node.style.top = native_document::parse_length(value);
        } else if ((name == "right" || name == "inset-inline-end") && !is_inline(inline_right)) {
            node.style.right = native_document::parse_length(value);
        } else if ((name == "bottom" || name == "inset-block-end") && !is_inline(inline_bottom)) {
            node.style.bottom = native_document::parse_length(value);
        }
        else if (name == "inset") {
            const auto length = native_document::parse_length(value);
            if (!is_inline(inline_left)) node.style.left = length;
            if (!is_inline(inline_top)) node.style.top = length;
            if (!is_inline(inline_right)) node.style.right = length;
            if (!is_inline(inline_bottom)) node.style.bottom = length;
        } else if (name == "padding" && !is_inline(inline_padding)) {
            std::vector<css_length> values;
            size_t cursor = 0;
            while (cursor < value.size()) {
                while (cursor < value.size()
                    && std::isspace(static_cast<unsigned char>(value[cursor]))) ++cursor;
                if (cursor >= value.size()) break;
                auto end = value.find(' ', cursor);
                if (end == std::string::npos) end = value.size();
                values.push_back(native_document::parse_length(
                    value.substr(cursor, end - cursor)));
                cursor = end + 1U;
            }
            if (!values.empty()) {
                node.style.padding_top = values[0];
                node.style.padding_right = values.size() > 1 ? values[1] : values[0];
                node.style.padding_bottom = values.size() > 2 ? values[2] : values[0];
                node.style.padding_left = values.size() > 3 ? values[3]
                    : values.size() > 1 ? values[1] : values[0];
            }
        } else if ((name == "padding-block" || name == "padding-inline")
            && !is_inline(inline_padding)) {
            std::istringstream stream(value);
            std::string first;
            std::string second;
            stream >> first >> second;
            if (!first.empty()) {
                const auto start = native_document::parse_length(first);
                const auto end = native_document::parse_length(second.empty() ? first : second);
                if (name == "padding-block") {
                    node.style.padding_top = start;
                    node.style.padding_bottom = end;
                } else {
                    node.style.padding_left = start;
                    node.style.padding_right = end;
                }
            }
        } else if ((name == "padding-left" || name == "padding-inline-start")
            && !is_inline(inline_padding)) {
            node.style.padding_left = native_document::parse_length(value);
        } else if ((name == "padding-right" || name == "padding-inline-end")
            && !is_inline(inline_padding)) {
            node.style.padding_right = native_document::parse_length(value);
        } else if ((name == "padding-top" || name == "padding-block-start")
            && !is_inline(inline_padding)) {
            node.style.padding_top = native_document::parse_length(value);
        } else if ((name == "padding-bottom" || name == "padding-block-end")
            && !is_inline(inline_padding)) {
            node.style.padding_bottom = native_document::parse_length(value);
        } else if (name == "margin" && !is_inline(inline_margin)) {
            std::vector<css_length> values;
            size_t cursor = 0;
            while (cursor < value.size()) {
                while (cursor < value.size()
                    && std::isspace(static_cast<unsigned char>(value[cursor]))) ++cursor;
                if (cursor >= value.size()) break;
                auto end = value.find(' ', cursor);
                if (end == std::string::npos) end = value.size();
                values.push_back(native_document::parse_length(value.substr(cursor, end - cursor)));
                cursor = end + 1U;
            }
            if (!values.empty()) {
                node.style.margin_top = values[0];
                node.style.margin_right = values.size() > 1 ? values[1] : values[0];
                node.style.margin_bottom = values.size() > 2 ? values[2] : values[0];
                node.style.margin_left = values.size() > 3 ? values[3]
                    : values.size() > 1 ? values[1] : values[0];
            }
        } else if ((name == "margin-block" || name == "margin-inline")
            && !is_inline(inline_margin)) {
            std::istringstream stream(value);
            std::string first;
            std::string second;
            stream >> first >> second;
            if (!first.empty()) {
                const auto start = native_document::parse_length(first);
                const auto end = native_document::parse_length(second.empty() ? first : second);
                if (name == "margin-block") {
                    node.style.margin_top = start;
                    node.style.margin_bottom = end;
                } else {
                    node.style.margin_left = start;
                    node.style.margin_right = end;
                }
            }
        } else if ((name == "margin-left" || name == "margin-inline-start")
            && !is_inline(inline_margin)) {
            node.style.margin_left = native_document::parse_length(value);
        } else if ((name == "margin-right" || name == "margin-inline-end")
            && !is_inline(inline_margin)) {
            node.style.margin_right = native_document::parse_length(value);
        } else if ((name == "margin-top" || name == "margin-block-start")
            && !is_inline(inline_margin)) {
            node.style.margin_top = native_document::parse_length(value);
        } else if ((name == "margin-bottom" || name == "margin-block-end")
            && !is_inline(inline_margin)) {
            node.style.margin_bottom = native_document::parse_length(value);
        } else if (name == "display" && !is_inline(inline_display)) {
            node.style.display = value == "flex" ? display_mode::flex
                : value == "inline-flex" ? display_mode::inline_flex
                : value == "grid" ? display_mode::grid
                : value == "inline-grid" ? display_mode::inline_grid
                : value == "inline" || value == "inline-block" ? display_mode::inline_block
                : value == "none" ? display_mode::none : display_mode::block;
        } else if (name == "grid-template-columns") {
            node.style.grid_two_columns = value.find("1fr") != std::string::npos;
        } else if (name == "grid-column") {
            node.style.grid_span_all = value.find('/') != std::string::npos;
        } else if (name == "position" && !is_inline(inline_position)) {
            node.style.position = value == "absolute" ? position_mode::absolute
                : value == "fixed" ? position_mode::fixed
                : value == "relative" ? position_mode::relative
                : position_mode::normal;
        } else if (name == "z-index") {
            node.style.z_index = value == "auto" ? 0 : std::atoi(value.c_str());
        } else if (name == "flex-direction" && !is_inline(inline_flex_direction)) {
            node.style.direction = value == "row" || value == "row-reverse"
                ? flex_direction::row : flex_direction::column;
            node.style.flex_reverse = value == "row-reverse" || value == "column-reverse";
        } else if (name == "flex-wrap" && !is_inline(inline_flex_wrap)) {
            node.style.flex_wrap = value == "wrap" || value == "wrap-reverse";
        } else if (name == "align-items" && !is_inline(inline_align_items)) {
            node.style.align_items = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
        } else if (name == "align-self" && !is_inline(inline_align_self)) {
            node.style.align_self_specified = value != "auto";
            node.style.align_self = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
        } else if (name == "justify-content" && !is_inline(inline_justify_content)) {
            node.style.justify_content = value == "center" ? justify_mode::center
                : value == "flex-end" || value == "end" ? justify_mode::end
                : value == "space-between" ? justify_mode::space_between
                : justify_mode::start;
        } else if (name == "flex-grow" && !is_inline(inline_flex_grow)) {
            node.style.flex_grow = std::strtof(value.c_str(), nullptr);
        } else if (name == "flex-shrink" && !is_inline(inline_flex_shrink)) {
            node.style.flex_shrink = std::max(0.0F, std::strtof(value.c_str(), nullptr));
        } else if (name == "flex"
            && (!is_inline(inline_flex_grow) || !is_inline(inline_flex_shrink))) {
            if (value == "none") {
                if (!is_inline(inline_flex_grow)) node.style.flex_grow = 0;
                if (!is_inline(inline_flex_shrink)) node.style.flex_shrink = 0;
            } else {
                std::istringstream stream(value);
                std::string grow_value;
                std::string shrink_value;
                stream >> grow_value >> shrink_value;
                if (!is_inline(inline_flex_grow) && !grow_value.empty()) {
                    node.style.flex_grow = grow_value == "auto" ? 1.0F
                        : std::max(0.0F, std::strtof(grow_value.c_str(), nullptr));
                }
                if (!is_inline(inline_flex_shrink)) {
                    node.style.flex_shrink = shrink_value.empty()
                        ? 1.0F : std::max(0.0F, std::strtof(shrink_value.c_str(), nullptr));
                }
            }
        } else if (name == "box-sizing" && !is_inline(inline_box_sizing)) {
            node.style.border_box = value == "border-box";
        } else if ((name == "background" || name == "background-color")
            && !is_inline(inline_background)) {
            const auto parsed = native_document::parse_color(value);
            if (parsed != 0U || value == "transparent") node.style.background_rgba = parsed;
        } else if ((name == "overflow" || name == "overflow-x" || name == "overflow-y")
            && !is_inline(inline_overflow)) {
            node.style.clip = value == "hidden" || value == "clip"
                || value == "auto" || value == "scroll";
        } else if (name == "visibility" && !is_inline(inline_visibility)) {
            node.style.visibility_specified = value != "inherit" && value != "unset";
            node.style.visibility_hidden = value == "hidden" || value == "collapse";
        } else if (name == "pointer-events" && !is_inline(inline_pointer_events)) {
            node.style.pointer_events_specified = value != "inherit" && value != "unset";
            node.style.pointer_events_none = value == "none";
        } else if (name == "opacity" && !is_inline(inline_opacity)) {
            node.style.opacity = std::clamp(std::strtof(value.c_str(), nullptr), 0.0F, 1.0F);
        } else if (name == "color" && !is_inline(inline_color)) {
            const auto parsed = native_document::parse_color(value);
            if (parsed != 0U || value == "transparent") node.style.foreground_rgba = parsed;
        } else if (name == "font-size" && !is_inline(inline_font_size)) {
            node.style.font_size = std::max(0.0F, native_document::parse_length(value).value);
        } else if (name == "font-family" && !is_inline(inline_font_family)) {
            node.style.font_family = value;
        } else if (name == "font-weight" && !is_inline(inline_font_weight)) {
            node.style.font_weight = value == "bold" ? 700
                : value == "normal" ? 400
                : std::max(1, static_cast<int>(std::lround(
                    native_document::parse_length(value).value)));
        } else if (name == "line-height" && !is_inline(inline_line_height)) {
            const auto parsed = std::max(0.0F, native_document::parse_length(value).value);
            node.style.line_height = parsed > 0 && parsed <= 4
                ? parsed * (node.style.font_size >= 0 ? node.style.font_size : 14.0F)
                : parsed;
        } else if (name == "text-align" && !is_inline(inline_text_align)) {
            node.style.text_align = value;
        } else if (name == "white-space" && !is_inline(inline_white_space)) {
            node.style.white_space = value == "inherit" || value == "unset"
                ? std::string{} : value;
        }
    }

    void apply_css_rules(dom_node& node)
    {
        binding_callback_timer timer(profile_startup ? &startup_css_apply : nullptr);
        // Text nodes generate anonymous inline boxes and are not CSS selector
        // subjects. Their inherited values are resolved from their ancestors.
        if (node.tag == "#text") {
            node.style.display = display_mode::inline_block;
            document.mark_dirty();
            return;
        }
        const auto previous_transform_rotation = node.style.transform_rotate_degrees;
        node.style.important_property_mask = 0;
        if ((node.style.inline_property_mask & inline_position) == 0U) {
            node.style.position = position_mode::normal;
        }
        node.style.z_index = 0;
        node.style.grid_two_columns = false;
        node.style.grid_span_all = false;
        if ((node.style.inline_property_mask & inline_width) == 0U) node.style.width = {};
        if ((node.style.inline_property_mask & inline_height) == 0U) node.style.height = {};
        if ((node.style.inline_property_mask & inline_min_max_size) == 0U) {
            node.style.min_width = {};
            node.style.min_height = {};
            node.style.max_width = {};
            node.style.max_height = {};
        }
        if ((node.style.inline_property_mask & inline_left) == 0U) node.style.left = {};
        if ((node.style.inline_property_mask & inline_top) == 0U) node.style.top = {};
        if ((node.style.inline_property_mask & inline_right) == 0U) node.style.right = {};
        if ((node.style.inline_property_mask & inline_bottom) == 0U) node.style.bottom = {};
        if ((node.style.inline_property_mask & inline_display) == 0U) {
            node.style.display = default_display_for_tag(node.tag);
        }
        if ((node.style.inline_property_mask & inline_padding) == 0U) {
            node.style.padding_left = {};
            node.style.padding_top = {};
            node.style.padding_right = {};
            node.style.padding_bottom = {};
        }
        if ((node.style.inline_property_mask & inline_margin) == 0U) {
            node.style.margin_left = {};
            node.style.margin_top = {};
            node.style.margin_right = {};
            node.style.margin_bottom = {};
        }
        node.style.border_left_width = {};
        node.style.border_top_width = {};
        node.style.border_right_width = {};
        node.style.border_bottom_width = {};
        node.style.border_left_rgba = 0;
        node.style.border_top_rgba = 0;
        node.style.border_right_rgba = 0;
        node.style.border_bottom_rgba = 0;
        if ((node.style.inline_property_mask & inline_border_radius) == 0U) {
            node.style.border_top_left_radius = {};
            node.style.border_top_right_radius = {};
            node.style.border_bottom_right_radius = {};
            node.style.border_bottom_left_radius = {};
        }
        if ((node.style.inline_property_mask & inline_transform) == 0U) {
            node.style.transform_translate_x = {};
            node.style.transform_translate_y = {};
            node.style.transform_origin_x = {50, length_unit::percent};
            node.style.transform_origin_y = {50, length_unit::percent};
            node.style.transform_scale_x = 1;
            node.style.transform_scale_y = 1;
            node.style.transform_rotate_degrees = 0;
            node.style.transform_specified = false;
        }
        node.style.transform_transition_duration_ms = 0;
        node.style.transform_transition_x1 = 0.25F;
        node.style.transform_transition_y1 = 0.1F;
        node.style.transform_transition_x2 = 0.25F;
        node.style.transform_transition_y2 = 1.0F;
        if ((node.style.inline_property_mask & inline_flex_direction) == 0U) {
            node.style.direction = flex_direction::row;
            node.style.flex_reverse = false;
        }
        if ((node.style.inline_property_mask & inline_align_items) == 0U) {
            node.style.align_items = align_mode::stretch;
        }
        if ((node.style.inline_property_mask & inline_align_self) == 0U) {
            node.style.align_self = align_mode::stretch;
            node.style.align_self_specified = false;
        }
        if ((node.style.inline_property_mask & inline_justify_content) == 0U) {
            node.style.justify_content = justify_mode::start;
        }
        if ((node.style.inline_property_mask & inline_flex_wrap) == 0U) node.style.flex_wrap = false;
        if ((node.style.inline_property_mask & inline_flex_grow) == 0U) node.style.flex_grow = 0;
        if ((node.style.inline_property_mask & inline_flex_shrink) == 0U) node.style.flex_shrink = 1;
        if ((node.style.inline_property_mask & inline_box_sizing) == 0U) node.style.border_box = false;
        if ((node.style.inline_property_mask & inline_background) == 0U) node.style.background_rgba = 0;
        if ((node.style.inline_property_mask & inline_overflow) == 0U) node.style.clip = false;
        if ((node.style.inline_property_mask & inline_pointer_events) == 0U) {
            node.style.pointer_events_none = false;
            node.style.pointer_events_specified = false;
        }
        if ((node.style.inline_property_mask & inline_opacity) == 0U) node.style.opacity = 1;
        if ((node.style.inline_property_mask & inline_color) == 0U) node.style.foreground_rgba = 0;
        if ((node.style.inline_property_mask & inline_font_size) == 0U) node.style.font_size = -1;
        if ((node.style.inline_property_mask & inline_font_family) == 0U) node.style.font_family.clear();
        if ((node.style.inline_property_mask & inline_font_weight) == 0U) node.style.font_weight = 0;
        if ((node.style.inline_property_mask & inline_line_height) == 0U) node.style.line_height = -1;
        if ((node.style.inline_property_mask & inline_text_align) == 0U) node.style.text_align.clear();
        if ((node.style.inline_property_mask & inline_white_space) == 0U) node.style.white_space.clear();
        node.style.before = {};
        node.style.after = {};
        // Recompute stylesheet visibility from the current class/selector set.
        // React reuses toolbar nodes while replacing their responsive classes;
        // retaining an earlier `visibility:hidden` makes the new variant stale.
        if ((node.style.inline_property_mask & inline_visibility) == 0U) {
            node.style.visibility_hidden = false;
            node.style.visibility_specified = false;
        }
        std::vector<size_t> candidates = unindexed_css_rules;
        const auto append_candidates = [&](const auto& index, const std::string& key) {
            const auto match = index.find(key);
            if (match != index.end()) {
                candidates.insert(candidates.end(), match->second.begin(), match->second.end());
            }
        };
        append_candidates(css_rules_by_tag, node.tag);
        if (!node.id_attribute.empty()) append_candidates(css_rules_by_id, node.id_attribute);
        const std::string_view classes(node.class_name);
        size_t class_cursor = 0;
        while (class_cursor < classes.size()) {
            while (class_cursor < classes.size()
                && std::isspace(static_cast<unsigned char>(classes[class_cursor]))) ++class_cursor;
            const auto start = class_cursor;
            while (class_cursor < classes.size()
                && !std::isspace(static_cast<unsigned char>(classes[class_cursor]))) ++class_cursor;
            if (class_cursor > start) {
                append_candidates(
                    css_rules_by_class,
                    std::string(classes.substr(start, class_cursor - start)));
            }
        }
        std::sort(candidates.begin(), candidates.end());
        candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());
        std::vector<const css_rule*> matched_rules;
        for (const auto index : candidates) {
            const auto& rule = css_rules[index];
            std::string pseudo_origin;
            const auto pseudo_kind = split_pseudo_element_selector(rule.selector, pseudo_origin);
            if (pseudo_kind != 0) {
                if (!pseudo_origin.empty() && css_selector_matches(node, pseudo_origin)) {
                    auto& pseudo = pseudo_kind == 1 ? node.style.before : node.style.after;
                    for (const auto& declaration : rule.declarations) {
                        apply_pseudo_css_declaration(node, pseudo, declaration);
                    }
                }
                continue;
            }
            if (!css_selector_matches(node, rule.selector)) continue;
            matched_rules.push_back(&rule);
        }
        // CSS custom properties are cascaded before dependent declarations are
        // computed. Applying each rule eagerly made a base button height resolve
        // with the default medium size before its later `.small-*` rule set the
        // size token.
        for (const auto* rule : matched_rules) {
            for (const auto& declaration : rule->declarations) {
                if (declaration.name.starts_with("--")) {
                    apply_css_declaration(node, declaration);
                }
            }
        }
        for (const auto* rule : matched_rules) {
            for (const auto& declaration : rule->declarations) {
                if (declaration.name.starts_with("--")) continue;
                apply_css_declaration(node, declaration);
            }
        }
        document.update_transform_animation(node, previous_transform_rotation);
        document.mark_dirty();
    }

    void apply_appended_css_rules(dom_node& node, size_t first_new_rule)
    {
        binding_callback_timer timer(
            profile_startup ? &startup_css_incremental_apply : nullptr);
        if (node.tag == "#text" || first_new_rule >= css_rules.size()) return;

        std::vector<size_t> candidates = unindexed_css_rules;
        const auto append_candidates = [&](const auto& index, const std::string& key) {
            const auto match = index.find(key);
            if (match != index.end()) {
                candidates.insert(candidates.end(), match->second.begin(), match->second.end());
            }
        };
        append_candidates(css_rules_by_tag, node.tag);
        if (!node.id_attribute.empty()) append_candidates(css_rules_by_id, node.id_attribute);
        const std::string_view classes(node.class_name);
        size_t class_cursor = 0;
        while (class_cursor < classes.size()) {
            while (class_cursor < classes.size()
                && std::isspace(static_cast<unsigned char>(classes[class_cursor]))) ++class_cursor;
            const auto start = class_cursor;
            while (class_cursor < classes.size()
                && !std::isspace(static_cast<unsigned char>(classes[class_cursor]))) ++class_cursor;
            if (class_cursor > start) {
                append_candidates(
                    css_rules_by_class,
                    std::string(classes.substr(start, class_cursor - start)));
            }
        }
        std::sort(candidates.begin(), candidates.end());
        candidates.erase(std::remove_if(
            candidates.begin(),
            candidates.end(),
            [first_new_rule](size_t index) { return index < first_new_rule; }), candidates.end());
        candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());

        const auto previous_transform_rotation = node.style.transform_rotate_degrees;
        std::vector<const css_rule*> matched_rules;
        for (const auto index : candidates) {
            const auto& rule = css_rules[index];
            std::string pseudo_origin;
            const auto pseudo_kind = split_pseudo_element_selector(rule.selector, pseudo_origin);
            if (pseudo_kind != 0) {
                if (!pseudo_origin.empty() && css_selector_matches(node, pseudo_origin)) {
                    auto& pseudo = pseudo_kind == 1 ? node.style.before : node.style.after;
                    for (const auto& declaration : rule.declarations) {
                        apply_pseudo_css_declaration(node, pseudo, declaration);
                    }
                }
                continue;
            }
            if (css_selector_matches(node, rule.selector)) matched_rules.push_back(&rule);
        }
        for (const auto* rule : matched_rules) {
            for (const auto& declaration : rule->declarations) {
                if (declaration.name.starts_with("--")) apply_css_declaration(node, declaration);
            }
        }
        for (const auto* rule : matched_rules) {
            for (const auto& declaration : rule->declarations) {
                if (!declaration.name.starts_with("--")) apply_css_declaration(node, declaration);
            }
        }
        document.update_transform_animation(node, previous_transform_rotation);
        document.mark_dirty();
    }

    void apply_css_rules_subtree(dom_node& node)
    {
        apply_css_rules(node);
        for (auto* child : node.children) apply_css_rules_subtree(*child);
    }

    bool is_connected(const dom_node& node) const
    {
        for (auto* current = &node; current != nullptr; current = current->parent) {
            if (current == &document.body()) return true;
        }
        return false;
    }

    void recascade_connected_subtree(dom_node& node)
    {
        if (!is_connected(node)) return;
        const auto trace = std::getenv("HTMLML_PROBE_PROFILE_CSS") != nullptr;
        const auto started = trace ? std::chrono::steady_clock::now()
            : std::chrono::steady_clock::time_point{};
        binding_callback_timer timer(profile_startup ? &startup_subtree_recascade : nullptr);
        apply_css_rules_subtree(node);
        if (trace) {
            const auto elapsed = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count();
            if (elapsed >= 0.5) {
                size_t descendants = 0;
                std::vector<const dom_node*> pending{&node};
                while (!pending.empty()) {
                    const auto* current = pending.back();
                    pending.pop_back();
                    ++descendants;
                    pending.insert(pending.end(), current->children.begin(), current->children.end());
                }
                std::cerr << "[Native Engine Probe] CSS recascade "
                    << std::fixed << std::setprecision(3) << elapsed << " ms, nodes="
                    << descendants << ", tag=" << node.tag << ", id=" << node.id_attribute
                    << ", class=" << node.class_name.substr(0, 180) << '\n';
            }
        }
    }

    void activate_connected_stylesheet(dom_node& node)
    {
        if (node.tag != "style" || !is_connected(node)) return;
        std::string stylesheet;
        append_node_text(node, stylesheet);
        if (stylesheet.empty()) return;

        const auto first_new_rule = add_stylesheet(std::move(stylesheet));
        for (auto* existing : document.query_selector_all(document.body(), "*")) {
            apply_appended_css_rules(*existing, first_new_rule);
        }
    }

    static std::vector<std::string> parse_frame_stylesheets(const std::string& html)
    {
        std::vector<std::string> result;
        size_t cursor = 0;
        while (true) {
            const auto open = html.find("<link", cursor);
            if (open == std::string::npos) break;
            const auto close = html.find('>', open + 5U);
            if (close == std::string::npos) break;
            const auto tag = html.substr(open, close - open + 1U);
            const auto relation = extract_html_attribute(tag, "rel");
            const auto href = extract_html_attribute(tag, "href");
            if (relation == "stylesheet" && !href.empty()) result.push_back(href);
            cursor = close + 1U;
        }
        return result;
    }

    std::filesystem::path compilation_cache_path(const std::array<uint8_t, 32>& key) const
    {
        if (compilation_cache_directory.empty()) return {};
        return std::filesystem::path(compilation_cache_directory)
            / (to_hex(key) + ".v8cache");
    }

    void prune_persistent_compilation_cache()
    {
        if (compilation_cache_directory.empty()) return;
        const auto directory = std::filesystem::path(compilation_cache_directory);
        std::error_code error;
        if (!std::filesystem::is_directory(directory, error)) return;

        struct cache_file final {
            std::filesystem::path path;
            std::filesystem::file_time_type last_write;
            uint64_t size;
        };
        std::vector<cache_file> files;
        uint64_t total_bytes = 0;
        std::filesystem::directory_iterator iterator(directory, error);
        const std::filesystem::directory_iterator end;
        while (!error && iterator != end) {
            const auto path = iterator->path();
            iterator.increment(error);
            if (path.extension() != ".v8cache") continue;
            std::error_code metadata_error;
            const auto size = std::filesystem::file_size(path, metadata_error);
            if (metadata_error) continue;
            const auto last_write = std::filesystem::last_write_time(path, metadata_error);
            if (metadata_error) continue;
            files.push_back(cache_file{path, last_write, size});
            total_bytes += size;
        }
        std::sort(files.begin(), files.end(), [](const cache_file& left, const cache_file& right) {
            return left.last_write < right.last_write;
        });
        size_t first_retained = 0;
        while (first_retained < files.size()
            && (files.size() - first_retained > maximum_persistent_cache_entries
                || total_bytes > maximum_persistent_cache_bytes)) {
            std::error_code remove_error;
            if (std::filesystem::remove(files[first_retained].path, remove_error)) {
                total_bytes -= files[first_retained].size;
            }
            ++first_retained;
        }
    }

    bool read_compilation_cache(
        const std::array<uint8_t, 32>& key,
        std::vector<uint8_t>& payload)
    {
        const auto path = compilation_cache_path(key);
        if (path.empty()) return false;
        std::ifstream stream(path, std::ios::binary);
        if (!stream) {
            ++compilation_persistent_miss_count;
            return false;
        }

        constexpr std::array<char, 8> magic{'H', 'M', 'L', 'N', 'V', '8', '0', '1'};
        std::array<char, 8> stored_magic{};
        uint32_t schema = 0;
        uint32_t version_tag = 0;
        std::array<uint8_t, 32> stored_key{};
        std::array<uint8_t, 32> stored_payload_hash{};
        uint64_t payload_length = 0;
        stream.read(stored_magic.data(), static_cast<std::streamsize>(stored_magic.size()));
        stream.read(reinterpret_cast<char*>(&schema), sizeof(schema));
        stream.read(reinterpret_cast<char*>(&version_tag), sizeof(version_tag));
        stream.read(reinterpret_cast<char*>(stored_key.data()), static_cast<std::streamsize>(stored_key.size()));
        stream.read(reinterpret_cast<char*>(stored_payload_hash.data()), static_cast<std::streamsize>(stored_payload_hash.size()));
        stream.read(reinterpret_cast<char*>(&payload_length), sizeof(payload_length));
        if (!stream || stored_magic != magic || schema != 1U
            || version_tag != v8::ScriptCompiler::CachedDataVersionTag()
            || stored_key != key || payload_length == 0U
            || payload_length > maximum_persistent_cache_entry_bytes) {
            ++compilation_cache_rejection_count;
            std::error_code error;
            std::filesystem::remove(path, error);
            return false;
        }
        payload.resize(static_cast<size_t>(payload_length));
        stream.read(reinterpret_cast<char*>(payload.data()), static_cast<std::streamsize>(payload.size()));
        if (!stream || stream.peek() != std::ifstream::traits_type::eof()
            || sha256(payload.data(), payload.size()) != stored_payload_hash) {
            payload.clear();
            ++compilation_cache_rejection_count;
            std::error_code error;
            std::filesystem::remove(path, error);
            return false;
        }
        compilation_cache_bytes_read_count += payload.size();
        std::error_code access_error;
        std::filesystem::last_write_time(
            path,
            std::filesystem::file_time_type::clock::now(),
            access_error);
        return true;
    }

    void write_compilation_cache(
        const std::array<uint8_t, 32>& key,
        const v8::ScriptCompiler::CachedData& data)
    {
        const auto path = compilation_cache_path(key);
        if (path.empty() || data.data == nullptr || data.length <= 0
            || static_cast<size_t>(data.length) > maximum_persistent_cache_entry_bytes) return;
        std::error_code error;
        std::filesystem::create_directories(path.parent_path(), error);
        if (error) return;

        static std::atomic<uint64_t> temporary_sequence{0};
        const auto temporary = path.string()
            + "." + std::to_string(current_process_id())
            + "." + std::to_string(temporary_sequence.fetch_add(1, std::memory_order_relaxed))
            + ".tmp";
        std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
        if (!stream) return;
        constexpr std::array<char, 8> magic{'H', 'M', 'L', 'N', 'V', '8', '0', '1'};
        constexpr uint32_t schema = 1U;
        const auto version_tag = v8::ScriptCompiler::CachedDataVersionTag();
        const auto payload_hash = sha256(data.data, static_cast<size_t>(data.length));
        const auto payload_length = static_cast<uint64_t>(data.length);
        stream.write(magic.data(), static_cast<std::streamsize>(magic.size()));
        stream.write(reinterpret_cast<const char*>(&schema), sizeof(schema));
        stream.write(reinterpret_cast<const char*>(&version_tag), sizeof(version_tag));
        stream.write(reinterpret_cast<const char*>(key.data()), static_cast<std::streamsize>(key.size()));
        stream.write(reinterpret_cast<const char*>(payload_hash.data()), static_cast<std::streamsize>(payload_hash.size()));
        stream.write(reinterpret_cast<const char*>(&payload_length), sizeof(payload_length));
        stream.write(reinterpret_cast<const char*>(data.data), data.length);
        stream.close();
        if (!stream) {
            std::filesystem::remove(temporary, error);
            return;
        }
        std::filesystem::rename(temporary, path, error);
        if (error) {
            std::filesystem::remove(temporary, error);
            return;
        }
        compilation_cache_bytes_written_count += static_cast<uint64_t>(data.length);
        if (++persistent_writes_since_prune >= 64U) {
            persistent_writes_since_prune = 0;
            prune_persistent_compilation_cache();
        }
    }

    void retain_compiled_script(
        const std::string& key,
        v8::Local<v8::UnboundScript> script,
        size_t source_bytes)
    {
        compilation_cache_entry entry;
        entry.script.Reset(isolate, script);
        entry.source_bytes = source_bytes;
        compilation_cache_bytes += source_bytes;
        compilation_cache.emplace(key, std::move(entry));
        compilation_cache_order.push_back(key);
        while (compilation_cache.size() > maximum_compilation_cache_entries
            || compilation_cache_bytes > maximum_compilation_cache_source_bytes) {
            const auto oldest = std::move(compilation_cache_order.front());
            compilation_cache_order.pop_front();
            const auto iterator = compilation_cache.find(oldest);
            if (iterator == compilation_cache.end()) continue;
            compilation_cache_bytes -= iterator->second.source_bytes;
            iterator->second.script.Reset();
            compilation_cache.erase(iterator);
        }
    }

    bool compile_script(
        const std::string& source,
        const std::string& document_name,
        v8::Local<v8::Script>& script)
    {
        ++compilation_request_count;
        const auto key_digest = compilation_key_digest(document_name, source);
        const auto key = to_hex(key_digest);
        const auto cached = compilation_cache.find(key);
        if (cached != compilation_cache.end()) {
            ++compilation_memory_hit_count;
            script = cached->second.script.Get(isolate)->BindToCurrentContext();
            return true;
        }

        auto source_value = v8::String::NewFromUtf8(
            isolate,
            source.data(),
            v8::NewStringType::kNormal,
            static_cast<int>(source.size())).ToLocalChecked();
        auto name_value = v8::String::NewFromUtf8(
            isolate,
            document_name.data(),
            v8::NewStringType::kNormal,
            static_cast<int>(document_name.size())).ToLocalChecked();
        v8::ScriptOrigin origin(name_value);
        std::vector<uint8_t> persistent_bytes;
        const auto has_persistent = read_compilation_cache(key_digest, persistent_bytes);
        v8::ScriptCompiler::CachedData* cached_data = nullptr;
        if (has_persistent) {
            auto* owned = new uint8_t[persistent_bytes.size()];
            std::memcpy(owned, persistent_bytes.data(), persistent_bytes.size());
            cached_data = new v8::ScriptCompiler::CachedData(
                owned,
                static_cast<int>(persistent_bytes.size()),
                v8::ScriptCompiler::CachedData::BufferOwned);
        }
        v8::ScriptCompiler::Source compiler_source(source_value, origin, cached_data);
        v8::Local<v8::UnboundScript> unbound;
        const auto started = std::chrono::steady_clock::now();
        const auto options = has_persistent
            ? v8::ScriptCompiler::kConsumeCodeCache
            : v8::ScriptCompiler::kNoCompileOptions;
        const auto compiled = v8::ScriptCompiler::CompileUnboundScript(
            isolate,
            &compiler_source,
            options).ToLocal(&unbound);
        compilation_time_nanosecond_count += static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - started).count());
        if (!compiled) return false;

        if (has_persistent && cached_data != nullptr && !cached_data->rejected) {
            ++compilation_persistent_hit_count;
        } else {
            if (has_persistent) {
                ++compilation_cache_rejection_count;
                std::error_code error;
                std::filesystem::remove(compilation_cache_path(key_digest), error);
            }
            if (!compilation_cache_directory.empty()) {
                std::unique_ptr<v8::ScriptCompiler::CachedData> generated(
                    v8::ScriptCompiler::CreateCodeCache(unbound));
                if (generated) write_compilation_cache(key_digest, *generated);
            }
        }
        retain_compiled_script(key, unbound, source.size());
        script = unbound->BindToCurrentContext();
        return true;
    }

    bool execute_in_context(
        v8::Local<v8::Context> local_context,
        const std::string& source,
        const std::string& document_name,
        std::string& error,
        bool checkpoint_microtasks = true)
    {
        binding_callback_timer timer(profile_startup ? &startup_script_execute : nullptr);
        v8::Context::Scope context_scope(local_context);
        v8::TryCatch try_catch(isolate);
        v8::Local<v8::Script> script;
        if (!compile_script(source, document_name, script)
            || script->Run(local_context).IsEmpty()) {
            error = describe_exception(try_catch, local_context);
            return false;
        }
        if (checkpoint_microtasks) isolate->PerformMicrotaskCheckpoint();
        return true;
    }

    static void current_script_get_attribute(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
    }

    void install_frame_globals(
        v8::Local<v8::Context> local_context,
        dom_node& frame,
        dom_node& frame_body)
    {
        v8::Context::Scope context_scope(local_context);
        auto global = local_context->Global();
        auto frame_document_value = document_template.Get(isolate)->NewInstance(local_context).ToLocalChecked();
        frame_document_value->SetAlignedPointerInInternalField(
            0,
            this,
            v8::kEmbedderDataTypeTagDefault);
        frame_document_value->Set(
            local_context,
            js_string(isolate, "readyState"),
            js_string(isolate, "loading")).Check();
        frame_document_value->Set(
            local_context,
            js_string(isolate, "currentScript"),
            v8::Null(isolate)).Check();

        global->Set(local_context, js_string(isolate, "window"), global).Check();
        global->Set(local_context, js_string(isolate, "self"), global).Check();
        global->Set(local_context, js_string(isolate, "document"), frame_document_value).Check();
        actual_frame_windows[frame.id].Reset(isolate, global);
        actual_frame_documents[frame.id].Reset(isolate, frame_document_value);
        global->Set(local_context, js_string(isolate, "name"), js_string(isolate, frame.attributes["name"].c_str())).Check();
        global->Set(local_context, js_string(isolate, "parent"), context.Get(isolate)->Global()).Check();
        global->Set(local_context, js_string(isolate, "top"), context.Get(isolate)->Global()).Check();
        global->Set(local_context, js_string(isolate, "frameElement"), wrap_node(frame)).Check();
        global->Set(local_context, js_string(isolate, "devicePixelRatio"), v8::Number::New(isolate, 1)).Check();
        global->SetNativeDataProperty(local_context, js_string(isolate, "innerWidth"), get_inner_width).Check();
        global->SetNativeDataProperty(local_context, js_string(isolate, "innerHeight"), get_inner_height).Check();
        global->Set(local_context, js_string(isolate, "setTimeout"), v8::Function::New(local_context, set_timeout).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "clearTimeout"), v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "setInterval"), v8::Function::New(local_context, set_interval).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "clearInterval"), v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "requestAnimationFrame"), v8::Function::New(local_context, request_animation_frame).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "cancelAnimationFrame"), v8::Function::New(local_context, clear_timeout).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "getComputedStyle"), v8::Function::New(local_context, get_computed_style).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "addEventListener"), v8::Function::New(local_context, add_event_listener).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "removeEventListener"), v8::Function::New(local_context, remove_event_listener).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "dispatchEvent"), v8::Function::New(local_context, dispatch_event).ToLocalChecked()).Check();
        auto event_template = v8::FunctionTemplate::New(isolate, event_constructor);
        global->Set(local_context, js_string(isolate, "Event"), event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "CustomEvent"), event_template->GetFunction(local_context).ToLocalChecked()).Check();
        auto observer_template = v8::FunctionTemplate::New(isolate, observer_constructor);
        global->Set(local_context, js_string(isolate, "MutationObserver"), observer_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "ResizeObserver"),
            v8::FunctionTemplate::New(isolate, resize_observer_constructor)
                ->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "IntersectionObserver"), observer_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "AbortController"), v8::FunctionTemplate::New(isolate, abort_controller_constructor)->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "matchMedia"), v8::Function::New(local_context, match_media).ToLocalChecked()).Check();
        auto element_constructor = element_template.Get(isolate)->GetFunction(local_context).ToLocalChecked();
        element_constructor->Set(
            local_context,
            js_string(isolate, "ELEMENT_NODE"),
            v8::Integer::New(isolate, 1)).Check();
        element_constructor->Set(
            local_context,
            js_string(isolate, "DOCUMENT_NODE"),
            v8::Integer::New(isolate, 9)).Check();
        global->Set(local_context, js_string(isolate, "Node"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Element"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLButtonElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLCanvasElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLIFrameElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLImageElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "HTMLInputElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "SVGElement"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Document"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "DocumentFragment"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "Window"), element_constructor).Check();
        global->Set(local_context, js_string(isolate, "KeyboardEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "MouseEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "PointerEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "WheelEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "FocusEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "InputEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "UIEvent"),
            event_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(
            local_context,
            js_string(isolate, "getSelection"),
            v8::Function::New(local_context, get_selection).ToLocalChecked()).Check();
        auto text_encoder_template = v8::FunctionTemplate::New(isolate);
        text_encoder_template->PrototypeTemplate()->Set(
            js_string(isolate, "encode"),
            v8::FunctionTemplate::New(isolate, text_encoder_encode));
        global->Set(local_context, js_string(isolate, "TextEncoder"), text_encoder_template->GetFunction(local_context).ToLocalChecked()).Check();
        auto path_template = v8::FunctionTemplate::New(isolate, path_2d_constructor);
        global->Set(local_context, js_string(isolate, "Path2D"), path_template->GetFunction(local_context).ToLocalChecked()).Check();
        auto dom_parser_template = v8::FunctionTemplate::New(isolate);
        dom_parser_template->PrototypeTemplate()->Set(
            js_string(isolate, "parseFromString"),
            v8::FunctionTemplate::New(isolate, dom_parser_parse));
        global->Set(local_context, js_string(isolate, "DOMParser"), dom_parser_template->GetFunction(local_context).ToLocalChecked()).Check();
        auto matrix_template = v8::FunctionTemplate::New(isolate, dom_matrix_constructor);
        global->Set(local_context, js_string(isolate, "DOMMatrix"), matrix_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "DOMMatrixReadOnly"), matrix_template->GetFunction(local_context).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "Image"),
            v8::FunctionTemplate::New(isolate, image_constructor)
                ->GetFunction(local_context).ToLocalChecked()).Check();

        const auto frame_path = std::string("/")
            + resource_root.filename().generic_string() + '/';
        const auto frame_href = std::string("http://127.0.0.1") + frame_path;
        auto location = v8::Object::New(isolate);
        location->Set(local_context, js_string(isolate, "href"), js_string(isolate, frame_href.c_str())).Check();
        location->Set(local_context, js_string(isolate, "protocol"), js_string(isolate, "http:")).Check();
        location->Set(local_context, js_string(isolate, "host"), js_string(isolate, "127.0.0.1")).Check();
        location->Set(local_context, js_string(isolate, "pathname"), js_string(isolate, frame_path.c_str())).Check();
        location->Set(local_context, js_string(isolate, "search"), js_string(isolate, "")).Check();
        location->Set(local_context, js_string(isolate, "hash"), js_string(isolate, "")).Check();
        global->Set(local_context, js_string(isolate, "location"), location).Check();

        auto navigator = v8::Object::New(isolate);
        navigator->Set(local_context, js_string(isolate, "userAgent"), js_string(isolate, "HtmlML.Native/V8")).Check();
        navigator->Set(local_context, js_string(isolate, "language"), js_string(isolate, "en-US")).Check();
        global->Set(local_context, js_string(isolate, "navigator"), navigator).Check();

        auto console = v8::Object::New(isolate);
        console->Set(local_context, js_string(isolate, "log"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 0)).ToLocalChecked()).Check();
        console->Set(local_context, js_string(isolate, "warn"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 1)).ToLocalChecked()).Check();
        console->Set(local_context, js_string(isolate, "error"),
            v8::Function::New(local_context, console_log, v8::Integer::New(isolate, 2)).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "console"), console).Check();

        auto performance = v8::Object::New(isolate);
        performance->Set(local_context, js_string(isolate, "now"), v8::Function::New(local_context, performance_now).ToLocalChecked()).Check();
        performance->Set(
            local_context,
            js_string(isolate, "getEntriesByName"),
            v8::Function::New(local_context, performance_get_entries_by_name).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "mark"),
            v8::Function::New(local_context, performance_entry).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "measure"),
            v8::Function::New(local_context, performance_entry).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "clearMarks"),
            v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        performance->Set(local_context, js_string(isolate, "clearMeasures"),
            v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        global->Set(local_context, js_string(isolate, "performance"), performance).Check();

        // React's scheduler prefers MessageChannel over timers. Keep the ports
        // realm-local and route delivery through this frame's setTimeout so a
        // posted message is a serialized browser task rather than a nested V8
        // call. This mirrors the working managed V8 iframe implementation.
        constexpr std::string_view message_channel_source = R"JS(
            (() => {
              class MessagePort {
                constructor() {
                  this.onmessage = null;
                  this._listeners = [];
                  this._peer = null;
                  this._closed = false;
                }
                postMessage(data) {
                  const target = this._peer;
                  if (this._closed || target === null || target._closed) return;
                  setTimeout(() => {
                    if (target._closed) return;
                    const event = {
                      type: 'message', data, target, currentTarget: target,
                      ports: [], origin: '', lastEventId: ''
                    };
                    if (typeof target.onmessage === 'function') {
                      target.onmessage.call(target, event);
                    }
                    for (const listener of target._listeners.slice()) {
                      listener.call(target, event);
                    }
                  }, 0);
                }
                addEventListener(type, listener) {
                  if (type === 'message' && typeof listener === 'function'
                      && !this._listeners.includes(listener)) {
                    this._listeners.push(listener);
                  }
                }
                removeEventListener(type, listener) {
                  if (type !== 'message') return;
                  const index = this._listeners.indexOf(listener);
                  if (index >= 0) this._listeners.splice(index, 1);
                }
                start() {}
                close() { this._closed = true; }
              }
              class MessageChannel {
                constructor() {
                  this.port1 = new MessagePort();
                  this.port2 = new MessagePort();
                  this.port1._peer = this.port2;
                  this.port2._peer = this.port1;
                }
              }
              class HtmlMLDOMException extends Error {
                constructor(message = '', name = 'Error') {
                  super(String(message));
                  this.name = String(name);
                  const legacyCodes = {
                    IndexSizeError: 1,
                    HierarchyRequestError: 3,
                    WrongDocumentError: 4,
                    InvalidCharacterError: 5,
                    NoModificationAllowedError: 7,
                    NotFoundError: 8,
                    NotSupportedError: 9,
                    InUseAttributeError: 10,
                    InvalidStateError: 11,
                    SyntaxError: 12,
                    InvalidModificationError: 13,
                    NamespaceError: 14,
                    InvalidAccessError: 15,
                    TypeMismatchError: 17,
                    SecurityError: 18,
                    NetworkError: 19,
                    AbortError: 20,
                    URLMismatchError: 21,
                    QuotaExceededError: 22,
                    TimeoutError: 23,
                    InvalidNodeTypeError: 24,
                    DataCloneError: 25
                  };
                  this.code = legacyCodes[this.name] || 0;
                }
              }
              class HtmlMLAbortSignal {
                constructor() {
                  this.aborted = false;
                  this.reason = undefined;
                  this.onabort = null;
                  this._listeners = [];
                }
                addEventListener(type, listener, options) {
                  if (type !== 'abort' || (typeof listener !== 'function'
                      && typeof listener?.handleEvent !== 'function')) return;
                  if (this._listeners.some(entry => entry.listener === listener)) return;
                  this._listeners.push({
                    listener,
                    once: options === true || Boolean(options?.once)
                  });
                }
                removeEventListener(type, listener) {
                  if (type !== 'abort') return;
                  const index = this._listeners.findIndex(entry => entry.listener === listener);
                  if (index >= 0) this._listeners.splice(index, 1);
                }
                throwIfAborted() {
                  if (this.aborted) throw this.reason;
                }
                _abort(reason) {
                  if (this.aborted) return;
                  this.aborted = true;
                  const error = reason === undefined
                    ? new HtmlMLDOMException('This operation was aborted', 'AbortError')
                    : reason;
                  this.reason = error;
                  const event = { type: 'abort', target: this, currentTarget: this };
                  if (typeof this.onabort === 'function') this.onabort.call(this, event);
                  for (const entry of this._listeners.slice()) {
                    if (typeof entry.listener === 'function') entry.listener.call(this, event);
                    else entry.listener.handleEvent(event);
                    if (entry.once) this.removeEventListener('abort', entry.listener);
                  }
                }
              }
              class HtmlMLAbortController {
                constructor() { this.signal = new HtmlMLAbortSignal(); }
                abort(reason) { this.signal._abort(reason); }
              }
              Object.defineProperties(globalThis, {
                MessagePort: { value: MessagePort, configurable: true },
                MessageChannel: { value: MessageChannel, configurable: true },
                DOMException: { value: HtmlMLDOMException, configurable: true },
                AbortSignal: { value: HtmlMLAbortSignal, configurable: true },
                AbortController: { value: HtmlMLAbortController, configurable: true },
                CSS: {
                  value: {
                    supports() { return false; },
                    escape(value) {
                      return String(value).replace(/[^a-zA-Z0-9_-]/g, character => `\\${character}`);
                    }
                  },
                  configurable: true
                },
                crypto: {
                  value: {
                    getRandomValues(array) {
                      if (!ArrayBuffer.isView(array) || array instanceof DataView) {
                        throw new TypeError('Expected an integer TypedArray');
                      }
                      for (let index = 0; index < array.length; index++) {
                        array[index] = Math.floor(Math.random() * 256);
                      }
                      return array;
                    }
                  },
                  configurable: true
                }
              });
            })();
        )JS";
        auto message_channel_script = v8::Script::Compile(
            local_context,
            js_string(isolate, std::string(message_channel_source).c_str())).ToLocalChecked();
        message_channel_script->Run(local_context).ToLocalChecked();
        static_cast<void>(frame_body);
    }

    bool hydrate_frame(dom_node& frame)
    {
        if (profile_startup && startup_frame_started == std::chrono::steady_clock::time_point{}) {
            startup_frame_started = std::chrono::steady_clock::now();
        }
        binding_callback_timer timer(profile_startup ? &startup_frame_hydrate : nullptr);
        const auto html_iterator = frame.attributes.find("frame-html");
        const auto object_iterator = frame.attributes.find("object-html");
        const auto& html = html_iterator != frame.attributes.end() && !html_iterator->second.empty()
            ? html_iterator->second
            : object_iterator != frame.attributes.end() ? object_iterator->second : empty_string;
        if (html.empty()) {
            frame_last_error_value = "Iframe document.close() received no HTML";
            ++frame_script_error_count;
            return false;
        }
        if (resource_root.empty()) {
            frame_last_error_value = "The connected component resource root was not configured";
            ++frame_script_error_count;
            return false;
        }

        css_rules.clear();
        css_rules_by_class.clear();
        css_rules_by_id.clear();
        css_rules_by_tag.clear();
        unindexed_css_rules.clear();
        css_variables.clear();
        for (const auto& href : parse_frame_stylesheets(html)) {
            std::string stylesheet;
            const auto path = resolve_resource_path(href);
            if (path.empty()) continue;
            if (!profiled_read_text_file(path, stylesheet)) {
                frame_last_error_value = "Unable to load iframe stylesheet: " + path.string();
                ++frame_script_error_count;
                return false;
            }
            add_stylesheet(std::move(stylesheet));
        }

        const auto opening_tag = [&](std::string_view name) {
            const auto open = html.find("<" + std::string(name));
            const auto close = open == std::string::npos ? std::string::npos : html.find('>', open);
            return open == std::string::npos || close == std::string::npos
                ? std::string{}
                : html.substr(open, close - open + 1U);
        };
        const auto html_opening = opening_tag("html");
        const auto body_opening = opening_tag("body");

        auto& frame_body = document.create_element("body");
        // A browsing context currently uses one native layout root for the
        // browser's HTML and BODY boxes. Preserve the authored state of both
        // elements on that root so `html[data-theme=dark]`, `body.chart-page`,
        // inherited custom properties, language, and direction all cascade.
        const auto html_class = extract_html_attribute(html_opening, "class");
        const auto body_class = extract_html_attribute(body_opening, "class");
        frame_body.class_name = html_class;
        if (!body_class.empty()) {
            if (!frame_body.class_name.empty()) frame_body.class_name.push_back(' ');
            frame_body.class_name += body_class;
        }
        if (!frame_body.class_name.empty()) frame_body.attributes["class"] = frame_body.class_name;
        for (const auto* attribute : {"data-theme", "lang", "dir"}) {
            auto value = extract_html_attribute(html_opening, attribute);
            if (value.empty()) value = extract_html_attribute(body_opening, attribute);
            if (!value.empty()) frame_body.attributes[attribute] = std::move(value);
        }
        frame_body.style.width = {100, length_unit::percent};
        frame_body.style.height = {100, length_unit::percent};
        frame_body.style.background_rgba = 0x131722FFU;
        apply_css_rules(frame_body);
        document.append_child(frame, frame_body);
        auto& loading = document.create_element("div");
        loading.id_attribute = "loading-indicator";
        loading.class_name = "loading-indicator";
        apply_css_rules(loading);
        document.append_child(frame_body, loading);

        auto frame_global_template = v8::ObjectTemplate::New(isolate);
        frame_global_template->SetHandler(v8::NamedPropertyHandlerConfiguration(
            get_window_named_property,
            nullptr,
            nullptr,
            nullptr,
            nullptr,
            {},
            v8::PropertyHandlerFlags::kNonMasking));
        auto local_frame_context = v8::Context::New(isolate, nullptr, frame_global_template);
        local_frame_context->SetSecurityToken(context.Get(isolate)->GetSecurityToken());
        local_frame_context->SetAlignedPointerInEmbedderData(
            1,
            &frame_body,
            v8::kEmbedderDataTypeTagDefault);
        frame_context.Reset(isolate, local_frame_context);
        install_frame_globals(local_frame_context, frame, frame_body);

        auto scripts = parse_frame_scripts(html);
        const auto execute_group = [&](bool defer) {
            for (const auto& script : scripts) {
                if (script.defer != defer) continue;
                std::string source = script.code;
                auto name = "htmlml-frame-inline-" + std::to_string(script.index) + ".js";
                if (!script.source.empty()) {
                    const auto path = resolve_resource_path(script.source);
                    name = path.string();
                    if (path.empty() || !profiled_read_text_file(path, source)) {
                        frame_last_error_value = "Unable to load iframe script: " + path.string();
                        ++frame_script_error_count;
                        return false;
                    }
                }
                {
                    v8::Context::Scope context_scope(local_frame_context);
                    auto document_value = local_frame_context->Global()->Get(
                        local_frame_context,
                        js_string(isolate, "document")).ToLocalChecked().As<v8::Object>();
                    auto current_script = v8::Object::New(isolate);
                    const auto script_url = script.source.empty()
                        ? name
                        : std::string("http://127.0.0.1/")
                            + resource_root.filename().generic_string() + '/' + script.source;
                    current_script->Set(
                        local_frame_context,
                        js_string(isolate, "src"),
                        js_string(isolate, script_url.c_str())).Check();
                    current_script->Set(
                        local_frame_context,
                        js_string(isolate, "tagName"),
                        js_string(isolate, "SCRIPT")).Check();
                    current_script->Set(
                        local_frame_context,
                        js_string(isolate, "nonce"),
                        js_string(isolate, "")).Check();
                    current_script->Set(
                        local_frame_context,
                        js_string(isolate, "getAttribute"),
                        v8::Function::New(local_frame_context, current_script_get_attribute).ToLocalChecked()).Check();
                    document_value->Set(
                        local_frame_context,
                        js_string(isolate, "currentScript"),
                        current_script).Check();
                }
                std::string error;
                if (!execute_in_context(local_frame_context, source, name, error)) {
                    frame_last_error_value = "Iframe script #" + std::to_string(script.index)
                        + " failed: " + error;
                    ++frame_script_error_count;
                    return false;
                }
                ++frame_script_execution_count;
            }
            return true;
        };
        if (!execute_group(false)) return false;
        {
            v8::Context::Scope context_scope(local_frame_context);
            auto document_value = local_frame_context->Global()->Get(
                local_frame_context,
                js_string(isolate, "document")).ToLocalChecked().As<v8::Object>();
            document_value->Set(
                local_frame_context,
                js_string(isolate, "readyState"),
                js_string(isolate, "interactive")).Check();
        }
        if (!execute_group(true)) return false;

        // Complete the parser lifecycle in the same ordering used by the
        // working managed iframe host. Deferred scripts observe interactive;
        // DOMContentLoaded runs before readyState=complete and Window load.
        // A checkpoint follows each event dispatch, after all listeners for
        // that event have run.
        v8::Context::Scope lifecycle_context_scope(local_frame_context);
        const auto dispatch_lifecycle_event = [&](v8::Local<v8::Object> target, const char* type) {
            auto event = v8::Object::New(isolate);
            event->Set(
                local_frame_context,
                js_string(isolate, "type"),
                js_string(isolate, type)).Check();
            event->Set(
                local_frame_context,
                js_string(isolate, "target"),
                target).Check();
            event->Set(
                local_frame_context,
                js_string(isolate, "currentTarget"),
                target).Check();
            v8::Local<v8::Value> arguments[] = {event};
            const auto listeners = frame_event_listeners.find(type);
            const auto listener_contexts = frame_event_listener_contexts.find(type);
            if (listeners != frame_event_listeners.end()) {
                auto* listener_list = &listeners->second;
                // Listener callbacks can register more listeners. Snapshot the
                // initial length, as EventTarget dispatch does, and reacquire
                // each persistent handle after returning from JavaScript.
                const auto listener_count = listener_list->size();
                for (size_t index = 0; index < listener_count; ++index) {
                    if (listener_contexts != frame_event_listener_contexts.end()
                        && index < listener_contexts->second.size()
                        && listener_contexts->second[index].Get(isolate)
                            != local_frame_context) continue;
                    auto callback = (*listener_list)[index].Get(isolate);
                    if (callback.IsEmpty()) continue;
                    v8::TryCatch try_catch(isolate);
                    if (callback->Call(
                            local_frame_context,
                            target,
                            1,
                            arguments).IsEmpty()) {
                        frame_last_error_value = std::string("Iframe ") + type
                            + " dispatch failed: " + describe_exception(try_catch, local_frame_context);
                        ++frame_script_error_count;
                        return false;
                    }
                }
            }
            isolate->PerformMicrotaskCheckpoint();
            return true;
        };
        auto frame_global = local_frame_context->Global();
        auto document_value = frame_global->Get(
            local_frame_context,
            js_string(isolate, "document")).ToLocalChecked().As<v8::Object>();
        if (!dispatch_lifecycle_event(document_value, "DOMContentLoaded")) return false;
        document_value->Set(
            local_frame_context,
            js_string(isolate, "readyState"),
            js_string(isolate, "complete")).Check();
        if (!dispatch_lifecycle_event(frame_global, "load")) return false;
        isolate->PerformMicrotaskCheckpoint();
        frame_last_error_value.clear();
        return true;
    }

    static void get_inner_html(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_property));
        info.GetReturnValue().Set(js_string(info.GetIsolate(), ""));
    }

    static std::string lower_html_name(std::string value)
    {
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
            return static_cast<char>(std::tolower(character));
        });
        return value;
    }

    static std::string decode_html_text(std::string value)
    {
        const std::pair<std::string_view, std::string_view> entities[] = {
            {"&lt;", "<"}, {"&gt;", ">"}, {"&amp;", "&"},
            {"&quot;", "\""}, {"&#39;", "'"}, {"&nbsp;", " "}};
        for (const auto& [encoded, decoded] : entities) {
            size_t cursor = 0;
            while ((cursor = value.find(encoded, cursor)) != std::string::npos) {
                value.replace(cursor, encoded.size(), decoded);
                cursor += decoded.size();
            }
        }
        return value;
    }

    static uint64_t inline_style_mask(std::string_view name)
    {
        if (name == "width") return inline_width;
        if (name == "height") return inline_height;
        if (name == "min-width" || name == "min-height"
            || name == "max-width" || name == "max-height") return inline_min_max_size;
        if (name == "left" || name == "inset-inline-start") return inline_left;
        if (name == "top" || name == "inset-block-start") return inline_top;
        if (name == "right" || name == "inset-inline-end") return inline_right;
        if (name == "bottom" || name == "inset-block-end") return inline_bottom;
        if (name == "display") return inline_display;
        if (name == "position") return inline_position;
        if (name == "flex-direction") return inline_flex_direction;
        if (name == "flex") return inline_flex_grow | inline_flex_shrink;
        if (name == "flex-grow") return inline_flex_grow;
        if (name == "flex-shrink") return inline_flex_shrink;
        if (name == "flex-wrap") return inline_flex_wrap;
        if (name == "background" || name == "background-color") return inline_background;
        if (name == "overflow" || name == "overflow-x" || name == "overflow-y") return inline_overflow;
        if (name == "visibility") return inline_visibility;
        if (name == "pointer-events") return inline_pointer_events;
        if (name == "color") return inline_color;
        if (name == "font-size") return inline_font_size;
        if (name == "font-family") return inline_font_family;
        if (name == "font-weight") return inline_font_weight;
        if (name == "line-height") return inline_line_height;
        if (name == "text-align") return inline_text_align;
        if (name == "white-space") return inline_white_space;
        if (name == "padding" || name.starts_with("padding-")) return inline_padding;
        if (name == "margin" || name.starts_with("margin-")) return inline_margin;
        if (name == "align-items") return inline_align_items;
        if (name == "align-self") return inline_align_self;
        if (name == "justify-content") return inline_justify_content;
        if (name == "box-sizing") return inline_box_sizing;
        if (name == "border-radius"
            || name == "border-top-left-radius" || name == "border-top-right-radius"
            || name == "border-bottom-right-radius" || name == "border-bottom-left-radius"
            || name == "border-start-start-radius" || name == "border-start-end-radius"
            || name == "border-end-end-radius" || name == "border-end-start-radius") {
            return inline_border_radius;
        }
        if (name == "transform") return inline_transform;
        if (name == "opacity") return inline_opacity;
        return 0U;
    }

    static size_t html_tag_end(const std::string& html, size_t open)
    {
        char quote = 0;
        for (auto cursor = open + 1U; cursor < html.size(); ++cursor) {
            const auto character = html[cursor];
            if (quote != 0) {
                if (character == quote) quote = 0;
            } else if (character == '\'' || character == '"') {
                quote = character;
            } else if (character == '>') {
                return cursor;
            }
        }
        return std::string::npos;
    }

    static std::vector<std::pair<std::string, std::string>> parse_html_attributes(
        std::string_view opening,
        size_t cursor)
    {
        std::vector<std::pair<std::string, std::string>> result;
        while (cursor < opening.size()) {
            while (cursor < opening.size()
                && std::isspace(static_cast<unsigned char>(opening[cursor]))) ++cursor;
            if (cursor >= opening.size() || opening[cursor] == '>' || opening[cursor] == '/') break;
            const auto name_begin = cursor;
            while (cursor < opening.size()
                && !std::isspace(static_cast<unsigned char>(opening[cursor]))
                && opening[cursor] != '=' && opening[cursor] != '>' && opening[cursor] != '/') ++cursor;
            auto name = std::string(opening.substr(name_begin, cursor - name_begin));
            while (cursor < opening.size()
                && std::isspace(static_cast<unsigned char>(opening[cursor]))) ++cursor;
            std::string value;
            if (cursor < opening.size() && opening[cursor] == '=') {
                ++cursor;
                while (cursor < opening.size()
                    && std::isspace(static_cast<unsigned char>(opening[cursor]))) ++cursor;
                if (cursor < opening.size() && (opening[cursor] == '\'' || opening[cursor] == '"')) {
                    const auto quote = opening[cursor++];
                    const auto value_begin = cursor;
                    while (cursor < opening.size() && opening[cursor] != quote) ++cursor;
                    value = std::string(opening.substr(value_begin, cursor - value_begin));
                    if (cursor < opening.size()) ++cursor;
                } else {
                    const auto value_begin = cursor;
                    while (cursor < opening.size()
                        && !std::isspace(static_cast<unsigned char>(opening[cursor]))
                        && opening[cursor] != '>') ++cursor;
                    value = std::string(opening.substr(value_begin, cursor - value_begin));
                }
            }
            if (!name.empty()) result.emplace_back(std::move(name), decode_html_text(std::move(value)));
        }
        return result;
    }

    void parse_inner_html(dom_node& parent, const std::string& html)
    {
        std::vector<dom_node*> stack{&parent};
        size_t cursor = 0;
        while (cursor < html.size()) {
            const auto open = html.find('<', cursor);
            if (open == std::string::npos) {
                const auto value = decode_html_text(html.substr(cursor));
                if (std::any_of(value.begin(), value.end(), [](unsigned char character) {
                    return !std::isspace(character);
                })) {
                    if (stack.back()->tag == "style") {
                        stack.back()->text_content += value;
                    } else {
                        auto& text = document.create_element("#text");
                        text.text_content = value;
                        document.append_child(*stack.back(), text);
                    }
                }
                break;
            }
            if (open > cursor) {
                const auto value = decode_html_text(html.substr(cursor, open - cursor));
                if (std::any_of(value.begin(), value.end(), [](unsigned char character) {
                    return !std::isspace(character);
                })) {
                    if (stack.back()->tag == "style") {
                        stack.back()->text_content += value;
                    } else {
                        auto& text = document.create_element("#text");
                        text.text_content = value;
                        document.append_child(*stack.back(), text);
                    }
                }
            }
            if (html.compare(open, 4U, "<!--") == 0) {
                const auto close = html.find("-->", open + 4U);
                cursor = close == std::string::npos ? html.size() : close + 3U;
                continue;
            }
            const auto close = html_tag_end(html, open);
            if (close == std::string::npos) break;
            if (open + 1U < html.size() && html[open + 1U] == '!') {
                cursor = close + 1U;
                continue;
            }
            if (open + 1U < html.size() && html[open + 1U] == '/') {
                auto name_begin = open + 2U;
                while (name_begin < close
                    && std::isspace(static_cast<unsigned char>(html[name_begin]))) ++name_begin;
                auto name_end = name_begin;
                while (name_end < close
                    && !std::isspace(static_cast<unsigned char>(html[name_end]))
                    && html[name_end] != '>') ++name_end;
                const auto name = lower_html_name(html.substr(name_begin, name_end - name_begin));
                while (stack.size() > 1U) {
                    const auto matched = stack.back()->tag == name;
                    if (matched && name == "style") {
                        std::string stylesheet = stack.back()->text_content;
                        for (const auto* child : stack.back()->children) {
                            if (child != nullptr && child->tag == "#text") {
                                stylesheet += child->text_content;
                            }
                        }
                        if (!stylesheet.empty()) add_stylesheet(std::move(stylesheet));
                        stack.back()->text_content.clear();
                    }
                    stack.pop_back();
                    if (matched) break;
                }
                cursor = close + 1U;
                continue;
            }

            auto name_begin = open + 1U;
            while (name_begin < close
                && std::isspace(static_cast<unsigned char>(html[name_begin]))) ++name_begin;
            auto name_end = name_begin;
            while (name_end < close
                && !std::isspace(static_cast<unsigned char>(html[name_end]))
                && html[name_end] != '/' && html[name_end] != '>') ++name_end;
            const auto name = lower_html_name(html.substr(name_begin, name_end - name_begin));
            if (name.empty()) {
                cursor = close + 1U;
                continue;
            }

            auto& node = document.create_element(name);
            const auto svg = name == "svg"
                || stack.back()->tag == "svg"
                || stack.back()->attributes.contains("namespace");
            if (svg) node.attributes["namespace"] = "http://www.w3.org/2000/svg";
            for (auto [attribute_name, value] : parse_html_attributes(
                std::string_view(html).substr(open, close - open + 1U),
                name_end - open)) {
                const auto lower_name = lower_html_name(attribute_name);
                if (lower_name == "class") node.class_name = value;
                else if (lower_name == "id") node.id_attribute = value;
                else {
                    if (lower_name == "viewbox") attribute_name = "viewBox";
                    node.attributes[attribute_name] = value;
                }
            }
            document.append_child(*stack.back(), node);
            apply_css_rules(node);
            if (const auto style = node.attributes.find("style"); style != node.attributes.end()) {
                for (const auto& declaration : parse_css_declarations(style->second)) {
                    apply_css_declaration(node, declaration);
                    node.style.inline_property_mask |= inline_style_mask(declaration.name);
                }
            }
            if (name == "iframe") {
                const auto source = node.attributes.find("src");
                if (source != node.attributes.end()) {
                    const auto object_url = object_urls.find(source->second);
                    if (object_url != object_urls.end()) node.attributes["object-html"] = object_url->second;
                }
                node.style.width = {100, length_unit::percent};
                node.style.height = {100, length_unit::percent};
                node.style.background_rgba = 0x131722FFU;
                node.style.inline_property_mask |= inline_width | inline_height | inline_background;
                if (node.attributes.contains("object-html")) pending_frame_hydrations.push_back(&node);
            }

            const auto self_closing = close > open && html[close - 1U] == '/';
            const auto void_element = name == "area" || name == "base" || name == "br"
                || name == "col" || name == "embed" || name == "hr" || name == "img"
                || name == "input" || name == "link" || name == "meta" || name == "param"
                || name == "source" || name == "track" || name == "wbr";
            if (!self_closing && !void_element) stack.push_back(&node);
            cursor = close + 1U;
        }
        document.mark_dirty();
    }

    static void set_inner_html(
        v8::Local<v8::Name>,
        v8::Local<v8::Value> raw_value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_mutation));
        auto* parent = unwrap_node(info.Holder());
        if (parent == nullptr) return;
        const auto html = to_utf8(info.GetIsolate(), raw_value);
        self->document.remove_all_children(*parent);
        if (!html.empty()) self->parse_inner_html(*parent, html);
    }

    static void get_content_window(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || node->tag != "iframe" || node->parent == nullptr) {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
            return;
        }
        const auto actual = self->actual_frame_windows.find(node->id);
        if (actual != self->actual_frame_windows.end()) {
            info.GetReturnValue().Set(actual->second.Get(info.GetIsolate()));
            return;
        }
        info.GetReturnValue().Set(self->frame_window(*node));
    }

    static void get_content_document(
        v8::Local<v8::Name>,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr || node->tag != "iframe" || node->parent == nullptr) {
            info.GetReturnValue().Set(v8::Null(info.GetIsolate()));
            return;
        }
        const auto actual = self->actual_frame_documents.find(node->id);
        if (actual != self->actual_frame_documents.end()) {
            info.GetReturnValue().Set(actual->second.Get(info.GetIsolate()));
            return;
        }
        info.GetReturnValue().Set(self->frame_document(*node));
    }

    static void frame_document_open(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.This());
        if (node != nullptr) node->attributes["frame-html"].clear();
    }

    static void frame_document_write(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* node = unwrap_node(info.This());
        if (node != nullptr && info.Length() > 0) {
            node->attributes["frame-html"] += to_utf8(info.GetIsolate(), info[0]);
        }
    }

    static void frame_document_close(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node != nullptr) self->pending_frame_hydrations.push_back(node);
    }

    static void frame_window_add_event_listener(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node == nullptr || info.Length() < 2 || !info[1]->IsFunction()) return;
        const auto type = to_utf8(info.GetIsolate(), info[0]);
        if (type == "innerWindowLoad") {
            self->frame_load_listeners[node->id].Reset(info.GetIsolate(), info[1].As<v8::Function>());
        }
    }

    static bool clear_inline_style(dom_node& node, const std::string& name)
    {
        if (name == "width") {
            node.style.width = {};
            node.style.inline_property_mask &= ~inline_width;
        } else if (name == "height") {
            node.style.height = {};
            node.style.inline_property_mask &= ~inline_height;
        } else if (name == "minWidth" || name == "min-width") {
            node.style.min_width = {};
            node.style.inline_property_mask &= ~inline_min_max_size;
        } else if (name == "minHeight" || name == "min-height") {
            node.style.min_height = {};
            node.style.inline_property_mask &= ~inline_min_max_size;
        } else if (name == "maxWidth" || name == "max-width") {
            node.style.max_width = {};
            node.style.inline_property_mask &= ~inline_min_max_size;
        } else if (name == "maxHeight" || name == "max-height") {
            node.style.max_height = {};
            node.style.inline_property_mask &= ~inline_min_max_size;
        } else if (name == "left") {
            node.style.left = {};
            node.style.inline_property_mask &= ~inline_left;
        } else if (name == "top") {
            node.style.top = {};
            node.style.inline_property_mask &= ~inline_top;
        } else if (name == "right") {
            node.style.right = {};
            node.style.inline_property_mask &= ~inline_right;
        } else if (name == "bottom") {
            node.style.bottom = {};
            node.style.inline_property_mask &= ~inline_bottom;
        } else if (name == "display") {
            node.style.display = default_display_for_tag(node.tag);
            node.style.inline_property_mask &= ~inline_display;
        } else if (name == "position") {
            node.style.position = position_mode::normal;
            node.style.inline_property_mask &= ~inline_position;
        } else if (name == "flexDirection" || name == "flex-direction") {
            node.style.direction = flex_direction::row;
            node.style.flex_reverse = false;
            node.style.inline_property_mask &= ~inline_flex_direction;
        } else if (name == "flexGrow" || name == "flex-grow" || name == "flex") {
            node.style.flex_grow = 0;
            node.style.inline_property_mask &= ~inline_flex_grow;
            if (name == "flex") {
                node.style.flex_shrink = 1;
                node.style.inline_property_mask &= ~inline_flex_shrink;
            }
        } else if (name == "flexShrink" || name == "flex-shrink") {
            node.style.flex_shrink = 1;
            node.style.inline_property_mask &= ~inline_flex_shrink;
        } else if (name == "flexWrap" || name == "flex-wrap") {
            node.style.flex_wrap = false;
            node.style.inline_property_mask &= ~inline_flex_wrap;
        } else if (name == "alignItems" || name == "align-items") {
            node.style.align_items = align_mode::stretch;
            node.style.inline_property_mask &= ~inline_align_items;
        } else if (name == "alignSelf" || name == "align-self") {
            node.style.align_self = align_mode::stretch;
            node.style.align_self_specified = false;
            node.style.inline_property_mask &= ~inline_align_self;
        } else if (name == "justifyContent" || name == "justify-content") {
            node.style.justify_content = justify_mode::start;
            node.style.inline_property_mask &= ~inline_justify_content;
        } else if (name == "boxSizing" || name == "box-sizing") {
            node.style.border_box = false;
            node.style.inline_property_mask &= ~inline_box_sizing;
        } else if (name == "borderRadius" || name == "border-radius") {
            node.style.border_top_left_radius = {};
            node.style.border_top_right_radius = {};
            node.style.border_bottom_right_radius = {};
            node.style.border_bottom_left_radius = {};
            node.style.inline_property_mask &= ~inline_border_radius;
        } else if (name == "transform") {
            node.style.transform_translate_x = {};
            node.style.transform_translate_y = {};
            node.style.transform_scale_x = 1;
            node.style.transform_scale_y = 1;
            node.style.transform_rotate_degrees = 0;
            node.style.transform_specified = false;
            node.style.inline_property_mask &= ~inline_transform;
        } else if (name == "transformOrigin" || name == "transform-origin") {
            node.style.transform_origin_x = {50, length_unit::percent};
            node.style.transform_origin_y = {50, length_unit::percent};
            node.style.inline_property_mask &= ~inline_transform;
        } else if (name == "opacity") {
            node.style.opacity = 1;
            node.style.inline_property_mask &= ~inline_opacity;
        } else if (name == "background" || name == "backgroundColor"
            || name == "background-color") {
            node.style.background_rgba = 0;
            node.style.inline_property_mask &= ~inline_background;
        } else if (name == "overflow") {
            node.style.clip = false;
            node.style.inline_property_mask &= ~inline_overflow;
        } else if (name == "visibility") {
            node.style.visibility_hidden = false;
            node.style.visibility_specified = false;
            node.style.inline_property_mask &= ~inline_visibility;
        } else if (name == "pointerEvents" || name == "pointer-events") {
            node.style.pointer_events_none = false;
            node.style.pointer_events_specified = false;
            node.style.inline_property_mask &= ~inline_pointer_events;
        } else if (name == "color") {
            node.style.foreground_rgba = 0;
            node.style.inline_property_mask &= ~inline_color;
        } else if (name == "fontSize" || name == "font-size") {
            node.style.font_size = -1;
            node.style.inline_property_mask &= ~inline_font_size;
        } else if (name == "fontFamily" || name == "font-family") {
            node.style.font_family.clear();
            node.style.inline_property_mask &= ~inline_font_family;
        } else if (name == "fontWeight" || name == "font-weight") {
            node.style.font_weight = 0;
            node.style.inline_property_mask &= ~inline_font_weight;
        } else if (name == "lineHeight" || name == "line-height") {
            node.style.line_height = -1;
            node.style.inline_property_mask &= ~inline_line_height;
        } else if (name == "textAlign" || name == "text-align") {
            node.style.text_align.clear();
            node.style.inline_property_mask &= ~inline_text_align;
        } else if (name == "whiteSpace" || name == "white-space") {
            node.style.white_space.clear();
            node.style.inline_property_mask &= ~inline_white_space;
        } else {
            return false;
        }
        return true;
    }

    static void style_set_property(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 2) return;
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::style));
        auto* node = unwrap_node(info.This());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        const auto property_mask = inline_style_mask(name);
        const auto has_important_rule =
            (node->style.important_property_mask & property_mask) != 0U;
        const auto value = to_utf8(info.GetIsolate(), info[1]);
        if (name.starts_with("--")) {
            if (value.empty()) node->style.custom_properties.erase(name);
            else node->style.custom_properties[name] = value;
            self->recascade_connected_subtree(*node);
            return;
        }
        if (value.empty()) {
            clear_inline_style(*node, name);
            self->recascade_connected_subtree(*node);
            return;
        }
        if (name == "width") {
            node->style.width = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_width;
        } else if (name == "height") {
            node->style.height = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_height;
        } else if (name == "minWidth") {
            node->style.min_width = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "minHeight") {
            node->style.min_height = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "maxWidth") {
            node->style.max_width = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "maxHeight") {
            node->style.max_height = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "min-width") {
            node->style.min_width = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "min-height") {
            node->style.min_height = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "max-width") {
            node->style.max_width = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "max-height") {
            node->style.max_height = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "position") {
            node->style.position = value == "absolute" ? position_mode::absolute
                : value == "fixed" ? position_mode::fixed
                : value == "relative" ? position_mode::relative
                : position_mode::normal;
            node->style.inline_property_mask |= inline_position;
        } else if (name == "display") {
            node->style.display = value == "flex" ? display_mode::flex
                : value == "inline-flex" ? display_mode::inline_flex
                : value == "grid" ? display_mode::grid
                : value == "inline-grid" ? display_mode::inline_grid
                : value == "inline" || value == "inline-block" ? display_mode::inline_block
                : value == "none" ? display_mode::none : display_mode::block;
            node->style.inline_property_mask |= inline_display;
        } else if (name == "flex-direction") {
            node->style.direction = value == "row" || value == "row-reverse"
                ? flex_direction::row : flex_direction::column;
            node->style.flex_reverse = value == "row-reverse" || value == "column-reverse";
            node->style.inline_property_mask |= inline_flex_direction;
        } else if (name == "flex-grow") {
            node->style.flex_grow = std::max(0.0F, std::strtof(value.c_str(), nullptr));
            node->style.inline_property_mask |= inline_flex_grow;
        } else if (name == "flex-shrink") {
            node->style.flex_shrink = std::max(0.0F, std::strtof(value.c_str(), nullptr));
            node->style.inline_property_mask |= inline_flex_shrink;
        } else if (name == "flex-wrap") {
            node->style.flex_wrap = value == "wrap" || value == "wrap-reverse";
            node->style.inline_property_mask |= inline_flex_wrap;
        } else if (name == "align-items") {
            node->style.align_items = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
            node->style.inline_property_mask |= inline_align_items;
        } else if (name == "align-self") {
            node->style.align_self_specified = value != "auto";
            node->style.align_self = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
            node->style.inline_property_mask |= inline_align_self;
        } else if (name == "justify-content") {
            node->style.justify_content = value == "center" ? justify_mode::center
                : value == "flex-end" || value == "end" ? justify_mode::end
                : value == "space-between" ? justify_mode::space_between
                : justify_mode::start;
            node->style.inline_property_mask |= inline_justify_content;
        } else if (name == "box-sizing") {
            node->style.border_box = value == "border-box";
            node->style.inline_property_mask |= inline_box_sizing;
        } else if (name == "border-radius") {
            apply_corner_radius_declaration(
                name,
                value,
                node->style.border_top_left_radius,
                node->style.border_top_right_radius,
                node->style.border_bottom_right_radius,
                node->style.border_bottom_left_radius);
            node->style.inline_property_mask |= inline_border_radius;
        } else if (name == "transform") {
            native_document::parse_transform_translate(
                value,
                node->style.transform_translate_x,
                node->style.transform_translate_y,
                node->style.transform_scale_x,
                node->style.transform_scale_y,
                node->style.transform_rotate_degrees);
            node->style.transform_specified = true;
            node->style.inline_property_mask |= inline_transform;
        } else if (name == "transform-origin") {
            std::istringstream stream(value);
            std::string x;
            std::string y;
            stream >> x >> y;
            node->style.transform_origin_x = native_document::parse_length(x);
            node->style.transform_origin_y = native_document::parse_length(y.empty() ? x : y);
            node->style.inline_property_mask |= inline_transform;
        } else if (name == "opacity") {
            node->style.opacity = std::clamp(std::strtof(value.c_str(), nullptr), 0.0F, 1.0F);
            node->style.inline_property_mask |= inline_opacity;
        } else if (name == "visibility") {
            node->style.visibility_hidden = value == "hidden" || value == "collapse";
            node->style.visibility_specified = value != "inherit" && value != "unset";
            node->style.inline_property_mask |= inline_visibility;
        } else if (name == "pointer-events") {
            node->style.pointer_events_none = value == "none";
            node->style.pointer_events_specified = value != "inherit" && value != "unset";
            node->style.inline_property_mask |= inline_pointer_events;
        }
        else if (name == "background" || name == "background-color") {
            node->style.background_rgba = native_document::parse_color(value);
            node->style.inline_property_mask |= inline_background;
        } else if (name == "color") {
            node->style.foreground_rgba = native_document::parse_color(value);
            node->style.inline_property_mask |= inline_color;
        } else if (name == "font-size") {
            node->style.font_size = std::max(0.0F, native_document::parse_length(value).value);
            node->style.inline_property_mask |= inline_font_size;
        } else if (name == "font-family") {
            node->style.font_family = value;
            node->style.inline_property_mask |= inline_font_family;
        } else if (name == "font-weight") {
            node->style.font_weight = value == "bold" ? 700
                : value == "normal" ? 400
                : std::max(1, static_cast<int>(std::lround(
                    native_document::parse_length(value).value)));
            node->style.inline_property_mask |= inline_font_weight;
        } else if (name == "line-height") {
            const auto parsed = std::max(0.0F, native_document::parse_length(value).value);
            node->style.line_height = parsed > 0 && parsed <= 4
                ? parsed * (node->style.font_size >= 0 ? node->style.font_size : 14.0F)
                : parsed;
            node->style.inline_property_mask |= inline_line_height;
        } else if (name == "text-align") {
            node->style.text_align = value;
            node->style.inline_property_mask |= inline_text_align;
        } else if (name == "white-space") {
            node->style.white_space = value;
            node->style.inline_property_mask |= inline_white_space;
        }
        if (has_important_rule) self->apply_css_rules(*node);
        else self->document.mark_dirty();
    }

    static void style_get_property(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 1) {
            info.GetReturnValue().Set(js_string(info.GetIsolate(), ""));
            return;
        }
        auto* node = unwrap_node(info.This());
        if (node == nullptr) {
            info.GetReturnValue().Set(js_string(info.GetIsolate(), ""));
            return;
        }
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        const auto computed = info.This()->InternalFieldCount() > 1
            && info.This()->GetInternalField(1).As<v8::Value>()->BooleanValue(info.GetIsolate());
        if (computed) current(info.GetIsolate())->ensure_layout();
        std::string value;
        if (name.starts_with("--")) {
            const auto known = node->style.custom_properties.find(name);
            value = known == node->style.custom_properties.end() ? "" : known->second;
        } else {
            const auto format_length = [](const css_length& length) {
                if (length.unit == length_unit::automatic) return std::string {};
                std::ostringstream stream;
                stream << length.value;
                stream << (length.unit == length_unit::percent ? "%" : "px");
                return stream.str();
            };
            const auto format_pixels = [](float pixels) {
                std::ostringstream stream;
                stream << pixels << "px";
                return stream.str();
            };
            if (name == "overflow" || name == "overflow-x" || name == "overflow-y") {
                // CSSOM returns the computed initial value rather than an
                // absent/undefined value. Modal scroll-lock implementations
                // unconditionally calls toLowerCase() on this result.
                value = node->style.clip ? "hidden" : "visible";
            } else if (name == "padding-left") {
                value = format_length(node->style.padding_left);
            } else if (name == "padding-top") {
                value = format_length(node->style.padding_top);
            } else if (name == "padding-right") {
                value = format_length(node->style.padding_right);
            } else if (name == "padding-bottom") {
                value = format_length(node->style.padding_bottom);
            } else if (name == "margin-left") {
                value = format_length(node->style.margin_left);
            } else if (name == "margin-top") {
                value = format_length(node->style.margin_top);
            } else if (name == "margin-right") {
                value = format_length(node->style.margin_right);
            } else if (name == "margin-bottom") {
                value = format_length(node->style.margin_bottom);
            } else if (name == "width") {
                value = computed ? format_pixels(node->layout.width) : format_length(node->style.width);
            } else if (name == "height") {
                value = computed ? format_pixels(node->layout.height) : format_length(node->style.height);
            } else if (name == "max-width") {
                value = node->style.max_width.value == 0
                    ? "none" : format_length(node->style.max_width);
            } else if (name == "max-height") {
                value = node->style.max_height.value == 0
                    ? "none" : format_length(node->style.max_height);
            } else if (name == "transform") {
                if (node->style.transform_specified) {
                    constexpr double degrees_to_radians = 0.017453292519943295;
                    const auto radians = static_cast<double>(node->style.transform_rotate_degrees)
                        * degrees_to_radians;
                    const auto cosine = std::cos(radians);
                    const auto sine = std::sin(radians);
                    const auto resolve = [](css_length length, float available) {
                        return length.unit == length_unit::percent
                            ? static_cast<double>(available) * length.value / 100.0
                            : static_cast<double>(length.value);
                    };
                    const auto format_number = [](double number) {
                        if (std::abs(number) < 0.000001) number = 0;
                        std::ostringstream stream;
                        stream << std::setprecision(8) << number;
                        return stream.str();
                    };
                    const auto a = cosine * node->style.transform_scale_x;
                    const auto b = sine * node->style.transform_scale_x;
                    const auto c = -sine * node->style.transform_scale_y;
                    const auto d = cosine * node->style.transform_scale_y;
                    const auto e = resolve(node->style.transform_translate_x, node->layout.width);
                    const auto f = resolve(node->style.transform_translate_y, node->layout.height);
                    value = "matrix(" + format_number(a) + ", " + format_number(b)
                        + ", " + format_number(c) + ", " + format_number(d)
                        + ", " + format_number(e) + ", " + format_number(f) + ")";
                } else {
                    value = "none";
                }
            } else if (name == "white-space") {
                value = node->style.white_space;
            }
        }
        // CSSStyleDeclaration.getPropertyValue() returns the empty string for
        // unsupported or unset declarations; it must never leak `undefined`.
        info.GetReturnValue().Set(js_string(info.GetIsolate(), value.c_str()));
    }

    static void style_remove_property(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 1) return;
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::style));
        auto* node = unwrap_node(info.This());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), info[0]);
        if (name.starts_with("--")) {
            node->style.custom_properties.erase(name);
            self->recascade_connected_subtree(*node);
            return;
        }
        clear_inline_style(*node, name);
        self->recascade_connected_subtree(*node);
    }

    static void get_style_property(
        v8::Local<v8::Name> property,
        const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::style));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), property);
        const auto computed = info.Holder()->InternalFieldCount() > 1
            && info.Holder()->GetInternalField(1).As<v8::Value>()->BooleanValue(info.GetIsolate());
        if (computed) self->ensure_layout();
        std::string value;
        const auto format_length = [](const css_length& length) {
            if (length.unit == length_unit::automatic) return std::string {};
            std::ostringstream stream;
            stream << length.value;
            stream << (length.unit == length_unit::percent ? "%" : "px");
            return stream.str();
        };
        const auto format_pixels = [](float pixels) {
            std::ostringstream stream;
            stream << pixels << "px";
            return stream.str();
        };
        const auto serialize_color = [](uint32_t color) {
            std::ostringstream stream;
            const auto red = static_cast<unsigned>((color >> 24U) & 0xFFU);
            const auto green = static_cast<unsigned>((color >> 16U) & 0xFFU);
            const auto blue = static_cast<unsigned>((color >> 8U) & 0xFFU);
            const auto alpha = static_cast<unsigned>(color & 0xFFU);
            if (alpha == 0xFFU) {
                stream << "rgb(" << red << ", " << green << ", " << blue << ')';
            } else {
                stream << "rgba(" << red << ", " << green << ", " << blue << ", "
                    << std::setprecision(6) << static_cast<double>(alpha) / 255.0 << ')';
            }
            return stream.str();
        };
        if (name == "width") {
            value = computed ? format_pixels(node->layout.width) : format_length(node->style.width);
        } else if (name == "height") {
            value = computed ? format_pixels(node->layout.height) : format_length(node->style.height);
        } else if (name == "display") {
            value = node->style.display == display_mode::flex ? "flex"
                : node->style.display == display_mode::inline_flex ? "inline-flex"
                : node->style.display == display_mode::grid ? "grid"
                : node->style.display == display_mode::inline_grid ? "inline-grid"
                : node->style.display == display_mode::inline_block ? "inline-block"
                : node->style.display == display_mode::none ? "none" : "block";
        } else if (name == "position") {
            value = node->style.position == position_mode::absolute ? "absolute"
                : node->style.position == position_mode::fixed ? "fixed"
                : node->style.position == position_mode::relative ? "relative"
                : "static";
        } else if (name == "flexDirection") {
            value = node->style.direction == flex_direction::row
                ? node->style.flex_reverse ? "row-reverse" : "row"
                : node->style.flex_reverse ? "column-reverse" : "column";
        } else if (name == "flexGrow") {
            value = std::to_string(node->style.flex_grow);
        } else if (name == "flexShrink") {
            value = std::to_string(node->style.flex_shrink);
        } else if (name == "flexWrap") {
            value = node->style.flex_wrap ? "wrap" : "nowrap";
        } else if (name == "alignItems") {
            value = node->style.align_items == align_mode::center ? "center"
                : node->style.align_items == align_mode::start ? "flex-start"
                : node->style.align_items == align_mode::end ? "flex-end" : "stretch";
        } else if (name == "alignSelf") {
            value = !node->style.align_self_specified ? "auto"
                : node->style.align_self == align_mode::center ? "center"
                : node->style.align_self == align_mode::start ? "flex-start"
                : node->style.align_self == align_mode::end ? "flex-end" : "stretch";
        } else if (name == "justifyContent") {
            value = node->style.justify_content == justify_mode::center ? "center"
                : node->style.justify_content == justify_mode::end ? "flex-end"
                : node->style.justify_content == justify_mode::space_between
                    ? "space-between" : "flex-start";
        } else if (name == "boxSizing") {
            value = node->style.border_box ? "border-box" : "content-box";
        } else if (name == "borderRadius") {
            value = std::to_string(node->style.border_top_left_radius.value) + "px";
        } else if (name == "transform") {
            const auto format_length = [](css_length length) {
                return std::to_string(length.value)
                    + (length.unit == length_unit::percent ? "%" : "px");
            };
            value = "translate(" + format_length(node->style.transform_translate_x)
                + ", " + format_length(node->style.transform_translate_y) + ") rotate("
                + std::to_string(node->painted_transform_rotate_degrees) + "deg)";
        } else if (name == "zIndex") {
            value = std::to_string(node->style.z_index);
        } else if (name == "opacity") {
            std::ostringstream stream;
            stream << std::setprecision(6) << node->style.opacity;
            value = stream.str();
        } else if (name == "background" || name == "backgroundColor") {
            value = serialize_color(node->style.background_rgba);
        } else if (name == "borderTopWidth") {
            value = std::to_string(node->style.border_top_width.value) + "px";
        } else if (name == "borderTopColor") {
            const auto color = node->style.border_top_rgba;
            char buffer[10]{};
            std::snprintf(buffer, sizeof(buffer), "#%02x%02x%02x",
                static_cast<unsigned>((color >> 24U) & 0xFFU),
                static_cast<unsigned>((color >> 16U) & 0xFFU),
                static_cast<unsigned>((color >> 8U) & 0xFFU));
            value = buffer;
        } else if (name == "overflow") {
            value = node->style.clip ? "hidden" : "visible";
        } else if (name == "color") {
            auto color = node->style.foreground_rgba;
            for (auto* ancestor = node->parent; (color & 0xFFU) == 0U && ancestor != nullptr;
                ancestor = ancestor->parent) {
                color = ancestor->style.foreground_rgba;
            }
            if ((color & 0xFFU) == 0U) color = 0x000000FFU;
            value = serialize_color(color);
        } else if (name == "fontSize") {
            value = std::to_string(node->style.font_size) + "px";
        } else if (name == "fontFamily") {
            value = node->style.font_family;
        } else if (name == "fontWeight") {
            value = std::to_string(node->style.font_weight);
        } else if (name == "lineHeight") {
            value = std::to_string(node->style.line_height) + "px";
        } else if (name == "textAlign") {
            value = node->style.text_align;
        } else if (name == "whiteSpace") {
            value = node->style.white_space;
        } else if (name == "visibility") {
            value = node->style.visibility_hidden ? "hidden" : "visible";
        } else if (name == "pointerEvents") {
            const auto* current = node;
            while (current != nullptr && !current->style.pointer_events_specified) {
                current = current->parent;
            }
            value = current != nullptr && current->style.pointer_events_none ? "none" : "auto";
        }
        info.GetReturnValue().Set(js_string(info.GetIsolate(), value.c_str()));
    }

    static void set_style_property(
        v8::Local<v8::Name> property,
        v8::Local<v8::Value> raw_value,
        const v8::PropertyCallbackInfo<void>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::style));
        auto* node = unwrap_node(info.Holder());
        if (node == nullptr) return;
        const auto name = to_utf8(info.GetIsolate(), property);
        const auto property_mask = inline_style_mask(name);
        const auto has_important_rule =
            (node->style.important_property_mask & property_mask) != 0U;
        const auto value = to_utf8(info.GetIsolate(), raw_value);
        if (value.empty()) {
            clear_inline_style(*node, name);
            self->recascade_connected_subtree(*node);
            return;
        }
        if (name == "width") {
            node->style.width = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_width;
        } else if (name == "height") {
            node->style.height = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_height;
        } else if (name == "minWidth") {
            node->style.min_width = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "minHeight") {
            node->style.min_height = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "maxWidth") {
            node->style.max_width = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "maxHeight") {
            node->style.max_height = value == "none" ? css_length{} : native_document::parse_length(value);
            node->style.inline_property_mask |= inline_min_max_size;
        } else if (name == "left") {
            node->style.left = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_left;
        } else if (name == "top") {
            node->style.top = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_top;
        } else if (name == "right") {
            node->style.right = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_right;
        } else if (name == "bottom") {
            node->style.bottom = native_document::parse_length(value);
            node->style.inline_property_mask |= inline_bottom;
        }
        else if (name == "display") {
            node->style.display = value == "flex" ? display_mode::flex
                : value == "inline-flex" ? display_mode::inline_flex
                : value == "grid" ? display_mode::grid
                : value == "inline-grid" ? display_mode::inline_grid
                : value == "inline" || value == "inline-block" ? display_mode::inline_block
                : value == "none" ? display_mode::none : display_mode::block;
            node->style.inline_property_mask |= inline_display;
        } else if (name == "position") {
            node->style.position = value == "absolute" ? position_mode::absolute
                : value == "fixed" ? position_mode::fixed
                : value == "relative" ? position_mode::relative
                : position_mode::normal;
            node->style.inline_property_mask |= inline_position;
        } else if (name == "flexDirection") {
            node->style.direction = value == "row" || value == "row-reverse"
                ? flex_direction::row : flex_direction::column;
            node->style.flex_reverse = value == "row-reverse" || value == "column-reverse";
            node->style.inline_property_mask |= inline_flex_direction;
        } else if (name == "flexGrow") {
            node->style.flex_grow = static_cast<float>(raw_value->NumberValue(info.GetIsolate()->GetCurrentContext()).FromMaybe(0));
            node->style.inline_property_mask |= inline_flex_grow;
        } else if (name == "flexShrink") {
            node->style.flex_shrink = std::max(
                0.0F,
                static_cast<float>(raw_value->NumberValue(
                    info.GetIsolate()->GetCurrentContext()).FromMaybe(1)));
            node->style.inline_property_mask |= inline_flex_shrink;
        } else if (name == "flexWrap") {
            node->style.flex_wrap = value == "wrap" || value == "wrap-reverse";
            node->style.inline_property_mask |= inline_flex_wrap;
        } else if (name == "alignItems") {
            node->style.align_items = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
            node->style.inline_property_mask |= inline_align_items;
        } else if (name == "alignSelf") {
            node->style.align_self_specified = value != "auto";
            node->style.align_self = value == "center" ? align_mode::center
                : value == "flex-start" || value == "start" ? align_mode::start
                : value == "flex-end" || value == "end" ? align_mode::end
                : align_mode::stretch;
            node->style.inline_property_mask |= inline_align_self;
        } else if (name == "justifyContent") {
            node->style.justify_content = value == "center" ? justify_mode::center
                : value == "flex-end" || value == "end" ? justify_mode::end
                : value == "space-between" ? justify_mode::space_between
                : justify_mode::start;
            node->style.inline_property_mask |= inline_justify_content;
        } else if (name == "boxSizing") {
            node->style.border_box = value == "border-box";
            node->style.inline_property_mask |= inline_box_sizing;
        } else if (name == "borderRadius") {
            apply_corner_radius_declaration(
                "border-radius",
                value,
                node->style.border_top_left_radius,
                node->style.border_top_right_radius,
                node->style.border_bottom_right_radius,
                node->style.border_bottom_left_radius);
            node->style.inline_property_mask |= inline_border_radius;
        } else if (name == "transform") {
            native_document::parse_transform_translate(
                value,
                node->style.transform_translate_x,
                node->style.transform_translate_y,
                node->style.transform_scale_x,
                node->style.transform_scale_y,
                node->style.transform_rotate_degrees);
            node->style.transform_specified = true;
            node->style.inline_property_mask |= inline_transform;
        } else if (name == "transformOrigin") {
            std::istringstream stream(value);
            std::string x;
            std::string y;
            stream >> x >> y;
            node->style.transform_origin_x = native_document::parse_length(x);
            node->style.transform_origin_y = native_document::parse_length(y.empty() ? x : y);
            node->style.inline_property_mask |= inline_transform;
        } else if (name == "opacity") {
            node->style.opacity = raw_value->IsNullOrUndefined()
                ? 1.0F
                : std::clamp(std::strtof(value.c_str(), nullptr), 0.0F, 1.0F);
            node->style.inline_property_mask |= inline_opacity;
        } else if (name == "background" || name == "backgroundColor") {
            node->style.background_rgba = native_document::parse_color(value);
            node->style.inline_property_mask |= inline_background;
        } else if (name == "overflow") {
            node->style.clip = value == "hidden" || value == "clip"
                || value == "auto" || value == "scroll";
            node->style.inline_property_mask |= inline_overflow;
        } else if (name == "visibility") {
            node->style.visibility_hidden = value == "hidden" || value == "collapse";
            node->style.visibility_specified = value != "inherit" && value != "unset";
            node->style.inline_property_mask |= inline_visibility;
        } else if (name == "pointerEvents") {
            node->style.pointer_events_none = value == "none";
            node->style.pointer_events_specified = value != "inherit" && value != "unset";
            node->style.inline_property_mask |= inline_pointer_events;
        } else if (name == "color") {
            node->style.foreground_rgba = native_document::parse_color(value);
            node->style.inline_property_mask |= inline_color;
        } else if (name == "fontSize") {
            node->style.font_size = std::max(0.0F, native_document::parse_length(value).value);
            node->style.inline_property_mask |= inline_font_size;
        } else if (name == "fontFamily") {
            node->style.font_family = value;
            node->style.inline_property_mask |= inline_font_family;
        } else if (name == "fontWeight") {
            node->style.font_weight = value == "bold" ? 700
                : value == "normal" ? 400
                : std::max(1, static_cast<int>(std::lround(
                    native_document::parse_length(value).value)));
            node->style.inline_property_mask |= inline_font_weight;
        } else if (name == "lineHeight") {
            const auto parsed = std::max(0.0F, native_document::parse_length(value).value);
            node->style.line_height = parsed > 0 && parsed <= 4
                ? parsed * (node->style.font_size >= 0 ? node->style.font_size : 14.0F)
                : parsed;
            node->style.inline_property_mask |= inline_line_height;
        } else if (name == "textAlign") {
            node->style.text_align = value;
            node->style.inline_property_mask |= inline_text_align;
        } else if (name == "whiteSpace") {
            node->style.white_space = value;
            node->style.inline_property_mask |= inline_white_space;
        }
        if (has_important_rule) self->apply_css_rules(*node);
        else self->document.mark_dirty();
    }

    static void get_computed_style(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        if (node != nullptr) info.GetReturnValue().Set(self->wrap_computed_style(*node));
    }

    static void request_animation_frame(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 1 || !info[0]->IsFunction()) return;
        auto* self = current(info.GetIsolate());
        if (self->profile_startup) ++self->startup_raf_scheduled;
        const auto id = self->next_timer_id++;
        constexpr auto frame_period = std::chrono::duration<double, std::milli>(1000.0 / 60.0);
        self->timers.push_back(timer_task{
            id,
            true,
            false,
            std::chrono::steady_clock::now()
                + std::chrono::duration_cast<std::chrono::steady_clock::duration>(frame_period),
            frame_period,
            v8::Global<v8::Context>(info.GetIsolate(), info.GetIsolate()->GetCurrentContext()),
            v8::Global<v8::Function>(info.GetIsolate(), info[0].As<v8::Function>()),
            {}});
        info.GetReturnValue().Set(v8::Integer::NewFromUnsigned(info.GetIsolate(), id));
    }

    static void schedule_timer(const v8::FunctionCallbackInfo<v8::Value>& info, bool repeating)
    {
        if (info.Length() < 1 || !info[0]->IsFunction()) return;
        auto* self = current(info.GetIsolate());
        const auto id = self->next_timer_id++;
        auto delay = info.Length() > 1
            ? info[1]->NumberValue(info.GetIsolate()->GetCurrentContext()).FromMaybe(0)
            : 0;
        if (!std::isfinite(delay) || delay < 0) delay = 0;
        if (self->profile_startup) {
            ++self->startup_timer_scheduled;
            self->startup_max_timer_delay_ms = std::max(
                self->startup_max_timer_delay_ms,
                delay);
        }
        if (repeating) delay = std::max(1.0, delay);
        const auto interval = std::chrono::duration<double, std::milli>(delay);
        std::vector<v8::Global<v8::Value>> arguments;
        arguments.reserve(info.Length() > 2 ? static_cast<size_t>(info.Length() - 2) : 0U);
        for (int index = 2; index < info.Length(); ++index) {
            arguments.emplace_back(info.GetIsolate(), info[index]);
        }
        self->timers.push_back(timer_task{
            id,
            false,
            repeating,
            std::chrono::steady_clock::now()
                + std::chrono::duration_cast<std::chrono::steady_clock::duration>(interval),
            interval,
            v8::Global<v8::Context>(info.GetIsolate(), info.GetIsolate()->GetCurrentContext()),
            v8::Global<v8::Function>(info.GetIsolate(), info[0].As<v8::Function>()),
            std::move(arguments)});
        info.GetReturnValue().Set(v8::Integer::NewFromUnsigned(info.GetIsolate(), id));
    }

    static void set_timeout(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        schedule_timer(info, false);
    }

    static void set_interval(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        schedule_timer(info, true);
    }

    static void clear_timeout(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 1) return;
        auto* self = current(info.GetIsolate());
        const auto id = info[0]->Uint32Value(info.GetIsolate()->GetCurrentContext()).FromMaybe(0);
        std::erase_if(self->timers, [id](const auto& timer) { return timer.id == id; });
        if (self->active_timer_id == id) self->active_timer_cancelled = true;
    }

    static void performance_now(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        const auto now = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
        info.GetReturnValue().Set(v8::Number::New(info.GetIsolate(), now));
    }

    static void performance_get_entries_by_name(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        // The probe does not retain a Performance Timeline yet. Returning an
        // empty sequence is the browser-correct result when no matching entry
        // has been recorded and lets consumers feature-test marks safely.
        info.GetReturnValue().Set(v8::Array::New(info.GetIsolate()));
    }

    static void performance_entry(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto result = v8::Object::New(info.GetIsolate());
        auto local_context = info.GetIsolate()->GetCurrentContext();
        result->Set(
            local_context,
            js_string(info.GetIsolate(), "name"),
            info.Length() > 0 ? info[0]
                : v8::Local<v8::Value>(js_string(info.GetIsolate(), ""))).Check();
        result->Set(
            local_context,
            js_string(info.GetIsolate(), "startTime"),
            v8::Number::New(info.GetIsolate(), std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now().time_since_epoch()).count())).Check();
        result->Set(local_context, js_string(info.GetIsolate(), "duration"),
            v8::Number::New(info.GetIsolate(), 0)).Check();
        info.GetReturnValue().Set(result);
    }

    static void image_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto& node = self->document.create_element("img");
        if (info.Length() > 0) node.attributes["width"] = to_utf8(info.GetIsolate(), info[0]);
        if (info.Length() > 1) node.attributes["height"] = to_utf8(info.GetIsolate(), info[1]);
        self->apply_css_rules(node);
        auto object = self->wrap_node(node);
        auto local_context = info.GetIsolate()->GetCurrentContext();
        object->Set(local_context, js_string(info.GetIsolate(), "complete"), v8::False(info.GetIsolate())).Check();
        object->Set(local_context, js_string(info.GetIsolate(), "naturalWidth"),
            v8::Integer::New(info.GetIsolate(), info.Length() > 0
                ? info[0]->Int32Value(local_context).FromMaybe(0) : 0)).Check();
        object->Set(local_context, js_string(info.GetIsolate(), "naturalHeight"),
            v8::Integer::New(info.GetIsolate(), info.Length() > 1
                ? info[1]->Int32Value(local_context).FromMaybe(0) : 0)).Check();
        info.GetReturnValue().Set(object);
    }

    static void image_decode(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto resolver = v8::Promise::Resolver::New(local_context).ToLocalChecked();
        resolver->Resolve(local_context, v8::Undefined(info.GetIsolate())).Check();
        info.GetReturnValue().Set(resolver->GetPromise());
    }

    static void add_event_listener(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 2 || !info[1]->IsFunction()) return;
        auto* self = current(info.GetIsolate());
        const auto type = to_utf8(info.GetIsolate(), info[0]);
        bool use_capture = false;
        bool use_once = false;
        if (info.Length() > 2) {
            if (info[2]->IsBoolean()) {
                use_capture = info[2]->BooleanValue(info.GetIsolate());
            } else if (info[2]->IsObject()) {
                v8::Local<v8::Value> capture;
                if (info[2].As<v8::Object>()->Get(
                        info.GetIsolate()->GetCurrentContext(),
                        js_string(info.GetIsolate(), "capture")).ToLocal(&capture)) {
                    use_capture = capture->BooleanValue(info.GetIsolate());
                }
                v8::Local<v8::Value> once;
                if (info[2].As<v8::Object>()->Get(
                        info.GetIsolate()->GetCurrentContext(),
                        js_string(info.GetIsolate(), "once")).ToLocal(&once)) {
                    use_once = once->BooleanValue(info.GetIsolate());
                }
            }
        }
        if (self->in_frame_context() || type != "resize") {
            uint32_t target_node_id = 0;
            for (const auto& [key, wrapper] : self->node_wrappers) {
                auto local_wrapper = wrapper.Get(info.GetIsolate());
                if (!local_wrapper.IsEmpty() && local_wrapper->StrictEquals(info.This())) {
                    target_node_id = static_cast<uint32_t>(key);
                    break;
                }
            }
            self->frame_event_listeners[type].emplace_back(
                info.GetIsolate(),
                info[1].As<v8::Function>());
            self->frame_event_listener_contexts[type].emplace_back(
                info.GetIsolate(),
                info.GetIsolate()->GetCurrentContext());
            self->frame_event_listener_targets[type].push_back(target_node_id);
            self->frame_event_listener_captures[type].push_back(use_capture);
            self->frame_event_listener_once[type].push_back(use_once);
            auto name = to_utf8(info.GetIsolate(), info[1].As<v8::Function>()->GetName());
            if (name.empty()) name = "<anonymous>";
            self->frame_event_listener_names[type].push_back(std::move(name));
            self->frame_event_listener_registration_sequences[type].push_back(
                self->current_input_sequence);
        } else if (type == "resize") {
            self->resize_listeners.emplace_back(info.GetIsolate(), info[1].As<v8::Function>());
        }
    }

    static void remove_event_listener(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (info.Length() < 2 || !info[1]->IsFunction()) return;
        auto* self = current(info.GetIsolate());
        const auto type = to_utf8(info.GetIsolate(), info[0]);
        const auto requested = info[1].As<v8::Function>();
        bool requested_capture = false;
        if (info.Length() > 2) {
            if (info[2]->IsBoolean()) {
                requested_capture = info[2]->BooleanValue(info.GetIsolate());
            } else if (info[2]->IsObject()) {
                v8::Local<v8::Value> capture;
                if (info[2].As<v8::Object>()->Get(
                        info.GetIsolate()->GetCurrentContext(),
                        js_string(info.GetIsolate(), "capture")).ToLocal(&capture)) {
                    requested_capture = capture->BooleanValue(info.GetIsolate());
                }
            }
        }
        if (self->in_frame_context() || type != "resize") {
            uint32_t target_node_id = 0;
            for (const auto& [key, wrapper] : self->node_wrappers) {
                auto local_wrapper = wrapper.Get(info.GetIsolate());
                if (!local_wrapper.IsEmpty() && local_wrapper->StrictEquals(info.This())) {
                    target_node_id = static_cast<uint32_t>(key);
                    break;
                }
            }
            auto listeners = self->frame_event_listeners.find(type);
            if (listeners == self->frame_event_listeners.end()) return;
            auto& callbacks = listeners->second;
            auto& contexts = self->frame_event_listener_contexts[type];
            auto& targets = self->frame_event_listener_targets[type];
            auto& captures = self->frame_event_listener_captures[type];
            auto local_context = info.GetIsolate()->GetCurrentContext();
            for (size_t index = 0; index < callbacks.size(); ++index) {
                auto callback = callbacks[index].Get(info.GetIsolate());
                const auto registered_target = index < targets.size() ? targets[index] : 0U;
                if (registered_target != target_node_id
                    || (index < contexts.size()
                        && contexts[index].Get(info.GetIsolate()) != local_context)
                    || (index < captures.size() ? captures[index] : false) != requested_capture
                    || callback.IsEmpty()
                    || !callback->StrictEquals(requested)) {
                    continue;
                }
                // Do not erase synchronized listener vectors while an event
                // may be iterating them. The dispatch tail compacts tombstones.
                callbacks[index].Reset();
                break;
            }
            return;
        }
        if (type != "resize") return;
        for (auto iterator = self->resize_listeners.begin(); iterator != self->resize_listeners.end(); ++iterator) {
            auto callback = iterator->Get(info.GetIsolate());
            if (callback.IsEmpty() || !callback->StrictEquals(requested)) continue;
            iterator->Reset();
            self->resize_listeners.erase(iterator);
            break;
        }
    }

    static void set_pointer_capture(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (node != nullptr) self->pointer_capture_target = node;
    }

    static void release_pointer_capture(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        if (self->pointer_capture_target == node) self->pointer_capture_target = nullptr;
    }

    static void has_pointer_capture(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto* node = unwrap_node(info.This());
        info.GetReturnValue().Set(
            self->pointer_capture_target == node
                ? v8::True(info.GetIsolate())
                : v8::False(info.GetIsolate()));
    }

    static void event_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto event = info.IsConstructCall() ? info.This() : v8::Object::New(isolate);
        event->Set(
            local_context,
            js_string(isolate, "type"),
            info.Length() > 0 ? info[0] : v8::Local<v8::Value>(js_string(isolate, ""))).Check();
        event->Set(local_context, js_string(isolate, "bubbles"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "cancelable"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "composed"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "defaultPrevented"), v8::False(isolate)).Check();
        event->Set(local_context, js_string(isolate, "eventPhase"), v8::Integer::New(isolate, 0)).Check();
        if (info.Length() > 1 && info[1]->IsObject()) {
            auto options = info[1].As<v8::Object>();
            const auto copy_option = [&](const char* name) {
                v8::Local<v8::Value> value;
                if (options->Get(local_context, js_string(isolate, name)).ToLocal(&value)
                    && !value->IsUndefined()) {
                    event->Set(local_context, js_string(isolate, name), value).Check();
                }
            };
            copy_option("detail");
            copy_option("bubbles");
            copy_option("cancelable");
            copy_option("composed");
        }
        event->Set(
            local_context,
            js_string(isolate, "preventDefault"),
            v8::Function::New(local_context, event_prevent_default).ToLocalChecked()).Check();
        event->Set(
            local_context,
            js_string(isolate, "stopPropagation"),
            v8::Function::New(local_context, event_stop_propagation).ToLocalChecked()).Check();
        event->Set(
            local_context,
            js_string(isolate, "stopImmediatePropagation"),
            v8::Function::New(local_context, event_stop_immediate_propagation).ToLocalChecked()).Check();
        info.GetReturnValue().Set(event);
    }

    static resize_observer_state* resize_observer(
        const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        return info.Data()->IsExternal()
            ? static_cast<resize_observer_state*>(info.Data().As<v8::External>()->Value(
                v8::kExternalPointerTypeTagDefault))
            : nullptr;
    }

    static void resize_observer_observe(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* observer = resize_observer(info);
        auto* node = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        if (observer == nullptr || node == nullptr) return;
        if (std::find(observer->nodes.begin(), observer->nodes.end(), node) == observer->nodes.end()) {
            observer->nodes.push_back(node);
            observer->delivered_sizes.erase(node->id);
            current(info.GetIsolate())->resize_observers_pending = true;
        }
    }

    static void resize_observer_unobserve(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* observer = resize_observer(info);
        auto* node = info.Length() > 0 && info[0]->IsObject()
            ? unwrap_node(info[0].As<v8::Object>())
            : nullptr;
        if (observer != nullptr && node != nullptr) {
            std::erase(observer->nodes, node);
            observer->delivered_sizes.erase(node->id);
        }
    }

    static void resize_observer_disconnect(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* observer = resize_observer(info);
        if (observer != nullptr) {
            observer->nodes.clear();
            observer->delivered_sizes.clear();
        }
    }

    static void resize_observer_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto object = info.IsConstructCall() ? info.This() : v8::Object::New(info.GetIsolate());
        if (info.Length() < 1 || !info[0]->IsFunction()) {
            info.GetReturnValue().Set(object);
            return;
        }
        auto observer = std::make_unique<resize_observer_state>();
        observer->context.Reset(info.GetIsolate(), local_context);
        observer->callback.Reset(info.GetIsolate(), info[0].As<v8::Function>());
        auto* state = observer.get();
        self->resize_observers.push_back(std::move(observer));
        auto data = v8::External::New(
            info.GetIsolate(),
            state,
            v8::kExternalPointerTypeTagDefault);
        object->Set(
            local_context,
            js_string(info.GetIsolate(), "observe"),
            v8::Function::New(local_context, resize_observer_observe, data).ToLocalChecked()).Check();
        object->Set(
            local_context,
            js_string(info.GetIsolate(), "unobserve"),
            v8::Function::New(local_context, resize_observer_unobserve, data).ToLocalChecked()).Check();
        object->Set(
            local_context,
            js_string(info.GetIsolate(), "disconnect"),
            v8::Function::New(local_context, resize_observer_disconnect, data).ToLocalChecked()).Check();
        info.GetReturnValue().Set(object);
    }

    static void observer_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto observer = info.IsConstructCall() ? info.This() : v8::Object::New(isolate);
        auto no_op = v8::Function::New(local_context, console_log).ToLocalChecked();
        observer->Set(local_context, js_string(isolate, "observe"), no_op).Check();
        observer->Set(local_context, js_string(isolate, "disconnect"), no_op).Check();
        observer->Set(local_context, js_string(isolate, "takeRecords"), v8::Function::New(local_context, empty_array).ToLocalChecked()).Check();
        info.GetReturnValue().Set(observer);
    }

    static void abort_controller_constructor(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto controller = info.IsConstructCall() ? info.This() : v8::Object::New(isolate);
        auto signal = v8::Object::New(isolate);
        signal->Set(local_context, js_string(isolate, "aborted"), v8::False(isolate)).Check();
        signal->Set(local_context, js_string(isolate, "addEventListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        signal->Set(local_context, js_string(isolate, "removeEventListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        controller->Set(local_context, js_string(isolate, "signal"), signal).Check();
        controller->Set(local_context, js_string(isolate, "abort"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        info.GetReturnValue().Set(controller);
    }

    static void match_media(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto isolate = info.GetIsolate();
        auto local_context = isolate->GetCurrentContext();
        auto result = v8::Object::New(isolate);
        result->Set(local_context, js_string(isolate, "matches"), v8::False(isolate)).Check();
        result->Set(local_context, js_string(isolate, "media"), info.Length() > 0 ? info[0] : v8::Local<v8::Value>(js_string(isolate, ""))).Check();
        result->Set(local_context, js_string(isolate, "addEventListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "removeEventListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "addListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        result->Set(local_context, js_string(isolate, "removeListener"), v8::Function::New(local_context, console_log).ToLocalChecked()).Check();
        info.GetReturnValue().Set(result);
    }

    static void empty_array(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        info.GetReturnValue().Set(v8::Array::New(info.GetIsolate()));
    }

    static void text_encoder_encode(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        const auto value = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        auto buffer = v8::ArrayBuffer::New(info.GetIsolate(), value.size());
        if (!value.empty()) {
            std::memcpy(buffer->GetBackingStore()->Data(), value.data(), value.size());
        }
        info.GetReturnValue().Set(v8::Uint8Array::New(buffer, 0, value.size()));
    }

    static void dispatch_event(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        if (info.Length() < 1 || !info[0]->IsObject()) return;
        auto local_context = info.GetIsolate()->GetCurrentContext();
        auto event = info[0].As<v8::Object>();
        v8::Local<v8::Value> type_value;
        if (!event->Get(local_context, js_string(info.GetIsolate(), "type")).ToLocal(&type_value)) return;
        const auto type = to_utf8(info.GetIsolate(), type_value);
        auto receiver = info.This()->IsObject()
            ? info.This().As<v8::Object>()
            : v8::Local<v8::Object> {};
        const auto receiver_is_document = !receiver.IsEmpty()
            && receiver->InternalFieldCount() > 0
            && receiver->GetAlignedPointerFromInternalField(
                0,
                v8::kEmbedderDataTypeTagDefault) == self;
        auto* target = receiver.IsEmpty() || receiver_is_document
            ? nullptr
            : unwrap_node(receiver);
        auto target_value = target != nullptr
            ? self->wrap_node(*target)
            : receiver_is_document
                ? receiver
                : v8::Local<v8::Object>(local_context->Global());
        event->Set(local_context, js_string(info.GetIsolate(), "target"), target_value).Check();
        event->Set(local_context, js_string(info.GetIsolate(), "srcElement"), target_value).Check();
        event->Set(
            local_context,
            js_string(info.GetIsolate(), "__htmlmlPropagationStopped"),
            v8::False(info.GetIsolate())).Check();
        event->Set(
            local_context,
            js_string(info.GetIsolate(), "__htmlmlImmediatePropagationStopped"),
            v8::False(info.GetIsolate())).Check();
        const auto read_boolean = [&](const char* name) {
            v8::Local<v8::Value> value;
            return event->Get(local_context, js_string(info.GetIsolate(), name)).ToLocal(&value)
                && value->BooleanValue(info.GetIsolate());
        };
        const auto propagation_stopped = [&]() {
            return read_boolean("__htmlmlPropagationStopped");
        };
        const auto immediate_propagation_stopped = [&]() {
            return read_boolean("__htmlmlImmediatePropagationStopped");
        };
        const auto set_phase = [&](v8::Local<v8::Object> current_target, int phase) {
            event->Set(
                local_context,
                js_string(info.GetIsolate(), "currentTarget"),
                current_target).Check();
            event->Set(
                local_context,
                js_string(info.GetIsolate(), "eventPhase"),
                v8::Integer::New(info.GetIsolate(), phase)).Check();
        };
        const auto listeners = self->frame_event_listeners.find(type);
        const auto listener_contexts = self->frame_event_listener_contexts.find(type);
        const auto targets = self->frame_event_listener_targets.find(type);
        const auto captures = self->frame_event_listener_captures.find(type);
        const auto once_flags = self->frame_event_listener_once.find(type);
        v8::Local<v8::Value> arguments[] = {event};
        const auto invoke_listeners = [&] (
            uint32_t target_id,
            v8::Local<v8::Object> current_target,
            bool capture,
            int phase) {
            if (listeners == self->frame_event_listeners.end()) return true;
            set_phase(current_target, phase);
            const auto listener_count = listeners->second.size();
            for (size_t index = 0; index < listener_count; ++index) {
                if (listener_contexts != self->frame_event_listener_contexts.end()
                    && index < listener_contexts->second.size()
                    && listener_contexts->second[index].Get(info.GetIsolate()) != local_context) continue;
                const auto registered_target = targets != self->frame_event_listener_targets.end()
                    && index < targets->second.size() ? targets->second[index] : 0U;
                const auto registered_capture = captures != self->frame_event_listener_captures.end()
                    && index < captures->second.size() ? captures->second[index] : false;
                if (registered_target != target_id || registered_capture != capture) continue;
                auto callback = listeners->second[index].Get(info.GetIsolate());
                if (callback.IsEmpty()) continue;
                if (callback->Call(local_context, current_target, 1, arguments).IsEmpty()) return false;
                const auto invoke_once = once_flags != self->frame_event_listener_once.end()
                    && index < once_flags->second.size() && once_flags->second[index];
                if (invoke_once) listeners->second[index].Reset();
                if (immediate_propagation_stopped()) break;
            }
            return true;
        };
        const auto property_name = std::string("on") + type;
        const auto invoke_property = [&](dom_node& node, int phase) {
            auto wrapper = self->wrap_node(node);
            v8::Local<v8::Value> handler;
            if (!wrapper->Get(
                    local_context,
                    js_string(info.GetIsolate(), property_name.c_str())).ToLocal(&handler)
                || !handler->IsFunction()) return true;
            set_phase(wrapper, phase);
            return !handler.As<v8::Function>()->Call(
                local_context,
                wrapper,
                1,
                arguments).IsEmpty();
        };

        auto global = local_context->Global();
        if (target == nullptr) {
            if (!invoke_listeners(0U, global, true, 2)
                || (!immediate_propagation_stopped()
                    && !invoke_listeners(0U, global, false, 2))) return;
        } else {
            std::vector<dom_node*> ancestry;
            for (auto* node = target; node != nullptr; node = node->parent) {
                ancestry.push_back(node);
            }
            if (!invoke_listeners(0U, global, true, 1)) return;
            for (size_t cursor = ancestry.size(); !propagation_stopped() && cursor-- > 1;) {
                auto* node = ancestry[cursor];
                if (!invoke_listeners(node->id, self->wrap_node(*node), true, 1)) return;
            }
            if (!propagation_stopped()) {
                if (!invoke_listeners(target->id, target_value, true, 2)) return;
                if (!immediate_propagation_stopped()
                    && !invoke_listeners(target->id, target_value, false, 2)) return;
                if (!immediate_propagation_stopped() && !invoke_property(*target, 2)) return;
            }
            if (read_boolean("bubbles") && !propagation_stopped()) {
                for (size_t cursor = 1; cursor < ancestry.size() && !propagation_stopped(); ++cursor) {
                    auto* node = ancestry[cursor];
                    auto wrapper = self->wrap_node(*node);
                    if (!invoke_listeners(node->id, wrapper, false, 3)
                        || (!immediate_propagation_stopped() && !invoke_property(*node, 3))) return;
                }
                if (!propagation_stopped()
                    && !invoke_listeners(0U, global, false, 3)) return;
            }
        }
        if (type == "innerWindowLoad") {
            for (auto& [node_id, listener] : self->frame_load_listeners) {
                static_cast<void>(node_id);
                auto owner_context = self->context.Get(info.GetIsolate());
                v8::Context::Scope owner_scope(owner_context);
                auto detail = v8::Object::New(info.GetIsolate());
                detail->Set(owner_context, js_string(info.GetIsolate(), "received"), v8::False(info.GetIsolate())).Check();
                auto owner_event = v8::Object::New(info.GetIsolate());
                owner_event->Set(owner_context, js_string(info.GetIsolate(), "detail"), detail).Check();
                v8::Local<v8::Value> arguments[] = {owner_event};
                auto callback = listener.Get(info.GetIsolate());
                if (!callback.IsEmpty()) {
                    callback->Call(owner_context, owner_context->Global(), 1, arguments).ToLocalChecked();
                }
            }
        }
        if (listeners != self->frame_event_listeners.end()) {
            auto& callbacks = listeners->second;
            auto& contexts = self->frame_event_listener_contexts[type];
            auto& registered_targets = self->frame_event_listener_targets[type];
            auto& registered_captures = self->frame_event_listener_captures[type];
            auto& registered_once = self->frame_event_listener_once[type];
            auto& names = self->frame_event_listener_names[type];
            auto& sequences = self->frame_event_listener_registration_sequences[type];
            for (size_t index = callbacks.size(); index-- > 0;) {
                if (!callbacks[index].IsEmpty()) continue;
                callbacks.erase(callbacks.begin() + static_cast<std::ptrdiff_t>(index));
                if (index < contexts.size()) {
                    contexts.erase(contexts.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < registered_targets.size()) {
                    registered_targets.erase(
                        registered_targets.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < registered_captures.size()) {
                    registered_captures.erase(
                        registered_captures.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < registered_once.size()) {
                    registered_once.erase(
                        registered_once.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < names.size()) {
                    names.erase(names.begin() + static_cast<std::ptrdiff_t>(index));
                }
                if (index < sequences.size()) {
                    sequences.erase(sequences.begin() + static_cast<std::ptrdiff_t>(index));
                }
            }
        }
        event->Set(
            local_context,
            js_string(info.GetIsolate(), "currentTarget"),
            v8::Null(info.GetIsolate())).Check();
        event->Set(
            local_context,
            js_string(info.GetIsolate(), "eventPhase"),
            v8::Integer::New(info.GetIsolate(), 0)).Check();
        info.GetReturnValue().Set(
            read_boolean("defaultPrevented")
                ? v8::False(info.GetIsolate())
                : v8::True(info.GetIsolate()));
    }

    static void create_object_url(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        const auto html = info.Length() > 0 ? to_utf8(info.GetIsolate(), info[0]) : std::string{};
        const auto url = "blob:htmlml-native/" + std::to_string(self->next_object_url_id++);
        self->object_urls[url] = html;
        info.GetReturnValue().Set(js_string(info.GetIsolate(), url.c_str()));
    }

    static void get_inner_width(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        const auto [width, height] = self->viewport_provider();
        static_cast<void>(height);
        info.GetReturnValue().Set(v8::Number::New(info.GetIsolate(), width));
    }

    static void get_inner_height(v8::Local<v8::Name>, const v8::PropertyCallbackInfo<v8::Value>& info)
    {
        auto* self = current(info.GetIsolate());
        binding_callback_timer binding_timer(self->profile(binding_category::dom_geometry));
        const auto [width, height] = self->viewport_provider();
        static_cast<void>(width);
        info.GetReturnValue().Set(v8::Number::New(info.GetIsolate(), height));
    }

    static void console_log(const v8::FunctionCallbackInfo<v8::Value>& info)
    {
        if (!info.Data()->IsInt32()) return;
        auto* self = current(info.GetIsolate());
        const auto level_index = std::clamp(info.Data().As<v8::Int32>()->Value(), 0, 2);
        constexpr std::array<std::string_view, 3> levels{"log", "warn", "error"};
        std::ostringstream message;
        for (int index = 0; index < info.Length(); ++index) {
            auto rendered = to_utf8(info.GetIsolate(), info[index]);
            message << (index == 0 ? "" : " | ") << rendered;
        }
        const auto payload = std::string(levels[static_cast<size_t>(level_index)])
            + '\n' + message.str();
        {
            std::lock_guard lock(self->console_message_mutex);
            constexpr size_t maximum_console_messages = 1024U;
            if (self->console_messages.size() >= maximum_console_messages) {
                self->console_messages.pop_front();
            }
            self->console_messages.push_back(payload);
        }
        if (std::getenv("HTMLML_PROBE_CONSOLE") != nullptr) {
            std::cerr << "[V8 console." << levels[static_cast<size_t>(level_index)]
                << "] " << message.str() << '\n';
        }
    }

    static void promise_rejected(v8::PromiseRejectMessage message)
    {
        auto* isolate = v8::Isolate::GetCurrent();
        auto* self = current(isolate);
        auto promise = message.GetPromise();
        if (message.GetEvent() == v8::kPromiseHandlerAddedAfterReject) {
            std::erase_if(
                self->pending_promise_rejections,
                [&promise, isolate](auto& rejection) {
                    return rejection.promise.Get(isolate)->StrictEquals(promise);
                });
            return;
        }
        if (message.GetEvent() != v8::kPromiseRejectWithNoHandler) return;
        auto value = message.GetValue();
        auto error = value.IsEmpty()
            ? "Unhandled promise rejection"
            : "Unhandled promise rejection: " + to_utf8(isolate, value);
        if (!value.IsEmpty() && value->IsObject()) {
            auto local_context = isolate->GetCurrentContext();
            v8::Local<v8::Value> stack;
            if (value.As<v8::Object>()->Get(local_context, js_string(isolate, "stack")).ToLocal(&stack)
                && stack->IsString()) {
                error += "\n" + to_utf8(isolate, stack);
            }
        }
        self->pending_promise_rejections.push_back({
            v8::Global<v8::Promise>(isolate, promise),
            std::move(error)});
    }

    native_document& document;
    std::function<std::pair<float, float>()> viewport_provider;
    v8::ArrayBuffer::Allocator* allocator{nullptr};
    v8::Isolate* isolate{nullptr};
    v8::CpuProfiler* cpu_profiler{nullptr};
    v8::Global<v8::Context> context;
    v8::Global<v8::FunctionTemplate> element_template;
    v8::Global<v8::ObjectTemplate> style_template;
    v8::Global<v8::ObjectTemplate> frame_document_template;
    v8::Global<v8::ObjectTemplate> frame_window_template;
    v8::Global<v8::Object> document_object;
    v8::Global<v8::ObjectTemplate> document_template;
    v8::Global<v8::Context> frame_context;
    std::unordered_map<uint64_t, v8::Global<v8::Object>> node_wrappers;
    std::unordered_map<uint64_t, v8::Global<v8::Object>> style_wrappers;
    std::unordered_map<uint64_t, v8::Global<v8::Object>> computed_style_wrappers;
    std::unordered_map<uint64_t, v8::Global<v8::Object>> canvas_contexts;
    std::unordered_map<uint64_t, std::unique_ptr<canvas_state>> canvas_states;
    std::unordered_map<uint32_t, v8::Global<v8::Object>> frame_documents;
    std::unordered_map<uint32_t, v8::Global<v8::Object>> frame_windows;
    std::unordered_map<uint32_t, v8::Global<v8::Object>> actual_frame_windows;
    std::unordered_map<uint32_t, v8::Global<v8::Object>> actual_frame_documents;
    std::unordered_map<uint32_t, v8::Global<v8::Function>> frame_load_listeners;
    std::unordered_map<std::string, std::vector<v8::Global<v8::Function>>> frame_event_listeners;
    std::unordered_map<std::string, std::vector<v8::Global<v8::Context>>>
        frame_event_listener_contexts;
    std::unordered_map<std::string, std::vector<uint32_t>> frame_event_listener_targets;
    std::unordered_map<std::string, std::vector<bool>> frame_event_listener_captures;
    std::unordered_map<std::string, std::vector<bool>> frame_event_listener_once;
    std::unordered_map<std::string, std::vector<std::string>> frame_event_listener_names;
    std::unordered_map<std::string, std::vector<uint64_t>>
        frame_event_listener_registration_sequences;
    std::unordered_map<std::string, uint64_t> event_dispatch_counts;
    std::unordered_map<std::string, std::unordered_map<uint32_t, uint64_t>>
        event_dispatch_target_counts;
    std::unordered_map<std::string, uint64_t> event_callback_counts;
    std::unordered_map<std::string, std::unordered_map<uint32_t, uint64_t>>
        event_callback_target_counts;
    std::unordered_map<std::string, std::unordered_map<size_t, uint64_t>>
        event_callback_index_counts;
    std::string last_mousemove_ancestry;
    std::vector<v8::Global<v8::Function>> resize_listeners;
    std::vector<timer_task> timers;
    std::vector<connected_resource_task> connected_resources;
    std::mutex host_request_mutex;
    std::deque<std::string> host_requests;
    std::mutex console_message_mutex;
    std::deque<std::string> console_messages;
    uint64_t next_host_request_id{0};
    std::vector<std::unique_ptr<resize_observer_state>> resize_observers;
    bool resize_observers_pending{false};
    std::vector<std::string> loaded_resource_names;
    uint32_t next_timer_id{1};
    uint32_t active_timer_id{0};
    bool active_timer_cancelled{false};
    bool prefer_timer_task{true};
    uint32_t next_object_url_id{1};
    std::unordered_map<std::string, std::string> object_urls;
    std::vector<dom_node*> pending_frame_hydrations;
    std::string last_error;
    std::vector<pending_promise_rejection> pending_promise_rejections;
    std::filesystem::path resource_root;
    std::vector<css_rule> css_rules;
    std::unordered_map<std::string, std::vector<size_t>> css_rules_by_class;
    std::unordered_map<std::string, std::vector<size_t>> css_rules_by_id;
    std::unordered_map<std::string, std::vector<size_t>> css_rules_by_tag;
    std::vector<size_t> unindexed_css_rules;
    std::unordered_map<std::string, std::string> css_variables;
    uint64_t frame_script_execution_count{0};
    uint64_t frame_script_error_count{0};
    static constexpr size_t maximum_compilation_cache_entries = 256U;
    static constexpr size_t maximum_compilation_cache_source_bytes = 256U * 1024U * 1024U;
    static constexpr uint64_t maximum_persistent_cache_entry_bytes = 64U * 1024U * 1024U;
    static constexpr size_t maximum_persistent_cache_entries = 1024U;
    static constexpr uint64_t maximum_persistent_cache_bytes = 512U * 1024U * 1024U;
    std::string compilation_cache_directory;
    std::unordered_map<std::string, compilation_cache_entry> compilation_cache;
    std::deque<std::string> compilation_cache_order;
    size_t compilation_cache_bytes{0};
    uint64_t compilation_request_count{0};
    uint64_t compilation_memory_hit_count{0};
    uint64_t compilation_persistent_hit_count{0};
    uint64_t compilation_persistent_miss_count{0};
    uint64_t compilation_cache_rejection_count{0};
    uint64_t compilation_cache_bytes_read_count{0};
    uint64_t compilation_cache_bytes_written_count{0};
    uint64_t compilation_time_nanosecond_count{0};
    uint32_t persistent_writes_since_prune{0};
    uint64_t input_event_dispatch_count{0};
    uint64_t input_callback_invocation_count{0};
    dom_node* pointer_capture_target{nullptr};
    dom_node* pointer_down_target{nullptr};
    dom_node* hover_target{nullptr};
    dom_node* current_related_target{nullptr};
    double pointer_down_x{0};
    double pointer_down_y{0};
    double last_pointer_x{0};
    double last_pointer_y{0};
    double current_movement_x{0};
    double current_movement_y{0};
    bool has_pointer_position{false};
    uint64_t current_input_sequence{0};
    dom_node* active_element{nullptr};
    std::string frame_last_error_value;
    const std::string empty_string;
    uint64_t last_resize_outer_listeners_ns{0};
    uint64_t last_resize_frame_listeners_ns{0};
    uint64_t last_resize_layout_ns{0};
    uint64_t last_resize_observers_ns{0};
    bool profile_bindings{false};
    bool profile_startup{false};
    std::array<binding_callback_stats, binding_category_count> binding_totals{};
    std::array<binding_callback_stats, binding_category_count> last_resize_binding_profile{};
    std::string last_resize_cpu_profile;
    binding_callback_stats startup_resource_read{};
    binding_callback_stats startup_css_parse{};
    binding_callback_stats startup_css_apply{};
    binding_callback_stats startup_css_incremental_apply{};
    binding_callback_stats startup_subtree_recascade{};
    binding_callback_stats startup_stylesheet_recascade{};
    binding_callback_stats startup_script_execute{};
    binding_callback_stats startup_layout{};
    binding_callback_stats startup_frame_hydrate{};
    uint64_t startup_stylesheet_recascade_nodes{0};
    uint64_t startup_connected_resources{0};
    uint64_t startup_raf_scheduled{0};
    uint64_t startup_raf_executed{0};
    uint64_t startup_timer_scheduled{0};
    uint64_t startup_timer_executed{0};
    double startup_max_timer_delay_ms{0};
    binding_callback_stats startup_task_callbacks{};
    std::chrono::steady_clock::time_point startup_frame_started{};
};

v8_dom_runtime::v8_dom_runtime(
    native_document& document,
    std::function<std::pair<float, float>()> viewport_provider,
    std::string compilation_cache_directory)
    : impl_(std::make_unique<implementation>(
        document,
        std::move(viewport_provider),
        std::move(compilation_cache_directory)))
{
}

v8_dom_runtime::~v8_dom_runtime() = default;

bool v8_dom_runtime::initialize()
{
    return impl_->initialize();
}

bool v8_dom_runtime::execute(const std::string& source, const std::string& document_name)
{
    return impl_->execute(source, document_name);
}

void v8_dom_runtime::set_resource_root(std::string resource_root)
{
    impl_->resource_root = std::filesystem::path(std::move(resource_root)).lexically_normal();
}

bool v8_dom_runtime::evaluate_json(
    const std::string& source,
    const std::string& document_name,
    std::string& result)
{
    return impl_->evaluate_json(source, document_name, result);
}

bool v8_dom_runtime::try_take_host_request(std::string& request)
{
    return impl_->try_take_host_request(request);
}

bool v8_dom_runtime::try_take_console_message(std::string& message)
{
    return impl_->try_take_console_message(message);
}

bool v8_dom_runtime::dispatch_resize()
{
    return impl_->dispatch_resize()
        && impl_->promote_pending_promise_error();
}

uint64_t v8_dom_runtime::last_resize_outer_listeners_nanoseconds() const noexcept
{
    return impl_->last_resize_outer_listeners_ns;
}

uint64_t v8_dom_runtime::last_resize_frame_listeners_nanoseconds() const noexcept
{
    return impl_->last_resize_frame_listeners_ns;
}

uint64_t v8_dom_runtime::last_resize_layout_nanoseconds() const noexcept
{
    return impl_->last_resize_layout_ns;
}

uint64_t v8_dom_runtime::last_resize_observers_nanoseconds() const noexcept
{
    return impl_->last_resize_observers_ns;
}

bool v8_dom_runtime::dispatch_input(const htmlml_input_event& event)
{
    return impl_->dispatch_input(event)
        && impl_->promote_pending_promise_error();
}

bool v8_dom_runtime::pump_task()
{
    v8::Isolate::Scope isolate_scope(impl_->isolate);
    v8::HandleScope handle_scope(impl_->isolate);
    auto local_context = impl_->context.Get(impl_->isolate);
    v8::Context::Scope context_scope(local_context);
    return impl_->drain_tasks()
        && impl_->promote_pending_promise_error();
}

bool v8_dom_runtime::has_pending_tasks() const noexcept
{
    return !impl_->pending_frame_hydrations.empty()
        || !impl_->connected_resources.empty()
        || impl_->resize_observers_pending
        || impl_->has_due_timer();
}

bool v8_dom_runtime::component_ready()
{
    v8::Isolate::Scope isolate_scope(impl_->isolate);
    v8::HandleScope handle_scope(impl_->isolate);
    auto local_context = impl_->context.Get(impl_->isolate);
    v8::Context::Scope context_scope(local_context);
    v8::Local<v8::Value> explicit_ready;
    return local_context->Global()->Get(
            local_context,
            js_string(impl_->isolate, "__htmlMlComponentReady")).ToLocal(&explicit_ready)
        && explicit_ready->BooleanValue(impl_->isolate);
}

std::string v8_dom_runtime::diagnostics()
{
    std::ostringstream description;
    description << "resources=[";
    const auto start = impl_->loaded_resource_names.size() > 24U
        ? impl_->loaded_resource_names.size() - 24U
        : 0U;
    for (auto index = start; index < impl_->loaded_resource_names.size(); ++index) {
        if (index != start) description << ',';
        description << impl_->loaded_resource_names[index];
    }
    description << ']';
    if (impl_->profile_startup) {
        const auto format = [](const binding_callback_stats& value) {
            std::ostringstream result;
            result << value.calls << '/' << std::fixed << std::setprecision(3)
                << value.nanoseconds / 1'000'000.0 << "ms";
            return std::move(result).str();
        };
        description << ", startup-profile={hydrate="
            << format(impl_->startup_frame_hydrate)
            << ",io=" << format(impl_->startup_resource_read)
            << ",css-parse=" << format(impl_->startup_css_parse)
            << ",css-apply=" << format(impl_->startup_css_apply)
            << ",css-incremental=" << format(impl_->startup_css_incremental_apply)
            << ",subtree-recascade=" << format(impl_->startup_subtree_recascade)
            << ",stylesheet-recascade=" << format(impl_->startup_stylesheet_recascade)
            << ",stylesheet-nodes=" << impl_->startup_stylesheet_recascade_nodes
            << ",css-rules=" << impl_->css_rules.size()
            << ",css-unindexed=" << impl_->unindexed_css_rules.size()
            << ",script=" << format(impl_->startup_script_execute)
            << ",layout=" << format(impl_->startup_layout)
            << ",resources=" << impl_->startup_connected_resources
            << ",raf=" << impl_->startup_raf_executed << '/'
            << impl_->startup_raf_scheduled
            << ",timers=" << impl_->startup_timer_executed << '/'
            << impl_->startup_timer_scheduled
            << ",max-timer=" << std::fixed << std::setprecision(1)
            << impl_->startup_max_timer_delay_ms << "ms"
            << ",task-callbacks=" << format(impl_->startup_task_callbacks);
        if (impl_->startup_frame_started != std::chrono::steady_clock::time_point{}) {
            description << ",wall=" << std::fixed << std::setprecision(1)
                << std::chrono::duration<double, std::milli>(
                    std::chrono::steady_clock::now() - impl_->startup_frame_started).count()
                << "ms";
        }
        description << '}';
        if (impl_->profile_bindings) {
            description << ",startup-bindings=[";
            for (size_t index = 0; index < binding_category_count; ++index) {
                if (index != 0) description << ',';
                const auto& stats = impl_->binding_totals[index];
                description << binding_category_names[index]
                    << ":c" << stats.calls
                    << "/ms" << std::fixed << std::setprecision(3)
                    << static_cast<double>(stats.nanoseconds) / 1'000'000.0;
            }
            description << ']';
        }
    }
    return description.str();
}

std::string v8_dom_runtime::event_diagnostics() const
{
    std::vector<std::string> types;
    types.reserve(impl_->event_dispatch_counts.size());
    for (const auto& [type, count] : impl_->event_dispatch_counts) {
        static_cast<void>(count);
        types.push_back(type);
    }
    std::sort(types.begin(), types.end());
    std::ostringstream result;
    result << "events=[";
    for (size_t index = 0; index < types.size(); ++index) {
        if (index != 0) result << ',';
        const auto& type = types[index];
        const auto listeners = impl_->frame_event_listeners.find(type);
        result << type << ":d" << impl_->event_dispatch_counts.at(type)
            << "/c" << (impl_->event_callback_counts.contains(type)
                ? impl_->event_callback_counts.at(type) : 0U)
            << "/l" << (listeners == impl_->frame_event_listeners.end()
                ? 0U : listeners->second.size());
        const auto dispatch_targets = impl_->event_dispatch_target_counts.find(type);
        if (dispatch_targets != impl_->event_dispatch_target_counts.end()) {
            result << "/h{";
            bool first = true;
            for (const auto& [target, count] : dispatch_targets->second) {
                if (!first) result << ';';
                first = false;
                result << target << ':' << count;
            }
            result << '}';
        }
        const auto targets = impl_->event_callback_target_counts.find(type);
        if (targets != impl_->event_callback_target_counts.end()) {
            result << "@{";
            bool first = true;
            for (const auto& [target, count] : targets->second) {
                if (!first) result << ';';
                first = false;
                result << target << ':' << count;
            }
            result << '}';
        }
        const auto registered_targets = impl_->frame_event_listener_targets.find(type);
        if (registered_targets != impl_->frame_event_listener_targets.end()) {
            std::unordered_map<uint32_t, size_t> target_counts;
            for (const auto target : registered_targets->second) ++target_counts[target];
            result << "/t{";
            bool first = true;
            for (const auto& [target, count] : target_counts) {
                if (!first) result << ';';
                first = false;
                result << target << ':' << count;
            }
            result << '}';
        }
        if (type == "mousemove") {
            const auto names = impl_->frame_event_listener_names.find(type);
            if (names != impl_->frame_event_listener_names.end()) {
                result << "/n{";
                for (size_t name_index = 0; name_index < names->second.size(); ++name_index) {
                    if (name_index != 0) result << ';';
                    const auto target = registered_targets != impl_->frame_event_listener_targets.end()
                        && name_index < registered_targets->second.size()
                        ? registered_targets->second[name_index]
                        : 0U;
                    const auto callbacks = impl_->event_callback_index_counts.contains(type)
                        && impl_->event_callback_index_counts.at(type).contains(name_index)
                        ? impl_->event_callback_index_counts.at(type).at(name_index)
                        : 0U;
                    const auto empty = listeners != impl_->frame_event_listeners.end()
                        && name_index < listeners->second.size()
                        && listeners->second[name_index].IsEmpty();
                    const auto registration_sequences =
                        impl_->frame_event_listener_registration_sequences.find(type);
                    const auto registered_at = registration_sequences
                            != impl_->frame_event_listener_registration_sequences.end()
                        && name_index < registration_sequences->second.size()
                        ? registration_sequences->second[name_index]
                        : 0U;
                    result << name_index << ':' << names->second[name_index]
                        << "@t" << target << "/c" << callbacks
                        << "/s" << registered_at
                        << (empty ? "/empty" : "");
                }
                result << '}';
            }
        }
    }
    result << ']';
    if (impl_->profile_bindings) {
        uint64_t total_nanoseconds = 0;
        result << ", resize-bindings=[";
        for (size_t index = 0; index < binding_category_count; ++index) {
            if (index != 0) result << ',';
            const auto& stats = impl_->last_resize_binding_profile[index];
            total_nanoseconds += stats.nanoseconds;
            result << binding_category_names[index]
                << ":c" << stats.calls
                << "/ms" << std::fixed << std::setprecision(3)
                << static_cast<double>(stats.nanoseconds) / 1'000'000.0;
        }
        result << "]/profiled-ms" << std::fixed << std::setprecision(3)
            << static_cast<double>(total_nanoseconds) / 1'000'000.0;
        if (!impl_->last_resize_cpu_profile.empty()) {
            result << ", resize-cpu=" << impl_->last_resize_cpu_profile;
        }
    }
    if (!impl_->last_mousemove_ancestry.empty()) {
        result << ", mousemove-ancestry=" << impl_->last_mousemove_ancestry;
    }
    return result.str();
}

const std::string& v8_dom_runtime::last_error() const noexcept
{
    return impl_->last_error;
}

uint64_t v8_dom_runtime::frame_scripts_executed() const noexcept
{
    return impl_->frame_script_execution_count;
}

uint64_t v8_dom_runtime::frame_script_errors() const noexcept
{
    return impl_->frame_script_error_count;
}

uint64_t v8_dom_runtime::compilation_requests() const noexcept
{
    return impl_->compilation_request_count;
}

uint64_t v8_dom_runtime::compilation_memory_hits() const noexcept
{
    return impl_->compilation_memory_hit_count;
}

uint64_t v8_dom_runtime::compilation_persistent_hits() const noexcept
{
    return impl_->compilation_persistent_hit_count;
}

uint64_t v8_dom_runtime::compilation_persistent_misses() const noexcept
{
    return impl_->compilation_persistent_miss_count;
}

uint64_t v8_dom_runtime::compilation_cache_rejections() const noexcept
{
    return impl_->compilation_cache_rejection_count;
}

uint64_t v8_dom_runtime::compilation_cache_bytes_read() const noexcept
{
    return impl_->compilation_cache_bytes_read_count;
}

uint64_t v8_dom_runtime::compilation_cache_bytes_written() const noexcept
{
    return impl_->compilation_cache_bytes_written_count;
}

uint64_t v8_dom_runtime::compilation_time_nanoseconds() const noexcept
{
    return impl_->compilation_time_nanosecond_count;
}

uint64_t v8_dom_runtime::input_events_dispatched() const noexcept
{
    return impl_->input_event_dispatch_count;
}

uint64_t v8_dom_runtime::input_callbacks_invoked() const noexcept
{
    return impl_->input_callback_invocation_count;
}

const std::string& v8_dom_runtime::frame_last_error() const noexcept
{
    return impl_->frame_last_error_value;
}

} // namespace htmlml_native

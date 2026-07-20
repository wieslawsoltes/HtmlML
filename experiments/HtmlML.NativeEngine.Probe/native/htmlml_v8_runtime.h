#pragma once

#include "htmlml_native_engine.h"

#include <functional>
#include <memory>
#include <string>
#include <utility>

namespace htmlml_native {

class native_document;

// Initializes the process-wide V8 platform without allocating a DOM runtime or
// isolate. Applications can call this during startup so the one-time V8 cost is
// outside the first component's load path. Compilation-unit cache behavior remains
// owned by each subsequently created runtime.
void prewarm_v8_process();

class v8_dom_runtime final {
public:
    v8_dom_runtime(
        native_document& document,
        std::function<std::pair<float, float>()> viewport_provider,
        std::string compilation_cache_directory = {});
    ~v8_dom_runtime();

    v8_dom_runtime(const v8_dom_runtime&) = delete;
    v8_dom_runtime& operator=(const v8_dom_runtime&) = delete;

    bool initialize();
    bool execute(const std::string& source, const std::string& document_name);
    void set_resource_root(std::string resource_root);
    bool evaluate_json(
        const std::string& source,
        const std::string& document_name,
        std::string& result);
    bool try_take_host_request(std::string& request);
    bool try_take_console_message(std::string& message);
    bool dispatch_resize();
    bool dispatch_input(const htmlml_input_event& event);
    bool pump_task();
    bool has_pending_tasks() const noexcept;
    bool component_ready();
    std::string diagnostics();
    std::string event_diagnostics() const;
    const std::string& last_error() const noexcept;
    uint64_t frame_scripts_executed() const noexcept;
    uint64_t frame_script_errors() const noexcept;
    uint64_t compilation_requests() const noexcept;
    uint64_t compilation_memory_hits() const noexcept;
    uint64_t compilation_persistent_hits() const noexcept;
    uint64_t compilation_persistent_misses() const noexcept;
    uint64_t compilation_cache_rejections() const noexcept;
    uint64_t compilation_cache_bytes_read() const noexcept;
    uint64_t compilation_cache_bytes_written() const noexcept;
    uint64_t compilation_time_nanoseconds() const noexcept;
    uint64_t input_events_dispatched() const noexcept;
    uint64_t input_callbacks_invoked() const noexcept;
    uint64_t last_resize_outer_listeners_nanoseconds() const noexcept;
    uint64_t last_resize_frame_listeners_nanoseconds() const noexcept;
    uint64_t last_resize_layout_nanoseconds() const noexcept;
    uint64_t last_resize_observers_nanoseconds() const noexcept;
    const std::string& frame_last_error() const noexcept;

private:
    struct implementation;
    std::unique_ptr<implementation> impl_;
};

} // namespace htmlml_native

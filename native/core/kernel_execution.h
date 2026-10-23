#pragma once
#include "components/component.h"
#include <set>

namespace jp {
// Shared algorithm-boundary helpers. The legacy C ABI and the managed workflow use the same values.
Json serialize_value(const Value &value);
void validate_parameter(const Json &spec, const Json &value);
bool unavailable_data(const Value &value);
Value skipped_value(const Component &entry, const InputValues &inputs, const Image &frame);

// Owns native pixels and typed kernel values for a single accepted run. It never chooses the next
// node, evaluates parameter expressions, selects a decision or builds the workflow result JSON.
class KernelContext {
    std::shared_ptr<Run> run_;
    std::shared_ptr<Result> result_;
    std::map<std::string, PortValues> values_;
    std::map<std::pair<std::string, std::string>, size_t> encodedBytes_;
    std::set<std::string> completed_, skipped_;
    std::map<const uint8_t *, size_t> imageBuffers_;
    std::set<const void *> contextPayloads_;
    size_t debugBytes_ = 0, measurementBytes_ = 0;
    bool finished_ = false;
    mutable std::mutex gate_;
    void check() const;
    void account_image(const Image &image);
    InputValues inputs(const std::string &node) const;
    const Image &parameter_image(const InputValues &inputs) const;
  public:
    explicit KernelContext(std::shared_ptr<Run> run);
    ~KernelContext();
    Json info() const;
    Json input_metadata(const std::string &node) const;
    Json field(const std::string &node, const std::string &port, const std::string &path) const;
    Json step(const std::string &node, Json parameters);
    Json publish(const std::string &name, bool includeJson);
    void release_node(const std::string &node);
    std::shared_ptr<Result> finish(bool failed);
};
} // namespace jp

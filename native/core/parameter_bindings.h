#pragma once
#include "components/component.h"
#include <functional>

namespace jp {
struct ParameterResolution {
    Json parameters, locals;
    bool deferred = false;
};
Json global_variable_values(const Json &recipe, const Json &overrides = Json::object());
std::map<std::string, std::pair<std::string, std::string>> parameter_dependencies(const Json &node);
ParameterResolution resolve_node_bindings(const Json &node, const Json &publicValues, const Plan &plan,
                                          const std::map<std::string, PortValues> *values, const Json *traces,
                                          const Image *input, const Json &globals,
                                          const std::function<void(const Json &, const Json &)> &validate);
Json result_field(const Value &value, const std::string &field);
} // namespace jp

#pragma once
#include "component.h"

namespace jp {
Json tolerance_parameters();
void validate_tolerance(const Json &parameters);
Json measurement_packet(const std::string &operation, const std::string &unit, const Json &parameters);
void finish_measurement(Json &result, const Json &parameters, double value);
void add_measurement_outputs(Component &definition);
} // namespace jp

#pragma once
#include "graph.h"

namespace jp {
void validate_fixture_reference(const Json &reference, bool required = false);
Json fixture_metadata(const FixtureValue &fixture);
cv::Point2d fixture_point(const FixtureValue &fixture, cv::Point2d point, bool inverse = false);
Json fixture_roi(const FixtureValue &fixture, const Json &roi, const Image &source);
Json fixture_parameters(const Image &source, const Json &parameters, bool rectangularStrip = false);
bool source_fixture_requested(const Image &source, const Json &parameters, const std::string &type);
} // namespace jp

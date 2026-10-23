#pragma once
#include "component.h"
#include <span>

namespace jp {
// One projected edge sample, shared by fitted calipers and the ordered edge-point tool.
struct CaliperEdgeSample {
    bool found = false;
    bool samplingAvailable = false;
    cv::Point2d point;
    double gradient = 0;
};
CaliperEdgeSample sample_caliper_edge(const Image &image, const cv::Mat &gray, cv::Point2d center,
                                      cv::Point2d normal, const Json &parameters);
struct CaliperSamplingStation {
    cv::Point2d center, normal;
};
// Same-index, full-precision samples. Callers retain their geometric/budget validation.
// Parameters, smoothing weights and temporary arrays are prepared once for the whole batch.
std::vector<CaliperEdgeSample> sample_caliper_edges(const Image &image, const cv::Mat &gray,
                                                  std::span<const CaliperSamplingStation> stations,
                                                  const Json &parameters);
void register_edge_point_components(ComponentRegistry &registry);
} // namespace jp

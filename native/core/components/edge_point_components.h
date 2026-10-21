#pragma once
#include "component.h"

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
void register_edge_point_components(ComponentRegistry &registry);
} // namespace jp

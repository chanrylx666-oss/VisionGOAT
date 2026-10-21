#pragma once
#include "graph.h"

namespace jp {
void validate_roi(const Json &roi);
std::vector<cv::Point2d> roi_vertices(const Json &roi);
// Validates ROI geometry and returns its acquired image intersection, possibly empty.
// A partially visible shape is valid; this never allocates an off-image canvas.
cv::Rect checked_roi_bounds(const Image &source, const Json &roi);
Image apply_roi(const Image &source, const Json &roi);
// Match-centered circles may extend past the acquired image; invalid pixels remain unsampled.
Image apply_positioned_circle_roi(const Image &source, const Json &roi);
Image apply_effective_roi(const Image &source, const Json &roi);
bool valid_point(const Image &source, double x, double y);
cv::Mat visible_pixels(const Image &source);
} // namespace jp

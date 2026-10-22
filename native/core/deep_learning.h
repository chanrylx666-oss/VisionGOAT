#pragma once
#include "graph.h"

namespace jp {
// The score image is transient float32 data. Public images remain Gray8/Bgr24.
struct DefectInference {
    Json manifest;
    cv::Mat scores;
    int tileCount = 0;
};
DefectInference infer_defect_scores(const Image &source, const Json &parameters);
} // namespace jp

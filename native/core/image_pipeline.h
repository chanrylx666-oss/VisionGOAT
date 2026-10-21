#pragma once
#include "components/component.h"
#include <functional>

namespace jp {
// Begin once for the accepted frame, before executing any nodes. Every branch in that run
// shares the identity, while a second execution always starts a fresh context.
Image begin_image_pipeline(const Image &image);

// Called after inserting the primary output into ports, before validating output completeness.
// Image pixels remain independent of graphics. Existing typed ports and packet shapes are retained.
void complete_image_pipeline(const std::string &nodeId, const Component &component, const InputValues &inputs,
                             PortValues &ports, const Json &parameters, const Image &frame);

// Returns the latest value of an output type (Features, Contours, PointSet, Pose, Measurement, Result,
// Int, Double, Bool or Decision). Empty/unavailable latest values deliberately shadow older ones.
// Image and Mask are represented by the image itself, never retained inside its context.
const Value *image_context_value(const Image &image, const std::string &type);
Json image_context_metadata(const Image &image);
Json image_context_overlay(const Image &image);
size_t image_context_bytes(const Image &image);

// Callers retaining several branches can count each immutable payload only once, just as
// shared cv::Mat backing allocations are counted once. Includes the context's pointer tables.
void image_context_payloads(const Image &image, const std::function<void(const void *, size_t)> &account);
} // namespace jp

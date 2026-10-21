#pragma once
#include "component.h"

namespace jp {
// Adds the image-input alternative without changing existing first/second Features recipes.
void configure_image_measurement(Component &definition);

struct ImageMeasurementInputs {
    Value first;
    Value second;
};

// Each object is detected independently from the clean input pixels. Coordinates remain original.
ImageMeasurementInputs detect_image_measurement(const Component &definition, const Image &image,
                                                const Json &parameters, const Image &frame);
} // namespace jp

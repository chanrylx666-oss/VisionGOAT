#pragma once
#include "component.h"

namespace jp {
// Keep the original typed contracts available while exposing a single image workflow.
void configure_image_component(Component &component);
Image binary_image(const Image &source, const Json &parameters);
struct PreparedImageInputs {
    PortValues owned;
    InputValues inputs;
};
PreparedImageInputs prepare_image_inputs(const Component &component, const InputValues &inputs,
                                         const Json &parameters, const Image &frame);
} // namespace jp

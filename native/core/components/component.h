#pragma once
#include "graph.h"
#include <functional>

namespace jp {
using ComputeComponent = std::function<Value(const Value *, const Json &, const Image &)>;
using InputValues = std::map<std::string, const Value *>;
using ComputeInputsComponent = std::function<Value(const InputValues &, const Json &, const Image &)>;
using ValidateComponent = std::function<void(const Json &)>;
using PortValues = std::map<std::string, Value>;
using ExpandComponent = std::function<PortValues(const Value &, const Json &)>;
using ExpandInputsComponent = std::function<PortValues(Value &, const Json &, const InputValues &)>;
struct Component {
    Json descriptor;
    ComputeComponent execute;
    ValidateComponent validate;
    ExpandComponent expand;
    ComputeInputsComponent executeInputs;
    ExpandInputsComponent expandInputs;
};
Json parameter(std::string name, std::string label, std::string type, Json value, Json min = nullptr,
               Json max = nullptr, Json choices = nullptr, bool bindable = false);
Json odd_parameter(std::string name, std::string label, int value, int minimum, int maximum,
                   bool allowZero = false);
Component component(const char *type, const char *label, const char *category, const char *inputType,
                    const char *outputType, const char *outputPort, Json parameters, ComputeComponent execute,
                    ValidateComponent validate = {});
void require_odd_kernel(const Json &parameters);
cv::Mat gray_pixels(const Image &image);

class ComponentRegistry {
  public:
    void add(Component definition);
    // The common OpenCV image -> image case needs only metadata and an algorithm lambda.
    void add_image(const char *type, const char *label, const char *category, Json parameters,
                   std::function<cv::Mat(const cv::Mat &, const Json &)> execute,
                   ValidateComponent validate = {});
    const Component &get(const std::string &type) const;
    Json catalog() const;

  private:
    std::vector<Component> components_;
};
const ComponentRegistry &component_registry();
void register_basic_components(ComponentRegistry &registry);
void register_geometry_components(ComponentRegistry &registry);
void register_custom_components(ComponentRegistry &registry);
void register_caliper_components(ComponentRegistry &registry);
void register_template_components(ComponentRegistry &registry);
void register_measurement_components(ComponentRegistry &registry);
void register_fixture_components(ComponentRegistry &registry);
void validate_template(const Json &model, bool requireTrained = false);
void integrate_measurements(Component &definition);
Json circle_caliper_parameters();
void validate_circle_caliper(const Json &parameters);
Features find_circle_caliper(const Image &image, const Json &parameters);
Json line_caliper_parameters();
void validate_line_caliper(const Json &parameters);
Features find_line_caliper(const Image &image, const Json &parameters);
} // namespace jp

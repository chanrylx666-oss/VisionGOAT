#include "image_measurement.h"
#include "roi.h"
#include <array>

namespace jp {
namespace {
struct ControlBinding {
    const char *suffix;
    const char *parameter;
};
constexpr std::array Controls = {
    ControlBinding{"CaliperCount", "caliperCount"},
    ControlBinding{"ProjectionWidth", "projectionWidth"},
    ControlBinding{"SearchLength", "searchLength"},
    ControlBinding{"Sigma", "sigma"},
    ControlBinding{"EdgeThreshold", "edgeThreshold"},
    ControlBinding{"Polarity", "polarity"},
    ControlBinding{"EdgeSelection", "edgeSelection"},
    ControlBinding{"RemovePointCount", "removePointCount"},
    ControlBinding{"MaximumDeviation", "maximumDeviation"},
    ControlBinding{"MinimumCoverage", "minimumCoverage"},
    ControlBinding{"ProjectionStatistic", "projectionStatistic"},
    ControlBinding{"SubpixelRefinement", "subpixelRefinement"},
    ControlBinding{"RoiEdgeIndex", "roiEdgeIndex"},
    ControlBinding{"ReverseSearch", "reverseSearch"},
};

bool measurement_component(const std::string &type) {
    return type == "MeasureCircleDistance" || type == "MeasureLineCircleDistance" ||
           type == "MeasureLineDistance" || type == "MeasureGeometry";
}

std::string object_type(const Component &definition, const Json &p, const std::string &side) {
    auto type = definition.descriptor.at("type").get<std::string>();
    if (type == "MeasureCircleDistance")
        return "circle";
    if (type == "MeasureLineCircleDistance")
        return side == "first" ? "line" : "circle";
    if (type == "MeasureLineDistance")
        return "line";
    auto operation = p.value("operation", std::string("pointDistance"));
    if (operation == "pointDistance")
        return p.value(side + "ObjectType", std::string("circle"));
    if (operation == "pointToLine" && side == "first")
        return p.value(side + "ObjectType", std::string("circle"));
    return "line";
}

void apply_controls(Json &target, const Json &p, const std::string &side, bool circle,
                    bool registeredDetector) {
    target["roi"] = p.at(side + "Roi");
    const auto &roi = target.at("roi");
    auto shape = roi.at("shape").get<std::string>();
    target["method"] = "caliper";
    target["caliperPlacement"] = circle || shape == "polygon" ? "roiBoundary" : "roiCenter";
    auto reference = p.value(side + "RoiReference", std::string("image"));
    if (roi.value("coordinateSpace", "input") == "reference")
        reference = "fixture";
    if (circle)
        target["positionReference"] = reference;
    else
        target["roiReference"] = reference;
    for (const auto &control : Controls) {
        auto name = side + control.suffix;
        if (!p.contains(name))
            continue;
        if (circle && (std::string(control.parameter) == "roiEdgeIndex" ||
                       std::string(control.parameter) == "reverseSearch"))
            continue;
        auto targetName = circle && registeredDetector && std::string(control.parameter) == "edgeThreshold"
                              ? "caliperThreshold"
                              : control.parameter;
        target[targetName] = p.at(name);
    }
}

Json validation_parameters(const Json &p, const std::string &side, bool circle) {
    Json result = Json::object();
    for (const auto &parameter : circle ? circle_caliper_parameters() : line_caliper_parameters())
        result[parameter.at("name").get<std::string>()] = parameter.at("default");
    apply_controls(result, p, side, circle, false);
    return result;
}

void validate_selection(const Component &definition, const Json &p, const std::string &side) {
    const auto &roi = p.at(side + "Roi");
    validate_roi(roi);
    if (roi.at("shape") == "none")
        return; // An unconfigured selection is a normal Unknown result until the user draws it.
    bool circle = object_type(definition, p, side) == "circle";
    auto sampling = validation_parameters(p, side, circle);
    if (circle)
        validate_circle_caliper(sampling);
    else
        validate_line_caliper(sampling);
}

Features unavailable_selection(const Image &source, const std::string &side, const std::string &kind,
                               const std::string &reason) {
    Features features;
    features.dataAvailable = false;
    features.sourceImage = std::make_shared<Image>(source);
    features.diagnostics = {{"reason", reason}, {"measurementSide", side}, {"featureKind", kind}};
    return features;
}

Features detect_selection(const Component &definition, const Image &source, const Json &p, const Image &frame,
                          const std::string &side) {
    auto kind = object_type(definition, p, side);
    if (!source.available || source.pixels.empty())
        return unavailable_selection(source, side, kind,
                                     source.unavailableReason.empty() ? "UnavailableInputImage"
                                                                      : source.unavailableReason);
    if (p.at(side + "Roi").at("shape") == "none")
        return unavailable_selection(source, side, kind, "MissingMeasurementRoi");
    bool circle = kind == "circle";
    const auto &detector = component_registry().get(circle ? "FindCircles" : "FindLines");
    Json sampling = Json::object();
    for (const auto &parameter : detector.descriptor.at("parameters"))
        sampling[parameter.at("name").get<std::string>()] = parameter.at("default");
    apply_controls(sampling, p, side, circle, true);
    // The measurement graphics use the fitted circumference, independently of detector band styling.
    if (sampling.contains("outputAnnulusWidth"))
        sampling["outputAnnulusWidth"] = 0.0;
    if (detector.validate)
        detector.validate(sampling);
    Value input = source;
    require(bool(detector.execute), JPV_INTERNAL_ERROR,
            "Inline image measurement requires a single-image detector callback.");
    auto detected = detector.execute(&input, sampling, frame);
    auto *features = std::get_if<Features>(&detected);
    require(features != nullptr, JPV_INTERNAL_ERROR,
            "Inline image measurement detector did not return geometry.");
    features->diagnostics["measurementSide"] = side;
    features->diagnostics["featureKind"] = kind;
    if (!features->sourceImage)
        features->sourceImage = std::make_shared<Image>(source);
    return std::move(*features);
}
} // namespace

void configure_image_measurement(Component &definition) {
    auto type = definition.descriptor.at("type").get<std::string>();
    if (!measurement_component(type) || definition.descriptor.value("imageMeasurement", false))
        return;
    definition.descriptor["imageMeasurement"] = true;
    auto &inputs = definition.descriptor["inputs"];
    for (auto &input : inputs)
        input["hidden"] = true;
    inputs.insert(inputs.begin(), Json{{"id", "input"},
                                       {"type", "Image"},
                                       {"label", "输入图像"},
                                       {"acceptedTypes", Json::array({"Image", "Mask"})}});
    definition.descriptor["inputGroups"] =
        Json::array({Json::array({"input"}), Json::array({"first", "second"})});

    auto schemas = line_caliper_parameters();
    auto &parameters = definition.descriptor["parameters"];
    for (const auto &side : {std::string("first"), std::string("second")}) {
        const std::string label = side == "first" ? "A" : "B";
        const std::string group = "测量对象 " + label;
        auto roi = parameter(side + "Roi", "对象 " + label + " ROI", "roi", Json{{"shape", "none"}});
        roi["group"] = group;
        roi["description"] = "在输入图像上绘制对象范围；使用真实边缘点进行卡尺拟合。";
        parameters.push_back(std::move(roi));
        if (type == "MeasureGeometry") {
            auto object = parameter(side + "ObjectType", "对象 " + label + " 类型", "enum", "circle", nullptr,
                                    nullptr, Json::array({"circle", "line"}));
            object["group"] = group;
            object["description"] = "点距可选择圆心或拟合直线上的点；线距、夹角和交点自动使用直线。";
            parameters.push_back(std::move(object));
        }
        auto reference = parameter(side + "RoiReference", "对象 " + label + " ROI 坐标基准", "enum", "image",
                                   nullptr, nullptr, Json::array({"image", "fixture"}));
        reference["group"] = group;
        reference["description"] = "默认使用当前输入图像坐标；定位基准需要上游有效的位置修正结果。";
        parameters.push_back(std::move(reference));
        for (const auto &control : Controls)
            for (auto schema : schemas)
                if (schema.at("name") == control.parameter) {
                    // Keep the existing controls for saved recipes, while a fixed circle
                    // side retains the bounded circle-fitting workload. Dynamic geometry
                    // sides are validated against their selected type at run time.
                    bool fixedCircle = type == "MeasureCircleDistance" ||
                                       (type == "MeasureLineCircleDistance" && side == "second");
                    if (fixedCircle && schema.at("name") == "caliperCount") {
                        schema["max"] = 256;
                        schema["description"] =
                            "沿圆周布置 4～256 个卡尺；圆拟合不使用直线的高密度卡尺上限。";
                    } else if (fixedCircle && schema.at("name") == "removePointCount") {
                        schema["max"] = 253;
                        schema["description"] = "首次圆拟合后剔除指定数量的最差点，再按最大拟合偏差筛选；"
                                                "需保留至少 max(3, ceil(卡尺数量 × 最低有效比例)) 个真实点。";
                    }
                    schema["name"] = side + control.suffix;
                    schema["label"] = "对象 " + label + " · " + schema.at("label").get<std::string>();
                    schema["group"] = group;
                    parameters.push_back(std::move(schema));
                    break;
                }
    }

    // Do not retain Component itself: its callbacks would recursively retain this validator.
    Component descriptorOnly;
    descriptorOnly.descriptor = {{"type", type}};
    auto validate = std::move(definition.validate);
    definition.validate = [validate = std::move(validate),
                           descriptorOnly = std::move(descriptorOnly)](const Json &p) {
        if (validate)
            validate(p);
        validate_selection(descriptorOnly, p, "first");
        validate_selection(descriptorOnly, p, "second");
    };
}

ImageMeasurementInputs detect_image_measurement(const Component &definition, const Image &image,
                                                const Json &parameters, const Image &frame) {
    require(definition.descriptor.value("imageMeasurement", false), JPV_INTERNAL_ERROR,
            "This component does not support inline image measurement.");
    return {detect_selection(definition, image, parameters, frame, "first"),
            detect_selection(definition, image, parameters, frame, "second")};
}
} // namespace jp

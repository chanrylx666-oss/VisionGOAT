#include "component.h"
#include "roi.h"
#include "fixture.h"
#include "image_components.h"
#include "annular_components.h"
#include "edge_point_components.h"
#include "blob_components.h"
#include <opencv2/imgproc.hpp>

namespace jp {
Json parameter(std::string name, std::string label, std::string type, Json value, Json min, Json max,
               Json choices, bool bindable) {
    Json p = {{"name", name}, {"label", label}, {"type", type}, {"default", value}, {"bindable", bindable}};
    if (!min.is_null())
        p["min"] = min;
    if (!max.is_null())
        p["max"] = max;
    if (!choices.is_null())
        p["choices"] = choices;
    return p;
}
Json odd_parameter(std::string name, std::string label, int value, int minimum, int maximum, bool allowZero) {
    auto p = parameter(std::move(name), std::move(label), "integer", value, minimum, maximum);
    p["step"] = 2;
    p["odd"] = true;
    p["allowZero"] = allowZero;
    return p;
}
Component component(const char *type, const char *label, const char *category, const char *inputType,
                    const char *outputType, const char *outputPort, Json parameters, ComputeComponent execute,
                    ValidateComponent validate) {
    Json inputs = Json::array();
    if (*inputType)
        inputs.push_back({{"id", "input"}, {"type", inputType}, {"label", "输入"}});
    return {{{"type", type},
             {"version", 1},
             {"label", label},
             {"category", category},
             {"inputs", inputs},
             {"outputs", Json::array({{{"id", outputPort}, {"type", outputType}, {"label", "输出"}}})},
             {"parameters", parameters}},
            std::move(execute),
            std::move(validate)};
}
void require_odd_kernel(const Json &p) {
    require(p.at("kernel").get<int>() % 2 == 1, JPV_INVALID_RECIPE, "Kernel size must be odd.");
    if (p.contains("kernelHeight"))
        require(p.at("kernelHeight") == 0 || p.at("kernelHeight").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
                "Kernel height must be zero or odd.");
}
cv::Mat gray_pixels(const Image &image) {
    if (image.pixels.type() == CV_8UC1)
        return image.pixels;
    cv::Mat gray;
    cv::cvtColor(image.pixels, gray, cv::COLOR_BGR2GRAY);
    return gray;
}
void ComponentRegistry::add(Component definition) {
    if (definition.descriptor.at("outputs")[0].at("type") == "Features")
        integrate_measurements(definition);
    auto type = definition.descriptor.at("type").get<std::string>();
    definition.descriptor["toolboxVisible"] = type != "CropRect" && type != "CircleCaliper" &&
                                              type != "CountFeatures" && type != "BuildFeatureOverlay" &&
                                              type != "CompareNumber" && type != "CountContours";
    const auto &inputs = definition.descriptor.at("inputs");
    if (!definition.executeInputs && !inputs.empty() &&
        (inputs[0].at("type") == "Image" || inputs[0].at("type") == "Mask")) {
        auto &parameters = definition.descriptor["parameters"];
        bool moduleRoi = type != "CropRect";
        if (moduleRoi)
            parameters.insert(parameters.begin(),
                              parameter("roi", "检测 ROI", "roi",
                                        definition.descriptor.value("defaultRoi", Json{{"shape", "none"}})));
        if (moduleRoi && type != "FindCircles")
            parameters.push_back(parameter("roiReference", "ROI 坐标基准", "enum", "image", nullptr, nullptr,
                                           Json::array({"image", "fixture"})));
        auto execute = std::move(definition.execute);
        bool mask = inputs[0].at("type") == "Mask";
        bool referenceOnly = definition.descriptor.value("roiReferenceOnly", false);
        bool coordinateTransform = definition.descriptor.value("coordinateTransform", false);
        bool caliper =
            type == "CircleCaliper" || type == "LineCaliper" || type == "FindCircles" || type == "FindLines";
        auto outputType = definition.descriptor.at("outputs")[0].at("type").get<std::string>();
        definition.execute = [execute = std::move(execute), mask, caliper, moduleRoi, outputType,
                              referenceOnly, coordinateTransform,
                              type](const Value *input, const Json &p, const Image &original) -> Value {
            const auto &upstream = std::get<Image>(*input);
            // An unresolved subscription has no current ROI. Structured components build an
            // explicit unavailable packet before validating image bounds of teaching placeholders.
            if (outputType == "Result" && p.contains("_unavailable"))
                return execute(input, p, original);
            // An absent match is a normal empty inspection, never permission to inspect the full image.
            // Its ROI coordinates have no current target frame, so skip runtime ROI bounds checks too.
            if (!upstream.available) {
                if (outputType == "Features") {
                    Features empty;
                    empty.sourceImage = std::make_shared<Image>(upstream);
                    empty.dataAvailable = false;
                    return empty;
                }
                if (outputType == "Image" || outputType == "Mask")
                    return upstream;
                if (outputType == "Contours")
                    return Contours{{}, upstream.x, upstream.y, false};
                if (outputType == "Result")
                    return execute(input, p, original);
                throw Error(JPV_INVALID_ARGUMENT,
                            "Input image is unavailable: no upstream target was selected.");
            }
            // Boundary ROIs define the reference path. The projection windows must see both sides
            // of that path, while retaining any validity mask imposed by an upstream component.
            Json effective = p, position = Json::object();
            bool positioned = false;
            bool fixtured = source_fixture_requested(upstream, p, type);
            if (fixtured) {
                effective = fixture_parameters(upstream, p, type == "FindEdgePoints");
                positioned = true;
            } else if (type == "FindCircles" && p.at("positionReference") != "image") {
                require(upstream.matchPosition || p.at("positionReference") == "auto", JPV_INVALID_ARGUMENT,
                        "Circle match-center reference requires a selected upstream matching result.");
                if (upstream.matchPosition) {
                    const auto &match = *upstream.matchPosition;
                    auto shape = p.at("roi").at("shape").get<std::string>();
                    require(shape == "circle" || shape == "annulus" || shape == "none", JPV_INVALID_ARGUMENT,
                            "Match-center circle search requires a circle ROI or automatic search radius.");
                    double offsetX = p.at("matchOffsetX"), offsetY = p.at("matchOffsetY"),
                           radius = shape == "circle" ? p.at("roi").at("radius").get<double>()
                                                      : p.at("matchSearchRadius").get<double>();
                    effective["roi"] =
                        shape == "annulus" ? p.at("roi") : Json{{"shape", "circle"}, {"radius", radius}};
                    effective["roi"]["cx"] = match.centerX - upstream.x + offsetX;
                    effective["roi"]["cy"] = match.centerY - upstream.y + offsetY;
                    if (p.at("method") == "caliper")
                        effective["caliperPlacement"] = "roiBoundary";
                    position = {{"source", "matchCenter"},
                                {"centerX", match.centerX},
                                {"centerY", match.centerY},
                                {"offsetX", offsetX},
                                {"offsetY", offsetY},
                                {"searchCenterX", match.centerX + offsetX},
                                {"searchCenterY", match.centerY + offsetY},
                                {"angleDegrees", match.angleDegrees},
                                {"scale", match.scale},
                                {"transform", "translation"}};
                    positioned = true;
                }
            }
            require(!moduleRoi || fixtured || p.at("roi").value("coordinateSpace", "input") != "reference",
                    JPV_INVALID_ARGUMENT, "Reference-coordinate ROI requires fixture positioning.");
            bool activeCaliper = caliper && ((type != "FindCircles" && type != "FindLines") ||
                                             effective.at("method") == "caliper");
            if (activeCaliper && effective.at("roi").at("shape") == "annulus") {
                const auto &ring = effective.at("roi");
                effective["caliperPlacement"] = "roiBoundary";
                effective["searchLength"] =
                    ring.at("outerRadius").get<double>() - ring.at("innerRadius").get<double>();
                effective["startAngle"] = ring.at("startAngle");
                effective["endAngle"] = ring.at("endAngle");
            }
            bool strip = activeCaliper && effective.at("caliperPlacement") == "roiCenter";
            if (strip)
                effective["searchLength"] = effective.at("roi").at("height");
            bool boundary = activeCaliper && (effective.at("caliperPlacement") == "roiBoundary" || strip);
            if (activeCaliper)
                require((2 * std::ceil(effective.at("searchLength").get<double>() / 2) + 1) *
                                effective.at("projectionWidth").get<int>() *
                                effective.at("caliperCount").get<int>() <=
                            16ULL * 1024 * 1024,
                        JPV_RESOURCE_LIMIT,
                        "Caliper sampling budget exceeded; reduce caliperCount, projectionWidth or search "
                        "height.");
            Image source = upstream;
            // Calipers and reference-only tools sample their original geometric windows.
            // Their sampler checks every footprint against image bounds and validity masks;
            // clipping or requiring the complete ROI would move the teaching geometry or
            // reject usable edge pixels. Ordinary image tools use the clipped shape mask.
            if (!boundary && !referenceOnly && moduleRoi)
                source = fixtured     ? apply_effective_roi(upstream, effective.at("roi"))
                         : positioned ? apply_effective_roi(upstream, effective.at("roi"))
                                      : apply_roi(upstream, effective.at("roi"));
            source.padded = upstream.padded;
            source.binaryMask = upstream.binaryMask;
            source.pipelineContext = upstream.pipelineContext;
            copy_image_coordinates(source, upstream);
            if (!source.valid.empty() && cv::countNonZero(source.valid) == 0)
                source.available = false;
            if (!source.available && source.unavailableReason.empty())
                source.unavailableReason = "UnavailableRoi";
            if (!source.available && outputType == "Features") {
                Features empty;
                empty.dataAvailable = false;
                empty.sourceImage = std::make_shared<Image>(upstream);
                empty.position = position;
                return empty;
            }
            if (!source.available && (outputType == "Image" || outputType == "Mask"))
                return source;
            if (!source.available && outputType == "Contours")
                return Contours{{}, source.x, source.y, false};
            if (mask)
                source.pixels = visible_pixels(source);
            Value selected = source;
            Value output = execute(&selected, effective, original);
            if (auto features = std::get_if<Features>(&output)) {
                features->sourceImage = std::make_shared<Image>(upstream);
                features->position = std::move(position);
                if (fixtured) {
                    features->fixture = fixture_metadata(*upstream.fixture);
                    features->fixture["coordinateSpace"] = upstream.coordinateSpace;
                }
            }
            if (auto contours = std::get_if<Contours>(&output); contours && !contours->sourceImage)
                contours->sourceImage = std::make_shared<Image>(source);
            if (auto image = std::get_if<Image>(&output)) {
                if (!coordinateTransform) {
                    image->available = source.available;
                    image->unavailableReason = source.unavailableReason;
                    image->padded = source.padded;
                    image->matchPosition = source.matchPosition;
                    image->fixture = source.fixture;
                    copy_image_coordinates(*image, source);
                    if (image->pixels.size() == source.pixels.size())
                        image->valid = source.valid;
                }
            }
            return output;
        };
    }
    require(!type.empty() && (bool(definition.execute) != bool(definition.executeInputs)), JPV_INTERNAL_ERROR,
            "Incomplete component definition.");
    require(definition.executeInputs || inputs.size() <= 1, JPV_INTERNAL_ERROR,
            "Multiple input ports require a multi-input component callback.");
    for (const auto &existing : components_)
        require(existing.descriptor.at("type").get<std::string>() != type, JPV_INTERNAL_ERROR,
                "Duplicate component registration: " + type);
    configure_image_component(definition);
    for (auto &p : definition.descriptor["parameters"])
        if (p.at("type") == "number" || p.at("type") == "integer" || p.at("type") == "bool")
            p["bindable"] = true;
    components_.push_back(std::move(definition));
}
void ComponentRegistry::add_image(const char *type, const char *label, const char *category, Json parameters,
                                  std::function<cv::Mat(const cv::Mat &, const Json &)> execute,
                                  ValidateComponent validate) {
    add(component(
        type, label, category, "Image", "Image", "image", std::move(parameters),
        [execute = std::move(execute)](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            auto pixels = execute(source.pixels, p);
            require(!pixels.empty() && (pixels.type() == CV_8UC1 || pixels.type() == CV_8UC3) &&
                        pixels.size() == source.pixels.size(),
                    JPV_INVALID_ARGUMENT, "Image component must preserve size and return Gray8/Bgr24.");
            return Image{std::move(pixels), source.x, source.y};
        },
        std::move(validate)));
}
const Component &ComponentRegistry::get(const std::string &type) const {
    for (const auto &entry : components_)
        if (entry.descriptor.at("type").get<std::string>() == type)
            return entry;
    throw Error(JPV_INVALID_RECIPE, "Unknown node type: " + type);
}
Json ComponentRegistry::catalog() const {
    Json result = Json::array();
    for (const auto &entry : components_)
        result.push_back(entry.descriptor);
    return result;
}
const ComponentRegistry &component_registry() {
    static const ComponentRegistry registry = [] {
        ComponentRegistry value;
        register_basic_components(value);
        register_geometry_components(value);
        register_caliper_components(value);
        register_template_components(value);
        register_measurement_components(value);
        register_fixture_components(value);
        register_custom_components(value);
        register_annular_components(value);
        register_edge_point_components(value);
        register_blob_components(value);
        return value;
    }();
    return registry;
}
Json node_catalog() {
    return component_registry().catalog();
}
} // namespace jp

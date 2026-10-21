#include "image_components.h"
#include "image_measurement.h"
#include "image_pipeline.h"
#include "roi.h"
#include "fixture.h"
#include <opencv2/imgproc.hpp>

namespace jp {
static bool measurement_component(const std::string &type) {
    return type == "MeasureGeometry" || type == "MeasureLineDistance" || type == "MeasureCircleDistance" ||
           type == "MeasureLineCircleDistance";
}
static void segmentation_parameters(Json &parameters) {
    auto method = parameter("imageThresholdMethod", "图像分割方式", "enum", "fixed", nullptr, nullptr,
                            Json::array({"fixed", "otsu", "triangle"}));
    method["group"] = "图像分割";
    method["description"] = "普通图像先转换为灰度并分割；二值图像保留已有掩膜。";
    parameters.push_back(std::move(method));
    auto value = parameter("imageThreshold", "图像分割阈值", "integer", 128, 0, 255);
    value["group"] = "图像分割";
    parameters.push_back(std::move(value));
    auto invert = parameter("imageInvert", "反向图像分割", "bool", false);
    invert["group"] = "图像分割";
    parameters.push_back(std::move(invert));
}
void configure_image_component(Component &definition) {
    auto type = definition.descriptor.at("type").get<std::string>();
    definition.descriptor["imagePipeline"] = true;
    if (measurement_component(type))
        configure_image_measurement(definition);
    else {
        for (auto &input : definition.descriptor["inputs"]) {
            const auto original = input.at("type").get<std::string>();
            if (original == "Image")
                continue;
            if (type == "PositionFixture" && original == "Pose") {
                input["hidden"] = true;
                continue;
            }
            input["valueType"] = original;
            input["acceptedTypes"] = Json::array({"Image", original});
            input["type"] = "Image";
        }
        if (type == "PositionFixture")
            definition.descriptor["inputGroups"] =
                Json::array({Json::array({"input"}), Json::array({"input", "position"})});
    }
    bool needsSegmentation = false;
    for (const auto &input : definition.descriptor["inputs"]) {
        auto expected = input.value("valueType", input.at("type").get<std::string>());
        needsSegmentation |=
            expected == "Mask" || expected == "Contours" || expected == "Features" || expected == "Int";
    }
    if (needsSegmentation && !measurement_component(type))
        segmentation_parameters(definition.descriptor["parameters"]);
    bool contourInput = false;
    for (const auto &input : definition.descriptor["inputs"])
        contourInput |= input.value("valueType", std::string()) == "Contours";
    if (contourInput) {
        definition.descriptor["parameters"].push_back(
            parameter("roi", "检测 ROI", "roi", Json{{"shape", "none"}}));
        definition.descriptor["parameters"].push_back(parameter("roiReference", "ROI 坐标基准", "enum",
                                                                "image", nullptr, nullptr,
                                                                Json::array({"image", "fixture"})));
    }
    bool hasImage = false, hasOverlay = false;
    for (auto &output : definition.descriptor["outputs"]) {
        bool image = output.at("id") == "image" && output.at("type") == "Image";
        output["hidden"] = !image;
        if (image) {
            output["label"] = "图像与结果";
            hasImage = true;
        }
        hasOverlay |= output.at("id") == "overlay";
    }
    if (!hasImage)
        definition.descriptor["outputs"].push_back(
            {{"id", "image"}, {"type", "Image"}, {"label", "图像与结果"}});
    if (!hasOverlay)
        definition.descriptor["outputs"].push_back(
            {{"id", "overlay"}, {"type", "Overlay"}, {"label", "结果标记"}, {"hidden", true}});
}
Image binary_image(const Image &source, const Json &parameters) {
    if (!source.available || source.binaryMask)
        return source;
    Image mask = source;
    const auto gray = gray_pixels(source);
    // cv::Mat is shared, not copy-on-write. Always allocate an independent destination.
    mask.pixels = cv::Mat();
    int flags = parameters.value("imageInvert", false) ? cv::THRESH_BINARY_INV : cv::THRESH_BINARY;
    const auto method = parameters.value("imageThresholdMethod", std::string("fixed"));
    if (method == "otsu")
        flags |= cv::THRESH_OTSU;
    if (method == "triangle")
        flags |= cv::THRESH_TRIANGLE;
    if (source.valid.empty())
        cv::threshold(gray, mask.pixels, parameters.value("imageThreshold", 128), 255, flags);
    else {
        mask.pixels = cv::Mat::zeros(gray.size(), CV_8UC1);
        cv::thresholdWithMask(gray, mask.pixels, source.valid, parameters.value("imageThreshold", 128), 255,
                              flags);
    }
    mask.binaryMask = true;
    return mask;
}
static Contours image_contours(const Image &source, const Json &p) {
    Image selected = source;
    if (source.available && p.contains("roi")) {
        if (p.value("roiReference", std::string("image")) == "fixture" ||
            p.at("roi").value("coordinateSpace", std::string("input")) == "reference") {
            auto effective = fixture_parameters(source, p);
            selected = apply_effective_roi(source, effective.at("roi"));
        } else
            selected = apply_roi(source, p.at("roi"));
        selected.binaryMask = source.binaryMask;
        selected.pipelineContext = source.pipelineContext;
        selected.matchPosition = source.matchPosition;
        selected.fixture = source.fixture;
        selected.padded = source.padded || selected.padded;
        copy_image_coordinates(selected, source);
    }
    auto mask = binary_image(selected, p);
    Contours contours{{}, mask.x, mask.y, mask.available};
    contours.sourceImage = std::make_shared<Image>(mask);
    if (!mask.available || (!mask.valid.empty() && cv::countNonZero(mask.valid) == 0)) {
        contours.dataAvailable = false;
        contours.sourceImage->available = false;
        if (contours.sourceImage->unavailableReason.empty())
            contours.sourceImage->unavailableReason = "UnavailableRoi";
        return contours;
    }
    cv::findContours(visible_pixels(mask).clone(), contours.items, cv::RETR_EXTERNAL,
                     cv::CHAIN_APPROX_SIMPLE);
    size_t count = 0;
    for (const auto &item : contours.items)
        count += item.size();
    require(contours.items.size() <= 10000 && count <= 1000000, JPV_RESOURCE_LIMIT,
            "Contour result exceeds limit.");
    return contours;
}
static Json default_parameters(const Component &definition) {
    Json p = Json::object();
    for (const auto &parameter : definition.descriptor.at("parameters"))
        p[parameter.at("name").get<std::string>()] = parameter.at("default");
    return p;
}
PreparedImageInputs prepare_image_inputs(const Component &definition, const InputValues &source,
                                         const Json &p, const Image &frame) {
    PreparedImageInputs prepared;
    prepared.inputs = source;
    const auto type = definition.descriptor.at("type").get<std::string>();
    if (measurement_component(type) && source.contains("input")) {
        const auto &image = std::get<Image>(*source.at("input"));
        auto detected = detect_image_measurement(definition, image, p, frame);
        prepared.owned.emplace("first", std::move(detected.first));
        prepared.owned.emplace("second", std::move(detected.second));
    } else {
        for (const auto &port : definition.descriptor.at("inputs")) {
            const auto id = port.at("id").get<std::string>();
            if (!source.contains(id))
                continue;
            const auto image = std::get_if<Image>(source.at(id));
            const auto expected = port.value("valueType", port.at("type").get<std::string>());
            if (!image || expected == "Image" || expected == "Mask")
                continue;
            if (expected == "Contours")
                prepared.owned.emplace(id, image_contours(*image, p));
            else if (expected == "Features") {
                if (const auto value = image_context_value(*image, "Features")) {
                    auto features = std::get<Features>(*value);
                    features.sourceImage = std::make_shared<Image>(*image);
                    prepared.owned.emplace(id, std::move(features));
                } else {
                    const auto &analyze = component_registry().get("AnalyzeContours");
                    Value contours = image_contours(*image, p);
                    auto features =
                        std::get<Features>(analyze.execute(&contours, default_parameters(analyze), frame));
                    features.sourceImage = std::make_shared<Image>(*image);
                    prepared.owned.emplace(id, std::move(features));
                }
            } else if (expected == "Int") {
                if (const auto value = image_context_value(*image, "Int"))
                    prepared.owned.emplace(id, *value);
                else if (const auto features = image_context_value(*image, "Features"))
                    prepared.owned.emplace(id, int64_t(std::get<Features>(*features).items.size()));
                else
                    prepared.owned.emplace(id, int64_t(image_contours(*image, p).items.size()));
            }
        }
        if (type == "PositionFixture" && !source.contains("position")) {
            const auto &image = std::get<Image>(*source.at("input"));
            if (const auto pose = image_context_value(image, "Pose"))
                prepared.owned.emplace("position", *pose);
            else {
                PoseValue unavailablePose;
                unavailablePose.reason = "MissingImagePosition";
                prepared.owned.emplace("position", std::move(unavailablePose));
            }
        }
    }
    for (auto &[id, value] : prepared.owned)
        prepared.inputs[id] = &value;
    if (type == "PositionFixture") {
        const auto &image = std::get<Image>(*prepared.inputs.at("input"));
        const auto &pose = std::get<PoseValue>(*prepared.inputs.at("position"));
        require(!pose.available || (image.coordinatePlane == pose.coordinatePlane &&
                                    image.coordinateSpace == pose.coordinateSpace),
                JPV_INVALID_ARGUMENT, "Image and position belong to different image coordinate planes.");
    }
    return prepared;
}
} // namespace jp

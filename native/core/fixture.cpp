#include "fixture.h"
#include "components/component.h"
#include "roi.h"
#include <cmath>
#include <numbers>

namespace jp {
bool source_fixture_requested(const Image &source, const Json &p, const std::string &type) {
    if (type == "FindCircles") {
        auto mode = p.at("positionReference").get<std::string>();
        return mode == "fixture" || (mode == "auto" && source.fixture.has_value());
    }
    return p.value("roiReference", "image") == "fixture";
}
static Json position_metadata(const MatchPosition &p) {
    return {
        {"centerX", p.centerX}, {"centerY", p.centerY}, {"angleDegrees", p.angleDegrees}, {"scale", p.scale}};
}
Json pose_metadata(const PoseValue &pose) {
    return {{"available", pose.available},
            {"position", pose.available ? position_metadata(pose.position) : Json()},
            {"modelHash", pose.modelHash},
            {"reason", pose.reason},
            {"coordinateSpace", pose.coordinateSpace},
            {"coordinatePlane", pose.coordinatePlane}};
}
void validate_fixture_reference(const Json &reference, bool required) {
    require(reference.is_object(), JPV_INVALID_RECIPE, "Fixture reference must be an object.");
    if (reference.empty()) {
        require(!required, JPV_INVALID_RECIPE, "Teach the fixture reference before running.");
        return;
    }
    require(reference.size() == 5 && reference.contains("modelHash") && reference.at("modelHash").is_string(),
            JPV_INVALID_RECIPE, "Fixture reference requires pose and model hash.");
    for (const auto &[name, limit] : {std::pair{"centerX", 65536.0}, std::pair{"centerY", 65536.0},
                                      std::pair{"angleDegrees", 360.0}, std::pair{"scale", 10.0}})
        require(reference.contains(name) && reference.at(name).is_number() &&
                    std::isfinite(reference.at(name).get<double>()) &&
                    std::abs(reference.at(name).get<double>()) <= limit,
                JPV_INVALID_RECIPE, "Invalid fixture reference pose.");
    require(reference.at("scale").get<double>() >= .1, JPV_INVALID_RECIPE,
            "Fixture reference scale must be positive.");
    auto hash = reference.at("modelHash").get<std::string>();
    require(hash.size() == 64 && hash.find_first_not_of("0123456789abcdef") == std::string::npos,
            JPV_INVALID_RECIPE, "Invalid fixture reference model hash.");
}
cv::Point2d fixture_point(const FixtureValue &fixture, cv::Point2d point, bool inverse) {
    const auto &a = inverse ? fixture.current : fixture.reference;
    const auto &b = inverse ? fixture.reference : fixture.current;
    double angle = (b.angleDegrees - a.angleDegrees) * std::numbers::pi / 180, scale = b.scale / a.scale;
    auto delta = point - cv::Point2d(a.centerX, a.centerY);
    return {b.centerX + scale * (std::cos(angle) * delta.x - std::sin(angle) * delta.y),
            b.centerY + scale * (std::sin(angle) * delta.x + std::cos(angle) * delta.y)};
}
Json fixture_metadata(const FixtureValue &fixture) {
    double radians = (fixture.current.angleDegrees - fixture.reference.angleDegrees) * std::numbers::pi / 180;
    double scale = fixture.current.scale / fixture.reference.scale, a = scale * std::cos(radians),
           b = scale * std::sin(radians);
    auto zero = fixture_point(fixture, {});
    auto reference = position_metadata(fixture.reference);
    reference["modelHash"] = fixture.modelHash;
    return {{"referenceHash", sha256(reference.dump())},
            {"reference", position_metadata(fixture.reference)},
            {"current", position_metadata(fixture.current)},
            {"modelHash", fixture.modelHash},
            {"transform", "similarity"},
            {"coordinateSpace", "original"},
            {"matrix", Json::array({a, -b, zero.x, b, a, zero.y})}};
}
Json fixture_roi(const FixtureValue &fixture, const Json &roi, const Image &source) {
    if (roi.contains("referenceHash"))
        require(roi.at("referenceHash") == fixture_metadata(fixture).at("referenceHash"), JPV_INVALID_RECIPE,
                "Fixture reference changed; redraw this ROI.");
    auto shape = roi.at("shape").get<std::string>();
    if (shape == "none")
        return roi;
    auto point = [&](double x, double y) {
        return fixture_point(fixture, {x, y}) - cv::Point2d(source.x, source.y);
    };
    double scale = fixture.current.scale / fixture.reference.scale;
    if (shape == "circle" || shape == "annulus" || shape == "rotatedRectangle") {
        auto center = point(roi.at("cx"), roi.at("cy"));
        if (shape == "annulus") {
            double start = std::remainder(roi.at("startAngle").get<double>() + fixture.current.angleDegrees -
                                              fixture.reference.angleDegrees,
                                          360.0);
            return {
                {"shape", shape},
                {"cx", center.x},
                {"cy", center.y},
                {"innerRadius", roi.at("innerRadius").get<double>() * scale},
                {"outerRadius", roi.at("outerRadius").get<double>() * scale},
                {"startAngle", start},
                {"endAngle", start + roi.at("endAngle").get<double>() - roi.at("startAngle").get<double>()}};
        }
        if (shape == "rotatedRectangle")
            return {{"shape", shape},
                    {"cx", center.x},
                    {"cy", center.y},
                    {"width", roi.at("width").get<double>() * scale},
                    {"height", roi.at("height").get<double>() * scale},
                    {"angleDegrees",
                     std::remainder(roi.at("angleDegrees").get<double>() + fixture.current.angleDegrees -
                                        fixture.reference.angleDegrees,
                                    360.0)}};
        return {{"shape", "circle"},
                {"cx", center.x},
                {"cy", center.y},
                {"radius", roi.at("radius").get<double>() * scale}};
    }
    Json vertices = Json::array();
    if (shape == "rectangle") {
        double x = roi.at("x"), y = roi.at("y"), w = roi.at("width"), h = roi.at("height");
        for (auto corner :
             {cv::Point2d(x, y), cv::Point2d(x + w, y), cv::Point2d(x + w, y + h), cv::Point2d(x, y + h)}) {
            auto p = point(corner.x, corner.y);
            vertices.push_back({p.x, p.y});
        }
    } else {
        require(shape == "polygon", JPV_INVALID_RECIPE, "Unsupported fixtured ROI shape.");
        for (const auto &corner : roi.at("points")) {
            auto p = point(corner[0], corner[1]);
            vertices.push_back({p.x, p.y});
        }
    }
    return {{"shape", "polygon"}, {"points", vertices}};
}
Json fixture_parameters(const Image &source, const Json &parameters, bool rectangularStrip) {
    require(source.fixture.has_value(), JPV_INVALID_ARGUMENT,
            "This ROI requires an upstream position fixture.");
    const auto &fixture = *source.fixture;
    Json effective = parameters;
    auto roi = parameters.at("roi");
    bool edgePair = parameters.contains("expectedWidth");
    if ((rectangularStrip || edgePair || parameters.value("caliperPlacement", "") == "roiCenter") &&
        roi.at("shape") == "rectangle") {
        roi["cx"] = roi.at("x").get<double>() + roi.at("width").get<double>() / 2;
        roi["cy"] = roi.at("y").get<double>() + roi.at("height").get<double>() / 2;
        roi["shape"] = "rotatedRectangle";
        roi["angleDegrees"] = 0.0;
        roi.erase("x");
        roi.erase("y");
    }
    effective["roi"] = fixture_roi(fixture, roi, source);
    double scale = fixture.current.scale / fixture.reference.scale;
    double rotation = fixture.current.angleDegrees - fixture.reference.angleDegrees;
    if (edgePair)
        for (auto key : {"expectedWidth", "minimumWidth", "maximumWidth"})
            effective[key] = parameters.at(key).get<double>() * scale;
    if (effective.at("roi").at("shape") == "none" && parameters.contains("matchSearchRadius"))
        effective["roi"] = {{"shape", "circle"},
                            {"cx", fixture.current.centerX - source.x},
                            {"cy", fixture.current.centerY - source.y},
                            {"radius", parameters.at("matchSearchRadius").get<double>() * scale}};
    if (effective.contains("caliperPlacement") &&
        (!effective.contains("method") || effective.at("method") == "caliper")) {
        effective["caliperPlacement"] =
            parameters.at("caliperPlacement") == "roiCenter" ? "roiCenter" : "roiBoundary";
        int width = std::max(1, int(std::round(parameters.at("projectionWidth").get<int>() * scale)));
        if (width % 2 == 0)
            ++width;
        require(width <= 631, JPV_RESOURCE_LIMIT, "Scaled caliper projection width exceeds 631 pixels.");
        effective["projectionWidth"] = width;
        effective["searchLength"] = parameters.at("searchLength").get<double>() * scale;
        require(effective.at("searchLength").get<double>() * width *
                        parameters.at("caliperCount").get<int>() <=
                    16ULL * 1024 * 1024,
                JPV_RESOURCE_LIMIT, "Scaled caliper sampling budget exceeded.");
        effective["maximumDeviation"] = parameters.at("maximumDeviation").get<double>() * scale;
        if (effective.contains("startAngle")) {
            double start = std::remainder(parameters.at("startAngle").get<double>() + rotation, 360.0);
            effective["startAngle"] = start;
            effective["endAngle"] =
                start + parameters.at("endAngle").get<double>() - parameters.at("startAngle").get<double>();
        }
        if (parameters.at("roi").at("shape") == "rectangle" && effective.contains("roiEdge")) {
            auto side = parameters.at("roiEdge").get<std::string>();
            effective["roiEdgeIndex"] = side == "top" ? 0 : side == "right" ? 1 : side == "bottom" ? 2 : 3;
        }
    }
    return effective;
}
void register_fixture_components(ComponentRegistry &registry) {
    auto definition =
        component("PositionFixture", "位置修正", "定位", "", "Image", "image",
                  Json::array({parameter("reference", "教学基准", "fixtureReference", Json::object())}), {},
                  [](const Json &p) { validate_fixture_reference(p.at("reference")); });
    definition.descriptor["inputs"] =
        Json::array({{{"id", "input"}, {"type", "Image"}, {"label", "测量图像"}},
                     {{"id", "position"}, {"type", "Pose"}, {"label", "定位来源"}}});
    definition.descriptor["outputs"][0]["hidden"] = true;
    definition.descriptor["outputs"].push_back({{"id", "result"}, {"type", "Result"}, {"label", "全部结果"}});
    definition.descriptor["outputs"].push_back(
        {{"id", "overlay"}, {"type", "Overlay"}, {"label", "定位轴"}, {"hidden", true}});
    definition.executeInputs = [](const InputValues &inputs, const Json &p, const Image &) -> Value {
        Image image = std::get<Image>(*inputs.at("input"));
        const auto &pose = std::get<PoseValue>(*inputs.at("position"));
        if (!image.available || !pose.available) {
            image.available = false;
            image.unavailableReason = !pose.available ? pose.reason : image.unavailableReason;
            image.matchPosition.reset();
            image.fixture.reset();
            return image;
        }
        validate_fixture_reference(p.at("reference"), true);
        const auto &reference = p.at("reference");
        require(reference.at("modelHash") == pose.modelHash, JPV_INVALID_RECIPE,
                "Fixture template changed; teach its reference again.");
        image.fixture = FixtureValue{{reference.at("centerX"), reference.at("centerY"),
                                      reference.at("angleDegrees"), reference.at("scale")},
                                     pose.position,
                                     pose.modelHash};
        image.matchPosition = pose.position;
        return image;
    };
    definition.expand = [](const Value &value, const Json &) -> PortValues {
        const auto &image = std::get<Image>(value);
        Json overlay = Json::array();
        if (image.available && image.fixture) {
            const auto &fixture = *image.fixture;
            auto center = fixture_point(fixture, {fixture.reference.centerX, fixture.reference.centerY});
            for (auto axis : {cv::Point2d(fixture.reference.centerX + 30, fixture.reference.centerY),
                              cv::Point2d(fixture.reference.centerX, fixture.reference.centerY + 30)}) {
                auto end = fixture_point(fixture, axis);
                overlay.push_back({{"type", "line"},
                                   {"points", Json::array({{center.x, center.y}, {end.x, end.y}})},
                                   {"color", DefaultResultColor},
                                   {"lineWidth", 2}});
            }
        }
        return {
            {"result",
             Json{{"resultKind", "positionFixture"}, {"image", image_metadata(image)}, {"overlay", overlay}}},
            {"overlay", overlay}};
    };
    registry.add(std::move(definition));
}
} // namespace jp

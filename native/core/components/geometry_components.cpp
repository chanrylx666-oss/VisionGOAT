#include "component.h"
#include "roi.h"
#include <algorithm>
#include <climits>
#include <cmath>
#include <numbers>
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>
#include <tuple>
#include <iomanip>
#include <sstream>

namespace jp {
static constexpr size_t MaxFeatures = 10000, MaxDetectionPixels = 4ULL * 1024 * 1024;
static void require_detection_size(const Image &image) {
    require(image.pixels.total() <= MaxDetectionPixels, JPV_RESOURCE_LIMIT,
            "Hough original-resolution input " + std::to_string(image.pixels.cols) + " x " +
                std::to_string(image.pixels.rows) +
                " exceeds 4,194,304 pixels. Select a smaller module ROI or use adaptive large-image mode.");
}
struct HoughImage {
    cv::Mat gray, valid;
    double scaleX = 1, scaleY = 1;
    Json information;
    double radius_scale() const {
        return (scaleX + scaleY) / 2;
    }
    cv::Point2d original_point(double x, double y) const {
        return {(x + .5) / scaleX - .5, (y + .5) / scaleY - .5};
    }
};
static HoughImage prepare_hough(const Image &source, const Json &p) {
    HoughImage work;
    bool downsample = source.pixels.total() > MaxDetectionPixels;
    if (downsample && p.at("largeImageMode") == "original")
        require_detection_size(source);
    auto gray = gray_pixels(source);
    if (downsample) {
        double scale = std::sqrt(double(MaxDetectionPixels) / source.pixels.total());
        cv::Size size(std::max(1, int(std::floor(gray.cols * scale))),
                      std::max(1, int(std::floor(gray.rows * scale))));
        cv::resize(gray, work.gray, size, 0, 0, cv::INTER_AREA);
        work.scaleX = double(size.width) / gray.cols;
        work.scaleY = double(size.height) / gray.rows;
        if (!source.valid.empty()) {
            cv::resize(source.valid, work.valid, size, 0, 0, cv::INTER_AREA);
            // Only fully covered cells belong to the effective ROI.
            cv::threshold(work.valid, work.valid, 254, 255, cv::THRESH_BINARY);
        }
    } else {
        work.gray = gray;
        work.valid = source.valid;
    }
    work.information = {{"downsampled", downsample},    {"inputWidth", gray.cols},
                        {"inputHeight", gray.rows},     {"workWidth", work.gray.cols},
                        {"workHeight", work.gray.rows}, {"scaleX", work.scaleX},
                        {"scaleY", work.scaleY}};
    return work;
}
Json feature_values(const Features &features) {
    Json items = Json::array();
    for (const auto &feature : features.items) {
        Json item = std::visit(
            [&features](const auto &value) -> Json {
                using T = std::decay_t<decltype(value)>;
                if constexpr (std::is_same_v<T, CircleFeature>)
                    return {{"kind", "circle"},
                            {"x", value.x},
                            {"y", value.y},
                            {"centerX", value.x},
                            {"centerY", value.y},
                            {"radius", value.radius},
                            {"diameter", 2 * value.radius},
                            {"area", std::numbers::pi * value.radius * value.radius},
                            {"perimeter", 2 * std::numbers::pi * value.radius},
                            {"circle",
                             {{"type", "circle"},
                              {"coordinateSpace", features.sourceImage ? features.sourceImage->coordinateSpace
                                                                       : features.coordinateSpace},
                              {"centerX", value.x},
                              {"centerY", value.y},
                              {"radius", value.radius},
                              {"diameter", 2 * value.radius}}},
                            {"outputAnnulus",
                             {{"coordinateSpace", features.sourceImage ? features.sourceImage->coordinateSpace
                                                                       : features.coordinateSpace},
                              {"centerX", value.x},
                              {"centerY", value.y},
                              {"innerRadius", std::max(0.0, value.radius - value.outputAnnulusWidth / 2)},
                              {"outerRadius", value.radius + value.outputAnnulusWidth / 2},
                              {"startAngle", 0.0},
                              {"endAngle", 360.0}}}};
                else if constexpr (std::is_same_v<T, LineFeature>) {
                    double length = std::hypot(value.x2 - value.x1, value.y2 - value.y1);
                    Json equation = nullptr;
                    if (length > 1e-9) {
                        double a = (value.y2 - value.y1) / length, b = (value.x1 - value.x2) / length;
                        if (a < -1e-12 || (std::abs(a) <= 1e-12 && b < 0)) {
                            a = -a;
                            b = -b;
                        }
                        equation = {{"a", a}, {"b", b}, {"c", -a * value.x1 - b * value.y1}};
                    }
                    return {{"kind", "line"},
                            {"x1", value.x1},
                            {"y1", value.y1},
                            {"x2", value.x2},
                            {"y2", value.y2},
                            {"centerX", (value.x1 + value.x2) / 2},
                            {"centerY", (value.y1 + value.y2) / 2},
                            {"length", value.length},
                            {"angleDegrees", value.angleDegrees},
                            {"equation", equation}};
                } else if constexpr (std::is_same_v<T, MatchFeature>) {
                    Json corners = Json::array();
                    for (auto p : value.corners)
                        corners.push_back({{"x", p.x}, {"y", p.y}});
                    return {{"kind", "match"},
                            {"x", value.x},
                            {"y", value.y},
                            {"width", value.width},
                            {"height", value.height},
                            {"centerX", value.centerX},
                            {"centerY", value.centerY},
                            {"angleDegrees", value.angleDegrees},
                            {"scale", value.scale},
                            {"score", value.score},
                            {"corners", corners}};
                } else {
                    Json region{{"kind", "region"},
                                {"area", value.area},
                                {"perimeter", value.perimeter},
                                {"centroidX", value.centroidX},
                                {"centroidY", value.centroidY},
                                {"centroidValid", value.centroidValid},
                                {"circularity", value.circularity},
                                {"x", value.x},
                                {"y", value.y},
                                {"width", value.width},
                                {"height", value.height}};
                    if (!value.areaKind.empty()) {
                        region["areaKind"] = value.areaKind;
                        if (value.areaKind == "pixelCount")
                            region["pixelCount"] = int64_t(std::llround(value.area));
                    }
                    if (value.aspectRatio)
                        region["aspectRatio"] = *value.aspectRatio;
                    if (value.extent)
                        region["extent"] = *value.extent;
                    if (value.touchesBoundary)
                        region["touchesBoundary"] = *value.touchesBoundary;
                    return region;
                }
            },
            feature);
        std::visit(
            [&](const auto &value) {
                using T = std::decay_t<decltype(value)>;
                if constexpr (std::is_same_v<T, CircleFeature> || std::is_same_v<T, LineFeature>) {
                    if (!value.edgePoints.empty()) {
                        item["method"] = "caliper";
                        item["rmsError"] = value.rmsError;
                        item["edgePoints"] = Json::array();
                        for (auto p : value.edgePoints)
                            item["edgePoints"].push_back({{"x", p.x}, {"y", p.y}});
                    }
                }
            },
            feature);
        if (!features.diagnostics.empty())
            item["caliper"] = features.diagnostics;
        const auto &space =
            features.sourceImage ? features.sourceImage->coordinateSpace : features.coordinateSpace;
        const auto &plane =
            features.sourceImage ? features.sourceImage->coordinatePlane : features.coordinatePlane;
        if (space != "original") {
            item["coordinateSpace"] = space;
            item["coordinatePlane"] = plane;
        }
        items.push_back(std::move(item));
    }
    return items;
}
static Image detector_image(const Features &features, const Json &p, const std::string &kind) {
    require(bool(features.sourceImage), JPV_INTERNAL_ERROR, "Detector source image was not retained.");
    Image source = *features.sourceImage;
    if (kind != "match")
        return source;
    int index = p.at("resultIndex");
    if (!source.available || size_t(index) >= features.items.size()) {
        source.available = false;
        if (source.unavailableReason.empty())
            source.unavailableReason = features.items.empty() ? "NoMatch" : "SelectionOutOfRange";
        source.matchPosition.reset();
        return source;
    }
    const auto *match = std::get_if<MatchFeature>(&features.items[index]);
    require(match && std::isfinite(match->x) && std::isfinite(match->y) && std::isfinite(match->width) &&
                std::isfinite(match->height) && std::isfinite(match->centerX) &&
                std::isfinite(match->centerY) && match->width > 0 && match->height > 0,
            JPV_INTERNAL_ERROR, "Selected matching feature has invalid image bounds.");
    source.matchPosition = MatchPosition{match->centerX, match->centerY, match->angleDegrees, match->scale};
    if (p.at("downstreamImageMode") == "source")
        return source;
    int padding = p.at("downstreamPadding");
    double left = std::floor(match->x - source.x) - padding, top = std::floor(match->y - source.y) - padding,
           right = std::ceil(match->x + match->width - source.x) + padding,
           bottom = std::ceil(match->y + match->height - source.y) + padding;
    double width = right - left, height = bottom - top;
    constexpr size_t maxPixels = 32ULL * 1024 * 1024, maxBytes = 128ULL * 1024 * 1024;
    require(width >= 1 && height >= 1 && width <= 32768 && height <= 32768 && width * height <= maxPixels &&
                width * height * (source.pixels.elemSize() + 1) <= maxBytes && left >= INT_MIN &&
                top >= INT_MIN && right <= INT_MAX && bottom <= INT_MAX && source.x + left >= INT_MIN &&
                source.x + left <= INT_MAX && source.y + top >= INT_MIN && source.y + top <= INT_MAX,
            JPV_RESOURCE_LIMIT, "Matched image padding exceeds the 32 megapixel/128 MiB image budget.");
    cv::Rect requested{int(left), int(top), int(width), int(height)};
    cv::Rect intersection = requested & cv::Rect(0, 0, source.pixels.cols, source.pixels.rows);
    require(!intersection.empty(), JPV_INTERNAL_ERROR, "Selected matching image lies outside its source.");
    // Keep the source orientation and pixel scale. Match bounds already include pose rotation/scale.
    // A fixed padded canvas keeps local ROI coordinates stable when a match touches an image edge.
    Image image{{}, source.x + requested.x, source.y + requested.y, {}, true, source.padded};
    image.matchPosition = source.matchPosition;
    if (intersection == requested) {
        image.pixels = source.pixels(requested);
        if (!source.valid.empty())
            image.valid = source.valid(requested);
    } else {
        image.padded = true;
        image.pixels = cv::Mat::zeros(requested.size(), source.pixels.type());
        image.valid = cv::Mat::zeros(requested.size(), CV_8UC1);
        cv::Rect destination(intersection.x - requested.x, intersection.y - requested.y, intersection.width,
                             intersection.height);
        source.pixels(intersection).copyTo(image.pixels(destination));
        if (source.valid.empty())
            image.valid(destination).setTo(255);
        else
            source.valid(intersection).copyTo(image.valid(destination));
    }
    return image;
}
void integrate_measurements(Component &definition) {
    auto type = definition.descriptor.at("type").get<std::string>();
    std::string kind = definition.descriptor.value("featureKind", "");
    if (kind.empty())
        kind = type == "FindCircles" || type == "CircleCaliper" ? "circle"
               : type == "FindLines" || type == "LineCaliper"   ? "line"
               : type == "TemplateMatch"                        ? "match"
               : type == "AnalyzeContours"                      ? "region"
                                                                : "";
    if (kind.empty())
        return; // A custom Features component opts in by declaring its homogeneous feature kind.
    require(kind == "circle" || kind == "line" || kind == "region" || kind == "match", JPV_INTERNAL_ERROR,
            "Unsupported measurement feature kind.");
    definition.descriptor["featureKind"] = kind;
    const auto &inputs = definition.descriptor.at("inputs");
    bool hasImage = !inputs.empty() && (inputs[0].at("type") == "Image" || inputs[0].at("type") == "Mask");
    auto &ports = definition.descriptor["outputs"];
    ports[0]["label"] = "全部几何结果";
    for (auto port : Json::array({{{"id", "measurement"}, {"type", "Measurement"}, {"label", "当前测量"}},
                                  {{"id", "count"}, {"type", "Int"}, {"label", "数量"}},
                                  {{"id", "found"}, {"type", "Bool"}, {"label", "找到目标"}},
                                  {{"id", "passed"}, {"type", "Bool"}, {"label", "数量合格"}},
                                  {{"id", "overlay"}, {"type", "Overlay"}, {"label", "结果标记"}}}))
        ports.push_back(std::move(port));
    for (auto &port : ports)
        port["hidden"] = true; // Keep the original typed ports for existing recipes and projections.
    ports.push_back({{"id", "result"}, {"type", "Result"}, {"label", "全部结果"}});
    if (hasImage)
        ports.push_back({{"id", "image"},
                         {"type", "Image"},
                         {"label", kind == "match" ? "选中目标图像" : "关联输入图像"},
                         {"hidden", true}});
    if (kind == "match")
        ports.push_back({{"id", "pose"}, {"type", "Pose"}, {"label", "选中定位位姿"}, {"hidden", true}});
    auto &parameters = definition.descriptor["parameters"];
    for (auto p :
         Json::array({parameter("resultIndex", "当前目标索引（从 0 开始）", "integer", 0, 0, 9999),
                      parameter("minimumCount", "合格数量下限", "integer", 1, 0, 10000, nullptr, true),
                      parameter("maximumCount", "合格数量上限", "integer", 10000, 0, 10000, nullptr, true),
                      parameter("showOverlay", "输出图形标记", "bool", true),
                      parameter("overlayColor", "标记颜色（#RRGGBB）", "string", DefaultResultColor),
                      parameter("overlayLineWidth", "标记线宽", "number", 2.0, .5, 10),
                      parameter("labelResults", "标记测量文字", "bool", false)}))
        parameters.push_back(std::move(p));
    if (hasImage && kind == "match") {
        auto mode = parameter("downstreamImageMode", "后续检测图像", "enum", "target", nullptr, nullptr,
                              Json::array({"source", "target"}));
        mode["editorDefault"] = "source";
        parameters.push_back(std::move(mode));
        parameters.push_back(parameter("downstreamPadding", "后续检测图像留边（px）", "integer", 0, 0, 2048));
    }
    if (kind == "circle") {
        parameters.push_back(
            parameter("outputAnnulusWidth", "结果圆环带宽（原图 px，0 仅圆周）", "number", 4.0, 0, 512));
        auto execute = std::move(definition.execute);
        definition.execute = [execute = std::move(execute)](const Value *input, const Json &p,
                                                            const Image &original) -> Value {
            auto value = execute(input, p, original);
            for (auto &item : std::get<Features>(value).items)
                std::get<CircleFeature>(item).outputAnnulusWidth = p.at("outputAnnulusWidth");
            return value;
        };
    }
    auto validate = std::move(definition.validate);
    definition.validate = [validate = std::move(validate)](const Json &p) {
        if (validate)
            validate(p);
        require(p.at("minimumCount").get<int>() <= p.at("maximumCount").get<int>(), JPV_INVALID_RECIPE,
                "Minimum count must not exceed maximum count.");
    };
    definition.expand = [kind, hasImage](const Value &value, const Json &p) -> PortValues {
        const auto &features = std::get<Features>(value);
        auto count = static_cast<int64_t>(features.items.size());
        int index = p.at("resultIndex");
        bool found = count != 0, selectedValid = index < count;
        bool passed = features.dataAvailable && count >= p.at("minimumCount").get<int>() &&
                      count <= p.at("maximumCount").get<int>();
        Json selected = nullptr;
        if (selectedValid) {
            Features one;
            one.items.push_back(features.items[index]);
            one.diagnostics = features.diagnostics;
            one.coordinateSpace =
                features.sourceImage ? features.sourceImage->coordinateSpace : features.coordinateSpace;
            one.coordinatePlane =
                features.sourceImage ? features.sourceImage->coordinatePlane : features.coordinatePlane;
            selected = feature_values(one)[0];
        }
        Json measurement = {{"featureKind", kind}, {"count", count},         {"found", found},
                            {"passed", passed},    {"selectedIndex", index}, {"selectedValid", selectedValid},
                            {"selected", selected}};
        measurement["dataAvailable"] = features.dataAvailable;
        if (!features.diagnostics.empty())
            measurement["caliper"] = features.diagnostics;
        if (!features.detection.empty())
            measurement["detection"] = features.detection;
        if (!features.position.empty())
            measurement["position"] = features.position;
        if (!features.fixture.empty())
            measurement["fixture"] = features.fixture;
        Json overlay = Json::array();
        auto color = p.at("overlayColor").get<std::string>();
        double width = p.at("overlayLineWidth");
        if (p.at("showOverlay").get<bool>()) {
            overlay = feature_overlays(features, color, width);
            for (const auto &feature : features.items)
                if (const auto *circle = std::get_if<CircleFeature>(&feature)) {
                    for (auto points :
                         {Json::array({{circle->x - 4, circle->y}, {circle->x + 4, circle->y}}),
                          Json::array({{circle->x, circle->y - 4}, {circle->x, circle->y + 4}})})
                        overlay.push_back({{"type", "line"},
                                           {"points", std::move(points)},
                                           {"color", color},
                                           {"lineWidth", width}});
                }
            for (const auto &point : features.diagnostics.value("rejectedPoints", Json::array())) {
                double x = point.at("x"), y = point.at("y");
                overlay.push_back({{"type", "line"},
                                   {"points", Json::array({{x - 3, y - 3}, {x + 3, y + 3}})},
                                   {"color", color},
                                   {"lineWidth", width}});
                overlay.push_back({{"type", "line"},
                                   {"points", Json::array({{x - 3, y + 3}, {x + 3, y - 3}})},
                                   {"color", color},
                                   {"lineWidth", width}});
            }
            if (p.at("labelResults").get<bool>()) {
                int i = 0;
                for (const auto &feature : features.items) {
                    std::visit(
                        [&](const auto &f) {
                            using T = std::decay_t<decltype(f)>;
                            double x, y;
                            std::ostringstream text;
                            text << std::fixed << std::setprecision(2) << "#" << ++i << " ";
                            if constexpr (std::is_same_v<T, CircleFeature>) {
                                x = f.x - f.radius;
                                y = f.y - f.radius - 36;
                                text << "R" << f.radius << "\nD" << 2 * f.radius;
                            } else if constexpr (std::is_same_v<T, LineFeature>) {
                                x = f.x1;
                                y = f.y1;
                                y -= 36;
                                text << "L" << f.length << "\nA" << f.angleDegrees;
                            } else if constexpr (std::is_same_v<T, MatchFeature>) {
                                x = f.x;
                                y = f.y;
                                y -= 36;
                                text << "S" << f.score << "\nA" << f.angleDegrees;
                            } else {
                                x = f.x;
                                y = f.y;
                                y -= 36;
                                text << "A" << f.area << "\nP" << f.perimeter;
                            }
                            overlay.push_back({{"type", "text"},
                                               {"x", x},
                                               {"y", y},
                                               {"text", text.str()},
                                               {"color", color},
                                               {"lineWidth", width}});
                        },
                        feature);
                }
            }
        }
        Json packet = {{"featureKind", kind},
                       {"features", feature_values(features)},
                       {"measurement", measurement},
                       {"count", count},
                       {"found", found},
                       {"passed", passed},
                       {"overlay", overlay}};
        packet["dataAvailable"] = features.dataAvailable;
        PortValues outputs{{"measurement", std::move(measurement)},
                           {"count", count},
                           {"found", found},
                           {"passed", passed},
                           {"overlay", std::move(overlay)}};
        if (hasImage) {
            auto image = detector_image(features, p, kind);
            packet["image"] = image_metadata(image);
            outputs.emplace("image", std::move(image));
        }
        if (kind == "match") {
            PoseValue pose;
            pose.modelHash = sha256(p.at("model").dump());
            if (selectedValid) {
                const auto &match = std::get<MatchFeature>(features.items[index]);
                pose.available = true;
                pose.position = {match.centerX, match.centerY, match.angleDegrees, match.scale};
            } else
                pose.reason = features.items.empty() ? "NoMatch" : "SelectionOutOfRange";
            packet["pose"] = pose_metadata(pose);
            outputs.emplace("pose", std::move(pose));
        }
        outputs.emplace("result", std::move(packet));
        return outputs;
    };
    auto &fields = definition.descriptor["fields"]["result"];
    for (auto name : {"count", "measurement.count", "measurement.selectedIndex"})
        fields[name] = {{"type", "Int"}, {"nullable", false}, {"label", name}};
    for (auto name : {"found", "passed", "measurement.selectedValid"})
        fields[name] = {{"type", "Bool"}, {"nullable", false}, {"label", name}};
    std::vector<std::string> selectedFields =
        kind == "circle" ? std::vector<std::string>{"centerX", "centerY", "radius", "diameter", "rmsError"}
        : kind == "line"
            ? std::vector<std::string>{"x1", "y1", "x2", "y2", "length", "angleDegrees", "rmsError"}
        : kind == "match"
            ? std::vector<std::string>{"centerX",      "centerY", "width", "height",
                                       "angleDegrees", "scale",   "score"}
            : std::vector<std::string>{"centroidX", "centroidY", "area", "perimeter", "circularity"};
    for (const auto &name : selectedFields) {
        auto path = "measurement.selected." + name;
        fields[path] = {{"type", "Double"}, {"nullable", true}, {"label", name}};
        if (kind == "region" && (name == "centroidX" || name == "centroidY"))
            fields[path]["validWhen"] = "measurement.selected.centroidValid";
    }
}
Json feature_overlays(const Features &features, const std::string &color, double lineWidth) {
    Json items = Json::array();
    for (const auto &feature : features.items) {
        Json item = std::visit(
            [](const auto &value) -> Json {
                using T = std::decay_t<decltype(value)>;
                if constexpr (std::is_same_v<T, CircleFeature>)
                    return {{"type", "circle"}, {"x", value.x}, {"y", value.y}, {"radius", value.radius}};
                else if constexpr (std::is_same_v<T, LineFeature>)
                    return {{"type", "line"},
                            {"points", Json::array({{value.x1, value.y1}, {value.x2, value.y2}})}};
                else if constexpr (std::is_same_v<T, MatchFeature>) {
                    Json points = Json::array();
                    for (auto p : value.corners)
                        points.push_back({p.x, p.y});
                    return {{"type", "polyline"}, {"closed", true}, {"points", points}};
                } else
                    return {{"type", "rectangle"},
                            {"x", value.x},
                            {"y", value.y},
                            {"width", value.width},
                            {"height", value.height}};
            },
            feature);
        item["color"] = color;
        item["lineWidth"] = lineWidth;
        items.push_back(std::move(item));
        // Both the detector packet and downstream markers use the final fitted geometry.
        if (const auto *circle = std::get_if<CircleFeature>(&feature);
            circle && circle->outputAnnulusWidth > 0) {
            double halfBand = circle->outputAnnulusWidth / 2;
            items.push_back({{"type", "annulus"},
                             {"x", circle->x},
                             {"y", circle->y},
                             {"innerRadius", std::max(0.0, circle->radius - halfBand)},
                             {"outerRadius", circle->radius + halfBand},
                             {"startAngle", 0.0},
                             {"endAngle", 360.0},
                             {"color", color},
                             {"lineWidth", std::max(.5, lineWidth / 2)}});
        }
        std::visit(
            [&](const auto &value) {
                using T = std::decay_t<decltype(value)>;
                if constexpr (std::is_same_v<T, CircleFeature> || std::is_same_v<T, LineFeature>) {
                    for (auto p : value.edgePoints)
                        items.push_back({{"type", "circle"},
                                         {"x", p.x},
                                         {"y", p.y},
                                         {"radius", 1.8},
                                         {"color", color},
                                         {"lineWidth", 1}});
                } else if constexpr (std::is_same_v<T, MatchFeature>)
                    items.push_back({{"type", "text"},
                                     {"x", value.x},
                                     {"y", value.y - 16},
                                     {"text", std::to_string(value.score).substr(0, 5)},
                                     {"color", color},
                                     {"lineWidth", lineWidth}});
            },
            feature);
    }
    return items;
}
void register_geometry_components(ComponentRegistry &r) {
    auto position = parameter("positionReference", "定位基准", "enum", "image", nullptr, nullptr,
                              Json::array({"auto", "matchCenter", "image", "fixture"}));
    position["editorDefault"] = "auto";
    auto circleParameters = Json::array(
        {std::move(position),
         parameter("matchOffsetX", "相对模板中心 X 偏移（px）", "number", 0.0, -32768, 32768),
         parameter("matchOffsetY", "相对模板中心 Y 偏移（px）", "number", 0.0, -32768, 32768),
         parameter("matchSearchRadius", "定位搜索半径（未画圆 ROI 时）", "number", 80.0, 1, 16384),
         parameter("method", "查找方式", "enum", "automatic", nullptr, nullptr,
                   Json::array({"automatic", "caliper"})),
         parameter("largeImageMode", "大图处理", "enum", "adaptive", nullptr, nullptr,
                   Json::array({"adaptive", "original"})),
         parameter("dp", "分辨率比例", "number", 1.0, 1, 8),
         parameter("minimumDistance", "圆心最小距离（px）", "number", 40.0, 1, 32768),
         parameter("edgeThreshold", "自动找圆边缘阈值", "number", 100.0, 1, 1000),
         parameter("voteThreshold", "圆检测阈值", "number", 30.0, 1, 1000),
         odd_parameter("preBlurKernel", "检测前平滑核（奇数 px）", 5, 1, 31),
         parameter("preBlurSigma", "检测前平滑 Sigma", "number", 1.0, 0, 10),
         parameter("minimumRadius", "最小半径（px）", "integer", 5, 1, 4096),
         parameter("maximumRadius", "最大半径（px）", "integer", 100, 1, 4096)});
    for (auto p : circle_caliper_parameters()) {
        if (p.at("name") == "edgeThreshold") {
            p["name"] = "caliperThreshold";
            p["label"] = "卡尺边缘强度阈值";
        }
        circleParameters.push_back(std::move(p));
    }
    r.add(component(
        "FindCircles", "圆查找", "几何检测", "Image", "Features", "features", circleParameters,
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            if (p.at("method") == "caliper") {
                auto caliper = p;
                caliper["edgeThreshold"] = p.at("caliperThreshold");
                return find_circle_caliper(source, caliper);
            }
            auto work = prepare_hough(source, p);
            cv::Mat smooth;
            int kernel = p.at("preBlurKernel");
            cv::GaussianBlur(work.gray, smooth, {kernel, kernel}, p.at("preBlurSigma").get<double>());
            std::vector<cv::Vec3f> circles;
            double scale = work.radius_scale();
            int minimum = std::max(1, int(std::floor(p.at("minimumRadius").get<int>() * scale))),
                maximum = std::max(minimum, int(std::ceil(p.at("maximumRadius").get<int>() * scale)));
            cv::HoughCircles(smooth, circles, cv::HOUGH_GRADIENT, p.at("dp").get<double>(),
                             std::max(1.0, p.at("minimumDistance").get<double>() * scale),
                             p.at("edgeThreshold").get<double>(),
                             std::max(1.0, p.at("voteThreshold").get<double>() * scale), minimum, maximum);
            require(circles.size() <= MaxFeatures, JPV_RESOURCE_LIMIT, "Circle result exceeds limit.");
            std::sort(circles.begin(), circles.end(), [](const auto &a, const auto &b) {
                return std::tie(a[0], a[1], a[2]) < std::tie(b[0], b[1], b[2]);
            });
            Features out;
            out.detection = work.information;
            for (const auto &c : circles) {
                auto center = work.original_point(c[0], c[1]);
                double radius = c[2] / scale;
                bool inside = true;
                if (!source.valid.empty())
                    for (int i = 0; i < 64; ++i) {
                        double angle = i * 2 * std::numbers::pi / 64;
                        inside &= valid_point(source, center.x + radius * std::cos(angle),
                                              center.y + radius * std::sin(angle));
                    }
                if (!inside)
                    continue;
                out.items.emplace_back(CircleFeature{center.x + source.x, center.y + source.y, radius});
            }
            return out;
        },
        [](const Json &p) {
            if (p.at("method") == "caliper") {
                validate_circle_caliper(p);
                return;
            }
            require(p.at("minimumRadius").get<int>() <= p.at("maximumRadius").get<int>(), JPV_INVALID_RECIPE,
                    "Minimum radius exceeds maximum radius.");
            require(p.at("preBlurKernel").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
                    "Circle pre-blur kernel size must be odd.");
        }));
    auto lineMethod = parameter("method", "查找方式", "enum", "automatic", nullptr, nullptr,
                                Json::array({"automatic", "caliper"}));
    lineMethod["editorDefault"] = "caliper";
    auto lineParameters =
        Json::array({std::move(lineMethod),
                     parameter("largeImageMode", "大图处理", "enum", "adaptive", nullptr, nullptr,
                               Json::array({"adaptive", "original"})),
                     parameter("edgeLow", "边缘低阈值", "number", 50.0, 0, 1000),
                     parameter("edgeHigh", "边缘高阈值", "number", 150.0, 1, 1000),
                     odd_parameter("edgeAperture", "Canny 梯度核（奇数 px）", 3, 3, 7),
                     parameter("edgeL2Gradient", "Canny 使用 L2 梯度", "bool", false),
                     parameter("voteThreshold", "直线检测阈值", "integer", 50, 1, 10000),
                     parameter("minimumLength", "最小长度（px）", "number", 30.0, 1, 32768),
                     parameter("maximumGap", "最大断点间距（px）", "number", 10.0, 0, 32768)});
    for (auto p : line_caliper_parameters()) {
        if (p.at("name") == "caliperPlacement")
            p["editorDefault"] = "roiCenter";
        lineParameters.push_back(std::move(p));
    }
    r.add(component(
        "FindLines", "直线查找", "几何检测", "Image", "Features", "features", lineParameters,
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            if (p.at("method") == "caliper")
                return find_line_caliper(source, p);
            auto work = prepare_hough(source, p);
            cv::Mat edges;
            cv::Canny(work.gray, edges, p.at("edgeLow").get<double>(), p.at("edgeHigh").get<double>(),
                      p.at("edgeAperture").get<int>(), p.at("edgeL2Gradient").get<bool>());
            if (!work.valid.empty()) {
                cv::Mat valid;
                if (source.padded)
                    cv::erode(work.valid, valid, cv::Mat::ones(3, 3, CV_8UC1), {-1, -1}, 1,
                              cv::BORDER_REPLICATE);
                else
                    valid = work.valid;
                // Canny needs neighbouring pixels. A padded boundary is outside the acquired image.
                edges.setTo(0, valid == 0);
            }
            std::vector<cv::Vec4i> lines;
            double scale = work.radius_scale();
            cv::HoughLinesP(edges, lines, 1, std::numbers::pi / 180,
                            std::max(1, int(std::lround(p.at("voteThreshold").get<int>() * scale))),
                            p.at("minimumLength").get<double>() * scale,
                            p.at("maximumGap").get<double>() * scale);
            require(lines.size() <= MaxFeatures, JPV_RESOURCE_LIMIT, "Line result exceeds limit.");
            for (auto &line : lines)
                if (std::tie(line[0], line[1]) > std::tie(line[2], line[3])) {
                    std::swap(line[0], line[2]);
                    std::swap(line[1], line[3]);
                }
            std::sort(lines.begin(), lines.end(), [](const auto &a, const auto &b) {
                return std::tie(a[0], a[1], a[2], a[3]) < std::tie(b[0], b[1], b[2], b[3]);
            });
            Features out;
            out.detection = work.information;
            for (const auto &l : lines) {
                auto a = work.original_point(l[0], l[1]), b = work.original_point(l[2], l[3]);
                bool inside = true;
                if (!source.valid.empty())
                    for (int i = 0; i <= 64; ++i)
                        inside &=
                            valid_point(source, a.x + (b.x - a.x) * i / 64.0, a.y + (b.y - a.y) * i / 64.0);
                if (!inside)
                    continue;
                double dx = b.x - a.x, dy = b.y - a.y;
                if (std::hypot(dx, dy) + 1e-6 < p.at("minimumLength").get<double>())
                    continue;
                out.items.emplace_back(LineFeature{a.x + source.x, a.y + source.y, b.x + source.x,
                                                   b.y + source.y, std::hypot(dx, dy),
                                                   std::atan2(dy, dx) * 180 / std::numbers::pi});
            }
            return out;
        },
        [](const Json &p) {
            if (p.at("method") == "caliper") {
                validate_line_caliper(p);
                return;
            }
            require(p.at("edgeLow").get<double>() < p.at("edgeHigh").get<double>(), JPV_INVALID_RECIPE,
                    "Edge low threshold must be below high threshold.");
            require(p.at("edgeAperture").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
                    "Canny aperture size must be odd (3, 5 or 7).");
        }));
    r.add(component(
        "AnalyzeContours", "区域形状分析", "几何检测", "Contours", "Features", "features", Json::array(),
        [](const Value *input, const Json &, const Image &) -> Value {
            const auto &source = std::get<Contours>(*input);
            Features out;
            out.dataAvailable = source.dataAvailable;
            out.sourceImage = source.sourceImage;
            for (const auto &contour : source.items) {
                auto bounds = cv::boundingRect(contour);
                double area = cv::contourArea(contour), perimeter = cv::arcLength(contour, true);
                auto moments = cv::moments(contour);
                bool valid = std::abs(moments.m00) > 1e-12;
                double cx = valid ? moments.m10 / moments.m00 : bounds.x + (bounds.width - 1) / 2.0;
                double cy = valid ? moments.m01 / moments.m00 : bounds.y + (bounds.height - 1) / 2.0;
                double circularity =
                    perimeter > 0 ? 4 * std::numbers::pi * area / (perimeter * perimeter) : 0;
                out.items.emplace_back(RegionFeature{area, perimeter, cx + source.x, cy + source.y,
                                                     circularity, bounds.x + source.x, bounds.y + source.y,
                                                     bounds.width, bounds.height, valid});
            }
            std::sort(out.items.begin(), out.items.end(), [](const auto &a, const auto &b) {
                const auto &ra = std::get<RegionFeature>(a), &rb = std::get<RegionFeature>(b);
                return std::tie(ra.centroidX, ra.centroidY) < std::tie(rb.centroidX, rb.centroidY);
            });
            return out;
        }));
    r.add(component("CountFeatures", "几何目标计数", "检测", "Features", "Int", "value", Json::array(),
                    [](const Value *input, const Json &, const Image &) -> Value {
                        return static_cast<int64_t>(std::get<Features>(*input).items.size());
                    }));
    r.add(component("BuildFeatureOverlay", "几何结果标记", "显示", "Features", "Overlay", "overlay",
                    Json::array({parameter("color", "颜色（#RRGGBB）", "string", DefaultResultColor),
                                 parameter("lineWidth", "线宽", "number", 2.0, 0.5, 10)}),
                    [](const Value *input, const Json &p, const Image &) -> Value {
                        return feature_overlays(std::get<Features>(*input), p.at("color").get<std::string>(),
                                                p.at("lineWidth").get<double>());
                    }));
}
} // namespace jp

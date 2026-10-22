#include "deep_learning_components.h"
#include "deep_learning.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <numbers>
#include <tuple>
#include <opencv2/imgproc.hpp>

namespace jp {
namespace {
Json setting(const char *name, const char *label, const char *type, Json value, const char *group,
             const char *description, Json minimum = nullptr, Json maximum = nullptr) {
    auto result = parameter(name, label, type, std::move(value), std::move(minimum), std::move(maximum));
    result["group"] = group;
    result["description"] = description;
    return result;
}
Json empty_result(const Image &source, const Json &p, bool available, const std::string &reason) {
    return {{"resultKind", "deepLearningDefect"},
            {"featureKind", "region"},
            {"coordinateSpace", source.coordinateSpace},
            {"coordinatePlane", source.coordinatePlane},
            {"dataAvailable", available},
            {"valid", available},
            {"reason", reason},
            {"modelKey", p.at("modelKey")},
            {"modelId", ""},
            {"modelVersion", ""},
            {"scoreKind", ""},
            {"count", 0},
            {"found", false},
            {"passed", available},
            {"maximumScore", nullptr},
            {"totalArea", available ? Json(0.0) : Json()},
            {"tileCount", 0},
            {"features", Json::array()},
            {"overlay", Json::array()},
            {"measurement",
             {{"featureKind", "region"},
              {"dataAvailable", available},
              {"count", 0},
              {"found", false},
              {"passed", available},
              {"selectedIndex", 0},
              {"selectedValid", false},
              {"selected", nullptr}}}};
}
struct Region {
    uint64_t area = 0, perimeter = 0, sumX = 0, sumY = 0;
    int left = std::numeric_limits<int>::max(), top = std::numeric_limits<int>::max(), right = -1,
        bottom = -1;
    float score = 0;
};
Value detect_defects(const Value *input, const Json &p, const Image &) {
    const auto &source = std::get<Image>(*input);
    if (p.contains("_unavailable"))
        return empty_result(source, p, false, "UnavailableParameter");
    if (!source.available || source.pixels.empty())
        return empty_result(source, p, false,
                            source.unavailableReason.empty() ? "UnavailableInputImage"
                                                             : source.unavailableReason);
    auto inference = infer_defect_scores(source, p);
    auto result = empty_result(source, p, true, "NoDefects");
    result["modelId"] = inference.manifest.at("modelId");
    result["modelVersion"] = inference.manifest.at("version");
    result["scoreKind"] = inference.manifest.at("output").at("scoreKind");
    result["tileCount"] = inference.tileCount;
    double maximumScore = 0;
    cv::minMaxLoc(inference.scores, nullptr, &maximumScore, nullptr, nullptr, source.valid);
    result["maximumScore"] = maximumScore;
    cv::Mat foreground;
    cv::compare(inference.scores, p.at("threshold").get<double>(), foreground, cv::CMP_GE);
    if (!source.valid.empty())
        foreground.setTo(0, source.valid == 0);
    if (!cv::countNonZero(foreground))
        return result;
    cv::Mat labels;
    int count = cv::connectedComponents(foreground, labels, 8, CV_32S, cv::CCL_SAUF);
    require(count - 1 <= 100000, JPV_RESOURCE_LIMIT,
            "DeepLearningResultLimit: more than 100000 candidate regions; adjust ROI or threshold.");
    std::vector<Region> regions(count);
    for (int y = 0; y < labels.rows; ++y) {
        const auto *row = labels.ptr<int>(y);
        const auto *above = y ? labels.ptr<int>(y - 1) : nullptr;
        const auto *below = y + 1 < labels.rows ? labels.ptr<int>(y + 1) : nullptr;
        const auto *scores = inference.scores.ptr<float>(y);
        for (int x = 0; x < labels.cols; ++x) {
            int label = row[x];
            if (!label)
                continue;
            auto &region = regions[label];
            ++region.area;
            region.sumX += x;
            region.sumY += y;
            region.left = std::min(region.left, x);
            region.top = std::min(region.top, y);
            region.right = std::max(region.right, x);
            region.bottom = std::max(region.bottom, y);
            region.perimeter += x == 0 || row[x - 1] != label;
            region.perimeter += x + 1 == labels.cols || row[x + 1] != label;
            region.perimeter += !above || above[x] != label;
            region.perimeter += !below || below[x] != label;
            region.score = std::max(region.score, scores[x]);
        }
    }
    std::vector<const Region *> accepted;
    const uint64_t minimumArea = p.at("minimumArea");
    for (int i = 1; i < count; ++i)
        if (regions[i].area >= minimumArea) {
            accepted.push_back(&regions[i]);
            require(accepted.size() <= p.at("maximumDefects").get<size_t>(), JPV_RESOURCE_LIMIT,
                    "DeepLearningResultLimit: too many accepted defects; no results were truncated.");
        }
    std::stable_sort(accepted.begin(), accepted.end(),
                     [](const Region *a, const Region *b) { return a->area > b->area; });
    uint64_t totalArea = 0;
    for (const auto *region : accepted) {
        const int x = source.x + region->left, y = source.y + region->top;
        const int width = region->right - region->left + 1, height = region->bottom - region->top + 1;
        result["features"].push_back(
            {{"kind", "region"},
             {"label", "defect"},
             {"score", region->score},
             {"area", double(region->area)},
             {"areaKind", "pixelCount"},
             {"perimeter", double(region->perimeter)},
             {"centroidX", source.x + double(region->sumX) / region->area},
             {"centroidY", source.y + double(region->sumY) / region->area},
             {"centroidValid", true},
             {"circularity", 4 * std::numbers::pi * region->area /
                                 (double(region->perimeter) * region->perimeter)},
             {"x", x},
             {"y", y},
             {"width", width},
             {"height", height},
             {"coordinateSpace", source.coordinateSpace},
             {"coordinatePlane", source.coordinatePlane}});
        if (p.at("showOverlay").get<bool>())
            result["overlay"].push_back({{"type", "rectangle"},
                                           {"x", x},
                                           {"y", y},
                                           {"width", width},
                                           {"height", height},
                                           {"color", DefaultResultColor},
                                           {"lineWidth", p.at("lineWidth")}});
        totalArea += region->area;
    }
    const int acceptedCount = int(accepted.size());
    result["count"] = acceptedCount;
    result["found"] = acceptedCount > 0;
    result["passed"] = acceptedCount == 0;
    result["reason"] = acceptedCount ? "DefectsFound" : "NoDefects";
    result["totalArea"] = double(totalArea);
    auto &measurement = result["measurement"];
    measurement["count"] = acceptedCount;
    measurement["found"] = acceptedCount > 0;
    measurement["passed"] = acceptedCount == 0;
    measurement["selectedValid"] = acceptedCount > 0;
    if (acceptedCount)
        measurement["selected"] = result.at("features")[0];
    return result;
}
} // namespace

void register_deep_learning_components(ComponentRegistry &registry) {
    Json parameters = Json::array({
        setting("modelRoot", "模型库目录", "string", "", "模型",
                "留空使用当前用户的 JP-Vision 模型库。模型单独导入，不嵌入配方资源。"),
        setting("modelKey", "模型版本键（SHA-256）", "string", "", "模型",
                "由模型导入生成，固定 manifest.json 和 model.onnx 内容，禁止静默替换。"),
        setting("overlap", "图块重叠宽度（px）", "integer", 16, "推理",
                "保留原始像素分块；须小于模型的宽和高，重叠区域平均融合。", 0, 512),
        setting("threshold", "缺陷分数阈值", "number", .5, "缺陷筛选",
                "大于等于该值的像素为候选缺陷；异常分数不等于概率，按验证集设置阈值。", 0, 1000000),
        setting("minimumArea", "最小缺陷像素面积", "integer", 10, "缺陷筛选",
                "按原图实际前景像素数筛选 8 邻域连通区域。", 1, 32 * 1024 * 1024),
        setting("maximumDefects", "最多输出缺陷数", "integer", 1000, "资源限制",
                "超过上限明确报错；不会截断缺陷后误判合格。", 1, 10000),
        setting("showOverlay", "显示缺陷标记", "bool", true, "结果显示",
                "绿色包围框单独传递，不修改输出图像像素。"),
        setting("lineWidth", "标记线宽", "number", 2.0, "结果显示", "默认绿色 #00FF00。", .5, 10),
    });
    auto definition = component("DeepLearningDefect", "深度学习缺陷检测", "深度学习", "Image", "Result",
                                "result", std::move(parameters), detect_defects);
    definition.descriptor["handlesUnavailableParameters"] = true;
    definition.descriptor["featureKind"] = "region";
    for (const auto &[id, type, label] :
         {std::tuple{"features", "Features", "缺陷区域"}, std::tuple{"measurement", "Measurement", "区域测量"},
          std::tuple{"count", "Int", "缺陷数量"}, std::tuple{"found", "Bool", "发现缺陷"},
          std::tuple{"passed", "Bool", "检测合格"}, std::tuple{"overlay", "Overlay", "缺陷标记"},
          std::tuple{"image", "Image", "图像与结果"}})
        definition.descriptor["outputs"].push_back({{"id", id}, {"type", type}, {"label", label}});
    definition.expandInputs = [](Value &value, const Json &, const InputValues &inputs) -> PortValues {
        auto &packet = std::get<Json>(value);
        auto image = std::get<Image>(*inputs.at("input"));
        Features features;
        features.dataAvailable = packet.at("dataAvailable");
        features.coordinateSpace = image.coordinateSpace;
        features.coordinatePlane = image.coordinatePlane;
        for (const auto &item : packet.at("features")) {
            RegionFeature region{item.at("area"), item.at("perimeter"), item.at("centroidX"),
                                 item.at("centroidY"), item.at("circularity"), item.at("x"), item.at("y"),
                                 item.at("width"), item.at("height"), true};
            region.areaKind = "pixelCount";
            features.items.emplace_back(std::move(region));
        }
        if (!features.dataAvailable) {
            image.available = false;
            image.unavailableReason = packet.at("reason").get<std::string>();
        }
        features.sourceImage = std::make_shared<Image>(image);
        packet["image"] = image_metadata(image);
        return {{"features", std::move(features)},
                {"measurement", packet.at("measurement")},
                {"count", packet.at("count").get<int64_t>()},
                {"found", packet.at("found").get<bool>()},
                {"passed", packet.at("passed").get<bool>()},
                {"overlay", packet.at("overlay")},
                {"image", std::move(image)}};
    };
    auto &fields = definition.descriptor["fields"]["result"];
    auto field = [&](const char *name, const char *type, const char *label, bool nullable = false) {
        fields[name] = {{"type", type}, {"label", label}, {"nullable", nullable}};
    };
    field("count", "Int", "缺陷数量");
    field("tileCount", "Int", "推理图块数");
    field("maximumScore", "Double", "最大像素分数", true);
    field("totalArea", "Double", "缺陷总像素面积", true);
    field("found", "Bool", "发现缺陷");
    field("passed", "Bool", "无合格缺陷");
    field("dataAvailable", "Bool", "数据有效");
    field("modelId", "String", "模型名称");
    field("modelVersion", "String", "模型版本");
    field("measurement.selected.centroidX", "Double", "最大缺陷质心 X", true);
    field("measurement.selected.centroidY", "Double", "最大缺陷质心 Y", true);
    field("measurement.selected.area", "Double", "最大缺陷面积", true);
    field("measurement.selected.score", "Double", "最大缺陷分数", true);
    registry.add(std::move(definition));
}
} // namespace jp

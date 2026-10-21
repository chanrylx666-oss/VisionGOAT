#include "blob_components.h"
#include "image_components.h"
#include "roi.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <numbers>
#include <opencv2/imgproc.hpp>
#include <tuple>

namespace jp {
namespace {
constexpr int MaximumBlobs = 10000, MaximumCandidates = 100000, MaximumDimension = 32768;
constexpr uint64_t MaximumPixels = 32ULL * 1024 * 1024;

Json setting(const char *name, const char *label, const char *type, Json value, const char *group,
             const char *description, Json minimum = nullptr, Json maximum = nullptr, Json choices = nullptr,
             const char *unit = "") {
    auto result = parameter(name, label, type, std::move(value), std::move(minimum), std::move(maximum),
                            std::move(choices));
    result["group"] = group;
    result["description"] = description;
    if (*unit)
        result["unit"] = unit;
    return result;
}

void validate_blob_parameters(const Json &p) {
    require(p.at("connectivity") == 4 || p.at("connectivity") == 8, JPV_INVALID_RECIPE,
            "Blob connectivity must be 4 or 8.");
    for (const auto &[minimum, maximum] :
         {std::pair{"minimumArea", "maximumArea"}, std::pair{"minimumWidth", "maximumWidth"},
          std::pair{"minimumHeight", "maximumHeight"}, std::pair{"minimumCircularity", "maximumCircularity"},
          std::pair{"minimumAspectRatio", "maximumAspectRatio"}, std::pair{"minimumExtent", "maximumExtent"},
          std::pair{"minimumCount", "maximumCount"}})
        require(p.at(minimum).get<double>() <= p.at(maximum).get<double>(), JPV_INVALID_RECIPE,
                std::string("Blob filter minimum exceeds maximum: ") + minimum);
}

Json blob_packet(const Image &source, const Json &p, bool available, const std::string &reason) {
    bool passed = available && p.at("minimumCount").get<int>() == 0;
    Json measurement{{"featureKind", "region"},
                     {"count", 0},
                     {"found", false},
                     {"passed", passed},
                     {"dataAvailable", available},
                     {"selectedIndex", p.at("resultIndex")},
                     {"selectedValid", false},
                     {"selected", nullptr}};
    return {{"resultKind", "blobAnalysis"},
            {"featureKind", "region"},
            {"coordinateSpace", source.coordinateSpace},
            {"coordinatePlane", source.coordinatePlane},
            {"dataAvailable", available},
            {"valid", available},
            {"reason", reason},
            {"areaKind", "pixelCount"},
            {"perimeterMethod", "pixelEdges"},
            {"connectivity", p.at("connectivity")},
            {"candidateCount", 0},
            {"rejectedCount", 0},
            {"totalArea", available ? Json(0.0) : Json()},
            {"count", 0},
            {"found", false},
            {"passed", passed},
            {"features", Json::array()},
            {"measurement", std::move(measurement)},
            {"overlay", Json::array()}};
}

struct Blob {
    uint64_t area = 0, sumX = 0, sumY = 0, perimeter = 0;
    int label = 0, left = std::numeric_limits<int>::max(), top = std::numeric_limits<int>::max(), right = -1,
        bottom = -1;
    bool touchesBoundary = false;
    int width() const {
        return right - left + 1;
    }
    int height() const {
        return bottom - top + 1;
    }
    double centerX() const {
        return double(sumX) / area;
    }
    double centerY() const {
        return double(sumY) / area;
    }
    double aspectRatio() const {
        return double(width()) / height();
    }
    double extent() const {
        return double(area) / (double(width()) * height());
    }
    double circularity() const {
        return perimeter ? 4 * std::numbers::pi * area / (double(perimeter) * perimeter) : 0;
    }
};

bool within(double value, const Json &p, const char *minimum, const char *maximum) {
    return value >= p.at(minimum).get<double>() && value <= p.at(maximum).get<double>();
}

bool accepts_blob(const Blob &blob, const Json &p) {
    return within(double(blob.area), p, "minimumArea", "maximumArea") &&
           within(blob.width(), p, "minimumWidth", "maximumWidth") &&
           within(blob.height(), p, "minimumHeight", "maximumHeight") &&
           within(blob.circularity(), p, "minimumCircularity", "maximumCircularity") &&
           within(blob.aspectRatio(), p, "minimumAspectRatio", "maximumAspectRatio") &&
           within(blob.extent(), p, "minimumExtent", "maximumExtent") &&
           !(p.at("excludeBorder").get<bool>() && blob.touchesBoundary);
}

Json blob_feature(const Blob &blob, const Image &source) {
    return {{"kind", "region"},
            {"label", blob.label},
            {"area", double(blob.area)},
            {"pixelCount", blob.area},
            {"areaKind", "pixelCount"},
            {"perimeter", double(blob.perimeter)},
            {"perimeterMethod", "pixelEdges"},
            {"centroidX", source.x + blob.centerX()},
            {"centroidY", source.y + blob.centerY()},
            {"centroidValid", true},
            {"circularity", blob.circularity()},
            {"x", source.x + blob.left},
            {"y", source.y + blob.top},
            {"width", blob.width()},
            {"height", blob.height()},
            {"aspectRatio", blob.aspectRatio()},
            {"extent", blob.extent()},
            {"touchesBoundary", blob.touchesBoundary},
            {"coordinateSpace", source.coordinateSpace},
            {"coordinatePlane", source.coordinatePlane}};
}

void append_blob_overlay(Json &overlay, const Json &feature, int index, const Json &p) {
    if (!p.at("showOverlay").get<bool>())
        return;
    const auto color = p.at("overlayColor").get<std::string>();
    double lineWidth = p.at("overlayLineWidth");
    if (p.at("showBoundingBox").get<bool>())
        overlay.push_back({{"type", "rectangle"},
                           {"x", feature.at("x")},
                           {"y", feature.at("y")},
                           {"width", feature.at("width")},
                           {"height", feature.at("height")},
                           {"color", color},
                           {"lineWidth", lineWidth}});
    if (p.at("showCentroid").get<bool>()) {
        double x = feature.at("centroidX"), y = feature.at("centroidY");
        for (const auto &points :
             {Json::array({{x - 3, y}, {x + 3, y}}), Json::array({{x, y - 3}, {x, y + 3}})})
            overlay.push_back(
                {{"type", "line"}, {"points", points}, {"color", color}, {"lineWidth", lineWidth}});
    }
    if (p.at("labelResults").get<bool>())
        overlay.push_back({{"type", "text"},
                           {"x", feature.at("x")},
                           {"y", feature.at("y").get<double>() - 15},
                           {"text", "#" + std::to_string(index + 1) +
                                        " A=" + std::to_string(feature.at("pixelCount").get<uint64_t>())},
                           {"color", color},
                           {"lineWidth", lineWidth}});
}

Value analyze_blobs(const Value *input, const Json &p, const Image &) {
    const auto &source = std::get<Image>(*input);
    if (p.contains("_unavailable"))
        return blob_packet(source, p, false, "UnavailableParameter");
    if (!source.available || source.pixels.empty())
        return blob_packet(source, p, false,
                           source.unavailableReason.empty() ? "UnavailableInputImage"
                                                            : source.unavailableReason);
    require(source.pixels.total() <= MaximumPixels, JPV_RESOURCE_LIMIT,
            "Blob input exceeds the 32 Mi pixel budget.");
    auto result = blob_packet(source, p, true, "NoBlobs");
    auto segmented = binary_image(source, p);
    require(segmented.pixels.type() == CV_8UC1, JPV_INVALID_ARGUMENT,
            "Blob segmentation requires a Gray8 mask.");
    cv::Mat foreground;
    cv::compare(segmented.pixels, 0, foreground, cv::CMP_GT);
    if (!source.valid.empty())
        foreground.setTo(0, source.valid == 0);
    if (cv::countNonZero(foreground) == 0)
        return result;

    // Labels alone have a fixed four-byte-per-pixel cost. Do not request WithStats:
    // a noisy image could otherwise allocate huge tables for millions of tiny labels
    // before there is an opportunity to enforce the candidate limit.
    cv::Mat labels;
    int labelCount =
        cv::connectedComponents(foreground, labels, p.at("connectivity").get<int>(), CV_32S, cv::CCL_SAUF);
    require(labelCount - 1 <= MaximumCandidates, JPV_RESOURCE_LIMIT,
            "Blob candidate count exceeds 100000; reduce noise or restrict the ROI.");
    std::vector<Blob> blobs(labelCount);
    for (int i = 1; i < labelCount; ++i)
        blobs[i].label = i;
    for (int y = 0; y < labels.rows; ++y) {
        const auto *row = labels.ptr<int>(y);
        const auto *above = y > 0 ? labels.ptr<int>(y - 1) : nullptr;
        const auto *below = y + 1 < labels.rows ? labels.ptr<int>(y + 1) : nullptr;
        for (int x = 0; x < labels.cols; ++x) {
            int label = row[x];
            if (!label)
                continue;
            auto &blob = blobs[label];
            ++blob.area;
            blob.sumX += x;
            blob.sumY += y;
            blob.left = std::min(blob.left, x);
            blob.top = std::min(blob.top, y);
            blob.right = std::max(blob.right, x);
            blob.bottom = std::max(blob.bottom, y);
            blob.perimeter += x == 0 || row[x - 1] != label;
            blob.perimeter += x + 1 == labels.cols || row[x + 1] != label;
            blob.perimeter += !above || above[x] != label;
            blob.perimeter += !below || below[x] != label;
            if (x == 0 || y == 0 || x + 1 == labels.cols || y + 1 == labels.rows)
                blob.touchesBoundary = true;
            else if (!source.valid.empty())
                blob.touchesBoundary = blob.touchesBoundary || !source.valid.at<uint8_t>(y, x - 1) ||
                                       !source.valid.at<uint8_t>(y, x + 1) ||
                                       !source.valid.at<uint8_t>(y - 1, x) ||
                                       !source.valid.at<uint8_t>(y + 1, x);
        }
    }
    std::vector<const Blob *> accepted;
    for (int i = 1; i < labelCount; ++i)
        if (blobs[i].area && accepts_blob(blobs[i], p)) {
            accepted.push_back(&blobs[i]);
            require(accepted.size() <= MaximumBlobs, JPV_RESOURCE_LIMIT,
                    "Blob result exceeds 10000 accepted regions; narrow the ROI or adjust filters.");
        }
    auto order = p.at("sortOrder").get<std::string>();
    std::sort(accepted.begin(), accepted.end(), [&](const Blob *a, const Blob *b) {
        if ((order == "areaDescending" || order == "areaAscending") && a->area != b->area)
            return order == "areaDescending" ? a->area > b->area : a->area < b->area;
        if (order == "labelAscending")
            return a->label < b->label;
        if (order == "yAscending" && a->centerY() != b->centerY())
            return a->centerY() < b->centerY();
        if (a->centerX() != b->centerX())
            return a->centerX() < b->centerX();
        if (a->centerY() != b->centerY())
            return a->centerY() < b->centerY();
        return a->label < b->label;
    });
    auto &features = result["features"];
    uint64_t totalArea = 0;
    for (const auto *blob : accepted) {
        auto feature = blob_feature(*blob, source);
        append_blob_overlay(result["overlay"], feature, int(features.size()), p);
        features.push_back(std::move(feature));
        totalArea += blob->area;
    }
    int count = int(accepted.size()), index = p.at("resultIndex");
    bool passed = count >= p.at("minimumCount").get<int>() && count <= p.at("maximumCount").get<int>();
    result["count"] = count;
    result["found"] = count != 0;
    result["passed"] = passed;
    result["candidateCount"] = labelCount - 1;
    result["rejectedCount"] = labelCount - 1 - count;
    result["totalArea"] = double(totalArea);
    result["reason"] = count ? "" : "NoAcceptedBlobs";
    result["measurement"]["count"] = count;
    result["measurement"]["found"] = count != 0;
    result["measurement"]["passed"] = passed;
    result["measurement"]["selectedValid"] = index < count;
    if (index < count)
        result["measurement"]["selected"] = features[index];
    return result;
}

void add_field(Json &fields, const std::string &path, const char *type, const char *label,
               bool nullable = false) {
    fields[path] = {{"type", type}, {"nullable", nullable}, {"label", label}};
}
} // namespace

void register_blob_components(ComponentRegistry &registry) {
    Json parameters = Json::array({
        setting("imageThresholdMethod", "图像分割方式", "enum", "fixed", "图像分割",
                "先将普通图像转换为灰度并分割；已携带二值掩膜的图像沿用原掩膜，不重复阈值分割。", nullptr,
                nullptr, Json::array({"fixed", "otsu", "triangle"})),
        setting("imageThreshold", "图像分割阈值", "integer", 128, "图像分割",
                "固定分割时，大于阈值的像素为白色前景；自动阈值方式不使用此值。", 0, 255, nullptr, "灰度级"),
        setting("imageInvert", "反向图像分割", "bool", false, "图像分割",
                "普通图像分割时将暗区域作为前景；已有二值掩膜不重新反相。"),
        setting("connectivity", "连通邻域", "integer", 8, "连通分析",
                "4 邻域只连接上下左右；8 邻域也连接对角相邻的白色像素。仅允许 4 或 8。", 4, 8),
        setting("minimumArea", "最小像素面积", "number", 1.0, "区域筛选",
                "包含下限；面积按实际前景像素数量统计，孔洞仍为背景，不按外轮廓填满。", 1, MaximumPixels,
                nullptr, "px²"),
        setting("maximumArea", "最大像素面积", "number", double(MaximumPixels), "区域筛选",
                "包含上限；使用当前输入图像的真实像素，不按显示缩放计算。", 1, MaximumPixels, nullptr, "px²"),
        setting("minimumWidth", "最小包围框宽度", "integer", 1, "区域筛选", "包含下限。", 1, MaximumDimension,
                nullptr, "px"),
        setting("maximumWidth", "最大包围框宽度", "integer", MaximumDimension, "区域筛选", "包含上限。", 1,
                MaximumDimension, nullptr, "px"),
        setting("minimumHeight", "最小包围框高度", "integer", 1, "区域筛选", "包含下限。", 1,
                MaximumDimension, nullptr, "px"),
        setting("maximumHeight", "最大包围框高度", "integer", MaximumDimension, "区域筛选", "包含上限。", 1,
                MaximumDimension, nullptr, "px"),
        setting("minimumCircularity", "最小网格圆度", "number", 0.0, "形状筛选",
                "4π × 像素面积 / 像素边界周长²；周长包含孔洞，与外轮廓圆度的数值不同。", 0, 1),
        setting("maximumCircularity", "最大网格圆度", "number", 1.0, "形状筛选", "包含上限。", 0, 1),
        setting("minimumAspectRatio", "最小宽高比", "number", 0.0, "形状筛选", "包围框宽 / 高，包含下限。", 0,
                MaximumDimension),
        setting("maximumAspectRatio", "最大宽高比", "number", double(MaximumDimension), "形状筛选",
                "包围框宽 / 高，包含上限。", 0, MaximumDimension),
        setting("minimumExtent", "最小矩形填充率", "number", 0.0, "形状筛选",
                "像素面积 / 包围框面积，0.5 表示 50%；孔洞会降低填充率。", 0, 1),
        setting("maximumExtent", "最大矩形填充率", "number", 1.0, "形状筛选", "包含上限。", 0, 1),
        setting("excludeBorder", "排除接触有效边界的区域", "bool", false, "区域筛选",
                "排除白色像素接触当前输入图边缘或 ROI 有效掩膜边界的区域；孔洞背景本身不算图像边界。"),
        setting(
            "sortOrder", "区域排序", "enum", "areaDescending", "结果选择",
            "在全部筛选完成后排序；位置排序按质心坐标，标签顺序按从上到下的扫描顺序。", nullptr, nullptr,
            Json::array({"areaDescending", "areaAscending", "xAscending", "yAscending", "labelAscending"})),
        setting("resultIndex", "当前区域索引（从 0 开始）", "integer", 0, "结果选择",
                "选择一个合格区域用于标量订阅；不会截断输出的全部区域。", 0, MaximumBlobs - 1),
        setting("minimumCount", "合格数量下限", "integer", 1, "数量判定",
                "全部筛选后的 Blob 数量，包含下限。", 0, MaximumBlobs),
        setting("maximumCount", "合格数量上限", "integer", MaximumBlobs, "数量判定",
                "包含上限；超出 10000 个实际合格区域时明确报错，不静默截断。", 0, MaximumBlobs),
        setting("showOverlay", "输出图形标记", "bool", true, "结果显示", "标记与图像像素独立传递。"),
        setting("showBoundingBox", "显示包围框", "bool", true, "结果显示", "显示筛选后的区域包围框。"),
        setting("showCentroid", "显示质心", "bool", true, "结果显示", "显示真实前景像素的平均位置。"),
        setting("labelResults", "显示编号与面积", "bool", false, "结果显示", "标记区域编号与像素面积。"),
        setting("overlayColor", "标记颜色（#RRGGBB）", "string", DefaultResultColor, "结果显示",
                "默认绿色 #00FF00；不写入输出图像的像素。"),
        setting("overlayLineWidth", "标记线宽", "number", 2.0, "结果显示", "不改变任何区域测量值。", .5, 10),
    });
    auto definition = component("BlobAnalysis", "Blob 分析", "检测", "Image", "Result", "result",
                                std::move(parameters), analyze_blobs, validate_blob_parameters);
    definition.descriptor["handlesUnavailableParameters"] = true;
    definition.descriptor["featureKind"] = "region";
    definition.descriptor["outputs"][0]["label"] = "Blob 分析结果";
    for (const auto &[id, type, label] :
         {std::tuple{"features", "Features", "全部区域"},
          std::tuple{"measurement", "Measurement", "选中区域测量"}, std::tuple{"count", "Int", "区域数量"},
          std::tuple{"found", "Bool", "找到区域"}, std::tuple{"passed", "Bool", "数量合格"},
          std::tuple{"overlay", "Overlay", "区域标记"}, std::tuple{"image", "Image", "图像与结果"}})
        definition.descriptor["outputs"].push_back({{"id", id}, {"type", type}, {"label", label}});
    definition.expandInputs = [](Value &value, const Json &, const InputValues &inputs) -> PortValues {
        auto &packet = std::get<Json>(value);
        auto image = std::get<Image>(*inputs.at("input"));
        Features features;
        features.dataAvailable = packet.at("dataAvailable");
        features.coordinateSpace = image.coordinateSpace;
        features.coordinatePlane = image.coordinatePlane;
        for (const auto &item : packet.at("features")) {
            RegionFeature region{item.at("area"),        item.at("perimeter"),
                                 item.at("centroidX"),   item.at("centroidY"),
                                 item.at("circularity"), item.at("x"),
                                 item.at("y"),           item.at("width"),
                                 item.at("height"),      true};
            region.areaKind = item.at("areaKind").get<std::string>();
            region.aspectRatio = item.at("aspectRatio").get<double>();
            region.extent = item.at("extent").get<double>();
            region.touchesBoundary = item.at("touchesBoundary").get<bool>();
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
    for (const auto &[path, label] :
         {std::pair{"count", "合格 Blob 数量"}, std::pair{"candidateCount", "候选 Blob 数量"},
          std::pair{"rejectedCount", "筛除 Blob 数量"}, std::pair{"measurement.count", "合格 Blob 数量"},
          std::pair{"measurement.selectedIndex", "选中区域索引"}})
        add_field(fields, path, "Int", label);
    add_field(fields, "totalArea", "Double", "合格区域总像素面积", true);
    for (const auto *path : {"found", "passed", "dataAvailable", "measurement.selectedValid"})
        add_field(fields, path, "Bool", path);
    for (const auto &[name, label] :
         {std::pair{"area", "像素面积"}, std::pair{"perimeter", "像素边界周长"},
          std::pair{"centroidX", "质心 X"}, std::pair{"centroidY", "质心 Y"},
          std::pair{"circularity", "网格圆度"}, std::pair{"aspectRatio", "包围框宽高比"},
          std::pair{"extent", "矩形填充率"}}) {
        const auto path = std::string("measurement.selected.") + name;
        add_field(fields, path, "Double", label, true);
        if (std::string(name) == "centroidX" || std::string(name) == "centroidY")
            fields[path]["validWhen"] = "measurement.selected.centroidValid";
    }
    for (const auto &[name, label] :
         {std::pair{"x", "包围框 X"}, std::pair{"y", "包围框 Y"}, std::pair{"width", "包围框宽度"},
          std::pair{"height", "包围框高度"}, std::pair{"pixelCount", "前景像素数量"},
          std::pair{"label", "连通标签"}})
        add_field(fields, std::string("measurement.selected.") + name, "Int", label, true);
    add_field(fields, "measurement.selected.touchesBoundary", "Bool", "接触有效图像边界", true);
    add_field(fields, "measurement.selected.centroidValid", "Bool", "质心有效", true);
    add_field(fields, "measurement.selected.areaKind", "String", "面积统计方式", true);
    registry.add(std::move(definition));
}
} // namespace jp

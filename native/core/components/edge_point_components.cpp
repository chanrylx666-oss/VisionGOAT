#include "edge_point_components.h"
#include "edge_reconstruction.h"
#include "image_pipeline.h"
#include "measurement.h"
#include "roi.h"
#include <algorithm>
#include <cmath>
#include <numbers>
#include <numeric>

namespace jp {
namespace {
constexpr uint64_t MaximumSamplingValues = 16ULL * 1024 * 1024;
constexpr int MaximumSearchDimension = 32768;
constexpr int MaximumProjectionWidth = 255;
constexpr int MaximumEdgePoints = MaximumSearchDimension + 1;

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

Json display_parameters(bool points) {
    Json result = Json::array({
        setting("showOverlay", "显示结果标记", "bool", true, "结果显示", "标记与图像像素独立传递。"),
        setting("overlayColor", "标记颜色（#RRGGBB）", "string", DefaultResultColor, "结果显示",
                "默认使用绿色 #00FF00。"),
        setting("overlayLineWidth", "标记线宽", "number", 2.0, "结果显示", "图形线宽，不改变测量值。", .5,
                10),
    });
    if (points) {
        result.push_back(setting("showPoints", "显示采样点", "bool", true, "结果显示",
                                 "实测点为小圆；补全点为较大空心圆，来源同时保留在结果表。"));
        result.push_back(setting("showCurve", "连接相邻边缘点", "bool", true, "结果显示",
                                 "仅连接连续输出测站；补全段用断续细线区分，未补全缺口不连接。"));
    }
    return result;
}

void append_parameters(Json &target, const Json &source) {
    for (const auto &value : source)
        target.push_back(value);
}

int sampling_station_count(const Json &parameters) {
    double width = parameters.at("roi").at("width"), spacing = parameters.at("sampleSpacing");
    double count = std::floor(width / spacing) + 1;
    require(std::isfinite(count) && count >= 1 && count <= parameters.at("maximumPoints").get<int>(),
            JPV_RESOURCE_LIMIT, "Edge-point station count exceeds maximumPoints; enlarge sampleSpacing.");
    return static_cast<int>(count);
}

void validate_edge_sampling(const Json &parameters) {
    require(parameters.at("projectionWidth").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
            "Caliper projection width must be odd.");
    const auto &roi = parameters.at("roi");
    if (roi.at("shape") == "none")
        return;
    require(roi.at("shape") == "rectangle" || roi.at("shape") == "rotatedRectangle", JPV_INVALID_RECIPE,
            "Edge-point extraction requires a rectangular or rotated rectangular search strip.");
    double height = roi.at("height"), sigma = parameters.at("sigma");
    require(height >= 8 && height <= MaximumSearchDimension && height >= 6 * sigma + 6, JPV_INVALID_RECIPE,
            "Edge-point search height must be 8 to 32768 pixels and sufficient for the smoothing sigma.");
    auto stations = sampling_station_count(parameters);
    uint64_t profileLength = static_cast<uint64_t>(2 * std::ceil(height / 2) + 1);
    require(profileLength * uint64_t(parameters.at("projectionWidth").get<int>()) * uint64_t(stations) <=
                MaximumSamplingValues,
            JPV_RESOURCE_LIMIT,
            "Edge-point extraction exceeds the 16 Mi sampling budget; increase sampleSpacing or reduce "
            "ROI height/projectionWidth.");
}

void validate_edge_points(const Json &parameters) {
    validate_roi(parameters.at("roi"));
    validate_edge_sampling(parameters);
    require(parameters.at("filterWindow").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
            "Edge curve filter window must be odd.");
}

Json empty_point_set(const Image &source, const Json &parameters, const char *reason, bool available = false) {
    Json result = {{"resultKind", "edgePointSet"},
            {"coordinateSpace", source.coordinateSpace},
            {"coordinatePlane", source.coordinatePlane},
            {"dataAvailable", available},
            {"valid", false},
            {"reason", reason},
            {"count", 0},
            {"stationCount", 0},
            {"missedCount", 0},
            {"coverage", 0.0},
            {"curveMode", parameters.at("curveMode")},
            {"rawCount", 0},
            {"measuredCount", 0},
            {"rejectedCount", 0},
            {"interpolatedCount", 0},
            {"observedCoverage", 0.0},
            {"rawPoints", Json::array()},
            {"points", Json::array()},
            {"overlay", Json::array()}};
    if (parameters.at("curveMode") == "raw") result.erase("rawPoints");
    return result;
}

Json segment(cv::Point2d first, cv::Point2d second, const Json &parameters) {
    return {{"type", "line"},
            {"points", Json::array({{first.x, first.y}, {second.x, second.y}})},
            {"color", parameters.at("overlayColor")},
            {"lineWidth", parameters.at("overlayLineWidth")}};
}

Json point_marker(cv::Point2d point, const Json &parameters) {
    return {{"type", "circle"},
            {"x", point.x},
            {"y", point.y},
            {"radius", 2.0},
            {"color", parameters.at("overlayColor")},
            {"lineWidth", parameters.at("overlayLineWidth")}};
}

Value find_edge_points(const Value *input, const Json &parameters, const Image &) {
    const auto &source = std::get<Image>(*input);
    if (parameters.contains("_unavailable"))
        return empty_point_set(source, parameters, "UnavailableParameter");
    if (!source.available || source.pixels.empty())
        return empty_point_set(source, parameters, "UnavailableInputImage");
    if (parameters.at("roi").at("shape") == "none")
        return empty_point_set(source, parameters, "MissingSearchRoi", true);
    // The definition was validated before positioning. A fixture may legitimately move its
    // center outside the image; validity masks and real sampling determine the visible points.
    validate_edge_sampling(parameters);
    auto gray = gray_pixels(source);
    auto result = empty_point_set(source, parameters, "NoEdgePoints", true);
    const auto &roi = parameters.at("roi");
    bool rotated = roi.at("shape") == "rotatedRectangle";
    cv::Point2d center = rotated
                             ? cv::Point2d(roi.at("cx"), roi.at("cy"))
                             : cv::Point2d(roi.at("x").get<double>() + roi.at("width").get<double>() / 2,
                                           roi.at("y").get<double>() + roi.at("height").get<double>() / 2);
    double angle = rotated ? roi.at("angleDegrees").get<double>() * std::numbers::pi / 180 : 0,
           width = roi.at("width"), spacing = parameters.at("sampleSpacing");
    cv::Point2d tangent(std::cos(angle), std::sin(angle)), normal(-tangent.y, tangent.x);
    if (parameters.at("reverseSearch").get<bool>())
        normal = -normal;
    require(std::isfinite(center.x) && std::isfinite(center.y) && std::isfinite(angle), JPV_INVALID_ARGUMENT,
            "Transformed edge-point search strip must have finite coordinates.");
    Json sampling = parameters;
    sampling["searchLength"] = roi.at("height");
    int count = sampling_station_count(parameters), samplingAvailable = 0;
    std::vector<bool> stationSamplingAvailable(count, false);
    double extent =
        std::ceil(roi.at("height").get<double>() / 2) + parameters.at("projectionWidth").get<int>() / 2.0 + 2;
    auto &points = result["points"];
    std::vector<CaliperSamplingStation> samplingStations;
    std::vector<int> stationIndices;
    samplingStations.reserve(count);
    stationIndices.reserve(count);
    for (int index = 0; index < count; ++index) {
        double position = index * spacing;
        auto station = center + tangent * (position - width / 2);
        // Reject a wholly distant station before the shared sampler converts coordinates to
        // pixel indices. Large legal image origins must produce unavailable samples safely.
        if (station.x < -extent || station.y < -extent || station.x > gray.cols + extent ||
            station.y > gray.rows + extent)
            continue;
        samplingStations.push_back({station, normal});
        stationIndices.push_back(index);
    }
    auto edges = sample_caliper_edges(source, gray, samplingStations, sampling);
    points.get_ref<Json::array_t &>().reserve(edges.size());
    for (size_t slot = 0; slot < edges.size(); ++slot) {
        const int index = stationIndices[slot];
        const auto &edge = edges[slot];
        samplingAvailable += edge.samplingAvailable;
        stationSamplingAvailable[index] = edge.samplingAvailable;
        if (!edge.found)
            continue;
        points.push_back({{"index", points.size()},
                          {"stationIndex", index},
                          {"x", edge.point.x + source.x},
                          {"y", edge.point.y + source.y},
                          {"gradient", edge.gradient},
                          {"pathPosition", index * spacing}});
    }
    int required = std::max(1, int(std::ceil(count * parameters.at("minimumCoverage").get<double>())));
    result["stationCount"] = count;
    result["dataAvailable"] = samplingAvailable >= required;
    result["validSamplingCount"] = samplingAvailable;
    if (!result.at("dataAvailable").get<bool>()) {
        points = Json::array();
        result["missedCount"] = count;
        result["reason"] = "UnavailableSamplingArea";
        return result;
    }
    edge_reconstruction::process(result, source, parameters, center, tangent, normal, count,
                                 stationSamplingAvailable);
    int measured = result.at("measuredCount"), found = int(points.size());
    result["count"] = found;
    result["missedCount"] = count - found;
    result["coverage"] = double(found) / count;
    // Estimated points never turn an insufficient measured contour into a valid inspection.
    result["valid"] = measured >= required;
    result["reason"] = measured == 0 ? (result.at("rawCount").get<int>() > 0 ? "NoQualifiedEdgePoints" : "NoEdgePoints")
                       : measured < required ? "InsufficientCoverage" : "";
    if (parameters.at("showOverlay").get<bool>()) {
        auto &overlay = result["overlay"];
        for (size_t index = 0; index < points.size(); ++index) {
            const auto &point = points[index];
            cv::Point2d current(point.at("x"), point.at("y"));
            std::string origin = point.value("origin", "measured");
            bool interpolated = origin == "interpolated";
            if (parameters.at("showPoints").get<bool>()) {
                auto marker = point_marker(current, parameters);
                if (point.contains("origin")) marker["origin"] = origin;
                if (interpolated) marker["radius"] = 3.5;
                overlay.push_back(std::move(marker));
            }
            if (parameters.at("showCurve").get<bool>() && index > 0 &&
                point.at("stationIndex").get<int>() == points[index - 1].at("stationIndex").get<int>() + 1) {
                const auto &previous = points[index - 1];
                cv::Point2d start(previous.at("x"), previous.at("y"));
                if (interpolated || previous.value("origin", "measured") == "interpolated") {
                    auto first = segment(start, start + (current - start) * .35, parameters);
                    auto second = segment(start + (current - start) * .65, current, parameters);
                    first["lineWidth"] = second["lineWidth"] =
                        std::max(.5, parameters.at("overlayLineWidth").get<double>() / 2);
                    first["origin"] = second["origin"] = "interpolated";
                    overlay.push_back(std::move(first)); overlay.push_back(std::move(second));
                } else overlay.push_back(segment(start, current, parameters));
            }
        }
    }
    return result;
}

Json empty_distances(const Json &parameters, const Image &source) {
    auto result = measurement_packet("pointSetToLine", "px", parameters);
    result["resultKind"] = "pointLineDistances";
    result["coordinateSpace"] = source.coordinateSpace;
    result["coordinatePlane"] = source.coordinatePlane;
    result["dataAvailable"] = false;
    result["reason"] = parameters.contains("_unavailable") ? "UnavailableParameter" : "MissingPointSet";
    result["count"] = 0;
    result["pointCount"] = 0;
    result["pointSource"] = parameters.at("pointSource");
    result["measuredCount"] = 0;
    result["interpolatedCount"] = 0;
    result["distances"] = Json::array();
    result["lineIndex"] = parameters.at("lineIndex");
    result["statistic"] = parameters.at("statistic");
    for (auto field : {"minimum", "maximum", "mean", "rms", "standardDeviation", "range", "minimumPoint",
                       "maximumPoint", "referenceLine"})
        result[field] = nullptr;
    return result;
}

Value point_line_distances(const InputValues &inputs, const Json &parameters, const Image &) {
    const auto &source = std::get<Image>(*inputs.at("input"));
    const auto &lineImage = inputs.contains("line") ? std::get<Image>(*inputs.at("line")) : source;
    require(source.coordinateSpace == lineImage.coordinateSpace &&
                source.coordinatePlane == lineImage.coordinatePlane,
            JPV_INVALID_ARGUMENT, "Point set and line belong to different image coordinate planes.");
    auto result = empty_distances(parameters, source);
    auto unavailable = [&](const char *reason) -> Value {
        result["reason"] = reason;
        result["decision"]["reason"] = reason;
        return result;
    };
    if (parameters.contains("_unavailable"))
        return unavailable("UnavailableParameter");
    if (!source.available || !lineImage.available)
        return unavailable("UnavailableInputImage");
    auto pointValue = image_context_value(source, "PointSet");
    const auto *pointSet = pointValue ? std::get_if<Json>(pointValue) : nullptr;
    if (!pointSet || !pointSet->is_object() || pointSet->value("resultKind", "") != "edgePointSet")
        return unavailable("MissingPointSet");
    result["pointCount"] = pointSet->at("points").size();
    if (!pointSet->value("dataAvailable", false))
        return unavailable("UnavailablePointSet");
    require(pointSet->value("coordinateSpace", source.coordinateSpace) == source.coordinateSpace &&
                pointSet->value("coordinatePlane", source.coordinatePlane) == source.coordinatePlane,
            JPV_INVALID_ARGUMENT, "Point-set coordinates do not match their image coordinate plane.");
    if (!pointSet->value("valid", false) || pointSet->at("points").empty())
        return unavailable("InvalidPointSet");
    auto lineValue = image_context_value(lineImage, "Features");
    const auto *features = lineValue ? std::get_if<Features>(lineValue) : nullptr;
    if (!features || !features->dataAvailable)
        return unavailable("MissingReferenceLine");
    require(features->coordinateSpace == source.coordinateSpace &&
                features->coordinatePlane == source.coordinatePlane,
            JPV_INVALID_ARGUMENT,
            "Fitted-line coordinates do not match the point-set image coordinate plane.");
    int lineIndex = parameters.at("lineIndex"), nextLineIndex = 0;
    const LineFeature *line = nullptr;
    for (const auto &feature : features->items)
        if (const auto *candidate = std::get_if<LineFeature>(&feature)) {
            if (nextLineIndex++ == lineIndex) {
                line = candidate;
                break;
            }
        }
    if (!line)
        return unavailable("ReferenceLineIndexOutOfRange");
    cv::Point2d first(line->x1, line->y1), second(line->x2, line->y2), direction = second - first;
    double length = cv::norm(direction);
    if (!std::isfinite(length) || length < 1e-9)
        return unavailable("DegenerateReferenceLine");
    if (direction.x < -1e-9 || (std::abs(direction.x) <= 1e-9 && direction.y < 0)) {
        std::swap(first, second);
        direction = -direction;
    }
    double a = -direction.y / length, b = direction.x / length, c = -(a * first.x + b * first.y);
    result["referenceLine"] = {{"x1", first.x}, {"y1", first.y}, {"x2", second.x}, {"y2", second.y},
                               {"a", a},        {"b", b},        {"c", c}};
    auto &distances = result["distances"];
    const auto &points = pointSet->at("points");
    require(points.size() <= MaximumEdgePoints, JPV_RESOURCE_LIMIT,
            "Point-line measurement supports up to 32769 points.");
    struct PointDistance {
        const Json *point;
        double x, y, projectionX, projectionY, distance, signedDistance;
        bool interpolated, adjusted;
    };
    std::vector<PointDistance> measured;
    measured.reserve(points.size());
    const bool rawMeasured = parameters.at("pointSource") == "measuredOnly";
    int measuredCount = 0, interpolatedCount = 0;
    double sum = 0, sumSquares = 0;
    size_t minimumIndex = 0, maximumIndex = 0;
    for (const auto &point : points) {
        std::string origin = point.value("origin", "measured");
        require(origin == "measured" || origin == "interpolated", JPV_INVALID_ARGUMENT,
                "Point set contains an unknown point origin.");
        if (rawMeasured && origin == "interpolated")
            continue;
        double x = rawMeasured ? point.value("rawX", point.at("x").get<double>()) : point.at("x").get<double>(),
               y = rawMeasured ? point.value("rawY", point.at("y").get<double>()) : point.at("y").get<double>(),
               signedDistance = a * x + b * y + c,
               distance = std::abs(signedDistance), projectionX = x - signedDistance * a,
               projectionY = y - signedDistance * b;
        require(std::isfinite(distance) && std::isfinite(projectionX) && std::isfinite(projectionY),
                JPV_INVALID_ARGUMENT, "Point-line measurement produced a nonfinite result.");
        const bool interpolated = origin == "interpolated";
        measured.push_back({&point, x, y, projectionX, projectionY, distance, signedDistance,
                            interpolated, !rawMeasured && point.value("adjusted", false)});
        if (interpolated) ++interpolatedCount; else ++measuredCount;
        auto current = measured.size() - 1;
        if (distance < measured[minimumIndex].distance)
            minimumIndex = current;
        if (distance > measured[maximumIndex].distance)
            maximumIndex = current;
        sum += distance;
        sumSquares += distance * distance;
    }
    if (measured.empty())
        return unavailable("NoMeasuredEdgePoints");
    double mean = sum / measured.size(), variance = 0;
    for (const auto &distance : measured) {
        double delta = distance.distance - mean;
        variance += delta * delta;
    }
    // Compute with native numbers first; materialize the public packet once after the statistics.
    // Point ordering, source coordinates, extrema tie-breaking and every per-point result are retained.
    distances.get_ref<Json::array_t &>().reserve(measured.size());
    for (size_t index = 0; index < measured.size(); ++index) {
        const auto &distance = measured[index];
        distances.push_back({{"index", index},
                             {"pointIndex", distance.point->at("index")},
                             {"stationIndex", distance.point->at("stationIndex")},
                             {"origin", distance.interpolated ? "interpolated" : "measured"},
                             {"adjusted", distance.adjusted},
                             {"coordinateSource", rawMeasured ? "rawMeasured" : "processed"},
                             {"x", distance.x}, {"y", distance.y},
                             {"projectionX", distance.projectionX}, {"projectionY", distance.projectionY},
                             {"distance", distance.distance}, {"signedDistance", distance.signedDistance}});
    }
    result["measuredCount"] = measuredCount;
    result["interpolatedCount"] = interpolatedCount;
    double minimum = measured[minimumIndex].distance, maximum = measured[maximumIndex].distance;
    result["minimum"] = minimum;
    result["maximum"] = maximum;
    result["mean"] = mean;
    result["rms"] = std::sqrt(sumSquares / measured.size());
    result["standardDeviation"] = std::sqrt(variance / measured.size());
    result["range"] = maximum - minimum;
    result["minimumPoint"] = distances[minimumIndex];
    result["maximumPoint"] = distances[maximumIndex];
    result["count"] = distances.size();
    result["dataAvailable"] = true;
    result["reason"] = "";
    finish_measurement(result, parameters, result.at(parameters.at("statistic").get<std::string>()));
    if (parameters.at("showOverlay").get<bool>()) {
        auto &overlay = result["overlay"];
        overlay.push_back(segment(first, second, parameters));
        int every = parameters.at("displayEveryNth");
        for (size_t index = 0; index < measured.size(); ++index) {
            if (index % every != 0 && index != minimumIndex && index != maximumIndex)
                continue;
            const auto &distance = measured[index];
            cv::Point2d point(distance.x, distance.y),
                projection(distance.projectionX, distance.projectionY);
            overlay.push_back(segment(point, projection, parameters));
            overlay.push_back(point_marker(point, parameters));
            overlay.push_back(point_marker(projection, parameters));
        }
    }
    return result;
}

void add_scalar(Json &fields, const char *name, const char *type, const char *label, bool nullable = false) {
    fields[name] = {{"type", type}, {"nullable", nullable}, {"label", label}};
}
} // namespace

void register_edge_point_components(ComponentRegistry &registry) {
    auto width = odd_parameter("projectionWidth", "卡尺切向宽度（奇数 px）", 5, 1, MaximumProjectionWidth);
    width["group"] = "搜索带与采样";
    width["description"] = "每个测站沿边缘长度方向汇总的像素宽度，用于均值或中值投影；与测站间距不同。"
                           "增大可抑制噪声，但可能抹平细节或混入相邻结构。";
    auto filterWindow = odd_parameter("filterWindow", "连续性邻域窗口（奇数测站）", 11, 3, 101);
    filterWindow["group"] = "曲线修正与补全";
    filterWindow["description"] = "去除局部斜率后，用邻域中值判断跳点；窗口越大参考越远。"
                                  "窗口内错误点占多数时无法可靠区分真实边缘，不能作为任意缺陷自动修复。";
    Json parameters = Json::array({
        setting("sampleSpacing", "采样间距（px）", "number", 4.0, "搜索带与采样",
                "沿搜索带长度 L 每隔 S 像素布置一个测站；总测站数 N = floor(L / S) + 1。"
                "间距越小点越密，不改变沿搜索高度 H 寻边的范围。",
                1, MaximumSearchDimension),
        std::move(width),
        setting("sigma", "法向平滑 Sigma（px）", "number", 1.0, "边缘筛选",
                "沿搜索高度方向平滑一维灰度投影，再提取梯度峰值；0 不平滑。"
                "搜索高度 H 至少为 max(8, 6 × Sigma + 6) px。",
                0, 10),
        setting("edgeThreshold", "最低边缘梯度（灰度级/px）", "number", 10.0, "边缘筛选",
                "只保留达到阈值的真实梯度峰；它是灰度变化强度，不是搜索距离。"
                "过高会丢点，过低易选到纹理；设为 0 也不会把平坦区域作为边缘。",
                0, 255, nullptr, "灰度级/px"),
        setting("polarity", "边缘极性", "enum", "any", "边缘筛选", "极性按卡尺法向搜索方向判断。", nullptr,
                nullptr, Json::array({"any", "darkToLight", "lightToDark"})),
        setting("edgeSelection", "边缘选择", "enum", "strongest", "边缘筛选",
                "每个测站仅保留第一个、最后一个或最强的真实边缘点。", nullptr, nullptr,
                Json::array({"first", "last", "strongest"})),
        setting("reverseSearch", "反转搜索方向", "bool", false, "搜索带与采样",
                "只反转卡尺法向，不改变点集沿宽度方向的排列顺序。"),
        setting("projectionStatistic", "投影统计", "enum", "mean", "边缘筛选", "中值投影可降低局部亮点影响。",
                nullptr, nullptr, Json::array({"mean", "median"})),
        setting("subpixelRefinement", "亚像素峰值插值", "bool", true, "边缘筛选",
                "使用相邻梯度峰值进行亚像素插值。"),
        setting("curveMode", "曲线处理方式", "enum", "raw", "曲线修正与补全",
                "原始模式保留旧结果；过滤模式剔除跳点；重建模式还按左右合格点补全内部短缺段。"
                "原始点始终保留，补全点单独标识。", nullptr, nullptr,
                Json::array({"raw", "filter", "reconstruct"})),
        std::move(filterWindow),
        setting("outlierTolerance", "跳点法向容差（px）", "number", 5.0, "曲线修正与补全",
                "相对去趋势邻域中值的最大法向偏差。减小会剔除更多点，也可能排除真实细小缺陷。",
                .01, MaximumSearchDimension, nullptr, "px"),
        setting("smoothCurve", "平滑保留点的显示曲线", "bool", true, "曲线修正与补全",
                "仅在过滤／重建模式生效，以局部稳健线性回归抑制锯齿并保留原始坐标。"
                "测距默认仍使用实测原始坐标，"
                "选择修正／补全点集时才使用平滑坐标。"),
        setting("maximumGapLength", "最大补全跨度（px）", "number", 80.0, "曲线修正与补全",
                "按左右合格锚点之间的沿带距离计算，不是缺失点数量。仅补内部缺段；0 禁用补全，"
                "超长、无两侧锚点或图像无效区域不会补点。", 0, MaximumSearchDimension, nullptr, "px"),
        setting("minimumCoverage", "最低有效点比例（0～1）", "number", .5, "结果有效性",
                "过滤后合格实测点占总测站数的最低比例；0.5 表示 50%。补全点不提高可靠度。"
                "不足时保留本次诊断点，但不能作为有效测量点集。",
                0, 1, nullptr, "比例"),
        setting("maximumPoints", "测站数量上限（个）", "integer", 10000, "高级资源限制",
                "限制 N = floor(L / S) + 1，不是要求输出的点数。超限时增大采样间距或提高此上限；"
                "仍须满足 N × (2 × ceil(H / 2) + 1) × 卡尺切向宽度 ≤ 16,777,216，"
                "不会静默截断点集。",
                2, MaximumEdgePoints, nullptr, "个"),
    });
    append_parameters(parameters, display_parameters(true));
    auto edge = component("FindEdgePoints", "边缘点集", "检测", "Image", "Result", "result",
                          std::move(parameters), find_edge_points, validate_edge_points);
    edge.descriptor["roiReferenceOnly"] = true;
    edge.descriptor["handlesUnavailableParameters"] = true;
    edge.descriptor["outputs"][0]["label"] = "边缘点集结果";
    edge.descriptor["outputs"].push_back(
        {{"id", "pointSet"}, {"type", "PointSet"}, {"label", "有序边缘点集"}, {"hidden", true}});
    edge.descriptor["outputs"].push_back(
        {{"id", "count"}, {"type", "Int"}, {"label", "有效边缘点数量"}, {"hidden", true}});
    edge.descriptor["outputs"].push_back(
        {{"id", "overlay"}, {"type", "Overlay"}, {"label", "边缘点集标记"}, {"hidden", true}});
    edge.expand = [](const Value &value, const Json &) -> PortValues {
        const auto &packet = std::get<Json>(value);
        return {{"pointSet", packet},
                {"count", packet.at("count").get<int64_t>()},
                {"overlay", packet.at("overlay")}};
    };
    auto &edgeFields = edge.descriptor["fields"]["result"];
    for (const auto &[name, label] :
         {std::pair{"count", "输出边缘点数"}, {"stationCount", "总测站数"}, {"missedCount", "未补全缺失测站数"},
          {"rawCount", "原始检测点数"}, {"measuredCount", "合格实测点数"}, {"rejectedCount", "剔除跳点数"},
          {"interpolatedCount", "模拟补全点数"}})
        add_scalar(edgeFields, name, "Int", label);
    add_scalar(edgeFields, "coverage", "Double", "有效测站比例");
    add_scalar(edgeFields, "observedCoverage", "Double", "合格实测覆盖比例");
    add_scalar(edgeFields, "valid", "Bool", "点集有效");
    add_scalar(edgeFields, "dataAvailable", "Bool", "采样数据可用");
    registry.add(std::move(edge));

    Json measurementParameters = Json::array({
        setting("lineIndex", "参考直线索引", "integer", 0, "参考直线", "仅对直线结果计数，从 0 开始。", 0,
                9999),
        setting("pointSource", "参与测距的点", "enum", "measuredOnly", "参考点集",
                "默认使用剔除跳点后的合格实测原始坐标；选择修正／补全点集时，"
                "使用平滑坐标及模拟点，距离结果仍分别标明来源。", nullptr, nullptr,
                Json::array({"measuredOnly", "all"})),
        setting("statistic", "公差判定统计值", "enum", "maximum", "距离统计",
                "对全部点到无限直线的垂直距离统计；每个点仍保留独立测量值。", nullptr, nullptr,
                Json::array({"minimum", "maximum", "mean", "rms", "range"})),
        setting("displayEveryNth", "距离标记间隔", "integer", 10, "结果显示",
                "每隔 N 个点绘制垂线，最大和最小距离总是显示；不会丢弃测量数据。", 1, 10000),
    });
    append_parameters(measurementParameters, tolerance_parameters());
    append_parameters(measurementParameters, display_parameters(false));
    auto measurement = component("MeasurePointLineDistances", "点集到线距离", "测量", "", "Result", "result",
                                 std::move(measurementParameters), {}, validate_tolerance);
    measurement.descriptor["inputs"] =
        Json::array({{{"id", "input"}, {"type", "Image"}, {"label", "点集图像"}},
                     {{"id", "line"}, {"type", "Image"}, {"label", "参考直线图像"}}});
    measurement.descriptor["inputGroups"] =
        Json::array({Json::array({"input"}), Json::array({"input", "line"})});
    measurement.executeInputs = point_line_distances;
    add_measurement_outputs(measurement);
    auto &fields = measurement.descriptor["fields"]["result"];
    add_scalar(fields, "count", "Int", "有效距离数量");
    add_scalar(fields, "pointCount", "Int", "源点集数量");
    add_scalar(fields, "measuredCount", "Int", "参与测距的实测点数");
    add_scalar(fields, "interpolatedCount", "Int", "参与测距的补全点数");
    for (const auto &[name, label] : {std::pair{"minimum", "最小垂直距离"},
                                      {"maximum", "最大垂直距离"},
                                      {"mean", "平均垂直距离"},
                                      {"rms", "距离均方根"},
                                      {"standardDeviation", "距离总体标准差"},
                                      {"range", "距离极差"},
                                      {"minimumPoint.x", "最小距离点 X"},
                                      {"minimumPoint.y", "最小距离点 Y"},
                                      {"maximumPoint.x", "最大距离点 X"},
                                      {"maximumPoint.y", "最大距离点 Y"}})
        add_scalar(fields, name, "Double", label, true);
    registry.add(std::move(measurement));
}
} // namespace jp

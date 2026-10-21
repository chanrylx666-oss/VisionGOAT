#include "measurement.h"
#include <cmath>
#include <iomanip>
#include <sstream>

namespace jp {
Json tolerance_parameters() {
    return Json::array(
        {parameter("toleranceEnabled", "启用尺寸公差", "bool", false, nullptr, nullptr, nullptr, true),
         parameter("toleranceMode", "公差方式", "enum", "limits", nullptr, nullptr,
                   Json::array({"limits", "deviation"})),
         parameter("lowerLimit", "下限（包含）", "number", 0.0, -1000000, 1000000, nullptr, true),
         parameter("upperLimit", "上限（包含）", "number", 1000.0, -1000000, 1000000, nullptr, true),
         parameter("nominalValue", "名义值", "number", 0.0, -1000000, 1000000, nullptr, true),
         parameter("lowerDeviation", "下偏差", "number", -1.0, -1000000, 1000000, nullptr, true),
         parameter("upperDeviation", "上偏差", "number", 1.0, -1000000, 1000000, nullptr, true)});
}
void validate_tolerance(const Json &p) {
    bool limits = p.at("toleranceMode") == "limits";
    require(p.at(limits ? "lowerLimit" : "lowerDeviation").get<double>() <=
                p.at(limits ? "upperLimit" : "upperDeviation").get<double>(),
            JPV_INVALID_RECIPE, "Measurement lower limit must not exceed upper limit.");
}
Json measurement_packet(const std::string &operation, const std::string &unit, const Json &p) {
    bool nominal = p.at("toleranceMode") == "deviation";
    double lo = nominal ? p.at("nominalValue").get<double>() + p.at("lowerDeviation").get<double>()
                        : p.at("lowerLimit").get<double>(),
           hi = nominal ? p.at("nominalValue").get<double>() + p.at("upperDeviation").get<double>()
                        : p.at("upperLimit").get<double>();
    Json result = {{"resultKind", "geometryMeasurement"},
                   {"operation", operation},
                   {"coordinateSpace", "original"},
                   {"unit", unit},
                   {"valid", false},
                   {"value", nullptr},
                   {"first", nullptr},
                   {"second", nullptr},
                   {"firstIndex", p.value("firstIndex", 0)},
                   {"secondIndex", p.value("secondIndex", 0)},
                   {"lowerLimit", lo},
                   {"upperLimit", hi},
                   {"nominalValue", nominal ? p.at("nominalValue") : Json()},
                   {"deviation", nullptr},
                   {"decision", {{"status", "Unknown"}, {"reason", "UnavailableSelection"}}},
                   {"overlay", Json::array()}};
    result["parametersAvailable"] = !p.contains("_unavailable");
    if (p.contains("_unavailable")) {
        result["unavailableParameters"] = p.at("_unavailable");
        result["decision"]["reason"] = "UnavailableParameter";
    }
    return result;
}
void finish_measurement(Json &result, const Json &p, double value) {
    require(std::isfinite(value), JPV_INVALID_ARGUMENT, "Measurement is not finite.");
    result["valid"] = true;
    result["value"] = value;
    if (!result.at("nominalValue").is_null())
        result["deviation"] = value - result.at("nominalValue").get<double>();
    bool inRange =
        value >= result.at("lowerLimit").get<double>() && value <= result.at("upperLimit").get<double>();
    result["decision"] = {
        {"status", p.at("toleranceEnabled").get<bool>() ? inRange ? "Pass" : "Fail" : "Unknown"},
        {"reason", p.at("toleranceEnabled").get<bool>() ? inRange ? "WithinTolerance" : "OutOfTolerance"
                                                        : "UnconfiguredTolerance"}};
}
void add_measurement_outputs(Component &definition) {
    definition.descriptor["handlesUnavailableParameters"] = true;
    definition.descriptor["fields"]["result"] = {
        {"value", {{"type", "Double"}, {"nullable", true}, {"label", "实测值"}}},
        {"valid", {{"type", "Bool"}, {"nullable", false}, {"label", "测量有效"}}},
        {"lowerLimit", {{"type", "Double"}, {"nullable", false}, {"label", "下限"}}},
        {"upperLimit", {{"type", "Double"}, {"nullable", false}, {"label", "上限"}}},
        {"deviation", {{"type", "Double"}, {"nullable", true}, {"label", "偏差"}}}};
    definition.descriptor["outputs"][0]["label"] = "全部结果";
    for (const auto &[id, type, label] :
         {std::tuple{"measurement", "Measurement", "测量结果"},
          std::tuple{"decision", "Decision", "尺寸判定"}, std::tuple{"overlay", "Overlay", "测量标记"}})
        definition.descriptor["outputs"].push_back(
            {{"id", id}, {"type", type}, {"label", label}, {"hidden", true}});
    definition.expand = [](const Value &value, const Json &) -> PortValues {
        const auto &result = std::get<Json>(value);
        return {
            {"measurement", result}, {"decision", result.at("decision")}, {"overlay", result.at("overlay")}};
    };
}
static cv::Point2d selected_point(const Feature &feature, const std::string &selection) {
    return std::visit(
        [&](const auto &f) -> cv::Point2d {
            using T = std::decay_t<decltype(f)>;
            if constexpr (std::is_same_v<T, CircleFeature>)
                return {f.x, f.y};
            else if constexpr (std::is_same_v<T, MatchFeature>)
                return {f.centerX, f.centerY};
            else if constexpr (std::is_same_v<T, RegionFeature>) {
                require(f.centroidValid, JPV_INVALID_ARGUMENT, "Region centroid is unavailable.");
                return {f.centroidX, f.centroidY};
            } else
                return selection == "start" ? cv::Point2d(f.x1, f.y1)
                       : selection == "end" ? cv::Point2d(f.x2, f.y2)
                                            : cv::Point2d((f.x1 + f.x2) / 2, (f.y1 + f.y2) / 2);
        },
        feature);
}
static Json point_source(cv::Point2d p) {
    return {{"kind", "point"}, {"point", {{"x", p.x}, {"y", p.y}}}};
}
static Json line_source(const LineFeature &line) {
    Features one;
    one.items.push_back(line);
    return {{"kind", "line"}, {"line", feature_values(one)[0]}};
}
static Json segment_overlay(cv::Point2d a, cv::Point2d b) {
    return {{"type", "line"},
            {"points", Json::array({{a.x, a.y}, {b.x, b.y}})},
            {"color", DefaultResultColor},
            {"lineWidth", 2}};
}
static Json point_json(cv::Point2d point) {
    return {{"x", point.x}, {"y", point.y}};
}
static Json circle_json(const CircleFeature &circle) {
    return {{"centerX", circle.x}, {"centerY", circle.y}, {"radius", circle.radius}};
}
static void circle_overlay(Json &result, const CircleFeature &circle) {
    result["overlay"].push_back({{"type", "circle"},
                                 {"x", circle.x},
                                 {"y", circle.y},
                                 {"radius", circle.radius},
                                 {"color", DefaultResultColor},
                                 {"lineWidth", 2}});
}
static void distance_graphics(Json &result, cv::Point2d first, cv::Point2d second, double value) {
    result["distanceSegment"] = {{"start", point_json(first)}, {"end", point_json(second)}};
    result["overlay"].push_back(segment_overlay(first, second));
    for (auto point : {first, second})
        result["overlay"].push_back({{"type", "circle"},
                                     {"x", point.x},
                                     {"y", point.y},
                                     {"radius", 3},
                                     {"color", DefaultResultColor},
                                     {"lineWidth", 2}});
    std::ostringstream label;
    label << std::fixed << std::setprecision(3) << value << " px";
    result["overlay"].push_back({{"type", "text"},
                                 {"color", DefaultResultColor},
                                 {"lineWidth", 1},
                                 {"x", (first.x + second.x) / 2 + 8},
                                 {"y", (first.y + second.y) / 2 - 16},
                                 {"text", label.str()}});
}
static void circular_distance_fields(Component &definition) {
    for (const auto &[field, label] :
         {std::pair{"centerDistance", "圆心距离"}, std::pair{"edgeDistance", "圆周最短间距"},
          std::pair{"distanceSegment.start.x", "测距起点 X"},
          std::pair{"distanceSegment.start.y", "测距起点 Y"},
          std::pair{"distanceSegment.end.x", "测距终点 X"}, std::pair{"distanceSegment.end.y", "测距终点 Y"}})
        definition.descriptor["fields"]["result"][field] = {
            {"type", "Double"}, {"nullable", true}, {"label", label}};
}
static Json distance_mode_parameter() {
    return parameter("distanceMode", "距离方式", "enum", "centerDistance", nullptr, nullptr,
                     Json::array({"centerDistance", "edgeDistance"}));
}
static void circular_distance_packet(Json &result, const Json &p) {
    result["distanceMode"] = p.at("distanceMode");
    result["centerDistance"] = nullptr;
    result["edgeDistance"] = nullptr;
    result["distanceSegment"] = nullptr;
    result["relation"] = nullptr;
}
struct CircleGap {
    double distance;
    cv::Point2d first, second;
    std::string relation;
};
static CircleGap circle_gap(const CircleFeature &a, const CircleFeature &b) {
    cv::Point2d ac(a.x, a.y), bc(b.x, b.y);
    double d = cv::norm(bc - ac), ra = a.radius, rb = b.radius;
    if (d == 0)
        return {std::abs(ra - rb), ac + cv::Point2d(ra, 0), bc + cv::Point2d(rb, 0),
                ra == rb ? "coincident" : "concentric"};
    auto direction = (bc - ac) / d;
    if (d >= ra + rb)
        return {d - ra - rb, ac + direction * ra, bc - direction * rb, d == ra + rb ? "tangent" : "separate"};
    if (d <= std::abs(ra - rb)) {
        auto side = ra >= rb ? direction : -direction;
        return {std::abs(ra - rb) - d, ac + side * ra, bc + side * rb,
                d == std::abs(ra - rb) ? "tangent" : "contained"};
    }
    // Intersecting circumferences share a point; a center projection is not on either edge.
    double along = (d * d + ra * ra - rb * rb) / (2 * d);
    double height = std::sqrt(std::max(0.0, ra * ra - along * along));
    auto point = ac + direction * along + cv::Point2d(-direction.y, direction.x) * height;
    return {0, point, point, "intersecting"};
}
void register_measurement_components(ComponentRegistry &registry) {
    auto definition = component("MeasureCircleDistance", "圆圆距离", "测量", "", "Result", "result",
                                Json::array({parameter("firstIndex", "圆 A 索引", "integer", 0, 0, 10000),
                                             parameter("secondIndex", "圆 B 索引", "integer", 0, 0, 10000),
                                             distance_mode_parameter()}),
                                {}, validate_tolerance);
    for (auto p : tolerance_parameters())
        definition.descriptor["parameters"].push_back(std::move(p));
    definition.descriptor["inputs"] =
        Json::array({{{"id", "first"}, {"type", "Features"}, {"label", "圆 A"}},
                     {{"id", "second"}, {"type", "Features"}, {"label", "圆 B"}}});
    add_measurement_outputs(definition);
    circular_distance_fields(definition);
    definition.executeInputs = [](const InputValues &inputs, const Json &p, const Image &) -> Value {
        const auto &first = std::get<Features>(*inputs.at("first"));
        const auto &second = std::get<Features>(*inputs.at("second"));
        size_t aIndex = p.at("firstIndex"), bIndex = p.at("secondIndex");
        bool edges = p.at("distanceMode") == "edgeDistance";
        Json result = measurement_packet(edges ? "circleEdgeDistance" : "circleCenterDistance", "px", p);
        circular_distance_packet(result, p);
        result["distanceDefinition"] = edges ? "circleBoundaries" : "circleCenters";
        if (p.contains("_unavailable"))
            return result;
        if (aIndex >= first.items.size() || bIndex >= second.items.size()) {
            result["decision"]["reason"] = "UnavailableSelection";
            return result;
        }
        const auto *a = std::get_if<CircleFeature>(&first.items[aIndex]);
        const auto *b = std::get_if<CircleFeature>(&second.items[bIndex]);
        require(a && b, JPV_INVALID_ARGUMENT, "Circle distance requires a circle at both selected indices.");
        require(std::isfinite(a->x) && std::isfinite(a->y) && std::isfinite(b->x) && std::isfinite(b->y) &&
                    std::isfinite(a->radius) && a->radius > 0 && std::isfinite(b->radius) && b->radius > 0,
                JPV_INVALID_ARGUMENT, "Circle coordinates and positive radii must be finite.");
        double centerDistance = std::hypot(b->x - a->x, b->y - a->y);
        auto gap = circle_gap(*a, *b);
        double distance = edges ? gap.distance : centerDistance;
        require(std::isfinite(distance), JPV_INVALID_ARGUMENT, "Circle distance is not finite.");
        finish_measurement(result, p, distance);
        result["first"] = circle_json(*a);
        result["second"] = circle_json(*b);
        result["centerDistance"] = centerDistance;
        result["edgeDistance"] = gap.distance;
        result["relation"] = gap.relation;
        circle_overlay(result, *a);
        circle_overlay(result, *b);
        distance_graphics(result, edges ? gap.first : cv::Point2d(a->x, a->y),
                          edges ? gap.second : cv::Point2d(b->x, b->y), distance);
        return result;
    };
    registry.add(std::move(definition));

    auto lineCircle = component(
        "MeasureLineCircleDistance", "线圆距离", "测量", "", "Result", "result",
        Json::array({parameter("firstIndex", "直线索引", "integer", 0, 0, 9999),
                     parameter("secondIndex", "圆索引", "integer", 0, 0, 9999), distance_mode_parameter()}),
        {}, validate_tolerance);
    for (auto p : tolerance_parameters())
        lineCircle.descriptor["parameters"].push_back(std::move(p));
    lineCircle.descriptor["inputs"] =
        Json::array({{{"id", "first"}, {"type", "Features"}, {"label", "直线"}},
                     {{"id", "second"}, {"type", "Features"}, {"label", "圆"}}});
    add_measurement_outputs(lineCircle);
    circular_distance_fields(lineCircle);
    lineCircle.executeInputs = [](const InputValues &inputs, const Json &p, const Image &) -> Value {
        const auto &first = std::get<Features>(*inputs.at("first"));
        const auto &second = std::get<Features>(*inputs.at("second"));
        Json result = measurement_packet("lineCircleDistance", "px", p);
        bool edges = p.at("distanceMode") == "edgeDistance";
        circular_distance_packet(result, p);
        result["distanceDefinition"] = edges ? "circleBoundaryToLine" : "circleCenterToLine";
        if (p.contains("_unavailable"))
            return result;
        size_t ai = p.at("firstIndex"), bi = p.at("secondIndex");
        if (ai >= first.items.size() || bi >= second.items.size())
            return result;
        const auto *line = std::get_if<LineFeature>(&first.items[ai]);
        const auto *circle = std::get_if<CircleFeature>(&second.items[bi]);
        require(line, JPV_INVALID_ARGUMENT, "This operation requires a line at input A.");
        require(circle, JPV_INVALID_ARGUMENT, "Line-circle distance requires a circle at input B.");
        cv::Point2d start(line->x1, line->y1), vector(line->x2 - line->x1, line->y2 - line->y1),
            center(circle->x, circle->y);
        double length = cv::norm(vector);
        result["first"] = line_source(*line);
        result["second"] = {{"kind", "circle"}, {"circle", circle_json(*circle)}};
        if (length < 1e-9) {
            result["decision"]["reason"] = "DegenerateLine";
            return result;
        }
        auto direction = vector / length;
        auto projection = start + direction * (center - start).dot(direction);
        double centerDistance = cv::norm(center - projection);
        double edgeDistance = std::max(0.0, centerDistance - circle->radius);
        cv::Point2d edgeOnLine, edgeOnCircle;
        if (centerDistance > circle->radius) {
            edgeOnLine = projection;
            edgeOnCircle = center + (projection - center) * (circle->radius / centerDistance);
            result["relation"] = "separate";
        } else {
            double along =
                std::sqrt(std::max(0.0, circle->radius * circle->radius - centerDistance * centerDistance));
            edgeOnLine = edgeOnCircle = projection + direction * along;
            result["relation"] = centerDistance == circle->radius ? "tangent" : "intersecting";
        }
        result["centerDistance"] = centerDistance;
        result["edgeDistance"] = edgeDistance;
        double distance = edges ? edgeDistance : centerDistance;
        finish_measurement(result, p, distance);
        result["overlay"].push_back(segment_overlay(start, {line->x2, line->y2}));
        circle_overlay(result, *circle);
        distance_graphics(result, edges ? edgeOnLine : projection, edges ? edgeOnCircle : center, distance);
        return result;
    };
    registry.add(std::move(lineCircle));

    auto geometric =
        component("MeasureGeometry", "几何测量", "测量", "", "Result", "result",
                  Json::array({parameter("operation", "测量方式", "enum", "pointDistance", nullptr, nullptr,
                                         Json::array({"pointDistance", "pointToLine", "lineDistance",
                                                      "lineAngle", "lineIntersection"})),
                               parameter("firstIndex", "对象 A 索引", "integer", 0, 0, 9999),
                               parameter("secondIndex", "对象 B 索引", "integer", 0, 0, 9999),
                               parameter("firstPoint", "A 的点位置（直线）", "enum", "center", nullptr,
                                         nullptr, Json::array({"center", "start", "end"})),
                               parameter("secondPoint", "B 的点位置（直线）", "enum", "center", nullptr,
                                         nullptr, Json::array({"center", "start", "end"})),
                               parameter("parallelAngleToleranceDegrees", "平行角度容差（°）", "number", 1.0,
                                         0, 10, nullptr, true)}),
                  {}, [](const Json &p) {
                      validate_tolerance(p);
                      require(p.value("operation", std::string("lineDistance")) != "lineIntersection" ||
                                  !p.at("toleranceEnabled").get<bool>(),
                              JPV_INVALID_RECIPE,
                              "Line intersection has no scalar tolerance; use its point coordinates.");
                  });
    for (auto p : tolerance_parameters())
        geometric.descriptor["parameters"].push_back(std::move(p));
    geometric.descriptor["inputs"] =
        Json::array({{{"id", "first"}, {"type", "Features"}, {"label", "对象 A"}},
                     {{"id", "second"}, {"type", "Features"}, {"label", "对象 B"}}});
    add_measurement_outputs(geometric);
    for (const auto &[field, label] :
         {std::pair{"lineAngleDegrees", "两线夹角（°）"}, std::pair{"distanceSegment.start.x", "测距起点 X"},
          std::pair{"distanceSegment.start.y", "测距起点 Y"},
          std::pair{"distanceSegment.end.x", "测距终点 X"}, std::pair{"distanceSegment.end.y", "测距终点 Y"}})
        geometric.descriptor["fields"]["result"][field] = {
            {"type", "Double"}, {"nullable", true}, {"label", label}};
    geometric.executeInputs = [](const InputValues &inputs, const Json &p, const Image &) -> Value {
        const auto &first = std::get<Features>(*inputs.at("first"));
        const auto &second = std::get<Features>(*inputs.at("second"));
        auto operation = p.value("operation", std::string("lineDistance"));
        Json result = measurement_packet(operation, operation == "lineAngle" ? "deg" : "px", p);
        result["point"] = nullptr;
        if (operation == "lineDistance") {
            result["distanceDefinition"] = "firstMidpointToSecondLine";
            result["parallelAngleToleranceDegrees"] = p.at("parallelAngleToleranceDegrees");
            result["lineAngleDegrees"] = nullptr;
            result["distanceSegment"] = nullptr;
        }
        if (p.contains("_unavailable"))
            return result;
        size_t ai = p.at("firstIndex"), bi = p.at("secondIndex");
        if (ai >= first.items.size() || bi >= second.items.size())
            return result;
        const auto &a = first.items[ai];
        const auto &b = second.items[bi];
        auto pointAvailable = [](const Feature &f) {
            auto region = std::get_if<RegionFeature>(&f);
            return !region || region->centroidValid;
        };
        if ((operation == "pointDistance" && (!pointAvailable(a) || !pointAvailable(b))) ||
            (operation == "pointToLine" && !pointAvailable(a))) {
            result["decision"]["reason"] = "UnavailablePoint";
            return result;
        }
        if (operation == "pointDistance") {
            auto ap = selected_point(a, p.at("firstPoint")), bp = selected_point(b, p.at("secondPoint"));
            result["first"] = point_source(ap);
            result["second"] = point_source(bp);
            finish_measurement(result, p, cv::norm(bp - ap));
            result["overlay"].push_back(segment_overlay(ap, bp));
        } else {
            const auto *lineB = std::get_if<LineFeature>(&b);
            require(lineB, JPV_INVALID_ARGUMENT, "This operation requires a line at input B.");
            auto bp = cv::Point2d(lineB->x1, lineB->y1),
                 bv = cv::Point2d(lineB->x2 - lineB->x1, lineB->y2 - lineB->y1);
            double bl = cv::norm(bv);
            result["second"] = line_source(*lineB);
            if (bl < 1e-9) {
                result["decision"]["reason"] = "DegenerateLine";
                return result;
            }
            if (operation == "pointToLine") {
                auto ap = selected_point(a, p.at("firstPoint"));
                auto projection = bp + bv * ((ap - bp).dot(bv) / (bl * bl));
                result["first"] = point_source(ap);
                finish_measurement(result, p, cv::norm(ap - projection));
                result["overlay"].push_back(segment_overlay(ap, projection));
            } else {
                const auto *lineA = std::get_if<LineFeature>(&a);
                require(lineA, JPV_INVALID_ARGUMENT, "This operation requires a line at input A.");
                auto ap = cv::Point2d(lineA->x1, lineA->y1),
                     av = cv::Point2d(lineA->x2 - lineA->x1, lineA->y2 - lineA->y1);
                double al = cv::norm(av), cross = av.x * bv.y - av.y * bv.x;
                result["first"] = line_source(*lineA);
                if (al < 1e-9) {
                    result["decision"]["reason"] = "DegenerateLine";
                    return result;
                }
                if (operation == "lineDistance") {
                    double angle = std::atan2(std::abs(cross), std::abs(av.dot(bv))) * 180 / CV_PI;
                    result["lineAngleDegrees"] = angle;
                    if (angle > p.at("parallelAngleToleranceDegrees").get<double>()) {
                        result["decision"]["reason"] = "NonParallelLines";
                        return result;
                    }
                    // Slightly skew fitted edges need a fixed measurement location: A's midpoint,
                    // projected perpendicularly onto B's infinite line, in original-image pixels.
                    auto midpoint = ap + av * 0.5;
                    auto projection = bp + bv * ((midpoint - bp).dot(bv) / (bl * bl));
                    double distance = cv::norm(midpoint - projection);
                    finish_measurement(result, p, distance);
                    result["overlay"].push_back(segment_overlay(ap, {lineA->x2, lineA->y2}));
                    result["overlay"].push_back(segment_overlay(bp, {lineB->x2, lineB->y2}));
                    distance_graphics(result, midpoint, projection, distance);
                } else if (operation == "lineAngle") {
                    finish_measurement(result, p,
                                       std::atan2(std::abs(cross), std::abs(av.dot(bv))) * 180 / CV_PI);
                    result["overlay"].push_back(segment_overlay(ap, {lineA->x2, lineA->y2}));
                    result["overlay"].push_back(segment_overlay(bp, {lineB->x2, lineB->y2}));
                } else {
                    if (std::abs(cross) <= 1e-9 * al * bl) {
                        result["decision"]["reason"] = "ParallelLines";
                        return result;
                    }
                    auto delta = bp - ap;
                    double t = (delta.x * bv.y - delta.y * bv.x) / cross;
                    auto point = ap + av * t;
                    require(std::isfinite(point.x) && std::isfinite(point.y), JPV_INVALID_ARGUMENT,
                            "Line intersection is not finite.");
                    result["valid"] = true;
                    result["point"] = {{"x", point.x}, {"y", point.y}};
                    result["decision"]["reason"] = "NoScalarTolerance";
                    result["overlay"].push_back({{"type", "circle"},
                                                 {"x", point.x},
                                                 {"y", point.y},
                                                 {"radius", 3},
                                                 {"color", DefaultResultColor},
                                                 {"lineWidth", 2}});
                }
            }
        }
        return result;
    };
    // The dedicated toolbox tool shares exactly the same geometry and result contract.
    auto lineDistance = geometric;
    lineDistance.descriptor["type"] = "MeasureLineDistance";
    lineDistance.descriptor["label"] = "线线距离";
    lineDistance.descriptor["inputs"][0]["label"] = "直线 A";
    lineDistance.descriptor["inputs"][1]["label"] = "直线 B";
    Json parameters = Json::array();
    for (const auto &p : geometric.descriptor.at("parameters"))
        if (p.at("name") != "operation" && p.at("name") != "firstPoint" && p.at("name") != "secondPoint")
            parameters.push_back(p);
    lineDistance.descriptor["parameters"] = std::move(parameters);
    registry.add(std::move(geometric));
    registry.add(std::move(lineDistance));
}
} // namespace jp

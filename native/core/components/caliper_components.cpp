#include "component.h"
#include "edge_point_components.h"
#include "measurement.h"
#include "roi.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <numbers>
#include <optional>
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>
#include <set>

namespace jp {
static constexpr int MaximumLineCaliperCount = 32768, MaximumLineFitIterations = 128;
static double sample(const Image &image, const cv::Mat &gray, double x, double y) {
    // Check before conversion so distant/NaN coordinates never become invalid integer indices.
    if (!(x >= 0 && y >= 0 && x < gray.cols - 1 && y < gray.rows - 1 &&
          x < image.pixels.cols - 1 && y < image.pixels.rows - 1))
        return NAN;
    int ix = int(std::floor(x)), iy = int(std::floor(y));
    if (!image.valid.empty()) {
        const auto *upper = image.valid.ptr<uint8_t>(iy) + ix;
        const auto *lower = image.valid.ptr<uint8_t>(iy + 1) + ix;
        // Keep all four validity checks even for zero interpolation weights.
        if (!upper[0] || !upper[1] || !lower[0] || !lower[1])
            return NAN;
    }
    const auto *upper = gray.ptr<uint8_t>(iy) + ix;
    const auto *lower = gray.ptr<uint8_t>(iy + 1) + ix;
    double fx = x - ix, fy = y - iy;
    return (1 - fy) * ((1 - fx) * upper[0] + fx * upper[1]) +
           fy * ((1 - fx) * lower[0] + fx * lower[1]);
}
struct EdgeProjection {
    std::vector<double> values, smooth, gradients;
    int half;
};
struct ProjectionSettings {
    int half, width, radius;
    bool median;
    std::vector<double> weights;
    double weightSum = 0;
    explicit ProjectionSettings(const Json &p)
        : half(int(std::ceil(p.at("searchLength").get<double>() / 2))),
          width(p.at("projectionWidth")), radius(0),
          median(p.value("projectionStatistic", "mean") == "median") {
        double sigma = p.at("sigma");
        radius = sigma > 0 ? std::max(1, int(std::ceil(sigma * 3))) : 0;
        weights.reserve(size_t(2 * radius + 1));
        for (int k = -radius; k <= radius; ++k) {
            double weight = radius ? std::exp(-k * k / (2 * sigma * sigma)) : 1;
            weights.push_back(weight);
            weightSum += weight;
        }
    }
};
struct ProjectionScratch {
    EdgeProjection projection;
    std::vector<double> window;
    explicit ProjectionScratch(const ProjectionSettings &p)
        : projection{std::vector<double>(2 * p.half + 1), std::vector<double>(2 * p.half + 1),
                     std::vector<double>(2 * p.half + 1), p.half},
          window(p.median ? p.width : 0) {}
};
static void edge_projection(const Image &image, const cv::Mat &gray, cv::Point2d center,
                            cv::Point2d normal, const ProjectionSettings &p,
                            ProjectionScratch &scratch) {
    const int half = p.half, width = p.width, radius = p.radius;
    auto &values = scratch.projection.values;
    auto &smooth = scratch.projection.smooth;
    auto &gradients = scratch.projection.gradients;
    auto &window = scratch.window;
    std::fill(values.begin(), values.end(), NAN);
    std::fill(smooth.begin(), smooth.end(), NAN);
    std::fill(gradients.begin(), gradients.end(), NAN);
    const double tangentX = -normal.y, tangentY = normal.x;
    for (int j = -half; j <= half; ++j) {
        double total = 0;
        bool valid = true;
        const double baseX = center.x + normal.x * double(j);
        const double baseY = center.y + normal.y * double(j);
        for (int k = -width / 2; k <= width / 2; ++k) {
            double x = baseX + tangentX * double(k);
            double y = baseY + tangentY * double(k);
            double value = sample(image, gray, x, y);
            valid &= std::isfinite(value);
            total += value;
            if (p.median)
                window[k + width / 2] = value;
        }
        if (valid) {
            if (p.median)
                std::nth_element(window.begin(), window.begin() + width / 2, window.begin() + width);
            values[j + half] = p.median ? window[width / 2] : total / width;
        }
    }
    for (int j = radius; j < int(values.size()) - radius; ++j) {
        double total = 0;
        for (int k = -radius; k <= radius; ++k) {
            total += values[j + k] * p.weights[k + radius];
        }
        smooth[j] = total / p.weightSum;
    }
    for (int j = 1; j < int(values.size()) - 1; ++j)
        gradients[j] = (smooth[j + 1] - smooth[j - 1]) / 2;
}
static EdgeProjection edge_projection(const Image &image, const cv::Mat &gray, cv::Point2d center,
                                      cv::Point2d normal, const Json &p) {
    ProjectionSettings settings(p);
    ProjectionScratch scratch(settings);
    edge_projection(image, gray, center, normal, settings, scratch);
    return std::move(scratch.projection);
}
struct ProjectionEdge {
    double offset, contrast;
    bool rising;
};
static void projection_edges(const EdgeProjection &projection, double threshold, bool subpixel,
                             const std::string &polarity, std::vector<ProjectionEdge> &edges) {
    const auto &gradients = projection.gradients;
    edges.clear();
    for (int j = 2; j < int(gradients.size()) - 2; ++j) {
        double raw = gradients[j], strength = polarity == "darkToLight"   ? raw
                                              : polarity == "lightToDark" ? -raw
                                                                          : std::abs(raw);
        if (!std::isfinite(strength) || strength < threshold)
            continue;
        auto magnitude = [&](int i) {
            return polarity == "darkToLight"   ? gradients[i]
                   : polarity == "lightToDark" ? -gradients[i]
                                               : std::abs(gradients[i]);
        };
        if (strength < magnitude(j - 1) || strength < magnitude(j + 1))
            continue;
        double a = std::abs(gradients[j - 1]), b = std::abs(gradients[j]), c = std::abs(gradients[j + 1]);
        double denominator = a - 2 * b + c, offset = subpixel &&
                                                             std::isfinite(denominator) &&
                                                             std::abs(denominator) > 1e-8
                                                         ? std::clamp(.5 * (a - c) / denominator, -.5, .5)
                                                         : 0;
        edges.push_back({j - projection.half + offset, strength, raw > 0});
    }
}
static std::vector<ProjectionEdge> projection_edges(const EdgeProjection &projection, const Json &p,
                                                    const std::string &polarity) {
    std::vector<ProjectionEdge> edges;
    projection_edges(projection, p.at("edgeThreshold"), p.value("subpixelRefinement", true), polarity,
                     edges);
    return edges;
}
class CaliperSampler {
    ProjectionSettings settings_;
    ProjectionScratch scratch_;
    std::vector<ProjectionEdge> candidates_;
    double threshold_;
    bool subpixel_;
    std::string polarity_, selection_;
  public:
    explicit CaliperSampler(const Json &p)
        : settings_(p), scratch_(settings_), threshold_(p.at("edgeThreshold")),
          subpixel_(p.value("subpixelRefinement", true)), polarity_(p.at("polarity").get<std::string>()),
          selection_(p.at("edgeSelection").get<std::string>()) {
        candidates_.reserve(scratch_.projection.gradients.size());
    }
    CaliperEdgeSample sample_edge(const Image &image, const cv::Mat &gray, cv::Point2d center,
                                  cv::Point2d normal) {
        CaliperEdgeSample result;
        edge_projection(image, gray, center, normal, settings_, scratch_);
        const auto &projection = scratch_.projection;
        result.samplingAvailable = std::any_of(projection.gradients.begin(), projection.gradients.end(),
                                               [](double value) { return std::isfinite(value); });
        projection_edges(projection, threshold_, subpixel_, polarity_, candidates_);
        // A zero threshold still requires a real edge; a flat profile is not a set of peaks.
        std::erase_if(candidates_, [](const auto &candidate) { return candidate.contrast <= 0; });
        if (candidates_.empty())
            return result;
        auto chosen = selection_ == "last" ? candidates_.end() - 1
                      : selection_ == "strongest"
                          ? std::max_element(candidates_.begin(), candidates_.end(),
                                             [](const auto &a, const auto &b) { return a.contrast < b.contrast; })
                          : candidates_.begin();
        result.found = true;
        result.point = center + normal * chosen->offset;
        result.gradient = chosen->contrast;
        return result;
    }
};
CaliperEdgeSample sample_caliper_edge(const Image &image, const cv::Mat &gray, cv::Point2d center,
                                      cv::Point2d normal, const Json &p) {
    CaliperSampler sampler(p);
    return sampler.sample_edge(image, gray, center, normal);
}
std::vector<CaliperEdgeSample> sample_caliper_edges(const Image &image, const cv::Mat &gray,
                                                  std::span<const CaliperSamplingStation> stations,
                                                  const Json &parameters) {
    CaliperSampler sampler(parameters);
    std::vector<CaliperEdgeSample> samples;
    samples.reserve(stations.size());
    for (const auto &station : stations)
        samples.push_back(sampler.sample_edge(image, gray, station.center, station.normal));
    return samples;
}
static bool edge_point(CaliperSampler &sampler, const Image &image, const cv::Mat &gray,
                       cv::Point2d center, cv::Point2d normal, cv::Point2d &edge,
                       bool &samplingAvailable) {
    auto result = sampler.sample_edge(image, gray, center, normal);
    edge = result.point;
    samplingAvailable = result.samplingAvailable;
    return result.found;
}
static Json common_parameters() {
    auto placement = parameter("caliperPlacement", "卡尺排列", "enum", "guide", nullptr, nullptr,
                               Json::array({"guide", "roiBoundary"}));
    placement["editorDefault"] = "roiBoundary";
    auto width = odd_parameter("projectionWidth", "卡尺宽度（奇数 px）", 5, 1, 63);
    return Json::array({std::move(placement), parameter("caliperCount", "卡尺数量", "integer", 24, 4, 256),
                        parameter("searchLength", "卡尺高度／搜索长度（px）", "number", 60.0, 8, 512),
                        std::move(width),
                        parameter("projectionStatistic", "投影统计", "enum", "mean", nullptr, nullptr,
                                  Json::array({"mean", "median"})),
                        parameter("subpixelRefinement", "亚像素峰值插值", "bool", true),
                        parameter("edgeThreshold", "边缘强度阈值", "number", 12.0, 1, 255),
                        parameter("sigma", "投影平滑 Sigma", "number", 1.0, 0, 5),
                        parameter("polarity", "边缘极性", "enum", "any", nullptr, nullptr,
                                  Json::array({"any", "darkToLight", "lightToDark"})),
                        parameter("edgeSelection", "边缘选择", "enum", "strongest", nullptr, nullptr,
                                  Json::array({"strongest", "first", "last"})),
                        parameter("maximumDeviation", "最大拟合偏差（px）", "number", 2.0, .1, 100),
                        parameter("removePointCount", "剔除点数（最大偏差点）", "integer", 0, 0, 253),
                        parameter("minimumCoverage", "最小有效卡尺比例", "number", .5, .1, 1)});
}
static void validate_caliper(const Json &p) {
    require(p.at("projectionWidth").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
            "Caliper projection width must be odd.");
    require(p.at("searchLength").get<double>() >= 6 * p.at("sigma").get<double>() + 6, JPV_INVALID_RECIPE,
            "Caliper search length is too short for the smoothing sigma.");
}
template <typename Error>
static void remove_outliers(std::vector<cv::Point2d> &points, int removeCount, double deviation,
                            Error error) {
    // Remove the requested worst samples first, then apply the geometric tolerance.
    std::stable_sort(points.begin(), points.end(), [&](auto a, auto b) { return error(a) > error(b); });
    points.erase(points.begin(), points.begin() + std::min(size_t(removeCount), points.size()));
    std::erase_if(points, [&](auto p) { return error(p) > deviation; });
}
static Json caliper_diagnostics(int requested, const std::vector<cv::Point2d> &sampled,
                                const std::vector<cv::Point2d> &retained) {
    Json rejected = Json::array();
    std::set<std::pair<double, double>> retainedPoints;
    for (auto p : retained)
        retainedPoints.emplace(p.x, p.y);
    for (auto p : sampled)
        if (!retainedPoints.contains({p.x, p.y}))
            rejected.push_back({{"x", p.x}, {"y", p.y}});
    return {{"requestedCount", requested},
            {"sampledCount", sampled.size()},
            {"retainedCount", retained.size()},
            {"rejectedCount", sampled.size() - retained.size()},
            {"missingCount", requested - sampled.size()},
            {"rejectedPoints", std::move(rejected)}};
}
static Json guide_diagnostic(const Image &image, cv::Point2d center, cv::Point2d normal, const Json &p) {
    return {{"x", center.x + image.x},
            {"y", center.y + image.y},
            {"normalX", normal.x},
            {"normalY", normal.y},
            {"width", p.at("projectionWidth")},
            {"height", 2 * std::ceil(p.at("searchLength").get<double>() / 2)}};
}
static void add_boundary_diagnostics(Features &out, Json guides) {
    out.diagnostics["placement"] = "roiBoundary";
    out.diagnostics["guides"] = std::move(guides);
}
static void validate_rejection(const Json &p, int minimum) {
    int required = std::max(
        minimum, int(std::ceil(p.at("caliperCount").get<int>() * p.at("minimumCoverage").get<double>())));
    require(p.at("removePointCount").get<int>() <= p.at("caliperCount").get<int>() - required,
            JPV_INVALID_RECIPE, "Caliper removal count leaves too few points for minimum coverage.");
}
struct LineFitResult {
    LineFeature line{};
    std::string failureReason;
    int iterations = 0;
    std::optional<double> initialMaximumDeviation, initialRmsError, maximumRetainedDeviation, finalRmsError;
};
static void line_residuals(const std::vector<cv::Point2d> &points, const cv::Vec4d &line,
                           std::optional<double> &maximum, std::optional<double> &rms) {
    maximum.reset();
    rms.reset();
    if (points.size() < 2)
        return;
    double worst = 0, squared = 0;
    for (auto p : points) {
        double distance = std::abs((p.x - line[2]) * line[1] - (p.y - line[3]) * line[0]);
        if (!std::isfinite(distance))
            return;
        worst = std::max(worst, distance);
        squared += distance * distance;
    }
    double value = std::sqrt(squared / points.size());
    if (std::isfinite(value)) {
        maximum = worst;
        rms = value;
    }
}
static void retained_line_residuals(const std::vector<cv::Point2d> &points, LineFitResult &result) {
    result.maximumRetainedDeviation.reset();
    result.finalRmsError.reset();
    if (points.size() < 2)
        return;
    cv::Vec4d line;
    cv::fitLine(points, line, cv::DIST_L2, 0, .01, .01);
    line_residuals(points, line, result.maximumRetainedDeviation, result.finalRmsError);
}
static LineFitResult fit_line(std::vector<cv::Point2d> &points, double deviation, int removeCount,
                              int required) {
    LineFitResult result;
    cv::Vec4d line;
    for (;;) {
        if (points.size() < size_t(required)) {
            result.failureReason = "InsufficientInliers";
            retained_line_residuals(points, result);
            return result;
        }
        if (result.iterations >= MaximumLineFitIterations) {
            result.failureReason = "FitIterationLimit";
            retained_line_residuals(points, result);
            return result;
        }
        ++result.iterations;
        cv::fitLine(points, line, cv::DIST_L2, 0, .01, .01);
        if (result.iterations == 1)
            line_residuals(points, line, result.initialMaximumDeviation, result.initialRmsError);
        line_residuals(points, line, result.maximumRetainedDeviation, result.finalRmsError);
        auto fittedError = [&](auto p) {
            return std::abs((p.x - line[2]) * line[1] - (p.y - line[3]) * line[0]);
        };
        cv::Vec4d reference = line;
        if (std::any_of(points.begin(), points.end(), [&](auto p) { return fittedError(p) > deviation; }))
            cv::fitLine(points, reference, cv::DIST_HUBER, 0, .01, .01);
        auto error = [&](auto p) {
            return std::abs((p.x - reference[2]) * reference[1] - (p.y - reference[3]) * reference[0]);
        };
        if (removeCount > 0) {
            std::stable_sort(points.begin(), points.end(),
                             [&](auto a, auto b) { return error(a) > error(b); });
            points.erase(points.begin(), points.begin() + std::min(size_t(removeCount), points.size()));
            removeCount = 0;
            continue;
        }
        auto before = points.size();
        // A line needs only threshold rejection here. Sorting on every refit adds no
        // information when the explicit worst-point removal count has already been used.
        std::erase_if(points, [&](auto p) { return error(p) > deviation; });
        if (points.size() == before) {
            auto worst = std::max_element(points.begin(), points.end(),
                                          [&](auto a, auto b) { return fittedError(a) < fittedError(b); });
            if (fittedError(*worst) > deviation) {
                points.erase(worst);
                continue;
            }
            break;
        }
    }
    cv::Point2d v(line[0], line[1]), center(line[2], line[3]);
    if (v.x < 0 || (std::abs(v.x) < 1e-9 && v.y < 0))
        v = -v;
    double first = INFINITY, last = -INFINITY, rms = 0;
    for (auto p : points) {
        double t = (p - center).dot(v);
        first = std::min(first, t);
        last = std::max(last, t);
        double error = (p - center).x * v.y - (p - center).y * v.x;
        rms += error * error;
    }
    auto a = center + v * first, b = center + v * last;
    if (!std::isfinite(cv::norm(b - a)) || cv::norm(b - a) < 1e-6) {
        result.failureReason = "DegenerateGeometry";
        return result;
    }
    result.line = {a.x,
                   a.y,
                   b.x,
                   b.y,
                   cv::norm(b - a),
                   std::atan2(v.y, v.x) * 180 / std::numbers::pi,
                   points,
                   std::sqrt(rms / points.size())};
    return result;
}
struct CircleFitResult {
    CircleFeature circle{};
    std::string failureReason;
    int iterations = 0;
};
static CircleFeature consensus_circle(const std::vector<cv::Point2d> &points, double deviation,
                                      const CircleFeature &initial) {
    CircleFeature best = initial;
    size_t bestCount = 0;
    double bestError = INFINITY;
    auto consider = [&](const CircleFeature &circle) {
        size_t count = 0;
        double error = 0;
        for (auto p : points) {
            double distance = std::abs(std::hypot(p.x - circle.x, p.y - circle.y) - circle.radius);
            if (distance <= deviation) {
                ++count;
                error += distance * distance;
            }
        }
        if (count > bestCount || (count == bestCount && error < bestError)) {
            best = circle;
            bestCount = count;
            bestError = error;
        }
    };
    consider(initial);
    if (bestCount == points.size())
        return best;
    auto triple = [&](size_t a, size_t b, size_t c) {
        if (a == b || a == c || b == c)
            return;
        auto u = points[b] - points[a], v = points[c] - points[a];
        double uu = u.dot(u), vv = v.dot(v), determinant = 2 * (u.x * v.y - u.y * v.x);
        if (std::abs(determinant) <= 1e-10 * std::max(uu, vv))
            return;
        double x = (v.y * uu - u.y * vv) / determinant, y = (u.x * vv - v.x * uu) / determinant,
               radius = std::hypot(x, y);
        if (std::isfinite(x) && std::isfinite(y) && std::isfinite(radius) && radius > 0)
            consider(CircleFeature{points[a].x + x, points[a].y + y, radius});
    };
    size_t count = points.size();
    for (size_t i = 0; i < count; ++i)
        triple(i, (i + count / 3) % count, (i + 2 * count / 3) % count);
    // A fixed seed makes the bounded consensus search reproducible, including clustered bad edges.
    uint32_t state = 0x4a505643;
    auto next = [&]() {
        state = 1664525u * state + 1013904223u;
        return size_t(state >> 8) % count;
    };
    for (int trial = 0; trial < 256; ++trial) {
        size_t a = next(), b = next(), c = next();
        triple(a, b, c);
    }
    return best;
}
static CircleFitResult fit_circle(std::vector<cv::Point2d> &points, double deviation, int removeCount,
                                  int required) {
    CircleFitResult result;
    // Every retry removes points, so the number of fits is bounded by the sample count plus one.
    for (;;) {
        if (points.size() < size_t(required)) {
            result.failureReason = "InsufficientInliers";
            return result;
        }
        ++result.iterations;
        cv::Mat a(int(points.size()), 3, CV_64F), b(int(points.size()), 1, CV_64F), solution;
        cv::Point2d origin;
        for (auto p : points)
            origin += p;
        origin *= 1.0 / points.size();
        for (int i = 0; i < int(points.size()); ++i) {
            auto p = points[i] - origin;
            a.at<double>(i, 0) = 2 * p.x;
            a.at<double>(i, 1) = 2 * p.y;
            a.at<double>(i, 2) = 1;
            b.at<double>(i) = p.x * p.x + p.y * p.y;
        }
        cv::SVD svd(a, cv::SVD::NO_UV);
        if (svd.w.at<double>(2) <= svd.w.at<double>(0) * 1e-8) {
            result.failureReason = "DegenerateGeometry";
            return result;
        }
        if (!cv::solve(a, b, solution, cv::DECOMP_SVD)) {
            result.failureReason = "NumericalFailure";
            return result;
        }
        double localX = solution.at<double>(0), localY = solution.at<double>(1), cx = localX + origin.x,
               cy = localY + origin.y, radius2 = solution.at<double>(2) + localX * localX + localY * localY;
        if (!std::isfinite(cx) || !std::isfinite(cy) || !std::isfinite(radius2) || radius2 <= 0) {
            result.failureReason = "InvalidCircle";
            return result;
        }
        result.circle = {cx, cy, std::sqrt(radius2)};
        const auto reference = consensus_circle(points, deviation, result.circle);
        auto error = [&](auto p) {
            return std::abs(std::hypot(p.x - reference.x, p.y - reference.y) - reference.radius);
        };
        if (removeCount > 0) {
            std::stable_sort(points.begin(), points.end(),
                             [&](auto a, auto b) { return error(a) > error(b); });
            points.erase(points.begin(), points.begin() + std::min(size_t(removeCount), points.size()));
            removeCount = 0;
            // Refit before checking the distance limit: the old circle is biased by the removed samples.
            continue;
        }
        auto before = points.size();
        remove_outliers(points, 0, deviation, error);
        if (points.size() == before) {
            auto fittedError = [&](auto p) {
                return std::abs(std::hypot(p.x - cx, p.y - cy) - result.circle.radius);
            };
            auto worst = std::max_element(points.begin(), points.end(),
                                          [&](auto a, auto b) { return fittedError(a) < fittedError(b); });
            if (fittedError(*worst) > deviation) {
                points.erase(worst);
                continue;
            }
            break;
        }
    }
    double rms = 0;
    for (auto p : points) {
        double error = std::hypot(p.x - result.circle.x, p.y - result.circle.y) - result.circle.radius;
        rms += error * error;
    }
    result.circle.edgePoints = points;
    result.circle.rmsError = std::sqrt(rms / points.size());
    return result;
}
Json circle_caliper_parameters() {
    auto parameters = common_parameters();
    parameters.push_back(parameter("radius", "指引圆半径（0 自动）", "number", 0.0, 0, 16384));
    parameters.push_back(parameter("startAngle", "起始角度（°）", "number", 0.0, -360, 360));
    parameters.push_back(parameter("endAngle", "终止角度（°）", "number", 360.0, -360, 720));
    return parameters;
}
void validate_circle_caliper(const Json &p) {
    require(p.at("caliperCount").get<int>() >= 4 && p.at("caliperCount").get<int>() <= 256,
            JPV_INVALID_RECIPE, "Circle caliper count must be 4 to 256.");
    validate_caliper(p);
    validate_rejection(p, 3);
    if (p.at("caliperPlacement") == "roiBoundary")
        require(p.at("roi").at("shape") == "circle" || p.at("roi").at("shape") == "annulus" ||
                    (p.contains("positionReference") && p.at("positionReference") != "image" &&
                     p.at("roi").at("shape") == "none"),
                JPV_INVALID_RECIPE, "Circular boundary calipers require a circle ROI.");
    bool annulus = p.at("roi").at("shape") == "annulus";
    if (annulus)
        require(p.at("roi").at("outerRadius").get<double>() - p.at("roi").at("innerRadius").get<double>() >=
                    std::max(8.0, 6 * p.at("sigma").get<double>() + 6),
                JPV_INVALID_RECIPE, "Caliper annulus is too narrow for its smoothing filter.");
    double range = annulus
                       ? p.at("roi").at("endAngle").get<double>() - p.at("roi").at("startAngle").get<double>()
                       : p.at("endAngle").get<double>() - p.at("startAngle").get<double>();
    require(range >= 10 && range <= 360, JPV_INVALID_RECIPE,
            "Circle caliper arc must span 10 to 360 degrees.");
}
Features find_circle_caliper(const Image &image, const Json &p) {
    auto gray = gray_pixels(image);
    CaliperSampler sampler(p);
    Features out;
    std::vector<cv::Point2d> points;
    bool boundary = p.at("caliperPlacement") == "roiBoundary";
    double radius = p.at("radius");
    if (boundary)
        radius = p.at("roi").at("shape") == "annulus" ? (p.at("roi").at("innerRadius").get<double>() +
                                                         p.at("roi").at("outerRadius").get<double>()) /
                                                            2
                                                      : p.at("roi").at("radius").get<double>();
    else if (radius == 0)
        radius = std::min(gray.cols - 1, gray.rows - 1) * .375;
    int count = p.at("caliperCount");
    double start = p.at("startAngle"), range = p.at("endAngle").get<double>() - start;
    cv::Point2d center((gray.cols - 1) / 2.0, (gray.rows - 1) / 2.0);
    if (boundary)
        center = {p.at("roi").at("cx"), p.at("roi").at("cy")};
    Json guides = Json::array();
    int validSampling = 0;
    for (int i = 0; i < count; ++i) {
        double angle = (start + range * i / (range >= 359.999 ? count : count - 1)) * std::numbers::pi / 180;
        cv::Point2d normal(std::cos(angle), std::sin(angle)), edge;
        auto station = center + normal * radius;
        if (boundary)
            guides.push_back(guide_diagnostic(image, station, normal, p));
        bool samplingAvailable = false;
        if (edge_point(sampler, image, gray, station, normal, edge, samplingAvailable))
            points.push_back(edge + cv::Point2d(image.x, image.y));
        validSampling += samplingAvailable;
    }
    auto sampled = points;
    int required = std::max(3, int(std::ceil(count * p.at("minimumCoverage").get<double>())));
    CircleFitResult fit;
    if (points.size() < size_t(required))
        fit.failureReason = "InsufficientEdgePoints";
    else {
        fit = fit_circle(points, p.at("maximumDeviation"), p.at("removePointCount"), required);
        if (fit.failureReason.empty())
            out.items.push_back(std::move(fit.circle));
    }
    out.diagnostics = caliper_diagnostics(count, sampled, points);
    out.dataAvailable = validSampling >= required;
    out.diagnostics["validSamplingCalipers"] = validSampling;
    out.diagnostics["fitSucceeded"] = !out.items.empty();
    out.diagnostics["fitFailureReason"] = out.dataAvailable ? fit.failureReason : "UnavailableSamplingArea";
    out.diagnostics["minimumRequiredPoints"] = required;
    out.diagnostics["requestedRemovalCount"] = p.at("removePointCount");
    out.diagnostics["maximumDeviation"] = p.at("maximumDeviation");
    out.diagnostics["fitIterations"] = fit.iterations;
    double halfSpan = std::ceil(p.at("searchLength").get<double>() / 2);
    out.diagnostics["searchAnnulus"] = {
        {"coordinateSpace", "original"},    {"centerX", center.x + image.x},
        {"centerY", center.y + image.y},    {"innerRadius", std::max(0.0, radius - halfSpan)},
        {"outerRadius", radius + halfSpan}, {"startAngle", start},
        {"endAngle", start + range}};
    if (boundary)
        add_boundary_diagnostics(out, std::move(guides));
    return out;
}
struct BoundaryLine {
    cv::Point2d a, b, normal;
};
static BoundaryLine boundary_line(const Json &p) {
    const auto &roi = p.at("roi");
    if (roi.at("shape") == "rectangle") {
        double x = roi.at("x"), y = roi.at("y"), right = x + roi.at("width").get<double>(),
               bottom = y + roi.at("height").get<double>();
        auto side = p.at("roiEdge").get<std::string>();
        if (side == "top")
            return {{x, y}, {right, y}, {0, -1}};
        if (side == "right")
            return {{right, y}, {right, bottom}, {1, 0}};
        if (side == "bottom")
            return {{right, bottom}, {x, bottom}, {0, 1}};
        return {{x, bottom}, {x, y}, {-1, 0}};
    }
    auto vertices = roi_vertices(roi);
    auto point = [&](size_t i) -> cv::Point2d { return vertices[i]; };
    auto side = p.at("roiEdge").get<std::string>();
    int index = roi.at("shape") == "rotatedRectangle" ? (side == "top"      ? 0
                                                         : side == "right"  ? 1
                                                         : side == "bottom" ? 2
                                                                            : 3)
                                                      : p.at("roiEdgeIndex").get<int>();
    auto a = point(index), b = point((index + 1) % vertices.size()), tangent = b - a;
    double area = 0;
    for (size_t i = 0; i < vertices.size(); ++i) {
        auto u = point(i), v = point((i + 1) % vertices.size());
        area += u.x * v.y - u.y * v.x;
    }
    double direction = area > 0 ? 1 : -1;
    return {a, b, cv::Point2d(tangent.y, -tangent.x) * (direction / cv::norm(tangent))};
}
Json line_caliper_parameters() {
    auto parameters = common_parameters();
    parameters[0]["choices"].push_back("roiCenter");
    for (auto &p : parameters) {
        if (p.at("name") == "caliperCount") {
            p["max"] = MaximumLineCaliperCount;
            p["description"] = "沿搜索带中线、指定 ROI 边或指引线均匀布置卡尺；数量增加会减小沿线间距，"
                               "但不会自动放宽拟合偏差或最低有效比例。仍受 16 Mi 采样工作量限制。";
        } else if (p.at("name") == "removePointCount") {
            p["max"] = MaximumLineCaliperCount - 2;
            p["description"] = "首次拟合后优先剔除指定数量的最差点，再按最大拟合偏差筛选；"
                               "需保留至少 max(2, ceil(卡尺数量 × 最低有效比例)) 个真实点。";
        }
    }
    parameters.push_back(parameter("reverseSearch", "反转搜索方向", "bool", false));
    parameters.push_back(parameter("roiEdge", "矩形参考边", "enum", "top", nullptr, nullptr,
                                   Json::array({"top", "right", "bottom", "left"})));
    parameters.push_back(parameter("roiEdgeIndex", "多边形参考边（从 0 开始）", "integer", 0, 0, 63));
    parameters.push_back(parameter("angleDegrees", "指引线角度（°）", "number", 0.0, -180, 180));
    parameters.push_back(parameter("guideLength", "指引线长度（0 自动）", "number", 0.0, 0, 32768));
    return parameters;
}
void validate_line_caliper(const Json &p) {
    auto effective = p;
    const auto &roi = p.at("roi");
    auto shape = roi.at("shape").get<std::string>();
    if (p.at("caliperPlacement") == "roiCenter") {
        require(shape == "rectangle" || shape == "rotatedRectangle", JPV_INVALID_RECIPE,
                "Line strip calipers require a rectangle or rotated rectangle ROI.");
        require(roi.at("height").get<double>() <= 512, JPV_INVALID_RECIPE,
                "Line strip search height must not exceed 512 pixels.");
        effective["searchLength"] = roi.at("height");
    }
    validate_caliper(effective);
    validate_rejection(p, 2);
    if (p.at("caliperPlacement") == "roiBoundary" || p.value("roiReference", "image") == "fixture") {
        require(shape == "rectangle" || shape == "rotatedRectangle" || shape == "polygon", JPV_INVALID_RECIPE,
                "Line boundary calipers require a rectangle or polygon ROI.");
        if (shape == "polygon")
            require(size_t(p.at("roiEdgeIndex").get<int>()) < roi.at("points").size(), JPV_INVALID_RECIPE,
                    "Polygon caliper edge index exceeds ROI vertex count.");
    }
}
Features find_line_caliper(const Image &image, const Json &p) {
    auto gray = gray_pixels(image);
    CaliperSampler sampler(p);
    Features out;
    std::vector<cv::Point2d> points;
    bool boundary = p.at("caliperPlacement") == "roiBoundary",
         strip = p.at("caliperPlacement") == "roiCenter";
    double angle = p.at("angleDegrees").get<double>() * std::numbers::pi / 180;
    cv::Point2d tangent(std::cos(angle), std::sin(angle)), normal(-tangent.y, tangent.x),
        center((gray.cols - 1) / 2.0, (gray.rows - 1) / 2.0);
    double length = p.at("guideLength");
    if (length == 0)
        length = .9 * std::min(gray.cols / std::max(1e-8, std::abs(tangent.x)),
                               gray.rows / std::max(1e-8, std::abs(tangent.y)));
    int count = p.at("caliperCount");
    BoundaryLine reference;
    if (boundary)
        reference = boundary_line(p);
    if (strip) {
        const auto &roi = p.at("roi");
        bool rotated = roi.at("shape") == "rotatedRectangle";
        angle = rotated ? roi.at("angleDegrees").get<double>() * std::numbers::pi / 180 : 0;
        tangent = {std::cos(angle), std::sin(angle)};
        normal = {-tangent.y, tangent.x};
        center = rotated ? cv::Point2d(roi.at("cx"), roi.at("cy"))
                         : cv::Point2d(roi.at("x").get<double>() + roi.at("width").get<double>() / 2,
                                       roi.at("y").get<double>() + roi.at("height").get<double>() / 2);
        length = roi.at("width");
    }
    Json guides = Json::array();
    int validSampling = 0;
    for (int i = 0; i < count; ++i) {
        cv::Point2d edge;
        double offset = strip ? length * ((i + .5) / count - .5) : length * (double(i) / (count - 1) - .5);
        auto station = boundary ? reference.a + (reference.b - reference.a) * ((i + .5) / count)
                                : center + tangent * offset;
        auto sampleNormal = boundary ? reference.normal : normal;
        if (p.value("reverseSearch", false))
            sampleNormal = -sampleNormal;
        if (boundary || strip)
            guides.push_back(guide_diagnostic(image, station, sampleNormal, p));
        bool samplingAvailable = false;
        if (edge_point(sampler, image, gray, station, sampleNormal, edge, samplingAvailable))
            points.push_back(edge + cv::Point2d(image.x, image.y));
        validSampling += samplingAvailable;
    }
    auto sampled = points;
    int required = std::max(2, int(std::ceil(count * p.at("minimumCoverage").get<double>())));
    LineFitResult fit;
    if (points.size() < size_t(required))
        fit.failureReason = "InsufficientEdgePoints";
    else {
        fit = fit_line(points, p.at("maximumDeviation"), p.at("removePointCount"), required);
        if (fit.failureReason.empty())
            out.items.push_back(std::move(fit.line));
    }
    out.diagnostics = caliper_diagnostics(count, sampled, points);
    out.dataAvailable = validSampling >= required;
    out.diagnostics["validSamplingCalipers"] = validSampling;
    out.diagnostics["fitSucceeded"] = !out.items.empty();
    out.diagnostics["fitFailureReason"] = out.dataAvailable ? fit.failureReason : "UnavailableSamplingArea";
    out.diagnostics["fitIterations"] = fit.iterations;
    out.diagnostics["minimumRequiredPoints"] = required;
    out.diagnostics["requestedRemovalCount"] = p.at("removePointCount");
    out.diagnostics["maximumDeviation"] = p.at("maximumDeviation");
    out.diagnostics["initialMaximumDeviation"] =
        fit.initialMaximumDeviation ? Json(*fit.initialMaximumDeviation) : Json();
    out.diagnostics["initialRmsError"] = fit.initialRmsError ? Json(*fit.initialRmsError) : Json();
    out.diagnostics["maximumRetainedDeviation"] =
        fit.maximumRetainedDeviation ? Json(*fit.maximumRetainedDeviation) : Json();
    out.diagnostics["finalRmsError"] = fit.finalRmsError ? Json(*fit.finalRmsError) : Json();
    out.diagnostics["fitIterationLimit"] = MaximumLineFitIterations;
    double pathLength = boundary ? cv::norm(reference.b - reference.a) : length;
    out.diagnostics["effectiveSamplingSpacing"] = pathLength / (boundary || strip ? count : count - 1);
    if (boundary || strip) {
        out.diagnostics["placement"] = strip ? "roiCenter" : "roiBoundary";
        out.diagnostics["guides"] = std::move(guides);
    }
    return out;
}
void register_caliper_components(ComponentRegistry &registry) {
    auto pair = component(
        "MeasureEdgePair", "双边卡尺／宽度", "测量", "Image", "Result", "result",
        Json::array({parameter("sigma", "投影平滑 Sigma", "number", 1.0, 0, 5),
                     parameter("projectionStatistic", "投影统计", "enum", "mean", nullptr, nullptr,
                               Json::array({"mean", "median"})),
                     parameter("subpixelRefinement", "亚像素峰值插值", "bool", true),
                     parameter("edgeThreshold", "边缘对比度阈值", "number", 10.0, 1, 255),
                     parameter("firstPolarity", "第一边极性", "enum", "darkToLight", nullptr, nullptr,
                               Json::array({"darkToLight", "lightToDark", "any"})),
                     parameter("secondPolarity", "第二边极性", "enum", "lightToDark", nullptr, nullptr,
                               Json::array({"darkToLight", "lightToDark", "any"})),
                     parameter("reverseSearch", "反转搜索方向", "bool", false),
                     parameter("expectedWidth", "期望边缘间距（px）", "number", 20.0, 0, 32768),
                     parameter("minimumWidth", "最小候选间距（px）", "number", 1.0, 0, 32768),
                     parameter("maximumWidth", "最大候选间距（px）", "number", 512.0, 0, 32768),
                     parameter("pairSelection", "边缘对选择", "enum", "closestWidth", nullptr, nullptr,
                               Json::array({"closestWidth", "strongest"}))}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &image = std::get<Image>(*input);
            Json result = measurement_packet("edgePairWidth", "px", p);
            if (p.contains("_unavailable"))
                return result;
            if (!image.available)
                return result;
            const auto &roi = p.at("roi");
            bool rotated = roi.at("shape") == "rotatedRectangle";
            double width = roi.at("width"), height = roi.at("height"),
                   angle = rotated ? roi.at("angleDegrees").get<double>() : 0;
            int projectionWidth = std::max(1, int(std::round(width)));
            if (projectionWidth % 2 == 0)
                ++projectionWidth;
            require(projectionWidth <= 631 &&
                        (2 * std::ceil(height / 2) + 1) * projectionWidth <= 16ULL * 1024 * 1024,
                    JPV_RESOURCE_LIMIT, "Edge-pair projection exceeds its sampling budget.");
            cv::Point2d center = rotated ? cv::Point2d(roi.at("cx"), roi.at("cy"))
                                         : cv::Point2d(roi.at("x").get<double>() + width / 2,
                                                       roi.at("y").get<double>() + height / 2);
            cv::Point2d normal(-std::sin(angle * CV_PI / 180), std::cos(angle * CV_PI / 180));
            if (p.at("reverseSearch").get<bool>())
                normal = -normal;
            Json sampling = p;
            sampling["searchLength"] = height;
            sampling["projectionWidth"] = projectionWidth;
            auto projection = edge_projection(image, gray_pixels(image), center, normal, sampling);
            auto a = projection_edges(projection, sampling, p.at("firstPolarity")),
                 b = projection_edges(projection, sampling, p.at("secondPolarity"));
            bool truncated = a.size() > 128 || b.size() > 128;
            if (a.size() > 128)
                a.resize(128);
            if (b.size() > 128)
                b.resize(128);
            Json profile = Json::array();
            for (size_t i = 0; i < projection.values.size(); ++i)
                profile.push_back(
                    {{"offset", int(i) - projection.half},
                     {"gray", std::isfinite(projection.values[i]) ? Json(projection.values[i]) : Json()},
                     {"gradient",
                      std::isfinite(projection.gradients[i]) ? Json(projection.gradients[i]) : Json()}});
            result["diagnostics"] = {{"projection", profile},
                                     {"firstCandidateCount", a.size()},
                                     {"secondCandidateCount", b.size()},
                                     {"candidateTruncated", truncated},
                                     {"window",
                                      {{"x", center.x + image.x},
                                       {"y", center.y + image.y},
                                       {"normalX", normal.x},
                                       {"normalY", normal.y},
                                       {"width", projectionWidth},
                                       {"height", 2 * std::ceil(height / 2)}}}};
            result["dataAvailable"] = std::any_of(projection.gradients.begin(), projection.gradients.end(),
                                                  [](double value) { return std::isfinite(value); });
            if (!result.at("dataAvailable").get<bool>()) {
                result["decision"]["reason"] = "UnavailableSamplingArea";
                return result;
            }
            double best = INFINITY;
            std::optional<std::pair<ProjectionEdge, ProjectionEdge>> chosen;
            for (auto first : a)
                for (auto second : b) {
                    double distance = second.offset - first.offset;
                    if (distance <= 1e-6 || distance < p.at("minimumWidth").get<double>() ||
                        distance > p.at("maximumWidth").get<double>())
                        continue;
                    double score = p.at("pairSelection") == "strongest"
                                       ? -std::min(first.contrast, second.contrast)
                                       : std::abs(distance - p.at("expectedWidth").get<double>());
                    if (score < best) {
                        best = score;
                        chosen = {first, second};
                    }
                }
            if (!chosen) {
                result["decision"]["reason"] = "NoEdgePair";
                return result;
            }
            auto first = center + normal * chosen->first.offset + cv::Point2d(image.x, image.y),
                 second = center + normal * chosen->second.offset + cv::Point2d(image.x, image.y);
            finish_measurement(result, p, chosen->second.offset - chosen->first.offset);
            result["first"] = {{"kind", "point"}, {"point", {{"x", first.x}, {"y", first.y}}}};
            result["second"] = {{"kind", "point"}, {"point", {{"x", second.x}, {"y", second.y}}}};
            result["contrast"] = std::min(chosen->first.contrast, chosen->second.contrast);
            result["center"] = {{"x", (first.x + second.x) / 2}, {"y", (first.y + second.y) / 2}};
            result["overlay"].push_back({{"type", "line"},
                                         {"points", Json::array({{first.x, first.y}, {second.x, second.y}})},
                                         {"color", DefaultResultColor},
                                         {"lineWidth", 2}});
            for (auto point : {first, second})
                result["overlay"].push_back({{"type", "circle"},
                                             {"x", point.x},
                                             {"y", point.y},
                                             {"radius", 2},
                                             {"color", DefaultResultColor},
                                             {"lineWidth", 2}});
            return result;
        },
        [](const Json &p) {
            validate_tolerance(p);
            auto shape = p.at("roi").at("shape").get<std::string>();
            require(shape == "rectangle" || shape == "rotatedRectangle", JPV_INVALID_RECIPE,
                    "Edge pair requires a rectangle or rotated rectangle ROI.");
            require(p.at("roi").at("height").get<double>() >=
                        std::max(8.0, 6 * p.at("sigma").get<double>() + 6),
                    JPV_INVALID_RECIPE, "Edge-pair ROI height is too short for smoothing.");
            require(p.at("roi").at("width").get<double>() <= 63 &&
                        p.at("minimumWidth").get<double>() <= p.at("maximumWidth").get<double>(),
                    JPV_INVALID_RECIPE, "Edge-pair projection width or distance range is invalid.");
        });
    pair.descriptor["roiReferenceOnly"] = true;
    for (auto p : tolerance_parameters())
        pair.descriptor["parameters"].push_back(std::move(p));
    add_measurement_outputs(pair);
    pair.descriptor["outputs"].push_back(
        {{"id", "image"}, {"type", "Image"}, {"label", "关联输入图像"}, {"hidden", true}});
    auto expand = std::move(pair.expand);
    pair.expandInputs = [expand = std::move(expand)](Value &value, const Json &p,
                                                     const InputValues &inputs) -> PortValues {
        auto image = std::get<Image>(*inputs.at("input"));
        if (p.contains("_unavailable")) {
            image.available = false;
            image.unavailableReason = "UnavailableParameter";
        }
        std::get<Json>(value)["image"] = image_metadata(image);
        auto ports = expand(value, p);
        ports.emplace("image", image);
        return ports;
    };
    registry.add(std::move(pair));
    registry.add(component(
        "LineCaliper", "直线卡尺", "测量", "Image", "Features", "features", line_caliper_parameters(),
        [](const Value *input, const Json &p, const Image &) -> Value {
            return find_line_caliper(std::get<Image>(*input), p);
        },
        validate_line_caliper));
    registry.add(component(
        "CircleCaliper", "圆卡尺", "测量", "Image", "Features", "features", circle_caliper_parameters(),
        [](const Value *input, const Json &p, const Image &) -> Value {
            return find_circle_caliper(std::get<Image>(*input), p);
        },
        validate_circle_caliper));
}
} // namespace jp

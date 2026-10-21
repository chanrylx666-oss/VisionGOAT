#include "roi.h"
#include <algorithm>
#include <cmath>
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>

namespace jp {
static double coordinate(const Json &roi, const char *key, double minimum = 0, double maximum = 32768) {
    if ((std::string(key) == "x" || std::string(key) == "y" || std::string(key) == "cx" ||
         std::string(key) == "cy") &&
        roi.value("coordinateSpace", "input") == "reference") {
        minimum = -65536;
        maximum = 65536;
    }
    require(roi.contains(key) && roi.at(key).is_number(), JPV_INVALID_RECIPE,
            std::string("ROI lacks ") + key);
    double value = roi.at(key).get<double>();
    require(std::isfinite(value) && value >= minimum && value <= maximum, JPV_INVALID_RECIPE,
            std::string("ROI coordinate out of range: ") + key);
    return value;
}
static std::vector<cv::Point2d> polygon_points(const Json &roi) {
    require(roi.contains("points") && roi.at("points").is_array() && roi.at("points").size() >= 3 &&
                roi.at("points").size() <= 64,
            JPV_INVALID_RECIPE, "Polygon ROI requires 3 to 64 vertices.");
    std::vector<cv::Point2d> points;
    for (const auto &point : roi.at("points")) {
        require(point.is_array() && point.size() == 2 && point[0].is_number() && point[1].is_number(),
                JPV_INVALID_RECIPE, "Polygon vertex must contain x/y.");
        double x = point[0].get<double>(), y = point[1].get<double>();
        double lo = roi.value("coordinateSpace", "input") == "reference" ? -65536 : 0,
               hi = roi.value("coordinateSpace", "input") == "reference" ? 65536 : 32768;
        require(std::isfinite(x) && std::isfinite(y) && x >= lo && y >= lo && x <= hi && y <= hi,
                JPV_INVALID_RECIPE, "Polygon vertex out of range.");
        points.emplace_back(x, y);
    }
    return points;
}
static double cross(cv::Point2d a, cv::Point2d b, cv::Point2d c) {
    return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}
std::vector<cv::Point2d> roi_vertices(const Json &roi) {
    auto shape = roi.at("shape").get<std::string>();
    std::vector<cv::Point2d> points;
    if (shape == "polygon") {
        for (const auto &p : roi.at("points"))
            points.emplace_back(p[0], p[1]);
    } else if (shape == "rotatedRectangle") {
        double cx = roi.at("cx"), cy = roi.at("cy"), w = roi.at("width"), h = roi.at("height"),
               angle = roi.at("angleDegrees").get<double>() * CV_PI / 180;
        for (auto p : {cv::Point2d(-w / 2, -h / 2), cv::Point2d(w / 2, -h / 2), cv::Point2d(w / 2, h / 2),
                       cv::Point2d(-w / 2, h / 2)})
            points.emplace_back(cx + std::cos(angle) * p.x - std::sin(angle) * p.y,
                                cy + std::sin(angle) * p.x + std::cos(angle) * p.y);
    } else if (shape == "rectangle") {
        double x = roi.at("x"), y = roi.at("y"), w = roi.at("width"), h = roi.at("height");
        points = {{x, y}, {x + w, y}, {x + w, y + h}, {x, y + h}};
    }
    return points;
}
static bool annulus_contains(const Json &roi, double x, double y) {
    double dx = x - roi.at("cx").get<double>(), dy = y - roi.at("cy").get<double>(), r = std::hypot(dx, dy),
           span = roi.at("endAngle").get<double>() - roi.at("startAngle").get<double>();
    if (r < roi.at("innerRadius").get<double>() || r > roi.at("outerRadius").get<double>())
        return false;
    if (span >= 359.999)
        return true;
    double relative =
        std::fmod(std::atan2(dy, dx) * 180 / CV_PI - roi.at("startAngle").get<double>() + 1080, 360.0);
    return relative <= span;
}
static bool intersects(cv::Point2d a, cv::Point2d b, cv::Point2d c, cv::Point2d d) {
    if (std::max(a.x, b.x) < std::min(c.x, d.x) || std::max(c.x, d.x) < std::min(a.x, b.x) ||
        std::max(a.y, b.y) < std::min(c.y, d.y) || std::max(c.y, d.y) < std::min(a.y, b.y))
        return false;
    return cross(a, b, c) * cross(a, b, d) <= 0 && cross(c, d, a) * cross(c, d, b) <= 0;
}
void validate_roi(const Json &roi) {
    require(roi.is_object() && roi.contains("shape") && roi.at("shape").is_string(), JPV_INVALID_RECIPE,
            "ROI must declare its shape.");
    auto shape = roi.at("shape").get<std::string>();
    std::vector<std::string> fields{"shape"};
    if (roi.contains("coordinateSpace")) {
        require(roi.at("coordinateSpace") == "input" || roi.at("coordinateSpace") == "reference",
                JPV_INVALID_RECIPE, "Invalid ROI coordinate space.");
        fields.push_back("coordinateSpace");
    }
    if (roi.contains("referenceHash")) {
        require(roi.value("coordinateSpace", "input") == "reference" && roi.at("referenceHash").is_string(),
                JPV_INVALID_RECIPE, "ROI reference hash requires reference coordinates.");
        auto hash = roi.at("referenceHash").get<std::string>();
        require(hash.size() == 64 && hash.find_first_not_of("0123456789abcdef") == std::string::npos,
                JPV_INVALID_RECIPE, "Invalid ROI reference hash.");
        fields.push_back("referenceHash");
    }
    if (shape == "rectangle") {
        coordinate(roi, "x");
        coordinate(roi, "y");
        coordinate(roi, "width", 1);
        coordinate(roi, "height", 1);
        for (const auto *name : {"x", "y", "width", "height"})
            require(roi.at(name).is_number_integer(), JPV_INVALID_RECIPE,
                    "Rectangle ROI uses integer pixels.");
        fields.insert(fields.end(), {"x", "y", "width", "height"});
    } else if (shape == "rotatedRectangle") {
        coordinate(roi, "cx");
        coordinate(roi, "cy");
        coordinate(roi, "width", 1);
        coordinate(roi, "height", 1);
        coordinate(roi, "angleDegrees", -180, 180);
        fields.insert(fields.end(), {"cx", "cy", "width", "height", "angleDegrees"});
    } else if (shape == "annulus") {
        coordinate(roi, "cx");
        coordinate(roi, "cy");
        coordinate(roi, "innerRadius", 0, 16384);
        coordinate(roi, "outerRadius", 1, 16384);
        coordinate(roi, "startAngle", -360, 360);
        coordinate(roi, "endAngle", -360, 720);
        double span = roi.at("endAngle").get<double>() - roi.at("startAngle").get<double>();
        require(roi.at("outerRadius").get<double>() > roi.at("innerRadius").get<double>() && span >= 10 &&
                    span <= 360,
                JPV_INVALID_RECIPE, "Annulus requires increasing radii and an arc of 10 to 360 degrees.");
        fields.insert(fields.end(), {"cx", "cy", "innerRadius", "outerRadius", "startAngle", "endAngle"});
    } else if (shape == "circle") {
        coordinate(roi, "cx");
        coordinate(roi, "cy");
        coordinate(roi, "radius", 1, 16384);
        fields.insert(fields.end(), {"cx", "cy", "radius"});
    } else if (shape == "polygon") {
        auto points = polygon_points(roi);
        double area = 0;
        for (size_t i = 0; i < points.size(); ++i) {
            auto a = points[i], b = points[(i + 1) % points.size()];
            require(cv::norm(a - b) >= 1, JPV_INVALID_RECIPE,
                    "Polygon has duplicate/short adjacent vertices.");
            area += a.x * b.y - a.y * b.x;
            for (size_t j = i + 1; j < points.size(); ++j) {
                if (j == i + 1 || (i == 0 && j == points.size() - 1))
                    continue;
                require(!intersects(a, b, points[j], points[(j + 1) % points.size()]), JPV_INVALID_RECIPE,
                        "Polygon ROI cannot intersect itself.");
            }
        }
        require(std::abs(area) >= 2, JPV_INVALID_RECIPE, "Polygon ROI has no usable area.");
        fields.push_back("points");
    } else
        require(shape == "none", JPV_INVALID_RECIPE, "Unsupported ROI shape.");
    for (auto it = roi.begin(); it != roi.end(); ++it)
        require(std::find(fields.begin(), fields.end(), it.key()) != fields.end(), JPV_INVALID_RECIPE,
                "Unknown ROI field: " + it.key());
}
cv::Rect checked_roi_bounds(const Image &source, const Json &roi) {
    validate_roi(roi);
    auto shape = roi.at("shape").get<std::string>();
    if (shape == "none")
        return {0, 0, source.pixels.cols, source.pixels.rows};
    cv::Rect bounds;
    std::vector<cv::Point2d> points;
    if (shape == "rectangle")
        bounds = {roi.at("x"), roi.at("y"), roi.at("width"), roi.at("height")};
    else {
        double left, top, right, bottom;
        if (shape == "circle" || shape == "annulus") {
            double cx = roi.at("cx"), cy = roi.at("cy"),
                   r = shape == "circle" ? roi.at("radius").get<double>()
                                         : roi.at("outerRadius").get<double>();
            left = cx - r;
            right = cx + r;
            top = cy - r;
            bottom = cy + r;
        } else {
            points = roi_vertices(roi);
            left = right = points[0].x;
            top = bottom = points[0].y;
            for (auto p : points) {
                left = std::min(left, p.x);
                right = std::max(right, p.x);
                top = std::min(top, p.y);
                bottom = std::max(bottom, p.y);
            }
        }
        int x = int(std::floor(left)), y = int(std::floor(top));
        bounds = {x, y, int(std::ceil(right)) + 1 - x, int(std::ceil(bottom)) + 1 - y};
    }
    // A taught ROI may touch or cross an acquired image edge (especially a rotated strip
    // on an unwrapped image). Never allocate its off-image canvas or silently use the full
    // image: only the acquired intersection can contribute valid pixels.
    return bounds & cv::Rect(0, 0, source.pixels.cols, source.pixels.rows);
}
Image apply_roi(const Image &source, const Json &roi) {
    auto bounds = checked_roi_bounds(source, roi);
    auto shape = roi.at("shape").get<std::string>();
    if (shape == "none")
        return source;
    if (bounds.empty()) {
        auto unavailable = source;
        unavailable.available = false;
        unavailable.unavailableReason = "UnavailableRoi";
        return unavailable;
    }
    Image selected = source;
    selected.pixels = source.pixels(bounds);
    selected.x += bounds.x;
    selected.y += bounds.y;
    selected.valid.release();
    if (shape != "rectangle") {
        selected.valid = cv::Mat::zeros(bounds.size(), CV_8UC1);
        if (shape == "circle" || shape == "annulus") {
            double cx = roi.at("cx"), cy = roi.at("cy"),
                   r = shape == "circle" ? roi.at("radius").get<double>()
                                         : roi.at("outerRadius").get<double>();
            for (int y = 0; y < bounds.height; ++y)
                for (int x = 0; x < bounds.width; ++x)
                    if (shape == "annulus" ? annulus_contains(roi, x + bounds.x, y + bounds.y)
                                           : std::hypot(x + bounds.x - cx, y + bounds.y - cy) <= r)
                        selected.valid.at<uint8_t>(y, x) = 255;
        } else {
            std::vector<cv::Point> fixed;
            for (auto p : roi_vertices(roi))
                fixed.emplace_back(cvRound((p.x - bounds.x) * 256), cvRound((p.y - bounds.y) * 256));
            cv::fillPoly(selected.valid, std::vector<std::vector<cv::Point>>{fixed}, 255, cv::LINE_8, 8);
        }
    }
    if (!source.valid.empty()) {
        if (selected.valid.empty())
            selected.valid = source.valid(bounds);
        else
            cv::bitwise_and(selected.valid, source.valid(bounds), selected.valid);
    }
    if (!selected.valid.empty() && cv::countNonZero(selected.valid) == 0) {
        selected.available = false;
        selected.unavailableReason = "UnavailableRoi";
    }
    return selected;
}
Image apply_positioned_circle_roi(const Image &source, const Json &roi) {
    double cx = roi.at("cx"), cy = roi.at("cy"), radius = roi.at("radius");
    int left = std::clamp(int(std::floor(cx - radius)), 0, source.pixels.cols),
        top = std::clamp(int(std::floor(cy - radius)), 0, source.pixels.rows),
        right = std::clamp(int(std::ceil(cx + radius)) + 1, 0, source.pixels.cols),
        bottom = std::clamp(int(std::ceil(cy + radius)) + 1, 0, source.pixels.rows);
    if (right <= left || bottom <= top) {
        auto unavailable = source;
        unavailable.available = false;
        return unavailable;
    }
    cv::Rect bounds(left, top, right - left, bottom - top);
    Image selected{source.pixels(bounds), source.x + left, source.y + top};
    selected.matchPosition = source.matchPosition;
    selected.fixture = source.fixture;
    selected.valid = cv::Mat::zeros(bounds.size(), CV_8UC1);
    for (int y = 0; y < bounds.height; ++y)
        for (int x = 0; x < bounds.width; ++x)
            if (std::hypot(x + left - cx, y + top - cy) <= radius)
                selected.valid.at<uint8_t>(y, x) = 255;
    if (!source.valid.empty())
        cv::bitwise_and(selected.valid, source.valid(bounds), selected.valid);
    return selected;
}
Image apply_effective_roi(const Image &source, const Json &roi) {
    auto shape = roi.at("shape").get<std::string>();
    if (shape == "none")
        return source;
    if (shape == "circle")
        return apply_positioned_circle_roi(source, roi);
    if (shape == "annulus") {
        Json circle = {{"shape", "circle"},
                       {"cx", roi.at("cx")},
                       {"cy", roi.at("cy")},
                       {"radius", roi.at("outerRadius")}};
        auto selected = apply_positioned_circle_roi(source, circle);
        if (!selected.available)
            return selected;
        for (int row = 0; row < selected.pixels.rows; ++row)
            for (int col = 0; col < selected.pixels.cols; ++col)
                if (!annulus_contains(roi, col + selected.x - source.x, row + selected.y - source.y))
                    selected.valid.at<uint8_t>(row, col) = 0;
        return selected;
    }
    require(shape == "polygon" || shape == "rotatedRectangle", JPV_INTERNAL_ERROR,
            "Unexpected transformed ROI shape.");
    std::vector<cv::Point2f> vertices;
    double left = INFINITY, top = INFINITY, right = -INFINITY, bottom = -INFINITY;
    for (const auto &point : roi_vertices(roi)) {
        double x = point.x, y = point.y;
        require(std::isfinite(x) && std::isfinite(y) && std::abs(x) < 10000000 && std::abs(y) < 10000000,
                JPV_RESOURCE_LIMIT, "Transformed ROI exceeds coordinate limits.");
        vertices.emplace_back(float(x), float(y));
        left = std::min(left, x);
        top = std::min(top, y);
        right = std::max(right, x);
        bottom = std::max(bottom, y);
    }
    int x = std::clamp(int(std::floor(left)), 0, source.pixels.cols),
        y = std::clamp(int(std::floor(top)), 0, source.pixels.rows),
        r = std::clamp(int(std::ceil(right)) + 1, 0, source.pixels.cols),
        b = std::clamp(int(std::ceil(bottom)) + 1, 0, source.pixels.rows);
    if (r <= x || b <= y) {
        auto empty = source;
        empty.available = false;
        return empty;
    }
    cv::Rect bounds(x, y, r - x, b - y);
    Image selected = source;
    selected.pixels = source.pixels(bounds);
    selected.x += x;
    selected.y += y;
    selected.valid = cv::Mat::zeros(bounds.size(), CV_8UC1);
    for (int row = 0; row < bounds.height; ++row)
        for (int col = 0; col < bounds.width; ++col)
            if (cv::pointPolygonTest(vertices, cv::Point2f(float(col + x), float(row + y)), false) >= 0)
                selected.valid.at<uint8_t>(row, col) = 255;
    if (!source.valid.empty())
        cv::bitwise_and(selected.valid, source.valid(bounds), selected.valid);
    return selected;
}
bool valid_point(const Image &source, double x, double y) {
    int ix = cvRound(x), iy = cvRound(y);
    return ix >= 0 && iy >= 0 && ix < source.pixels.cols && iy < source.pixels.rows &&
           (source.valid.empty() || source.valid.at<uint8_t>(iy, ix) != 0);
}
cv::Mat visible_pixels(const Image &source) {
    if (source.valid.empty())
        return source.pixels;
    cv::Mat pixels = source.pixels.clone();
    pixels.setTo(cv::Scalar::all(0), source.valid == 0);
    return pixels;
}
} // namespace jp

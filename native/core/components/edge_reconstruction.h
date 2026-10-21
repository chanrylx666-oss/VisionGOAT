#pragma once
#include "component.h"
#include "roi.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <optional>
#include <vector>

namespace jp::edge_reconstruction {
struct Sample {
    int station;
    double path, offset;
    Json point;
};

inline double median(double *values, size_t count) {
    auto middle = values + count / 2;
    std::nth_element(values, middle, values + count);
    double result = *middle;
    if (count % 2 == 0)
        result = (*std::max_element(values, middle) + result) / 2;
    return result;
}

// A median of detrended offsets rejects spikes without mistaking a sloping edge for a jump.
// Neighborhoods are measured in stations, not in the number of surviving points across a gap.
inline std::optional<double> local_trend(size_t target, int radius,
                                         const std::vector<Sample> &samples,
                                         const std::vector<int> &stationToSample,
                                         const std::vector<bool> &keep) {
    const auto &sample = samples[target];
    std::array<size_t, 101> neighbors;
    size_t neighborCount = 0;
    bool left = false, right = false;
    for (int station = std::max(0, sample.station - radius);
         station <= std::min(int(stationToSample.size()) - 1, sample.station + radius); ++station) {
        int index = stationToSample[station];
        if (index < 0 || !keep[index] || size_t(index) == target)
            continue;
        neighbors[neighborCount++] = size_t(index);
        left = left || station < sample.station;
        right = right || station > sample.station;
    }
    if (neighborCount < 2 || !left || !right)
        return std::nullopt;
    std::array<double, 101> slopes, intercepts;
    for (size_t index = 1; index < neighborCount; ++index) {
        const auto &first = samples[neighbors[index - 1]], &second = samples[neighbors[index]];
        slopes[index - 1] = (second.offset - first.offset) / (second.path - first.path);
    }
    double slope = median(slopes.data(), neighborCount - 1);
    for (size_t index = 0; index < neighborCount; ++index) {
        const auto &neighbor = samples[neighbors[index]];
        intercepts[index] = neighbor.offset - slope * (neighbor.path - sample.path);
    }
    return median(intercepts.data(), neighborCount);
}

// Unlike a median, a local mean fit attenuates alternating jitter instead of reversing its phase.
// Centered path coordinates preserve linear trends; absent support on either side leaves endpoints raw.
inline std::optional<double> smooth_offset(size_t target, int radius, double tolerance,
                                          const std::vector<Sample> &samples,
                                          const std::vector<int> &stationToSample,
                                          const std::vector<bool> &keep) {
    const auto &sample = samples[target];
    std::array<size_t, 101> neighbors;
    size_t count = 0;
    bool left = false, right = false;
    for (int station = std::max(0, sample.station - radius);
         station <= std::min(int(stationToSample.size()) - 1, sample.station + radius); ++station) {
        int index = stationToSample[station];
        if (index < 0 || !keep[index])
            continue;
        neighbors[count++] = size_t(index);
        left = left || station < sample.station;
        right = right || station > sample.station;
    }
    if (count < 3 || !left || !right)
        return std::nullopt;
    double scale = std::max(sample.path - samples[neighbors[0]].path,
                            samples[neighbors[count - 1]].path - sample.path);
    std::array<double, 101> u, y, kernel;
    double minimum = samples[neighbors[0]].offset, maximum = minimum;
    for (size_t index = 0; index < count; ++index) {
        const auto &neighbor = samples[neighbors[index]];
        u[index] = (neighbor.path - sample.path) / scale;
        y[index] = neighbor.offset;
        kernel[index] = radius + 1 - std::abs(neighbor.station - sample.station);
        minimum = std::min(minimum, y[index]); maximum = std::max(maximum, y[index]);
    }
    double intercept = sample.offset, slope = 0;
    // The first weighted least-squares fit is followed by two bounded Huber reweighting steps.
    for (int iteration = 0; iteration < 3; ++iteration) {
        double s0 = 0, s1 = 0, s2 = 0, sy = 0, suy = 0;
        for (size_t index = 0; index < count; ++index) {
            double weight = kernel[index];
            if (iteration > 0) {
                double residual = std::abs(y[index] - intercept - slope * u[index]);
                if (residual > tolerance) weight *= tolerance / residual;
            }
            s0 += weight; s1 += weight * u[index]; s2 += weight * u[index] * u[index];
            sy += weight * y[index]; suy += weight * u[index] * y[index];
        }
        double determinant = s0 * s2 - s1 * s1;
        if (!(determinant > 1e-12 * s0 * s2))
            return std::nullopt;
        intercept = (sy * s2 - suy * s1) / determinant;
        slope = (suy * s0 - sy * s1) / determinant;
        if (!std::isfinite(intercept) || !std::isfinite(slope))
            return std::nullopt;
    }
    return std::clamp(intercept, minimum, maximum);
}

inline std::vector<double> pchip_slopes(const std::vector<Sample> &anchors) {
    std::vector<double> slopes(anchors.size(), 0);
    if (anchors.size() < 2)
        return slopes;
    std::vector<double> h(anchors.size() - 1), secants(h.size());
    for (size_t index = 0; index < h.size(); ++index) {
        h[index] = anchors[index + 1].path - anchors[index].path;
        secants[index] = (anchors[index + 1].offset - anchors[index].offset) / h[index];
    }
    if (anchors.size() == 2) {
        slopes[0] = slopes[1] = secants[0];
        return slopes;
    }
    auto endpoint = [](double firstH, double secondH, double firstD, double secondD) {
        double slope = ((2 * firstH + secondH) * firstD - firstH * secondD) / (firstH + secondH);
        if (slope * firstD <= 0)
            return 0.0;
        if (firstD * secondD <= 0 && std::abs(slope) > 3 * std::abs(firstD))
            return 3 * firstD;
        return slope;
    };
    slopes.front() = endpoint(h[0], h[1], secants[0], secants[1]);
    slopes.back() = endpoint(h.back(), h[h.size() - 2], secants.back(), secants[secants.size() - 2]);
    for (size_t index = 1; index + 1 < anchors.size(); ++index) {
        double previous = secants[index - 1], next = secants[index];
        if (previous * next <= 0)
            continue;
        double w1 = 2 * h[index] + h[index - 1], w2 = h[index] + 2 * h[index - 1];
        slopes[index] = (w1 + w2) / (w1 / previous + w2 / next);
    }
    return slopes;
}

inline void process(Json &packet, const Image &source, const Json &parameters, cv::Point2d center,
                    cv::Point2d tangent, cv::Point2d normal, int stations,
                    const std::vector<bool> &samplingAvailable) {
    const std::string mode = parameters.at("curveMode");
    auto &points = packet["points"];
    packet["rawCount"] = points.size();
    packet["curveMode"] = mode;
    packet["measuredCount"] = points.size();
    packet["rejectedCount"] = 0;
    packet["interpolatedCount"] = 0;
    packet["observedCoverage"] = double(points.size()) / stations;
    // Raw points already are their own original data. Duplicating them and their coordinates
    // across every published alias would regress the existing 32769-station result budget.
    if (mode == "raw") {
        packet.erase("rawPoints");
        return;
    }
    for (auto &point : points) {
        point["origin"] = "measured";
        point["rawX"] = point.at("x");
        point["rawY"] = point.at("y");
        point["adjusted"] = false;
    }
    packet["rawPoints"] = points;
    if (points.empty())
        return;

    const double spacing = parameters.at("sampleSpacing"), width = parameters.at("roi").at("width"),
                 height = parameters.at("roi").at("height"), tolerance = parameters.at("outlierTolerance");
    std::vector<Sample> samples;
    std::vector<int> stationToSample(stations, -1);
    samples.reserve(points.size());
    for (const auto &point : points) {
        cv::Point2d local(point.at("x").get<double>() - source.x, point.at("y").get<double>() - source.y);
        int station = point.at("stationIndex");
        stationToSample[station] = int(samples.size());
        samples.push_back({station, point.at("pathPosition"), (local - center).dot(normal), point});
    }
    std::vector<bool> keep(samples.size(), true);
    int radius = parameters.at("filterWindow").get<int>() / 2;
    for (int pass = 0; pass < 2; ++pass) {
        auto next = keep;
        for (size_t index = 0; index < samples.size(); ++index) {
            if (!keep[index])
                continue;
            auto trend = local_trend(index, radius, samples, stationToSample, keep);
            if (trend && std::abs(samples[index].offset - *trend) > tolerance)
                next[index] = false;
        }
        keep = std::move(next);
    }

    auto position = [&](int station, double offset) {
        return center + tangent * (station * spacing - width / 2) + normal * offset;
    };
    auto permitted = [&](int station, double offset) {
        if (!std::isfinite(offset) || std::abs(offset) > height / 2 || !samplingAvailable[station])
            return false;
        auto local = position(station, offset);
        return local.x >= 0 && local.y >= 0 && local.x < source.pixels.cols &&
               local.y < source.pixels.rows && valid_point(source, local.x, local.y);
    };
    std::vector<Sample> anchors;
    anchors.reserve(samples.size());
    const bool smooth = parameters.at("smoothCurve");
    for (size_t index = 0; index < samples.size(); ++index) {
        if (!keep[index])
            continue;
        auto anchor = samples[index];
        if (smooth) {
            auto trend = smooth_offset(index, radius, tolerance, samples, stationToSample, keep);
            if (trend && permitted(anchor.station, *trend)) {
                auto local = position(anchor.station, *trend);
                anchor.offset = *trend;
                anchor.point["x"] = local.x + source.x;
                anchor.point["y"] = local.y + source.y;
                anchor.point["adjusted"] =
                    std::abs(local.x + source.x - anchor.point.at("rawX").get<double>()) > 1e-9 ||
                    std::abs(local.y + source.y - anchor.point.at("rawY").get<double>()) > 1e-9;
            }
        }
        anchors.push_back(std::move(anchor));
    }
    packet["measuredCount"] = anchors.size();
    packet["rejectedCount"] = samples.size() - anchors.size();
    packet["observedCoverage"] = double(anchors.size()) / stations;
    points = Json::array();
    auto append = [&](Json point) {
        point["index"] = points.size();
        points.push_back(std::move(point));
    };
    const double maximumGap = parameters.at("maximumGapLength");
    auto slopes = mode == "reconstruct" && maximumGap > 0 ? pchip_slopes(anchors) : std::vector<double>{};
    for (size_t index = 0; index < anchors.size(); ++index) {
        append(anchors[index].point);
        if (mode != "reconstruct" || index + 1 == anchors.size())
            continue;
        const auto &left = anchors[index], &right = anchors[index + 1];
        double span = right.path - left.path;
        if (right.station <= left.station + 1 || span > maximumGap)
            continue;
        for (int station = left.station + 1; station < right.station; ++station) {
            double path = station * spacing, t = (path - left.path) / span, t2 = t * t, t3 = t2 * t;
            double offset = (2 * t3 - 3 * t2 + 1) * left.offset + (t3 - 2 * t2 + t) * span * slopes[index] +
                            (-2 * t3 + 3 * t2) * right.offset + (t3 - t2) * span * slopes[index + 1];
            offset = std::clamp(offset, std::min(left.offset, right.offset), std::max(left.offset, right.offset));
            if (!permitted(station, offset))
                continue;
            auto local = position(station, offset);
            append({{"stationIndex", station}, {"pathPosition", path}, {"x", local.x + source.x},
                    {"y", local.y + source.y}, {"gradient", 0.0}, {"origin", "interpolated"},
                    {"adjusted", false}});
            packet["interpolatedCount"] = packet.at("interpolatedCount").get<int>() + 1;
        }
    }
}
} // namespace jp::edge_reconstruction

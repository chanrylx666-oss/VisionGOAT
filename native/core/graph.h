#pragma once
#include "common.h"
#include "license.h"
#include <atomic>
#include <array>
#include <chrono>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <opencv2/core.hpp>
#include <variant>

namespace jp {
inline constexpr char DefaultResultColor[] = "#00FF00";
Json node_catalog();
Json seal_recipe(Json recipe);
struct MatchPosition {
    double centerX, centerY, angleDegrees, scale;
};
struct PoseValue {
    bool available = false;
    MatchPosition position{};
    std::string modelHash;
    std::string reason;
    std::string coordinateSpace = "original", coordinatePlane;
};
struct FixtureValue {
    MatchPosition reference{}, current{};
    std::string modelHash;
};
struct ImagePipelineContext;
struct Image {
    cv::Mat pixels;
    int x = 0, y = 0;
    cv::Mat valid;         // Empty means the whole image; nonzero pixels belong to the effective ROI.
    bool available = true; // False when a selected upstream target has no associated image.
    bool padded = false;   // The image uses or inherits an explicitly padded canvas.
    std::optional<MatchPosition> matchPosition; // Selected template center in original image coordinates.
    std::optional<FixtureValue> fixture;
    std::string unavailableReason;
    bool binaryMask = false;
    std::shared_ptr<const ImagePipelineContext> pipelineContext;
    // Nonlinear image transforms start a separate pixel plane. Origins remain offsets in that plane.
    std::string coordinateSpace = "original", coordinatePlane;
    Json coordinateMapping = Json::object();
};
inline void copy_image_coordinates(Image &target, const Image &source) {
    target.coordinateSpace = source.coordinateSpace;
    target.coordinatePlane = source.coordinatePlane;
    target.coordinateMapping = source.coordinateMapping;
}
struct Contours {
    std::vector<std::vector<cv::Point>> items;
    int x = 0, y = 0;
    bool dataAvailable = true;
    std::shared_ptr<Image> sourceImage;
};
// Feature coordinates are always expressed in the original input image.
struct CircleFeature {
    double x, y, radius;
    std::vector<cv::Point2d> edgePoints;
    double rmsError = 0;
    double outputAnnulusWidth = 0; // Configured output band, not a second measured material edge.
};
struct LineFeature {
    double x1, y1, x2, y2, length, angleDegrees;
    std::vector<cv::Point2d> edgePoints;
    double rmsError = 0;
};
struct RegionFeature {
    double area, perimeter, centroidX, centroidY, circularity;
    int x, y, width, height;
    bool centroidValid;
    // Optional Blob statistics; legacy contour aggregates retain their existing JSON shape.
    std::string areaKind{};
    std::optional<double> aspectRatio{}, extent{};
    std::optional<bool> touchesBoundary{};
};
struct MatchFeature {
    double x, y, width, height, centerX, centerY, angleDegrees, scale, score;
    std::array<cv::Point2d, 4> corners;
    std::string templateId{}, templateName{};
};
using Feature = std::variant<CircleFeature, LineFeature, RegionFeature, MatchFeature>;
struct Features {
    std::vector<Feature> items;
    Json diagnostics = Json::object();
    Json detection = Json::object();    // Hough working resolution; distinct from caliper diagnostics.
    Json position = Json::object();     // Reference used by a positioned detector in this execution.
    std::shared_ptr<Image> sourceImage; // Actual input before this detector's own search ROI.
    Json fixture = Json::object();
    bool dataAvailable = true;
    std::string coordinateSpace = "original", coordinatePlane;
};
Json feature_values(const Features &features);
Json feature_overlays(const Features &features, const std::string &color = DefaultResultColor,
                      double lineWidth = 2);
using Value = std::variant<Image, Contours, Features, int64_t, double, bool, Json, PoseValue>;
struct Engine {
    std::shared_ptr<LicenseService> license;
    explicit Engine(const Json &options);
};
struct Plan {
    Json recipe;
    std::map<std::string, Json> nodes;
    std::vector<std::string> order;
    std::map<std::string, std::map<std::string, std::pair<std::string, std::string>>> incoming;
    std::map<std::string, std::map<std::string, std::pair<std::string, std::string>>> parameterIncoming;
    std::map<std::string, int> uses;
};
Plan compile_recipe(Json recipe, const fs::path &root = {});
struct Recipe {
    std::shared_ptr<Engine> engine;
    Plan plan;
};
struct Run;
struct Session {
    std::shared_ptr<Recipe> recipe;
    std::mutex mutex;
    bool closed = false;
    std::weak_ptr<Run> active;
    explicit Session(std::shared_ptr<Recipe> value) : recipe(std::move(value)) {}
    void close();
};
struct Result {
    Json json;
    std::map<std::string, Image> images;
    std::map<std::string, Image> inputImages;
    std::map<std::string, Image> outputImages;
    std::shared_ptr<LicenseService> license;
};
struct Run {
    std::shared_ptr<Session> session;
    std::shared_ptr<Image> frame;
    Json request, parameters, variables;
    std::atomic<bool> cancel{false}, started{false}, finished{false};
    bool counted = false;
    std::chrono::steady_clock::time_point accepted;
    ~Run();
    void finish();
    std::shared_ptr<Result> execute();
};
std::shared_ptr<Run> accept_run(std::shared_ptr<Session> session, std::shared_ptr<Image> frame,
                                const Json &request);
std::shared_ptr<Image> copy_frame(const jpv_image_info &info, const uint8_t *pixels);
Json image_metadata(const Image &image);
Json pose_metadata(const PoseValue &pose);
int active_run_count();
} // namespace jp

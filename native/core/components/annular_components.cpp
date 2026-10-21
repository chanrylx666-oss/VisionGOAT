#include "annular_components.h"
#include "roi.h"
#include <algorithm>
#include <cmath>
#include <opencv2/imgproc.hpp>

namespace jp {
namespace {
constexpr int MaximumRemapDimension = 32766;
constexpr uint64_t MaximumUnwrappedPixels = 16ULL * 1024 * 1024;
constexpr uint64_t MaximumMapBytes = 128ULL * 1024 * 1024;

Json described_parameter(const char *name, const char *label, const char *type, Json value,
                         const char *description, Json minimum = nullptr, Json maximum = nullptr,
                         Json choices = nullptr, const char *unit = "") {
    auto result = parameter(name, label, type, std::move(value), std::move(minimum), std::move(maximum),
                            std::move(choices));
    result["group"] = "展开设置";
    result["description"] = description;
    if (*unit)
        result["unit"] = unit;
    return result;
}

void validate_annular_parameters(const Json &parameters) {
    const auto &roi = parameters.at("roi");
    validate_roi(roi);
    require(roi.at("shape") == "annulus", JPV_INVALID_RECIPE,
            "Annular unwrapping requires an annulus ROI. Draw an annulus first.");
}

int output_dimension(int configured, double automatic) {
    double selected = configured ? configured : std::max(1.0, std::ceil(automatic));
    require(std::isfinite(selected) && selected >= 1 && selected <= MaximumRemapDimension, JPV_RESOURCE_LIMIT,
            "Annular unwrapping dimensions exceed the remap limit.");
    return static_cast<int>(selected);
}

bool source_pixel_valid(const Image &source, int x, int y) {
    return x >= 0 && y >= 0 && x < source.pixels.cols && y < source.pixels.rows &&
           (source.valid.empty() || source.valid.at<uint8_t>(y, x) != 0);
}

// Validity is stricter than the interpolated image: no invalid source pixel may contribute to a
// valid output pixel. This keeps masks from creating artificial edges in downstream inspection.
bool sample_valid(const Image &source, float x, float y, bool nearest) {
    if (nearest)
        return source_pixel_valid(source, cvRound(x), cvRound(y));
    int left = static_cast<int>(std::floor(x)), top = static_cast<int>(std::floor(y));
    int right = x == static_cast<float>(left) ? left : left + 1;
    int bottom = y == static_cast<float>(top) ? top : top + 1;
    return source_pixel_valid(source, left, top) && source_pixel_valid(source, right, top) &&
           source_pixel_valid(source, left, bottom) && source_pixel_valid(source, right, bottom);
}

Value unwrap_annulus(const Value *input, const Json &parameters, const Image &) {
    const auto &source = std::get<Image>(*input);
    if (!source.available)
        return source;
    require(!source.pixels.empty() && (source.pixels.type() == CV_8UC1 || source.pixels.type() == CV_8UC3),
            JPV_INVALID_ARGUMENT, "Annular unwrapping requires a Gray8 or Bgr24 image.");
    require(source.pixels.cols <= MaximumRemapDimension && source.pixels.rows <= MaximumRemapDimension,
            JPV_RESOURCE_LIMIT, "Annular unwrapping source dimensions exceed the remap limit.");
    require(source.valid.empty() ||
                (source.valid.size() == source.pixels.size() && source.valid.type() == CV_8UC1),
            JPV_INVALID_ARGUMENT, "Annular unwrapping input validity mask does not match the image.");
    validate_annular_parameters(parameters);
    const auto &roi = parameters.at("roi");
    double centerX = roi.at("cx"), centerY = roi.at("cy"), inner = roi.at("innerRadius"),
           outer = roi.at("outerRadius"), start = roi.at("startAngle"),
           span = roi.at("endAngle").get<double>() - start;
    int width = output_dimension(parameters.at("outputWidth"), (inner + outer) * .5 * span * CV_PI / 180);
    int height = output_dimension(parameters.at("outputHeight"), outer - inner);
    uint64_t pixels = uint64_t(width) * uint64_t(height);
    require(pixels <= MaximumUnwrappedPixels && pixels <= MaximumMapBytes / (2 * sizeof(float)),
            JPV_RESOURCE_LIMIT, "Annular unwrapping output exceeds the sampling budget.");
    bool clockwise = parameters.at("direction") == "clockwise";
    bool innerToOuter = parameters.at("radialOrder") == "innerToOuter";
    bool nearest = parameters.at("interpolation") == "nearest";
    double angularOrigin = clockwise ? start : start + span;
    double angleStep = (clockwise ? 1 : -1) * span / width;
    double radialStep = height > 1 ? (outer - inner) / (height - 1) * (innerToOuter ? 1 : -1) : 0;
    double firstRadius = height == 1 ? (inner + outer) * .5 : innerToOuter ? inner : outer;
    cv::Mat mapX(height, width, CV_32FC1), mapY(height, width, CV_32FC1);
    Image output;
    output.valid.create(height, width, CV_8UC1);
    std::vector<double> cosines(width), sines(width);
    for (int column = 0; column < width; ++column) {
        double angle = (angularOrigin + angleStep * column) * CV_PI / 180;
        cosines[column] = std::cos(angle);
        sines[column] = std::sin(angle);
    }
    for (int row = 0; row < height; ++row) {
        double radius = firstRadius + radialStep * row;
        auto *xs = mapX.ptr<float>(row), *ys = mapY.ptr<float>(row);
        auto *valid = output.valid.ptr<uint8_t>(row);
        for (int column = 0; column < width; ++column) {
            float x = static_cast<float>(centerX + radius * cosines[column]);
            float y = static_cast<float>(centerY + radius * sines[column]);
            xs[column] = x;
            ys[column] = y;
            valid[column] = sample_valid(source, x, y, nearest) ? 255 : 0;
        }
    }
    const int borderValue = 0;
    cv::remap(source.pixels, output.pixels, mapX, mapY, nearest ? cv::INTER_NEAREST : cv::INTER_LINEAR,
              cv::BORDER_CONSTANT, cv::Scalar::all(borderValue));
    int validPixels = cv::countNonZero(output.valid);
    if (uint64_t(validPixels) != pixels) {
        cv::Mat invalid;
        cv::bitwise_not(output.valid, invalid);
        output.pixels.setTo(cv::Scalar::all(borderValue), invalid);
    } else
        output.valid.release();
    output.available = validPixels != 0;
    output.unavailableReason = output.available ? "" : "UnavailableAnnularSamples";
    output.padded = source.padded || uint64_t(validPixels) != pixels;
    output.binaryMask = source.binaryMask && nearest;
    output.coordinateSpace = "annularUnwrapped";
    output.coordinateMapping = {{"kind", "annularUnwrap"},
                                {"sourceCoordinateSpace", source.coordinateSpace},
                                {"sourcePlane", source.coordinatePlane},
                                {"sourceOriginX", source.x},
                                {"sourceOriginY", source.y},
                                {"centerX", source.x + centerX},
                                {"centerY", source.y + centerY},
                                {"innerRadius", inner},
                                {"outerRadius", outer},
                                {"startAngle", start},
                                {"spanDegrees", span},
                                {"direction", parameters.at("direction")},
                                {"radialOrder", parameters.at("radialOrder")},
                                {"width", width},
                                {"height", height},
                                {"angularOriginDegrees", angularOrigin},
                                {"angleStepDegrees", angleStep},
                                {"firstRadius", firstRadius},
                                {"radialOrigin", firstRadius},
                                {"radialStep", radialStep}};
    return output;
}
} // namespace

void register_annular_components(ComponentRegistry &registry) {
    auto definition = component(
        "AnnularUnwrap", "圆环展开", "图像变换", "Image", "Image", "image",
        Json::array(
            {described_parameter("outputWidth", "展开宽度（0 自动）", "integer", 0,
                                 "横向对应角度；0 按圆环平均半径和角度范围自动计算，整圆不重复首尾列。", 0,
                                 MaximumRemapDimension, nullptr, "px"),
             described_parameter("outputHeight", "展开高度（0 自动）", "integer", 0,
                                 "纵向对应半径；0 按外半径减内半径向上取整，首末行对应内外边界。", 0,
                                 MaximumRemapDimension, nullptr, "px"),
             described_parameter(
                 "direction", "角度展开方向", "enum", "clockwise",
                 "顺时针从起始角展开；逆时针从结束角反向展开同一范围。向右为 0°，向下为 90°。", nullptr,
                 nullptr, Json::array({"clockwise", "counterclockwise"})),
             described_parameter("radialOrder", "半径展开顺序", "enum", "innerToOuter",
                                 "选择图像从上到下由内圈到外圈，或由外圈到内圈。", nullptr, nullptr,
                                 Json::array({"innerToOuter", "outerToInner"})),
             described_parameter("interpolation", "采样插值", "enum", "linear",
                                 "双线性适合灰度或彩色图像；最近邻适合保持二值图的像素值。", nullptr, nullptr,
                                 Json::array({"linear", "nearest"}))}),
        unwrap_annulus, validate_annular_parameters);
    definition.descriptor["roiReferenceOnly"] = true;
    definition.descriptor["coordinateTransform"] = true;
    definition.descriptor["defaultRoi"] = {{"shape", "annulus"}, {"cx", 160},          {"cy", 160},
                                           {"innerRadius", 60},  {"outerRadius", 100}, {"startAngle", 0},
                                           {"endAngle", 360}};
    registry.add(std::move(definition));
}
} // namespace jp

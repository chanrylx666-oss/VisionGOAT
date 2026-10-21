#include "component.h"
#include "roi.h"
#include "image_components.h"
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>

namespace jp {
static int border_mode(const Json &p) {
    const auto mode = p.at("borderMode").get<std::string>();
    return mode == "replicate"  ? cv::BORDER_REPLICATE
           : mode == "reflect"  ? cv::BORDER_REFLECT
           : mode == "constant" ? cv::BORDER_CONSTANT
                                : cv::BORDER_REFLECT_101;
}
void register_basic_components(ComponentRegistry &r) {
    r.add(component("ExternalImage", "外部图像", "输入", "", "Image", "image",
                    Json::array({parameter("input", "公开输入名称", "string", "InputImage")}),
                    [](const Value *, const Json &, const Image &original) -> Value { return original; }));
    r.add_image("ToGray", "灰度转换", "预处理",
                Json::array({parameter("channel", "灰度来源", "enum", "luminance", nullptr, nullptr,
                                       Json::array({"luminance", "blue", "green", "red"}))}),
                [](const cv::Mat &source, const Json &p) {
                    if (source.channels() == 1 || p.at("channel") == "luminance")
                        return gray_pixels(Image{source});
                    cv::Mat out;
                    const auto channel = p.at("channel").get<std::string>();
                    cv::extractChannel(source, out, channel == "blue" ? 0 : channel == "green" ? 1 : 2);
                    return out;
                });
    r.add_image(
        "GaussianBlur", "高斯滤波", "预处理",
        Json::array({odd_parameter("kernel", "核宽度（奇数 px）", 5, 1, 101),
                     odd_parameter("kernelHeight", "核高度（0 跟随宽度）", 0, 0, 101, true),
                     parameter("sigma", "X 方向 Sigma", "number", 1.0, 0, 100),
                     parameter("sigmaY", "Y 方向 Sigma（0 跟随 X）", "number", 0.0, 0, 100),
                     parameter("borderMode", "边界处理", "enum", "reflect101", nullptr, nullptr,
                               Json::array({"reflect101", "reflect", "replicate", "constant"}))}),
        [](const cv::Mat &source, const Json &p) {
            cv::Mat out;
            int kernel = p.at("kernel");
            int height = p.at("kernelHeight");
            cv::GaussianBlur(source, out, {kernel, height ? height : kernel}, p.at("sigma").get<double>(),
                             p.at("sigmaY").get<double>(), border_mode(p));
            return out;
        },
        require_odd_kernel);
    r.add(component(
        "Threshold", "二值化", "分割", "Image", "Mask", "mask",
        Json::array(
            {parameter("method", "阈值方式", "enum", "fixed", nullptr, nullptr,
                       Json::array({"fixed", "otsu", "triangle", "adaptiveMean", "adaptiveGaussian"})),
             parameter("value", "固定阈值", "integer", 128, 0, 255),
             parameter("invert", "反向阈值", "bool", false),
             odd_parameter("blockSize", "局部窗口（奇数 px）", 15, 3, 255),
             parameter("offset", "局部阈值偏移 C", "number", 2.0, -255, 255)}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            const auto gray = gray_pixels(source);
            Image out{{}, source.x, source.y};
            int operation = p.at("invert").get<bool>() ? cv::THRESH_BINARY_INV : cv::THRESH_BINARY;
            const auto method = p.at("method").get<std::string>();
            if (method == "adaptiveMean" || method == "adaptiveGaussian")
                cv::adaptiveThreshold(gray, out.pixels, 255,
                                      method == "adaptiveMean" ? cv::ADAPTIVE_THRESH_MEAN_C
                                                               : cv::ADAPTIVE_THRESH_GAUSSIAN_C,
                                      operation, p.at("blockSize").get<int>(), p.at("offset").get<double>());
            else {
                int flags = operation | (method == "otsu"       ? cv::THRESH_OTSU
                                         : method == "triangle" ? cv::THRESH_TRIANGLE
                                                                : 0);
                if (source.valid.empty())
                    cv::threshold(gray, out.pixels, p.at("value").get<int>(), 255, flags);
                else {
                    // Automatic thresholds must not include pixels outside a non-rectangular ROI.
                    out.pixels = cv::Mat::zeros(gray.size(), gray.type());
                    cv::thresholdWithMask(gray, out.pixels, source.valid, p.at("value").get<int>(), 255,
                                          flags);
                }
            }
            return out;
        },
        [](const Json &p) {
            require(p.at("blockSize").get<int>() % 2 == 1, JPV_INVALID_RECIPE,
                    "Adaptive threshold block size must be odd.");
        }));
    r.add(component(
        "Morphology", "形态学", "分割", "Mask", "Mask", "mask",
        Json::array(
            {parameter("operation", "操作", "enum", "open", nullptr, nullptr,
                       Json::array({"open", "close", "erode", "dilate", "gradient", "topHat", "blackHat"})),
             parameter("kernelShape", "结构元素形状", "enum", "rectangle", nullptr, nullptr,
                       Json::array({"rectangle", "ellipse", "cross"})),
             odd_parameter("kernel", "核宽度（奇数 px）", 3, 1, 101),
             odd_parameter("kernelHeight", "核高度（0 跟随宽度）", 0, 0, 101, true),
             parameter("iterations", "迭代次数", "integer", 1, 1, 16),
             parameter("borderMode", "边界处理", "enum", "constant", nullptr, nullptr,
                       Json::array({"constant", "replicate", "reflect", "reflect101"})),
             parameter("borderValue", "边界灰度（-1 自动）", "integer", -1, -1, 255)}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto source = binary_image(std::get<Image>(*input), p);
            require(source.pixels.type() == CV_8UC1, JPV_INVALID_ARGUMENT,
                    "Morphology requires a binary mask.");
            int k = p.at("kernel"), n = p.at("iterations");
            int height = p.at("kernelHeight");
            const auto shape = p.at("kernelShape").get<std::string>();
            auto kernel = cv::getStructuringElement(shape == "ellipse" ? cv::MORPH_ELLIPSE
                                                    : shape == "cross" ? cv::MORPH_CROSS
                                                                       : cv::MORPH_RECT,
                                                    {k, height ? height : k});
            auto op = p.at("operation").get<std::string>();
            int operation = op == "open"       ? cv::MORPH_OPEN
                            : op == "close"    ? cv::MORPH_CLOSE
                            : op == "erode"    ? cv::MORPH_ERODE
                            : op == "dilate"   ? cv::MORPH_DILATE
                            : op == "gradient" ? cv::MORPH_GRADIENT
                            : op == "topHat"   ? cv::MORPH_TOPHAT
                                               : cv::MORPH_BLACKHAT;
            Image out{{}, source.x, source.y};
            int value = p.at("borderValue");
            cv::morphologyEx(source.pixels, out.pixels, operation, kernel, {-1, -1}, n, border_mode(p),
                             value < 0 ? cv::morphologyDefaultBorderValue() : cv::Scalar::all(value));
            return out;
        },
        [](const Json &p) {
            require_odd_kernel(p);
            int value = p.at("borderValue");
            require(value == -1 || value == 0 || value == 255, JPV_INVALID_RECIPE,
                    "Binary morphology border value must be -1 (automatic), 0 or 255.");
        }));
    r.add(component(
        "CropRect", "矩形 ROI", "预处理", "Image", "Image", "image",
        Json::array({parameter("x", "X", "integer", 0, 0, 32767), parameter("y", "Y", "integer", 0, 0, 32767),
                     parameter("width", "宽度", "integer", 200, 1, 32768),
                     parameter("height", "高度", "integer", 200, 1, 32768)}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            int x = p.at("x"), y = p.at("y"), w = p.at("width"), h = p.at("height");
            require(x <= source.pixels.cols - w && y <= source.pixels.rows - h, JPV_INVALID_ARGUMENT,
                    "ROI lies outside input image.");
            cv::Rect rect(x, y, w, h);
            return Image{source.pixels(rect).clone(), source.x + x, source.y + y,
                         source.valid.empty() ? cv::Mat() : source.valid(rect).clone()};
        }));
    r.add(component("FindContours", "提取轮廓", "检测", "Mask", "Contours", "contours", Json::array(),
                    [](const Value *input, const Json &p, const Image &) -> Value {
                        const auto source = binary_image(std::get<Image>(*input), p);
                        require(source.pixels.type() == CV_8UC1, JPV_INVALID_ARGUMENT,
                                "FindContours requires a Gray8 mask.");
                        Contours out{{}, source.x, source.y};
                        out.sourceImage = std::make_shared<Image>(source);
                        cv::findContours(visible_pixels(source).clone(), out.items, cv::RETR_EXTERNAL,
                                         cv::CHAIN_APPROX_SIMPLE);
                        size_t points = 0;
                        for (const auto &contour : out.items)
                            points += contour.size();
                        require(out.items.size() <= 10000 && points <= 1000000, JPV_RESOURCE_LIMIT,
                                "Contour result exceeds limit.");
                        return out;
                    }));
    r.add(component(
        "FilterContoursByArea", "轮廓面积筛选", "检测", "Contours", "Contours", "contours",
        Json::array({parameter("minimum", "最小面积（px²）", "number", 100.0, 0, 1e12),
                     parameter("maximum", "最大面积（px²）", "number", 1000000.0, 0, 1e12)}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Contours>(*input);
            Contours out{{}, source.x, source.y};
            out.dataAvailable = source.dataAvailable;
            out.sourceImage = source.sourceImage;
            for (const auto &contour : source.items) {
                double area = cv::contourArea(contour);
                if (area >= p.at("minimum").get<double>() && area <= p.at("maximum").get<double>())
                    out.items.push_back(contour);
            }
            return out;
        },
        [](const Json &p) {
            require(p.at("minimum").get<double>() <= p.at("maximum").get<double>(), JPV_INVALID_RECIPE,
                    "Minimum area exceeds maximum.");
        }));
    r.add(component("CountContours", "目标计数", "检测", "Contours", "Int", "value", Json::array(),
                    [](const Value *input, const Json &, const Image &) -> Value {
                        return static_cast<int64_t>(std::get<Contours>(*input).items.size());
                    }));
    r.add(component("CompareNumber", "数量判定", "判定", "Int", "Bool", "value",
                    Json::array({parameter("expected", "预期数量", "integer",
                                           Json{{"input", "ExpectedCount"}}, 0, 1000000, nullptr, true)}),
                    [](const Value *input, const Json &p, const Image &) -> Value {
                        return std::get<int64_t>(*input) == p.at("expected").get<int64_t>();
                    }));
    r.add(component("BuildContourOverlay", "轮廓标记", "显示", "Contours", "Overlay", "overlay",
                    Json::array({parameter("color", "颜色（#RRGGBB）", "string", DefaultResultColor),
                                 parameter("lineWidth", "线宽", "number", 2.0, 0.5, 10)}),
                    [](const Value *input, const Json &p, const Image &) -> Value {
                        const auto &contours = std::get<Contours>(*input);
                        Json overlay = Json::array();
                        for (const auto &contour : contours.items) {
                            Json points = Json::array();
                            for (const auto &point : contour)
                                points.push_back({point.x + contours.x, point.y + contours.y});
                            overlay.push_back({{"type", "polyline"},
                                               {"closed", true},
                                               {"points", points},
                                               {"color", p.at("color")},
                                               {"lineWidth", p.at("lineWidth")}});
                        }
                        overlay.push_back({{"type", "text"},
                                           {"x", 16},
                                           {"y", 20},
                                           {"text", "Count: " + std::to_string(contours.items.size())},
                                           {"color", p.at("color")}});
                        return overlay;
                    }));
}
} // namespace jp

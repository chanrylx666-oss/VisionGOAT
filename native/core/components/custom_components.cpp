#include "component.h"
#include <opencv2/imgproc.hpp>

namespace jp {
// Add future OpenCV components here. The toolbox and parameter inspector are automatic.
void register_custom_components(ComponentRegistry &registry) {
    registry.add_image(
        "MedianBlur", "中值滤波", "预处理",
        Json::array({odd_parameter("kernel", "核尺寸（奇数 px）", 3, 3, 31)}),
        [](const cv::Mat &image, const Json &parameters) {
            cv::Mat output;
            cv::medianBlur(image, output, parameters.at("kernel").get<int>());
            return output;
        },
        require_odd_kernel);
}
} // namespace jp

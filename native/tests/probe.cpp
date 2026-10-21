#include <filesystem>
#include <iostream>
#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>
int main(int argc, char **argv) {
    cv::Mat image(360, 640, CV_8UC3, cv::Scalar(18, 18, 18));
    for (int i = 0; i < 3; ++i)
        cv::circle(image, {120 + i * 180, 180}, 48, cv::Scalar(235, 235, 235), -1);
    cv::Mat gray, mask;
    cv::cvtColor(image, gray, cv::COLOR_BGR2GRAY);
    cv::threshold(gray, mask, 128, 255, cv::THRESH_BINARY);
    std::vector<std::vector<cv::Point>> contours;
    cv::findContours(mask, contours, cv::RETR_EXTERNAL, cv::CHAIN_APPROX_SIMPLE);
    if (contours.size() != 3)
        return 1;
    if (argc > 1) {
        std::filesystem::create_directories(argv[1]);
        cv::imwrite(std::string(argv[1]) + "/three-parts.png", image);
        cv::circle(image, {300, 180}, 55, cv::Scalar(18, 18, 18), -1);
        cv::imwrite(std::string(argv[1]) + "/two-parts.png", image);
        cv::Mat empty(360, 640, CV_8UC3, cv::Scalar(18, 18, 18));
        cv::imwrite(std::string(argv[1]) + "/no-parts.png", empty);
        cv::Mat lines = empty.clone();
        for (int y : {90, 180, 270})
            cv::line(lines, {100, y}, {540, y}, cv::Scalar(235, 235, 235), 3);
        cv::imwrite(std::string(argv[1]) + "/three-lines.png", lines);
        cv::Mat edge = empty.clone();
        edge(cv::Rect(0, 180, 640, 180)).setTo(cv::Scalar(235, 235, 235));
        cv::imwrite(std::string(argv[1]) + "/single-edge.png", edge);
    }
    std::cout << "OpenCV " << CV_VERSION << ", MSVC x64: 3 known objects detected.\n";
    return 0;
}

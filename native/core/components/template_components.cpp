#include "component.h"
#include <algorithm>
#include <cmath>
#include <numbers>
#include <mutex>
#include <unordered_set>
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>

namespace jp {
struct TemplateModel {
    cv::Mat gray, mask;
};
static TemplateModel read_model(const Json &model) {
    require(model.is_object() && model.contains("width") && model.contains("height") &&
                model.contains("grayBase64"),
            JPV_INVALID_RECIPE, "Template has not been taught or imported.");
    int width = int(integer(model.at("width"), "Template width", 4, 512)),
        height = int(integer(model.at("height"), "Template height", 4, 512));
    require(model.at("grayBase64").is_string(), JPV_INVALID_RECIPE, "Template pixels must be Base64.");
    auto bytes = base64_decode(model.at("grayBase64"), 350000, JPV_INVALID_RECIPE);
    require(bytes.size() == size_t(width) * height, JPV_INVALID_RECIPE,
            "Template pixel dimensions do not match.");
    TemplateModel result{cv::Mat(height, width, CV_8UC1, bytes.data()).clone(),
                         cv::Mat(height, width, CV_8UC1, cv::Scalar(255))};
    if (model.contains("maskBase64")) {
        require(model.at("maskBase64").is_string(), JPV_INVALID_RECIPE, "Template mask must be Base64.");
        auto mask = base64_decode(model.at("maskBase64"), 350000, JPV_INVALID_RECIPE);
        require(mask.size() == bytes.size() &&
                    std::all_of(mask.begin(), mask.end(), [](uint8_t n) { return n == 0 || n == 255; }),
                JPV_INVALID_RECIPE, "Template mask is invalid.");
        result.mask = cv::Mat(height, width, CV_8UC1, mask.data()).clone();
    }
    for (auto it = model.begin(); it != model.end(); ++it)
        require(it.key() == "width" || it.key() == "height" || it.key() == "grayBase64" ||
                    it.key() == "maskBase64",
                JPV_INVALID_RECIPE, "Unknown template field.");
    cv::Scalar mean, dev;
    cv::meanStdDev(result.gray, mean, dev, result.mask);
    require(cv::countNonZero(result.mask) >= 16 && dev[0] >= 1, JPV_INVALID_RECIPE,
            "Template needs at least 16 valid pixels and grayscale contrast.");
    return result;
}
static TemplateModel decode_model(const Json &model) {
    // Cache only immutable decoded models, never per-frame positions or search results.
    struct Cached {
        std::string key;
        TemplateModel model;
    };
    static std::mutex mutex;
    static std::vector<Cached> cache; // At most 16 * (512*512*2) = 8 MiB of pixels.
    auto key = sha256(std::string("jpv-template-v1:") + CV_VERSION + model.dump());
    std::lock_guard lock(mutex);
    auto found =
        std::find_if(cache.begin(), cache.end(), [&](const auto &entry) { return entry.key == key; });
    if (found != cache.end()) {
        auto entry = *found;
        cache.erase(found);
        cache.push_back(entry);
        return entry.model;
    }
    auto decoded = read_model(model);
    if (cache.size() == 16)
        cache.erase(cache.begin());
    cache.push_back({std::move(key), decoded});
    return decoded;
}
struct FastTemplate {
    TemplateModel model;
    std::string id, name;
};
static bool is_template_bank(const Json &model) {
    return model.is_object() && model.contains("templates");
}
static std::vector<FastTemplate> read_fast_models(const Json &model, bool trained) {
    if (!is_template_bank(model))
        return {{decode_model(model), {}, {}}};
    require(model.size() == 1 && model.at("templates").is_array() &&
                !model.at("templates").empty() && model.at("templates").size() <= 8,
            JPV_INVALID_RECIPE, "Template bank must contain between 1 and 8 templates only.");
    std::vector<FastTemplate> enabled;
    std::unordered_set<std::string> ids;
    for (const auto &entry : model.at("templates")) {
        require(entry.is_object() && entry.size() == 4 && entry.contains("id") && entry.contains("name") &&
                    entry.contains("enabled") && entry.contains("model") && entry.at("id").is_string() &&
                    entry.at("name").is_string() && entry.at("enabled").is_boolean(),
                JPV_INVALID_RECIPE, "Each template needs exactly id, name, enabled and model fields.");
        auto id = entry.at("id").get<std::string>(), name = entry.at("name").get<std::string>();
        require(!id.empty() && id.size() <= 64 &&
                    std::all_of(id.begin(), id.end(), [](unsigned char c) {
                        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                               (c >= '0' && c <= '9') || c == '_' || c == '-';
                    }) && ids.insert(id).second,
                JPV_INVALID_RECIPE, "Template ids must be unique and use 1-64 ASCII letters, digits, _ or -.");
        require(!name.empty() && name.size() <= 128, JPV_INVALID_RECIPE,
                "Template names must contain between 1 and 128 UTF-8 bytes.");
        // Disabled entries are still persisted recipe data, so invalid or empty models are rejected.
        auto decoded = decode_model(entry.at("model"));
        if (entry.at("enabled").get<bool>())
            enabled.push_back({std::move(decoded), std::move(id), std::move(name)});
    }
    require(!trained || !enabled.empty(), JPV_INVALID_RECIPE,
            "Enable at least one taught template before running fast matching.");
    return enabled;
}
void validate_template(const Json &model, bool trained) {
    require(model.is_object(), JPV_INVALID_RECIPE, "Template model must be an object.");
    if (!trained && model.empty())
        return;
    read_fast_models(model, trained);
}
static std::vector<double> fast_scales(const Json &p, size_t enabledTemplates) {
    double minimum = p.at("scaleMinimum"), maximum = p.at("scaleMaximum"), step = p.at("scaleStep");
    require(std::isfinite(minimum) && std::isfinite(maximum) && std::isfinite(step) && minimum >= .5 &&
                maximum <= 2 && minimum <= maximum && step >= .01 && step <= 1,
            JPV_INVALID_RECIPE, "Fast matching scales need 0.5 <= minimum <= maximum <= 2 and step 0.01-1.");
    std::vector<double> scales{minimum};
    int count = int(std::floor((maximum - minimum) / step + 1e-8));
    for (int index = 1; index <= count; ++index) {
        double value = minimum + index * step;
        // The explicit maximum below also covers a final interval smaller than the step.
        if (value < maximum - 1e-10)
            scales.push_back(value);
    }
    if (maximum > minimum)
        scales.push_back(maximum);
    if (minimum <= 1 && maximum >= 1) {
        auto unit = std::find_if(scales.begin(), scales.end(), [](double value) {
            return std::abs(value - 1) < 1e-10;
        });
        if (unit == scales.end())
            scales.push_back(1);
        else if (*unit != minimum && *unit != maximum)
            *unit = 1;
        // An endpoint very close to 1 remains an endpoint; include exact unit scale as well.
        else if (*unit != 1)
            scales.push_back(1);
    }
    std::sort(scales.begin(), scales.end());
    require(enabledTemplates != 0 && scales.size() * enabledTemplates <= 121, JPV_RESOURCE_LIMIT,
            "Fast matching accepts at most 121 enabled-template/scale candidates; reduce templates or scale range.");
    return scales;
}
struct Pose {
    cv::Mat gray, mask, matrix;
    std::array<cv::Point2d, 4> corners;
};
static Pose transform_template(const TemplateModel &model, double angle, double scale) {
    double radians = angle * std::numbers::pi / 180, c = std::abs(std::cos(radians)),
           s = std::abs(std::sin(radians));
    int width = int(std::ceil((model.gray.cols * c + model.gray.rows * s) * scale - 1e-8)),
        height = int(std::ceil((model.gray.cols * s + model.gray.rows * c) * scale - 1e-8));
    Pose pose;
    cv::Point2f center((model.gray.cols - 1) / 2.0f, (model.gray.rows - 1) / 2.0f);
    pose.matrix = cv::getRotationMatrix2D(center, -angle, scale);
    pose.matrix.at<double>(0, 2) += (width - 1) / 2.0 - center.x;
    pose.matrix.at<double>(1, 2) += (height - 1) / 2.0 - center.y;
    cv::warpAffine(model.gray, pose.gray, pose.matrix, {width, height}, cv::INTER_LINEAR, cv::BORDER_CONSTANT,
                   0);
    cv::warpAffine(model.mask, pose.mask, pose.matrix, {width, height}, cv::INTER_NEAREST,
                   cv::BORDER_CONSTANT, 0);
    std::array<cv::Point2d, 4> corners{{{-.5, -.5},
                                        {model.gray.cols - .5, -.5},
                                        {model.gray.cols - .5, model.gray.rows - .5},
                                        {-.5, model.gray.rows - .5}}};
    for (int i = 0; i < 4; ++i) {
        auto p = corners[i];
        pose.corners[i] = {pose.matrix.at<double>(0, 0) * p.x + pose.matrix.at<double>(0, 1) * p.y +
                               pose.matrix.at<double>(0, 2) + .5,
                           pose.matrix.at<double>(1, 0) * p.x + pose.matrix.at<double>(1, 1) * p.y +
                               pose.matrix.at<double>(1, 2) + .5};
    }
    return pose;
}
// Zero-mean NCC via correlation sums also supports non-rectangular template masks.
static cv::Mat ncc(const cv::Mat &image, const cv::Mat &squared, const cv::Mat &valid, const Pose &pose) {
    cv::Mat weights, templ;
    pose.mask.convertTo(weights, CV_32F, 1.0 / 255);
    pose.gray.convertTo(templ, CV_32F);
    double count = cv::sum(weights)[0];
    double mean = cv::sum(templ.mul(weights))[0] / count;
    cv::Mat centered = (templ - mean).mul(weights);
    double variance = cv::sum(centered.mul(centered))[0];
    cv::Mat numerator, sum, squares, coverage;
    cv::matchTemplate(image, centered, numerator, cv::TM_CCORR);
    cv::matchTemplate(image, weights, sum, cv::TM_CCORR);
    cv::matchTemplate(squared, weights, squares, cv::TM_CCORR);
    if (!valid.empty())
        cv::matchTemplate(valid, weights, coverage, cv::TM_CCORR);
    for (int y = 0; y < numerator.rows; ++y)
        for (int x = 0; x < numerator.cols; ++x) {
            double localVariance =
                squares.at<float>(y, x) - double(sum.at<float>(y, x)) * sum.at<float>(y, x) / count;
            double score = localVariance >= count && variance > 1
                               ? numerator.at<float>(y, x) / std::sqrt(localVariance * variance)
                               : -1;
            if (!std::isfinite(score) || (!coverage.empty() && coverage.at<float>(y, x) < count - .1))
                score = -1;
            numerator.at<float>(y, x) = float(std::clamp(score, -1.0, 1.0));
        }
    return numerator;
}
static int pose_count(const Json &p) {
    double a = p.at("angleMinimum"), b = p.at("angleMaximum"), lo = p.at("scaleMinimum"),
           hi = p.at("scaleMaximum");
    require(a <= b && lo <= hi, JPV_INVALID_RECIPE, "Template angle/scale minimum exceeds maximum.");
    int angles = int(std::floor((b - a) / p.at("angleStep").get<double>() + 1e-8)) + 1;
    int scales = int(std::floor((hi - lo) / p.at("scaleStep").get<double>() + 1e-8)) + 1;
    require(angles <= 121 && scales <= 121 && angles * scales <= 121, JPV_RESOURCE_LIMIT,
            "Template search accepts at most 121 angle/scale poses.");
    return angles * scales;
}
static double overlap(const MatchFeature &a, const MatchFeature &b) {
    double width = std::max(0.0, std::min(a.x + a.width, b.x + b.width) - std::max(a.x, b.x)),
           height = std::max(0.0, std::min(a.y + a.height, b.y + b.height) - std::max(a.y, b.y));
    double area = width * height;
    return area / (a.width * a.height + b.width * b.height - area);
}
struct FastSearchBudget {
    size_t pixels = 0;
    void add(const cv::Mat &image) {
        constexpr size_t maximum = 512ULL * 1024 * 1024;
        require(image.total() <= maximum - pixels, JPV_RESOURCE_LIMIT,
                "Fast matching search budget exceeded; reduce ROI, enabled templates or scale range.");
        pixels += image.total();
    }
};
static cv::Mat translation_scores(const cv::Mat &gray, const cv::Mat &valid, const TemplateModel &model,
                                  FastSearchBudget &budget) {
    // Count actual coarse tiles, refinement patches and fallback tiles, rather than charging a
    // full-resolution scan for every pose even when coarse search avoids that scan.
    budget.add(gray);
    cv::Mat scores;
    if (valid.empty() && cv::countNonZero(model.mask) == int(model.mask.total())) {
        cv::matchTemplate(gray, model.gray, scores, cv::TM_CCOEFF_NORMED);
        cv::patchNaNs(scores, -1);
        return scores;
    }
    cv::Mat image, coverage;
    gray.convertTo(image, CV_32F);
    if (!valid.empty())
        valid.convertTo(coverage, CV_32F, 1.0 / 255);
    Pose pose;
    pose.gray = model.gray;
    pose.mask = model.mask;
    return ncc(image, image.mul(image), coverage, pose);
}
static void suppress_peak(cv::Mat &scores, cv::Point point, cv::Size size) {
    int rx = std::max(1, size.width / 3), ry = std::max(1, size.height / 3);
    cv::Rect area(std::max(0, point.x - rx), std::max(0, point.y - ry), 0, 0);
    area.width = std::min(scores.cols, point.x + rx + 1) - area.x;
    area.height = std::min(scores.rows, point.y + ry + 1) - area.y;
    scores(area).setTo(-1);
}
static constexpr size_t MaxFastImagePixels = 32ULL * 1024 * 1024, MaxCoarseImagePixels = 8ULL * 1024 * 1024;
static constexpr int FastTilePositions = 1024;
// Split the domain of template top-left positions, not the input pixels. Each input tile includes
// modelSize - 1 extra rows/columns, so every possible window occurs once, including seam windows.
template <typename Collect>
static void translation_tiles(const cv::Mat &gray, const cv::Mat &valid, const TemplateModel &model,
                              FastSearchBudget &budget, Collect collect) {
    int columns = gray.cols - model.gray.cols + 1, rows = gray.rows - model.gray.rows + 1;
    // Size the result domain independently of the model: a 1024-pixel scaled template must not
    // degenerate into one input tile per pixel. Input tiles remain bounded to 2047 x 2047 pixels.
    int stepX = FastTilePositions, stepY = FastTilePositions;
    for (int y = 0; y < rows; y += stepY)
        for (int x = 0; x < columns; x += stepX) {
            int scoreWidth = std::min(stepX, columns - x), scoreHeight = std::min(stepY, rows - y);
            cv::Rect tile(x, y, scoreWidth + model.gray.cols - 1, scoreHeight + model.gray.rows - 1);
            collect(translation_scores(gray(tile), valid.empty() ? cv::Mat() : valid(tile), model, budget),
                    cv::Point(x, y));
        }
}
using TranslationCandidate = std::pair<cv::Point, double>;
static bool better_candidate(const TranslationCandidate &a, const TranslationCandidate &b) {
    if (a.second != b.second)
        return a.second > b.second;
    return a.first.y != b.first.y ? a.first.y < b.first.y : a.first.x < b.first.x;
}
static void retain_candidate(std::vector<TranslationCandidate> &candidates, TranslationCandidate candidate,
                             int limit, cv::Size size, double maximumOverlap) {
    auto duplicates = [&](const TranslationCandidate &other) {
        cv::Rect a(candidate.first, size), b(other.first, size);
        double intersection = double((a & b).area());
        return intersection / (2.0 * size.area() - intersection) > maximumOverlap;
    };
    // A peak next to a tile seam can appear in adjacent score tiles with a small offset.
    // Merge those windows before they consume the global candidate budget.
    if (std::any_of(candidates.begin(), candidates.end(), [&](const auto &other) {
            return duplicates(other) && !better_candidate(candidate, other);
        }))
        return;
    std::erase_if(candidates, duplicates);
    if (candidates.size() == size_t(limit) && !better_candidate(candidate, candidates.back()))
        return;
    candidates.insert(std::lower_bound(candidates.begin(), candidates.end(), candidate, better_candidate),
                      candidate);
    if (candidates.size() > size_t(limit))
        candidates.pop_back();
}
struct CoarseSearchImage {
    cv::Mat gray, valid;
};
struct FastSearchImages {
    cv::Mat gray, valid;
    std::map<int, CoarseSearchImage> coarse;
    const CoarseSearchImage &downsample(int factor) {
        auto found = coarse.find(factor);
        if (found != coarse.end())
            return found->second;
        require(size_t(gray.cols / factor) * (gray.rows / factor) <= MaxCoarseImagePixels,
                JPV_RESOURCE_LIMIT, "Fast matching coarse image budget exceeded.");
        CoarseSearchImage image;
        cv::resize(gray, image.gray, {gray.cols / factor, gray.rows / factor}, 0, 0, cv::INTER_AREA);
        if (!valid.empty()) {
            cv::resize(valid, image.valid, image.gray.size(), 0, 0, cv::INTER_AREA);
            cv::threshold(image.valid, image.valid, 254, 255, cv::THRESH_BINARY);
        }
        return coarse.emplace(factor, std::move(image)).first->second;
    }
};
static TemplateModel scaled_fast_model(const TemplateModel &model, double scale) {
    if (scale == 1)
        return model;
    int width = int(std::lround(model.gray.cols * scale)),
        height = int(std::lround(model.gray.rows * scale));
    require(width > 0 && height > 0 && width <= 1024 && height <= 1024, JPV_RESOURCE_LIMIT,
            "Scaled fast matching template exceeds 1024 pixels per side.");
    TemplateModel scaled;
    cv::resize(model.gray, scaled.gray, {width, height}, 0, 0, cv::INTER_LINEAR);
    cv::resize(model.mask, scaled.mask, scaled.gray.size(), 0, 0, cv::INTER_NEAREST);
    cv::Scalar mean, deviation;
    cv::meanStdDev(scaled.gray, mean, deviation, scaled.mask);
    // Resizing may erase a sparse mask or its contrast. Such a pose has no valid NCC score;
    // never let OpenCV's constant-template score produce a false positive.
    if (cv::countNonZero(scaled.mask) < 16 || deviation[0] < 1)
        return {};
    return scaled;
}
static std::vector<TranslationCandidate> fast_candidates(FastSearchImages &images,
                                                        const TemplateModel &model, const Json &p,
                                                        FastSearchBudget &budget) {
    const auto &gray = images.gray;
    if (gray.cols < model.gray.cols || gray.rows < model.gray.rows)
        return {};
    int factor = p.at("searchDownsample"), limit = p.at("candidateLimit");
    if (factor == 0) {
        factor = 1;
        while (factor < 8 && std::min(model.gray.cols, model.gray.rows) / (factor * 2) >= 16 &&
               gray.total() / (factor * factor) > 512ULL * 512)
            factor *= 2;
    }
    // A small model cannot retain enough contrast after coarse downsampling.
    while (factor > 1 && std::min(model.gray.cols, model.gray.rows) / factor < 8)
        factor /= 2;
    std::vector<TranslationCandidate> candidates;
    candidates.reserve(limit);
    double threshold = p.at("scoreThreshold");
    auto collect = [&](cv::Mat scores, cv::Point origin, cv::Size size, double minimum, bool coarse) {
        for (int i = 0; i < limit; ++i) {
            double score;
            cv::Point point;
            cv::minMaxLoc(scores, nullptr, &score, nullptr, &point);
            if (!std::isfinite(score) || score < minimum)
                break;
            auto at = point + origin;
            if (coarse) {
                int margin = factor * 2;
                cv::Rect patch(std::max(0, at.x * factor - margin), std::max(0, at.y * factor - margin), 0,
                               0);
                patch.width = std::min(gray.cols, at.x * factor + model.gray.cols + margin) - patch.x;
                patch.height = std::min(gray.rows, at.y * factor + model.gray.rows + margin) - patch.y;
                if (patch.width >= model.gray.cols && patch.height >= model.gray.rows) {
                    cv::Point refined;
                    auto fine = translation_scores(
                        gray(patch), images.valid.empty() ? cv::Mat() : images.valid(patch), model, budget);
                    cv::minMaxLoc(fine, nullptr, &score, nullptr, &refined);
                    if (std::isfinite(score) && score >= threshold)
                        retain_candidate(candidates, {refined + patch.tl(), score}, limit, model.gray.size(),
                                         p.at("overlapThreshold"));
                }
            } else {
                retain_candidate(candidates, {at, score}, limit, model.gray.size(), p.at("overlapThreshold"));
            }
            suppress_peak(scores, point, size);
        }
    };
    if (factor > 1) {
        TemplateModel coarse;
        const auto &image = images.downsample(factor);
        cv::resize(model.gray, coarse.gray, {model.gray.cols / factor, model.gray.rows / factor}, 0, 0,
                   cv::INTER_AREA);
        cv::resize(model.mask, coarse.mask, coarse.gray.size(), 0, 0, cv::INTER_NEAREST);
        cv::Scalar mean, deviation;
        cv::meanStdDev(coarse.gray, mean, deviation, coarse.mask);
        if (deviation[0] >= 1 && cv::countNonZero(coarse.mask) >= 16)
            translation_tiles(image.gray, image.valid, coarse, budget, [&](cv::Mat scores, cv::Point origin) {
                collect(std::move(scores), origin, coarse.gray.size(), 0.1, true);
            });
    }
    bool fallback = factor > 1 && candidates.empty();
    if (factor == 1 || fallback)
        translation_tiles(gray, images.valid, model, budget, [&](cv::Mat scores, cv::Point origin) {
            collect(std::move(scores), origin, model.gray.size(), threshold, false);
        });
    return candidates;
}
static Features fast_translation(const Image &source, const Json &p, bool single) {
    require(source.pixels.total() <= MaxFastImagePixels, JPV_RESOURCE_LIMIT,
            "Fast matching accepts at most 32 megapixels.");
    auto models = read_fast_models(p.at("model"), true);
    auto scales = fast_scales(p, models.size());
    // Gray conversion and each coarse input level are shared by all templates and scales.
    FastSearchImages images{gray_pixels(source), source.valid, {}};
    Features out;
    double minimumPixel, maximumPixel;
    cv::minMaxLoc(images.gray, &minimumPixel, &maximumPixel, nullptr, nullptr, source.valid);
    // A small, high-contrast object can have very little effect on full-image variance.
    // Only skip an actually constant effective input, leaving local contrast checks to NCC.
    if (maximumPixel - minimumPixel < 1)
        return out;
    bool bank = is_template_bank(p.at("model"));
    int maximum = bank || single ? 1 : p.at("maximumMatches").get<int>();
    std::vector<MatchFeature> candidates;
    FastSearchBudget budget;
    for (const auto &entry : models)
        for (double scale : scales) {
            auto model = scaled_fast_model(entry.model, scale);
            if (model.gray.empty())
                continue;
            auto found = fast_candidates(images, model, p, budget);
            // A bank produces one best identity, so weaker matches for the same pose cannot win.
            if (bank && found.size() > 1)
                found.resize(1);
            for (const auto &[point, score] : found) {
                MatchFeature match{double(source.x + point.x),
                                   double(source.y + point.y),
                                   double(model.gray.cols),
                                   double(model.gray.rows),
                                   source.x + point.x + model.gray.cols / 2.0,
                                   source.y + point.y + model.gray.rows / 2.0,
                                   0,
                                   scale,
                                   score,
                                   {},
                                   entry.id,
                                   entry.name};
                match.corners = {{{match.x, match.y},
                                  {match.x + match.width, match.y},
                                  {match.x + match.width, match.y + match.height},
                                  {match.x, match.y + match.height}}};
                candidates.push_back(std::move(match));
            }
        }
    // Generation is bank order, increasing scale, then deterministic score/y/x for each pose.
    // A stable sort preserves that ordering when scores are exactly equal.
    std::stable_sort(candidates.begin(), candidates.end(),
                     [](const auto &a, const auto &b) { return a.score > b.score; });
    for (const auto &match : candidates) {
        bool duplicate = std::any_of(out.items.begin(), out.items.end(), [&](const auto &kept) {
            return overlap(match, std::get<MatchFeature>(kept)) > p.at("overlapThreshold").get<double>();
        });
        if (!duplicate)
            out.items.emplace_back(match);
        if (out.items.size() >= size_t(maximum))
            break;
    }
    return out;
}
void register_template_components(ComponentRegistry &registry) {
    for (bool single : {false, true}) {
        auto params = Json::array({parameter("model", "模板模型", "template", Json::object()),
                                   parameter("scoreThreshold", "最小匹配分数", "number", .8, .05, 1),
                                   parameter("searchDownsample", "粗搜索倍数（0=自动）", "integer", 0, 0, 8),
                                   parameter("candidateLimit", "最大候选数", "integer", 64, 8, 256),
                                   parameter("overlapThreshold", "最大重叠比例", "number", .2, 0, .9),
                                   parameter("scaleMinimum", "最小模板尺度", "number", 1.0, .5, 2),
                                   parameter("scaleMaximum", "最大模板尺度", "number", 1.0, .5, 2),
                                   parameter("scaleStep", "模板尺度步长", "number", .05, .01, 1)});
        if (!single)
            params.push_back(parameter("maximumMatches", "最大目标数量", "integer", 10, 1, 100));
        auto definition = component(
            single ? "FastLocate" : "FastTemplateMatch", single ? "快速定位" : "快速匹配", "定位", "Image",
            "Features", "features", params,
            [single](const Value *input, const Json &p, const Image &) -> Value {
                return fast_translation(std::get<Image>(*input), p, single);
            },
            [](const Json &p) {
                auto models = read_fast_models(p.at("model"), true);
                fast_scales(p, models.size());
                int factor = p.at("searchDownsample");
                require(factor == 0 || factor == 1 || factor == 2 || factor == 4 || factor == 8,
                        JPV_INVALID_RECIPE, "Search downsample must be 0, 1, 2, 4 or 8.");
            });
        definition.descriptor["featureKind"] = "match";
        registry.add(std::move(definition));
    }
    registry.add(component(
        "TemplateMatch", "灰度模板匹配", "定位", "Image", "Features", "features",
        Json::array({parameter("model", "模板模型", "template", Json::object()),
                     parameter("scoreThreshold", "最小匹配分数", "number", .8, .05, 1),
                     parameter("maximumMatches", "最大目标数量", "integer", 10, 1, 100),
                     parameter("overlapThreshold", "最大重叠比例", "number", .2, 0, .9),
                     parameter("angleMinimum", "最小角度（°）", "number", 0.0, -180, 180),
                     parameter("angleMaximum", "最大角度（°）", "number", 0.0, -180, 180),
                     parameter("angleStep", "角度步长（°）", "number", 1.0, .25, 180),
                     parameter("scaleMinimum", "最小尺度", "number", 1.0, .5, 2),
                     parameter("scaleMaximum", "最大尺度", "number", 1.0, .5, 2),
                     parameter("scaleStep", "尺度步长", "number", .1, .01, 1)}),
        [](const Value *input, const Json &p, const Image &) -> Value {
            const auto &source = std::get<Image>(*input);
            int count = pose_count(p);
            require(source.pixels.total() <= 4ULL * 1024 * 1024 &&
                        source.pixels.total() * count <= 64ULL * 1024 * 1024,
                    JPV_RESOURCE_LIMIT, "Template search budget exceeded; reduce ROI or angle/scale range.");
            auto model = decode_model(p.at("model"));
            cv::Mat image, squared, valid;
            gray_pixels(source).convertTo(image, CV_32F);
            squared = image.mul(image);
            if (!source.valid.empty())
                source.valid.convertTo(valid, CV_32F, 1.0 / 255);
            int maxMatches = p.at("maximumMatches");
            std::vector<MatchFeature> candidates;
            double minimumAngle = p.at("angleMinimum"), maximumAngle = p.at("angleMaximum"),
                   angleStep = p.at("angleStep"), minimumScale = p.at("scaleMinimum"),
                   maximumScale = p.at("scaleMaximum"), scaleStep = p.at("scaleStep"),
                   threshold = p.at("scoreThreshold");
            for (double scale = minimumScale; scale <= maximumScale + 1e-8; scale += scaleStep)
                for (double angle = minimumAngle; angle <= maximumAngle + 1e-8; angle += angleStep) {
                    auto pose = transform_template(model, angle, scale);
                    if (pose.gray.cols > image.cols || pose.gray.rows > image.rows ||
                        cv::countNonZero(pose.mask) < 16)
                        continue;
                    auto scores = ncc(image, squared, valid, pose);
                    for (int i = 0; i < maxMatches * 4; ++i) {
                        double score;
                        cv::Point at;
                        cv::minMaxLoc(scores, nullptr, &score, nullptr, &at);
                        if (score < threshold)
                            break;
                        MatchFeature match{double(source.x + at.x),
                                           double(source.y + at.y),
                                           double(pose.gray.cols),
                                           double(pose.gray.rows),
                                           source.x + at.x + pose.gray.cols / 2.0,
                                           source.y + at.y + pose.gray.rows / 2.0,
                                           angle,
                                           scale,
                                           score,
                                           pose.corners};
                        for (auto &point : match.corners)
                            point += cv::Point2d(match.x, match.y);
                        candidates.push_back(std::move(match));
                        int rx = std::max(1, pose.gray.cols / 3), ry = std::max(1, pose.gray.rows / 3);
                        cv::Rect exclude(std::max(0, at.x - rx), std::max(0, at.y - ry), 0, 0);
                        exclude.width = std::min(scores.cols, at.x + rx + 1) - exclude.x;
                        exclude.height = std::min(scores.rows, at.y + ry + 1) - exclude.y;
                        scores(exclude).setTo(-1);
                    }
                }
            std::stable_sort(candidates.begin(), candidates.end(),
                             [](const auto &a, const auto &b) { return a.score > b.score; });
            Features out;
            double allowed = p.at("overlapThreshold");
            for (const auto &candidate : candidates) {
                bool duplicate = false;
                for (const auto &kept : out.items)
                    if (overlap(candidate, std::get<MatchFeature>(kept)) > allowed) {
                        duplicate = true;
                        break;
                    }
                if (!duplicate)
                    out.items.emplace_back(candidate);
                if (out.items.size() >= size_t(maxMatches))
                    break;
            }
            return out;
        },
        [](const Json &p) {
            require(!is_template_bank(p.at("model")), JPV_INVALID_RECIPE,
                    "Template banks are supported by FastTemplateMatch and FastLocate only.");
            validate_template(p.at("model"), true);
            pose_count(p);
        }));
}
} // namespace jp

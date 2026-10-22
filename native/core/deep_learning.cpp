#define ORT_API_MANUAL_INIT
#include "deep_learning.h"
#include "onnxruntime/onnxruntime_cxx_api.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <mutex>
#include <shlobj.h>
#include <opencv2/imgproc.hpp>

namespace jp {
namespace {
constexpr size_t MaxModelBytes = 512ULL * 1024 * 1024;
constexpr uint64_t MaxPixels = 32ULL * 1024 * 1024;
bool hash_text(const std::string &value) {
    return value.size() == 64 && std::all_of(value.begin(), value.end(), [](char c) {
               return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
           });
}
void require_plain_path(const fs::path &path) {
    // Check every existing component; model bundles must never follow directory junctions.
    fs::path part;
    for (const auto &name : path) {
        part /= name;
        DWORD attributes = GetFileAttributesW(part.c_str());
        require(attributes != INVALID_FILE_ATTRIBUTES, JPV_INVALID_ARGUMENT,
                "DeepLearningModelMissing: model repository or bundle does not exist.");
        require(!(attributes & FILE_ATTRIBUTE_REPARSE_POINT), JPV_INVALID_ARGUMENT,
                "DeepLearningModelInvalid: reparse points are not accepted in model paths.");
    }
}
fs::path repository(const Json &p) {
    auto root = p.at("modelRoot").get<std::string>();
    fs::path path;
    if (!root.empty()) {
        path = path_utf8(root);
        require(path.is_absolute(), JPV_INVALID_ARGUMENT,
                "DeepLearningModelInvalid: modelRoot must be an absolute directory.");
    } else {
        PWSTR local = nullptr;
        HRESULT hr = SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local);
        require(SUCCEEDED(hr) && local, JPV_INTERNAL_ERROR, "Cannot locate local model repository.");
        path = fs::path(local) / L"JP-Vision" / L"Models";
        CoTaskMemFree(local);
    }
    return fs::absolute(path).lexically_normal();
}
void validate_manifest(const Json &m) {
    auto invalid = [](bool ok) {
        require(ok, JPV_INVALID_ARGUMENT,
                "DeepLearningModelInvalid: unsupported or malformed model manifest.");
    };
    invalid(m.is_object() && m.at("schemaVersion").is_number_integer() && m.at("schemaVersion") == 1);
    for (const auto *name : {"modelId", "version"})
        invalid(m.at(name).is_string() && !m.at(name).get<std::string>().empty() &&
                m.at(name).get<std::string>().size() <= 128);
    invalid(m.at("task") == "binarySegmentation" || m.at("task") == "anomalySegmentation");
    invalid(m.at("modelFile") == "model.onnx" && m.at("modelSha256").is_string() &&
            hash_text(m.at("modelSha256")));
    const auto &input = m.at("input"), &output = m.at("output");
    invalid(input.at("layout") == "NCHW" && input.at("dtype") == "float32" &&
            output.at("layout") == "NCHW" && output.at("dtype") == "float32");
    for (const auto *axis : {"width", "height"})
        invalid(input.at(axis).is_number_integer() && input.at(axis) >= 32 && input.at(axis) <= 2048);
    invalid(input.at("channels").is_number_integer());
    invalid(input.at("channels") == 1 || input.at("channels") == 3);
    int channels = input.at("channels");
    invalid(channels == 1 || channels == 3);
    invalid(input.at("color") == (channels == 1 ? "Gray" : "RGB"));
    invalid(input.at("mean").is_array() && input.at("mean").size() == size_t(channels) &&
            input.at("std").is_array() && input.at("std").size() == size_t(channels));
    double scale = input.at("scale");
    invalid(std::isfinite(scale) && scale > 0 && scale <= 1);
    for (int i = 0; i < channels; ++i) {
        double mean = input.at("mean")[i], deviation = input.at("std")[i];
        invalid(std::isfinite(mean) && std::isfinite(deviation) && deviation > 0);
    }
    invalid(input.at("name").is_string() && !input.at("name").get<std::string>().empty() &&
            output.at("name").is_string() && !output.at("name").get<std::string>().empty());
    invalid(output.at("activation") == "none" || output.at("activation") == "sigmoid");
    invalid(output.at("scoreKind") == "probability" || output.at("scoreKind") == "anomaly");
    invalid((m.at("task") == "binarySegmentation" && output.at("scoreKind") == "probability") ||
            (m.at("task") == "anomalySegmentation" && output.at("scoreKind") == "anomaly" &&
             output.at("activation") == "none"));
    invalid(m.at("labels") == Json::array({"background", "defect"}));
}

struct OrtRuntime {
    HMODULE module = nullptr;
    // A manual-init ORT wrapper calls the API even when releasing nullptr. Do not create
    // one until InitApi succeeds, so an absent optional DLL can unwind safely.
    std::unique_ptr<Ort::Env> environment;
    OrtRuntime() {
        HMODULE own = nullptr;
        require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                      GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                  reinterpret_cast<LPCWSTR>(&infer_defect_scores), &own),
                JPV_INTERNAL_ERROR, "Cannot locate JP-Vision runtime module.");
        std::wstring filename(32768, L'\0');
        DWORD length = GetModuleFileNameW(own, filename.data(), DWORD(filename.size()));
        require(length && length < filename.size(), JPV_INTERNAL_ERROR, "Cannot locate runtime path.");
        filename.resize(length);
        auto path = fs::path(filename).parent_path() / L"onnxruntime.dll";
        module = LoadLibraryExW(path.c_str(), nullptr,
                                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        require(module != nullptr, JPV_INVALID_ARGUMENT,
                "DeepLearningRuntimeUnavailable: install the optional x64 ONNX Runtime beside "
                "JPVision.Runtime.dll.");
        auto entry = reinterpret_cast<const OrtApiBase *(ORT_API_CALL *)()>(
            GetProcAddress(module, "OrtGetApiBase"));
        const auto *api = entry ? entry()->GetApi(ORT_API_VERSION) : nullptr;
        if (!api) {
            FreeLibrary(module);
            module = nullptr;
            throw Error(JPV_INVALID_ARGUMENT,
                        "DeepLearningRuntimeUnavailable: incompatible ONNX Runtime C API.");
        }
        Ort::InitApi(api);
        environment = std::make_unique<Ort::Env>(ORT_LOGGING_LEVEL_WARNING, "JP-Vision");
    }
    // The library is intentionally retained until process exit: all cached sessions refer to it.
};
std::shared_ptr<OrtRuntime> runtime() {
    // Failed construction is retried after installation. Sessions retain environment ownership,
    // including during static teardown (the cache is initialized before the first environment).
    static auto instance = std::make_shared<OrtRuntime>();
    return instance;
}
struct Model {
    Json manifest;
    std::shared_ptr<OrtRuntime> engine;
    Ort::Session session{nullptr};
    Model(Json value, const std::string &bytes) : manifest(std::move(value)), engine(runtime()) {
        Ort::SessionOptions options;
        options.SetIntraOpNumThreads(2);
        options.SetInterOpNumThreads(1);
        options.SetExecutionMode(ExecutionMode::ORT_SEQUENTIAL);
        options.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        session = Ort::Session(*engine->environment, bytes.data(), bytes.size(), options);
        require(session.GetInputCount() == 1 && session.GetOutputCount() == 1,
                JPV_INVALID_ARGUMENT, "DeepLearningModelInvalid: exactly one input/output is required.");
        Ort::AllocatorWithDefaultOptions allocator;
        auto inputName = session.GetInputNameAllocated(0, allocator);
        auto outputName = session.GetOutputNameAllocated(0, allocator);
        const auto &input = manifest.at("input"), &output = manifest.at("output");
        require(input.at("name") == inputName.get() && output.at("name") == outputName.get(),
                JPV_INVALID_ARGUMENT, "DeepLearningModelInvalid: tensor names differ from manifest.");
        auto inputType = session.GetInputTypeInfo(0), outputType = session.GetOutputTypeInfo(0);
        auto inputInfo = inputType.GetTensorTypeAndShapeInfo();
        auto outputInfo = outputType.GetTensorTypeAndShapeInfo();
        std::vector<int64_t> inputShape{1, input.at("channels"), input.at("height"), input.at("width")};
        std::vector<int64_t> outputShape{1, 1, input.at("height"), input.at("width")};
        require(inputInfo.GetElementType() == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT &&
                    outputInfo.GetElementType() == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT &&
                    inputInfo.GetShape() == inputShape && outputInfo.GetShape() == outputShape,
                JPV_INVALID_ARGUMENT,
                "DeepLearningModelInvalid: float32 fixed NCHW tensors [1,C,H,W]/[1,1,H,W] required.");
    }
};
std::shared_ptr<Model> load_model(const Json &p) {
    const auto key = p.at("modelKey").get<std::string>();
    require(hash_text(key), JPV_INVALID_ARGUMENT,
            "DeepLearningModelMissing: select an imported model before running.");
    const auto directory = repository(p) / key;
    require_plain_path(directory / L"manifest.json");
    const auto text = read_file(directory / L"manifest.json", 65536, JPV_INVALID_ARGUMENT);
    require(sha256(text) == key, JPV_INVALID_ARGUMENT,
            "DeepLearningModelInvalid: pinned manifest SHA-256 does not match.");
    Json manifest = strict_json(text, JPV_INVALID_ARGUMENT);
    validate_manifest(manifest);
    const auto file = directory / L"model.onnx";
    require_plain_path(file);
    // Each cache entry is an immutable snapshot of verified bytes. Subsequent file edits cannot
    // silently replace this model. A different manifest hash always creates a different session.
    static std::mutex mutex;
    static std::string cachedPath, cachedKey;
    static std::shared_ptr<Model> cached;
    std::lock_guard lock(mutex);
    const auto path = path_string(directory);
    if (cached && path == cachedPath && key == cachedKey)
        return cached;
    auto bytes = read_file(file, MaxModelBytes, JPV_INVALID_ARGUMENT);
    require(!bytes.empty() && sha256(bytes) == manifest.at("modelSha256").get<std::string>(),
            JPV_INVALID_ARGUMENT,
            "DeepLearningModelInvalid: model SHA-256 does not match the manifest.");
    auto model = std::make_shared<Model>(std::move(manifest), bytes);
    cached = model;
    cachedPath = path;
    cachedKey = key;
    return model;
}
std::vector<int> tile_positions(int extent, int tile, int overlap) {
    std::vector<int> positions;
    if (extent <= tile)
        return {0};
    for (int at = 0; at < extent - tile; at += tile - overlap)
        positions.push_back(at);
    positions.push_back(extent - tile);
    return positions;
}
} // namespace

DefectInference infer_defect_scores(const Image &source, const Json &parameters) {
    try {
        require(source.available && !source.pixels.empty() &&
                    (source.pixels.type() == CV_8UC1 || source.pixels.type() == CV_8UC3),
                JPV_INVALID_ARGUMENT, "DeepLearningInputInvalid: Gray8/Bgr24 input is required.");
        require(source.pixels.total() <= MaxPixels, JPV_RESOURCE_LIMIT,
                "DeepLearningInputInvalid: image exceeds 32 Mi pixels.");
        auto model = load_model(parameters);
        const auto &input = model->manifest.at("input"), &output = model->manifest.at("output");
        const int width = input.at("width"), height = input.at("height"), channels = input.at("channels");
        const int overlap = parameters.at("overlap");
        require(overlap < width && overlap < height, JPV_INVALID_ARGUMENT,
                "DeepLearningTileInvalid: overlap must be smaller than both model dimensions.");
        auto xs = tile_positions(source.pixels.cols, width, overlap);
        auto ys = tile_positions(source.pixels.rows, height, overlap);
        require(xs.size() * ys.size() <= 4096, JPV_RESOURCE_LIMIT,
                "DeepLearningTileInvalid: more than 4096 tiles; reduce ROI or use a larger model tile.");
        cv::Mat converted;
        if (channels == 1) {
            if (source.pixels.type() == CV_8UC1)
                converted = source.pixels;
            else
                cv::cvtColor(source.pixels, converted, cv::COLOR_BGR2GRAY);
        } else if (source.pixels.type() == CV_8UC1)
            cv::cvtColor(source.pixels, converted, cv::COLOR_GRAY2RGB);
        else
            cv::cvtColor(source.pixels, converted, cv::COLOR_BGR2RGB);
        cv::Mat sums = cv::Mat::zeros(source.pixels.size(), CV_32FC1);
        cv::Mat counts = cv::Mat::zeros(source.pixels.size(), CV_16UC1);
        const size_t plane = size_t(width) * height;
        std::vector<float> tensor(plane * channels);
        const std::array<int64_t, 4> shape{1, channels, height, width};
        auto memory = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
        const auto inputName = input.at("name").get<std::string>();
        const auto outputName = output.at("name").get<std::string>();
        const char *inputNames[]{inputName.c_str()}, *outputNames[]{outputName.c_str()};
        auto value = Ort::Value::CreateTensor<float>(memory, tensor.data(), tensor.size(), shape.data(), 4);
        const double scale = input.at("scale");
        const bool sigmoid = output.at("activation") == "sigmoid";
        const bool probability = output.at("scoreKind") == "probability";
        require(!probability || parameters.at("threshold").get<double>() <= 1,
                JPV_INVALID_ARGUMENT,
                "DeepLearningThresholdInvalid: probability threshold must be between zero and one.");
        int tiles = 0;
        for (int top : ys)
            for (int left : xs) {
                int realWidth = std::min(width, converted.cols - left);
                int realHeight = std::min(height, converted.rows - top);
                // Pad with black in pixel space, then normalize exactly as real image pixels.
                for (int c = 0; c < channels; ++c) {
                    const double mean = input.at("mean")[c], deviation = input.at("std")[c];
                    float *target = tensor.data() + c * plane;
                    for (int y = 0; y < height; ++y) {
                        const auto *row = y < realHeight ? converted.ptr<uint8_t>(top + y) : nullptr;
                        for (int x = 0; x < width; ++x) {
                            int pixel = row && x < realWidth ? row[(left + x) * channels + c] : 0;
                            double normalized = (pixel * scale - mean) / deviation;
                            if (!std::isfinite(normalized) ||
                                std::abs(normalized) > std::numeric_limits<float>::max())
                                throw Error(JPV_INVALID_ARGUMENT,
                                            "DeepLearningModelInvalid: preprocessing overflows float32.");
                            target[size_t(y) * width + x] = float(normalized);
                        }
                    }
                }
                auto values = model->session.Run(Ort::RunOptions{nullptr}, inputNames, &value, 1,
                                                  outputNames, 1);
                const auto info = values[0].GetTensorTypeAndShapeInfo();
                require(info.GetElementType() == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT &&
                            info.GetShape() == std::vector<int64_t>({1, 1, height, width}),
                        JPV_INVALID_ARGUMENT, "DeepLearningOutputInvalid: tensor shape changed at runtime.");
                const float *scores = values[0].GetTensorData<float>();
                for (int y = 0; y < height; ++y)
                    for (int x = 0; x < width; ++x) {
                        double score = scores[size_t(y) * width + x];
                        if (!std::isfinite(score))
                            throw Error(JPV_INVALID_ARGUMENT,
                                        "DeepLearningOutputInvalid: NaN or infinite output.");
                        if (sigmoid)
                            score = score >= 0 ? 1 / (1 + std::exp(-score))
                                               : std::exp(score) / (1 + std::exp(score));
                        if (score < 0 || (probability && score > 1))
                            throw Error(JPV_INVALID_ARGUMENT,
                                        "DeepLearningOutputInvalid: score lies outside its declared range.");
                        if (x < realWidth && y < realHeight &&
                            (source.valid.empty() || source.valid.at<uint8_t>(top + y, left + x))) {
                            sums.at<float>(top + y, left + x) += float(score);
                            ++counts.at<uint16_t>(top + y, left + x);
                        }
                    }
                ++tiles;
            }
        for (int y = 0; y < sums.rows; ++y) {
            auto *row = sums.ptr<float>(y);
            const auto *weights = counts.ptr<uint16_t>(y);
            for (int x = 0; x < sums.cols; ++x) {
                row[x] = weights[x] ? row[x] / weights[x] : 0;
                if (!std::isfinite(row[x]))
                    throw Error(JPV_INVALID_ARGUMENT,
                                "DeepLearningOutputInvalid: stitched score overflows float32.");
            }
        }
        return {model->manifest, std::move(sums), tiles};
    } catch (const Error &) {
        throw;
    } catch (const Ort::Exception &error) {
        throw Error(JPV_INVALID_ARGUMENT, std::string("DeepLearningInferenceFailed: ") + error.what());
    } catch (const Json::exception &error) {
        throw Error(JPV_INVALID_ARGUMENT, std::string("DeepLearningModelInvalid: ") + error.what());
    } catch (const fs::filesystem_error &error) {
        throw Error(JPV_INVALID_ARGUMENT, std::string("DeepLearningModelInvalid: ") + error.what());
    }
}
} // namespace jp

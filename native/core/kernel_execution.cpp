#include "kernel_execution.h"
#include "components/image_components.h"
#include "image_pipeline.h"
#include "parameter_bindings.h"
#include "roi.h"
#include <algorithm>
#include <opencv2/imgproc.hpp>

namespace jp {
namespace {
constexpr size_t MaxDebugBytes = 256ULL * 1024 * 1024;
constexpr size_t MaxMeasurementBytes = 32ULL * 1024 * 1024;
using Clock = std::chrono::steady_clock;
double milliseconds(Clock::duration elapsed) {
    return std::chrono::duration<double, std::milli>(elapsed).count();
}
struct EncodedPort {
    const Json *existing = nullptr;
    std::optional<Json> generated;
    std::optional<size_t> length;
    explicit EncodedPort(const Value &value) {
        existing = std::get_if<Json>(&value);
        if (!existing) generated = serialize_value(value);
    }
    const Json &json() const { return existing ? *existing : *generated; }
    size_t bytes() {
        if (!length) length = json().dump().size();
        return *length;
    }
    Json take() { return existing ? *existing : std::move(*generated); }
};
} // namespace

KernelContext::KernelContext(std::shared_ptr<Run> run) : run_(std::move(run)), result_(std::make_shared<Result>()) {
    require(!run_->started.exchange(true), JPV_DISPOSED, "Run has already executed.");
    result_->license = run_->session->recipe->engine->license;
}
KernelContext::~KernelContext() { run_->finish(); }
void KernelContext::check() const {
    require(!finished_, JPV_DISPOSED, "Kernel run has finished.");
    require(!run_->cancel, JPV_CANCELLED, "Inspection cancelled.");
    int timeout = run_->request.value("timeoutMs", 0);
    require(timeout == 0 || Clock::now() - run_->accepted < std::chrono::milliseconds(timeout),
            JPV_TIMED_OUT, "Inspection deadline exceeded.");
}
Json KernelContext::info() const {
    std::lock_guard lock(gate_);
    return {{"inputs", run_->parameters}, {"variables", run_->variables},
            {"runtimeVersion", JPV_VERSION}, {"opencvVersion", CV_VERSION},
            {"acceptedElapsedMs", milliseconds(Clock::now() - run_->accepted)}};
}
InputValues KernelContext::inputs(const std::string &node) const {
    const auto &plan = run_->session->recipe->plan;
    require(plan.nodes.contains(node), JPV_INVALID_ARGUMENT, "Unknown kernel node.");
    InputValues result;
    for (const auto &[port, source] : plan.incoming.at(node)) {
        require(values_.contains(source.first) && values_.at(source.first).contains(source.second),
                JPV_INVALID_ARGUMENT, "Kernel input is not available in this run.");
        result.emplace(port, &values_.at(source.first).at(source.second));
    }
    return result;
}
const Image &KernelContext::parameter_image(const InputValues &values) const {
    if (values.contains("input")) {
        if (auto image = std::get_if<Image>(values.at("input"))) return *image;
        if (auto features = std::get_if<Features>(values.at("input")); features && features->sourceImage)
            return *features->sourceImage;
        if (auto contours = std::get_if<Contours>(values.at("input")); contours && contours->sourceImage)
            return *contours->sourceImage;
    }
    return *run_->frame;
}
Json KernelContext::input_metadata(const std::string &node) const {
    std::lock_guard lock(gate_);
    check();
    const auto &image = parameter_image(inputs(node));
    return {{"originX", image.x}, {"originY", image.y}, {"coordinateSpace", image.coordinateSpace},
            {"coordinatePlane", image.coordinatePlane}, {"hasFixture", bool(image.fixture)},
            {"hasMatchPosition", bool(image.matchPosition)}};
}
Json KernelContext::field(const std::string &node, const std::string &port, const std::string &path) const {
    std::lock_guard lock(gate_);
    check();
    require(values_.contains(node) && values_.at(node).contains(port), JPV_INVALID_ARGUMENT,
            "Kernel result field is not available in this run.");
    return result_field(values_.at(node).at(port), path);
}
void KernelContext::account_image(const Image &image) {
    for (const auto *matrix : {&image.pixels, &image.valid}) {
        if (matrix->empty()) continue;
        size_t bytes = size_t(matrix->dataend - matrix->datastart);
        auto &accounted = imageBuffers_[matrix->datastart];
        if (bytes > accounted) {
            debugBytes_ += bytes - accounted;
            accounted = bytes;
        }
    }
    image_context_payloads(image, [&](const void *identity, size_t bytes) {
        if (contextPayloads_.insert(identity).second) debugBytes_ += bytes;
    });
}

Json KernelContext::step(const std::string &id, Json parameters) {
    std::lock_guard lock(gate_);
    check();
    result_->license->check();
    const auto start = Clock::now();
    const auto &plan = run_->session->recipe->plan;
    require(plan.nodes.contains(id) && std::find(plan.order.begin(), plan.order.end(), id) != plan.order.end(),
            JPV_INVALID_ARGUMENT, "Kernel node is not part of this execution.");
    require(!completed_.contains(id), JPV_INVALID_ARGUMENT, "Kernel node has already executed.");
    require(parameters.is_object(), JPV_INVALID_ARGUMENT, "Kernel parameters must be an object.");
    const auto &entry = component_registry().get(plan.nodes.at(id).at("type"));
    for (const auto &spec : entry.descriptor.at("parameters")) {
        const auto name = spec.at("name").get<std::string>();
        require(parameters.contains(name), JPV_INVALID_RECIPE, "Missing resolved kernel parameter: " + name);
        if (spec.at("type") != "roi" || !parameters.contains("_unavailable"))
            validate_parameter(spec, parameters.at(name));
    }
    if (entry.validate && !parameters.contains("_unavailable")) entry.validate(parameters);
    auto input = inputs(id);
    for (const auto &[port, source] : plan.incoming.at(id))
        if (skipped_.contains(source.first) || unavailable_data(values_.at(source.first).at(source.second))) {
            if (!parameters.contains("_unavailable")) parameters["_unavailable"] = Json::array();
            parameters["_unavailable"].push_back("input:" + port);
        }
    auto prepared = prepare_image_inputs(entry, input, parameters, *run_->frame);
    input = prepared.inputs;
    for (const auto &[port, value] : input)
        if (unavailable_data(*value)) {
            if (!parameters.contains("_unavailable")) parameters["_unavailable"] = Json::array();
            parameters["_unavailable"].push_back("input:" + port);
        }
    bool skipped = parameters.contains("_unavailable");
    const Value *first = input.empty() ? nullptr : input.begin()->second;
    const auto executeStart = Clock::now();
    Value computed = skipped && !entry.descriptor.value("handlesUnavailableParameters", false)
                         ? skipped_value(entry, input, *run_->frame)
                     : entry.executeInputs ? entry.executeInputs(input, parameters, *run_->frame)
                                           : entry.execute(first, parameters, *run_->frame);
    const auto executeEnd = Clock::now();
    skipped = skipped || unavailable_data(computed);
    check();
    auto ports = entry.expandInputs ? entry.expandInputs(computed, parameters, input)
                 : entry.expand ? entry.expand(computed, parameters) : PortValues{};
    check();
    const auto primary = entry.descriptor.at("outputs")[0].at("id").get<std::string>();
    ports.emplace(primary, std::move(computed));
    complete_image_pipeline(id, entry, input, ports, parameters, *run_->frame);
    const auto &output = ports.at(primary);
    std::map<std::string, EncodedPort> serialized;
    auto encoded = [&](const std::string &name) -> EncodedPort & {
        return serialized.try_emplace(name, ports.at(name)).first->second;
    };
    if (entry.descriptor.at("outputs")[0].at("type") == "Result") {
        debugBytes_ += encoded(primary).bytes();
        require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Result packet budget exceeded.");
    }
    require(ports.size() == entry.descriptor.at("outputs").size(), JPV_INTERNAL_ERROR,
            "Component did not produce all declared outputs.");
    if (auto features = std::get_if<Features>(&output); features && features->sourceImage)
        account_image(*features->sourceImage);
    if (ports.contains("image")) account_image(std::get<Image>(ports.at("image")));
    require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Associated image budget exceeded.");
    if (ports.contains("result")) {
        debugBytes_ += encoded("result").bytes();
        require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Result packet budget exceeded.");
    }
    if (ports.contains("measurement")) {
        measurementBytes_ += encoded("measurement").bytes();
        require(measurementBytes_ <= MaxMeasurementBytes, JPV_RESOURCE_LIMIT, "Measurement result budget exceeded.");
    }
    check();
    const bool retained = run_->request.value("debug", false) || id == run_->request.value("previewNodeId", "");
    Json response = Json::object();
    if (retained) {
        for (const auto &[port, value] : input)
            if (auto image = std::get_if<Image>(value); image && port == "input") {
                account_image(*image);
                result_->inputImages[id] = *image;
                break;
            }
        debugBytes_ += encoded(primary).bytes();
        if (auto image = std::get_if<Image>(&output)) {
            account_image(*image);
            result_->images[id] = *image;
        }
        if (ports.contains("image")) {
            const auto &image = std::get<Image>(ports.at("image"));
            account_image(image);
            result_->images[id] = image;
        }
        if (auto features = std::get_if<Features>(&output)) {
            if (features->sourceImage) result_->inputImages[id] = *features->sourceImage;
            if (ports.contains("overlay")) debugBytes_ += encoded("overlay").bytes();
            else {
                auto overlay = feature_overlays(*features);
                debugBytes_ += overlay.dump().size();
                response["debugOverlay"] = std::move(overlay);
            }
        }
        require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Debug result budget exceeded.");
    }
    Json previews = Json::object();
    for (const auto &spec : entry.descriptor.at("outputs")) {
        const auto name = spec.at("id").get<std::string>(), type = spec.at("type").get<std::string>();
        bool small = type == "Int" || type == "Double" || type == "Bool" || type == "String" ||
                     type == "Measurement" || type == "Decision" || type == "Pose";
        if (small || retained || type == "Image" || type == "Mask") {
            auto &value = encoded(name);
            encodedBytes_[{id, name}] = value.bytes();
            auto data = value.take();
            if (type == "Mask" && retained) {
                const auto &image = std::get<Image>(ports.at(name));
                data["foregroundPixels"] = image.available ? cv::countNonZero(visible_pixels(image)) : 0;
            }
            if (retained || type == "Image" || type == "Mask")
                debugBytes_ += type == "Mask" && retained ? data.dump().size() : value.bytes();
            previews[name] = std::move(data);
        }
    }
    require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Node preview budget exceeded.");
    response["ports"] = std::move(previews);
    response["unavailable"] = parameters.value("_unavailable", Json::array());
    response["skipped"] = skipped;
    response["prepareMs"] = milliseconds(executeStart - start);
    response["executeMs"] = milliseconds(executeEnd - executeStart);
    response["resultMs"] = milliseconds(Clock::now() - executeEnd);
    if (auto use = plan.uses.find(id); use != plan.uses.end() && use->second > 0)
        values_[id] = std::move(ports);
    completed_.insert(id);
    if (skipped) skipped_.insert(id);
    check();
    return response;
}

Json KernelContext::publish(const std::string &name, bool includeJson) {
    std::lock_guard lock(gate_);
    check();
    const auto &outputs = run_->session->recipe->plan.recipe.at("outputs");
    require(outputs.contains(name), JPV_INVALID_ARGUMENT, "Unknown public output.");
    const auto &spec = outputs.at(name);
    const auto node = spec.at("node").get<std::string>(), port = spec.at("port").get<std::string>();
    require(values_.contains(node) && values_.at(node).contains(port), JPV_INVALID_ARGUMENT,
            "Public output is not available in this run.");
    const auto &value = values_.at(node).at(port);
    const auto type = spec.at("type").get<std::string>();
    Json data;
    const auto key = std::pair{node, port};
    if (includeJson || !encodedBytes_.contains(key)) data = serialize_value(value);
    if (!encodedBytes_.contains(key)) encodedBytes_[key] = data.dump().size();
    if (type == "Result" || type == "Image" || type == "Mask") {
        debugBytes_ += encodedBytes_.at(key);
        require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Public result packet budget exceeded.");
    }
    if (auto image = std::get_if<Image>(&value)) {
        account_image(*image);
        result_->outputImages[name] = *image;
    } else if (type == "Result" && values_.at(node).contains("image")) {
        const auto &projection = std::get<Image>(values_.at(node).at("image"));
        account_image(projection);
        result_->outputImages[name] = projection;
    }
    require(debugBytes_ <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Public image budget exceeded.");
    check();
    return includeJson ? std::move(data) : Json();
}
void KernelContext::release_node(const std::string &id) {
    std::lock_guard lock(gate_);
    check();
    values_.erase(id);
}
std::shared_ptr<Result> KernelContext::finish(bool failed) {
    std::lock_guard lock(gate_);
    require(!finished_, JPV_DISPOSED, "Kernel run has finished.");
    if (failed) result_->outputImages.clear();
    // The public-image getter also enforces run success on the native image container.
    result_->json = {{"executionStatus", failed ? "Failed" : "Succeeded"}};
    values_.clear();
    finished_ = true;
    run_->finish();
    return result_;
}
} // namespace jp

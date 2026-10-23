#include "graph.h"
#include "components/component.h"
#include "components/image_components.h"
#include "image_pipeline.h"
#include "roi.h"
#include "fixture.h"
#include "parameter_bindings.h"
#include "kernel_execution.h"
#include <algorithm>
#include <cmath>
#include <functional>
#include <opencv2/geometry.hpp>
#include <opencv2/imgproc.hpp>
#include <set>
#include <thread>

namespace jp {
static constexpr size_t MaxImageBytes = 128ULL * 1024 * 1024, MaxPixels = 32ULL * 1024 * 1024,
                        MaxDebugBytes = 256ULL * 1024 * 1024, MaxMeasurementBytes = 32ULL * 1024 * 1024;
static std::atomic<int> activeRuns{0};
int active_run_count() {
    return activeRuns.load();
}
static const int ParallelBudget = std::clamp(static_cast<int>(std::thread::hardware_concurrency() / 2), 1, 8);
Engine::Engine(const Json &options) {
    require(options.is_object(), JPV_INVALID_ARGUMENT, "Engine options must be an object.");
    license = std::make_shared<LicenseService>(options.value("licenseStore", ""));
    license->check();
    static std::once_flag initialized;
    std::call_once(initialized, [] { cv::setNumThreads(2); });
}
static const Json &descriptor(const std::string &type) {
    return component_registry().get(type).descriptor;
}
static std::string port_type(const Json &meta, const char *direction, const std::string &id) {
    for (const auto &port : meta.at(direction))
        if (port.at("id") == id)
            return port.at("type").get<std::string>();
    throw Error(JPV_INVALID_RECIPE, "Unknown " + std::string(direction) + " port: " + id);
}
static bool accepts_input(const Json &meta, const std::string &id, const std::string &type) {
    for (const auto &port : meta.at("inputs"))
        if (port.at("id") == id) {
            if (port.at("type") == type)
                return true;
            const auto accepted = port.value("acceptedTypes", Json::array());
            return std::find(accepted.begin(), accepted.end(), type) != accepted.end();
        }
    throw Error(JPV_INVALID_RECIPE, "Unknown inputs port: " + id);
}
void validate_parameter(const Json &p, const Json &value) {
    auto name = p.at("name").get<std::string>(), type = p.at("type").get<std::string>();
    if (type == "fixtureReference")
        validate_fixture_reference(value);
    else if (type == "roi")
        validate_roi(value);
    else if (type == "template")
        validate_template(value);
    else if (type == "integer")
        integer(value, name, p.at("min").get<int64_t>(), p.at("max").get<int64_t>());
    else if (type == "number")
        require(value.is_number() && std::isfinite(value.get<double>()) &&
                    value.get<double>() >= p.at("min").get<double>() &&
                    value.get<double>() <= p.at("max").get<double>(),
                JPV_INVALID_RECIPE, name + " is out of range.");
    else if (type == "bool")
        require(value.is_boolean(), JPV_INVALID_RECIPE, name + " must be Bool.");
    else {
        require(value.is_string() && value.get<std::string>().size() <= 256, JPV_INVALID_RECIPE,
                name + " must be a short string.");
        if (type == "enum") {
            bool found = false;
            for (const auto &choice : p.at("choices"))
                if (choice == value)
                    found = true;
            require(found, JPV_INVALID_RECIPE, name + " has unsupported value.");
        }
        if (name == "color" || name == "overlayColor") {
            auto color = value.get<std::string>();
            require(color.size() == 7 && color[0] == '#' &&
                        color.find_first_not_of("0123456789abcdefABCDEF", 1) == std::string::npos,
                    JPV_INVALID_RECIPE, "Color must be #RRGGBB.");
        }
    }
}
Json result_field(const Value &value, const std::string &field) {
    Json serialized;
    const Json *current = std::get_if<Json>(&value);
    if (!current) {
        serialized = serialize_value(value);
        current = &serialized;
    }
    size_t start = 0;
    while (start < field.size()) {
        auto end = field.find('.', start);
        auto key = field.substr(start, end == std::string::npos ? end : end - start);
        if (!current->is_object() || !current->contains(key))
            return nullptr;
        current = &current->at(key);
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return *current;
}
static Json resolved_parameters(const Json &node, const Json &publicValues, const Plan &plan,
                                const std::map<std::string, PortValues> *values = nullptr,
                                const Json *traces = nullptr, const Image *input = nullptr,
                                const Json *globals = nullptr, Json *locals = nullptr) {
    auto resolved =
        resolve_node_bindings(node, publicValues, plan, values, traces, input,
                              globals ? *globals : global_variable_values(plan.recipe), validate_parameter);
    auto parameters = std::move(resolved.parameters);
    const auto &entry = component_registry().get(node.at("type").get<std::string>());
    if (entry.validate && !resolved.deferred && !parameters.contains("_unavailable"))
        entry.validate(parameters);
    if (locals)
        *locals = std::move(resolved.locals);
    return parameters;
}
static Json defaults(const Json &recipe, const Json &provided, bool validateMissing) {
    require(provided.is_object(), JPV_INVALID_ARGUMENT, "Run inputs must be an object.");
    Json values = Json::object();
    for (auto it = provided.begin(); it != provided.end(); ++it)
        require(recipe.at("inputs").contains(it.key()) &&
                    recipe.at("inputs").at(it.key()).at("type") != "Image",
                JPV_INVALID_ARGUMENT, "Unknown scalar input: " + it.key());
    int imageCount = 0;
    for (auto it = recipe.at("inputs").begin(); it != recipe.at("inputs").end(); ++it) {
        const auto &definition = it.value();
        auto type = definition.at("type").get<std::string>();
        if (type == "Image") {
            ++imageCount;
            continue;
        }
        Json value =
            provided.contains(it.key()) ? provided.at(it.key()) : definition.value("default", Json());
        if (validateMissing && !provided.contains(it.key()) && !definition.contains("default") &&
            !definition.value("required", true))
            continue;
        if (value.is_null() && !validateMissing) {
            value = type == "Int"      ? Json(0)
                    : type == "Bool"   ? Json(false)
                    : type == "Double" ? Json(0.0)
                                       : Json("");
        }
        require(!value.is_null(), JPV_INVALID_ARGUMENT, "Missing public input: " + it.key());
        bool valid = (type == "Int" && value.is_number_integer()) ||
                     (type == "Double" && value.is_number() && std::isfinite(value.get<double>())) ||
                     (type == "Bool" && value.is_boolean()) ||
                     (type == "String" && value.is_string() && value.get<std::string>().size() <= 256);
        require(valid, JPV_INVALID_ARGUMENT, "Public input type mismatch: " + it.key());
        values[it.key()] = value;
    }
    require(imageCount == 1, JPV_INVALID_RECIPE, "This release requires exactly one public Image input.");
    return values;
}
static Json canonical_content(const Json &recipe) {
    Json nodes = recipe.at("nodes"), connections = recipe.at("connections");
    std::sort(nodes.begin(), nodes.end(), [](const Json &a, const Json &b) {
        return a.at("id").get<std::string>() < b.at("id").get<std::string>();
    });
    std::sort(connections.begin(), connections.end(),
              [](const Json &a, const Json &b) { return a.dump() < b.dump(); });
    Json content = {{"schemaVersion", recipe.at("schemaVersion")},
                    {"kind", recipe.at("kind")},
                    {"minimumRuntimeAbi", recipe.value("minimumRuntimeAbi", 1)},
                    {"nodes", nodes},
                    {"connections", connections},
                    {"inputs", recipe.at("inputs")},
                    {"outputs", recipe.at("outputs")},
                    {"decisionOutput", recipe.value("decisionOutput", "")},
                    {"resources", recipe.value("resources", Json::array())}};
    for (const char *key : {"variables", "functions"})
        if (recipe.contains(key))
            content[key] = recipe.at(key);
    if (recipe.contains("previewNodes")) {
        auto targets = recipe.at("previewNodes");
        require(targets.is_array(), JPV_INVALID_RECIPE, "Preview nodes must be an array.");
        std::sort(targets.begin(), targets.end());
        content["previewNodes"] = std::move(targets);
    }
    return content;
}
Json seal_recipe(Json recipe) {
    require(recipe.is_object() && recipe.contains("nodes") && recipe.at("nodes").is_array() &&
                recipe.contains("connections") && recipe.at("connections").is_array(),
            JPV_INVALID_RECIPE, "Recipe graph is missing.");
    recipe["contentHash"] = sha256(canonical_content(recipe).dump());
    return recipe;
}
Plan compile_recipe(Json recipe, const fs::path &root) {
    try {
        require(recipe.at("schemaVersion") == 1 && recipe.value("minimumRuntimeAbi", 1) == 1,
                JPV_INVALID_RECIPE, "Unsupported recipe/ABI version.");
        auto kind = recipe.at("kind").get<std::string>();
        require(kind == "inspection" || kind == "processing", JPV_INVALID_RECIPE,
                "Unsupported recipe kind; expected inspection or processing.");
        require(recipe.at("recipeId").is_string() && !recipe.at("recipeId").get<std::string>().empty() &&
                    recipe.at("recipeId").get<std::string>().size() <= 128,
                JPV_INVALID_RECIPE, "Invalid recipe ID.");
        integer(recipe.at("recipeVersion"), "RecipeVersion", 1, INT_MAX);
        require(recipe.at("contentHash") == seal_recipe(recipe).at("contentHash"), JPV_INVALID_RECIPE,
                "Recipe content hash mismatch; save/reseal after editing.");
        require(recipe.at("inputs").is_object() && recipe.at("inputs").size() <= 64 &&
                    recipe.at("outputs").is_object() && !recipe.at("outputs").empty() &&
                    recipe.at("outputs").size() <= 64,
                JPV_INVALID_RECIPE, "Public interfaces are invalid.");
        require(recipe.at("nodes").size() <= 1024 && recipe.at("connections").size() <= 4096,
                JPV_RESOURCE_LIMIT, "Graph size limit exceeded.");
        Plan plan;
        plan.recipe = recipe;
        auto publicDefaults = defaults(recipe, Json::object(), false);
        auto globalDefaults = global_variable_values(recipe);
        for (const auto &node : recipe.at("nodes")) {
            auto id = node.at("id").get<std::string>();
            require(!id.empty() && id.size() <= 128 && !plan.nodes.contains(id), JPV_INVALID_RECIPE,
                    "Invalid or duplicate node ID.");
            descriptor(node.at("type"));
            require(node.at("version") == descriptor(node.at("type")).at("version"), JPV_INVALID_RECIPE,
                    "Unsupported node version.");
            plan.nodes[id] = node;
            if (node.at("type") == "ExternalImage") {
                auto input = node.at("parameters").value("input", "InputImage");
                require(recipe.at("inputs").contains(input) &&
                            recipe.at("inputs").at(input).at("type") == "Image",
                        JPV_INVALID_RECIPE, "ExternalImage must bind the public Image input.");
            }
        }
        size_t bindingCount = recipe.at("connections").size();
        for (const auto &[id, node] : plan.nodes) {
            plan.parameterIncoming[id] = parameter_dependencies(node);
            bindingCount += plan.parameterIncoming[id].size();
            require(bindingCount <= 4096, JPV_RESOURCE_LIMIT, "Graph binding limit exceeded.");
            resolved_parameters(node, publicDefaults, plan, nullptr, nullptr, nullptr, &globalDefaults);
        }
        for (const auto &edge : recipe.at("connections")) {
            auto from = edge.at("fromNode").get<std::string>(), to = edge.at("toNode").get<std::string>(),
                 out = edge.at("fromPort").get<std::string>(), in = edge.at("toPort").get<std::string>();
            require(plan.nodes.contains(from) && plan.nodes.contains(to) && from != to, JPV_INVALID_RECIPE,
                    "Connection endpoint is invalid.");
            require(accepts_input(descriptor(plan.nodes.at(to).at("type")), in,
                                  port_type(descriptor(plan.nodes.at(from).at("type")), "outputs", out)),
                    JPV_INVALID_RECIPE, "Connection data types do not match.");
            require(!plan.incoming[to].contains(in), JPV_INVALID_RECIPE,
                    "Input port already has a connection.");
            plan.incoming[to][in] = {from, out};
        }
        std::map<std::string, int> marks;
        std::function<void(const std::string &, bool)> visit;
        visit = [&](const std::string &id, bool record) {
            require(marks[id] != 1, JPV_INVALID_RECIPE, "Graph contains a cycle.");
            if (marks[id] == 2)
                return;
            marks[id] = 1;
            for (const auto &[port, source] : plan.incoming[id])
                visit(source.first, record);
            for (const auto &[parameter, source] : plan.parameterIncoming[id])
                visit(source.first, record);
            marks[id] = 2;
            if (record)
                plan.order.push_back(id);
        };
        for (const auto &[id, node] : plan.nodes)
            visit(id, false);
        marks.clear();
        for (auto it = recipe.at("outputs").begin(); it != recipe.at("outputs").end(); ++it) {
            const auto &output = it.value();
            auto id = output.at("node").get<std::string>(), port = output.at("port").get<std::string>();
            require(plan.nodes.contains(id), JPV_INVALID_RECIPE, "Output refers to missing node.");
            require(port_type(descriptor(plan.nodes.at(id).at("type")), "outputs", port) ==
                        output.at("type").get<std::string>(),
                    JPV_INVALID_RECIPE, "Public output type mismatch.");
            ++plan.uses[id];
            visit(id, true);
        }
        auto decision = recipe.at("decisionOutput").get<std::string>();
        if (kind == "inspection")
            require(recipe.at("outputs").contains(decision) &&
                        (recipe.at("outputs").at(decision).at("type") == "Bool" ||
                         recipe.at("outputs").at(decision).at("type") == "Decision"),
                    JPV_INVALID_RECIPE, "Inspection decision must bind a Bool or Decision output.");
        else
            require(decision.empty(), JPV_INVALID_RECIPE,
                    "Processing recipes must not declare an inspection decision.");
        auto targets = recipe.value("previewNodes", Json::array());
        require(targets.is_array() && targets.size() <= 1024, JPV_INVALID_RECIPE,
                "Invalid preview node list.");
        for (const auto &target : targets) {
            require(target.is_string() && plan.nodes.contains(target.get<std::string>()), JPV_INVALID_RECIPE,
                    "Preview refers to missing node.");
            visit(target.get<std::string>(), true);
        }
        for (const auto &id : plan.order) {
            const auto &meta = descriptor(plan.nodes.at(id).at("type"));
            if (meta.contains("inputGroups")) {
                bool matches = false;
                for (const auto &group : meta.at("inputGroups")) {
                    bool complete = group.size() == plan.incoming[id].size();
                    for (const auto &port : group)
                        complete &= plan.incoming[id].contains(port.get<std::string>());
                    matches |= complete;
                }
                require(matches, JPV_INVALID_RECIPE,
                        "Connect either the image input or the complete legacy object inputs on node: " + id);
            } else
                for (const auto &port : meta.at("inputs"))
                    require(plan.incoming[id].contains(port.at("id").get<std::string>()), JPV_INVALID_RECIPE,
                            "Required input not connected on node: " + id);
            for (const auto &[port, source] : plan.incoming[id])
                ++plan.uses[source.first];
            for (const auto &[parameter, source] : plan.parameterIncoming[id])
                ++plan.uses[source.first];
        }
        auto resources = recipe.value("resources", Json::array());
        require(resources.is_array() && resources.size() <= 128, JPV_INVALID_RECIPE,
                "Resource manifest is invalid.");
        for (const auto &resource : resources) {
            require(!root.empty(), JPV_INVALID_RECIPE, "Resource validation requires recipe directory.");
            auto relative = path_utf8(resource.at("path").get<std::string>());
            require(!relative.is_absolute(), JPV_INVALID_RECIPE, "Resource path must be relative.");
            auto absolute = fs::weakly_canonical(root / relative);
            auto base = fs::weakly_canonical(root);
            auto rel = absolute.lexically_relative(base);
            require(!rel.empty() && *rel.begin() != L"..", JPV_INVALID_RECIPE,
                    "Resource path escapes recipe.");
            require(sha256(read_file(absolute, 32 * 1024 * 1024, JPV_INVALID_RECIPE)) ==
                        resource.at("sha256").get<std::string>(),
                    JPV_INVALID_RECIPE, "Resource hash mismatch.");
        }
        return plan;
    } catch (const Error &) {
        throw;
    } catch (const std::exception &error) {
        throw Error(JPV_INVALID_RECIPE, std::string("Invalid recipe: ") + error.what());
    }
}
std::shared_ptr<Image> copy_frame(const jpv_image_info &info, const uint8_t *pixels) {
    require(info.struct_size == sizeof(jpv_image_info) && pixels, JPV_INVALID_ARGUMENT,
            "Invalid frame descriptor.");
    require(info.origin_x >= -32768 && info.origin_x <= 32768 && info.origin_y >= -32768 &&
                info.origin_y <= 32768,
            JPV_INVALID_ARGUMENT, "External image origin exceeds supported range.");
    int channels = info.pixel_format == JPV_GRAY8 ? 1 : info.pixel_format == JPV_BGR24 ? 3 : 0;
    require(channels && info.width > 0 && info.height > 0 && info.width <= 32768 && info.height <= 32768,
            JPV_INVALID_ARGUMENT, "Unsupported image format or dimensions.");
    uint64_t rows = info.height, rowBytes = uint64_t(info.width) * channels;
    require(info.stride >= rowBytes, JPV_INVALID_ARGUMENT, "Stride is shorter than a row.");
    uint64_t needed = (rows - 1) * info.stride + rowBytes;
    require(info.buffer_length >= needed, JPV_INVALID_ARGUMENT, "Image buffer is too short.");
    require(uint64_t(info.width) * info.height <= MaxPixels && info.buffer_length <= MaxImageBytes &&
                needed <= MaxImageBytes,
            JPV_RESOURCE_LIMIT, "Image exceeds 32 megapixels/128 MiB limit.");
    auto frame = std::make_shared<Image>();
    cv::Mat source(static_cast<int>(info.height), static_cast<int>(info.width),
                   channels == 1 ? CV_8UC1 : CV_8UC3, const_cast<uint8_t *>(pixels), info.stride);
    frame->pixels = source.clone();
    frame->x = info.origin_x;
    frame->y = info.origin_y;
    return frame;
}
Json image_metadata(const Image &image) {
    Json metadata = {{"width", image.pixels.cols},
                     {"height", image.pixels.rows},
                     {"stride", image.pixels.cols * image.pixels.channels()},
                     {"pixelFormat", image.pixels.channels() == 1 ? "Gray8" : "Bgr24"},
                     {"originX", image.x},
                     {"originY", image.y},
                     {"available", image.available},
                     {"padded", image.padded}};
    metadata["coordinateSpace"] = image.coordinateSpace;
    if (!image.coordinatePlane.empty())
        metadata["coordinatePlane"] = image.coordinatePlane;
    if (!image.coordinateMapping.empty())
        metadata["coordinateMapping"] = image.coordinateMapping;
    if (!image.available && !image.unavailableReason.empty())
        metadata["unavailableReason"] = image.unavailableReason;
    if (image.available && image.matchPosition) {
        const auto &position = *image.matchPosition;
        metadata["matchPosition"] = {{"centerX", position.centerX},
                                     {"centerY", position.centerY},
                                     {"angleDegrees", position.angleDegrees},
                                     {"scale", position.scale}};
    }
    if (image.available && image.fixture) {
        metadata["fixture"] = fixture_metadata(*image.fixture);
        metadata["fixture"]["coordinateSpace"] = image.coordinateSpace;
    }
    metadata["binaryMask"] = image.binaryMask;
    if (image.pipelineContext)
        metadata["pipeline"] = image_context_metadata(image);
    return metadata;
}
void Session::close() {
    std::shared_ptr<Run> run;
    {
        std::lock_guard lock(mutex);
        closed = true;
        run = active.lock();
    }
    if (run)
        run->cancel = true;
}
Run::~Run() {
    finish();
}
void Run::finish() {
    if (finished.exchange(true))
        return;
    if (counted)
        --activeRuns;
    if (!session)
        return;
    std::lock_guard lock(session->mutex);
    if (auto current = session->active.lock(); current.get() == this)
        session->active.reset();
}
std::shared_ptr<Run> accept_run(std::shared_ptr<Session> session, std::shared_ptr<Image> frame,
                                const Json &request) {
    require(request.is_object(), JPV_INVALID_ARGUMENT, "Run request must be an object.");
    session->recipe->engine->license->check();
    auto resolved = defaults(session->recipe->plan.recipe, request.value("inputs", Json::object()), true);
    auto variables =
        global_variable_values(session->recipe->plan.recipe, request.value("variables", Json::object()));
    for (const auto &id : session->recipe->plan.order)
        resolved_parameters(session->recipe->plan.nodes.at(id), resolved, session->recipe->plan, nullptr,
                            nullptr, nullptr, &variables);
    require(request.value("runId", "").size() <= 128 && request.value("frameId", "").size() <= 128,
            JPV_INVALID_ARGUMENT, "Task identifiers exceed limit.");
    integer(request.value("timeoutMs", Json(0)), "TimeoutMs", 0, 3600000);
    auto preview = request.value("previewNodeId", "");
    require(preview.size() <= 128 &&
                (preview.empty() ||
                 std::find(session->recipe->plan.order.begin(), session->recipe->plan.order.end(), preview) !=
                     session->recipe->plan.order.end()),
            JPV_INVALID_ARGUMENT, "Preview node is not part of this execution.");
    auto run = std::make_shared<Run>();
    run->frame = std::make_shared<Image>(begin_image_pipeline(*frame));
    run->request = request;
    run->parameters = resolved;
    run->variables = std::move(variables);
    run->accepted = std::chrono::steady_clock::now();
    std::lock_guard lock(session->mutex);
    require(!session->closed, JPV_DISPOSED, "Session is disposed.");
    if (auto active = session->active.lock())
        require(active->finished, JPV_BUSY, "Session is already running.");
    if (activeRuns.fetch_add(1) >= ParallelBudget) {
        --activeRuns;
        throw Error(JPV_BUSY, "Process execution budget reached.");
    }
    run->session = session;
    run->counted = true;
    session->active = run;
    return run;
}
Json serialize_value(const Value &value) {
    return std::visit(
        [](const auto &item) -> Json {
            using T = std::decay_t<decltype(item)>;
            if constexpr (std::is_same_v<T, Image>)
                return image_metadata(item);
            else if constexpr (std::is_same_v<T, PoseValue>)
                return pose_metadata(item);
            else if constexpr (std::is_same_v<T, Contours>) {
                Json contours = Json::array();
                for (const auto &contour : item.items) {
                    Json points = Json::array();
                    for (const auto &point : contour)
                        points.push_back({point.x + item.x, point.y + item.y});
                    contours.push_back(points);
                }
                return contours;
            } else if constexpr (std::is_same_v<T, Features>)
                return feature_values(item);
            else
                return Json(item);
        },
        value);
}
// Ports are immutable after complete_image_pipeline. Reuse their conversion and exact encoded
// byte count while copying into the legacy result locations; public packet shapes stay unchanged.
struct SerializedPort {
    const Json *existing = nullptr;
    std::optional<Json> generated;
    std::optional<size_t> encodedBytes;
    explicit SerializedPort(const Value &value) {
        existing = std::get_if<Json>(&value);
        if (!existing)
            generated = serialize_value(value);
    }
    const Json &json() const {
        return existing ? *existing : *generated;
    }
    Json take() {
        return existing ? *existing : std::move(*generated);
    }
    size_t bytes() {
        if (!encodedBytes)
            encodedBytes = json().dump().size();
        return *encodedBytes;
    }
};
bool unavailable_data(const Value &value) {
    if (auto contours = std::get_if<Contours>(&value))
        return !contours->dataAvailable;
    if (auto packet = std::get_if<Json>(&value); packet && packet->is_object())
        return !packet->value("dataAvailable", true);
    if (auto features = std::get_if<Features>(&value))
        return !features->dataAvailable;
    if (auto image = std::get_if<Image>(&value))
        return !image->available;
    return false;
}
Value skipped_value(const Component &entry, const InputValues &inputs, const Image &frame) {
    auto type = entry.descriptor.at("outputs")[0].at("type").get<std::string>();
    Image image = frame;
    for (const auto &[port, value] : inputs)
        if (auto source = std::get_if<Image>(value)) {
            image = *source;
            break;
        }
    image.available = false;
    image.unavailableReason = "UnavailableInput";
    image.matchPosition.reset();
    image.fixture.reset();
    if (type == "Image" || type == "Mask")
        return image;
    if (type == "Features") {
        Features features;
        features.dataAvailable = false;
        features.sourceImage = std::make_shared<Image>(image);
        return features;
    }
    if (type == "Contours")
        return Contours{{}, image.x, image.y, false};
    if (type == "Pose") {
        PoseValue pose;
        pose.reason = "UnavailableInput";
        return pose;
    }
    if (type == "Overlay")
        return Json::array();
    if (type == "Decision")
        return Json{{"status", "Unknown"}, {"reason", "UnavailableInput"}};
    return Json(nullptr);
}
std::shared_ptr<Result> Run::execute() {
    require(!started.exchange(true), JPV_DISPOSED, "Run has already executed.");
    struct Finish {
        Run *run;
        ~Finish() {
            run->finish();
        }
    } finishGuard{this};
    const auto &plan = session->recipe->plan;
    auto result = std::make_shared<Result>();
    result->license = session->recipe->engine->license;
    auto start = std::chrono::steady_clock::now();
    auto timeout = request.value("timeoutMs", 0);
    bool debug = request.value("debug", false);
    auto previewNode = request.value("previewNodeId", "");
    result->json = {{"executionStatus", "Succeeded"},
                    {"decision", "Unknown"},
                    {"runId", request.value("runId", "")},
                    {"frameId", request.value("frameId", "")},
                    {"recipeId", plan.recipe.at("recipeId")},
                    {"recipeVersion", plan.recipe.at("recipeVersion")},
                    {"recipeKind", plan.recipe.at("kind")},
                    {"contentHash", plan.recipe.at("contentHash")},
                    {"runtimeVersion", JPV_VERSION},
                    {"opencvVersion", CV_VERSION},
                    {"inputs", parameters},
                    {"variables", variables},
                    {"outputs", Json::object()},
                    {"measurements", Json::object()},
                    {"nodes", Json::object()},
                    {"executionOrder", plan.order},
                    {"nodeOutputs", Json::object()},
                    {"error", nullptr}};
    std::map<std::string, PortValues> values;
    std::map<std::pair<std::string, std::string>, size_t> serializedPortBytes;
    auto uses = plan.uses;
    size_t debugBytes = 0;
    size_t measurementBytes = 0;
    std::map<const uint8_t *, size_t> imageBuffers;
    std::set<const void *> contextPayloads;
    auto accountImage = [&](const Image &image) {
        for (const auto *matrix : {&image.pixels, &image.valid}) {
            if (matrix->empty())
                continue;
            // ROI views keep their complete backing allocation alive. Shared views count once.
            size_t bytes = size_t(matrix->dataend - matrix->datastart);
            auto &accounted = imageBuffers[matrix->datastart];
            if (bytes > accounted) {
                debugBytes += bytes - accounted;
                accounted = bytes;
            }
        }
        image_context_payloads(image, [&](const void *identity, size_t bytes) {
            if (contextPayloads.insert(identity).second)
                debugBytes += bytes;
        });
    };
    auto check = [&] {
        require(!cancel, JPV_CANCELLED, "Inspection cancelled.");
        require(timeout == 0 ||
                    std::chrono::steady_clock::now() - accepted < std::chrono::milliseconds(timeout),
                JPV_TIMED_OUT, "Inspection deadline exceeded.");
    };
    std::string current;
    for (const auto &id : plan.order)
        result->json["nodes"][id] = {{"status", "Pending"}, {"elapsedMs", 0}};
    try {
        result->license->check();
        check();
        for (const auto &id : plan.order) {
            current = id;
            check();
            auto nodeStart = std::chrono::steady_clock::now();
            result->json["nodes"][id]["status"] = "Running";
            InputValues inputs;
            for (const auto &[port, source] : plan.incoming.at(id))
                inputs.emplace(port, &values.at(source.first).at(source.second));
            const auto &node = plan.nodes.at(id);
            const Image *parameterImage = frame.get();
            if (inputs.contains("input")) {
                if (auto image = std::get_if<Image>(inputs.at("input")))
                    parameterImage = image;
                else if (auto features = std::get_if<Features>(inputs.at("input"));
                         features && features->sourceImage)
                    parameterImage = features->sourceImage.get();
                else if (auto contours = std::get_if<Contours>(inputs.at("input"));
                         contours && contours->sourceImage)
                    parameterImage = contours->sourceImage.get();
            }
            Json localVariables;
            auto p = resolved_parameters(node, parameters, plan, &values, &result->json["nodes"],
                                         parameterImage, &variables, &localVariables);
            const auto &entry = component_registry().get(node.at("type").get<std::string>());
            for (const auto &[port, source] : plan.incoming.at(id))
                if (result->json["nodes"].at(source.first).at("status") == "Skipped" ||
                    unavailable_data(values.at(source.first).at(source.second))) {
                    if (!p.contains("_unavailable"))
                        p["_unavailable"] = Json::array();
                    p["_unavailable"].push_back("input:" + port);
                }
            auto prepared = prepare_image_inputs(entry, inputs, p, *frame);
            inputs = prepared.inputs;
            for (const auto &[port, value] : inputs)
                if (unavailable_data(*value)) {
                    if (!p.contains("_unavailable"))
                        p["_unavailable"] = Json::array();
                    p["_unavailable"].push_back("input:" + port);
                }
            bool skipped = p.contains("_unavailable");
            const Value *input = inputs.empty() ? nullptr : inputs.begin()->second;
            auto executeStart = std::chrono::steady_clock::now();
            Value computed = skipped && !entry.descriptor.value("handlesUnavailableParameters", false)
                                 ? skipped_value(entry, inputs, *frame)
                             : entry.executeInputs ? entry.executeInputs(inputs, p, *frame)
                                                   : entry.execute(input, p, *frame);
            auto executeEnd = std::chrono::steady_clock::now();
            skipped = skipped || unavailable_data(computed);
            check();
            PortValues ports = entry.expandInputs ? entry.expandInputs(computed, p, inputs)
                               : entry.expand     ? entry.expand(computed, p)
                                                  : PortValues{};
            check();
            auto primary = entry.descriptor.at("outputs")[0].at("id").get<std::string>();
            ports.emplace(primary, std::move(computed));
            complete_image_pipeline(id, entry, inputs, ports, p, *frame);
            auto &output = ports.at(primary);
            std::map<std::string, SerializedPort> serializedPorts;
            auto serialized = [&](const std::string &name) -> SerializedPort & {
                return serializedPorts.try_emplace(name, ports.at(name)).first->second;
            };
            if (entry.descriptor.at("outputs")[0].at("type") == "Result") {
                debugBytes += serialized(primary).bytes();
                require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Result packet budget exceeded.");
            }
            require(ports.size() == entry.descriptor.at("outputs").size(), JPV_INTERNAL_ERROR,
                    "Component did not produce all declared outputs.");
            if (auto features = std::get_if<Features>(&output); features && features->sourceImage)
                accountImage(*features->sourceImage);
            if (ports.contains("image"))
                if (auto image = std::get_if<Image>(&ports.at("image")))
                    accountImage(*image);
            require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Associated image budget exceeded.");
            if (ports.contains("result")) {
                // Packets are complex JSON even when Debug is off. Count their generation so an
                // unpublished packet or a large graph cannot bypass the result memory budget.
                debugBytes += serialized("result").bytes();
                require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Result packet budget exceeded.");
            }
            if (ports.contains("measurement")) {
                auto &measurement = serialized("measurement");
                measurementBytes += measurement.bytes();
                require(measurementBytes <= MaxMeasurementBytes, JPV_RESOURCE_LIMIT,
                        "Measurement result budget exceeded.");
                result->json["measurements"][id] = measurement.json();
            }
            check();
            bool retained = debug || id == previewNode;
            if (retained) {
                for (const auto &[port, value] : inputs)
                    if (auto image = std::get_if<Image>(value); image && port == "input") {
                        accountImage(*image);
                        result->inputImages[id] = *image;
                        break;
                    }
                auto &data = serialized(primary);
                debugBytes += data.bytes();
                if (auto image = std::get_if<Image>(&output)) {
                    accountImage(*image);
                    result->images[id] = *image;
                }
                if (ports.contains("image")) {
                    const auto &image = std::get<Image>(ports.at("image"));
                    accountImage(image);
                    result->images[id] = image;
                }
                if (ports.contains("overlay"))
                    result->json["debugOverlays"][id] = std::get<Json>(ports.at("overlay"));
                if (auto features = std::get_if<Features>(&output)) {
                    if (features->sourceImage)
                        result->inputImages[id] = *features->sourceImage;
                    if (ports.contains("image"))
                        result->images[id] = std::get<Image>(ports.at("image"));
                    auto overlays = ports.contains("overlay") ? std::get<Json>(ports.at("overlay"))
                                                              : feature_overlays(*features);
                    debugBytes += overlays.dump().size();
                    result->json["debugOverlays"][id] = std::move(overlays);
                }
                require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Debug result budget exceeded.");
                result->json["debugValues"][id] = data.json();
            }
            Json nodePorts = Json::object();
            for (const auto &port : entry.descriptor.at("outputs")) {
                auto name = port.at("id").get<std::string>(), type = port.at("type").get<std::string>();
                const auto &value = ports.at(name);
                bool small = type == "Int" || type == "Double" || type == "Bool" || type == "String" ||
                             type == "Measurement" || type == "Decision" || type == "Pose";
                if (small || retained || type == "Image" || type == "Mask") {
                    auto &encoded = serialized(name);
                    serializedPortBytes[{id, name}] = encoded.bytes();
                    auto data = encoded.take();
                    if (type == "Mask" && retained) {
                        const auto &image = std::get<Image>(value);
                        data["foregroundPixels"] =
                            image.available ? cv::countNonZero(visible_pixels(image)) : 0;
                    }
                    if (retained || type == "Image" || type == "Mask")
                        debugBytes += type == "Mask" && retained ? data.dump().size() : encoded.bytes();
                    nodePorts[name] = std::move(data);
                }
            }
            require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Node preview budget exceeded.");
            result->json["nodeOutputs"][id] = std::move(nodePorts);
            if (uses[id] > 0)
                values[id] = std::move(ports);
            auto resultEnd = std::chrono::steady_clock::now();
            result->json["nodes"][id] = {{"status", skipped ? "Skipped" : "Succeeded"},
                                         {"elapsedMs", std::chrono::duration<double, std::milli>(
                                                           resultEnd - nodeStart).count()},
                                         {"prepareMs", std::chrono::duration<double, std::milli>(executeStart - nodeStart).count()},
                                         {"executeMs", std::chrono::duration<double, std::milli>(executeEnd - executeStart).count()},
                                         {"resultMs", std::chrono::duration<double, std::milli>(resultEnd - executeEnd).count()}};
            Json resolvedSnapshot = Json::object();
            for (auto it = p.begin(); it != p.end(); ++it)
                if (it.key() == "roi" || it.key() == "firstRoi" || it.key() == "secondRoi" ||
                    (node.at("parameters").contains(it.key()) &&
                     node.at("parameters").at(it.key()).is_object() &&
                     (node.at("parameters").at(it.key()).contains("node") ||
                      node.at("parameters").at(it.key()).contains("variable") ||
                      node.at("parameters").at(it.key()).contains("expression"))))
                    resolvedSnapshot[it.key()] = it.value();
            result->json["nodes"][id]["resolvedParameters"] = std::move(resolvedSnapshot);
            result->json["nodes"][id]["localVariables"] = std::move(localVariables);
            if (p.contains("_unavailable")) {
                result->json["nodes"][id]["unavailableParameters"] = p.at("_unavailable");
                // Teaching placeholders are never reported as actual current-frame values.
                result->json["nodes"][id]["resolvedParameters"] = nullptr;
            }
            if (skipped)
                result->json["nodes"][id]["reason"] =
                    p.contains("_unavailable") ? "UnavailableParameterOrInput" : "UnavailableSelection";
            for (const auto &[port, source] : plan.incoming.at(id))
                if (--uses[source.first] == 0)
                    values.erase(source.first);
            for (const auto &[parameter, source] : plan.parameterIncoming.at(id))
                if (--uses[source.first] == 0)
                    values.erase(source.first);
        }
        check();
        struct PublishedValue {
            const Json *json;
            size_t bytes;
        };
        std::map<std::pair<std::string, std::string>, PublishedValue> publishedValues;
        for (auto it = plan.recipe.at("outputs").begin(); it != plan.recipe.at("outputs").end(); ++it) {
            auto id = it.value().at("node").get<std::string>();
            auto port = it.value().at("port").get<std::string>();
            const auto &value = values.at(id).at(port);
            const auto key = std::pair{id, port};
            Json data;
            size_t bytes = 0;
            if (auto previous = publishedValues.find(key); previous != publishedValues.end()) {
                data = *previous->second.json;
                bytes = previous->second.bytes;
            } else if (const auto sizes = serializedPortBytes.find(key);
                       sizes != serializedPortBytes.end()) {
                data = result->json.at("nodeOutputs").at(id).at(port);
                // Retained masks add preview-only statistics; public mask metadata never did.
                if (it.value().at("type") == "Mask")
                    data.erase("foregroundPixels");
                bytes = sizes->second;
            } else {
                data = serialize_value(value);
                bytes = data.dump().size();
            }
            if (it.value().at("type") == "Result" || it.value().at("type") == "Image" ||
                it.value().at("type") == "Mask") {
                // Public aliases each retain another packet; include every copy in the same budget.
                debugBytes += bytes;
                require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT,
                        "Public result packet budget exceeded.");
            }
            result->json["outputs"][it.key()] = std::move(data);
            publishedValues.try_emplace(key, PublishedValue{&result->json["outputs"].at(it.key()), bytes});
            if (auto image = std::get_if<Image>(&value)) {
                accountImage(*image);
                result->outputImages[it.key()] = *image;
            } else if (it.value().at("type") == "Result" && values.at(id).contains("image")) {
                const auto &projection = std::get<Image>(values.at(id).at("image"));
                accountImage(projection);
                result->outputImages[it.key()] = projection;
            }
            require(debugBytes <= MaxDebugBytes, JPV_RESOURCE_LIMIT, "Public image budget exceeded.");
        }
        check();
        if (plan.recipe.at("kind") == "inspection") {
            auto name = plan.recipe.at("decisionOutput").get<std::string>();
            const auto &decision = result->json.at("outputs").at(name);
            std::set<std::string> visited;
            std::function<bool(const std::string &)> skippedAncestor = [&](const std::string &id) {
                if (!visited.insert(id).second)
                    return false;
                if (result->json["nodes"].at(id).at("status") == "Skipped")
                    return true;
                // Missing parameters actually read by a node already mark it Skipped.
                // A lazy expression may deliberately avoid an unavailable branch.
                for (const auto &[key, source] : plan.incoming.at(id))
                    if (skippedAncestor(source.first))
                        return true;
                return false;
            };
            if (skippedAncestor(plan.recipe.at("outputs").at(name).at("node")))
                result->json["decision"] = "Unknown";
            else if (plan.recipe.at("outputs").at(name).at("type") == "Bool")
                result->json["decision"] = decision.get<bool>() ? "Pass" : "Fail";
            else {
                auto status = decision.at("status").get<std::string>();
                require(status == "Pass" || status == "Fail" || status == "Unknown", JPV_INTERNAL_ERROR,
                        "Component produced an invalid Decision.");
                result->json["decision"] = status;
            }
        }
    } catch (const std::exception &error) {
        int code = JPV_INTERNAL_ERROR;
        if (auto known = dynamic_cast<const Error *>(&error))
            code = known->code;
        auto status = code == JPV_CANCELLED ? "Cancelled" : code == JPV_TIMED_OUT ? "TimedOut" : "Failed";
        result->json["executionStatus"] = status;
        result->json["decision"] = "Unknown";
        result->json["outputs"] = Json::object();
        result->json["measurements"] = Json::object();
        result->json["nodeOutputs"] = Json::object();
        result->outputImages.clear();
        result->json["error"] = {{"code", code}, {"nodeId", current}, {"message", error.what()}};
        for (auto it = result->json["nodes"].begin(); it != result->json["nodes"].end(); ++it) {
            if (it.key() == current)
                it.value()["status"] = status;
            else if (it.value()["status"] == "Pending")
                it.value()["status"] = "Skipped";
        }
    }
    result->json["elapsedMs"] =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    return result;
}
} // namespace jp

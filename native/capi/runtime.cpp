#define JPV_RUNTIME_EXPORTS
#include "graph.h"
#include "license.h"
#include "vision_api.h"
#include <cstring>
#include <functional>

namespace {
thread_local std::string lastError;
std::atomic<int64_t> activeHandles{0}, activeStrings{0};
int guard(const std::function<void()> &function) {
    lastError.clear();
    try {
        function();
        return JPV_OK;
    } catch (const jp::Error &error) {
        lastError = error.what();
        return error.code;
    } catch (const std::exception &error) {
        lastError = error.what();
        return JPV_INTERNAL_ERROR;
    } catch (...) {
        lastError = "Unknown native error.";
        return JPV_INTERNAL_ERROR;
    }
}
void write_string(char **target, const jp::Json &value) {
    jp::require(target, JPV_INVALID_ARGUMENT, "Missing output pointer.");
    auto text = value.dump();
    *target = new char[text.size() + 1];
    std::memcpy(*target, text.c_str(), text.size() + 1);
    ++activeStrings;
}
std::string safe_string(const char *value, size_t max = 1024 * 1024) {
    jp::require(value, JPV_INVALID_ARGUMENT, "Missing string.");
    size_t length = strnlen(value, max + 1);
    jp::require(length <= max, JPV_RESOURCE_LIMIT, "String exceeds limit.");
    return std::string(value, length);
}
template <class T> struct Holder {
    std::shared_ptr<T> value;
    explicit Holder(std::shared_ptr<T> item) : value(std::move(item)) {
        ++activeHandles;
    }
    ~Holder() {
        --activeHandles;
    }
};
template <class T> std::shared_ptr<T> object(jpv_handle handle) {
    jp::require(handle, JPV_INVALID_ARGUMENT, "Missing native handle.");
    return static_cast<Holder<T> *>(handle)->value;
}
template <class T> void output(jpv_handle *target, std::shared_ptr<T> value) {
    jp::require(target, JPV_INVALID_ARGUMENT, "Missing handle output.");
    *target = new Holder<T>(std::move(value));
}
} // namespace
extern "C" {
const char *__cdecl vision_version() {
    return "JP-Vision/" JPV_VERSION " ABI/1 OpenCV/5.0.0";
}
const char *__cdecl vision_last_error() {
    return lastError.c_str();
}
void __cdecl vision_string_free(char *text) {
    if (text) {
        delete[] text;
        --activeStrings;
    }
}
int32_t __cdecl vision_diagnostics(char **json) {
    if (json)
        *json = nullptr;
    return guard([&] {
        write_string(json, {{"activeNativeHandles", activeHandles.load()},
                            {"activeNativeStrings", activeStrings.load()},
                            {"activeRuns", jp::active_run_count()}});
    });
}
int32_t __cdecl vision_machine_request(char **json) {
    if (json)
        *json = nullptr;
    return guard([&] { write_string(json, jp::machine_request()); });
}
int32_t __cdecl vision_license_status(const char *store, const char *feature, char **json) {
    if (json)
        *json = nullptr;
    return guard([&] {
        jp::LicenseService service(store ? safe_string(store) : "");
        write_string(json, service.info(feature ? safe_string(feature) : "runtime"));
    });
}
int32_t __cdecl vision_license_import(const char *store, const char *file, char **json) {
    if (json)
        *json = nullptr;
    return guard([&] {
        jp::LicenseService service(store ? safe_string(store) : "");
        write_string(json, service.import_file(jp::path_utf8(safe_string(file))));
    });
}
int32_t __cdecl vision_node_catalog(char **json) {
    if (json)
        *json = nullptr;
    return guard([&] { write_string(json, jp::node_catalog()); });
}
int32_t __cdecl vision_recipe_seal(const char *recipe, char **json) {
    if (json)
        *json = nullptr;
    return guard([&] {
        write_string(
            json, jp::seal_recipe(jp::strict_json(safe_string(recipe, 4 * 1024 * 1024), JPV_INVALID_RECIPE)));
    });
}
int32_t __cdecl vision_engine_create(const char *options, jpv_handle *engine) {
    if (engine)
        *engine = nullptr;
    return guard([&] {
        output(engine, std::make_shared<jp::Engine>(
                           options ? jp::strict_json(safe_string(options), JPV_INVALID_ARGUMENT)
                                   : jp::Json::object()));
    });
}
void __cdecl vision_engine_release(jpv_handle engine) {
    delete static_cast<Holder<jp::Engine> *>(engine);
}
int32_t __cdecl vision_recipe_load(jpv_handle engine, const char *directory, jpv_handle *recipe) {
    if (recipe)
        *recipe = nullptr;
    return guard([&] {
        auto owner = object<jp::Engine>(engine);
        owner->license->check();
        auto path = jp::path_utf8(safe_string(directory));
        auto json = jp::strict_json(jp::read_file(path / L"recipe.json", 4 * 1024 * 1024, JPV_INVALID_RECIPE),
                                    JPV_INVALID_RECIPE);
        auto value = std::make_shared<jp::Recipe>();
        value->engine = owner;
        value->plan = jp::compile_recipe(json, path);
        output(recipe, std::move(value));
    });
}
int32_t __cdecl vision_recipe_validate(jpv_handle engine, const char *recipe, char **diagnostics) {
    if (diagnostics)
        *diagnostics = nullptr;
    return guard([&] {
        auto owner = object<jp::Engine>(engine);
        owner->license->check();
        auto plan =
            jp::compile_recipe(jp::strict_json(safe_string(recipe, 4 * 1024 * 1024), JPV_INVALID_RECIPE));
        write_string(diagnostics, {{"valid", true},
                                   {"executionOrder", plan.order},
                                   {"contentHash", plan.recipe.at("contentHash")}});
    });
}
void __cdecl vision_recipe_release(jpv_handle recipe) {
    delete static_cast<Holder<jp::Recipe> *>(recipe);
}
int32_t __cdecl vision_recipe_description(jpv_handle recipe, char **json) {
    if (json)
        *json = nullptr;
    return guard([&] {
        auto owner = object<jp::Recipe>(recipe);
        owner->engine->license->check();
        const auto &document = owner->plan.recipe;
        write_string(json, {{"recipeId", document.at("recipeId")},
                            {"recipeVersion", document.at("recipeVersion")},
                            {"kind", document.at("kind")},
                            {"contentHash", document.at("contentHash")},
                            {"inputs", document.at("inputs")},
                            {"outputs", document.at("outputs")},
                            {"decisionOutput", document.at("decisionOutput")}});
    });
}
int32_t __cdecl vision_session_create(jpv_handle recipe, jpv_handle *session) {
    if (session)
        *session = nullptr;
    return guard([&] {
        auto value = object<jp::Recipe>(recipe);
        value->engine->license->check();
        output(session, std::make_shared<jp::Session>(value));
    });
}
void __cdecl vision_session_release(jpv_handle session) {
    if (!session)
        return;
    auto holder = static_cast<Holder<jp::Session> *>(session);
    holder->value->close();
    delete holder;
}
int32_t __cdecl vision_frame_copy(const jpv_image_info *info, const uint8_t *data, jpv_handle *frame) {
    if (frame)
        *frame = nullptr;
    return guard([&] {
        jp::require(info, JPV_INVALID_ARGUMENT, "Missing frame information.");
        output(frame, jp::copy_frame(*info, data));
    });
}
int32_t __cdecl vision_frame_info(jpv_handle frame, jpv_image_info *info) {
    return guard([&] {
        jp::require(info && info->struct_size == sizeof(jpv_image_info), JPV_INVALID_ARGUMENT,
                    "Frame info ABI mismatch.");
        auto image = object<jp::Image>(frame);
        *info = {sizeof(jpv_image_info),
                 static_cast<uint32_t>(image->pixels.cols),
                 static_cast<uint32_t>(image->pixels.rows),
                 static_cast<uint32_t>(image->pixels.cols * image->pixels.channels()),
                 static_cast<uint32_t>(image->pixels.channels() == 1 ? JPV_GRAY8 : JPV_BGR24),
                 0,
                 image->pixels.total() * image->pixels.elemSize(),
                 image->x,
                 image->y};
    });
}
int32_t __cdecl vision_frame_pixels(jpv_handle frame, uint8_t *target, uint64_t length) {
    return guard([&] {
        auto image = object<jp::Image>(frame);
        auto bytes = image->pixels.total() * image->pixels.elemSize();
        jp::require(target && length >= bytes, JPV_INVALID_ARGUMENT, "Pixel destination is too short.");
        size_t row = static_cast<size_t>(image->pixels.cols) * image->pixels.elemSize();
        for (int y = 0; y < image->pixels.rows; ++y) {
            std::memcpy(target + static_cast<size_t>(y) * row, image->pixels.ptr(y), row);
            if (!image->valid.empty())
                for (int x = 0; x < image->pixels.cols; ++x)
                    if (image->valid.at<uint8_t>(y, x) == 0)
                        std::memset(target + static_cast<size_t>(y) * row + x * image->pixels.elemSize(), 0,
                                    image->pixels.elemSize());
        }
    });
}
void __cdecl vision_frame_release(jpv_handle frame) {
    delete static_cast<Holder<jp::Image> *>(frame);
}
int32_t __cdecl vision_run_create(jpv_handle session, jpv_handle frame, const char *request,
                                  jpv_handle *run) {
    if (run)
        *run = nullptr;
    return guard([&] {
        output(run, jp::accept_run(object<jp::Session>(session), object<jp::Image>(frame),
                                   request ? jp::strict_json(safe_string(request), JPV_INVALID_ARGUMENT)
                                           : jp::Json::object()));
    });
}
int32_t __cdecl vision_run_execute(jpv_handle run, jpv_handle *result) {
    if (result)
        *result = nullptr;
    return guard([&] { output(result, object<jp::Run>(run)->execute()); });
}
void __cdecl vision_run_cancel(jpv_handle run) {
    if (run)
        static_cast<Holder<jp::Run> *>(run)->value->cancel = true;
}
void __cdecl vision_run_release(jpv_handle run) {
    delete static_cast<Holder<jp::Run> *>(run);
}
int32_t __cdecl vision_result_json(jpv_handle result, char **json) {
    if (json)
        *json = nullptr;
    return guard([&] { write_string(json, object<jp::Result>(result)->json); });
}
int32_t __cdecl vision_result_frame(jpv_handle result, const char *node, jpv_handle *frame) {
    if (frame)
        *frame = nullptr;
    return guard([&] {
        auto value = object<jp::Result>(result);
        value->license->check();
        auto id = safe_string(node);
        jp::require(value->images.contains(id), JPV_INVALID_ARGUMENT,
                    "Intermediate image not retained; enable debug and use a node ID.");
        jp::require(value->images.at(id).available, JPV_INVALID_ARGUMENT,
                    "Image is unavailable: no upstream matching target was selected.");
        output(frame, std::make_shared<jp::Image>(value->images.at(id)));
    });
}
int32_t __cdecl vision_result_input_frame(jpv_handle result, const char *node, jpv_handle *frame) {
    if (frame)
        *frame = nullptr;
    return guard([&] {
        auto value = object<jp::Result>(result);
        value->license->check();
        auto id = safe_string(node);
        jp::require(value->inputImages.contains(id), JPV_INVALID_ARGUMENT,
                    "Detector input image not retained; enable debug or select the node for preview.");
        jp::require(value->inputImages.at(id).available, JPV_INVALID_ARGUMENT,
                    "Input image is unavailable: no upstream matching target was selected.");
        output(frame, std::make_shared<jp::Image>(value->inputImages.at(id)));
    });
}
void __cdecl vision_result_release(jpv_handle result) {
    delete static_cast<Holder<jp::Result> *>(result);
}
int32_t __cdecl vision_result_output_frame(jpv_handle result, const char *name, jpv_handle *frame) {
    if (frame)
        *frame = nullptr;
    return guard([&] {
        auto value = object<jp::Result>(result);
        value->license->check();
        auto id = safe_string(name);
        jp::require(value->outputImages.contains(id) && value->json.at("executionStatus") == "Succeeded",
                    JPV_INVALID_ARGUMENT, "Public image output is missing or this run did not succeed.");
        jp::require(value->outputImages.at(id).available, JPV_INVALID_ARGUMENT,
                    "Image is unavailable: no upstream matching target was selected.");
        output(frame, std::make_shared<jp::Image>(value->outputImages.at(id)));
    });
}
}

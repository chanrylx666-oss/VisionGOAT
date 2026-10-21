#include "json.hpp"
#include "vision_api.h"
#include <filesystem>
#include <fstream>
#include <functional>
#include <thread>
#include <iostream>
#include <vector>
using Json = nlohmann::json;
static void expect(bool value, const char *message) {
    if (!value)
        throw std::runtime_error(message);
}
static void ok(int code) {
    if (code)
        throw std::runtime_error(std::to_string(code) + ": " + vision_last_error());
}
static std::string take(int code, char *text) {
    ok(code);
    std::string value = text;
    vision_string_free(text);
    return value;
}
static std::string seal(const Json &recipe) {
    char *text = nullptr;
    int code = vision_recipe_seal(recipe.dump().c_str(), &text);
    return take(code, text);
}
struct Owned {
    jpv_handle h = nullptr;
    void (*release)(jpv_handle);
    explicit Owned(void (*r)(jpv_handle)) : release(r) {}
    ~Owned() {
        release(h);
    }
    Owned(const Owned &) = delete;
};
int main(int argc, char **argv) {
    try {
        expect(argc == 4, "Arguments: recipe directory, license file, isolated store.");
        std::string store = argv[3];
        char *text = nullptr;
        Owned missing(vision_engine_release);
        auto options = Json{{"licenseStore", store + "-missing"}}.dump();
        expect(vision_engine_create(options.c_str(), &missing.h) == JPV_LICENSE_MISSING,
               "Unlicensed native entry must reject.");
        ok(vision_license_import(store.c_str(), argv[2], &text));
        vision_string_free(text);
        text = nullptr;
        Owned engine(vision_engine_release), recipe(vision_recipe_release), session(vision_session_release),
            frame(vision_frame_release);
        options = Json{{"licenseStore", store}}.dump();
        ok(vision_engine_create(options.c_str(), &engine.h));
        ok(vision_recipe_load(engine.h, argv[1], &recipe.h));
        ok(vision_session_create(recipe.h, &session.h));
        const int width = 120, height = 50, stride = 368;
        std::vector<uint8_t> pixels(stride * height, 0);
        for (int object = 0; object < 3; ++object)
            for (int y = 10; y < 30; ++y)
                for (int x = 5 + 40 * object; x < 25 + 40 * object; ++x)
                    for (int c = 0; c < 3; ++c)
                        pixels[y * stride + x * 3 + c] = 255;
        jpv_image_info info{sizeof(jpv_image_info), width, height, stride, JPV_BGR24, 0, pixels.size(), 0, 0};
        ok(vision_frame_copy(&info, pixels.data(), &frame.h));
        auto request =
            Json{{"runId", "native"}, {"frameId", "padded-frame"}, {"inputs", {{"ExpectedCount", 3}}}}.dump();
        auto inspect = [&](const std::string &json, bool cancel) {
            Owned run(vision_run_release), result(vision_result_release);
            ok(vision_run_create(session.h, frame.h, json.c_str(), &run.h));
            if (cancel)
                vision_run_cancel(run.h);
            ok(vision_run_execute(run.h, &result.h));
            char *data = nullptr;
            int code = vision_result_json(result.h, &data);
            return Json::parse(take(code, data));
        };
        auto first = inspect(request, false);
        expect(first["decision"] == "Pass" && first["outputs"]["ObjectCount"] == 3,
               "Known padded image must count three.");
        auto cancelled = inspect(request, true);
        expect(cancelled["executionStatus"] == "Cancelled" && cancelled["decision"] == "Unknown" &&
                   cancelled["outputs"].empty(),
               "Cancellation before execution must be preserved.");
        auto third = inspect(request, false);
        expect(third["decision"] == "Pass", "Old cancellation must not affect new run.");
        Owned accepted(vision_run_release), extra(vision_run_release), result(vision_result_release);
        ok(vision_run_create(session.h, frame.h, request.c_str(), &accepted.h));
        expect(vision_run_create(session.h, frame.h, request.c_str(), &extra.h) == JPV_BUSY,
               "Session reentry must report Busy.");
        vision_frame_release(frame.h);
        frame.h = nullptr;
        ok(vision_run_execute(accepted.h, &result.h));
        char *resultText = nullptr;
        int code = vision_result_json(result.h, &resultText);
        auto retained = Json::parse(take(code, resultText));
        expect(retained["outputs"]["ObjectCount"] == 3, "Accepted run must retain disposed frame.");
        ok(vision_frame_copy(&info, pixels.data(), &frame.h));
        Owned next(vision_run_release), nextResult(vision_result_release);
        ok(vision_run_create(session.h, frame.h, request.c_str(), &next.h));
        vision_run_cancel(accepted.h);
        ok(vision_run_execute(next.h, &nextResult.h));
        char *nextText = nullptr;
        code = vision_result_json(nextResult.h, &nextText);
        expect(Json::parse(take(code, nextText))["decision"] == "Pass",
               "Late cancellation of an old run must not cancel the next run.");
        auto deadline = Json::parse(request);
        deadline["timeoutMs"] = 1;
        Owned timed(vision_run_release), timedResult(vision_result_release);
        ok(vision_run_create(session.h, frame.h, deadline.dump().c_str(), &timed.h));
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        ok(vision_run_execute(timed.h, &timedResult.h));
        char *timedText = nullptr;
        code = vision_result_json(timedResult.h, &timedText);
        auto expired = Json::parse(take(code, timedText));
        expect(expired["executionStatus"] == "TimedOut" && expired["decision"] == "Unknown" &&
                   expired["outputs"].empty(),
               "Accepted deadline must not produce a valid old Pass.");
        std::ifstream input(std::filesystem::path(argv[1]) / "recipe.json");
        Json data;
        input >> data;
        data["outputs"].erase("InspectionPassed");
        auto invalid = seal(data);
        char *diagnostics = nullptr;
        expect(vision_recipe_validate(engine.h, invalid.c_str(), &diagnostics) == JPV_INVALID_RECIPE,
               "Missing inspection decision must be rejected.");
        std::ifstream original(std::filesystem::path(argv[1]) / "recipe.json");
        original >> data;
        data["nodes"].push_back(
            {{"id", "cycle-a"}, {"type", "ToGray"}, {"version", 1}, {"parameters", Json::object()}});
        data["nodes"].push_back(
            {{"id", "cycle-b"}, {"type", "ToGray"}, {"version", 1}, {"parameters", Json::object()}});
        data["connections"].push_back(
            {{"fromNode", "cycle-a"}, {"fromPort", "image"}, {"toNode", "cycle-b"}, {"toPort", "input"}});
        data["connections"].push_back(
            {{"fromNode", "cycle-b"}, {"fromPort", "image"}, {"toNode", "cycle-a"}, {"toPort", "input"}});
        invalid = seal(data);
        expect(vision_recipe_validate(engine.h, invalid.c_str(), &diagnostics) == JPV_INVALID_RECIPE,
               "Structural cycle must be rejected.");
        std::cout << "PASS native licensing, padded pixels, real count, cancellation, retained ownership, "
                     "Busy, decision and cycle checks.\n";
        return 0;
    } catch (const std::exception &error) {
        std::cerr << "FAIL " << error.what() << "\n";
        return 1;
    }
}

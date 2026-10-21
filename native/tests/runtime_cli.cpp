#include "json.hpp"
#include "vision_api.h"
#include <fstream>
#include <iostream>
#include <opencv2/imgcodecs.hpp>
#include <string>
int main(int argc, char **argv) {
    if (argc < 2)
        return 2;
    char *text = nullptr;
    int code = 0;
    std::string command = argv[1];
    if (command == "request")
        code = vision_machine_request(&text);
    else if (command == "status")
        code = vision_license_status(argc > 2 ? argv[2] : nullptr, "runtime", &text);
    else if (command == "import" && argc > 2)
        code = vision_license_import(argc > 3 ? argv[3] : nullptr, argv[2], &text);
    else if (command == "catalog")
        code = vision_node_catalog(&text);
    else if (command == "seal" && argc > 3) {
        std::ifstream input(argv[2], std::ios::binary);
        std::string content((std::istreambuf_iterator<char>(input)), {});
        code = vision_recipe_seal(content.c_str(), &text);
        if (!code) {
            std::ofstream output(argv[3], std::ios::binary);
            output << text;
            if (!output)
                code = JPV_INTERNAL_ERROR;
        }
    } else if (command == "run" && argc > 4) {
        jpv_handle engine = nullptr, recipe = nullptr, session = nullptr, frame = nullptr, run = nullptr,
                   result = nullptr;
        std::string options = "{}";
        if (argc > 5)
            options = nlohmann::json{{"licenseStore", argv[5]}}.dump();
        code = vision_engine_create(options.c_str(), &engine);
        if (!code)
            code = vision_recipe_load(engine, argv[2], &recipe);
        if (!code)
            code = vision_session_create(recipe, &session);
        auto image = cv::imread(argv[3], cv::IMREAD_COLOR);
        if (!code && image.empty())
            code = JPV_INVALID_ARGUMENT;
        if (!code) {
            jpv_image_info info{sizeof(jpv_image_info),
                                static_cast<uint32_t>(image.cols),
                                static_cast<uint32_t>(image.rows),
                                static_cast<uint32_t>(image.step),
                                JPV_BGR24,
                                0,
                                image.total() * image.elemSize(),
                                0,
                                0};
            code = vision_frame_copy(&info, image.ptr(), &frame);
        }
        if (!code) {
            std::string request =
                "{\"runId\":\"cli\",\"frameId\":\"image\",\"debug\":true,\"inputs\":{\"ExpectedCount\":" +
                std::string(argc > 6 ? argv[6] : "3") + "}}";
            code = vision_run_create(session, frame, request.c_str(), &run);
        }
        if (!code)
            code = vision_run_execute(run, &result);
        if (!code)
            code = vision_result_json(result, &text);
        if (!code) {
            std::ofstream output(argv[4], std::ios::binary);
            output << text;
            if (!output)
                code = JPV_INTERNAL_ERROR;
        }
        std::string error = vision_last_error();
        vision_result_release(result);
        vision_run_release(run);
        vision_frame_release(frame);
        vision_session_release(session);
        vision_recipe_release(recipe);
        vision_engine_release(engine);
        if (code) {
            std::cerr << error << "\n";
            vision_string_free(text);
            return code;
        }
    } else
        return 2;
    if (code) {
        std::cerr << vision_last_error() << "\n";
        vision_string_free(text);
        return code;
    }
    if (command == "request" && argc > 2) {
        std::ofstream file(argv[2], std::ios::binary);
        file << text;
        if (!file) {
            vision_string_free(text);
            return 2;
        }
    } else if (command == "run")
        std::cout << "Run result saved to " << argv[4] << "\n";
    else if (command != "seal")
        std::cout << text << "\n";
    vision_string_free(text);
    return 0;
}

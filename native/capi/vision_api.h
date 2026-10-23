#pragma once
#include <stdint.h>
#if defined(JPV_RUNTIME_EXPORTS)
#define JPV_API __declspec(dllexport)
#elif defined(JPV_DYNAMIC_IMPORT)
#define JPV_API
#else
#define JPV_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef void *jpv_handle;
enum jpv_status {
    JPV_OK = 0,
    JPV_INVALID_ARGUMENT = 1,
    JPV_INVALID_RECIPE = 2,
    JPV_BUSY = 3,
    JPV_DISPOSED = 4,
    JPV_RESOURCE_LIMIT = 5,
    JPV_INTERNAL_ERROR = 6,
    JPV_CANCELLED = 7,
    JPV_TIMED_OUT = 8,
    JPV_LICENSE_MISSING = 100,
    JPV_LICENSE_FORMAT = 101,
    JPV_LICENSE_SIGNATURE = 102,
    JPV_LICENSE_UNKNOWN_KEY = 103,
    JPV_LICENSE_MACHINE = 104,
    JPV_LICENSE_PRODUCT = 105,
    JPV_LICENSE_FEATURE = 106,
    JPV_LICENSE_VERSION = 107,
    JPV_MACHINE_UNAVAILABLE = 108,
    JPV_LICENSE_STORAGE = 109
};
enum jpv_pixel_format { JPV_GRAY8 = 1, JPV_BGR24 = 2 };
typedef struct jpv_image_info {
    uint32_t struct_size, width, height, stride, pixel_format, reserved;
    uint64_t buffer_length;
    int32_t origin_x, origin_y;
} jpv_image_info;
JPV_API const char *__cdecl vision_version(void);
JPV_API const char *__cdecl vision_last_error(void);
JPV_API int32_t __cdecl vision_diagnostics(char **json);
JPV_API void __cdecl vision_string_free(char *text);
JPV_API int32_t __cdecl vision_machine_request(char **json);
JPV_API int32_t __cdecl vision_license_status(const char *store, const char *feature, char **json);
JPV_API int32_t __cdecl vision_license_import(const char *store, const char *file, char **json);
JPV_API int32_t __cdecl vision_node_catalog(char **json);
JPV_API int32_t __cdecl vision_recipe_seal(const char *recipe, char **json);
JPV_API int32_t __cdecl vision_engine_create(const char *options, jpv_handle *engine);
JPV_API void __cdecl vision_engine_release(jpv_handle engine);
JPV_API int32_t __cdecl vision_recipe_load(jpv_handle engine, const char *directory, jpv_handle *recipe);
JPV_API int32_t __cdecl vision_recipe_validate(jpv_handle engine, const char *recipe, char **diagnostics);
JPV_API void __cdecl vision_recipe_release(jpv_handle recipe);
JPV_API int32_t __cdecl vision_recipe_description(jpv_handle recipe, char **json);
JPV_API int32_t __cdecl vision_recipe_definition(jpv_handle recipe, char **json);
JPV_API int32_t __cdecl vision_session_create(jpv_handle recipe, jpv_handle *session);
JPV_API void __cdecl vision_session_release(jpv_handle session);
JPV_API int32_t __cdecl vision_frame_copy(const jpv_image_info *info, const uint8_t *data, jpv_handle *frame);
JPV_API int32_t __cdecl vision_frame_info(jpv_handle frame, jpv_image_info *info);
JPV_API int32_t __cdecl vision_frame_pixels(jpv_handle frame, uint8_t *target, uint64_t length);
JPV_API void __cdecl vision_frame_release(jpv_handle frame);
JPV_API int32_t __cdecl vision_run_create(jpv_handle session, jpv_handle frame, const char *request,
                                          jpv_handle *run);
JPV_API int32_t __cdecl vision_run_execute(jpv_handle run, jpv_handle *result);
JPV_API void __cdecl vision_run_cancel(jpv_handle run);
JPV_API void __cdecl vision_run_release(jpv_handle run);
// Per-node kernel bridge used by the C# workflow. Pixels and typed values stay native.
JPV_API int32_t __cdecl vision_kernel_begin(jpv_handle run, jpv_handle *kernel, char **info);
JPV_API int32_t __cdecl vision_kernel_input(jpv_handle kernel, const char *node, char **json);
JPV_API int32_t __cdecl vision_kernel_field(jpv_handle kernel, const char *node, const char *port,
                                           const char *field, char **json);
JPV_API int32_t __cdecl vision_kernel_step(jpv_handle kernel, const char *node,
                                          const char *parameters, char **json);
JPV_API int32_t __cdecl vision_kernel_publish(jpv_handle kernel, const char *output,
                                             int32_t include_json, char **json);
JPV_API int32_t __cdecl vision_kernel_release_node(jpv_handle kernel, const char *node);
JPV_API int32_t __cdecl vision_kernel_finish(jpv_handle kernel, int32_t failed, jpv_handle *result);
JPV_API void __cdecl vision_kernel_release(jpv_handle kernel);
JPV_API int32_t __cdecl vision_result_json(jpv_handle result, char **json);
JPV_API int32_t __cdecl vision_result_frame(jpv_handle result, const char *node, jpv_handle *frame);
JPV_API int32_t __cdecl vision_result_input_frame(jpv_handle result, const char *node, jpv_handle *frame);
JPV_API int32_t __cdecl vision_result_output_frame(jpv_handle result, const char *name, jpv_handle *frame);
JPV_API void __cdecl vision_result_release(jpv_handle result);
#ifdef __cplusplus
}
#endif

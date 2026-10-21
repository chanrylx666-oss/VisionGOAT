#pragma once
#include <stdint.h>

#ifdef _WIN32
#define JPC_API __declspec(dllexport)
#else
#define JPC_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Optional MVS adapter, independent of the licensed algorithm Runtime ABI.
   0=success, 1=read timeout, 2=SDK unavailable, 3=device busy/access denied,
   4=device error/disconnected, 5=resource limit, 6=invalid argument.
   Errors are UTF-8 in the caller-owned buffer. Handles are never shared with MVS callers.
   All camera operations must be serialized; stop/close follow completion of read.
   A successful read owns copied/converted pixels until jpc_mvs_frame_free. */
typedef struct JpcMvsFrameInfo {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t pixel_format; /* 1=Gray8, 2=Bgr24 */
    uint32_t reserved;
    uint64_t length;
    uint64_t frame_number;
    uint64_t device_timestamp;
    int64_t received_qpc; /* QueryPerformanceCounter immediately after SDK receipt. */
    const uint8_t *data;
} JpcMvsFrameInfo;

JPC_API int jpc_mvs_enumerate(char **json, char *error, uint32_t error_capacity);
JPC_API void jpc_mvs_string_free(char *text);
JPC_API int jpc_mvs_open(const char *serial, void **camera, char *error, uint32_t error_capacity);
JPC_API int jpc_mvs_start(void *camera, char *error, uint32_t error_capacity);
JPC_API int jpc_mvs_stop(void *camera, char *error, uint32_t error_capacity);
JPC_API void jpc_mvs_close(void *camera);
JPC_API int jpc_mvs_read(void *camera, uint32_t timeout_ms, JpcMvsFrameInfo *info, void **frame,
                         char *error, uint32_t error_capacity);
JPC_API void jpc_mvs_frame_free(void *frame);
/* settings JSON: exposure/gain/frameRate, each null or {value,min,max,writable}.
   Set is allowed while stopped only. Supported names: ExposureTime, Gain,
   AcquisitionFrameRate. Unsupported/read-only settings fail explicitly. */
JPC_API int jpc_mvs_settings(void *camera, char **json, char *error, uint32_t error_capacity);
JPC_API int jpc_mvs_set_float(void *camera, const char *name, double value,
                              char *error, uint32_t error_capacity);

#ifdef __cplusplus
}
static_assert(sizeof(JpcMvsFrameInfo) == 64, "MVS adapter requires Windows x64 ABI");
#endif

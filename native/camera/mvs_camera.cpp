#include "mvs_camera.h"
#include "json.hpp"
#include <windows.h>
#include <shlobj.h>
#include <MvCameraControl.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

namespace {
using Json = nlohmann::json;
constexpr uint64_t MaximumPixels = 32ULL * 1024 * 1024, MaximumBytes = 128ULL * 1024 * 1024;
constexpr uint32_t MaximumDimension = 32768;

struct Failure : std::runtime_error {
    int code;
    Failure(int value, const std::string &message) : std::runtime_error(message), code(value) {}
};

void require(bool condition, int code, const std::string &message) {
    if (!condition)
        throw Failure(code, message);
}

void error_text(char *target, uint32_t capacity, const std::string &message) noexcept {
    if (!target || !capacity)
        return;
    size_t length = std::min<size_t>(message.size(), capacity - 1);
    // Do not cut through a UTF-8 continuation sequence.
    while (length < message.size() && length && (static_cast<unsigned char>(message[length]) & 0xc0) == 0x80)
        --length;
    std::memcpy(target, message.data(), length);
    target[length] = 0;
}

template <typename Action> int invoke(char *error, uint32_t capacity, Action action) noexcept {
    error_text(error, capacity, "");
    try {
        action();
        return 0;
    } catch (const Failure &failure) {
        error_text(error, capacity, failure.what());
        return failure.code;
    } catch (const std::bad_alloc &) {
        error_text(error, capacity, "Camera memory allocation failed.");
        return 5;
    } catch (const std::exception &exception) {
        error_text(error, capacity, exception.what());
        return 4;
    } catch (...) {
        error_text(error, capacity, "Unexpected camera adapter error.");
        return 4;
    }
}

std::string hex_code(int code) {
    std::ostringstream stream;
    stream << "0x" << std::hex << std::uppercase << std::setw(8) << std::setfill('0') << uint32_t(code);
    return stream.str();
}

void sdk_check(int result, const char *operation) {
    if (result == MV_OK)
        return;
    const uint32_t code = uint32_t(result);
    int mapped = code == MV_E_ACCESS_DENIED || code == MV_E_RESOURCE_IN_USE ? 3
                 : code == MV_E_RESOURCE || code == MV_E_BUFOVER            ? 5
                                                                            : 4;
    throw Failure(mapped, std::string(operation) + " failed (" + hex_code(result) + ").");
}

char *json_string(const Json &value) {
    auto text = value.dump();
    auto result = std::make_unique<char[]>(text.size() + 1);
    std::memcpy(result.get(), text.c_str(), text.size() + 1);
    return result.release();
}

template <size_t Length> std::string sdk_text(const unsigned char (&value)[Length]) {
    auto end = std::find(std::begin(value), std::end(value), 0);
    std::string raw(reinterpret_cast<const char *>(value), size_t(end - std::begin(value)));
    if (raw.empty() ||
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, raw.data(), int(raw.size()), nullptr, 0) > 0)
        return raw;
    int count = MultiByteToWideChar(CP_ACP, 0, raw.data(), int(raw.size()), nullptr, 0);
    std::wstring wide(size_t(count), L'\0');
    MultiByteToWideChar(CP_ACP, 0, raw.data(), int(raw.size()), wide.data(), count);
    int bytes = WideCharToMultiByte(CP_UTF8, 0, wide.data(), count, nullptr, 0, nullptr, nullptr);
    std::string utf8(size_t(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), count, utf8.data(), bytes, nullptr, nullptr);
    return utf8;
}

std::string ip_address(unsigned int ip) {
    return std::to_string((ip >> 24) & 255) + "." + std::to_string((ip >> 16) & 255) + "." +
           std::to_string((ip >> 8) & 255) + "." + std::to_string(ip & 255);
}

std::vector<std::filesystem::path> installed_runtime_libraries() {
    std::vector<std::filesystem::path> libraries;
    // Both locations are Windows-owned installation roots. Never search the working
    // directory, a caller-controlled PATH, or an arbitrary SDK/environment override.
    for (const auto *folder : {&FOLDERID_ProgramFilesCommonX86, &FOLDERID_ProgramFilesCommon}) {
        PWSTR value = nullptr;
        HRESULT result = SHGetKnownFolderPath(*folder, KF_FLAG_DEFAULT, nullptr, &value);
        if (SUCCEEDED(result) && value) {
            std::filesystem::path library(value);
            library /= L"MVS";
            library /= L"Runtime";
            library /= L"Win64_x64";
            library /= L"MvCameraControl.dll";
            // LoadLibraryEx's DLL_LOAD_DIR processing requires native backslash paths;
            // mixed separators can find the main file but fail to resolve its siblings.
            library.make_preferred();
            if (std::find(libraries.begin(), libraries.end(), library) == libraries.end())
                libraries.push_back(std::move(library));
        }
        CoTaskMemFree(value);
    }
    return libraries;
}

std::string path_text(const std::filesystem::path &path) {
    const auto utf8 = path.u8string();
    return std::string(utf8.begin(), utf8.end());
}

struct Sdk {
    HMODULE module = nullptr;
    bool initialized = false;
    std::mutex enumerationMutex;
    decltype(&MV_CC_Initialize) initialize = nullptr;
    decltype(&MV_CC_Finalize) finalize = nullptr;
    decltype(&MV_CC_EnumDevices) enumerate = nullptr;
    decltype(&MV_CC_IsDeviceAccessible) accessible = nullptr;
    decltype(&MV_CC_CreateHandle) create = nullptr;
    decltype(&MV_CC_OpenDevice) open = nullptr;
    decltype(&MV_CC_CloseDevice) close = nullptr;
    decltype(&MV_CC_DestroyHandle) destroy = nullptr;
    decltype(&MV_CC_IsDeviceConnected) connected = nullptr;
    decltype(&MV_CC_GetOptimalPacketSize) optimalPacketSize = nullptr;
    decltype(&MV_CC_SetIntValueEx) setInteger = nullptr;
    decltype(&MV_CC_SetEnumValueByString) setEnumeration = nullptr;
    decltype(&MV_CC_GetEnumValue) getEnumeration = nullptr;
    decltype(&MV_CC_SetBoolValue) setBoolean = nullptr;
    decltype(&MV_CC_GetBoolValue) getBoolean = nullptr;
    decltype(&MV_CC_SetImageNodeNum) setImageNodes = nullptr;
    decltype(&MV_CC_SetGrabStrategy) setStrategy = nullptr;
    decltype(&MV_CC_StartGrabbing) start = nullptr;
    decltype(&MV_CC_StopGrabbing) stop = nullptr;
    decltype(&MV_CC_GetImageBuffer) getBuffer = nullptr;
    decltype(&MV_CC_FreeImageBuffer) freeBuffer = nullptr;
    decltype(&MV_CC_ConvertPixelTypeEx) convert = nullptr;
    decltype(&MV_XML_GetNodeAccessMode) accessMode = nullptr;
    decltype(&MV_CC_GetFloatValue) getFloat = nullptr;
    decltype(&MV_CC_SetFloatValue) setFloat = nullptr;

    template <typename Function> Function function(const char *name) {
        auto address = GetProcAddress(module, name);
        require(address != nullptr, 2, std::string("The installed MVS SDK lacks ") + name + ".");
        // Copy the Win32 procedure pointer without a calling-convention or struct-layout guess.
        static_assert(sizeof(Function) == sizeof(address));
        Function result;
        std::memcpy(&result, &address, sizeof(result));
        return result;
    }

    Sdk() {
        auto libraries = installed_runtime_libraries();
        require(!libraries.empty(), 2, "Cannot locate the Windows MVS installation directories.");
        std::string attempts;
        for (const auto &library : libraries) {
            std::error_code status;
            bool installed = std::filesystem::is_regular_file(library, status);
            if (installed) {
                module = LoadLibraryExW(library.c_str(), nullptr,
                                        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
                if (module)
                    break;
                DWORD failed = GetLastError();
                attempts += path_text(library) + " (Win32 " + std::to_string(failed) + "); ";
            } else
                attempts += path_text(library) + " (not installed); ";
        }
        require(module != nullptr, 2,
                "MVS x64 runtime or one of its installed dependencies could not be loaded. " + attempts);
        try {
#define JPC_LOAD(member, entry) member = function<decltype(member)>(#entry)
            JPC_LOAD(initialize, MV_CC_Initialize);
            JPC_LOAD(finalize, MV_CC_Finalize);
            JPC_LOAD(enumerate, MV_CC_EnumDevices);
            JPC_LOAD(accessible, MV_CC_IsDeviceAccessible);
            JPC_LOAD(create, MV_CC_CreateHandle);
            JPC_LOAD(open, MV_CC_OpenDevice);
            JPC_LOAD(close, MV_CC_CloseDevice);
            JPC_LOAD(destroy, MV_CC_DestroyHandle);
            JPC_LOAD(connected, MV_CC_IsDeviceConnected);
            JPC_LOAD(optimalPacketSize, MV_CC_GetOptimalPacketSize);
            JPC_LOAD(setInteger, MV_CC_SetIntValueEx);
            JPC_LOAD(setEnumeration, MV_CC_SetEnumValueByString);
            JPC_LOAD(getEnumeration, MV_CC_GetEnumValue);
            JPC_LOAD(setBoolean, MV_CC_SetBoolValue);
            JPC_LOAD(getBoolean, MV_CC_GetBoolValue);
            JPC_LOAD(setImageNodes, MV_CC_SetImageNodeNum);
            JPC_LOAD(setStrategy, MV_CC_SetGrabStrategy);
            JPC_LOAD(start, MV_CC_StartGrabbing);
            JPC_LOAD(stop, MV_CC_StopGrabbing);
            JPC_LOAD(getBuffer, MV_CC_GetImageBuffer);
            JPC_LOAD(freeBuffer, MV_CC_FreeImageBuffer);
            JPC_LOAD(convert, MV_CC_ConvertPixelTypeEx);
            JPC_LOAD(accessMode, MV_XML_GetNodeAccessMode);
            JPC_LOAD(getFloat, MV_CC_GetFloatValue);
            JPC_LOAD(setFloat, MV_CC_SetFloatValue);
#undef JPC_LOAD
            int result = initialize();
            require(result == MV_OK, 2, "MVS initialization failed (" + hex_code(result) + ").");
            initialized = true;
        } catch (...) {
            FreeLibrary(module);
            module = nullptr;
            throw;
        }
    }
    ~Sdk() {
        if (initialized)
            finalize();
        if (module)
            FreeLibrary(module);
    }
};

std::shared_ptr<Sdk> sdk() {
    static std::mutex mutex;
    static std::shared_ptr<Sdk> instance;
    std::lock_guard lock(mutex);
    if (!instance)
        instance = std::make_shared<Sdk>();
    return instance;
}

bool physical_gige(const MV_CC_DEVICE_INFO &info) {
    return info.nTLayerType == MV_GIGE_DEVICE;
}

struct Camera {
    std::shared_ptr<Sdk> api;
    void *handle = nullptr;
    bool opened = false, grabbing = false, faulted = false, closed = false;
    std::mutex mutex;
    explicit Camera(std::shared_ptr<Sdk> value) : api(std::move(value)) {}
    ~Camera() {
        if (!handle)
            return;
        if (grabbing)
            api->stop(handle);
        if (opened)
            api->close(handle);
        api->destroy(handle);
    }
    void ensure_connected() {
        require(!closed && handle && opened && !faulted, 4,
                "Camera is closed or faulted; reconnect before acquiring another image.");
        if (!api->connected(handle)) {
            faulted = true;
            throw Failure(4, "Camera disconnected; no cached image will be returned.");
        }
    }
};

std::mutex registryMutex;
std::unordered_map<void *, std::shared_ptr<Camera>> cameras;

std::shared_ptr<Camera> camera_handle(void *handle) {
    std::lock_guard lock(registryMutex);
    auto found = cameras.find(handle);
    require(found != cameras.end(), 6, "Unknown or closed camera handle.");
    return found->second;
}

struct OwnedFrame {
    std::vector<uint8_t> pixels;
};
std::mutex framesMutex;
std::unordered_map<void *, std::unique_ptr<OwnedFrame>> frames;

struct BufferLease {
    Camera &camera;
    MV_FRAME_OUT frame{};
    bool held = false;
    explicit BufferLease(Camera &value) : camera(value) {}
    ~BufferLease() {
        if (held && camera.api->freeBuffer(camera.handle, &frame) != MV_OK)
            camera.faulted = true;
    }
    void release() {
        if (!held)
            return;
        held = false;
        int result = camera.api->freeBuffer(camera.handle, &frame);
        if (result != MV_OK)
            camera.faulted = true;
        sdk_check(result, "MV_CC_FreeImageBuffer");
    }
};

bool mono_format(MvGvspPixelType type) {
    switch (type) {
    case PixelType_Gvsp_Mono1p:
    case PixelType_Gvsp_Mono2p:
    case PixelType_Gvsp_Mono4p:
    case PixelType_Gvsp_Mono8:
    case PixelType_Gvsp_Mono8_Signed:
    case PixelType_Gvsp_Mono10:
    case PixelType_Gvsp_Mono10_Packed:
    case PixelType_Gvsp_Mono12:
    case PixelType_Gvsp_Mono12_Packed:
    case PixelType_Gvsp_Mono14:
    case PixelType_Gvsp_Mono16:
        return true;
    default:
        return false;
    }
}

bool readable(MV_XML_AccessMode mode) {
    return mode == AM_RO || mode == AM_RW;
}

bool manual_writable(Camera &camera, const char *name, MV_XML_AccessMode mode) {
    if (camera.grabbing)
        return false;
    if (mode == AM_RW)
        return true;
    const char *automatic = std::strcmp(name, "ExposureTime") == 0 ? "ExposureAuto"
                            : std::strcmp(name, "Gain") == 0       ? "GainAuto"
                                                                   : nullptr;
    MV_XML_AccessMode controlMode = AM_Undefined;
    if (automatic) {
        MVCC_ENUMVALUE current{};
        return camera.api->accessMode(camera.handle, automatic, &controlMode) == MV_OK &&
               controlMode == AM_RW &&
               camera.api->getEnumeration(camera.handle, automatic, &current) == MV_OK &&
               current.nCurValue != 0;
    }
    if (std::strcmp(name, "AcquisitionFrameRate") == 0) {
        bool enabled = false;
        return camera.api->accessMode(camera.handle, "AcquisitionFrameRateEnable", &controlMode) == MV_OK &&
               controlMode == AM_RW &&
               camera.api->getBoolean(camera.handle, "AcquisitionFrameRateEnable", &enabled) == MV_OK &&
               !enabled;
    }
    return false;
}

Json float_setting(Camera &camera, const char *name) {
    MV_XML_AccessMode mode = AM_Undefined;
    MVCC_FLOATVALUE value{};
    if (camera.api->accessMode(camera.handle, name, &mode) != MV_OK || !readable(mode) ||
        camera.api->getFloat(camera.handle, name, &value) != MV_OK || !std::isfinite(value.fCurValue) ||
        !std::isfinite(value.fMin) || !std::isfinite(value.fMax) || value.fMin > value.fMax)
        return nullptr;
    return {{"value", value.fCurValue},
            {"min", value.fMin},
            {"max", value.fMax},
            {"writable", manual_writable(camera, name, mode)}};
}

void disable_auto(Camera &camera, const char *name) {
    MV_XML_AccessMode mode = AM_Undefined;
    int queried = camera.api->accessMode(camera.handle, name, &mode);
    if (queried != MV_OK || mode == AM_NI || mode == AM_NA)
        return; // Optional SFNC automatic-control node is absent/unavailable.
    require(readable(mode), 6, std::string(name) + " cannot be read to select manual control.");
    MVCC_ENUMVALUE current{};
    sdk_check(camera.api->getEnumeration(camera.handle, name, &current), "MV_CC_GetEnumValue");
    if (current.nCurValue == 0)
        return;
    require(mode == AM_RW, 6, std::string(name) + " is read-only; automatic control cannot be disabled.");
    sdk_check(camera.api->setEnumeration(camera.handle, name, "Off"), "Set automatic control Off");
}

void enable_frame_rate(Camera &camera) {
    constexpr const char *name = "AcquisitionFrameRateEnable";
    MV_XML_AccessMode mode = AM_Undefined;
    int queried = camera.api->accessMode(camera.handle, name, &mode);
    if (queried != MV_OK || mode == AM_NI || mode == AM_NA)
        return;
    require(readable(mode), 6, "AcquisitionFrameRateEnable cannot be read.");
    bool current = false;
    sdk_check(camera.api->getBoolean(camera.handle, name, &current), "MV_CC_GetBoolValue");
    if (current)
        return;
    require(mode == AM_RW, 6, "AcquisitionFrameRateEnable is read-only and disabled.");
    sdk_check(camera.api->setBoolean(camera.handle, name, true), "Enable AcquisitionFrameRate");
}
} // namespace

extern "C" {
int jpc_mvs_enumerate(char **json, char *error, uint32_t error_capacity) {
    if (json)
        *json = nullptr;
    return invoke(error, error_capacity, [&] {
        require(json != nullptr, 6, "An output JSON pointer is required.");
        auto api = sdk();
        std::lock_guard lock(api->enumerationMutex);
        MV_CC_DEVICE_INFO_LIST devices{};
        sdk_check(api->enumerate(MV_GIGE_DEVICE, &devices), "MV_CC_EnumDevices");
        require(devices.nDeviceNum <= MV_MAX_DEVICE_NUM, 5, "MVS device count exceeds its list capacity.");
        auto output = Json::array();
        for (unsigned int i = 0; i < devices.nDeviceNum; ++i) {
            const auto *info = devices.pDeviceInfo[i];
            if (!info || !physical_gige(*info))
                continue;
            const auto &gige = info->SpecialInfo.stGigEInfo;
            output.push_back({{"serial", sdk_text(gige.chSerialNumber)},
                              {"manufacturer", sdk_text(gige.chManufacturerName)},
                              {"model", sdk_text(gige.chModelName)},
                              {"userName", sdk_text(gige.chUserDefinedName)},
                              {"ipAddress", ip_address(gige.nCurrentIp)},
                              {"interfaceIp", ip_address(gige.nNetExport)},
                              {"transport", "GigE"},
                              {"accessible",
                               api->accessible(const_cast<MV_CC_DEVICE_INFO *>(info), MV_ACCESS_Exclusive)}});
        }
        *json = json_string(output);
    });
}

void jpc_mvs_string_free(char *text) {
    delete[] text;
}

int jpc_mvs_open(const char *serial, void **camera, char *error, uint32_t error_capacity) {
    if (camera)
        *camera = nullptr;
    return invoke(error, error_capacity, [&] {
        require(serial && camera, 6, "Serial number and output camera pointer are required.");
        size_t length = strnlen_s(serial, 129);
        require(length > 0 && length <= 128, 6, "The camera serial number is invalid.");
        auto api = sdk();
        MV_CC_DEVICE_INFO selected{};
        bool found = false;
        {
            std::lock_guard lock(api->enumerationMutex);
            MV_CC_DEVICE_INFO_LIST devices{};
            sdk_check(api->enumerate(MV_GIGE_DEVICE, &devices), "MV_CC_EnumDevices");
            require(devices.nDeviceNum <= MV_MAX_DEVICE_NUM, 5,
                    "MVS device count exceeds its list capacity.");
            for (unsigned int i = 0; i < devices.nDeviceNum; ++i)
                if (devices.pDeviceInfo[i] && physical_gige(*devices.pDeviceInfo[i]) &&
                    sdk_text(devices.pDeviceInfo[i]->SpecialInfo.stGigEInfo.chSerialNumber) == serial) {
                    require(!found, 6, "The serial number identifies more than one GigE camera.");
                    selected = *devices.pDeviceInfo[i];
                    found = true;
                }
        }
        require(found, 4, "The requested GigE camera is not present; refresh the device list.");
        auto object = std::make_shared<Camera>(api);
        sdk_check(api->create(&object->handle, &selected), "MV_CC_CreateHandle");
        require(object->handle != nullptr, 4, "MVS returned an empty camera handle.");
        sdk_check(api->open(object->handle, MV_ACCESS_Exclusive, 0), "MV_CC_OpenDevice");
        object->opened = true;
        int packetSize = api->optimalPacketSize(object->handle);
        if (packetSize > 0)
            api->setInteger(object->handle, "GevSCPSPacketSize", packetSize);
        // Packet optimization is optional. The camera's existing packet size remains usable
        // if the network cannot perform the probe or rejects this recommendation.
        {
            std::lock_guard lock(registryMutex);
            cameras.emplace(object.get(), object);
        }
        *camera = object.get();
    });
}

int jpc_mvs_start(void *camera, char *error, uint32_t error_capacity) {
    return invoke(error, error_capacity, [&] {
        auto object = camera_handle(camera);
        std::lock_guard lock(object->mutex);
        object->ensure_connected();
        if (object->grabbing)
            return;
        sdk_check(object->api->setEnumeration(object->handle, "AcquisitionMode", "Continuous"),
                  "Set AcquisitionMode Continuous");
        sdk_check(object->api->setEnumeration(object->handle, "TriggerMode", "Off"), "Set TriggerMode Off");
        sdk_check(object->api->setImageNodes(object->handle, 5), "MV_CC_SetImageNodeNum");
        sdk_check(object->api->setStrategy(object->handle, MV_GrabStrategy_LatestImagesOnly),
                  "MV_CC_SetGrabStrategy");
        sdk_check(object->api->start(object->handle), "MV_CC_StartGrabbing");
        object->grabbing = true;
    });
}

int jpc_mvs_stop(void *camera, char *error, uint32_t error_capacity) {
    return invoke(error, error_capacity, [&] {
        auto object = camera_handle(camera);
        std::lock_guard lock(object->mutex);
        require(!object->closed, 6, "Camera is closed.");
        if (!object->grabbing)
            return;
        int result = object->api->stop(object->handle);
        object->grabbing = false;
        if (result != MV_OK)
            object->faulted = true;
        sdk_check(result, "MV_CC_StopGrabbing");
    });
}

void jpc_mvs_close(void *camera) {
    std::shared_ptr<Camera> object;
    {
        std::lock_guard lock(registryMutex);
        auto found = cameras.find(camera);
        if (found == cameras.end())
            return;
        object = std::move(found->second);
        cameras.erase(found);
    }
    std::lock_guard lock(object->mutex);
    object->closed = true;
}

int jpc_mvs_read(void *camera, uint32_t timeout_ms, JpcMvsFrameInfo *info, void **frame, char *error,
                 uint32_t error_capacity) {
    uint32_t requestedSize = info ? info->struct_size : 0;
    if (frame)
        *frame = nullptr;
    if (info) {
        *info = {};
        info->struct_size = sizeof(JpcMvsFrameInfo);
    }
    return invoke(error, error_capacity, [&] {
        require(info && frame && (requestedSize == 0 || requestedSize == sizeof(JpcMvsFrameInfo)), 6,
                "A current JpcMvsFrameInfo and output frame pointer are required.");
        require(timeout_ms != INFINITE, 6, "An infinite camera timeout is not supported.");
        auto object = camera_handle(camera);
        std::lock_guard lock(object->mutex);
        object->ensure_connected();
        require(object->grabbing, 6, "Start the camera stream before reading a frame.");
        BufferLease buffer(*object);
        int received = object->api->getBuffer(object->handle, &buffer.frame, timeout_ms);
        LARGE_INTEGER receipt{};
        QueryPerformanceCounter(&receipt);
        if (received == MV_OK)
            buffer.held = true;
        object->ensure_connected();
        if (uint32_t(received) == MV_E_NODATA)
            throw Failure(1, "Camera read timed out; no previous frame is returned.");
        if (received != MV_OK)
            object->faulted = true;
        sdk_check(received, "MV_CC_GetImageBuffer");
        // FreeImageBuffer may clear the SDK's output structure. Keep frame identity and
        // dimensions in our own value before releasing the SDK lease.
        const auto source = buffer.frame.stFrameInfo;
        uint32_t width = source.nExtendWidth ? source.nExtendWidth : source.nWidth;
        uint32_t height = source.nExtendHeight ? source.nExtendHeight : source.nHeight;
        uint64_t sourceLength = source.nFrameLenEx ? source.nFrameLenEx : source.nFrameLen;
        require(buffer.frame.pBufAddr && width && height && sourceLength, 4,
                "MVS returned an empty camera image.");
        require(source.nLostPacket == 0, 4, "The camera frame has lost network packets and was discarded.");
        require(width <= MaximumDimension && height <= MaximumDimension &&
                    uint64_t(width) * height <= MaximumPixels && sourceLength <= MaximumBytes,
                5, "Camera frame exceeds 32768 pixels per edge, 32 Mi pixels or the 128 MiB buffer budget.");
        require(source.nSubImageNum == 0, 4, "Multipart camera images are not supported by this adapter.");
        bool mono = mono_format(source.enPixelType);
        uint32_t stride = width * (mono ? 1U : 3U);
        uint64_t length = uint64_t(stride) * height;
        require(length <= MaximumBytes, 5, "Converted camera image exceeds the buffer budget.");
        auto owned = std::make_unique<OwnedFrame>();
        owned->pixels.resize(size_t(length));
        MvGvspPixelType destination = mono ? PixelType_Gvsp_Mono8 : PixelType_Gvsp_BGR8_Packed;
        if (source.enPixelType == destination) {
            require(sourceLength >= length, 4,
                    "The acquired camera buffer is shorter than its image dimensions.");
            std::memcpy(owned->pixels.data(), buffer.frame.pBufAddr, size_t(length));
        } else {
            MV_CC_PIXEL_CONVERT_PARAM_EX conversion{};
            conversion.nWidth = width;
            conversion.nHeight = height;
            conversion.enSrcPixelType = source.enPixelType;
            conversion.pSrcData = buffer.frame.pBufAddr;
            conversion.nSrcDataLen = uint32_t(sourceLength);
            conversion.enDstPixelType = destination;
            conversion.pDstBuffer = owned->pixels.data();
            conversion.nDstBufferSize = uint32_t(length);
            sdk_check(object->api->convert(object->handle, &conversion), "MV_CC_ConvertPixelTypeEx");
            require(conversion.nDstLen == length, 4,
                    "MVS pixel conversion returned an unexpected image length.");
        }
        buffer.release();
        object->ensure_connected();
        JpcMvsFrameInfo complete{};
        complete.struct_size = sizeof(complete);
        complete.width = width;
        complete.height = height;
        complete.stride = stride;
        complete.pixel_format = mono ? 1 : 2;
        complete.length = length;
        complete.frame_number = source.nFrameNum;
        complete.device_timestamp = (uint64_t(source.nDevTimeStampHigh) << 32) | source.nDevTimeStampLow;
        complete.received_qpc = receipt.QuadPart;
        complete.data = owned->pixels.data();
        auto identity = owned.get();
        {
            std::lock_guard frameLock(framesMutex);
            frames.emplace(identity, std::move(owned));
        }
        *info = complete;
        *frame = identity;
    });
}

void jpc_mvs_frame_free(void *frame) {
    std::lock_guard lock(framesMutex);
    frames.erase(frame);
}

int jpc_mvs_settings(void *camera, char **json, char *error, uint32_t error_capacity) {
    if (json)
        *json = nullptr;
    return invoke(error, error_capacity, [&] {
        require(json != nullptr, 6, "An output JSON pointer is required.");
        auto object = camera_handle(camera);
        std::lock_guard lock(object->mutex);
        object->ensure_connected();
        Json result{{"exposure", float_setting(*object, "ExposureTime")},
                    {"gain", float_setting(*object, "Gain")},
                    {"frameRate", float_setting(*object, "AcquisitionFrameRate")}};
        object->ensure_connected();
        *json = json_string(result);
    });
}

int jpc_mvs_set_float(void *camera, const char *name, double value, char *error, uint32_t error_capacity) {
    return invoke(error, error_capacity, [&] {
        require(name && std::isfinite(value), 6, "A finite camera setting value is required.");
        std::string node(name, strnlen_s(name, 65));
        require(node == "ExposureTime" || node == "Gain" || node == "AcquisitionFrameRate", 6,
                "Only ExposureTime, Gain and AcquisitionFrameRate can be changed.");
        auto object = camera_handle(camera);
        std::lock_guard lock(object->mutex);
        object->ensure_connected();
        require(!object->grabbing, 6, "Stop the camera stream before changing acquisition settings.");
        // Reject a nonexistent node before touching its optional automatic/enable control.
        // An implemented but temporarily unavailable float can become writable after that
        // control changes; validate its actual range after the transition as well.
        MV_XML_AccessMode mode = AM_Undefined;
        require(object->api->accessMode(object->handle, node.c_str(), &mode) == MV_OK && mode != AM_NI, 6,
                "The camera does not implement this setting: " + node);
        auto current = float_setting(*object, node.c_str());
        require(std::abs(value) <= std::numeric_limits<float>::max(), 6,
                "The requested camera setting is outside the SDK float range: " + node);
        if (!current.is_null())
            require(value >= current.at("min").get<double>() && value <= current.at("max").get<double>(), 6,
                    "The requested camera setting is outside its supported range: " + node);
        else
            require(mode == AM_NA, 6, "The camera does not expose a readable setting: " + node);
        if (node == "ExposureTime")
            disable_auto(*object, "ExposureAuto");
        else if (node == "Gain")
            disable_auto(*object, "GainAuto");
        else
            enable_frame_rate(*object);
        current = float_setting(*object, node.c_str());
        mode = AM_Undefined;
        require(!current.is_null() && object->api->accessMode(object->handle, node.c_str(), &mode) == MV_OK &&
                    mode == AM_RW,
                6, "The camera setting is read-only or unavailable in the current mode: " + node);
        require(value >= current.at("min").get<double>() && value <= current.at("max").get<double>(), 6,
                "The camera setting range changed with acquisition mode: " + node);
        sdk_check(object->api->setFloat(object->handle, node.c_str(), float(value)), "MV_CC_SetFloatValue");
        object->ensure_connected();
    });
}
} // extern "C"

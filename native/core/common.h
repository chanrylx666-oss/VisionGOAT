#pragma once
#include "json.hpp"
#include "vision_api.h"
#include <atomic>
#include <windows.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>
#include <wincrypt.h>

namespace jp {
using Json = nlohmann::json;
namespace fs = std::filesystem;
struct Error : std::runtime_error {
    int code;
    Error(int value, const std::string &message) : std::runtime_error(message), code(value) {}
};
inline void require(bool ok, int code, const std::string &message) {
    if (!ok)
        throw Error(code, message);
}
inline fs::path path_utf8(const std::string &value) {
    return fs::path(std::u8string(value.begin(), value.end()));
}
inline std::string path_string(const fs::path &path) {
    auto text = path.u8string();
    return std::string(reinterpret_cast<const char *>(text.data()), text.size());
}
inline std::string read_file(const fs::path &path, size_t limit, int code) {
    HANDLE file =
        CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    require(file != INVALID_HANDLE_VALUE, code, "Cannot read file: " + path_string(path.filename()));
    struct CloseFile {
        HANDLE value;
        ~CloseFile() {
            CloseHandle(value);
        }
    } close{file};
    LARGE_INTEGER size{};
    require(GetFileSizeEx(file, &size) && size.QuadPart >= 0 && uint64_t(size.QuadPart) <= limit &&
                uint64_t(size.QuadPart) <= MAXDWORD,
            code, "File exceeds size limit.");
    std::string content(static_cast<size_t>(size.QuadPart), '\0');
    DWORD received = 0;
    require(ReadFile(file, content.data(), static_cast<DWORD>(content.size()), &received, nullptr) &&
                received == content.size(),
            code, "Incomplete file read.");
    return content;
}
inline Json strict_json(const std::string &content, int code) {
    std::vector<std::unordered_set<std::string>> keys;
    try {
        return Json::parse(content, [&](int depth, Json::parse_event_t event, Json &parsed) {
            require(depth <= 64, code, "JSON nesting limit exceeded.");
            if (event == Json::parse_event_t::object_start)
                keys.emplace_back();
            if (event == Json::parse_event_t::key)
                require(!keys.empty() && keys.back().insert(parsed.get<std::string>()).second, code,
                        "Duplicate JSON field.");
            if (event == Json::parse_event_t::object_end)
                keys.pop_back();
            return true;
        });
    } catch (const Error &) {
        throw;
    } catch (const std::exception &e) {
        throw Error(code, std::string("Invalid JSON: ") + e.what());
    }
}
inline std::vector<uint8_t> sha256_bytes(const uint8_t *bytes, size_t size) {
    require(size <= ULONG_MAX, JPV_RESOURCE_LIMIT, "Hash input exceeds limit.");
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0,
            JPV_INTERNAL_ERROR, "SHA-256 provider unavailable.");
    std::vector<uint8_t> output(32);
    NTSTATUS status = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0);
    if (status >= 0)
        status = BCryptHashData(hash, const_cast<PUCHAR>(bytes), static_cast<ULONG>(size), 0);
    if (status >= 0)
        status = BCryptFinishHash(hash, output.data(), 32, 0);
    if (hash)
        BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    require(status >= 0, JPV_INTERNAL_ERROR, "SHA-256 operation failed.");
    return output;
}
inline std::string sha256(const std::string &value) {
    auto bytes = sha256_bytes(reinterpret_cast<const uint8_t *>(value.data()), value.size());
    static constexpr char hex[] = "0123456789abcdef";
    std::string output;
    output.reserve(64);
    for (auto b : bytes) {
        output.push_back(hex[b >> 4]);
        output.push_back(hex[b & 15]);
    }
    return output;
}
inline std::vector<uint8_t> base64_decode(const std::string &value, size_t limit = 65536,
                                          int code = JPV_LICENSE_FORMAT) {
    require(!value.empty() && value.size() <= limit && value.size() % 4 == 0, code, "Invalid Base64 size.");
    size_t padding = 0;
    for (size_t i = 0; i < value.size(); ++i) {
        auto c = static_cast<unsigned char>(value[i]);
        if (c == '=') {
            ++padding;
            require(i >= value.size() - 2 && padding <= 2, code, "Invalid Base64 padding.");
        } else
            require(padding == 0 && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                                     (c >= '0' && c <= '9') || c == '+' || c == '/'),
                    code, "Invalid Base64 character.");
    }
    DWORD size = 0;
    require(CryptStringToBinaryA(value.c_str(), static_cast<DWORD>(value.size()), CRYPT_STRING_BASE64,
                                 nullptr, &size, nullptr, nullptr),
            code, "Base64 decode failed.");
    std::vector<uint8_t> bytes(size);
    require(CryptStringToBinaryA(value.c_str(), static_cast<DWORD>(value.size()), CRYPT_STRING_BASE64,
                                 bytes.data(), &size, nullptr, nullptr),
            code, "Base64 decode failed.");
    bytes.resize(size);
    return bytes;
}
inline void atomic_write(const fs::path &destination, const std::string &contents, int code) {
    static std::atomic<uint64_t> sequence{0};
    fs::path temporary = destination;
    temporary += "." + std::to_string(GetCurrentProcessId()) + "." + std::to_string(GetTickCount64()) + "." +
                 std::to_string(sequence.fetch_add(1)) + ".tmp";
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
    require(file != INVALID_HANDLE_VALUE, code, "Cannot create file; check write permissions.");
    DWORD written = 0;
    bool ok = WriteFile(file, contents.data(), static_cast<DWORD>(contents.size()), &written, nullptr) &&
              written == contents.size();
    if (ok)
        ok = FlushFileBuffers(file) != 0;
    CloseHandle(file);
    if (ok) {
        // Windows may briefly lock a destination while another process replaces
        // it. Retry only that bounded sharing/access race; retain the old grant
        // and report a storage error if it cannot be replaced.
        ok = false;
        for (int attempt = 0; attempt < 20; ++attempt) {
            if (MoveFileExW(temporary.c_str(), destination.c_str(),
                            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
                ok = true;
                break;
            }
            DWORD reason = GetLastError();
            if (reason != ERROR_SHARING_VIOLATION && reason != ERROR_ACCESS_DENIED)
                break;
            Sleep(10);
        }
    }
    if (!ok) {
        DeleteFileW(temporary.c_str());
        throw Error(code, "Cannot persist file atomically; existing file preserved.");
    }
}
inline int64_t integer(const Json &value, const std::string &name, int64_t min, int64_t max) {
    require(value.is_number_integer(), JPV_INVALID_ARGUMENT, name + " must be an integer.");
    int64_t number = value.get<int64_t>();
    require(number >= min && number <= max, JPV_INVALID_ARGUMENT, name + " is out of range.");
    return number;
}
} // namespace jp

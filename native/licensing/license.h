#pragma once
#include "common.h"
#include <mutex>

namespace jp {
Json machine_request();
fs::path default_license_store();
std::string license_status_name(int code);
class LicenseService {
    fs::path store_;
    mutable std::mutex mutex_;
    mutable std::string cachedHash_;
    mutable Json cachedPayload_;
    Json verify(const std::string &content) const;

  public:
    explicit LicenseService(const std::string &store = "")
        : store_(store.empty() ? default_license_store() : path_utf8(store)) {}
    Json check(const std::string &feature = "runtime") const;
    Json info(const std::string &feature = "runtime") const;
    Json import_file(const fs::path &file);
};
} // namespace jp

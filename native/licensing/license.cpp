#include "license.h"
#include "trusted_key.h"
#include <algorithm>
#include <cctype>
#include <future>
#include <sddl.h>
#include <shlobj.h>
#include <wbemidl.h>

namespace jp {
template <class T> struct Com {
    T *value = nullptr;
    ~Com() {
        if (value)
            value->Release();
    }
    T **out() {
        return &value;
    }
    T *operator->() const {
        return value;
    }
};
static std::string utf8(const std::wstring &value) {
    if (value.empty())
        return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0,
                                   nullptr, nullptr);
    std::string output(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), output.data(), size,
                        nullptr, nullptr);
    return output;
}
static std::string normalize(std::string value) {
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front())))
        value.erase(value.begin());
    while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
        value.pop_back();
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) {
        return c < 128 ? static_cast<char>(std::toupper(c)) : static_cast<char>(c);
    });
    return value;
}
static bool usable(const std::string &value) {
    if (value.size() < 5 || value == "NONE" || value == "DEFAULT STRING" ||
        value == "TO BE FILLED BY O.E.M." || value == "SYSTEM SERIAL NUMBER" || value == "UNKNOWN")
        return false;
    std::string compact;
    for (char c : value)
        if (c != '-' && c != '{' && c != '}' && c != ' ')
            compact += c;
    return !compact.empty() && compact.find_first_not_of('0') != std::string::npos &&
           compact.find_first_not_of('F') != std::string::npos;
}
static std::string wmi_value(IWbemServices *service, const wchar_t *query, const wchar_t *field) {
    Com<IEnumWbemClassObject> items;
    BSTR language = SysAllocString(L"WQL"), text = SysAllocString(query);
    HRESULT hr = service->ExecQuery(language, text, WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                                    nullptr, items.out());
    SysFreeString(language);
    SysFreeString(text);
    if (FAILED(hr))
        return {};
    Com<IWbemClassObject> item;
    ULONG count = 0;
    hr = items->Next(3000, 1, item.out(), &count);
    if (FAILED(hr) || count == 0)
        return {};
    VARIANT result;
    VariantInit(&result);
    hr = item->Get(field, 0, &result, nullptr, nullptr);
    std::string value;
    if (SUCCEEDED(hr) && result.vt == VT_BSTR && result.bstrVal)
        value = utf8(std::wstring(result.bstrVal, SysStringLen(result.bstrVal)));
    VariantClear(&result);
    return normalize(value);
}
static Json collect_machine() {
    HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    require(SUCCEEDED(init), JPV_MACHINE_UNAVAILABLE, "Machine identity COM initialization failed.");
    struct Apartment {
        ~Apartment() {
            CoUninitialize();
        }
    } apartment;
    HRESULT security = CoInitializeSecurity(nullptr, -1, nullptr, nullptr, RPC_C_AUTHN_LEVEL_DEFAULT,
                                            RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE, nullptr);
    require(SUCCEEDED(security) || security == RPC_E_TOO_LATE, JPV_MACHINE_UNAVAILABLE,
            "Machine identity security initialization failed.");
    Com<IWbemLocator> locator;
    require(SUCCEEDED(CoCreateInstance(CLSID_WbemLocator, nullptr, CLSCTX_INPROC_SERVER, IID_IWbemLocator,
                                       reinterpret_cast<void **>(locator.out()))),
            JPV_MACHINE_UNAVAILABLE, "WMI unavailable.");
    Com<IWbemServices> service;
    BSTR name = SysAllocString(L"ROOT\\CIMV2");
    HRESULT hr = locator->ConnectServer(name, nullptr, nullptr, nullptr, 0, nullptr, nullptr, service.out());
    SysFreeString(name);
    require(SUCCEEDED(hr), JPV_MACHINE_UNAVAILABLE, "Cannot connect to local machine identity provider.");
    require(
        SUCCEEDED(CoSetProxyBlanket(service.value, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, nullptr,
                                    RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE, nullptr, EOAC_NONE)),
        JPV_MACHINE_UNAVAILABLE, "Cannot initialize local identity provider.");
    Json profiles = Json::object();
    auto uuid = wmi_value(service.value, L"SELECT UUID FROM Win32_ComputerSystemProduct", L"UUID");
    if (usable(uuid) && uuid.size() == 36)
        profiles["smbios-uuid"] = sha256("JP-Vision|1|smbios-uuid|" + uuid);
    auto serial = wmi_value(service.value, L"SELECT SerialNumber FROM Win32_BaseBoard", L"SerialNumber");
    auto vendor = wmi_value(service.value, L"SELECT Manufacturer FROM Win32_BaseBoard", L"Manufacturer");
    if (usable(serial) && !vendor.empty())
        profiles["baseboard"] = sha256("JP-Vision|1|baseboard|" + vendor + "|" + serial);
    require(!profiles.empty(), JPV_MACHINE_UNAVAILABLE,
            "No reliable hardware identity; contact the developer.");
    return profiles;
}
static const Json &machine_profiles() {
    static const Json profiles = std::async(std::launch::async, collect_machine).get();
    return profiles;
}
Json machine_request() {
    const auto &profiles = machine_profiles();
    std::string profile = profiles.contains("smbios-uuid") ? "smbios-uuid" : "baseboard";
    return {{"requestVersion", 1},
            {"productId", "JP-Vision"},
            {"machineBinding",
             {{"fingerprintVersion", 1}, {"profile", profile}, {"fingerprint", profiles.at(profile)}}}};
}
fs::path default_license_store() {
    PWSTR directory = nullptr;
    require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &directory)),
            JPV_LICENSE_STORAGE, "ProgramData unavailable.");
    fs::path path(directory);
    CoTaskMemFree(directory);
    return path / L"JP-Vision";
}
std::string license_status_name(int code) {
    switch (code) {
    case JPV_OK:
        return "Valid";
    case JPV_LICENSE_MISSING:
        return "Missing";
    case JPV_LICENSE_FORMAT:
        return "InvalidFormat";
    case JPV_LICENSE_SIGNATURE:
        return "InvalidSignature";
    case JPV_LICENSE_UNKNOWN_KEY:
        return "UnknownKey";
    case JPV_LICENSE_MACHINE:
        return "MachineMismatch";
    case JPV_LICENSE_PRODUCT:
        return "WrongProduct";
    case JPV_LICENSE_FEATURE:
        return "FeatureNotLicensed";
    case JPV_LICENSE_VERSION:
        return "UnsupportedVersion";
    case JPV_MACHINE_UNAVAILABLE:
        return "MachineIdUnavailable";
    case JPV_LICENSE_STORAGE:
        return "StorageAccessDenied";
    default:
        return "Error";
    }
}
static bool verify_signature(const std::vector<uint8_t> &payload, const std::vector<uint8_t> &signature) {
    if (signature.size() != 64)
        return false;
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_KEY_HANDLE key = nullptr;
    require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_ECDSA_P256_ALGORITHM, nullptr, 0) >= 0,
            JPV_INTERNAL_ERROR, "ECDSA provider unavailable.");
    struct PublicBlob {
        BCRYPT_ECCKEY_BLOB header;
        uint8_t x[32];
        uint8_t y[32];
    } blob{};
    blob.header.dwMagic = BCRYPT_ECDSA_PUBLIC_P256_MAGIC;
    blob.header.cbKey = 32;
    std::copy(TrustX.begin(), TrustX.end(), blob.x);
    std::copy(TrustY.begin(), TrustY.end(), blob.y);
    NTSTATUS status = BCryptImportKeyPair(algorithm, nullptr, BCRYPT_ECCPUBLIC_BLOB, &key,
                                          reinterpret_cast<PUCHAR>(&blob), sizeof(blob), 0);
    if (status < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        throw Error(JPV_INTERNAL_ERROR, "Cannot import trusted public key.");
    }
    auto hash = sha256_bytes(payload.data(), payload.size());
    status =
        BCryptVerifySignature(key, nullptr, hash.data(), static_cast<ULONG>(hash.size()),
                              const_cast<PUCHAR>(signature.data()), static_cast<ULONG>(signature.size()), 0);
    BCryptDestroyKey(key);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return status >= 0;
}
Json LicenseService::verify(const std::string &content) const {
    try {
        require(content.size() <= 65536, JPV_LICENSE_FORMAT, "License exceeds 64 KiB.");
        auto envelope = strict_json(content, JPV_LICENSE_FORMAT);
        require(envelope.is_object() && envelope.contains("payload") && envelope.contains("signature"),
                JPV_LICENSE_FORMAT, "Missing license envelope fields.");
        auto payloadBytes = base64_decode(envelope.at("payload").get<std::string>());
        auto signature = base64_decode(envelope.at("signature").get<std::string>());
        auto payload = strict_json(std::string(payloadBytes.begin(), payloadBytes.end()), JPV_LICENSE_FORMAT);
        require(payload.at("keyId").get<std::string>() == TrustKeyId, JPV_LICENSE_UNKNOWN_KEY,
                "Unknown license signing key.");
        require(verify_signature(payloadBytes, signature), JPV_LICENSE_SIGNATURE,
                "License signature does not match.");
        require(payload.at("licenseFormatVersion") == 1, JPV_LICENSE_VERSION, "Unsupported license format.");
        require(payload.at("productId") == "JP-Vision", JPV_LICENSE_PRODUCT,
                "License belongs to another product.");
        require(payload.at("grantType") == "perpetual", JPV_LICENSE_VERSION,
                "This release requires a perpetual license.");
        require(payload.at("licenseId").is_string() && !payload.at("licenseId").get<std::string>().empty(),
                JPV_LICENSE_FORMAT, "Missing license ID.");
        const auto &binding = payload.at("machineBinding");
        require(binding.at("fingerprintVersion") == 1, JPV_LICENSE_VERSION,
                "Unsupported fingerprint version.");
        auto profile = binding.at("profile").get<std::string>();
        const auto &profiles = machine_profiles();
        require(profiles.contains(profile) && profiles.at(profile) == binding.at("fingerprint"),
                JPV_LICENSE_MACHINE, "License is bound to a different computer.");
        require(payload.at("features").is_array(), JPV_LICENSE_FORMAT, "Invalid license features.");
        for (const auto &feature : payload.at("features"))
            require(feature.is_string(), JPV_LICENSE_FORMAT, "Invalid feature type.");
        return payload;
    } catch (const Error &) {
        throw;
    } catch (const std::exception &) {
        throw Error(JPV_LICENSE_FORMAT, "License fields are missing or invalid.");
    }
}
Json LicenseService::check(const std::string &feature) const {
    std::lock_guard lock(mutex_);
    auto file = store_ / L"license.vlic";
    std::error_code accessError;
    bool exists = fs::exists(file, accessError);
    require(!accessError, JPV_LICENSE_STORAGE, "Cannot access the license store; check permissions.");
    require(exists, JPV_LICENSE_MISSING,
            "JP-Vision is not activated. Export a machine request and import a signed license.");
    auto content = read_file(file, 65536, JPV_LICENSE_STORAGE);
    auto hash = sha256(content);
    if (hash != cachedHash_) {
        auto verified = verify(content);
        cachedPayload_ = std::move(verified);
        cachedHash_ = hash;
    }
    bool allowed = feature.empty();
    for (const auto &item : cachedPayload_.at("features"))
        if (item == feature)
            allowed = true;
    require(allowed, JPV_LICENSE_FEATURE, "License does not grant the requested feature.");
    return cachedPayload_;
}
Json LicenseService::info(const std::string &feature) const {
    try {
        auto payload = check(feature);
        return {{"status", "Valid"},
                {"code", 0},
                {"licenseId", payload.at("licenseId")},
                {"customer", payload.value("customer", "Customer")},
                {"grantType", "perpetual"},
                {"store", path_string(store_)}};
    } catch (const Error &error) {
        return {{"status", license_status_name(error.code)},
                {"code", error.code},
                {"message", error.what()},
                {"store", path_string(store_)}};
    }
}
static void protect_new_store(const fs::path &store) {
    HANDLE token = nullptr;
    require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token), JPV_LICENSE_STORAGE,
            "Cannot query directory owner.");
    DWORD length = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &length);
    std::vector<uint8_t> data(length);
    bool ok = GetTokenInformation(token, TokenUser, data.data(), length, &length) != 0;
    CloseHandle(token);
    require(ok, JPV_LICENSE_STORAGE, "Cannot query directory owner.");
    LPWSTR sid = nullptr;
    require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER *>(data.data())->User.Sid, &sid),
            JPV_LICENSE_STORAGE, "Cannot query directory owner.");
    std::wstring sddl =
        L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + std::wstring(sid) + L")(A;OICI;GRGX;;;BU)";
    LocalFree(sid);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor,
                                                                 nullptr),
            JPV_LICENSE_STORAGE, "Cannot configure license directory security.");
    ok = SetFileSecurityW(store.c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                          descriptor) != 0;
    LocalFree(descriptor);
    require(ok, JPV_LICENSE_STORAGE, "Cannot protect license directory.");
}
Json LicenseService::import_file(const fs::path &file) {
    static std::mutex imports;
    std::lock_guard importing(imports);
    auto content = read_file(file, 65536, JPV_LICENSE_FORMAT);
    auto verified = verify(content);
    bool runtimeGranted = false;
    for (const auto &feature : verified.at("features"))
        if (feature == "runtime")
            runtimeGranted = true;
    require(runtimeGranted, JPV_LICENSE_FEATURE,
            "License does not grant the required runtime feature; existing license preserved.");
    std::error_code accessError;
    bool created = fs::create_directories(store_, accessError);
    require(!accessError, JPV_LICENSE_STORAGE, "Cannot create the license store; check permissions.");
    if (created && store_ == default_license_store())
        protect_new_store(store_);
    atomic_write(store_ / L"license.vlic", content, JPV_LICENSE_STORAGE);
    {
        std::lock_guard lock(mutex_);
        cachedHash_.clear();
        cachedPayload_ = Json();
    }
    return info();
}
} // namespace jp

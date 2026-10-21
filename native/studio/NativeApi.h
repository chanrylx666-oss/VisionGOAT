#pragma once
#define JPV_DYNAMIC_IMPORT
#include "vision_api.h"
#include <QCoreApplication>
#include <QImage>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLibrary>
#include <memory>
#include <stdexcept>

struct NativeFailure : std::runtime_error {
    int code;
    NativeFailure(int c, const QString &message)
        : std::runtime_error(message.toUtf8().constData()), code(c) {}
};
struct OwnedHandle {
    jpv_handle value = nullptr;
    void(__cdecl *release)(jpv_handle) = nullptr;
    OwnedHandle() = default;
    OwnedHandle(jpv_handle v, void(__cdecl *r)(jpv_handle)) : value(v), release(r) {}
    OwnedHandle(const OwnedHandle &) = delete;
    OwnedHandle &operator=(const OwnedHandle &) = delete;
    OwnedHandle(OwnedHandle &&other) noexcept : value(other.value), release(other.release) {
        other.value = nullptr;
    }
    OwnedHandle &operator=(OwnedHandle &&other) noexcept {
        if (this != &other) {
            reset();
            value = other.value;
            release = other.release;
            other.value = nullptr;
        }
        return *this;
    }
    ~OwnedHandle() {
        reset();
    }
    void reset() {
        if (value && release)
            release(value);
        value = nullptr;
    }
};

class NativeApi {
    QLibrary library_;
    QString store_;
    template <class T> T resolve(const char *name) {
        auto value = library_.resolve(name);
        if (!value)
            throw NativeFailure(6, "运行库缺少接口：" + QString::fromLatin1(name));
        return reinterpret_cast<T>(value);
    }

  public:
#define JPV_FUNCTION(name) decltype(&vision_##name) name = nullptr
    JPV_FUNCTION(version);
    JPV_FUNCTION(last_error);
    JPV_FUNCTION(string_free);
    JPV_FUNCTION(machine_request);
    JPV_FUNCTION(license_status);
    JPV_FUNCTION(license_import);
    JPV_FUNCTION(node_catalog);
    JPV_FUNCTION(recipe_seal);
    JPV_FUNCTION(engine_create);
    JPV_FUNCTION(engine_release);
    JPV_FUNCTION(recipe_load);
    JPV_FUNCTION(recipe_validate);
    JPV_FUNCTION(recipe_release);
    JPV_FUNCTION(session_create);
    JPV_FUNCTION(session_release);
    JPV_FUNCTION(frame_copy);
    JPV_FUNCTION(frame_info);
    JPV_FUNCTION(frame_pixels);
    JPV_FUNCTION(frame_release);
    JPV_FUNCTION(run_create);
    JPV_FUNCTION(run_execute);
    JPV_FUNCTION(run_cancel);
    JPV_FUNCTION(run_release);
    JPV_FUNCTION(result_json);
    JPV_FUNCTION(result_frame);
    JPV_FUNCTION(result_output_frame);
    JPV_FUNCTION(result_release);
#undef JPV_FUNCTION
    explicit NativeApi(QString store = {})
        : library_(QCoreApplication::applicationDirPath() + "/JPVision.Runtime.dll"),
          store_(std::move(store)) {
        library_.setLoadHints(QLibrary::PreventUnloadHint);
        if (!library_.load())
            throw NativeFailure(6, "无法加载视觉运行库：" + library_.errorString());
#define LOAD(name) name = resolve<decltype(name)>("vision_" #name)
        LOAD(version);
        LOAD(last_error);
        LOAD(string_free);
        LOAD(machine_request);
        LOAD(license_status);
        LOAD(license_import);
        LOAD(node_catalog);
        LOAD(recipe_seal);
        LOAD(engine_create);
        LOAD(engine_release);
        LOAD(recipe_load);
        LOAD(recipe_validate);
        LOAD(recipe_release);
        LOAD(session_create);
        LOAD(session_release);
        LOAD(frame_copy);
        LOAD(frame_info);
        LOAD(frame_pixels);
        LOAD(frame_release);
        LOAD(run_create);
        LOAD(run_execute);
        LOAD(run_cancel);
        LOAD(run_release);
        LOAD(result_json);
        LOAD(result_frame);
        LOAD(result_output_frame);
        LOAD(result_release);
#undef LOAD
    }
    void check(int code) const {
        if (code)
            throw NativeFailure(code, QString::fromUtf8(last_error()));
    }
    QJsonDocument take(int code, char *text) const {
        std::unique_ptr<char, decltype(string_free)> owner(text, string_free);
        check(code);
        if (!text)
            throw NativeFailure(6, "运行库返回空数据。");
        QJsonParseError error;
        auto document = QJsonDocument::fromJson(QByteArray(text), &error);
        if (error.error != QJsonParseError::NoError)
            throw NativeFailure(6, error.errorString());
        return document;
    }
    template <class F> QJsonDocument json(F callback) const {
        char *text = nullptr;
        int code = callback(&text);
        return take(code, text);
    }
    QJsonObject status() const {
        auto bytes = store_.toUtf8();
        return json([&](char **out) {
                   return license_status(bytes.isEmpty() ? nullptr : bytes.constData(), "studio", out);
               })
            .object();
    }
    QJsonObject importLicense(const QString &path) const {
        auto bytes = path.toUtf8(), store = store_.toUtf8();
        return json([&](char **out) {
                   return license_import(store.isEmpty() ? nullptr : store.constData(), bytes.constData(),
                                         out);
               })
            .object();
    }
    OwnedHandle engine() const {
        jpv_handle value = nullptr;
        auto options = QJsonDocument(QJsonObject{{"licenseStore", store_}}).toJson(QJsonDocument::Compact);
        check(engine_create(options.constData(), &value));
        return {value, engine_release};
    }
    OwnedHandle frame(const QImage &source) const {
        auto image = source.convertToFormat(QImage::Format_BGR888);
        jpv_image_info info{sizeof(jpv_image_info),
                            uint32_t(image.width()),
                            uint32_t(image.height()),
                            uint32_t(image.bytesPerLine()),
                            JPV_BGR24,
                            0,
                            uint64_t(image.sizeInBytes()),
                            0,
                            0};
        jpv_handle value = nullptr;
        check(frame_copy(&info, image.constBits(), &value));
        return {value, frame_release};
    }
    QImage image(jpv_handle frame, jpv_image_info *metadata = nullptr) const {
        jpv_image_info info{};
        info.struct_size = sizeof(info);
        check(frame_info(frame, &info));
        QByteArray pixels(qsizetype(info.buffer_length), Qt::Uninitialized);
        check(frame_pixels(frame, reinterpret_cast<uint8_t *>(pixels.data()), info.buffer_length));
        if (metadata)
            *metadata = info;
        return QImage(reinterpret_cast<const uchar *>(pixels.constData()), int(info.width), int(info.height),
                      int(info.stride),
                      info.pixel_format == JPV_GRAY8 ? QImage::Format_Grayscale8 : QImage::Format_BGR888)
            .copy();
    }
};

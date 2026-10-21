#include "StudioWindow.h"
#include "RegionGeometry.h"
#include "RegionEditor.h"
#include <QDialog>
#include <QDialogButtonBox>
#include <QFileDialog>
#include <QImageReader>
#include <QPushButton>
#include <QHBoxLayout>
#include <QVBoxLayout>
#include <QSet>
#include <cmath>
#include <numbers>

bool StudioWindow::supportsModuleRoi(QtNodes::NodeId id) const {
    if (!model_->nodeExists(id))
        return false;
    auto descriptor = model_->descriptor(model_->business(id)["type"].toString());
    for (auto p : descriptor["parameters"].toArray())
        if (p.toObject()["type"] == "roi")
            return true;
    return false;
}
QImage StudioWindow::upstreamImage(QtNodes::NodeId id, QPoint *origin) const {
    if (origin)
        *origin = {};
    if (original_.isNull())
        return {};
    QList<QtNodes::NodeId> ancestors;
    QSet<QtNodes::NodeId> seen;
    auto current = id;
    while (model_->nodeExists(current)) {
        QtNodes::NodeId parent = QtNodes::InvalidNodeId;
        for (auto edge : model_->allConnectionIds(current))
            if (edge.inNodeId == current) {
                parent = edge.outNodeId;
                break;
            }
        if (parent == QtNodes::InvalidNodeId)
            break;
        if (seen.contains(parent))
            throw std::runtime_error("上游图像存在环路。");
        seen.insert(parent);
        ancestors.prepend(parent);
        current = parent;
    }
    if (result_.value && !ancestors.isEmpty() &&
        outcome_.value("debugValues").toObject().contains(model_->stableId(ancestors.last()))) {
        jpv_handle pointer = nullptr;
        api_->check(api_->result_frame(result_.value, model_->stableId(ancestors.last()).toUtf8().constData(),
                                       &pointer));
        OwnedHandle frame(pointer, api_->frame_release);
        jpv_image_info info{};
        auto image = api_->image(frame.value, &info);
        if (origin)
            *origin = {info.origin_x, info.origin_y};
        return image;
    }
    QImage image = original_;
    QPoint offset;
    for (auto parent : ancestors) {
        auto business = model_->business(parent), p = business["parameters"].toObject();
        QJsonObject region;
        if (business["type"] == "CropRect")
            region = {{"shape", "rectangle"},
                      {"x", p["x"]},
                      {"y", p["y"]},
                      {"width", p["width"]},
                      {"height", p["height"]}};
        else
            region = p.value("roi").toObject();
        if (region.isEmpty() || region.value("shape") == "none")
            continue;
        auto bounds = RegionGeometry::pixelBounds(region, image.size());
        if (bounds.isEmpty() || !image.rect().contains(bounds))
            throw std::runtime_error("上游模块 ROI 超出图像，请先修正。");
        image = image.copy(bounds);
        offset += bounds.topLeft();
    }
    if (origin)
        *origin = offset;
    return image;
}
void StudioWindow::prepareModuleRoi(bool draw) {
    if (!supportsModuleRoi(selected_) || original_.isNull())
        return;
    try {
        QPoint origin;
        auto image = upstreamImage(selected_, &origin);
        imageView_->present(image, nodeOverlays(selected_), origin);
        auto value = model_->business(selected_)["parameters"].toObject().value("roi").toObject();
        if (value.isEmpty())
            value = {{"shape", "none"}};
        imageView_->setRegion(value);
        imageView_->addGuides(caliperGuides(selected_, image.size(), origin));
        if (draw) {
            imageView_->drawRegion(value.value("shape").toString("rectangle"));
            log_->appendPlainText(
                "编辑当前模块 ROI · 矩形拖动两角，圆形从圆心拖出半径，多边形点击顶点后双击或 Enter 完成。");
        }
    } catch (const std::exception &e) {
        showError(e);
    }
}
QJsonArray StudioWindow::caliperGuides(QtNodes::NodeId id, QSize size, QPoint origin) const {
    auto node = model_->business(id);
    auto type = node["type"].toString();
    QJsonArray guides;
    auto p = node["parameters"].toObject();
    if (type == "FindCircles" && p.value("method").toString("automatic") == "caliper")
        type = "CircleCaliper";
    if (type != "LineCaliper" && type != "CircleCaliper")
        return guides;
    auto meta = model_->descriptor(node["type"].toString());
    for (auto value : meta["parameters"].toArray()) {
        auto parameter = value.toObject();
        auto name = parameter["name"].toString();
        if (!p.contains(name))
            p[name] = parameter["default"];
    }
    auto bounds = RegionGeometry::pixelBounds(p.value("roi").toObject(), size);
    if (bounds.isEmpty())
        return guides;
    QPointF center =
        bounds.topLeft() + QPointF((bounds.width() - 1) / 2.0, (bounds.height() - 1) / 2.0) + origin;
    int count = p["caliperCount"].toInt();
    double search = p["searchLength"].toDouble(), width = p["projectionWidth"].toDouble(),
           angle = p["angleDegrees"].toDouble() * std::numbers::pi / 180;
    QPointF tangent(std::cos(angle), std::sin(angle)), normal(-tangent.y(), tangent.x());
    double length = p["guideLength"].toDouble();
    if (length == 0)
        length = .9 * std::min(bounds.width() / std::max(1e-8, std::abs(tangent.x())),
                               bounds.height() / std::max(1e-8, std::abs(tangent.y())));
    double radius = p["radius"].toDouble();
    if (radius == 0)
        radius = std::min(bounds.width() - 1, bounds.height() - 1) * .375;
    for (int i = 0; i < count; ++i) {
        QPointF at;
        if (type == "LineCaliper")
            at = center + tangent * (length * (double(i) / (count - 1) - .5));
        else {
            double start = p["startAngle"].toDouble(), range = p["endAngle"].toDouble() - start,
                   a = (start + range * i / (range >= 359.999 ? count : count - 1)) * std::numbers::pi / 180;
            normal = {std::cos(a), std::sin(a)};
            tangent = {-normal.y(), normal.x()};
            at = center + normal * radius;
        }
        QJsonArray points;
        for (auto point : {at - normal * (search / 2) - tangent * (width / 2),
                           at + normal * (search / 2) - tangent * (width / 2),
                           at + normal * (search / 2) + tangent * (width / 2),
                           at - normal * (search / 2) + tangent * (width / 2)})
            points.append(QJsonArray{point.x(), point.y()});
        guides.append(QJsonObject{{"points", points}, {"closed", true}, {"color", "#B88E49"}});
        auto a = at - normal * (search / 2), b = at + normal * (search / 2);
        guides.append(QJsonObject{{"points", QJsonArray{QJsonArray{a.x(), a.y()}, QJsonArray{b.x(), b.y()}}},
                                  {"color", "#725F43"}});
    }
    return guides;
}
QImage StudioWindow::upstreamMask(QtNodes::NodeId id) const {
    if (original_.isNull())
        return {};
    QList<QtNodes::NodeId> ancestors;
    QSet<QtNodes::NodeId> seen;
    auto current = id;
    while (model_->nodeExists(current)) {
        QtNodes::NodeId parent = QtNodes::InvalidNodeId;
        for (auto edge : model_->allConnectionIds(current))
            if (edge.inNodeId == current) {
                parent = edge.outNodeId;
                break;
            }
        if (parent == QtNodes::InvalidNodeId)
            break;
        if (seen.contains(parent))
            throw std::runtime_error("上游图像存在环路。");
        seen.insert(parent);
        ancestors.prepend(parent);
        current = parent;
    }
    QImage mask(original_.size(), QImage::Format_Grayscale8);
    mask.fill(255);
    for (auto parent : ancestors) {
        auto node = model_->business(parent), p = node["parameters"].toObject();
        auto region = p.value("roi").toObject();
        if (node["type"] == "CropRect")
            region = {{"shape", "rectangle"},
                      {"x", p["x"]},
                      {"y", p["y"]},
                      {"width", p["width"]},
                      {"height", p["height"]}};
        if (region.isEmpty() || region.value("shape") == "none")
            continue;
        auto bounds = RegionGeometry::pixelBounds(region, mask.size());
        if (bounds.isEmpty() || !mask.rect().contains(bounds))
            throw std::runtime_error("上游 ROI 越界。");
        auto selected = RegionGeometry::mask(region, mask.size()).copy(bounds);
        mask = mask.copy(bounds);
        for (int y = 0; y < mask.height(); ++y)
            for (int x = 0; x < mask.width(); ++x)
                mask.scanLine(y)[x] &= selected.constScanLine(y)[x];
    }
    return mask;
}
QJsonObject StudioWindow::templateModel(const QImage &image, const QJsonObject &roi,
                                        const QImage &validMask) const {
    auto bounds = RegionGeometry::pixelBounds(roi, image.size());
    if (bounds.width() < 4 || bounds.height() < 4 || bounds.width() > 512 || bounds.height() > 512 ||
        !image.rect().contains(bounds))
        throw std::runtime_error("模板区域必须在图像内，尺寸为 4–512 像素。");
    auto gray = image.copy(bounds).convertToFormat(QImage::Format_Grayscale8);
    auto mask = RegionGeometry::mask(roi, image.size()).copy(bounds);
    if (!validMask.isNull() && validMask.size() == image.size()) {
        auto valid = validMask.copy(bounds);
        for (int y = 0; y < mask.height(); ++y)
            for (int x = 0; x < mask.width(); ++x)
                mask.scanLine(y)[x] &= valid.constScanLine(y)[x];
    }
    QByteArray pixels, valid;
    double sum = 0, squares = 0;
    int count = 0;
    for (int y = 0; y < gray.height(); ++y) {
        pixels.append(reinterpret_cast<const char *>(gray.constScanLine(y)), gray.width());
        valid.append(reinterpret_cast<const char *>(mask.constScanLine(y)), mask.width());
        for (int x = 0; x < gray.width(); ++x)
            if (mask.constScanLine(y)[x]) {
                double n = gray.constScanLine(y)[x];
                sum += n;
                squares += n * n;
                ++count;
            }
    }
    if (count < 16 || squares / count - (sum / count) * (sum / count) < 1)
        throw std::runtime_error("模板必须包含灰度对比，不能使用纯色区域。");
    return {{"width", gray.width()},
            {"height", gray.height()},
            {"grayBase64", QString::fromLatin1(pixels.toBase64())},
            {"maskBase64", QString::fromLatin1(valid.toBase64())}};
}
void StudioWindow::teachTemplate(QtNodes::NodeId id, bool importImage) {
    if (!authorized_ || busy_ || !model_->nodeExists(id))
        return;
    try {
        QImage image;
        if (importImage) {
            auto path =
                QFileDialog::getOpenFileName(this, "导入模板图像", {}, "图像 (*.png *.bmp *.jpg *.tif)");
            if (path.isEmpty())
                return;
            QImageReader reader(path);
            auto size = reader.size();
            if (size.width() > 512 || size.height() > 512)
                throw std::runtime_error("导入模板不能超过 512 × 512。");
            image = reader.read();
        } else
            image = upstreamImage(id);
        if (image.isNull())
            throw std::runtime_error("请先打开图像。");
        QJsonObject model;
        if (importImage)
            model = templateModel(image, {{"shape", "rectangle"},
                                          {"x", 0},
                                          {"y", 0},
                                          {"width", image.width()},
                                          {"height", image.height()}});
        else {
            QDialog dialog(this);
            dialog.setWindowTitle("模板教学 · 框选特征区域");
            dialog.resize(1000, 680);
            auto layout = new QVBoxLayout(&dialog);
            auto body = new QHBoxLayout;
            auto view = new ImageView;
            view->present(image);
            auto roi = RegionGeometry::defaults("rectangle", image.size());
            view->setRegion(roi);
            auto editor = new RegionEditor(roi, image.size());
            editor->setFixedWidth(260);
            body->addWidget(view, 1);
            body->addWidget(editor);
            layout->addLayout(body, 1);
            connect(editor, &RegionEditor::valueChanged, &dialog,
                    [view](QJsonObject value) { view->setRegion(value); });
            connect(editor, &RegionEditor::editRequested, &dialog,
                    [view](QString shape) { view->drawRegion(shape); });
            connect(view, &ImageView::regionSelected, &dialog,
                    [editor](QJsonObject value) { editor->setValue(value); });
            auto note = new QLabel("选取有辨识度的特征。矩形、圆形、多边形均可；模板及掩膜随配方保存。");
            layout->addWidget(note);
            auto buttons = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel);
            buttons->button(QDialogButtonBox::Ok)->setText("学习模板");
            buttons->button(QDialogButtonBox::Cancel)->setText("取消");
            layout->addWidget(buttons);
            connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
            connect(buttons, &QDialogButtonBox::accepted, &dialog, [&] {
                try {
                    model = templateModel(image, editor->value(), upstreamMask(id));
                    dialog.accept();
                } catch (const std::exception &e) {
                    note->setText(QString::fromUtf8(e.what()));
                }
            });
            if (dialog.exec() != QDialog::Accepted)
                return;
        }
        changeParameter(id, "model", model);
        inspect(id);
        prepareModuleRoi();
        log_->appendPlainText(QString("模板已学习 · %1 × %2 · 保存配方后可供 WPF 使用。")
                                  .arg(model["width"].toInt())
                                  .arg(model["height"].toInt()));
    } catch (const std::exception &e) {
        showError(e);
    }
}

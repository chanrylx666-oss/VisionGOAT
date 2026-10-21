#include "ImageView.h"
#include "RegionItem.h"
#include "RegionGeometry.h"
#include <QGraphicsPathItem>
#include <QJsonArray>
#include <QKeyEvent>
#include <QMouseEvent>
#include <QTimer>
#include <cmath>

void ImageView::setRegion(QJsonObject region, bool editable) {
    if (region_) {
        scene_.removeItem(region_);
        delete region_;
        region_ = nullptr;
    }
    if (image_.isNull() || region.value("shape").toString() == "none" || region.isEmpty())
        return;
    region_ = new RegionItem(region, image_.size(), editable);
    region_->changed = [this](QJsonObject changed) {
        QTimer::singleShot(0, this, [this, changed] { Q_EMIT regionSelected(changed); });
    };
    scene_.addItem(region_);
}
QJsonObject ImageView::region() const {
    return region_ ? region_->region() : QJsonObject{{"shape", "none"}};
}
void ImageView::drawRegion(QString shape) {
    if (image_.isNull() || !QStringList{"rectangle", "circle", "polygon"}.contains(shape))
        return;
    cancelRegionDrawing();
    clearRoi();
    regionDrawing_ = shape;
    setRoiMode(false);
    setDragMode(NoDrag);
    setFocus();
    viewport()->setCursor(Qt::CrossCursor);
    if (region_)
        region_->setVisible(false);
}
void ImageView::cancelRegionDrawing() {
    if (draftRegion_) {
        scene_.removeItem(draftRegion_);
        delete draftRegion_;
        draftRegion_ = nullptr;
    }
    regionDrawing_.clear();
    polygonDraft_.clear();
    regionPressed_ = false;
    setDragMode(ScrollHandDrag);
    viewport()->setCursor(Qt::ArrowCursor);
    if (region_)
        region_->setVisible(true);
}
void ImageView::updateRegionDraft(QPointF point) {
    QPainterPath path;
    if (regionDrawing_ == "polygon") {
        auto points = polygonDraft_;
        points << point;
        path.addPolygon(points);
    } else if (regionDrawing_ == "circle") {
        double maximum = std::min({regionPress_.x(), regionPress_.y(), image_.width() - 1 - regionPress_.x(),
                                   image_.height() - 1 - regionPress_.y()});
        double radius = std::clamp(QLineF(regionPress_, point).length(), 0.0, std::max(0.0, maximum));
        path.addEllipse(regionPress_, radius, radius);
    } else
        path.addRect(QRectF(regionPress_, point).normalized());
    if (!draftRegion_) {
        QPen pen(QColor("#FF3038"), 1.5, Qt::DashLine);
        pen.setCosmetic(true);
        draftRegion_ = scene_.addPath(path, pen, QColor(255, 48, 56, 18));
        draftRegion_->setZValue(45);
    } else
        draftRegion_->setPath(path);
}
bool ImageView::regionMousePress(QMouseEvent *event) {
    if (event->button() != Qt::LeftButton || image_.isNull())
        return false;
    if (regionDrawing_.isEmpty() && region_ && event->modifiers().testFlag(Qt::ShiftModifier))
        drawRegion(region().value("shape").toString());
    if (regionDrawing_.isEmpty())
        return false;
    auto raw = mapToScene(event->position().toPoint());
    QPointF point(std::clamp(qRound(raw.x()), 0, image_.width() - 1),
                  std::clamp(qRound(raw.y()), 0, image_.height() - 1));
    if (regionDrawing_ == "polygon") {
        if (polygonDraft_.size() >= 3 &&
            QLineF(point, polygonDraft_.first()).length() * transform().m11() < 8) {
            finishPolygon();
            event->accept();
            return true;
        }
        if (polygonDraft_.size() < 64 &&
            (polygonDraft_.isEmpty() || QLineF(point, polygonDraft_.last()).length() >= 1))
            polygonDraft_ << point;
    } else {
        regionPress_ = point;
        regionPressed_ = true;
    }
    updateRegionDraft(point);
    event->accept();
    return true;
}
bool ImageView::regionMouseMove(QMouseEvent *event) {
    if (regionDrawing_.isEmpty() || (regionDrawing_ != "polygon" && !regionPressed_))
        return false;
    auto raw = mapToScene(event->position().toPoint());
    QPointF point(std::clamp(qRound(raw.x()), 0, image_.width() - 1),
                  std::clamp(qRound(raw.y()), 0, image_.height() - 1));
    updateRegionDraft(point);
    event->accept();
    return true;
}
bool ImageView::regionMouseRelease(QMouseEvent *event) {
    if (regionDrawing_.isEmpty() || event->button() != Qt::LeftButton)
        return false;
    event->accept();
    if (regionDrawing_ == "polygon")
        return true;
    if (!regionPressed_)
        return true;
    QJsonObject result;
    auto path = draftRegion_ ? draftRegion_->path() : QPainterPath();
    auto box = path.boundingRect();
    if (regionDrawing_ == "rectangle" && box.width() >= 1 && box.height() >= 1)
        result = {{"shape", "rectangle"},
                  {"x", qRound(box.x())},
                  {"y", qRound(box.y())},
                  {"width", qRound(box.width())},
                  {"height", qRound(box.height())}};
    if (regionDrawing_ == "circle" && box.width() >= 2)
        result = {{"shape", "circle"},
                  {"cx", regionPress_.x()},
                  {"cy", regionPress_.y()},
                  {"radius", box.width() / 2}};
    cancelRegionDrawing();
    if (!result.isEmpty()) {
        setRegion(result);
        Q_EMIT regionSelected(result);
    }
    Q_EMIT regionDrawingFinished();
    return true;
}
void ImageView::finishPolygon() {
    if (!RegionGeometry::validPolygon(polygonDraft_))
        return;
    QJsonArray points;
    for (auto p : polygonDraft_)
        points.append(QJsonArray{p.x(), p.y()});
    QJsonObject result{{"shape", "polygon"}, {"points", points}};
    cancelRegionDrawing();
    setRegion(result);
    Q_EMIT regionSelected(result);
    Q_EMIT regionDrawingFinished();
}
void ImageView::mouseDoubleClickEvent(QMouseEvent *event) {
    if (regionDrawing_ == "polygon") {
        finishPolygon();
        event->accept();
        return;
    }
    QGraphicsView::mouseDoubleClickEvent(event);
}
bool ImageView::regionKeyPress(QKeyEvent *event) {
    if (!regionDrawing_.isEmpty()) {
        if (event->key() == Qt::Key_Escape) {
            cancelRegionDrawing();
            Q_EMIT regionDrawingFinished();
            event->accept();
            return true;
        }
        if (event->key() == Qt::Key_Return || event->key() == Qt::Key_Enter) {
            finishPolygon();
            event->accept();
            return true;
        }
        if (event->key() == Qt::Key_Backspace && regionDrawing_ == "polygon" && !polygonDraft_.isEmpty()) {
            polygonDraft_.removeLast();
            updateRegionDraft(polygonDraft_.isEmpty() ? QPointF() : polygonDraft_.last());
            event->accept();
            return true;
        }
        return false;
    }
    if (!region_)
        return false;
    int dx = event->key() == Qt::Key_Right  ? 1
             : event->key() == Qt::Key_Left ? -1
                                            : 0,
        dy = event->key() == Qt::Key_Down ? 1
             : event->key() == Qt::Key_Up ? -1
                                          : 0;
    if (!dx && !dy)
        return false;
    int step = event->modifiers().testFlag(Qt::ControlModifier) ? 10 : 1;
    auto value = region();
    auto type = value.value("shape").toString();
    auto bounds = RegionGeometry::path(value).boundingRect();
    double limit = type == "rectangle" ? 0 : 1;
    double x = std::clamp(double(dx * step), -bounds.left(), image_.width() - limit - bounds.right()),
           y = std::clamp(double(dy * step), -bounds.top(), image_.height() - limit - bounds.bottom());
    if (type == "rectangle") {
        value["x"] = qRound(value["x"].toDouble() + x);
        value["y"] = qRound(value["y"].toDouble() + y);
    } else if (type == "circle") {
        value["cx"] = value["cx"].toDouble() + x;
        value["cy"] = value["cy"].toDouble() + y;
    } else {
        QJsonArray points;
        for (auto p : RegionGeometry::points(value))
            points.append(QJsonArray{p.x() + x, p.y() + y});
        value["points"] = points;
    }
    setRegion(value);
    Q_EMIT regionSelected(value);
    event->accept();
    return true;
}
void ImageView::addGuides(const QJsonArray &guides) {
    for (auto v : guides) {
        auto item = v.toObject();
        QPainterPath path;
        auto points = item["points"].toArray();
        bool first = true;
        for (auto a : points) {
            auto p = a.toArray();
            QPointF point(p[0].toDouble() - origin_.x(), p[1].toDouble() - origin_.y());
            if (first) {
                path.moveTo(point);
                first = false;
            } else
                path.lineTo(point);
        }
        if (item["closed"].toBool())
            path.closeSubpath();
        QPen pen(QColor(item["color"].toString("#E4AF56")), 1);
        pen.setCosmetic(true);
        auto guide = scene_.addPath(path, pen);
        guide->setZValue(10);
    }
}

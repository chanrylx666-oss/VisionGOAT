#include "ImageView.h"
#include <QGraphicsPathItem>
#include <QGraphicsSimpleTextItem>
#include <QJsonObject>
#include <QMouseEvent>
#include <QKeyEvent>
#include <QPainter>
#include <QPainterPath>
#include <QWheelEvent>

ImageView::ImageView(QWidget *parent) : QGraphicsView(parent) {
    setScene(&scene_);
    setBackgroundBrush(QColor("#09090B"));
    setRenderHint(QPainter::Antialiasing);
    setDragMode(ScrollHandDrag);
    setTransformationAnchor(AnchorUnderMouse);
    setViewportUpdateMode(BoundingRectViewportUpdate);
    setMouseTracking(true);
    setFrameShape(QFrame::NoFrame);
    setFocusPolicy(Qt::StrongFocus);
}
void ImageView::present(const QImage &image, const QJsonArray &overlays, QPoint origin) {
    bool changed = image_.size() != image.size();
    image_ = image;
    origin_ = origin;
    scene_.clear();
    roi_ = nullptr;
    roiEditable_ = false;
    roiDrag_ = -2;
    region_ = nullptr;
    draftRegion_ = nullptr;
    regionDrawing_.clear();
    polygonDraft_.clear();
    regionPressed_ = false;
    scene_.addPixmap(QPixmap::fromImage(image));
    scene_.setSceneRect(0, 0, image.width(), image.height());
    for (const auto &value : overlays) {
        auto item = value.toObject();
        QColor color(item["color"].toString("#44D7B6"));
        QPen pen(color, item["lineWidth"].toDouble(2));
        pen.setCosmetic(true);
        auto type = item["type"].toString();
        if (type == "polyline" || type == "line") {
            QPainterPath path;
            bool first = true;
            for (const auto &v : item["points"].toArray()) {
                auto p = v.toArray();
                QPointF point(p[0].toDouble() - origin.x(), p[1].toDouble() - origin.y());
                if (first) {
                    path.moveTo(point);
                    first = false;
                } else
                    path.lineTo(point);
            }
            if (item["closed"].toBool())
                path.closeSubpath();
            scene_.addPath(path, pen);
        } else if (type == "circle") {
            double rx = item["radiusX"].toDouble(item["radius"].toDouble());
            double ry = item["radiusY"].toDouble(item["radius"].toDouble());
            scene_.addEllipse(item["x"].toDouble() - origin.x() - rx, item["y"].toDouble() - origin.y() - ry,
                              2 * rx, 2 * ry, pen);
        } else if (type == "rectangle") {
            scene_.addRect(item["x"].toDouble() - origin.x(), item["y"].toDouble() - origin.y(),
                           item["width"].toDouble(), item["height"].toDouble(), pen);
        } else if (type == "text") {
            auto text = scene_.addSimpleText(item["text"].toString());
            text->setBrush(color);
            text->setFlag(QGraphicsItem::ItemIgnoresTransformations);
            text->setPos(item["x"].toDouble() - origin.x(), item["y"].toDouble() - origin.y());
        }
    }
    if (changed)
        fit();
    Q_EMIT imageChanged();
}
void ImageView::fit() {
    fitMode_ = true;
    if (!image_.isNull()) {
        fitInView(QRectF(0, 0, image_.width(), image_.height()), Qt::KeepAspectRatio);
        Q_EMIT zoomChanged(transform().m11() * 100);
    }
}
void ImageView::setRoiMode(bool value) {
    roiMode_ = value;
    setDragMode(value ? NoDrag : ScrollHandDrag);
    viewport()->setCursor(value ? Qt::CrossCursor : Qt::ArrowCursor);
}
void ImageView::setRoiRect(QRect rectangle, bool editable) {
    rectangle = rectangle.intersected(image_.rect());
    if (rectangle.isEmpty()) {
        clearRoi();
        return;
    }
    if (!roi_) {
        QPen pen(QColor("#FF3038"), 1.5);
        pen.setCosmetic(true);
        roi_ = scene_.addRect(QRectF(rectangle), pen, QColor(255, 48, 56, 18));
        roi_->setZValue(20);
    } else
        roi_->setRect(rectangle);
    roiEditable_ = editable;
    viewport()->update();
}
QRect ImageView::roiRect() const {
    return roi_ ? roi_->rect().toAlignedRect().intersected(image_.rect()) : QRect();
}
void ImageView::clearRoi() {
    if (roi_) {
        scene_.removeItem(roi_);
        delete roi_;
    }
    roi_ = nullptr;
    roiEditable_ = false;
    roiDrag_ = -2;
    viewport()->update();
}
QVector<QPointF> ImageView::roiHandles() const {
    if (!roi_)
        return {};
    auto r = roi_->rect();
    return {r.topLeft(),     {r.center().x(), r.top()},    r.topRight(),   {r.right(), r.center().y()},
            r.bottomRight(), {r.center().x(), r.bottom()}, r.bottomLeft(), {r.left(), r.center().y()}};
}
int ImageView::hitRoi(QPoint point) const {
    if (!roiEditable_ || !roi_)
        return -2;
    auto handles = roiHandles();
    for (int i = 0; i < handles.size(); ++i)
        if (QLineF(QPointF(point), QPointF(mapFromScene(handles[i]))).length() <= 7)
            return i;
    return roi_->rect().contains(mapToScene(point)) ? 8 : -2;
}
void ImageView::updateRoiCursor(QPoint point) {
    if (roiMode_) {
        viewport()->setCursor(Qt::CrossCursor);
        return;
    }
    int hit = hitRoi(point);
    if (hit == 0 || hit == 4)
        viewport()->setCursor(Qt::SizeFDiagCursor);
    else if (hit == 2 || hit == 6)
        viewport()->setCursor(Qt::SizeBDiagCursor);
    else if (hit == 1 || hit == 5)
        viewport()->setCursor(Qt::SizeVerCursor);
    else if (hit == 3 || hit == 7)
        viewport()->setCursor(Qt::SizeHorCursor);
    else
        viewport()->setCursor(hit == 8 ? Qt::SizeAllCursor : Qt::ArrowCursor);
}
void ImageView::setZoomPercent(double percent) {
    if (image_.isNull() || percent <= 0)
        return;
    fitMode_ = false;
    QTransform matrix;
    matrix.scale(std::clamp(percent / 100, .02, 100.0), std::clamp(percent / 100, .02, 100.0));
    setTransform(matrix);
    Q_EMIT zoomChanged(transform().m11() * 100);
}
void ImageView::wheelEvent(QWheelEvent *event) {
    if (image_.isNull())
        return;
    double factor = event->angleDelta().y() > 0 ? 1.15 : 1 / 1.15;
    auto current = transform().m11();
    if (current * factor > .02 && current * factor < 100) {
        fitMode_ = false;
        scale(factor, factor);
        Q_EMIT zoomChanged(transform().m11() * 100);
    }
    event->accept();
}
void ImageView::mousePressEvent(QMouseEvent *event) {
    if (regionMousePress(event))
        return;
    if (event->button() == Qt::LeftButton && !image_.isNull()) {
        auto point = mapToScene(event->position().toPoint());
        roiStart_ = {double(std::clamp(qRound(point.x()), 0, image_.width())),
                     double(std::clamp(qRound(point.y()), 0, image_.height()))};
        roiDrag_ = roiMode_ || (roiEditable_ && event->modifiers().testFlag(Qt::ShiftModifier))
                       ? -1
                       : hitRoi(event->position().toPoint());
        if (roiDrag_ != -2) {
            setFocus();
            if (roiDrag_ == -1) {
                clearRoi();
                roiDrag_ = -1;
                QPen pen(QColor("#FF3038"), 1.5);
                pen.setCosmetic(true);
                roi_ = scene_.addRect(QRectF(roiStart_, roiStart_), pen, QColor(255, 48, 56, 18));
                roi_->setZValue(20);
            }
            pressRect_ = roi_->rect();
            event->accept();
            return;
        }
    }
    QGraphicsView::mousePressEvent(event);
}
void ImageView::mouseMoveEvent(QMouseEvent *event) {
    auto point = mapToScene(event->position().toPoint());
    int x = int(point.x()), y = int(point.y());
    if (!image_.isNull() && x >= 0 && y >= 0 && x < image_.width() && y < image_.height()) {
        auto pixel = image_.pixelColor(x, y);
        Q_EMIT pixelChanged(QString("X %1  Y %2 · R %3 G %4 B %5")
                                .arg(x + origin_.x())
                                .arg(y + origin_.y())
                                .arg(pixel.red())
                                .arg(pixel.green())
                                .arg(pixel.blue()));
    }
    if (regionMouseMove(event))
        return;
    if (roiDrag_ != -2 && roi_ && (event->buttons() & Qt::LeftButton)) {
        point.setX(std::clamp(qRound(point.x()), 0, image_.width()));
        point.setY(std::clamp(qRound(point.y()), 0, image_.height()));
        QRectF rect = pressRect_;
        if (roiDrag_ == -1)
            rect = QRectF(roiStart_, point).normalized();
        else if (roiDrag_ == 8) {
            auto delta = mapToScene(event->position().toPoint()) - roiStart_;
            delta = {double(qRound(delta.x())), double(qRound(delta.y()))};
            rect.moveLeft(std::clamp(pressRect_.left() + delta.x(), 0.0, image_.width() - rect.width()));
            rect.moveTop(std::clamp(pressRect_.top() + delta.y(), 0.0, image_.height() - rect.height()));
        } else {
            if (roiDrag_ == 0 || roiDrag_ == 6 || roiDrag_ == 7)
                rect.setLeft(std::min(point.x(), rect.right() - 1));
            if (roiDrag_ == 2 || roiDrag_ == 3 || roiDrag_ == 4)
                rect.setRight(std::max(point.x(), rect.left() + 1));
            if (roiDrag_ == 0 || roiDrag_ == 1 || roiDrag_ == 2)
                rect.setTop(std::min(point.y(), rect.bottom() - 1));
            if (roiDrag_ == 4 || roiDrag_ == 5 || roiDrag_ == 6)
                rect.setBottom(std::max(point.y(), rect.top() + 1));
        }
        roi_->setRect(rect);
        viewport()->update();
        event->accept();
        return;
    }
    updateRoiCursor(event->position().toPoint());
    QGraphicsView::mouseMoveEvent(event);
}
void ImageView::mouseReleaseEvent(QMouseEvent *event) {
    if (regionMouseRelease(event))
        return;
    if (roiDrag_ != -2 && roi_ && event->button() == Qt::LeftButton) {
        auto rect = roi_->rect().toAlignedRect().intersected(image_.rect());
        bool changed = roiDrag_ == -1 || QRectF(rect) != pressRect_;
        roiDrag_ = -2;
        roiEditable_ = true;
        setRoiMode(false);
        if (!rect.isEmpty() && changed)
            Q_EMIT roiSelected(rect);
        event->accept();
        viewport()->update();
        return;
    }
    QGraphicsView::mouseReleaseEvent(event);
}
void ImageView::keyPressEvent(QKeyEvent *event) {
    if (regionKeyPress(event))
        return;
    if (roiEditable_ && roi_ && !roiMode_) {
        int dx = event->key() == Qt::Key_Left ? -1 : event->key() == Qt::Key_Right ? 1 : 0;
        int dy = event->key() == Qt::Key_Up ? -1 : event->key() == Qt::Key_Down ? 1 : 0;
        if (dx || dy) {
            int step = event->modifiers().testFlag(Qt::ControlModifier) ? 10 : 1;
            auto rect = roiRect();
            if (event->modifiers().testFlag(Qt::ShiftModifier)) {
                rect.setWidth(std::clamp(rect.width() + dx * step, 1, image_.width() - rect.x()));
                rect.setHeight(std::clamp(rect.height() + dy * step, 1, image_.height() - rect.y()));
            } else {
                rect.moveLeft(std::clamp(rect.x() + dx * step, 0, image_.width() - rect.width()));
                rect.moveTop(std::clamp(rect.y() + dy * step, 0, image_.height() - rect.height()));
            }
            setRoiRect(rect);
            Q_EMIT roiSelected(rect);
            event->accept();
            return;
        }
    }
    QGraphicsView::keyPressEvent(event);
}
void ImageView::resizeEvent(QResizeEvent *event) {
    QGraphicsView::resizeEvent(event);
    if (fitMode_)
        fit();
}
void ImageView::drawForeground(QPainter *painter, const QRectF &) {
    if (!roiEditable_ || !roi_)
        return;
    painter->save();
    QPen pen(QColor("#FAFAFC"));
    pen.setCosmetic(true);
    painter->setPen(pen);
    painter->setBrush(QColor("#D32630"));
    double radius = 3.5 / transform().m11();
    for (auto point : roiHandles())
        painter->drawRect(QRectF(point.x() - radius, point.y() - radius, 2 * radius, 2 * radius));
    painter->restore();
}

#include "RegionItem.h"
#include "RegionGeometry.h"
#include <QGraphicsScene>
#include <QGraphicsView>
#include <QGraphicsSceneMouseEvent>
#include <QGraphicsSceneHoverEvent>
#include <QJsonArray>
#include <QPainter>
#include <QPainterPathStroker>
#include <cmath>

RegionItem::RegionItem(QJsonObject region, QSize size, bool editable)
    : region_(region), imageSize_(size), editable_(editable) {
    setAcceptHoverEvents(editable);
    setAcceptedMouseButtons(editable ? Qt::LeftButton : Qt::NoButton);
    setZValue(40);
}
double RegionItem::zoom() const {
    return scene() && !scene()->views().isEmpty() ? scene()->views().first()->transform().m11() : 1;
}
QVector<QPointF> RegionItem::handles() const {
    if (region_["shape"] == "polygon")
        return RegionGeometry::points(region_);
    auto box = RegionGeometry::path(region_).boundingRect();
    auto c = box.center();
    if (region_["shape"] == "circle")
        return {QPointF(c.x(), box.top()), QPointF(box.right(), c.y()), QPointF(c.x(), box.bottom()),
                QPointF(box.left(), c.y())};
    return {box.topLeft(),     {c.x(), box.top()},    box.topRight(),   {box.right(), c.y()},
            box.bottomRight(), {c.x(), box.bottom()}, box.bottomLeft(), {box.left(), c.y()}};
}
int RegionItem::hit(QPointF point) const {
    auto h = handles();
    for (int i = 0; i < h.size(); ++i)
        if (QLineF(point, h[i]).length() * zoom() <= 8)
            return i;
    return RegionGeometry::path(region_).contains(point) ? -1 : -2;
}
QRectF RegionItem::boundingRect() const {
    return RegionGeometry::path(region_).boundingRect().adjusted(-512, -512, 512, 512);
}
QPainterPath RegionItem::shape() const {
    auto result = RegionGeometry::path(region_);
    QPainterPathStroker outline;
    outline.setWidth(16 / zoom());
    result = result.united(outline.createStroke(result));
    if (editable_)
        for (auto point : handles()) {
            QPainterPath knob;
            knob.addEllipse(point, 8 / zoom(), 8 / zoom());
            result = result.united(knob);
        }
    return result;
}
void RegionItem::paint(QPainter *p, const QStyleOptionGraphicsItem *, QWidget *) {
    p->setRenderHint(QPainter::Antialiasing);
    QPen pen(QColor("#FF3038"), 1.5);
    pen.setCosmetic(true);
    p->setPen(pen);
    p->setBrush(QColor(255, 48, 56, 18));
    p->drawPath(RegionGeometry::path(region_));
    if (!editable_)
        return;
    p->setBrush(QColor("#D32630"));
    double r = 3.5 / zoom();
    for (auto point : handles())
        p->drawRect(QRectF(point.x() - r, point.y() - r, 2 * r, 2 * r));
}
void RegionItem::change(QJsonObject region) {
    prepareGeometryChange();
    region_ = std::move(region);
    update();
}
void RegionItem::hoverMoveEvent(QGraphicsSceneHoverEvent *e) {
    int h = hit(e->pos());
    setCursor(h >= 0 ? Qt::CrossCursor : h == -1 ? Qt::SizeAllCursor : Qt::ArrowCursor);
    e->accept();
}
void RegionItem::mousePressEvent(QGraphicsSceneMouseEvent *e) {
    handle_ = hit(e->pos());
    if (handle_ == -2) {
        e->ignore();
        return;
    }
    press_ = e->pos();
    before_ = region_;
    e->accept();
}
void RegionItem::mouseMoveEvent(QGraphicsSceneMouseEvent *e) {
    if (handle_ == -2) {
        e->ignore();
        return;
    }
    auto after = before_;
    auto type = after["shape"].toString();
    int border = type == "rectangle" ? 0 : 1;
    QPointF point(std::clamp(qRound(e->pos().x()), 0, imageSize_.width() - border),
                  std::clamp(qRound(e->pos().y()), 0, imageSize_.height() - border));
    if (handle_ == -1) {
        auto box = RegionGeometry::path(before_).boundingRect();
        auto delta = e->pos() - press_;
        double dx = std::clamp(double(qRound(delta.x())), -box.left(),
                               imageSize_.width() - border - box.right()),
               dy = std::clamp(double(qRound(delta.y())), -box.top(),
                               imageSize_.height() - border - box.bottom());
        if (type == "rectangle") {
            after["x"] = qRound(before_["x"].toDouble() + dx);
            after["y"] = qRound(before_["y"].toDouble() + dy);
        } else if (type == "circle") {
            after["cx"] = before_["cx"].toDouble() + dx;
            after["cy"] = before_["cy"].toDouble() + dy;
        } else {
            auto p = RegionGeometry::points(before_);
            QJsonArray values;
            for (auto v : p)
                values.append(QJsonArray{v.x() + dx, v.y() + dy});
            after["points"] = values;
        }
    } else if (type == "polygon") {
        auto values = before_["points"].toArray();
        values[handle_] = QJsonArray{point.x(), point.y()};
        after["points"] = values;
    } else if (type == "circle") {
        QPointF center(before_["cx"].toDouble(), before_["cy"].toDouble());
        double maximum = std::min({center.x(), center.y(), imageSize_.width() - 1 - center.x(),
                                   imageSize_.height() - 1 - center.y()});
        after["radius"] = std::clamp(QLineF(center, point).length(), 1.0, std::max(1.0, maximum));
    } else {
        auto box = RegionGeometry::path(before_).boundingRect();
        if (handle_ == 0 || handle_ == 6 || handle_ == 7)
            box.setLeft(std::min(point.x(), box.right() - 1));
        if (handle_ == 2 || handle_ == 3 || handle_ == 4)
            box.setRight(std::max(point.x(), box.left() + 1));
        if (handle_ == 0 || handle_ == 1 || handle_ == 2)
            box.setTop(std::min(point.y(), box.bottom() - 1));
        if (handle_ == 4 || handle_ == 5 || handle_ == 6)
            box.setBottom(std::max(point.y(), box.top() + 1));
        after["x"] = qRound(box.x());
        after["y"] = qRound(box.y());
        after["width"] = qRound(box.width());
        after["height"] = qRound(box.height());
    }
    change(after);
    e->accept();
}
void RegionItem::mouseReleaseEvent(QGraphicsSceneMouseEvent *e) {
    e->accept();
    handle_ = -2;
    if (region_["shape"] == "polygon" && !RegionGeometry::validPolygon(RegionGeometry::points(region_))) {
        change(before_);
        return;
    }
    if (before_ != region_ && changed)
        changed(region_);
}

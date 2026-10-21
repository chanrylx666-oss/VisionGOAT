#pragma once
#include <QJsonObject>
#include <QImage>
#include <QPainterPath>
#include <QPolygonF>
namespace RegionGeometry {
QPolygonF points(const QJsonObject &region);
QRect pixelBounds(const QJsonObject &region, QSize fullSize = {});
QPainterPath path(const QJsonObject &region);
QJsonObject defaults(const QString &shape, QSize size);
bool validPolygon(const QPolygonF &points);
QImage mask(const QJsonObject &region, QSize size);
} // namespace RegionGeometry

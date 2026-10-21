#include "RegionGeometry.h"
#include <QJsonArray>
#include <QPainter>
#include <cmath>
namespace RegionGeometry {
QPolygonF points(const QJsonObject &region) {
    QPolygonF points;
    for (auto value : region["points"].toArray()) {
        auto a = value.toArray();
        points << QPointF(a[0].toDouble(), a[1].toDouble());
    }
    return points;
}
QPainterPath path(const QJsonObject &region) {
    QPainterPath result;
    auto type = region["shape"].toString();
    if (type == "rectangle")
        result.addRect(region["x"].toDouble(), region["y"].toDouble(), region["width"].toDouble(),
                       region["height"].toDouble());
    else if (type == "circle") {
        double r = region["radius"].toDouble();
        result.addEllipse(QPointF(region["cx"].toDouble(), region["cy"].toDouble()), r, r);
    } else if (type == "polygon") {
        result.addPolygon(points(region));
        result.closeSubpath();
    }
    return result;
}
QRect pixelBounds(const QJsonObject &region, QSize size) {
    auto type = region["shape"].toString();
    if (type.isEmpty() || type == "none")
        return QRect(QPoint(), size);
    if (type == "rectangle")
        return {region["x"].toInt(), region["y"].toInt(), region["width"].toInt(), region["height"].toInt()};
    auto box = path(region).boundingRect();
    int x = int(std::floor(box.left())), y = int(std::floor(box.top()));
    return {x, y, int(std::ceil(box.right())) + 1 - x, int(std::ceil(box.bottom())) + 1 - y};
}
QJsonObject defaults(const QString &shape, QSize size) {
    int w = std::max(8, size.width()), h = std::max(8, size.height());
    if (shape == "rectangle")
        return {{"shape", shape}, {"x", w / 5}, {"y", h / 5}, {"width", w * 3 / 5}, {"height", h * 3 / 5}};
    if (shape == "circle")
        return {{"shape", shape}, {"cx", w / 2.0}, {"cy", h / 2.0}, {"radius", std::min(w, h) * .3}};
    if (shape == "polygon")
        return {{"shape", shape},
                {"points", QJsonArray{QJsonArray{w / 5, h / 5}, QJsonArray{w * 4 / 5, h / 5},
                                      QJsonArray{w * 4 / 5, h * 4 / 5}, QJsonArray{w / 5, h * 4 / 5}}}};
    return {{"shape", "none"}};
}
bool validPolygon(const QPolygonF &p) {
    if (p.size() < 3 || p.size() > 64)
        return false;
    auto cross = [](QPointF a, QPointF b, QPointF c) {
        return (b.x() - a.x()) * (c.y() - a.y()) - (b.y() - a.y()) * (c.x() - a.x());
    };
    double area = 0;
    for (int i = 0; i < p.size(); ++i) {
        auto a = p[i], b = p[(i + 1) % p.size()];
        if (QLineF(a, b).length() < 1)
            return false;
        area += a.x() * b.y() - a.y() * b.x();
        for (int j = i + 1; j < p.size(); ++j) {
            if (j == i + 1 || (i == 0 && j == p.size() - 1))
                continue;
            auto c = p[j], d = p[(j + 1) % p.size()];
            if (std::max(a.x(), b.x()) < std::min(c.x(), d.x()) ||
                std::max(c.x(), d.x()) < std::min(a.x(), b.x()) ||
                std::max(a.y(), b.y()) < std::min(c.y(), d.y()) ||
                std::max(c.y(), d.y()) < std::min(a.y(), b.y()))
                continue;
            if (cross(a, b, c) * cross(a, b, d) <= 0 && cross(c, d, a) * cross(c, d, b) <= 0)
                return false;
        }
    }
    return std::abs(area) >= 2;
}
QImage mask(const QJsonObject &region, QSize size) {
    QImage result(size, QImage::Format_Grayscale8);
    result.fill(0);
    auto shape = region.value("shape").toString();
    auto bounds = pixelBounds(region, size).intersected(QRect(QPoint(), size));
    auto outline = path(region);
    double cx = region.value("cx").toDouble(), cy = region.value("cy").toDouble(),
           radius = region.value("radius").toDouble();
    for (int y = bounds.top(); y <= bounds.bottom(); ++y)
        for (int x = bounds.left(); x <= bounds.right(); ++x) {
            bool inside = shape == "none" || shape == "rectangle" ||
                          (shape == "circle" && std::hypot(x - cx, y - cy) <= radius) ||
                          (shape == "polygon" && outline.contains(QPointF(x, y)));
            if (inside)
                result.scanLine(y)[x] = 255;
        }
    return result;
}
} // namespace RegionGeometry

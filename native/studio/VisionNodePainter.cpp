#include "VisionNodePainter.h"
#include "GraphModel.h"
#include "StudioTheme.h"
#include <QFontMetrics>
#include <QLinearGradient>
#include <QtNodes/internal/BasicGraphicsScene.hpp>
#include <QtNodes/internal/NodeGraphicsObject.hpp>

QSize VisionNodeGeometry::size(QtNodes::NodeId id) const {
    QFont font("Microsoft YaHei UI", 10, QFont::DemiBold);
    auto label = _graphModel.nodeData(id, QtNodes::NodeRole::Caption).toString();
    int ports = std::max(_graphModel.nodeData(id, QtNodes::NodeRole::InPortCount).toInt(),
                         _graphModel.nodeData(id, QtNodes::NodeRole::OutPortCount).toInt());
    return {std::max(ports > 1 ? 252 : 188, QFontMetrics(font).horizontalAdvance(label) + 80),
            ports > 1 ? 52 + (ports - 1) * 23 + 22 : 78};
}
QRectF VisionNodeGeometry::boundingRect(QtNodes::NodeId id) const {
    return QRectF(QPointF(), size(id)).adjusted(-6, -6, 6, 6);
}
void VisionNodeGeometry::recomputeSize(QtNodes::NodeId id) const {
    _graphModel.setNodeData(id, QtNodes::NodeRole::Size, size(id));
}
QPointF VisionNodeGeometry::portPosition(QtNodes::NodeId id, QtNodes::PortType type,
                                         QtNodes::PortIndex index) const {
    bool multi = _graphModel.nodeData(id, QtNodes::NodeRole::OutPortCount).toInt() > 1;
    return {type == QtNodes::PortType::In ? 0.0 : double(size(id).width()),
            (multi ? 52.0 : 39.0) + index * 23};
}
QPointF VisionNodeGeometry::portTextPosition(QtNodes::NodeId id, QtNodes::PortType type,
                                             QtNodes::PortIndex index) const {
    auto point = portPosition(id, type, index);
    return {type == QtNodes::PortType::In ? 12.0 : double(size(id).width() - 145), point.y() + 4};
}
void VisionNodePainter::paint(QPainter *p, QtNodes::NodeGraphicsObject &object) const {
    auto &model = object.graphModel();
    auto id = object.nodeId();
    auto size = object.nodeScene()->nodeGeometry().size(id);
    auto rect = QRectF(QPointF(), size);
    auto type = model.nodeData(id, QtNodes::NodeRole::Type).toString();
    QJsonDocument styleDoc = QJsonDocument::fromVariant(model.nodeData(id, QtNodes::NodeRole::Style));
    auto statusColor = styleDoc.object()["NodeStyle"].toObject()["NormalBoundaryColor"].toString();
    QColor border = object.isSelected() ? QColor(StudioTheme::Accent) : QColor("#8D8D94");
    if (statusColor == "#F57681")
        border = QColor("#FF7379");
    p->save();
    p->setRenderHint(QPainter::Antialiasing);
    for (int margin : {3, 1}) {
        p->setPen(QPen(QColor(255, 48, 56, object.isSelected() ? 48 : 12), 1));
        p->setBrush(Qt::NoBrush);
        p->drawRoundedRect(rect.adjusted(-margin, -margin, margin, margin), 7, 7);
    }
    QLinearGradient gradient(0, 0, 0, size.height());
    gradient.setColorAt(0, QColor("#2C2C30"));
    gradient.setColorAt(.45, QColor("#1E1E21"));
    gradient.setColorAt(1, QColor("#141416"));
    p->setPen(QPen(border, object.isSelected() ? 1.8 : 1.25));
    p->setBrush(gradient);
    p->drawRoundedRect(rect, 6, 6);
    p->setPen(QPen(QColor(StudioTheme::Accent), 2, Qt::SolidLine, Qt::RoundCap));
    p->drawLine(QPointF(7, 1), QPointF(size.width() - 7, 1));
    StudioTheme::icon(type, QColor(StudioTheme::Accent)).paint(p, QRect(17, 21, 31, 31));
    QFont font("Microsoft YaHei UI", 10, QFont::DemiBold);
    p->setFont(font);
    p->setPen(QColor(StudioTheme::Text));
    p->drawText(QRectF(58, 12, size.width() - 70, 24), Qt::AlignVCenter | Qt::AlignLeft,
                model.nodeData(id, QtNodes::NodeRole::Caption).toString());
    p->setFont(QFont("Segoe UI", 9));
    p->setPen(QColor("#C0C0C7"));
    int inputCount = model.nodeData(id, QtNodes::NodeRole::InPortCount).toUInt();
    int outputCount = model.nodeData(id, QtNodes::NodeRole::OutPortCount).toUInt();
    auto input = inputCount
                     ? model.portData(id, QtNodes::PortType::In, 0, QtNodes::PortRole::Caption).toString()
                     : QString();
    auto output = model.portData(id, QtNodes::PortType::Out, 0, QtNodes::PortRole::Caption).toString();
    if (outputCount > 1) {
        if (inputCount)
            p->drawText(QRectF(12, 41, 65, 22), Qt::AlignVCenter, input);
        for (int i = 0; i < outputCount; ++i)
            p->drawText(QRectF(85, 41 + i * 23, size.width() - 97, 22), Qt::AlignVCenter | Qt::AlignRight,
                        model.portData(id, QtNodes::PortType::Out, i, QtNodes::PortRole::Caption).toString());
    } else if (inputCount) {
        p->drawText(QRectF(58, 42, 70, 22), Qt::AlignVCenter, input);
        p->drawText(QRectF(size.width() - 77, 42, 65, 22), Qt::AlignVCenter | Qt::AlignRight, output);
    } else
        p->drawText(QRectF(58, 42, 100, 22), Qt::AlignVCenter, output);
    for (auto direction : {QtNodes::PortType::In, QtNodes::PortType::Out}) {
        int count = model
                        .nodeData(id, direction == QtNodes::PortType::In ? QtNodes::NodeRole::InPortCount
                                                                         : QtNodes::NodeRole::OutPortCount)
                        .toUInt();
        for (int i = 0; i < count; ++i) {
            auto point = object.nodeScene()->nodeGeometry().portPosition(id, direction, i);
            bool filled = !model.connections(id, direction, i).empty();
            p->setPen(QPen(filled ? QColor(StudioTheme::Accent) : QColor("#A0A0A8"), 1.8));
            p->setBrush(filled ? QColor("#FAFAFC") : QColor("#303035"));
            p->drawEllipse(point, 5, 5);
        }
    }
    if (statusColor == "#44D7B6") {
        p->setPen(Qt::NoPen);
        p->setBrush(QColor("#E0E0E4"));
        p->drawEllipse(QPointF(size.width() - 10, 10), 2.5, 2.5);
    }
    p->restore();
}

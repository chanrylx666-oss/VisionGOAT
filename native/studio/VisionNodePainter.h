#pragma once
#include <QtNodes/internal/AbstractNodeGeometry.hpp>
#include <QtNodes/internal/AbstractNodePainter.hpp>

class VisionNodeGeometry : public QtNodes::AbstractNodeGeometry {
  public:
    explicit VisionNodeGeometry(QtNodes::AbstractGraphModel &model) : AbstractNodeGeometry(model) {}
    QSize size(QtNodes::NodeId id) const override;
    QRectF boundingRect(QtNodes::NodeId id) const override;
    void recomputeSize(QtNodes::NodeId id) const override;
    QPointF portPosition(QtNodes::NodeId id, QtNodes::PortType type, QtNodes::PortIndex index) const override;
    QPointF portTextPosition(QtNodes::NodeId id, QtNodes::PortType type,
                             QtNodes::PortIndex index) const override;
    QPointF captionPosition(QtNodes::NodeId) const override {
        return {58, 29};
    }
    QRectF captionRect(QtNodes::NodeId id) const override {
        return {58, 12, double(size(id).width() - 72), 24};
    }
    QPointF widgetPosition(QtNodes::NodeId) const override {
        return {};
    }
    QRect resizeHandleRect(QtNodes::NodeId) const override {
        return {};
    }
};
class VisionNodePainter : public QtNodes::AbstractNodePainter {
  public:
    void paint(QPainter *painter, QtNodes::NodeGraphicsObject &node) const override;
};

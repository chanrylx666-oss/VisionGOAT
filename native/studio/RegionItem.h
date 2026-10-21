#pragma once
#include <QGraphicsItem>
#include <QJsonObject>
#include <functional>

class RegionItem : public QGraphicsItem {
    QJsonObject region_, before_;
    QSize imageSize_;
    QPointF press_;
    int handle_ = -2;
    bool editable_ = true;
    QVector<QPointF> handles() const;
    double zoom() const;
    int hit(QPointF point) const;
    void change(QJsonObject region);

  public:
    std::function<void(QJsonObject)> changed;
    RegionItem(QJsonObject region, QSize size, bool editable);
    QJsonObject region() const {
        return region_;
    }
    QRectF boundingRect() const override;
    QPainterPath shape() const override;
    void paint(QPainter *p, const QStyleOptionGraphicsItem *, QWidget *) override;

  protected:
    void hoverMoveEvent(QGraphicsSceneHoverEvent *event) override;
    void mousePressEvent(QGraphicsSceneMouseEvent *event) override;
    void mouseMoveEvent(QGraphicsSceneMouseEvent *event) override;
    void mouseReleaseEvent(QGraphicsSceneMouseEvent *event) override;
};

#pragma once
#include <QGraphicsPixmapItem>
#include <QGraphicsRectItem>
#include <QGraphicsView>
#include <QJsonArray>
#include <QJsonObject>
#include <QPolygonF>
class RegionItem;
class QGraphicsPathItem;

class ImageView : public QGraphicsView {
    Q_OBJECT
    QGraphicsScene scene_;
    QImage image_;
    QPoint origin_;
    bool roiMode_ = false;
    bool roiEditable_ = false, fitMode_ = true;
    QPointF roiStart_;
    QRectF pressRect_;
    int roiDrag_ = -2;
    QGraphicsRectItem *roi_ = nullptr;
    RegionItem *region_ = nullptr;
    QGraphicsPathItem *draftRegion_ = nullptr;
    QString regionDrawing_;
    QPointF regionPress_;
    QPolygonF polygonDraft_;
    bool regionPressed_ = false;
    bool regionMousePress(QMouseEvent *event);
    bool regionMouseMove(QMouseEvent *event);
    bool regionMouseRelease(QMouseEvent *event);
    bool regionKeyPress(QKeyEvent *event);
    void updateRegionDraft(QPointF point);
    void finishPolygon();
    QVector<QPointF> roiHandles() const;
    int hitRoi(QPoint viewportPoint) const;
    void updateRoiCursor(QPoint viewportPoint);

  public:
    explicit ImageView(QWidget *parent = nullptr);
    void present(const QImage &image, const QJsonArray &overlays = {}, QPoint origin = {});
    void fit();
    void setRoiMode(bool enabled);
    void setRoiRect(QRect rectangle, bool editable = true);
    QRect roiRect() const;
    void clearRoi();
    void setZoomPercent(double percent);
    void setRegion(QJsonObject region, bool editable = true);
    QJsonObject region() const;
    void drawRegion(QString shape);
    void cancelRegionDrawing();
    void addGuides(const QJsonArray &guides);
    QImage currentImage() const {
        return image_;
    }

  protected:
    void wheelEvent(QWheelEvent *event) override;
    void mousePressEvent(QMouseEvent *event) override;
    void mouseMoveEvent(QMouseEvent *event) override;
    void mouseReleaseEvent(QMouseEvent *event) override;
    void mouseDoubleClickEvent(QMouseEvent *event) override;
    void keyPressEvent(QKeyEvent *event) override;
    void resizeEvent(QResizeEvent *event) override;
    void drawForeground(QPainter *painter, const QRectF &rect) override;
  Q_SIGNALS:
    void roiSelected(QRect rect);
    void pixelChanged(QString text);
    void imageChanged();
    void zoomChanged(double percent);
    void regionSelected(QJsonObject region);
    void regionDrawingFinished();
};

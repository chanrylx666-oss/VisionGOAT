#pragma once
#include <QDrag>
#include <QDragEnterEvent>
#include <QDropEvent>
#include <QMimeData>
#include <QTreeWidget>
#include <QPainter>
#include <QKeyEvent>
#include <cmath>
#include <QtNodes/GraphicsView>

class GraphView : public QtNodes::GraphicsView {
    Q_OBJECT
    bool panMode_ = false;

  public:
    explicit GraphView(QtNodes::BasicGraphicsScene *scene, QWidget *parent = nullptr)
        : GraphicsView(scene, parent) {
        setAcceptDrops(true);
        setScaleRange(.1, 4.0);
        setFrameShape(QFrame::NoFrame);
        setDragMode(RubberBandDrag);
    }
    void setPanMode(bool enabled) {
        panMode_ = enabled;
        setDragMode(enabled ? ScrollHandDrag : RubberBandDrag);
        viewport()->setCursor(enabled ? Qt::OpenHandCursor : Qt::ArrowCursor);
    }
    void zoomFitAll() {
        GraphicsView::zoomFitAll();
        Q_EMIT scaleChanged(transform().m11());
    }
    void zoomFitSelected() {
        GraphicsView::zoomFitSelected();
        Q_EMIT scaleChanged(transform().m11());
    }

  protected:
    void drawBackground(QPainter *painter, const QRectF &rect) override {
        painter->fillRect(rect, QColor("#101012"));
        painter->setRenderHint(QPainter::Antialiasing, false);
        for (int spacing : {20, 100}) {
            QPen pen(spacing == 20 ? QColor("#1C1C20") : QColor("#2C2C31"));
            pen.setCosmetic(true);
            painter->setPen(pen);
            double left = std::floor(rect.left() / spacing) * spacing,
                   top = std::floor(rect.top() / spacing) * spacing;
            for (double x = left; x <= rect.right(); x += spacing)
                painter->drawLine(QPointF(x, rect.top()), QPointF(x, rect.bottom()));
            for (double y = top; y <= rect.bottom(); y += spacing)
                painter->drawLine(QPointF(rect.left(), y), QPointF(rect.right(), y));
        }
    }
    void keyReleaseEvent(QKeyEvent *event) override {
        GraphicsView::keyReleaseEvent(event);
        if (event->key() == Qt::Key_Shift)
            setPanMode(panMode_);
    }
    void dragEnterEvent(QDragEnterEvent *event) override {
        if (event->mimeData()->hasFormat("application/x-jpvision-node"))
            event->acceptProposedAction();
        else
            GraphicsView::dragEnterEvent(event);
    }
    void dragMoveEvent(QDragMoveEvent *event) override {
        if (event->mimeData()->hasFormat("application/x-jpvision-node"))
            event->acceptProposedAction();
        else
            GraphicsView::dragMoveEvent(event);
    }
    void dropEvent(QDropEvent *event) override {
        if (event->mimeData()->hasFormat("application/x-jpvision-node")) {
            Q_EMIT nodeDropped(QString::fromUtf8(event->mimeData()->data("application/x-jpvision-node")),
                               mapToScene(event->position().toPoint()));
            event->acceptProposedAction();
        } else
            GraphicsView::dropEvent(event);
    }
  Q_SIGNALS:
    void nodeDropped(QString type, QPointF position);
};
class ToolTree : public QTreeWidget {
  public:
    explicit ToolTree(QWidget *parent = nullptr) : QTreeWidget(parent) {
        setDragEnabled(true);
        setHeaderHidden(true);
    }

  protected:
    void startDrag(Qt::DropActions) override {
        auto item = currentItem();
        if (!item)
            return;
        auto type = item->data(0, Qt::UserRole).toString();
        if (type.isEmpty())
            return;
        auto mime = new QMimeData;
        mime->setData("application/x-jpvision-node", type.toUtf8());
        auto drag = new QDrag(this);
        drag->setMimeData(mime);
        drag->exec(Qt::CopyAction);
    }
};

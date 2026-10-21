#pragma once
#include <QWidget>
#include <QImage>
#include <array>

class HistogramWidget : public QWidget {
    std::array<std::array<quint64, 256>, 4> values_{};
    int channel_ = -1;
    bool empty_ = true;

  public:
    explicit HistogramWidget(QWidget *parent = nullptr) : QWidget(parent) {
        setMinimumHeight(90);
    }
    void setImage(const QImage &image);
    void setChannel(int channel) {
        channel_ = channel;
        update();
    }

  protected:
    void paintEvent(QPaintEvent *) override;
};

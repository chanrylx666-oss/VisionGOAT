#include "HistogramWidget.h"
#include <QPainter>
#include <QPainterPath>
#include <cmath>

void HistogramWidget::setImage(const QImage &image) {
    values_ = {};
    empty_ = image.isNull();
    if (!empty_) {
        auto rgb = image.convertToFormat(QImage::Format_RGB888);
        int step = std::max(1, int(std::ceil(std::sqrt(double(rgb.width()) * rgb.height() / 1000000))));
        for (int y = 0; y < rgb.height(); y += step) {
            auto row = rgb.constScanLine(y);
            for (int x = 0; x < rgb.width(); x += step) {
                auto pixel = row + 3 * x;
                ++values_[0][pixel[0]];
                ++values_[1][pixel[1]];
                ++values_[2][pixel[2]];
                ++values_[3][qGray(pixel[0], pixel[1], pixel[2])];
            }
        }
    }
    update();
}
void HistogramWidget::paintEvent(QPaintEvent *) {
    QPainter p(this);
    p.setRenderHint(QPainter::Antialiasing);
    auto area = QRectF(rect()).adjusted(8, 8, -8, -20);
    p.fillRect(rect(), QColor("#101012"));
    p.setPen(QPen(QColor("#38383C"), 1));
    for (int i = 0; i <= 4; ++i) {
        double y = area.bottom() - area.height() * i / 4;
        p.drawLine(QPointF(area.left(), y), QPointF(area.right(), y));
    }
    p.setPen(QColor("#A6A6AC"));
    p.setFont(QFont("Microsoft YaHei UI", 8));
    p.drawText(QPointF(area.left(), height() - 4), "0");
    p.drawText(QPointF(area.center().x() - 8, height() - 4), "128");
    p.drawText(QPointF(area.right() - 19, height() - 4), "255");
    if (empty_) {
        p.drawText(area, Qt::AlignCenter, "打开图像后显示直方图");
        return;
    }
    quint64 maximum = 1;
    for (int c = 0; c < 4; ++c)
        if (channel_ == c || (channel_ < 0 && c < 3))
            for (auto n : values_[c])
                maximum = std::max(maximum, n);
    const QColor colors[] = {QColor("#FF526B"), QColor("#15D89A"), QColor("#19ADFF"), QColor("#E1E1E5")};
    for (int c = 0; c < 4; ++c)
        if (channel_ == c || (channel_ < 0 && c < 3)) {
            QPainterPath path;
            path.moveTo(area.left(), area.bottom());
            for (int x = 0; x < 256; ++x) {
                double value = std::log1p(double(values_[c][x])) / std::log1p(double(maximum));
                path.lineTo(area.left() + area.width() * x / 255, area.bottom() - area.height() * value);
            }
            p.setPen(QPen(colors[c], 1.2));
            p.drawPath(path);
        }
}

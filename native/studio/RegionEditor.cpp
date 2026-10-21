#include "RegionEditor.h"
#include "RegionGeometry.h"
#include <QComboBox>
#include <QDoubleSpinBox>
#include <QFormLayout>
#include <QHeaderView>
#include <QJsonArray>
#include <QLabel>
#include <QPushButton>
#include <QSignalBlocker>
#include <QTableWidget>
#include <QVBoxLayout>

RegionEditor::RegionEditor(QJsonObject value, QSize imageSize, QWidget *parent)
    : QWidget(parent), size_(imageSize.isEmpty() ? QSize(640, 360) : imageSize) {
    setObjectName("Param_roi");
    auto layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->setSpacing(6);
    shape_ = new QComboBox;
    shape_->setObjectName("RoiShape");
    for (auto pair : {std::pair<QString, QString>{"全图", "none"},
                      {"矩形", "rectangle"},
                      {"圆形", "circle"},
                      {"多边形", "polygon"}})
        shape_->addItem(pair.first, pair.second);
    layout->addWidget(shape_);
    auto edit = new QPushButton("在图像中编辑 / 重画 ROI");
    edit->setObjectName("EditModuleRoi");
    layout->addWidget(edit);
    auto body = new QWidget;
    fields_ = new QFormLayout(body);
    fields_->setContentsMargins(0, 0, 0, 0);
    fields_->setSpacing(5);
    layout->addWidget(body);
    value_ = value.isEmpty() ? QJsonObject{{"shape", "none"}} : value;
    shape_->setCurrentIndex(std::max(0, shape_->findData(value_.value("shape").toString())));
    rebuild();
    connect(shape_, &QComboBox::currentIndexChanged, this, [this] {
        if (changing_)
            return;
        value_ = RegionGeometry::defaults(shape_->currentData().toString(), size_);
        rebuild();
        Q_EMIT valueChanged(value_);
    });
    connect(edit, &QPushButton::clicked, this, [this] {
        auto shape = shape_->currentData().toString();
        if (shape == "none") {
            shape_->setCurrentIndex(1);
            shape = "rectangle";
        }
        Q_EMIT editRequested(shape);
    });
}
void RegionEditor::rebuild() {
    changing_ = true;
    while (fields_->count()) {
        auto item = fields_->takeAt(0);
        if (item->widget())
            delete item->widget();
        delete item;
    }
    points_ = nullptr;
    auto type = value_.value("shape").toString();
    if (type == "rectangle" || type == "circle") {
        auto names = type == "rectangle" ? QStringList{"x", "y", "width", "height"}
                                         : QStringList{"cx", "cy", "radius"};
        for (const auto &name : names) {
            auto number = new QDoubleSpinBox;
            number->setObjectName("Roi_" + name);
            number->setDecimals(type == "rectangle" ? 0 : 2);
            number->setRange(name == "width" || name == "height" || name == "radius" ? 1 : 0, 32768);
            number->setKeyboardTracking(false);
            number->setValue(value_.value(name).toDouble());
            QString label = name == "width"    ? "宽度"
                            : name == "height" ? "高度"
                            : name == "radius" ? "半径"
                            : name == "cx"     ? "圆心 X"
                            : name == "cy"     ? "圆心 Y"
                                               : name.toUpper();
            fields_->addRow(label, number);
            connect(number, &QDoubleSpinBox::valueChanged, this, [this, name, type](double v) {
                if (changing_)
                    return;
                value_[name] = type == "rectangle" ? QJsonValue(qRound(v)) : QJsonValue(v);
                publish();
            });
        }
    } else if (type == "polygon") {
        points_ = new QTableWidget;
        points_->setObjectName("RoiVertices");
        points_->setColumnCount(2);
        points_->setHorizontalHeaderLabels({"X", "Y"});
        points_->horizontalHeader()->setSectionResizeMode(QHeaderView::Stretch);
        points_->setFixedHeight(130);
        auto points = value_["points"].toArray();
        points_->setRowCount(points.size());
        for (int i = 0; i < points.size(); ++i) {
            auto p = points[i].toArray();
            for (int j = 0; j < 2; ++j)
                points_->setItem(i, j, new QTableWidgetItem(QString::number(p[j].toDouble(), 'g', 8)));
        }
        fields_->addRow(points_);
        auto hint = new QLabel("点击添加顶点，双击或 Enter 完成；\n拖动顶点或内部调整，Esc 取消重画。");
        hint->setWordWrap(true);
        fields_->addRow(hint);
        connect(points_, &QTableWidget::itemChanged, this, [this] {
            if (changing_)
                return;
            QJsonArray points;
            QPolygonF polygon;
            for (int i = 0; i < points_->rowCount(); ++i) {
                bool okX = false, okY = false;
                double x = points_->item(i, 0)->text().toDouble(&okX),
                       y = points_->item(i, 1)->text().toDouble(&okY);
                if (!okX || !okY || x < 0 || y < 0 || x >= size_.width() || y >= size_.height())
                    return;
                points.append(QJsonArray{x, y});
                polygon << QPointF(x, y);
            }
            if (RegionGeometry::validPolygon(polygon)) {
                value_["points"] = points;
                Q_EMIT valueChanged(value_);
            }
        });
    } else {
        auto hint = new QLabel("仅影响当前模块；保存后由 WPF 直接使用。");
        hint->setWordWrap(true);
        fields_->addRow(hint);
    }
    changing_ = false;
}
void RegionEditor::setValue(QJsonObject value) {
    auto oldType = value_.value("shape");
    auto oldCount = value_.value("points").toArray().size();
    value_ = value;
    QSignalBlocker block(shape_);
    shape_->setCurrentIndex(std::max(0, shape_->findData(value_.value("shape").toString())));
    if (oldType != value_.value("shape") || oldCount != value_.value("points").toArray().size()) {
        rebuild();
        return;
    }
    changing_ = true;
    for (auto number : findChildren<QDoubleSpinBox *>()) {
        auto name = number->objectName().mid(4);
        QSignalBlocker blocker(number);
        number->setValue(value_.value(name).toDouble());
    }
    if (points_) {
        QSignalBlocker blocker(points_);
        auto points = value_.value("points").toArray();
        for (int i = 0; i < points.size(); ++i)
            for (int j = 0; j < 2; ++j)
                points_->item(i, j)->setText(QString::number(points[i].toArray()[j].toDouble(), 'g', 8));
    }
    changing_ = false;
}
void RegionEditor::publish() {
    auto type = value_.value("shape").toString();
    auto after = value_;
    if (type == "rectangle") {
        int x = std::clamp(after["x"].toInt(), 0, size_.width() - 1),
            y = std::clamp(after["y"].toInt(), 0, size_.height() - 1);
        after["x"] = x;
        after["y"] = y;
        after["width"] = std::clamp(after["width"].toInt(), 1, size_.width() - x);
        after["height"] = std::clamp(after["height"].toInt(), 1, size_.height() - y);
    } else if (type == "circle" && size_.width() >= 3 && size_.height() >= 3) {
        double x = std::clamp(after["cx"].toDouble(), 1.0, size_.width() - 2.0),
               y = std::clamp(after["cy"].toDouble(), 1.0, size_.height() - 2.0);
        after["cx"] = x;
        after["cy"] = y;
        after["radius"] = std::clamp(after["radius"].toDouble(), 1.0,
                                     std::min({x, y, size_.width() - 1 - x, size_.height() - 1 - y}));
    }
    setValue(after);
    Q_EMIT valueChanged(value_);
}

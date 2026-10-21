#pragma once
#include <QWidget>
#include <QJsonObject>
class QComboBox;
class QFormLayout;
class QTableWidget;
class RegionEditor : public QWidget {
    Q_OBJECT
    QJsonObject value_;
    QSize size_;
    QComboBox *shape_;
    QFormLayout *fields_;
    QTableWidget *points_ = nullptr;
    bool changing_ = false;
    void rebuild();
    void publish();

  public:
    RegionEditor(QJsonObject value, QSize imageSize, QWidget *parent = nullptr);
    QJsonObject value() const {
        return value_;
    }
    void setValue(QJsonObject value);
  Q_SIGNALS:
    void valueChanged(QJsonObject value);
    void editRequested(QString shape);
};

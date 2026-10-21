#pragma once
#include "GraphModel.h"
#include <QComboBox>
#include <QDialog>
#include <QTableWidget>
class InterfaceDialog : public QDialog {
    QTableWidget *inputs_;
    QTableWidget *outputs_;
    QComboBox *decision_, *kind_;
    GraphModel *model_;
    void addInput(QString name = "", QJsonObject value = {});
    void addOutput(QString name = "", QJsonObject value = {});

  public:
    explicit InterfaceDialog(GraphModel *model, QWidget *parent = nullptr);
    QJsonObject inputs() const;
    QJsonObject outputs() const;
    QString kind() const {
        return kind_->currentData().toString();
    }
    QString decision() const {
        return kind() == "inspection" ? decision_->currentText() : QString();
    }
};

#include "InterfaceDialog.h"
#include <QDialogButtonBox>
#include <QHBoxLayout>
#include <QHeaderView>
#include <QJsonDocument>
#include <QLabel>
#include <QMessageBox>
#include <QPushButton>
#include <QVBoxLayout>
#include <cmath>
#include <stdexcept>

static QString valueText(QJsonValue value) {
    if (value.isString())
        return value.toString();
    QJsonArray array{value};
    auto text = QJsonDocument(array).toJson(QJsonDocument::Compact);
    return QString::fromUtf8(text.mid(1, text.size() - 2));
}
InterfaceDialog::InterfaceDialog(GraphModel *model, QWidget *parent) : QDialog(parent), model_(model) {
    setWindowTitle("配方公开接口");
    resize(820, 650);
    auto layout = new QVBoxLayout(this);
    layout->addWidget(new QLabel("WPF 通过下列稳定名称传参和取结果。图像输入首版仅支持一个。"));
    auto purpose = new QHBoxLayout;
    purpose->addWidget(new QLabel("配方用途"));
    kind_ = new QComboBox;
    kind_->setObjectName("RecipeKind");
    kind_->addItem("完整检测（合格 / 不合格判定）", "inspection");
    kind_->addItem("图像处理（不输出检测判定）", "processing");
    kind_->setCurrentIndex(model_->interfaces().value("kind") == "processing" ? 1 : 0);
    purpose->addWidget(kind_);
    layout->addLayout(purpose);
    inputs_ = new QTableWidget(0, 3);
    inputs_->setHorizontalHeaderLabels({"输入名称", "类型", "默认值（标量）"});
    inputs_->horizontalHeader()->setSectionResizeMode(QHeaderView::Stretch);
    layout->addWidget(new QLabel("公开输入"));
    layout->addWidget(inputs_);
    auto buttons = new QHBoxLayout;
    auto add = new QPushButton("添加输入"), remove = new QPushButton("删除输入");
    buttons->addWidget(add);
    buttons->addWidget(remove);
    buttons->addStretch();
    layout->addLayout(buttons);
    connect(add, &QPushButton::clicked, this,
            [this] { addInput("Input" + QString::number(inputs_->rowCount())); });
    connect(remove, &QPushButton::clicked, this, [this] {
        if (inputs_->currentRow() >= 0)
            inputs_->removeRow(inputs_->currentRow());
    });
    outputs_ = new QTableWidget(0, 3);
    outputs_->setHorizontalHeaderLabels({"输出名称", "数据来源（节点 / 端口）", "单位"});
    outputs_->horizontalHeader()->setSectionResizeMode(QHeaderView::Stretch);
    layout->addWidget(new QLabel("公开输出"));
    layout->addWidget(outputs_);
    buttons = new QHBoxLayout;
    add = new QPushButton("添加输出");
    remove = new QPushButton("删除输出");
    buttons->addWidget(add);
    buttons->addWidget(remove);
    buttons->addStretch();
    layout->addLayout(buttons);
    connect(add, &QPushButton::clicked, this,
            [this] { addOutput("Output" + QString::number(outputs_->rowCount())); });
    connect(remove, &QPushButton::clicked, this, [this] {
        if (outputs_->currentRow() >= 0)
            outputs_->removeRow(outputs_->currentRow());
    });
    auto row = new QHBoxLayout;
    row->addWidget(new QLabel("检测判定输出（Bool）"));
    decision_ = new QComboBox;
    decision_->setEditable(true);
    row->addWidget(decision_);
    layout->addLayout(row);
    auto metadata = model->interfaces();
    auto inputMeta = metadata["inputs"].toObject();
    for (auto it = inputMeta.begin(); it != inputMeta.end(); ++it)
        addInput(it.key(), it.value().toObject());
    auto outputMeta = metadata["outputs"].toObject();
    for (auto it = outputMeta.begin(); it != outputMeta.end(); ++it) {
        addOutput(it.key(), it.value().toObject());
        if (it.value().toObject()["type"] == "Bool")
            decision_->addItem(it.key());
    }
    decision_->setCurrentText(metadata["decisionOutput"].toString());
    decision_->setEnabled(kind() == "inspection");
    connect(kind_, &QComboBox::currentIndexChanged, this,
            [this] { decision_->setEnabled(kind() == "inspection"); });
    auto box = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel);
    layout->addWidget(box);
    connect(box, &QDialogButtonBox::rejected, this, &QDialog::reject);
    connect(box, &QDialogButtonBox::accepted, this, [this] {
        try {
            auto i = inputs(), o = outputs();
            if (o.isEmpty() || o.size() > 64)
                throw std::runtime_error("请选择 1–64 个公开输出。");
            if (kind() == "inspection" &&
                (!o.contains(decision()) || o[decision()].toObject()["type"] != "Bool"))
                throw std::runtime_error("检测判定必须选择 Bool 类型输出。");
            int images = 0;
            for (auto v : i)
                if (v.toObject()["type"] == "Image")
                    ++images;
            if (images != 1)
                throw std::runtime_error("首版需要且仅需要一个 Image 输入。");
            accept();
        } catch (const std::exception &e) {
            QMessageBox::warning(this, "接口无效", QString::fromUtf8(e.what()));
        }
    });
}
void InterfaceDialog::addInput(QString name, QJsonObject value) {
    int row = inputs_->rowCount();
    inputs_->insertRow(row);
    inputs_->setItem(row, 0, new QTableWidgetItem(name));
    auto type = new QComboBox;
    type->addItems({"Image", "Int", "Double", "Bool", "String"});
    type->setCurrentText(value["type"].toString("Int"));
    inputs_->setCellWidget(row, 1, type);
    inputs_->setItem(row, 2,
                     new QTableWidgetItem(value.contains("default") ? valueText(value["default"]) : "0"));
}
void InterfaceDialog::addOutput(QString name, QJsonObject value) {
    int row = outputs_->rowCount();
    outputs_->insertRow(row);
    outputs_->setItem(row, 0, new QTableWidgetItem(name));
    auto source = new QComboBox;
    source->addItem("未选择", QJsonObject());
    int chosen = 0;
    for (auto id : model_->allNodeIds()) {
        auto node = model_->business(id), meta = model_->descriptor(node["type"].toString());
        for (auto p : meta["outputs"].toArray()) {
            auto port = p.toObject();
            auto data = QJsonObject{{"node", node["id"]}, {"port", port["id"]}, {"type", port["type"]}};
            source->addItem(meta["label"].toString() + " / " + node["id"].toString() + " / " +
                                port["label"].toString() + " (" + port["id"].toString() + ") · " +
                                port["type"].toString(),
                            data);
            if (data["node"] == value["node"] && data["port"] == value["port"])
                chosen = source->count() - 1;
        }
    }
    source->setCurrentIndex(chosen);
    outputs_->setCellWidget(row, 1, source);
    outputs_->setItem(row, 2, new QTableWidgetItem(value["unit"].toString()));
}
QJsonObject InterfaceDialog::inputs() const {
    QJsonObject result;
    for (int row = 0; row < inputs_->rowCount(); ++row) {
        auto name = inputs_->item(row, 0)->text().trimmed();
        if (name.isEmpty() || result.contains(name))
            throw std::runtime_error("输入名称为空或重复。");
        auto type = qobject_cast<QComboBox *>(inputs_->cellWidget(row, 1))->currentText();
        QJsonObject value{{"type", type}, {"required", true}};
        QString text = inputs_->item(row, 2)->text();
        if (type != "Image") {
            QJsonValue fallback;
            if (type == "String")
                fallback = text;
            else {
                auto document = QJsonDocument::fromJson(("[" + text + "]").toUtf8());
                if (!document.isArray() || document.array().size() != 1)
                    throw std::runtime_error("标量默认值必须与类型匹配。");
                fallback = document.array()[0];
                if ((type == "Bool" && !fallback.isBool()) ||
                    ((type == "Int" || type == "Double") && !fallback.isDouble()) ||
                    (type == "Int" && std::floor(fallback.toDouble()) != fallback.toDouble()))
                    throw std::runtime_error("默认值类型不匹配。");
            }
            value["default"] = fallback;
        }
        result[name] = value;
    }
    return result;
}
QJsonObject InterfaceDialog::outputs() const {
    QJsonObject result;
    for (int row = 0; row < outputs_->rowCount(); ++row) {
        auto name = outputs_->item(row, 0)->text().trimmed();
        if (name.isEmpty() || result.contains(name))
            throw std::runtime_error("输出名称为空或重复。");
        auto value = qobject_cast<QComboBox *>(outputs_->cellWidget(row, 1))->currentData().toJsonObject();
        if (value.isEmpty())
            throw std::runtime_error("请选择输出来源。");
        auto unit = outputs_->item(row, 2)->text();
        if (!unit.isEmpty())
            value["unit"] = unit;
        result[name] = value;
    }
    return result;
}

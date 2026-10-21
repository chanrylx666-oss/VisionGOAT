#include "StudioWindow.h"
#include "MeasurementUi.h"
#include "InterfaceDialog.h"
#include "StudioTheme.h"
#include "RegionEditor.h"
#include <QApplication>
#include <QCheckBox>
#include <QCloseEvent>
#include <QComboBox>
#include <QCryptographicHash>
#include <QDir>
#include <QDockWidget>
#include <QDoubleSpinBox>
#include <QFileDialog>
#include <QHBoxLayout>
#include <QHash>
#include <QImageReader>
#include <QJsonDocument>
#include <QLineEdit>
#include <QMenuBar>
#include <QMessageBox>
#include <QPushButton>
#include <QSaveFile>
#include <QScopedValueRollback>
#include <QScreen>
#include <QSignalBlocker>
#include <QSet>
#include <QSettings>
#include <QSplitter>
#include <QStatusBar>
#include <QTest>
#include <QTimer>
#include <QToolBar>
#include <QUndoStack>
#include <QUuid>
#include <QVBoxLayout>
#include <QtConcurrent/QtConcurrentRun>
#include <QtNodes/UndoCommands>
#include <QtNodes/internal/AbstractNodeGeometry.hpp>
#include <QtNodes/internal/NodeGraphicsObject.hpp>

using QtNodes::NodeId;
using QtNodes::NodeRole;
StudioWindow::~StudioWindow() {
    // UndoStack emits while it is destroyed. Disconnect UI callbacks first and
    // retain the graph model until the scene and graphics items have gone away.
    for (auto child : findChildren<QObject *>())
        QObject::disconnect(child, nullptr, this, nullptr);
    delete view_;
    delete scene_;
    delete model_;
}
static void writeJson(const QString &path, const QJsonObject &value) {
    QSaveFile file(path);
    if (!file.open(QIODevice::WriteOnly))
        throw std::runtime_error("无法创建项目文件。");
    auto bytes = QJsonDocument(value).toJson(QJsonDocument::Indented);
    if (file.write(bytes) != bytes.size() || !file.commit())
        throw std::runtime_error("项目文件保存失败。");
}
static QJsonObject readJson(const QString &path) {
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly) || file.size() > 4 * 1024 * 1024)
        throw std::runtime_error("项目文件不存在或超过大小限制。");
    QJsonParseError error;
    auto doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError || !doc.isObject())
        throw std::runtime_error("项目 JSON 格式无效。");
    return doc.object();
}
static void copyResources(const QString &sourceDirectory, const QString &targetDirectory,
                          const QJsonObject &recipe) {
    auto sourceBase = QFileInfo(sourceDirectory).canonicalFilePath() + "/";
    auto targetBase = QFileInfo(targetDirectory).canonicalFilePath() + "/";
    for (auto value : recipe["resources"].toArray()) {
        auto resource = value.toObject();
        auto relative = resource["path"].toString();
        auto suffix = QFileInfo(relative).suffix().toLower();
        if (QStringList{"vlic", "vreq", "private", "key", "pem", "pfx", "secret"}.contains(suffix))
            throw std::runtime_error("许可证或密钥文件不能作为配方资源导出。");
        auto clean = QDir::cleanPath(relative);
        if (QDir::isAbsolutePath(relative) || clean == ".." || clean.startsWith("../") || clean.contains(':'))
            throw std::runtime_error("资源路径不能离开配方目录。");
        auto source = QFileInfo(sourceDirectory + "/" + clean).canonicalFilePath();
        if (source.isEmpty() || !source.startsWith(sourceBase, Qt::CaseInsensitive))
            throw std::runtime_error("配方资源不存在或路径越界。");
        QFile file(source);
        if (!file.open(QIODevice::ReadOnly) || file.size() > 32 * 1024 * 1024)
            throw std::runtime_error("配方资源无法读取或超过上限。");
        auto bytes = file.readAll();
        if (QString::fromLatin1(QCryptographicHash::hash(bytes, QCryptographicHash::Sha256).toHex()) !=
            resource["sha256"].toString())
            throw std::runtime_error("资源摘要不匹配。");
        auto target = targetDirectory + "/" + clean;
        if (QFileInfo(target).canonicalFilePath() == source)
            continue;
        if (!QDir().mkpath(QFileInfo(target).absolutePath()))
            throw std::runtime_error("无法创建资源目录。");
        auto parent = QFileInfo(QFileInfo(target).absolutePath()).canonicalFilePath() + "/";
        if (!parent.startsWith(targetBase, Qt::CaseInsensitive))
            throw std::runtime_error("目标资源路径越界。");
        QSaveFile output(target);
        if (!output.open(QIODevice::WriteOnly) || output.write(bytes) != bytes.size() || !output.commit())
            throw std::runtime_error("资源复制失败。");
    }
}
class ParametersCommand : public QUndoCommand {
    GraphModel *model_;
    NodeId id_;
    QJsonObject before_, after_;

  public:
    ParametersCommand(GraphModel *m, NodeId id, QJsonObject before, QJsonObject after)
        : model_(m), id_(id), before_(before), after_(after) {
        setText("修改算子参数");
    }
    void undo() override {
        model_->setParameters(id_, before_);
    }
    void redo() override {
        model_->setParameters(id_, after_);
    }
};
class InterfacesCommand : public QUndoCommand {
    GraphModel *model_;
    QJsonObject before_, after_;
    void apply(const QJsonObject &value) {
        model_->setInterfaces(value["inputs"].toObject(), value["outputs"].toObject(),
                              value["decisionOutput"].toString(), value["kind"].toString());
    }

  public:
    InterfacesCommand(GraphModel *m, QJsonObject after) : model_(m), before_(m->interfaces()), after_(after) {
        setText("修改公开接口");
    }
    void undo() override {
        apply(before_);
    }
    void redo() override {
        apply(after_);
    }
};
class RepositionNodesCommand : public QUndoCommand {
    GraphModel *model_;
    QMap<NodeId, QPointF> before_, after_;
    void apply(const QMap<NodeId, QPointF> &positions) {
        for (auto it = positions.begin(); it != positions.end(); ++it)
            model_->setNodeData(it.key(), NodeRole::Position, it.value());
    }

  public:
    RepositionNodesCommand(GraphModel *model, const QSet<NodeId> &nodes, QPointF offset) : model_(model) {
        for (auto id : nodes) {
            auto point = model->nodeData(id, NodeRole::Position).toPointF();
            before_[id] = point;
            after_[id] = point + offset;
        }
    }
    void undo() override {
        apply(before_);
    }
    void redo() override {
        apply(after_);
    }
};
StudioWindow::StudioWindow(std::shared_ptr<NativeApi> api, bool smoke, QString screenshot, bool expectLocked)
    : api_(std::move(api)), smoke_(smoke), screenshot_(std::move(screenshot)), expectLocked_(expectLocked) {
    auto catalog = api_->json([&](char **out) { return api_->node_catalog(out); }).array();
    model_ = new GraphModel(catalog, this);
    scene_ = new QtNodes::BasicGraphicsScene(*model_, this);
    watcher_ = new QFutureWatcher<RunOutcome>(this);
    buildUi();
    connect(model_, &GraphModel::documentChanged, this, [this] {
        if (!loading_)
            invalidate();
    });
    connect(model_, &GraphModel::nodeDeleted, this, [this](NodeId id) {
        if (selected_ == id) {
            selected_ = QtNodes::InvalidNodeId;
            inspect(selected_);
        }
    });
    connect(scene_, &QtNodes::BasicGraphicsScene::nodeSelected, this, [this](NodeId id) {
        selected_ = id;
        inspect(id);
        showNodeImage(id);
    });
    connect(&scene_->undoStack(), &QUndoStack::indexChanged, this, [this] {
        if (!editing_) {
            inspect(selected_);
            if (model_->nodeExists(selected_) && model_->business(selected_)["type"] == "CropRect" &&
                !original_.isNull())
                prepareRoi(false);
            else if (supportsModuleRoi(selected_))
                prepareModuleRoi();
        }
        updateCaption();
    });
    connect(watcher_, &QFutureWatcher<RunOutcome>::finished, this, &StudioWindow::finished);
    connect(view_, &GraphView::nodeDropped, this, &StudioWindow::addNode);
    connect(imageView_, &ImageView::pixelChanged, pixelLabel_, &QLabel::setText);
    connect(imageView_, &ImageView::roiSelected, this, [this](QRect rect) {
        if (selected_ == QtNodes::InvalidNodeId || model_->business(selected_)["type"] != "CropRect")
            return;
        auto before = model_->business(selected_)["parameters"].toObject(), after = before;
        after["x"] = rect.x();
        after["y"] = rect.y();
        after["width"] = rect.width();
        after["height"] = rect.height();
        scene_->undoStack().push(new ParametersCommand(model_, selected_, before, after));
        roiAction_->setChecked(false);
        inspect(selected_);
        prepareRoi(false);
    });
    connect(imageView_, &ImageView::regionSelected, this, [this](QJsonObject region) {
        if (!supportsModuleRoi(selected_) || busy_)
            return;
        changeParameter(selected_, "roi", region);
        if (auto editor = inspector_->findChild<RegionEditor *>())
            editor->setValue(region);
        roiAction_->setChecked(false);
    });
    connect(imageView_, &ImageView::regionDrawingFinished, this, [this] { roiAction_->setChecked(false); });
    auto screen = QApplication::primaryScreen()->availableGeometry();
    resize(std::min(1672, screen.width() - 32), std::min(960, screen.height() - 48));
    setMinimumSize(1180, 760);
    setWindowTitle("JP-Vision · 视觉算法平台[*]");
    QTimer::singleShot(0, this, &StudioWindow::refreshLicense);
}
void StudioWindow::setEnabledState(bool authorized) {
    authorized_ = authorized;
    for (auto dock : businessDocks_)
        dock->setVisible(authorized);
    for (auto menu : businessMenus_)
        menu->setEnabled(authorized && !busy_);
    pages_->setCurrentIndex(authorized ? 1 : 0);
    for (auto action : {newAction_, openAction_, saveAction_, imageAction_, interfaceAction_, validateAction_,
                        runAction_, roiAction_})
        action->setEnabled(authorized && !busy_);
    tools_->parentWidget()->setEnabled(authorized && !busy_);
    view_->setEnabled(authorized && !busy_);
    inspector_->setEnabled(authorized && !busy_);
    expected_->setEnabled(authorized && !busy_);
    cancelAction_->setEnabled(busy_);
    imageView_->setEnabled(authorized && !busy_);
    parameterTabs_->setEnabled(authorized && !busy_);
    readyLabel_->setText(authorized ? (busy_ ? "●  运行中" : "●  就绪  |  JP-Vision Ready")
                                    : "○  授权不可用");
    readyLabel_->setStyleSheet(authorized ? "color:#E2E2E8;" : "color:#FF777D;");
}
void StudioWindow::refreshLicense() {
    auto watcher = new QFutureWatcher<QJsonObject>(this);
    auto api = api_;
    connect(watcher, &QFutureWatcher<QJsonObject>::finished, this, [this, watcher] {
        try {
            auto info = watcher->result();
            bool valid = info["code"].toInt() == 0;
            licenseLabel_->setText(valid ? "已授权 · " + info["customer"].toString() + " · 永久有效"
                                         : "未授权\n" + info["message"].toString() +
                                               "\n请导出机器请求并导入开发者签发的许可证。");
            setEnabledState(valid);
            if (!valid) {
                clearResult();
                engine_.reset();
                if (smoke_) {
                    QTimer::singleShot(150, this, [this] {
                        capture();
                        QApplication::exit(expectLocked_ ? 0 : 1);
                    });
                }
            }
            if (valid && expectLocked_) {
                QApplication::exit(1);
                return;
            }
            if (valid && !engine_.value) {
                engine_ = api_->engine();
                QString path =
                    QCoreApplication::applicationDirPath() + "/samples/recipes/parts-count.vrecipe";
                auto argument = [](const QString &name, QString fallback) {
                    auto args = QCoreApplication::arguments();
                    int index = args.indexOf(name);
                    return index >= 0 && index + 1 < args.size() ? args[index + 1] : fallback;
                };
                path = argument("--recipe", path);
                if (QFileInfo::exists(path + "/recipe.json"))
                    openRecipe(path);
                else
                    newRecipe();
                QString image = QCoreApplication::applicationDirPath() + "/samples/images/three-parts.png";
                image = argument("--image", image);
                if (QFileInfo::exists(image))
                    openImage(image);
                if (smoke_)
                    QTimer::singleShot(500, this, &StudioWindow::smokeTest);
            }
        } catch (const std::exception &e) {
            licenseLabel_->setText("授权检查失败：" + QString::fromUtf8(e.what()));
            setEnabledState(false);
            if (smoke_)
                QApplication::exit(1);
        }
        watcher->deleteLater();
    });
    watcher->setFuture(QtConcurrent::run([api] { return api->status(); }));
}
void StudioWindow::exportRequest() {
    auto path =
        QFileDialog::getSaveFileName(this, "导出机器请求", "JP-Vision-request.vreq", "机器请求 (*.vreq)");
    if (path.isEmpty())
        return;
    try {
        auto document = api_->json([&](char **out) { return api_->machine_request(out); });
        QSaveFile file(path);
        if (!file.open(QIODevice::WriteOnly) || file.write(document.toJson()) < 0 || !file.commit())
            throw std::runtime_error("机器请求保存失败。");
        QMessageBox::information(this, "请求已导出", "请将此文件交给开发者签发许可证。");
    } catch (const std::exception &e) {
        showError(e);
    }
}
void StudioWindow::importLicense() {
    auto path = QFileDialog::getOpenFileName(this, "导入许可证", {}, "JP-Vision 许可证 (*.vlic)");
    if (path.isEmpty())
        return;
    try {
        api_->importLicense(path);
        refreshLicense();
    } catch (const std::exception &e) {
        showError(e);
    }
}
void StudioWindow::showError(const std::exception &e) {
    QString message = QString::fromUtf8(e.what());
    log_->appendPlainText("错误 · " + message);
    if (message == "Public interfaces are invalid.")
        message = "配方的公开输入输出无效。请在“编辑 → 公开输入输出”中检查配置。";
    else if (message == "Inspection decision must bind a Bool output.")
        message = "检测配方尚未配置 Bool 判定输出。请添加判定模块，并在“编辑 → 公开输入输出”中选择检测判定。";
    resultLabel_->setText("操作失败 · 请查看日志");
    if (!smoke_)
        QMessageBox::warning(this, "JP-Vision", message);
}
void StudioWindow::clearResult() {
    result_.reset();
    snapshot_.reset();
    outcome_ = {};
    if (auto summary = inspector_->findChild<QLabel *>("MeasurementSummary"))
        summary->setText("结果已清除，请重新执行。");
    if (auto details = inspector_->findChild<QPushButton *>("MeasurementDetails"))
        details->setEnabled(false);
    if (auto trace = inspector_->findChild<QLabel *>("LastNodeTrace"))
        trace->hide();
    executedRecipe_ = {};
    updateIntermediate();
    updateNodeResult(selected_);
    model_->setTrace({});
    roiAction_->setChecked(false);
    imageView_->setRoiMode(false);
    resultLabel_->setText("等待执行");
    if (!original_.isNull())
        imageView_->present(original_);
}
QJsonArray StudioWindow::resultOverlays() const {
    auto definitions = executedRecipe_.value("outputs").toObject();
    auto values = outcome_.value("outputs").toObject();
    if (definitions.value("ResultOverlay").toObject().value("type") == "Overlay")
        return values.value("ResultOverlay").toArray();
    for (auto it = definitions.begin(); it != definitions.end(); ++it)
        if (it.value().toObject().value("type") == "Overlay")
            return values.value(it.key()).toArray();
    return {};
}
QJsonArray StudioWindow::nodeOverlays(NodeId id) const {
    auto stable = model_->stableId(id);
    auto debug = outcome_.value("debugOverlays").toObject();
    if (debug.contains(stable))
        return debug.value(stable).toArray();
    auto definitions = executedRecipe_.value("outputs").toObject();
    auto outputs = outcome_.value("outputs").toObject();
    for (auto it = definitions.begin(); it != definitions.end(); ++it) {
        auto definition = it.value().toObject();
        if (definition["node"] == stable && definition["type"] == "Overlay")
            return outputs.value(it.key()).toArray();
    }
    return {};
}
void StudioWindow::showResultImage() {
    if (original_.isNull())
        return;
    auto image = original_;
    QPoint origin;
    if (result_.value && outcome_.value("executionStatus") == "Succeeded" &&
        outcome_.value("recipeKind") == "processing") {
        auto outputs = executedRecipe_.value("outputs").toObject();
        QString name;
        for (auto it = outputs.begin(); it != outputs.end(); ++it) {
            auto output = it.value().toObject();
            if (output.value("type") != "Image" && output.value("type") != "Mask")
                continue;
            if (name.isEmpty())
                name = it.key();
            if (output.value("node").toString() == model_->stableId(selected_)) {
                name = it.key();
                break;
            }
        }
        if (!name.isEmpty()) {
            try {
                jpv_handle pointer = nullptr;
                api_->check(api_->result_output_frame(result_.value, name.toUtf8().constData(), &pointer));
                OwnedHandle frame(pointer, api_->frame_release);
                jpv_image_info info{};
                image = api_->image(frame.value, &info);
                origin = QPoint(info.origin_x, info.origin_y);
            } catch (const std::exception &e) {
                showError(e);
            }
        }
    }
    imageView_->present(image, resultOverlays(), origin);
    imageView_->clearRoi();
}
QString StudioWindow::resultSummary() const {
    auto status = outcome_.value("executionStatus").toString();
    if (status != "Succeeded")
        return "执行未完成 · " + status;
    QStringList values;
    auto outputs = outcome_.value("outputs").toObject();
    for (auto it = outputs.begin(); it != outputs.end(); ++it) {
        auto value = it.value();
        if (value.isDouble())
            values.append(it.key() + " " + QString::number(value.toDouble()));
        else if (value.isString())
            values.append(it.key() + " " + value.toString());
    }
    auto decision = outcome_.value("decision").toString();
    auto label = decision == "Pass"                             ? QString("OK / 合格")
                 : decision == "Fail"                           ? QString("NG / 不合格")
                 : outcome_.value("recipeKind") == "processing" ? QString("调试完成 · 未配置检测判定")
                                                                : QString("执行完成 · 判定未知");
    return QString("%1 · %2 · %3 ms")
        .arg(label, values.join(" · "))
        .arg(outcome_.value("elapsedMs").toDouble(), 0, 'f', 2);
}
void StudioWindow::invalidate() {
    dirty_ = true;
    setWindowModified(true);
    updateCaption();
    clearResult();
    resultLabel_->setText("草稿 · 流程已修改，请重新执行");
}
void StudioWindow::addNode(QString type, QPointF position) {
    if (!authorized_ || busy_)
        return;
    scene_->undoStack().push(new QtNodes::CreateCommand(scene_, type, position));
}
void StudioWindow::changeParameter(NodeId id, QString name, QJsonValue value) {
    if (busy_ || !model_->nodeExists(id))
        return;
    QScopedValueRollback<bool> editing(editing_, true);
    auto before = model_->business(id)["parameters"].toObject(), after = before;
    after[name] = value;
    if (model_->business(id)["type"] == "CropRect" && !original_.isNull()) {
        prepareRoi(false);
        auto size = imageView_->currentImage().size();
        if (!size.isEmpty()) {
            int x = std::clamp(after["x"].toInt(), 0, size.width() - 1),
                y = std::clamp(after["y"].toInt(), 0, size.height() - 1);
            after["x"] = x;
            after["y"] = y;
            after["width"] = std::clamp(after["width"].toInt(), 1, size.width() - x);
            after["height"] = std::clamp(after["height"].toInt(), 1, size.height() - y);
        }
    }
    if (before != after)
        scene_->undoStack().push(new ParametersCommand(model_, id, before, after));
    if (model_->business(id)["type"] == "CropRect" && !original_.isNull())
        prepareRoi(false);
    else if (supportsModuleRoi(id))
        prepareModuleRoi();
}
void StudioWindow::inspect(NodeId id) {
    while (form_->count()) {
        auto item = form_->takeAt(0);
        if (item->widget())
            delete item->widget();
        delete item;
    }
    details_ = new QLabel;
    details_->setWordWrap(true);
    details_->setObjectName("NodeHeading");
    form_->addRow(details_);
    updateNodeResult(id);
    if (!model_->nodeExists(id)) {
        details_->setText("选择节点查看参数。\n插入算子后，连接相同类型的端口。");
        auto empty = new QLabel;
        empty->setAlignment(Qt::AlignCenter);
        empty->setPixmap(StudioTheme::icon("file", QColor("#66666E")).pixmap(64, 64));
        empty->setContentsMargins(0, 22, 0, 22);
        form_->addRow(empty);
        auto note = new QLabel("当前未选择节点\n\n在流程图中选择一个组件，\n在此处直接编辑参数。");
        note->setAlignment(Qt::AlignCenter);
        note->setObjectName("EmptyNote");
        form_->addRow(note);
        return;
    }
    auto node = model_->business(id), meta = model_->descriptor(node["type"].toString());
    details_->setText(meta["label"].toString() + "\n" + node["id"].toString());
    auto measurement = outcome_.value("measurements").toObject().value(node["id"].toString()).toObject();
    if (!measurement.isEmpty()) {
        auto summary = new QLabel(MeasurementUi::summary(measurement));
        summary->setObjectName("MeasurementSummary");
        summary->setWordWrap(true);
        summary->setTextInteractionFlags(Qt::TextSelectableByMouse);
        form_->addRow(summary);
        auto results = new QPushButton("查看全部目标与测量结果");
        results->setObjectName("MeasurementDetails");
        connect(results, &QPushButton::clicked, this, [this] { parameterTabs_->setCurrentIndex(1); });
        form_->addRow(results);
    }
    auto parameters = node["parameters"].toObject();
    static const QSet<QString> caliperNames{
        "caliperCount", "projectionWidth", "searchLength",     "caliperThreshold", "sigma",
        "polarity",     "edgeSelection",   "maximumDeviation", "removePointCount", "minimumCoverage",
        "radius",       "startAngle",      "endAngle"};
    static const QSet<QString> automaticNames{
        "dp", "minimumDistance", "edgeThreshold", "voteThreshold", "minimumRadius", "maximumRadius"};
    bool circleFind = node["type"] == "FindCircles",
         caliperMode = parameters.value("method").toString("automatic") == "caliper";
    for (const auto &v : meta["parameters"].toArray()) {
        auto p = v.toObject();
        auto name = p["name"].toString(), type = p["type"].toString();
        auto value = parameters.contains(name) ? parameters[name] : p["default"];
        if (circleFind &&
            ((caliperNames.contains(name) && !caliperMode) || (automaticNames.contains(name) && caliperMode)))
            continue;
        if (name == "caliperCount" || name == "resultIndex" || name == "showOverlay") {
            auto heading = new QLabel(name == "caliperCount"  ? "卡尺与拟合"
                                      : name == "resultIndex" ? "结果选择与数量判定"
                                                              : "结果标记");
            heading->setObjectName("NodeHeading");
            form_->addRow(heading);
        }
        if (type == "roi") {
            QSize imageSize = original_.size();
            try {
                imageSize = upstreamImage(id).size();
            } catch (const std::exception &e) {
                log_->appendPlainText(QString::fromUtf8(e.what()));
            }
            auto editor = new RegionEditor(value.toObject(), imageSize);
            form_->addRow("模块检测区域", editor);
            connect(editor, &RegionEditor::valueChanged, this,
                    [this, id](QJsonObject v) { changeParameter(id, "roi", v); });
            connect(editor, &RegionEditor::editRequested, this, [this, id](QString) {
                selected_ = id;
                prepareModuleRoi(true);
            });
        } else if (type == "template") {
            auto box = new QWidget;
            auto layout = new QVBoxLayout(box);
            layout->setContentsMargins(0, 0, 0, 0);
            auto model = value.toObject();
            auto status = new QLabel(model.isEmpty() ? "尚未学习模板"
                                                     : QString("已学习 %1 × %2 模板")
                                                           .arg(model["width"].toInt())
                                                           .arg(model["height"].toInt()));
            status->setWordWrap(true);
            layout->addWidget(status);
            auto teach = new QPushButton("从当前图像学习模板");
            teach->setObjectName("TeachTemplate");
            layout->addWidget(teach);
            auto import = new QPushButton("导入模板图像…");
            layout->addWidget(import);
            connect(teach, &QPushButton::clicked, this, [this, id] { teachTemplate(id); });
            connect(import, &QPushButton::clicked, this, [this, id] { teachTemplate(id, true); });
            form_->addRow("模板教学", box);
        } else if (type == "integer") {
            auto box = new QWidget;
            auto row = new QVBoxLayout(box);
            row->setContentsMargins(0, 0, 0, 0);
            auto number = new QSpinBox;
            number->setObjectName("Param_" + name);
            number->setKeyboardTracking(false);
            number->setRange(p["min"].toInt(), p["max"].toInt());
            number->setValue(value.isObject() ? model_->interfaces()["inputs"]
                                                    .toObject()[value.toObject()["input"].toString()]
                                                    .toObject()["default"]
                                                    .toInt()
                                              : value.toInt());
            row->addWidget(number);
            if (p["bindable"].toBool()) {
                auto binding = new QComboBox;
                binding->addItem("固定值", QString());
                auto inputs = model_->interfaces()["inputs"].toObject();
                for (auto it = inputs.begin(); it != inputs.end(); ++it)
                    if (it.value().toObject()["type"] == "Int")
                        binding->addItem("绑定 · " + it.key(), it.key());
                if (value.isObject()) {
                    auto input = value.toObject()["input"].toString();
                    int index = binding->findData(input);
                    if (index < 0) {
                        binding->addItem("缺失 · " + input, input);
                        index = binding->count() - 1;
                    }
                    binding->setCurrentIndex(index);
                }
                number->setEnabled(binding->currentIndex() == 0);
                row->addWidget(binding);
                connect(binding, &QComboBox::currentIndexChanged, this, [this, id, name, binding, number] {
                    auto input = binding->currentData().toString();
                    number->setEnabled(input.isEmpty());
                    changeParameter(id, name,
                                    input.isEmpty() ? QJsonValue(number->value())
                                                    : QJsonValue(QJsonObject{{"input", input}}));
                });
                connect(number, &QSpinBox::valueChanged, this, [this, id, name, binding](int v) {
                    if (binding->currentIndex() == 0)
                        changeParameter(id, name, v);
                });
            } else
                connect(number, &QSpinBox::valueChanged, this,
                        [this, id, name](int v) { changeParameter(id, name, v); });
            form_->addRow(p["label"].toString(), box);
        } else if (type == "number") {
            auto number = new QDoubleSpinBox;
            number->setObjectName("Param_" + name);
            number->setKeyboardTracking(false);
            number->setDecimals(3);
            number->setRange(p["min"].toDouble(), p["max"].toDouble());
            number->setValue(value.toDouble());
            form_->addRow(p["label"].toString(), number);
            connect(number, &QDoubleSpinBox::valueChanged, this,
                    [this, id, name](double v) { changeParameter(id, name, v); });
        } else if (type == "bool") {
            auto check = new QCheckBox;
            check->setObjectName("Param_" + name);
            check->setChecked(value.toBool());
            form_->addRow(p["label"].toString(), check);
            connect(check, &QCheckBox::toggled, this,
                    [this, id, name](bool v) { changeParameter(id, name, v); });
        } else if (type == "enum") {
            auto combo = new QComboBox;
            combo->setObjectName("Param_" + name);
            static const QHash<QString, QString> choices{
                {"automatic", "自动找圆"},  {"caliper", "卡尺拟合"},    {"any", "任意极性"},
                {"darkToLight", "暗 → 亮"}, {"lightToDark", "亮 → 暗"}, {"strongest", "最强边缘"},
                {"first", "第一条边缘"},    {"last", "最后一条边缘"}};
            for (auto choice : p["choices"].toArray())
                combo->addItem(choices.value(choice.toString(), choice.toString()), choice.toString());
            combo->setCurrentIndex(combo->findData(value.toString()));
            form_->addRow(p["label"].toString(), combo);
            if (name == "method" && circleFind) {
                auto hint = new QLabel("卡尺拟合：在 ROI 内设置卡尺数量、宽高和剔除点数，测量单个圆。");
                hint->setWordWrap(true);
                form_->addRow(hint);
            }
            connect(combo, &QComboBox::currentIndexChanged, this, [this, id, name, combo] {
                changeParameter(id, name, combo->currentData().toString());
                if (name == "method")
                    QTimer::singleShot(0, this, [this, id] {
                        if (selected_ == id) {
                            inspect(id);
                            prepareModuleRoi();
                        }
                    });
            });
        } else {
            auto text = new QLineEdit(value.toString());
            text->setObjectName("Param_" + name);
            text->setMaxLength(256);
            form_->addRow(p["label"].toString(), text);
            connect(text, &QLineEdit::editingFinished, this,
                    [this, id, name, text] { changeParameter(id, name, text->text()); });
        }
    }
    if (!outcome_.value("nodes").toObject().isEmpty()) {
        auto trace = outcome_.value("nodes").toObject().value(node["id"].toString()).toObject();
        QJsonValue value = outcome_.value("debugValues").toObject().value(node["id"].toString());
        QString text = "本次执行 · " + trace["status"].toString() + "\n耗时 " +
                       QString::number(trace["elapsedMs"].toDouble(), 'f', 3) + " ms";
        if (value.isDouble())
            text += "\n数值 · " + QString::number(value.toDouble());
        if (value.isBool())
            text += "\n布尔 · " + QString(value.toBool() ? "true" : "false");
        if (value.isArray())
            text += "\n结果项 · " + QString::number(value.toArray().size());
        auto label = new QLabel(text);
        label->setObjectName("LastNodeTrace");
        label->setWordWrap(true);
        form_->addRow(label);
    }
}
void StudioWindow::showNodeImage(NodeId id) {
    if (original_.isNull())
        return;
    if (supportsModuleRoi(id) &&
        (model_->descriptor(model_->business(id)["type"].toString())["outputs"]
                 .toArray()
                 .first()
                 .toObject()["type"] == "Features" ||
         model_->business(id)["parameters"].toObject().value("roi").toObject().value("shape").toString(
             "none") != "none")) {
        selected_ = id;
        prepareModuleRoi();
        return;
    }
    if (model_->nodeExists(id) && model_->business(id)["type"] == "CropRect") {
        selected_ = id;
        prepareRoi(false);
        return;
    }
    if (!model_->nodeExists(id) || !result_.value) {
        imageView_->present(original_);
        return;
    }
    auto node = model_->business(id);
    auto ports = model_->descriptor(node["type"].toString())["outputs"].toArray();
    if (ports.isEmpty())
        return;
    auto type = ports.first().toObject()["type"].toString();
    if (type == "Image" || type == "Mask") {
        try {
            jpv_handle value = nullptr;
            api_->check(
                api_->result_frame(result_.value, node["id"].toString().toUtf8().constData(), &value));
            OwnedHandle image(value, api_->frame_release);
            jpv_image_info info{};
            auto pixels = api_->image(image.value, &info);
            imageView_->present(pixels, {}, QPoint(info.origin_x, info.origin_y));
            return;
        } catch (const std::exception &e) {
            log_->appendPlainText(QString::fromUtf8(e.what()));
        }
    }
    auto overlay = resultOverlays();
    if (type == "Contours") {
        overlay = {};
        for (const auto &points : outcome_["debugValues"].toObject()[node["id"].toString()].toArray())
            overlay.append(QJsonObject{{"type", "polyline"},
                                       {"closed", true},
                                       {"points", points},
                                       {"color", "#44D7B6"},
                                       {"lineWidth", 2}});
    }
    if (type == "Features")
        overlay = nodeOverlays(id);
    if (type == "Overlay")
        overlay = outcome_["debugValues"].toObject()[node["id"].toString()].toArray();
    imageView_->present(original_, overlay);
}
bool StudioWindow::confirmDiscard() {
    if (!dirty_)
        return true;
    if (smoke_)
        return true;
    auto answer = QMessageBox::question(this, "未保存的修改", "是否保存当前配方？",
                                        QMessageBox::Save | QMessageBox::Discard | QMessageBox::Cancel);
    if (answer == QMessageBox::Cancel)
        return false;
    if (answer == QMessageBox::Save) {
        saveRecipe(false);
        return !dirty_;
    }
    return true;
}
void StudioWindow::openRecipe(QString directory) {
    QFile raw(directory + "/recipe.json");
    if (!raw.open(QIODevice::ReadOnly) || raw.size() > 4 * 1024 * 1024)
        throw std::runtime_error("配方无法读取。");
    auto bytes = raw.readAll();
    api_->json([&](char **out) { return api_->recipe_seal(bytes.constData(), out); });
    auto recipe = readJson(directory + "/recipe.json"), layout = QFileInfo::exists(directory + "/layout.json")
                                                                     ? readJson(directory + "/layout.json")
                                                                     : QJsonObject();
    QScopedValueRollback<bool> loading(loading_, true);
    model_->load(recipe, layout);
    scene_->undoStack().clear();
    directory_ = directory;
    dirty_ = false;
    setWindowModified(false);
    selected_ = QtNodes::InvalidNodeId;
    clearResult();
    inspect(selected_);
    expected_->setValue(recipe["inputs"].toObject()["ExpectedCount"].toObject()["default"].toInt(3));
    setWindowTitle("JP-Vision · " + QFileInfo(directory).fileName() + "[*]");
    updateCaption();
    QTimer::singleShot(0, view_, &GraphView::zoomFitAll);
    resultLabel_->setText("配方已加载 · 等待执行");
    log_->appendPlainText("打开配方 · " + QFileInfo(directory).fileName());
}
QJsonObject StudioWindow::sealedRecipe(bool forExecution) const {
    auto bytes = QJsonDocument(forExecution ? model_->executionRecipe() : model_->recipe())
                     .toJson(QJsonDocument::Compact);
    auto sealed = api_->json([&](char **out) { return api_->recipe_seal(bytes.constData(), out); }).object();
    auto saved = model_->interfaces()["contentHash"].toString();
    if (!saved.isEmpty() && saved != sealed["contentHash"].toString())
        sealed["recipeVersion"] = sealed["recipeVersion"].toInt() + 1;
    return sealed;
}
void StudioWindow::saveRecipe(bool choose) {
    if (!authorized_ || busy_)
        return;
    try {
        QString path = directory_;
        if (choose || path.isEmpty()) {
            path = QFileDialog::getSaveFileName(this, "保存配方包", "untitled.vrecipe",
                                                "JP-Vision 配方包 (*.vrecipe)");
            if (path.isEmpty())
                return;
        }
        if (!QDir().mkpath(path))
            throw std::runtime_error("无法创建配方目录。");
        auto recipe = sealedRecipe();
        copyResources(directory_, path, recipe);
        writeJson(path + "/recipe.json", recipe);
        writeJson(path + "/layout.json", model_->layout());
        model_->markSaved(recipe);
        directory_ = path;
        dirty_ = false;
        setWindowModified(false);
        updateCaption();
        log_->appendPlainText("已保存 · " + path);
        resultLabel_->setText("已保存 · 可校验或执行");
    } catch (const std::exception &e) {
        showError(e);
    }
}
void StudioWindow::openImage(QString path) {
    QImageReader reader(path);
    auto size = reader.size();
    if (qint64(size.width()) * size.height() > 32LL * 1024 * 1024)
        throw std::runtime_error("图像超过 32MP 上限。");
    auto image = reader.read();
    if (image.isNull())
        throw std::runtime_error("图像读取失败。");
    // Keep the preview and the native input in the same supported pixel format.
    image = image.convertToFormat(QImage::Format_BGR888);
    auto next = api_->frame(image);
    frame_ = std::move(next);
    original_ = image;
    imageName_ = QFileInfo(path).fileName();
    clearResult();
    imageView_->present(image);
    log_->appendPlainText("输入图像 · " + QFileInfo(path).fileName() +
                          QString(" · %1 × %2").arg(image.width()).arg(image.height()));
}
void StudioWindow::newRecipe() {
    if (!authorized_ || busy_ || !confirmDiscard())
        return;
    QJsonObject recipe{
        {"schemaVersion", 1},
        {"minimumRuntimeAbi", 1},
        {"recipeId", QUuid::createUuid().toString(QUuid::WithoutBraces)},
        {"recipeVersion", 1},
        {"kind", "inspection"},
        {"decisionOutput", "InspectionPassed"},
        {"contentHash", ""},
        {"inputs", QJsonObject{{"InputImage", QJsonObject{{"type", "Image"}, {"required", true}}},
                               {"ExpectedCount", QJsonObject{{"type", "Int"}, {"default", 3}}}}},
        {"outputs", QJsonObject()},
        {"nodes", QJsonArray()},
        {"connections", QJsonArray()},
        {"resources", QJsonArray()}};
    {
        QScopedValueRollback<bool> loading(loading_, true);
        model_->load(recipe, {});
        scene_->undoStack().clear();
    }
    directory_.clear();
    selected_ = QtNodes::InvalidNodeId;
    inspect(selected_);
    invalidate();
    setWindowTitle("JP-Vision · 新建配方[*]");
    log_->appendPlainText("新建草稿 · 连接算子后可直接调试；供 WPF 检测使用时配置公开输出和判定。");
}
void StudioWindow::configureInterfaces() {
    if (!authorized_ || busy_)
        return;
    InterfaceDialog dialog(model_, this);
    if (dialog.exec() == QDialog::Accepted) {
        scene_->undoStack().push(new InterfacesCommand(model_, {{"inputs", dialog.inputs()},
                                                                {"outputs", dialog.outputs()},
                                                                {"kind", dialog.kind()},
                                                                {"decisionOutput", dialog.decision()}}));
        inspect(selected_);
        if (supportsModuleRoi(selected_))
            prepareModuleRoi();
        log_->appendPlainText("公开接口已更新。");
    }
}
void StudioWindow::validate() {
    if (!authorized_ || busy_)
        return;
    try {
        auto recipe = sealedRecipe(true);
        auto bytes = QJsonDocument(recipe).toJson(QJsonDocument::Compact);
        auto report = api_->json([&](char **out) {
                              return api_->recipe_validate(engine_.value, bytes.constData(), out);
                          })
                          .object();
        resultLabel_->setText("校验通过 · " + QString::number(report["executionOrder"].toArray().size()) +
                              " 个执行节点");
        log_->appendPlainText(recipe.value("kind") == "processing" ? "调试流程校验通过 · 不输出检测判定。"
                                                                   : "流程校验通过。");
    } catch (const std::exception &e) {
        showError(e);
    }
}
void StudioWindow::run() {
    if (!authorized_ || busy_)
        return;
    clearResult();
    try {
        if (!frame_.value)
            throw std::runtime_error("请先打开调试图像。");
        snapshot_ = std::make_unique<QTemporaryDir>(QDir::tempPath() + "/JP-Vision-snapshot-XXXXXX");
        if (!snapshot_->isValid())
            throw std::runtime_error("无法创建执行快照。");
        auto recipe = sealedRecipe(true);
        executedRecipe_ = recipe;
        if (model_->interfaces().value("outputs").toObject().isEmpty())
            log_->appendPlainText("调试运行 · 自动选择末端输出，尚未配置检测判定。");
        writeJson(snapshot_->path() + "/recipe.json", recipe);
        executedLayout_ = model_->layout();
        copyResources(directory_, snapshot_->path(), recipe);
        jpv_handle pointer = nullptr;
        auto path = snapshot_->path().toUtf8();
        api_->check(api_->recipe_load(engine_.value, path.constData(), &pointer));
        OwnedHandle loaded(pointer, api_->recipe_release);
        pointer = nullptr;
        api_->check(api_->session_create(loaded.value, &pointer));
        OwnedHandle session(pointer, api_->session_release);
        QJsonObject inputs;
        auto definitions = recipe["inputs"].toObject();
        for (auto it = definitions.begin(); it != definitions.end(); ++it) {
            auto def = it.value().toObject();
            if (def["type"] != "Image" && def.contains("default"))
                inputs[it.key()] = def["default"];
        }
        if (definitions.contains("ExpectedCount"))
            inputs["ExpectedCount"] = expected_->value();
        runRequest_ = QJsonObject{{"runId", QUuid::createUuid().toString(QUuid::WithoutBraces)},
                                  {"frameId", QUuid::createUuid().toString(QUuid::WithoutBraces)},
                                  {"inputs", inputs},
                                  {"debug", debugImages_},
                                  {"timeoutMs", timeoutMs_}};
        auto request = QJsonDocument(runRequest_).toJson(QJsonDocument::Compact);
        pointer = nullptr;
        api_->check(api_->run_create(session.value, frame_.value, request.constData(), &pointer));
        run_ = OwnedHandle(pointer, api_->run_release);
        // The accepted Run retains Session/Recipe/Frame; keep the public Session handle
        // alive until execution finishes, as release intentionally requests cancellation.
        auto retainedSession = std::make_shared<OwnedHandle>(std::move(session));
        auto api = api_;
        auto handle = run_.value;
        result_.reset();
        outcome_ = {};
        busy_ = true;
        setEnabledState(true);
        resultLabel_->setText("执行中…");
        imageView_->present(original_);
        watcher_->setFuture(QtConcurrent::run([api, handle, retainedSession] {
            RunOutcome outcome;
            try {
                api->check(api->run_execute(handle, &outcome.result));
                outcome.data =
                    api->json([&](char **out) { return api->result_json(outcome.result, out); }).object();
            } catch (const std::exception &e) {
                outcome.error = QString::fromUtf8(e.what());
            }
            return outcome;
        }));
    } catch (const std::exception &e) {
        snapshot_.reset();
        showError(e);
        if (smoke_)
            QApplication::exit(1);
    }
}
void StudioWindow::finished() {
    auto outcome = watcher_->result();
    run_.reset();
    busy_ = false;
    setEnabledState(authorized_);
    result_ = OwnedHandle(outcome.result, api_->result_release);
    outcome_ = outcome.data;
    updateIntermediate();
    if (!outcome.error.isEmpty()) {
        log_->appendPlainText("执行失败 · " + outcome.error);
        resultLabel_->setText("执行错误");
    } else {
        auto state = outcome_["executionStatus"].toString(), decision = outcome_["decision"].toString();
        model_->setTrace(outcome_["nodes"].toObject());
        resultLabel_->setText(resultSummary());
        log_->appendPlainText("执行结果 · " + resultLabel_->text());
        if (!outcome_["error"].isNull())
            log_->appendPlainText(QJsonDocument(outcome_["error"].toObject()).toJson(QJsonDocument::Compact));
        imageView_->present(original_, resultOverlays());
        inspect(selected_);
        if (supportsModuleRoi(selected_))
            prepareModuleRoi();
        if (outcome_.value("recipeKind") == "processing")
            showResultImage();
    }
    if (smoke_) {
        bool valid = outcome_["decision"] == "Pass" &&
                     outcome_["outputs"].toObject()["ObjectCount"].toInt() == expected_->value();
        if (QCoreApplication::arguments().contains("--draft-smoke"))
            valid =
                outcome_.value("executionStatus") == "Succeeded" && outcome_.value("decision") == "Unknown" &&
                outcome_.value("nodes").toObject().size() == 2 && imageView_->currentImage().isGrayscale() &&
                model_->interfaces().value("outputs").toObject().isEmpty() &&
                resultSummary().contains("未配置检测判定");
        if (QCoreApplication::arguments().contains("--processing-smoke"))
            valid = outcome_.value("executionStatus") == "Succeeded" &&
                    outcome_.value("decision") == "Unknown" && imageView_->currentImage().isGrayscale() &&
                    outcome_.value("outputs").toObject().contains("GrayImage");
        if (QCoreApplication::arguments().contains("--measurement-smoke")) {
            auto m = outcome_.value("measurements").toObject().value("detect").toObject();
            auto summary = inspector_->findChild<QLabel *>("MeasurementSummary");
            valid = valid && m["selectedValid"].toBool() && summary && summary->text().contains("圆心") &&
                    outcome_.value("nodes").toObject().size() == 2;
            if (QCoreApplication::arguments().contains("--edit-calipers"))
                valid = valid && m["caliper"].toObject()["requestedCount"] == 52 &&
                        m["caliper"].toObject()["rejectedCount"].toInt() >= 6 &&
                        summary->text().contains("剔除");
            if (valid && !measurementInvalidationVerified_) {
                measurementInvalidationVerified_ = true;
                clearResult();
                auto details = inspector_->findChild<QPushButton *>("MeasurementDetails");
                if (summary->text().contains("圆心") || !summary->text().contains("重新执行") || !details ||
                    details->isEnabled()) {
                    log_->appendPlainText("修改／清除后仍显示过期测量。");
                    QApplication::exit(1);
                    return;
                }
                run();
                return;
            }
        }
        try {
            QTemporaryDir repro;
            auto path = repro.path() + "/export";
            writeRepro(path);
            jpv_handle pointer = nullptr;
            auto bytes = (path + "/recipe.vrecipe").toUtf8();
            api_->check(api_->recipe_load(engine_.value, bytes.constData(), &pointer));
            OwnedHandle replay(pointer, api_->recipe_release);
            pointer = nullptr;
            api_->check(api_->session_create(replay.value, &pointer));
            OwnedHandle session(pointer, api_->session_release);
            auto request = QJsonDocument(readJson(path + "/request.json")).toJson(QJsonDocument::Compact);
            pointer = nullptr;
            api_->check(api_->run_create(session.value, frame_.value, request.constData(), &pointer));
            OwnedHandle rerun(pointer, api_->run_release);
            pointer = nullptr;
            api_->check(api_->run_execute(rerun.value, &pointer));
            OwnedHandle result(pointer, api_->result_release);
            auto replayed =
                api_->json([&](char **out) { return api_->result_json(result.value, out); }).object();
            if (replayed["outputs"] != outcome_["outputs"] || replayed["decision"] != outcome_["decision"])
                valid = false;
            if (readJson(path + "/request.json")["inputs"] != outcome_["inputs"])
                valid = false;
        } catch (const std::exception &e) {
            showError(e);
            valid = false;
        }
        QTimer::singleShot(150, this, [this, valid] {
            if (QCoreApplication::arguments().contains("--ui-smoke"))
                prepareRoi(false);
            capture();
            QApplication::exit(valid ? 0 : 1);
        });
    } else if (closing_)
        close();
}
void StudioWindow::roi() {
    if (!authorized_ || busy_)
        return;
    if (!roiAction_->isChecked()) {
        imageView_->setRoiMode(false);
        imageView_->cancelRegionDrawing();
        return;
    }
    if (original_.isNull()) {
        roiAction_->setChecked(false);
        log_->appendPlainText("请先打开图像。");
        return;
    }
    try {
        if (supportsModuleRoi(selected_)) {
            auto p = model_->business(selected_)["parameters"].toObject();
            if (p.value("roi").toObject().value("shape").toString("none") == "none") {
                auto editor = inspector_->findChild<RegionEditor *>();
                auto image = upstreamImage(selected_);
                QJsonObject region{{"shape", "rectangle"},
                                   {"x", 0},
                                   {"y", 0},
                                   {"width", image.width()},
                                   {"height", image.height()}};
                changeParameter(selected_, "roi", region);
                if (editor)
                    editor->setValue(region);
            }
            prepareModuleRoi(true);
            return;
        }
        if (!model_->nodeExists(selected_) || model_->business(selected_)["type"] != "CropRect")
            throw std::runtime_error("请选择支持 ROI 的图像、检测或测量模块。");
        selected_ = ensureRoiNode();
        inspect(selected_);
        prepareRoi(true);
    } catch (const std::exception &e) {
        roiAction_->setChecked(false);
        showError(e);
    }
}
NodeId StudioWindow::ensureRoiNode() {
    if (model_->nodeExists(selected_) && model_->business(selected_)["type"] == "CropRect")
        return selected_;
    auto destination = selected_;
    if (!model_->nodeExists(destination)) {
        for (auto id : model_->allNodeIds()) {
            auto type = model_->business(id)["type"].toString();
            if (type == "FindCircles" || type == "FindLines" || type == "Threshold") {
                destination = id;
                break;
            }
        }
    }
    auto inputEdge = [&](NodeId id) {
        for (auto edge : model_->allConnectionIds(id))
            if (edge.inNodeId == id)
                return edge;
        return QtNodes::ConnectionId{QtNodes::InvalidNodeId, 0, QtNodes::InvalidNodeId, 0};
    };
    QSet<NodeId> visited;
    while (model_->nodeExists(destination)) {
        if (visited.contains(destination))
            throw std::runtime_error("ROI 上游出现环路。");
        visited.insert(destination);
        auto ports = model_->descriptor(model_->business(destination)["type"].toString())["inputs"].toArray();
        if (!ports.isEmpty() && ports.first().toObject()["type"] == "Image")
            break;
        destination = inputEdge(destination).outNodeId;
    }
    if (!model_->nodeExists(destination))
        throw std::runtime_error("请选择已连接图像输入的处理组件，再设置 ROI。");
    auto edge = inputEdge(destination);
    if (!model_->nodeExists(edge.outNodeId))
        throw std::runtime_error("请先连接该组件的图像输入。");
    auto parent = edge.outNodeId;
    auto sourceSize = original_.size();
    visited.clear();
    auto ancestor = parent;
    while (model_->nodeExists(ancestor)) {
        if (visited.contains(ancestor))
            throw std::runtime_error("ROI 上游出现环路。");
        visited.insert(ancestor);
        if (model_->business(ancestor)["type"] == "CropRect")
            return ancestor;
        ancestor = inputEdge(ancestor).outNodeId;
    }
    QSet<NodeId> descendants;
    std::function<void(NodeId)> visit = [&](NodeId id) {
        if (descendants.contains(id))
            return;
        descendants.insert(id);
        for (auto e : model_->allConnectionIds(id))
            if (e.outNodeId == id)
                visit(e.inNodeId);
    };
    visit(destination);
    auto position = model_->nodeData(destination, NodeRole::Position).toPointF();
    auto previous = model_->allNodeIds();
    QScopedValueRollback<bool> editing(editing_, true);
    auto &undo = scene_->undoStack();
    undo.beginMacro("添加并连接矩形 ROI");
    undo.push(new RepositionNodesCommand(model_, descendants, {250, 0}));
    undo.push(new QtNodes::CreateCommand(scene_, "CropRect", position));
    NodeId crop = QtNodes::InvalidNodeId;
    for (auto id : model_->allNodeIds())
        if (!previous.contains(id))
            crop = id;
    auto before = model_->business(crop)["parameters"].toObject();
    auto after = before;
    after["x"] = 0;
    after["y"] = 0;
    after["width"] = sourceSize.width();
    after["height"] = sourceSize.height();
    undo.push(new ParametersCommand(model_, crop, before, after));
    undo.push(new QtNodes::DisconnectCommand(scene_, edge));
    undo.push(new QtNodes::ConnectCommand(scene_, {parent, edge.outPortIndex, crop, 0}));
    undo.push(new QtNodes::ConnectCommand(scene_, {crop, 0, destination, edge.inPortIndex}));
    undo.endMacro();
    scene_->clearSelection();
    scene_->nodeGraphicsObject(crop)->setSelected(true);
    view_->zoomFitAll();
    log_->appendPlainText("已插入矩形 ROI · 在图像上框选，拖动位置或边角调整尺寸。");
    return crop;
}
void StudioWindow::prepareRoi(bool draw) {
    if (original_.isNull() || !model_->nodeExists(selected_) ||
        model_->business(selected_)["type"] != "CropRect")
        return;
    try {
        QList<NodeId> ancestors;
        auto current = selected_;
        QSet<NodeId> seen;
        while (true) {
            NodeId parent = QtNodes::InvalidNodeId;
            for (const auto &e : model_->allConnectionIds(current))
                if (e.inNodeId == current) {
                    parent = e.outNodeId;
                    break;
                }
            if (parent == QtNodes::InvalidNodeId)
                break;
            if (seen.contains(parent))
                throw std::runtime_error("ROI 上游出现环路。");
            seen.insert(parent);
            ancestors.prepend(parent);
            current = parent;
        }
        QImage image = original_;
        QPoint origin;
        for (auto id : ancestors)
            if (model_->business(id)["type"] == "CropRect") {
                auto p = model_->business(id)["parameters"].toObject();
                QRect rect(p["x"].toInt(), p["y"].toInt(), p["width"].toInt(), p["height"].toInt());
                if (rect.isEmpty() || !image.rect().contains(rect))
                    throw std::runtime_error("上游 ROI 越界，请先修正上游参数。");
                image = image.copy(rect);
                origin += rect.topLeft();
            }
        if (result_.value && !ancestors.isEmpty() &&
            outcome_.value("debugValues").toObject().contains(model_->stableId(ancestors.last()))) {
            jpv_handle pointer = nullptr;
            api_->check(api_->result_frame(
                result_.value, model_->stableId(ancestors.last()).toUtf8().constData(), &pointer));
            OwnedHandle frame(pointer, api_->frame_release);
            jpv_image_info info{};
            image = api_->image(frame.value, &info);
            origin = {info.origin_x, info.origin_y};
        }
        imageView_->present(image, {}, origin);
        auto p = model_->business(selected_)["parameters"].toObject();
        imageView_->setRoiRect(
            QRect(p["x"].toInt(), p["y"].toInt(), p["width"].toInt(), p["height"].toInt()));
        for (const auto &name : {QString("x"), QString("y"), QString("width"), QString("height")})
            if (auto control = inspector_->findChild<QSpinBox *>("Param_" + name)) {
                QSignalBlocker blocker(control);
                control->setValue(p[name].toInt());
            }
        if (draw)
            imageView_->fit();
        imageView_->setRoiMode(draw);
    } catch (const std::exception &e) {
        roiAction_->setChecked(false);
        imageView_->setRoiMode(false);
        showError(e);
    }
}
void StudioWindow::writeRepro(const QString &path) {
    if (outcome_.isEmpty() || original_.isNull() || !snapshot_)
        throw std::runtime_error("请先执行一次检测。");
    if (QFileInfo::exists(path))
        throw std::runtime_error("请选择不存在的新目录，避免覆盖。");
    if (!QDir().mkpath(path + "/recipe.vrecipe"))
        throw std::runtime_error("无法创建复现目录。");
    auto recipe = readJson(snapshot_->path() + "/recipe.json");
    if (recipe["contentHash"] != outcome_["contentHash"])
        throw std::runtime_error("执行快照与结果不匹配。");
    copyResources(snapshot_->path(), path + "/recipe.vrecipe", recipe);
    writeJson(path + "/recipe.vrecipe/recipe.json", recipe);
    writeJson(path + "/recipe.vrecipe/layout.json", executedLayout_);
    writeJson(path + "/run.json", outcome_);
    writeJson(path + "/request.json", runRequest_);
    if (!original_.save(path + "/input.png"))
        throw std::runtime_error("输入图像导出失败。");
    log_->appendPlainText("复现包已导出 · " + path);
}
void StudioWindow::exportRepro() {
    auto path = QFileDialog::getSaveFileName(this, "导出复现包", "inspection-repro", "复现目录 (*)");
    if (path.isEmpty())
        return;
    try {
        writeRepro(path);
    } catch (const std::exception &e) {
        showError(e);
    }
}
void StudioWindow::capture() {
    if (screenshot_.isEmpty())
        return;
    QDir().mkpath(QFileInfo(screenshot_).absolutePath());
    grab().save(screenshot_);
    if (!outcome_.isEmpty())
        writeJson(screenshot_ + ".json", outcome_);
}
void StudioWindow::closeEvent(QCloseEvent *event) {
    if (busy_) {
        closing_ = true;
        api_->run_cancel(run_.value);
        event->ignore();
        resultLabel_->setText("取消并等待安全收尾…");
        return;
    }
    if (!confirmDiscard()) {
        closing_ = false;
        event->ignore();
        return;
    }
    QSettings settings;
    settings.setValue("geometry", saveGeometry());
    settings.setValue("docks", saveState());
    event->accept();
}

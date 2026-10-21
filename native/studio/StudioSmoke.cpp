#include "StudioWindow.h"
#include <QApplication>
#include <QTest>
#include <QMouseEvent>
#include <QFile>
#include <QJsonDocument>
#include <QDebug>
#include <QPainter>
#include <QSignalBlocker>
#include <QUndoStack>
#include <QtNodes/internal/NodeGraphicsObject.hpp>
#include <QtNodes/internal/AbstractNodeGeometry.hpp>
#include <QDoubleSpinBox>
using QtNodes::NodeId;
static QJsonObject readSmokeJson(const QString &path) {
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly))
        throw std::runtime_error("Smoke output missing.");
    return QJsonDocument::fromJson(file.readAll()).object();
}
void StudioWindow::draftSmokeTest() {
    auto require = [](bool condition, const char *message) {
        if (!condition)
            throw std::runtime_error(message);
    };
    newRecipe();
    addNode("ExternalImage", QPointF(60, 100));
    addNode("ToGray", QPointF(320, 100));
    NodeId source = QtNodes::InvalidNodeId, gray = QtNodes::InvalidNodeId;
    for (auto id : model_->allNodeIds()) {
        auto type = model_->business(id).value("type").toString();
        if (type == "ExternalImage")
            source = id;
        if (type == "ToGray")
            gray = id;
    }
    require(source != QtNodes::InvalidNodeId && gray != QtNodes::InvalidNodeId, "草稿图像处理节点创建失败。");
    view_->zoomFitAll();
    QApplication::processEvents();
    auto port = [&](NodeId id, QtNodes::PortType type) {
        auto item = scene_->nodeGraphicsObject(id);
        return view_->mapFromScene(item->mapToScene(scene_->nodeGeometry().portPosition(id, type, 0)));
    };
    auto from = port(source, QtNodes::PortType::Out), to = port(gray, QtNodes::PortType::In);
    QTest::mousePress(view_->viewport(), Qt::LeftButton, Qt::NoModifier, from);
    QMouseEvent movement(QEvent::MouseMove, QPointF(to), QPointF(view_->viewport()->mapToGlobal(to)),
                         Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
    QApplication::sendEvent(view_->viewport(), &movement);
    QTest::mouseRelease(view_->viewport(), Qt::LeftButton, Qt::NoModifier, to);
    require(model_->connectionExists({source, 0, gray, 0}), "草稿图像处理端口连接失败。");

    // Match the 5120 x 5120 image size in the reported failure.
    QImage image(5120, 5120, QImage::Format_BGR888);
    image.fill(QColor(40, 120, 200));
    {
        QPainter painter(&image);
        painter.setPen(QPen(QColor(220, 180, 50), 250));
        painter.drawEllipse(QRect(900, 900, 3320, 3320));
    }
    auto imagePath = QFileInfo(screenshot_).absolutePath() + "/draft-processing-input.png";
    require(image.save(imagePath), "调试图像保存失败。");
    openImage(imagePath);
    auto stable = model_->stableId(gray);
    directory_ = QFileInfo(screenshot_).absolutePath() + "/editor-built-draft.vrecipe";
    saveRecipe(false);
    openRecipe(directory_);
    selected_ = model_->visualId(stable);
    inspect(selected_);
    scene_->nodeGraphicsObject(selected_)->setSelected(true);
    auto before = model_->recipe();
    validate();
    require(resultLabel_->text().startsWith("校验通过"), "未配置公开输出的草稿无法校验。");
    require(model_->recipe() == before, "调试校验不应修改保存的公开接口。");
    debugImages_ = !QCoreApplication::arguments().contains("--no-debug");
    run();
}
void StudioWindow::smokeTest() {
    try {
        if (QCoreApplication::arguments().contains("--measurement-smoke")) {
            auto require = [](bool condition, const char *message) {
                if (!condition)
                    throw std::runtime_error(message);
            };
            selected_ = model_->visualId("detect");
            auto stable = model_->stableId(selected_);
            require(model_->business(selected_)["type"] == "FindCircles", "测量检查需要圆查找示例。");
            inspect(selected_);
            require(model_->descriptor("FindCircles")["outputs"].toArray().size() == 6, "集成测量端口缺失。");
            auto detector = selected_;
            addNode("CompareNumber", {630, 75});
            NodeId judge = QtNodes::InvalidNodeId;
            for (auto id : model_->allNodeIds())
                if (model_->business(id)["type"] == "CompareNumber")
                    judge = id;
            require(judge != QtNodes::InvalidNodeId, "数量分支节点创建失败。");
            view_->zoomFitAll();
            QApplication::processEvents();
            auto port = [&](NodeId node, QtNodes::PortType direction, int index) {
                auto object = scene_->nodeGraphicsObject(node);
                auto point = scene_->nodeGeometry().portPosition(node, direction, index);
                require(scene_->nodeGeometry().boundingRect(node).contains(point),
                        "多输出端口超出节点边界。");
                return view_->mapFromScene(object->mapToScene(point));
            };
            auto from = port(detector, QtNodes::PortType::Out, 2), to = port(judge, QtNodes::PortType::In, 0);
            QTest::mousePress(view_->viewport(), Qt::LeftButton, Qt::NoModifier, from);
            QMouseEvent move(QEvent::MouseMove, QPointF(to), QPointF(view_->viewport()->mapToGlobal(to)),
                             Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view_->viewport(), &move);
            QTest::mouseRelease(view_->viewport(), Qt::LeftButton, Qt::NoModifier, to);
            require(model_->connectionExists({detector, 2, judge, 0}), "数量输出端口不能通过鼠标连接。");
            scene_->undoStack().undo();
            scene_->undoStack().undo();
            require(model_->allNodeIds().size() == 2 && !model_->nodeExists(judge),
                    "多输出连接撤销未恢复示例。");
            selected_ = detector;
            inspect(selected_);
            if (QCoreApplication::arguments().contains("--edit-calipers")) {
                auto method = inspector_->findChild<QComboBox *>("Param_method");
                require(method, "圆查找模式控件缺失。");
                method->setCurrentIndex(method->findData("automatic"));
                QApplication::processEvents();
                require(!inspector_->findChild<QSpinBox *>("Param_caliperCount") &&
                            inspector_->findChild<QSpinBox *>("Param_minimumRadius"),
                        "自动找圆模式没有切换参数区。");
                method = inspector_->findChild<QComboBox *>("Param_method");
                method->setCurrentIndex(method->findData("caliper"));
                QApplication::processEvents();
                auto setInteger = [&](const char *name, int value) {
                    auto widget = inspector_->findChild<QSpinBox *>("Param_" + QString(name));
                    require(widget, "卡尺整数参数控件缺失。");
                    widget->setValue(value);
                };
                setInteger("caliperCount", 52);
                setInteger("projectionWidth", 9);
                setInteger("removePointCount", 6);
                auto height = inspector_->findChild<QDoubleSpinBox *>("Param_searchLength");
                require(height, "卡尺高度参数控件缺失。");
                height->setValue(48);
                require(caliperGuides(selected_, original_.size(), {}).size() == 104,
                        "可视卡尺数量与参数不同步。");
                directory_ = QFileInfo(screenshot_).absolutePath() + "/editor-built-measurement.vrecipe";
                saveRecipe(false);
                openRecipe(directory_);
                selected_ = model_->visualId(stable);
                inspect(selected_);
                auto p = model_->business(selected_)["parameters"].toObject();
                require(p["caliperCount"] == 52 && p["projectionWidth"] == 9 && p["removePointCount"] == 6 &&
                            p["searchLength"] == 48,
                        "卡尺参数保存重载丢失。");
            }
            scene_->nodeGraphicsObject(selected_)->setSelected(true);
            view_->zoomFitAll();
            debugImages_ = !QCoreApplication::arguments().contains("--no-debug");
            validate();
            run();
            return;
        }
        if (QCoreApplication::arguments().contains("--processing-smoke")) {
            selected_ = model_->visualId("gray");
            inspect(selected_);
            validate();
            run();
            return;
        }
        if (QCoreApplication::arguments().contains("--draft-smoke")) {
            draftSmokeTest();
            return;
        }
        if (QCoreApplication::arguments().contains("--template-smoke")) {
            templateSmokeTest();
            return;
        }
        if (QCoreApplication::arguments().contains("--ui-smoke")) {
            uiSmokeTest();
            return;
        }
        if (QCoreApplication::arguments().contains("--sample-smoke")) {
            selected_ = model_->visualId("detect");
            inspect(selected_);
            validate();
            run();
            return;
        }
        auto require = [](bool condition, const char *message) {
            if (!condition)
                throw std::runtime_error(message);
        };
        require(model_->allNodeIds().size() == 8, "示例图节点数不正确。");
        auto fixture = model_->recipe(), positions = model_->layout().value("positions").toObject();
        // Exercise the real viewport drag/drop handler, then real port mouse events.
        QMimeData mime;
        mime.setData("application/x-jpvision-node", "GaussianBlur");
        auto dropPoint = view_->mapFromScene(QPointF(30, 130));
        QDragEnterEvent enter(QPointF(dropPoint), Qt::CopyAction, &mime, Qt::LeftButton, Qt::NoModifier);
        QApplication::sendEvent(view_->viewport(), &enter);
        QDropEvent drop(dropPoint, Qt::CopyAction, &mime, Qt::LeftButton, Qt::NoModifier);
        QApplication::sendEvent(view_->viewport(), &drop);
        require(model_->allNodeIds().size() == 9, "拖入节点失败。");
        NodeId blur = QtNodes::InvalidNodeId;
        for (auto id : model_->allNodeIds())
            if (model_->business(id)["type"] == "GaussianBlur")
                blur = id;
        require(blur != QtNodes::InvalidNodeId, "未找到新增节点。");
        view_->zoomFitAll();
        QApplication::processEvents();
        auto source = model_->visualId("source");
        QtNodes::ConnectionId edge{source, 0, blur, 0};
        auto port = [&](NodeId id, QtNodes::PortType type) {
            auto item = scene_->nodeGraphicsObject(id);
            return view_->mapFromScene(item->mapToScene(scene_->nodeGeometry().portPosition(id, type, 0)));
        };
        auto from = port(source, QtNodes::PortType::Out), to = port(blur, QtNodes::PortType::In);
        QTest::mousePress(view_->viewport(), Qt::LeftButton, Qt::NoModifier, from);
        QMouseEvent move(QEvent::MouseMove, QPointF(to), QPointF(view_->viewport()->mapToGlobal(to)),
                         Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
        QApplication::sendEvent(view_->viewport(), &move);
        QTest::mouseRelease(view_->viewport(), Qt::LeftButton, Qt::NoModifier, to);
        require(model_->connectionExists(edge), "鼠标连接端口失败。");
        require(!model_->connectionPossible({model_->visualId("threshold"), 0, blur, 0}),
                "非法类型连接应被拒绝。");
        scene_->undoStack().undo();
        require(!model_->connectionExists(edge), "连线撤销失败。");
        scene_->undoStack().redo();
        require(model_->connectionExists(edge), "连线重做失败。");
        scene_->clearSelection();
        scene_->nodeGraphicsObject(blur)->setSelected(true);
        view_->deleteSelectionAction()->trigger();
        require(!model_->nodeExists(blur), "删除节点失败。");
        scene_->undoStack().undo();
        require(model_->nodeExists(blur) && model_->connectionExists(edge), "删除节点及连线撤销失败。");
        // ROI drag uses the displayed upstream image, then writes undoable parameters.
        addNode("CropRect", QPointF(30, 360));
        NodeId crop = QtNodes::InvalidNodeId;
        for (auto id : model_->allNodeIds())
            if (model_->business(id)["type"] == "CropRect")
                crop = id;
        selected_ = crop;
        roiAction_->setChecked(true);
        roi();
        auto start = imageView_->mapFromScene(QPointF(50, 40)),
             finish = imageView_->mapFromScene(QPointF(250, 200));
        QTest::mousePress(imageView_->viewport(), Qt::LeftButton, Qt::NoModifier, start);
        QMouseEvent roiMove(QEvent::MouseMove, QPointF(finish),
                            QPointF(imageView_->viewport()->mapToGlobal(finish)), Qt::NoButton,
                            Qt::LeftButton, Qt::NoModifier);
        QApplication::sendEvent(imageView_->viewport(), &roiMove);
        QTest::mouseRelease(imageView_->viewport(), Qt::LeftButton, Qt::NoModifier, finish);
        auto rect = model_->business(crop).value("parameters").toObject();
        require(std::abs(rect.value("x").toInt() - 50) <= 5 && std::abs(rect.value("y").toInt() - 40) <= 5 &&
                    rect.value("width").toInt() > 180,
                "ROI 鼠标框选参数失败。");
        scene_->undoStack().undo();
        require(model_->business(crop).value("parameters").toObject().value("x").toInt() == 0,
                "ROI 撤销失败。");
        QTemporaryDir saved;
        directory_ = saved.path() + "/draft.vrecipe";
        saveRecipe(false);
        auto version = readSmokeJson(directory_ + "/recipe.json")["recipeVersion"].toInt();
        saveRecipe(false);
        require(readSmokeJson(directory_ + "/recipe.json")["recipeVersion"].toInt() == version,
                "重复保存不能再次递增版本。");
        // Build a fresh graph entirely through viewport drop and port mouse events.
        newRecipe();
        view_->fitInView(QRectF(-60, -50, 1300, 600), Qt::KeepAspectRatio);
        QMap<QString, NodeId> ids;
        for (auto value : fixture.value("nodes").toArray()) {
            auto node = value.toObject();
            auto p = positions.value(node.value("id").toString()).toObject();
            auto point = view_->mapFromScene(QPointF(p.value("x").toDouble(), p.value("y").toDouble()));
            auto previous = model_->allNodeIds();
            QMimeData item;
            item.setData("application/x-jpvision-node", node.value("type").toString().toUtf8());
            QDragEnterEvent drag(QPointF(point), Qt::CopyAction, &item, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view_->viewport(), &drag);
            QDropEvent event(point, Qt::CopyAction, &item, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view_->viewport(), &event);
            NodeId added = QtNodes::InvalidNodeId;
            for (auto id : model_->allNodeIds())
                if (!previous.contains(id))
                    added = id;
            require(added != QtNodes::InvalidNodeId, "空白画布拖入节点失败。");
            ids[node.value("id").toString()] = added;
            model_->setParameters(added, node.value("parameters").toObject());
        }
        view_->zoomFitAll();
        QApplication::processEvents();
        for (auto value : fixture.value("connections").toArray()) {
            auto e = value.toObject();
            auto a = ids.value(e.value("fromNode").toString()), b = ids.value(e.value("toNode").toString());
            auto begin = port(a, QtNodes::PortType::Out), end = port(b, QtNodes::PortType::In);
            QTest::mousePress(view_->viewport(), Qt::LeftButton, Qt::NoModifier, begin);
            QMouseEvent movement(QEvent::MouseMove, QPointF(end),
                                 QPointF(view_->viewport()->mapToGlobal(end)), Qt::NoButton, Qt::LeftButton,
                                 Qt::NoModifier);
            QApplication::sendEvent(view_->viewport(), &movement);
            QTest::mouseRelease(view_->viewport(), Qt::LeftButton, Qt::NoModifier, end);
            require(model_->connectionExists({a, 0, b, 0}), "空白检测链鼠标连线失败。");
        }
        auto outputs = fixture.value("outputs").toObject();
        for (auto it = outputs.begin(); it != outputs.end(); ++it) {
            auto output = it.value().toObject();
            output["node"] = model_->stableId(ids.value(output.value("node").toString()));
            it.value() = output;
        }
        model_->setInterfaces(fixture.value("inputs").toObject(), outputs,
                              fixture.value("decisionOutput").toString());
        directory_ = QFileInfo(screenshot_).absolutePath() + "/editor-built-" +
                     QFileInfo(screenshot_).completeBaseName() + ".vrecipe";
        saveRecipe(false);
        openRecipe(directory_);
        view_->zoomFitAll();
        selected_ = ids.value("filter");
        inspect(selected_);
        imageView_->fit();
        validate();
        run();
    } catch (const std::exception &e) {
        qWarning().noquote() << "Studio smoke failed:" << e.what();
        showError(e);
        QFile diagnostic(screenshot_ + ".error.txt");
        if (diagnostic.open(QIODevice::WriteOnly))
            diagnostic.write(e.what());
        capture();
        QApplication::exit(1);
    }
}

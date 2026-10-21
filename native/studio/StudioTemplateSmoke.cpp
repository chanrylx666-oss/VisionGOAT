#include "StudioWindow.h"
#include "RegionEditor.h"
#include <QApplication>
#include <QDialog>
#include <QDialogButtonBox>
#include <QPushButton>
#include <QMouseEvent>
#include <QTest>
#include <QTimer>
#include <QUndoStack>

void StudioWindow::templateSmokeTest() {
    auto require = [](bool condition, const char *message) {
        if (!condition)
            throw std::runtime_error(message);
    };
    auto detect = model_->visualId("detect");
    require(model_->business(detect)["type"] == "TemplateMatch", "模板教学检查需要模板匹配示例。");
    bool sourceRoi = QCoreApplication::arguments().contains("--source-roi");
    if (sourceRoi) {
        auto recipe = model_->recipe();
        auto nodes = recipe["nodes"].toArray();
        nodes.append(QJsonObject{
            {"id", "teachingSource"},
            {"type", "ToGray"},
            {"version", 1},
            {"parameters",
             QJsonObject{
                 {"roi", QJsonObject{{"shape", "circle"}, {"cx", 120}, {"cy", 180}, {"radius", 70}}}}}});
        auto edges = recipe["connections"].toArray();
        for (int i = 0; i < edges.size(); ++i) {
            auto edge = edges[i].toObject();
            if (edge["toNode"] == "detect") {
                edge["fromNode"] = "teachingSource";
                edges[i] = edge;
            }
        }
        edges.append(QJsonObject{{"fromNode", "source"},
                                 {"fromPort", "image"},
                                 {"toNode", "teachingSource"},
                                 {"toPort", "input"}});
        recipe["nodes"] = nodes;
        recipe["connections"] = edges;
        auto inputs = recipe["inputs"].toObject();
        auto expected = inputs["ExpectedCount"].toObject();
        expected["default"] = 1;
        inputs["ExpectedCount"] = expected;
        recipe["inputs"] = inputs;
        model_->load(recipe, model_->layout());
        detect = model_->visualId("detect");
        expected_->setValue(1);
    }
    selected_ = detect;
    changeParameter(detect, "model", QJsonObject());
    inspect(detect);
    QString teachingError;
    QTimer::singleShot(150, this, [&] {
        auto dialog = qobject_cast<QDialog *>(QApplication::activeModalWidget());
        try {
            require(dialog, "模板教学窗口没有打开。");
            auto view = dialog->findChild<ImageView *>();
            require(view, "教学图像控件缺失。");
            view->drawRegion("rectangle");
            auto a = view->mapFromScene(sourceRoi ? QPointF(10, 10) : QPointF(60, 120)),
                 b = view->mapFromScene(sourceRoi ? QPointF(130, 130) : QPointF(180, 240));
            QTest::mousePress(view->viewport(), Qt::LeftButton, Qt::NoModifier, a);
            QMouseEvent movement(QEvent::MouseMove, QPointF(b), QPointF(view->viewport()->mapToGlobal(b)),
                                 Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view->viewport(), &movement);
            QTest::mouseRelease(view->viewport(), Qt::LeftButton, Qt::NoModifier, b);
            QApplication::processEvents();
            require(view->region()["width"].toInt() > 110, "模板教学区域框选失败。");
            dialog->grab().save(QFileInfo(screenshot_).absolutePath() + "/template-teaching.png");
            auto buttons = dialog->findChild<QDialogButtonBox *>();
            require(buttons, "模板学习按钮缺失。");
            QTest::mouseClick(buttons->button(QDialogButtonBox::Ok), Qt::LeftButton);
            if (dialog->isVisible()) {
                teachingError = "模板学习没有完成。";
                dialog->reject();
            }
        } catch (const std::exception &e) {
            teachingError = QString::fromUtf8(e.what());
            if (dialog)
                dialog->reject();
        }
    });
    auto teach = inspector_->findChild<QPushButton *>("TeachTemplate");
    require(teach, "模板教学入口缺失。");
    QTest::mouseClick(teach, Qt::LeftButton);
    if (!teachingError.isEmpty())
        throw std::runtime_error(teachingError.toStdString());
    auto trained = model_->business(detect)["parameters"].toObject()["model"].toObject();
    require(trained["width"].toInt() > 110 && trained["grayBase64"].toString().size() > 10000,
            "学习后的模板未写入模块。");
    scene_->undoStack().undo();
    require(model_->business(detect)["parameters"].toObject()["model"].toObject().isEmpty(),
            "模板学习不能撤销。");
    scene_->undoStack().redo();
    directory_ = QFileInfo(screenshot_).absolutePath() + "/editor-built-template.vrecipe";
    saveRecipe(false);
    openRecipe(directory_);
    selected_ = model_->visualId("detect");
    inspect(selected_);
    require(model_->business(selected_)["parameters"].toObject()["model"].toObject() == trained,
            "模板保存后重新加载不一致。");
    validate();
    run();
}

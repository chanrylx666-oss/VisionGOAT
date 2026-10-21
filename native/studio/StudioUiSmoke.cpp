#include "StudioWindow.h"
#include "RegionEditor.h"
#include "RegionGeometry.h"
#include <QDoubleSpinBox>
#include <QApplication>
#include <QMouseEvent>
#include <QTest>
#include <QUndoStack>
#include <QtNodes/internal/NodeGraphicsObject.hpp>

void StudioWindow::uiSmokeTest() {
    auto require = [](bool condition, const char *message) {
        if (!condition)
            throw std::runtime_error(message);
    };
    auto detect = model_->visualId("detect");
    require(model_->nodeExists(detect) && model_->business(detect)["type"] == "FindCircles",
            "UI 检查需要圆查找示例。");
    selected_ = detect;
    inspect(detect);
    require(outcome_.isEmpty(), "尚未执行时，界面读取产生了空的运行结果。");
    auto radius = inspector_->findChild<QSpinBox *>("Param_minimumRadius");
    require(radius, "圆查找参数控件缺失。");
    radius->setValue(45);
    require(model_->business(detect)["parameters"].toObject()["minimumRadius"].toInt() == 45,
            "参数修改未写入组件。");

    auto previous = model_->allNodeIds().size();
    roiAction_->setChecked(true);
    roi();
    auto crop = detect;
    require(selected_ == detect && model_->allNodeIds().size() == previous &&
                model_->business(detect)["parameters"].toObject()["roi"].toObject()["shape"] == "rectangle",
            "模块内 ROI 不应新增或改接节点。");
    scene_->undoStack().undo();
    require(model_->allNodeIds().size() == previous &&
                model_->business(detect)["parameters"].toObject()["roi"].toObject()["shape"].toString(
                    "none") == "none",
            "模块 ROI 参数不能撤销。");
    scene_->undoStack().redo();
    require(model_->business(detect)["parameters"].toObject()["roi"].toObject()["shape"] == "rectangle",
            "模块 ROI 不能重做。");
    selected_ = crop;
    inspect(crop);
    prepareModuleRoi(true);
    QApplication::processEvents();
    auto drag = [&](QPointF from, QPointF to) {
        auto begin = imageView_->mapFromScene(from), end = imageView_->mapFromScene(to);
        QTest::mousePress(imageView_->viewport(), Qt::LeftButton, Qt::NoModifier, begin);
        QMouseEvent movement(QEvent::MouseMove, QPointF(end),
                             QPointF(imageView_->viewport()->mapToGlobal(end)), Qt::NoButton, Qt::LeftButton,
                             Qt::NoModifier);
        QApplication::sendEvent(imageView_->viewport(), &movement);
        QTest::mouseRelease(imageView_->viewport(), Qt::LeftButton, Qt::NoModifier, end);
        QApplication::processEvents();
    };
    drag({60, 100}, {390, 260});
    auto drawn = RegionGeometry::pixelBounds(imageView_->region());
    require(std::abs(drawn.x() - 60) <= 2 && std::abs(drawn.width() - 330) <= 3, "ROI 框选没有同步。");
    drag(drawn.center(), drawn.center() + QPoint(5, 10));
    auto moved = RegionGeometry::pixelBounds(imageView_->region());
    require(moved.x() > drawn.x() && moved.y() > drawn.y() && moved.size() == drawn.size(),
            "ROI 内部拖动没有移动矩形。");
    auto corner = QPointF(moved.x() + moved.width(), moved.y() + moved.height());
    drag(corner, corner + QPointF(20, -10));
    auto resized = RegionGeometry::pixelBounds(imageView_->region());
    require(resized.width() > moved.width() && resized.height() < moved.height(),
            "ROI 边角拖动没有改变尺寸。");
    auto parameters = model_->business(crop)["parameters"].toObject()["roi"].toObject();
    require(parameters["x"].toInt() == resized.x() && parameters["y"].toInt() == resized.y() &&
                parameters["width"].toInt() == resized.width() &&
                parameters["height"].toInt() == resized.height(),
            "ROI 图形和配方参数不一致。");
    auto x = inspector_->findChild<QDoubleSpinBox *>("Roi_x");
    require(x && x->value() == resized.x(), "ROI 图形和参数面板不一致。");
    x->setValue(64);
    require(RegionGeometry::pixelBounds(imageView_->region()).x() == 64, "输入 ROI 坐标没有更新图形。");
    scene_->undoStack().undo();
    require(RegionGeometry::pixelBounds(imageView_->region()) == resized, "ROI 参数撤销没有恢复图形。");
    scene_->undoStack().redo();
    require(RegionGeometry::pixelBounds(imageView_->region()).x() == 64, "ROI 参数重做没有恢复图形。");
    auto edited = RegionGeometry::pixelBounds(imageView_->region());
    x = inspector_->findChild<QDoubleSpinBox *>("Roi_x");
    x->setValue(1000);
    auto width = inspector_->findChild<QDoubleSpinBox *>("Roi_width");
    require(RegionGeometry::pixelBounds(imageView_->region()).x() == original_.width() - 1 && width &&
                width->value() == 1 && x->value() == original_.width() - 1,
            "ROI 越界输入没有同步修正图形与所有参数控件。");
    scene_->undoStack().undo();
    require(RegionGeometry::pixelBounds(imageView_->region()) == edited, "ROI 越界参数撤销失败。");

    auto shape = inspector_->findChild<QComboBox *>("RoiShape");
    shape->setCurrentIndex(shape->findData("circle"));
    prepareModuleRoi(true);
    drag({120, 180}, {190, 180});
    require(imageView_->region()["shape"] == "circle" &&
                std::abs(imageView_->region()["radius"].toDouble() - 70) <= 2,
            "圆形 ROI 框选失败。");
    drag({120, 180}, {130, 185});
    require(imageView_->region()["cx"].toDouble() > 125, "圆形 ROI 移动失败。");
    auto circle = imageView_->region();
    drag({circle["cx"].toDouble() + circle["radius"].toDouble(), circle["cy"].toDouble()},
         {circle["cx"].toDouble() + circle["radius"].toDouble() + 10, circle["cy"].toDouble()});
    require(imageView_->region()["radius"].toDouble() > circle["radius"].toDouble(),
            "圆形 ROI 半径控制点失败。");
    shape = inspector_->findChild<QComboBox *>("RoiShape");
    shape->setCurrentIndex(shape->findData("polygon"));
    prepareModuleRoi(true);
    for (QPointF p : {QPointF(60, 100), QPointF(390, 100), QPointF(420, 270), QPointF(60, 270)})
        QTest::mouseClick(imageView_->viewport(), Qt::LeftButton, Qt::NoModifier,
                          imageView_->mapFromScene(p));
    QTest::keyClick(imageView_, Qt::Key_Return);
    QApplication::processEvents();
    require(imageView_->region()["shape"] == "polygon" &&
                imageView_->region()["points"].toArray().size() == 4,
            "多边形 ROI 点击顶点完成失败。");

    auto interfaces = model_->interfaces();
    auto inputs = interfaces["inputs"].toObject();
    auto expected = inputs["ExpectedCount"].toObject();
    expected["default"] = 2;
    inputs["ExpectedCount"] = expected;
    model_->setInterfaces(inputs, interfaces["outputs"].toObject(), interfaces["decisionOutput"].toString());
    expected_->setValue(2);
    auto stableCrop = model_->stableId(crop);
    directory_ = QFileInfo(screenshot_).absolutePath() + "/editor-built-ui-roi.vrecipe";
    saveRecipe(false);
    openRecipe(directory_);
    selected_ = model_->visualId(stableCrop);
    inspect(selected_);
    prepareModuleRoi();
    require(model_->business(model_->visualId("detect"))["parameters"].toObject()["minimumRadius"].toInt() ==
                    45 &&
                imageView_->region()["shape"] == "polygon" && expected_->value() == 2,
            "配方保存后重新加载没有恢复参数和 ROI。");
    view_->zoomFitAll();
    if (QCoreApplication::arguments().contains("--no-debug"))
        debugImages_ = false;
    validate();
    run();
}

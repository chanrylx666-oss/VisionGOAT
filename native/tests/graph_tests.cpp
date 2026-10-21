#include "GraphModel.h"
#include "NativeApi.h"
#include "InterfaceDialog.h"
#include "ImageView.h"
#include "RegionGeometry.h"
#include <QFile>
#include <QGraphicsEllipseItem>
#include <QGraphicsPathItem>
#include <QSignalSpy>
#include <QDialogButtonBox>
#include <QPushButton>
#include <QTest>

class GraphTests : public QObject {
    Q_OBJECT
    std::unique_ptr<NativeApi> api;
    QJsonArray catalog;
    QJsonObject recipe;
  private Q_SLOTS:
    void initTestCase() {
        api = std::make_unique<NativeApi>();
        catalog = api->json([&](char **p) { return api->node_catalog(p); }).array();
        QFile file(QCoreApplication::applicationDirPath() +
                   "/samples/recipes/parts-count.vrecipe/recipe.json");
        QVERIFY(file.open(QIODevice::ReadOnly));
        recipe = QJsonDocument::fromJson(file.readAll()).object();
    }
    void failedOpenPreservesDocument() {
        GraphModel model(catalog);
        model.load(recipe, {});
        auto before = model.recipe();
        auto invalid = recipe;
        auto nodes = invalid["nodes"].toArray();
        auto node = nodes[0].toObject();
        node["type"] = "Missing";
        nodes[0] = node;
        invalid["nodes"] = nodes;
        QVERIFY_EXCEPTION_THROWN(model.load(invalid, {}), std::runtime_error);
        QCOMPARE(model.recipe(), before);
    }
    void duplicateIdsRejected() {
        GraphModel model(catalog);
        auto invalid = recipe;
        auto nodes = invalid["nodes"].toArray();
        nodes.append(nodes.first());
        invalid["nodes"] = nodes;
        QVERIFY_EXCEPTION_THROWN(model.load(invalid, {}), std::runtime_error);
    }
    void incompleteDraftRoundTrips() {
        GraphModel model(catalog);
        auto draft = recipe;
        draft["outputs"] = QJsonObject();
        draft["connections"] = QJsonArray();
        model.load(draft, {});
        auto id = model.addNode("GaussianBlur");
        QVERIFY(id != QtNodes::InvalidNodeId);
        GraphModel restored(catalog);
        restored.load(model.recipe(), model.layout());
        QCOMPARE(restored.allNodeIds().size(), size_t(9));
    }
    void draftExecutionUsesTerminalOutputsWithoutChangingSavedInterfaces() {
        GraphModel model(catalog);
        auto draft = recipe;
        draft["nodes"] = QJsonArray();
        draft["connections"] = QJsonArray();
        draft["outputs"] = QJsonObject();
        model.load(draft, {});
        auto source = model.addNode("ExternalImage"), gray = model.addNode("ToGray");
        model.addConnection({source, 0, gray, 0});
        auto before = model.recipe();
        auto preview = model.executionRecipe();
        QCOMPARE(model.recipe(), before);
        QCOMPARE(preview.value("kind").toString(), QString("processing"));
        QVERIFY(preview.value("decisionOutput").toString().isEmpty());
        auto outputs = preview.value("outputs").toObject();
        QCOMPARE(outputs.size(), qsizetype(1));
        QCOMPARE(outputs.begin().value().toObject().value("node").toString(), model.stableId(gray));
        QCOMPARE(outputs.begin().value().toObject().value("type").toString(), QString("Image"));
        auto bytes = QJsonDocument(preview).toJson(QJsonDocument::Compact);
        auto sealed = api->json([&](char **p) { return api->recipe_seal(bytes.constData(), p); });
        bytes = sealed.toJson(QJsonDocument::Compact);
        jpv_handle pointer = nullptr;
        QCOMPARE(api->engine_create("{}", &pointer), 0);
        OwnedHandle engine(pointer, api->engine_release);
        auto report =
            api->json([&](char **p) { return api->recipe_validate(engine.value, bytes.constData(), p); });
        QCOMPARE(report.object().value("executionOrder").toArray().size(), qsizetype(2));
        GraphModel restored(catalog);
        restored.load(preview, model.layout());
        QCOMPARE(restored.recipe().value("outputs"), preview.value("outputs"));
    }
    void draftExecutionIncludesAllTerminalBranches() {
        GraphModel model(catalog);
        auto draft = recipe;
        draft["nodes"] = QJsonArray();
        draft["connections"] = QJsonArray();
        draft["outputs"] = QJsonObject();
        model.load(draft, {});
        auto source = model.addNode("ExternalImage"), gray = model.addNode("ToGray");
        auto blur = model.addNode("GaussianBlur");
        model.addConnection({source, 0, gray, 0});
        model.addConnection({source, 0, blur, 0});
        auto outputs = model.executionRecipe().value("outputs").toObject();
        QCOMPARE(outputs.size(), qsizetype(2));
        for (const auto &value : outputs)
            QVERIFY(value.toObject().value("node").toString() != model.stableId(source));
    }
    void configuredInspectionKeepsItsExplicitExecutionOutputs() {
        GraphModel model(catalog);
        model.load(recipe, {});
        QCOMPARE(model.executionRecipe(), model.recipe());
    }
    void replacedFlowIgnoresDanglingPublicOutputsOnlyInPreview() {
        GraphModel model(catalog);
        auto edited = recipe;
        edited["nodes"] = QJsonArray();
        edited["connections"] = QJsonArray();
        // Keep the old ObjectCount / InspectionPassed / ResultOverlay declarations.
        model.load(edited, {});
        auto source = model.addNode("ExternalImage"), gray = model.addNode("ToGray"), binary = model.addNode("Threshold");
        model.addConnection({source, 0, gray, 0}); model.addConnection({gray, 0, binary, 0});
        auto before = model.recipe();
        auto preview = model.executionRecipe();
        QCOMPARE(model.recipe(), before);
        QCOMPARE(preview["kind"].toString(), QString("processing"));
        QVERIFY(preview["decisionOutput"].toString().isEmpty());
        auto outputs = preview["outputs"].toObject();
        QCOMPARE(outputs.size(), qsizetype(1));
        QCOMPARE(outputs.begin().value().toObject()["node"].toString(), model.stableId(binary));
        QCOMPARE(outputs.begin().value().toObject()["type"].toString(), QString("Mask"));
        auto bytes = QJsonDocument(preview).toJson(QJsonDocument::Compact);
        bytes = api->json([&](char **out) { return api->recipe_seal(bytes.constData(), out); }).toJson(QJsonDocument::Compact);
        jpv_handle pointer = nullptr; QCOMPARE(api->engine_create("{}", &pointer), 0);
        OwnedHandle engine(pointer, api->engine_release);
        auto report = api->json([&](char **out) { return api->recipe_validate(engine.value, bytes.constData(), out); });
        QCOMPARE(report.object()["executionOrder"].toArray().size(), qsizetype(3));
    }
    void newlyConnectedBranchIsPreviewedWithoutReplacingValidPublicNames() {
        GraphModel model(catalog);
        model.load(recipe, {});
        auto median = model.addNode("MedianBlur");
        model.addConnection({model.visualId("gray"), 0, median, 0});
        auto before = model.recipe();
        auto preview = model.executionRecipe();
        QCOMPARE(model.recipe(), before);
        QCOMPARE(preview["outputs"], before["outputs"]);
        QCOMPARE(preview["kind"], before["kind"]);
        QVERIFY(preview["previewNodes"].toArray().contains(model.stableId(median)));
    }
    void publicInterfaceDialogCanPublishImageProcessingWithoutBool() {
        GraphModel model(catalog);
        auto draft = recipe;
        draft["nodes"] = QJsonArray();
        draft["connections"] = QJsonArray();
        draft["outputs"] = QJsonObject();
        model.load(draft, {});
        auto source = model.addNode("ExternalImage"), gray = model.addNode("ToGray");
        model.addConnection({source, 0, gray, 0});
        auto preview = model.executionRecipe();
        model.setInterfaces(preview.value("inputs").toObject(), preview.value("outputs").toObject(),
                            "InspectionPassed", "inspection");
        InterfaceDialog dialog(&model);
        auto purpose = dialog.findChild<QComboBox *>("RecipeKind");
        QVERIFY(purpose);
        purpose->setCurrentIndex(1);
        QSignalSpy accepted(&dialog, &QDialog::accepted);
        auto buttons = dialog.findChild<QDialogButtonBox *>();
        QVERIFY(buttons);
        buttons->button(QDialogButtonBox::Ok)->click();
        QCOMPARE(accepted.size(), 1);
        QVERIFY(dialog.decision().isEmpty());
        model.setInterfaces(dialog.inputs(), dialog.outputs(), dialog.decision(), dialog.kind());
        QCOMPARE(model.recipe().value("kind").toString(), QString("processing"));
        QCOMPARE(model.recipe().value("outputs").toObject().size(), qsizetype(1));
        QCOMPARE(model.executionRecipe(), model.recipe());
        GraphModel restored(catalog);
        restored.load(model.recipe(), model.layout());
        QCOMPARE(restored.recipe().value("kind").toString(), QString("processing"));
    }
    void typeAndCycleRules() {
        GraphModel model(catalog);
        model.load(recipe, {});
        auto a = model.addNode("ToGray"), b = model.addNode("ToGray");
        QVERIFY(model.connectionPossible({a, 0, b, 0}));
        model.addConnection({a, 0, b, 0});
        QVERIFY(!model.connectionPossible({b, 0, a, 0}));
        auto blur = model.addNode("GaussianBlur");
        QVERIFY(!model.connectionPossible({model.visualId("threshold"), 0, blur, 0}));
    }
    void movingCanvasDoesNotChangeBusinessHash() {
        GraphModel model(catalog);
        model.load(recipe, {});
        auto seal = [&] {
            auto bytes = QJsonDocument(model.recipe()).toJson(QJsonDocument::Compact);
            return api->json([&](char **p) { return api->recipe_seal(bytes.constData(), p); })
                .object()["contentHash"]
                .toString();
        };
        auto hash = seal();
        model.setNodeData(model.visualId("gray"), QtNodes::NodeRole::Position, QPointF(800, 900));
        QCOMPARE(seal(), hash);
        auto id = model.visualId("threshold");
        auto p = model.business(id)["parameters"].toObject();
        p["value"] = 80;
        model.setParameters(id, p);
        QVERIFY(seal() != hash);
    }
    void geometryComponentsUseCatalogPortsAndRoundTrip() {
        GraphModel model(catalog);
        auto draft = recipe;
        draft["nodes"] = QJsonArray();
        draft["connections"] = QJsonArray();
        draft["outputs"] = QJsonObject();
        model.load(draft, {});
        auto circle = model.addNode("FindCircles"), line = model.addNode("FindLines");
        auto area = model.addNode("AnalyzeContours"), count = model.addNode("CountFeatures");
        auto overlay = model.addNode("BuildFeatureOverlay"), image = model.addNode("MedianBlur");
        QVERIFY(model.connectionPossible({circle, 0, count, 0}));
        QVERIFY(model.connectionPossible({line, 0, count, 0}));
        QVERIFY(model.connectionPossible({area, 0, overlay, 0}));
        QVERIFY(!model.connectionPossible({circle, 0, image, 0}));
        model.addConnection({circle, 0, count, 0});
        GraphModel restored(catalog);
        restored.load(model.recipe(), model.layout());
        QCOMPARE(restored.allNodeIds().size(), size_t(6));
        QVERIFY(restored.connectionExists(
            {restored.visualId(model.stableId(circle)), 0, restored.visualId(model.stableId(count)), 0}));
    }
    void geometryOverlayPrimitivesRespectImageOrigin() {
        ImageView view;
        QImage image(240, 180, QImage::Format_RGB32);
        image.fill(Qt::black);
        view.present(
            image,
            QJsonArray{
                QJsonObject{{"type", "circle"}, {"x", 200}, {"y", 125}, {"radius", 30}},
                QJsonObject{{"type", "line"},
                            {"points", QJsonArray{QJsonArray{120, 110}, QJsonArray{260, 110}}}},
                QJsonObject{{"type", "rectangle"}, {"x", 140}, {"y", 90}, {"width", 20}, {"height", 20}}},
            {100, 50});
        int shapes = 0;
        for (auto item : view.scene()->items()) {
            if (auto ellipse = dynamic_cast<QGraphicsEllipseItem *>(item)) {
                QCOMPARE(ellipse->rect(), QRectF(70, 45, 60, 60));
                QVERIFY(ellipse->pen().isCosmetic());
                ++shapes;
            } else if (auto path = dynamic_cast<QGraphicsPathItem *>(item)) {
                QCOMPARE(path->path().elementCount(), 2);
                QCOMPARE(path->path().elementAt(0).x, 20.0);
                QCOMPARE(path->path().elementAt(0).y, 60.0);
                QCOMPARE(path->path().elementAt(1).x, 160.0);
                ++shapes;
            } else if (auto rect = dynamic_cast<QGraphicsRectItem *>(item)) {
                QCOMPARE(rect->rect(), QRectF(40, 40, 20, 20));
                ++shapes;
            }
        }
        QCOMPARE(shapes, 3);
    }
    void existingRoiCanMoveResizeAndStayInsideImage() {
        ImageView view;
        view.resize(640, 400);
        view.show();
        QTest::qWait(20);
        QImage image(400, 240, QImage::Format_RGB32);
        image.fill(Qt::black);
        view.present(image);
        view.resetTransform();
        QSignalSpy changes(&view, &ImageView::roiSelected);
        auto drag = [&](QPointF a, QPointF b) {
            auto from = view.mapFromScene(a), to = view.mapFromScene(b);
            QTest::mousePress(view.viewport(), Qt::LeftButton, Qt::NoModifier, from);
            QMouseEvent move(QEvent::MouseMove, QPointF(to), QPointF(view.viewport()->mapToGlobal(to)),
                             Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view.viewport(), &move);
            QTest::mouseRelease(view.viewport(), Qt::LeftButton, Qt::NoModifier, to);
        };
        view.setRoiMode(true);
        drag({30, 30}, {130, 100});
        QCOMPARE(changes.size(), 1);
        changes.clear();
        drag({80, 60}, {120, 90});
        QCOMPARE(changes.size(), 1);
        auto moved = changes.takeFirst()[0].toRect();
        QCOMPARE(moved, QRect(70, 60, 100, 70));
        drag({170, 130}, {210, 150});
        QCOMPARE(changes.size(), 1);
        QCOMPARE(changes.takeFirst()[0].toRect(), QRect(70, 60, 140, 90));
        drag({120, 100}, {520, 400});
        QCOMPARE(changes.size(), 1);
        auto clamped = changes.takeFirst()[0].toRect();
        QVERIFY(image.rect().contains(clamped));
        QCOMPARE(clamped.size(), QSize(140, 90));
    }
    void movingRoiAtFractionalZoomPreservesPixelSize() {
        ImageView view;
        view.resize(640, 400);
        view.show();
        QTest::qWait(20);
        QImage image(640, 360, QImage::Format_RGB32);
        image.fill(Qt::black);
        view.present(image);
        view.setZoomPercent(54.4);
        view.setRoiRect(QRect(60, 100, 330, 160));
        QSignalSpy changes(&view, &ImageView::roiSelected);
        auto begin = view.mapFromScene(QPointF(225, 180)), end = view.mapFromScene(QPointF(230, 190));
        QTest::mousePress(view.viewport(), Qt::LeftButton, Qt::NoModifier, begin);
        QMouseEvent move(QEvent::MouseMove, QPointF(end), QPointF(view.viewport()->mapToGlobal(end)),
                         Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
        QApplication::sendEvent(view.viewport(), &move);
        QTest::mouseRelease(view.viewport(), Qt::LeftButton, Qt::NoModifier, end);
        QCOMPARE(changes.size(), 1);
        QCOMPARE(changes.first()[0].toRect().size(), QSize(330, 160));
        QCOMPARE(view.roiRect().size(), QSize(330, 160));
    }
    void moduleRegionHandlesAndPolygonCompletion() {
        ImageView view;
        view.resize(640, 400);
        view.show();
        QTest::qWait(20);
        QImage image(640, 360, QImage::Format_RGB32);
        image.fill(Qt::black);
        view.present(image);
        view.setZoomPercent(54.4);
        view.setRegion(
            QJsonObject{{"shape", "rectangle"}, {"x", 60}, {"y", 100}, {"width", 330}, {"height", 160}});
        auto drag = [&](QPointF a, QPointF b) {
            auto from = view.mapFromScene(a), to = view.mapFromScene(b);
            QTest::mousePress(view.viewport(), Qt::LeftButton, Qt::NoModifier, from);
            QMouseEvent movement(QEvent::MouseMove, QPointF(to), QPointF(view.viewport()->mapToGlobal(to)),
                                 Qt::NoButton, Qt::LeftButton, Qt::NoModifier);
            QApplication::sendEvent(view.viewport(), &movement);
            QTest::mouseRelease(view.viewport(), Qt::LeftButton, Qt::NoModifier, to);
            QApplication::processEvents();
        };
        drag({390, 260}, {410, 250});
        QVERIFY(view.region()["width"].toInt() > 340);
        QVERIFY(view.region()["height"].toInt() < 155);
        view.setRegion(QJsonObject{{"shape", "circle"}, {"cx", 180}, {"cy", 160}, {"radius", 60}});
        drag({240, 160}, {250, 160});
        QVERIFY(view.region()["radius"].toDouble() > 65);
        auto radius = view.region()["radius"].toDouble();
        drag({180, 160}, {190, 170});
        QVERIFY(view.region()["cx"].toDouble() > 185);
        QCOMPARE(view.region()["radius"].toDouble(), radius);
        view.drawRegion("polygon");
        for (auto p : {QPointF(40, 40), QPointF(300, 40), QPointF(300, 250), QPointF(40, 250)})
            QTest::mouseClick(view.viewport(), Qt::LeftButton, Qt::NoModifier, view.mapFromScene(p));
        QTest::keyClick(&view, Qt::Key_Return);
        QCOMPARE(view.region()["shape"].toString(), QString("polygon"));
        QCOMPARE(view.region()["points"].toArray().size(), qsizetype(4));
        auto before = view.region();
        view.drawRegion("polygon");
        QTest::mouseClick(view.viewport(), Qt::LeftButton, Qt::NoModifier,
                          view.mapFromScene(QPointF(60, 60)));
        QTest::keyClick(&view, Qt::Key_Escape);
        QCOMPARE(view.region(), before);
    }
};
QTEST_MAIN(GraphTests)
#include "graph_tests.moc"

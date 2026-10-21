#pragma once
#include "GraphModel.h"
#include "GraphView.h"
#include "ImageView.h"
#include "NativeApi.h"
#include "HistogramWidget.h"
#include "RunLog.h"
#include <QDockWidget>
#include <QFormLayout>
#include <QFutureWatcher>
#include <QLabel>
#include <QMainWindow>
#include <QPlainTextEdit>
#include <QSpinBox>
#include <QStackedWidget>
#include <QTemporaryDir>
#include <QComboBox>
#include <QTabWidget>
#include <QToolButton>
#include <QtNodes/BasicGraphicsScene>

struct RunOutcome {
    QJsonObject data;
    jpv_handle result = nullptr;
    QString error;
};
class StudioWindow : public QMainWindow {
    Q_OBJECT
    std::shared_ptr<NativeApi> api_;
    OwnedHandle engine_, frame_, run_, result_;
    GraphModel *model_;
    QtNodes::BasicGraphicsScene *scene_;
    GraphView *view_;
    ToolTree *tools_;
    ImageView *imageView_;
    QWidget *inspector_;
    QFormLayout *form_;
    QLabel *resultLabel_;
    QLabel *details_;
    QLabel *pixelLabel_;
    QLabel *licenseLabel_;
    QSpinBox *expected_;
    RunLog *log_;
    QStackedWidget *pages_;
    QAction *runAction_, *cancelAction_, *openAction_, *saveAction_, *newAction_, *imageAction_, *roiAction_,
        *interfaceAction_, *validateAction_;
    QFutureWatcher<RunOutcome> *watcher_;
    std::unique_ptr<QTemporaryDir> snapshot_;
    QImage original_;
    QJsonObject outcome_, executedRecipe_, executedLayout_, runRequest_;
    QtNodes::NodeId selected_ = QtNodes::InvalidNodeId;
    QString directory_;
    bool dirty_ = false, busy_ = false, closing_ = false, loading_ = false, editing_ = false, smoke_ = false;
    QString screenshot_;
    bool expectLocked_ = false;
    QList<QDockWidget *> businessDocks_;
    bool authorized_ = false;
    QList<QMenu *> businessMenus_;
    QTabWidget *parameterTabs_;
    QTreeWidget *resultTree_;
    QLabel *recipeCaption_, *previewInfo_, *imageInfo_, *readyLabel_;
    QToolButton *documentTab_;
    QComboBox *intermediate_;
    HistogramWidget *histogram_;
    QString imageName_;
    bool debugImages_ = true;
    bool measurementInvalidationVerified_ = false;
    int timeoutMs_ = 0;
    void buildUi();
    void refreshLicense();
    void exportRequest();
    void importLicense();
    void showError(const std::exception &error);
    void setEnabledState(bool authorized);
    void clearResult();
    void invalidate();
    void addNode(QString type, QPointF pos);
    void inspect(QtNodes::NodeId id);
    void changeParameter(QtNodes::NodeId id, QString name, QJsonValue value);
    void showNodeImage(QtNodes::NodeId id);
    void openRecipe(QString directory);
    void saveRecipe(bool choose);
    void openImage(QString path);
    bool confirmDiscard();
    void newRecipe();
    void configureInterfaces();
    QJsonObject sealedRecipe(bool forExecution = false) const;
    void validate();
    void run();
    void finished();
    QJsonArray resultOverlays() const;
    QJsonArray nodeOverlays(QtNodes::NodeId id) const;
    void showResultImage();
    QString resultSummary() const;
    void exportRepro();
    void roi();
    void prepareRoi(bool draw);
    QtNodes::NodeId ensureRoiNode();
    void updateCaption();
    void updateImageInfo();
    void updateNodeResult(QtNodes::NodeId id);
    void updateIntermediate();
    void configureRun();
    bool supportsModuleRoi(QtNodes::NodeId id) const;
    QImage upstreamImage(QtNodes::NodeId id, QPoint *origin = nullptr) const;
    void prepareModuleRoi(bool draw = false);
    void teachTemplate(QtNodes::NodeId id, bool importImage = false);
    QJsonObject templateModel(const QImage &image, const QJsonObject &roi,
                              const QImage &validMask = {}) const;
    QImage upstreamMask(QtNodes::NodeId id) const;
    QJsonArray caliperGuides(QtNodes::NodeId id, QSize size, QPoint origin) const;
    void smokeTest();
    void draftSmokeTest();
    void uiSmokeTest();
    void templateSmokeTest();
    void capture();
    void writeRepro(const QString &path);

  protected:
    void closeEvent(QCloseEvent *event) override;

  public:
    StudioWindow(std::shared_ptr<NativeApi> api, bool smoke = false, QString screenshot = {},
                 bool expectLocked = false);
    ~StudioWindow() override;
};

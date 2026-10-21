#include "StudioWindow.h"
#include "MeasurementUi.h"
#include "StudioTheme.h"
#include "VisionNodePainter.h"
#include <QApplication>
#include <QButtonGroup>
#include <QCheckBox>
#include <QDialog>
#include <QDialogButtonBox>
#include <QFileDialog>
#include <QFrame>
#include <QHeaderView>
#include <QHBoxLayout>
#include <QLineEdit>
#include <QMenuBar>
#include <QMessageBox>
#include <QMouseEvent>
#include <QPushButton>
#include <QPainter>
#include <QSaveFile>
#include <QScrollArea>
#include <QSignalBlocker>
#include <QSplitter>
#include <QStatusBar>
#include <QToolBar>
#include <QUndoStack>
#include <QVBoxLayout>
#include <QWindow>
#include <QtNodes/UndoCommands>

namespace {
struct Panel {
    QFrame *frame;
    QVBoxLayout *layout;
    QHBoxLayout *header;
};
Panel panel(const QString &title) {
    auto frame = new QFrame;
    frame->setObjectName("Panel");
    auto layout = new QVBoxLayout(frame);
    layout->setContentsMargins(1, 1, 1, 1);
    layout->setSpacing(0);
    auto header = new QWidget;
    header->setObjectName("PanelHeader");
    header->setFixedHeight(41);
    auto row = new QHBoxLayout(header);
    row->setContentsMargins(12, 4, 10, 4);
    row->setSpacing(8);
    auto accent = new QLabel;
    accent->setFixedSize(3, 15);
    accent->setObjectName("PanelAccent");
    row->addWidget(accent);
    auto caption = new QLabel(title);
    caption->setObjectName("SectionTitle");
    row->addWidget(caption);
    row->addStretch();
    layout->addWidget(header);
    return {frame, layout, row};
}
QToolButton *tool(const QString &name, const QString &tip) {
    auto button = new QToolButton;
    button->setIcon(StudioTheme::icon(name));
    button->setIconSize({18, 18});
    button->setToolTip(tip);
    button->setAccessibleName(tip);
    button->setAutoRaise(true);
    button->setFixedSize(31, 31);
    return button;
}
QComboBox *zoomBox() {
    auto box = new QComboBox;
    box->addItems({"25%", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%"});
    box->setEditable(true);
    box->setCurrentText("100%");
    box->setFixedWidth(82);
    box->setToolTip("缩放比例，可输入百分比");
    return box;
}
class TitleBar : public QFrame {
  protected:
    void paintEvent(QPaintEvent *event) override {
        QFrame::paintEvent(event);
        QPainter painter(this);
        painter.setRenderHint(QPainter::Antialiasing);
        const int edge = width() - 230;
        QPolygonF bevel{QPointF(edge + 48, 0), QPointF(width(), 0), QPointF(width(), height()),
                        QPointF(edge, height())};
        painter.setPen(Qt::NoPen);
        painter.setBrush(QColor(255, 48, 56, 12));
        painter.drawPolygon(bevel);
        painter.setPen(QPen(QColor(StudioTheme::Accent), 1));
        painter.drawLine(QPointF(edge + 48, 0), QPointF(edge, height() - 1));
    }
    void mousePressEvent(QMouseEvent *event) override {
        if (event->button() == Qt::LeftButton && window()->windowHandle())
            window()->windowHandle()->startSystemMove();
    }
    void mouseDoubleClickEvent(QMouseEvent *event) override {
        if (event->button() == Qt::LeftButton) {
            if (window()->isMaximized())
                window()->showNormal();
            else
                window()->showMaximized();
        }
    }
};
} // namespace
void StudioWindow::buildUi() {
    StudioTheme::apply();
    setWindowFlag(Qt::FramelessWindowHint);
    scene_->setNodeGeometry(std::make_unique<VisionNodeGeometry>(*model_));
    scene_->setNodePainter(std::make_unique<VisionNodePainter>());
    pages_ = new QStackedWidget;
    setCentralWidget(pages_);
    auto locked = new QWidget;
    auto lockedLayout = new QVBoxLayout(locked);
    lockedLayout->addStretch();
    auto lockIcon = new QLabel;
    lockIcon->setPixmap(windowIcon().pixmap(96, 96));
    lockIcon->setAlignment(Qt::AlignCenter);
    lockedLayout->addWidget(lockIcon);
    auto brand = new QLabel("JP-Vision");
    brand->setObjectName("Brand");
    brand->setAlignment(Qt::AlignCenter);
    lockedLayout->addWidget(brand);
    licenseLabel_ = new QLabel("正在检查本机授权…");
    licenseLabel_->setAlignment(Qt::AlignCenter);
    licenseLabel_->setWordWrap(true);
    lockedLayout->addWidget(licenseLabel_);
    auto activationRow = new QHBoxLayout;
    activationRow->addStretch();
    auto request = new QPushButton("导出机器请求"), activate = new QPushButton("导入许可证");
    activationRow->addWidget(request);
    activationRow->addWidget(activate);
    activationRow->addStretch();
    lockedLayout->addLayout(activationRow);
    lockedLayout->addStretch();
    pages_->addWidget(locked);
    connect(request, &QPushButton::clicked, this, &StudioWindow::exportRequest);
    connect(activate, &QPushButton::clicked, this, &StudioWindow::importLicense);
    auto editor = new QWidget;
    auto outer = new QHBoxLayout(editor);
    outer->setContentsMargins(8, 8, 8, 4);
    outer->setSpacing(8);
    auto toolbox = panel("算法工具箱");
    toolbox.frame->setFixedWidth(256);
    auto search = new QLineEdit;
    search->setPlaceholderText("搜索算子…");
    search->addAction(StudioTheme::icon("zoom"), QLineEdit::LeadingPosition);
    search->setAccessibleName("搜索算子");
    auto toolBody = new QWidget;
    auto toolLayout = new QVBoxLayout(toolBody);
    toolLayout->setContentsMargins(10, 8, 8, 6);
    toolLayout->setSpacing(6);
    toolLayout->addWidget(search);
    tools_ = new ToolTree;
    tools_->setIndentation(16);
    tools_->setIconSize({18, 18});
    toolLayout->addWidget(tools_, 1);
    auto insertTool = new QPushButton("＋  插入选中算子");
    insertTool->setObjectName("PrimaryButton");
    insertTool->setToolTip("选中一个算子后点击插入，也可拖拽或双击添加");
    toolLayout->addWidget(insertTool);
    connect(insertTool, &QPushButton::clicked, this, [this] {
        auto item = tools_->currentItem();
        if (!item)
            return;
        auto type = item->data(0, Qt::UserRole).toString();
        if (!type.isEmpty())
            addNode(type, view_->mapToScene(view_->viewport()->rect().center()));
    });
    toolbox.layout->addWidget(toolBody, 1);
    outer->addWidget(toolbox.frame);
    auto catalog = api_->json([&](char **out) { return api_->node_catalog(out); }).array();
    QMap<QString, QTreeWidgetItem *> categories;
    for (auto value : catalog) {
        auto meta = value.toObject();
        auto category = meta["category"].toString();
        if (!categories.contains(category)) {
            auto group = new QTreeWidgetItem(tools_, {category});
            group->setFlags(group->flags() & ~Qt::ItemIsDragEnabled);
            group->setForeground(0, QColor(StudioTheme::Text));
            auto font = group->font(0);
            font.setBold(true);
            group->setFont(0, font);
            categories[category] = group;
        }
        auto item = new QTreeWidgetItem(categories[category], {meta["label"].toString()});
        auto type = meta["type"].toString();
        item->setData(0, Qt::UserRole, type);
        item->setIcon(0, StudioTheme::icon(type));
        item->setToolTip(0, "拖入流程或双击添加 · " + type);
    }
    tools_->expandAll();
    connect(tools_, &QTreeWidget::itemDoubleClicked, this, [this](QTreeWidgetItem *item, int) {
        auto type = item->data(0, Qt::UserRole).toString();
        if (!type.isEmpty())
            addNode(type, view_->mapToScene(view_->viewport()->rect().center()));
    });
    connect(search, &QLineEdit::textChanged, this, [this](const QString &text) {
        for (int i = 0; i < tools_->topLevelItemCount(); ++i) {
            auto category = tools_->topLevelItem(i);
            bool visible = false;
            for (int j = 0; j < category->childCount(); ++j) {
                auto item = category->child(j);
                bool match = item->text(0).contains(text, Qt::CaseInsensitive) ||
                             item->data(0, Qt::UserRole).toString().contains(text, Qt::CaseInsensitive);
                item->setHidden(!match);
                visible |= match;
            }
            category->setHidden(!visible);
        }
    });
    auto work = new QSplitter(Qt::Vertical);
    work->setHandleWidth(8);
    outer->addWidget(work, 1);
    auto upper = new QSplitter(Qt::Horizontal);
    upper->setHandleWidth(8);
    auto visualWork = new QSplitter(Qt::Vertical);
    visualWork->setHandleWidth(8);
    upper->addWidget(visualWork);
    auto flow = panel("JP-Vision  /  流程编辑");
    flow.frame->setMinimumWidth(560);
    flow.frame->setMinimumHeight(240);
    auto title = flow.header->itemAt(1)->widget();
    title->setObjectName("FlowTitle");
    documentTab_ = new QToolButton;
    documentTab_->setObjectName("DocumentTab");
    documentTab_->setText("未命名配方");
    documentTab_->setToolTip("当前配方");
    flow.header->insertWidget(2, documentTab_);
    auto newDocument = tool("plus", "新建配方"), select = tool("select", "选择节点"),
         pan = tool("hand", "平移画布"), graphFit = tool("fit", "流程适应窗口");
    flow.header->insertWidget(3, newDocument);
    auto modes = new QButtonGroup(this);
    modes->setExclusive(true);
    for (auto button : {select, pan}) {
        button->setCheckable(true);
        modes->addButton(button);
        flow.header->addWidget(button);
    }
    select->setChecked(true);
    auto graphZoom = zoomBox();
    flow.header->addWidget(graphZoom);
    flow.header->addWidget(graphFit);
    view_ = new GraphView(scene_);
    view_->setObjectName("FlowCanvas");
    view_->setContextMenuPolicy(Qt::ActionsContextMenu);
    flow.layout->addWidget(view_, 1);
    visualWork->addWidget(flow.frame);
    connect(select, &QToolButton::clicked, this, [this] { view_->setPanMode(false); });
    connect(pan, &QToolButton::clicked, this, [this] { view_->setPanMode(true); });
    connect(graphFit, &QToolButton::clicked, view_, &GraphView::zoomFitAll);
    connect(graphZoom, &QComboBox::currentTextChanged, this, [this](QString text) {
        text.remove('%');
        bool ok = false;
        double scale = text.toDouble(&ok);
        if (ok && scale > 0)
            view_->setupScale(scale / 100);
    });
    connect(view_, &GraphView::scaleChanged, this, [graphZoom](double scale) {
        QSignalBlocker block(graphZoom);
        graphZoom->setCurrentText(QString::number(scale * 100, 'f', 0) + "%");
    });
    auto parameters = panel("参数与结果");
    parameters.frame->setMinimumWidth(280);
    parameters.frame->setMaximumWidth(420);
    parameterTabs_ = new QTabWidget;
    parameterTabs_->setDocumentMode(true);
    auto scroll = new QScrollArea;
    scroll->setWidgetResizable(true);
    inspector_ = new QWidget;
    form_ = new QFormLayout(inspector_);
    form_->setContentsMargins(14, 12, 14, 12);
    form_->setSpacing(10);
    form_->setFieldGrowthPolicy(QFormLayout::AllNonFixedFieldsGrow);
    form_->setRowWrapPolicy(QFormLayout::WrapLongRows);
    scroll->setWidget(inspector_);
    details_ = new QLabel("在流程图中选择一个组件，在此处编辑参数。");
    details_->setWordWrap(true);
    form_->addRow(details_);
    parameterTabs_->addTab(scroll, "参数设置");
    resultTree_ = new QTreeWidget;
    resultTree_->setColumnCount(2);
    resultTree_->setHeaderLabels({"项目", "数值"});
    resultTree_->header()->setSectionResizeMode(0, QHeaderView::ResizeToContents);
    resultTree_->header()->setSectionResizeMode(1, QHeaderView::Stretch);
    resultTree_->setAlternatingRowColors(true);
    parameterTabs_->addTab(resultTree_, "运行结果");
    parameters.layout->addWidget(parameterTabs_, 1);
    upper->addWidget(parameters.frame);
    upper->setStretchFactor(0, 1);
    upper->setStretchFactor(1, 0);
    upper->setSizes({1000, 280});
    work->addWidget(upper);
    auto lower = new QSplitter(Qt::Horizontal);
    lower->setHandleWidth(6);
    auto preview = panel("图像与中间结果");
    preview.frame->setMinimumHeight(200);
    auto original = new QPushButton("原图"), resultImage = new QPushButton("结果图");
    original->setCheckable(true);
    resultImage->setCheckable(true);
    resultImage->setChecked(true);
    auto previewMode = new QButtonGroup(this);
    previewMode->addButton(original);
    previewMode->addButton(resultImage);
    previewMode->setExclusive(true);
    preview.header->addWidget(original);
    preview.header->addWidget(resultImage);
    intermediate_ = new QComboBox;
    intermediate_->setMinimumWidth(128);
    intermediate_->addItem("中间结果", QString());
    preview.header->addWidget(intermediate_);
    imageView_ = new ImageView;
    imageView_->setObjectName("ImagePreview");
    preview.layout->addWidget(imageView_, 1);
    auto imageBar = new QWidget;
    auto imageBarLayout = new QHBoxLayout(imageBar);
    imageBarLayout->setContentsMargins(10, 2, 10, 3);
    imageBarLayout->addStretch();
    auto imageZoom = zoomBox();
    auto imageFit = tool("fit", "图像适应窗口");
    imageBarLayout->addWidget(imageZoom);
    imageBarLayout->addWidget(imageFit);
    previewInfo_ = new QLabel("未加载图像");
    previewInfo_->setObjectName("Muted");
    imageBarLayout->addWidget(previewInfo_);
    imageBarLayout->addStretch();
    preview.layout->addWidget(imageBar);
    lower->addWidget(preview.frame);
    connect(original, &QPushButton::clicked, this, [this] {
        if (!original_.isNull()) {
            imageView_->present(original_);
            imageView_->clearRoi();
        }
    });
    connect(resultImage, &QPushButton::clicked, this, [this] { showResultImage(); });
    connect(intermediate_, &QComboBox::currentIndexChanged, this, [this](int index) {
        auto stable = intermediate_->itemData(index).toString();
        if (stable.isEmpty())
            return;
        auto id = model_->visualId(stable);
        auto outputs = model_->descriptor(model_->business(id)["type"].toString())["outputs"].toArray();
        auto type = outputs.isEmpty() ? QString() : outputs.first().toObject()["type"].toString();
        if (result_.value && (type == "Image" || type == "Mask")) {
            try {
                jpv_handle pointer = nullptr;
                api_->check(api_->result_frame(result_.value, stable.toUtf8().constData(), &pointer));
                OwnedHandle frame(pointer, api_->frame_release);
                jpv_image_info info{};
                auto image = api_->image(frame.value, &info);
                imageView_->present(image, {}, QPoint(info.origin_x, info.origin_y));
            } catch (const std::exception &e) {
                showError(e);
            }
        } else
            showNodeImage(id);
    });
    connect(imageFit, &QToolButton::clicked, imageView_, &ImageView::fit);
    connect(imageZoom, &QComboBox::currentTextChanged, this, [this](QString text) {
        text.remove('%');
        bool ok = false;
        double percent = text.toDouble(&ok);
        if (ok && percent > 0)
            imageView_->setZoomPercent(percent);
    });
    connect(imageView_, &ImageView::zoomChanged, this, [imageZoom](double scale) {
        QSignalBlocker block(imageZoom);
        imageZoom->setCurrentText(QString::number(scale, 'f', 0) + "%");
    });
    auto metadata = panel("图像信息");
    metadata.frame->setMinimumWidth(210);
    metadata.frame->setMaximumWidth(260);
    auto channel = new QComboBox;
    channel->addItems({"RGB", "R", "G", "B", "灰度"});
    metadata.header->addWidget(channel);
    histogram_ = new HistogramWidget;
    metadata.layout->addWidget(histogram_, 1);
    imageInfo_ = new QLabel("尺寸      —\n通道数   —\n数据类型  —\n图像来源  —");
    imageInfo_->setContentsMargins(12, 6, 12, 10);
    imageInfo_->setWordWrap(true);
    metadata.layout->addWidget(imageInfo_);
    lower->addWidget(metadata.frame);
    lower->setStretchFactor(0, 1);
    lower->setStretchFactor(1, 0);
    lower->setSizes({800, 220});
    visualWork->addWidget(lower);
    visualWork->setStretchFactor(0, 4);
    visualWork->setStretchFactor(1, 3);
    visualWork->setSizes({390, 260});
    connect(channel, &QComboBox::currentIndexChanged, this,
            [this](int index) { histogram_->setChannel(index - 1); });
    connect(imageView_, &ImageView::imageChanged, this, &StudioWindow::updateImageInfo);
    auto logs = panel("运行日志");
    logs.frame->setMinimumHeight(82);
    auto errors = new QCheckBox("仅显示错误");
    logs.header->addWidget(errors);
    auto clear = tool("clear", "清空日志"), exportLog = tool("export", "导出日志");
    logs.header->addWidget(clear);
    logs.header->addWidget(exportLog);
    log_ = new RunLog;
    logs.layout->addWidget(log_, 1);
    work->addWidget(logs.frame);
    work->setStretchFactor(0, 7);
    work->setStretchFactor(1, 1);
    work->setSizes({680, 108});
    connect(errors, &QCheckBox::toggled, log_, &RunLog::setErrorsOnly);
    connect(clear, &QToolButton::clicked, log_, &RunLog::clearEntries);
    connect(exportLog, &QToolButton::clicked, this, [this] {
        auto path = QFileDialog::getSaveFileName(this, "导出运行日志", "JP-Vision.log", "日志 (*.log)");
        if (path.isEmpty())
            return;
        QSaveFile file(path);
        auto bytes = log_->allText().toUtf8();
        if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size() || !file.commit())
            showError(std::runtime_error("日志导出失败。"));
    });
    pages_->addWidget(editor);
    auto titleBar = new TitleBar;
    titleBar->setObjectName("TitleBar");
    titleBar->setFixedHeight(72);
    auto titleLayout = new QVBoxLayout(titleBar);
    titleLayout->setContentsMargins(0, 0, 0, 0);
    titleLayout->setSpacing(0);
    auto titleRow = new QHBoxLayout;
    titleRow->setContentsMargins(14, 0, 7, 0);
    titleRow->setSpacing(12);
    auto logo = new QLabel;
    logo->setPixmap(windowIcon().pixmap(28, 28));
    titleRow->addWidget(logo);
    auto wordmark = new QLabel("JP-Vision");
    wordmark->setObjectName("Brand");
    titleRow->addWidget(wordmark);
    auto subtitle = new QLabel("|   机器视觉算法平台");
    subtitle->setObjectName("TitleSubtitle");
    titleRow->addWidget(subtitle);
    recipeCaption_ = new QLabel("未命名配方");
    recipeCaption_->setObjectName("Muted");
    recipeCaption_->setMinimumWidth(130);
    titleRow->addStretch();
    titleRow->addWidget(recipeCaption_);
    auto menus = new QMenuBar;
    for (auto name : {QString("minimize"), QString("maximize"), QString("close")}) {
        auto button = tool(name, name == "close" ? "关闭" : name == "maximize" ? "最大化 / 还原" : "最小化");
        if (name == "close") {
            button->setObjectName("WindowClose");
            connect(button, &QToolButton::clicked, this, &QWidget::close);
        } else if (name == "minimize")
            connect(button, &QToolButton::clicked, this, &QWidget::showMinimized);
        else
            connect(button, &QToolButton::clicked, this, [this] {
                if (isMaximized())
                    showNormal();
                else
                    showMaximized();
            });
        titleRow->addWidget(button);
    }
    titleLayout->addLayout(titleRow);
    titleLayout->addWidget(menus);
    setMenuWidget(titleBar);
    auto file = menus->addMenu("文件(&F)");
    auto edit = menus->addMenu("编辑(&E)");
    auto execution = menus->addMenu("运行(&R)");
    auto viewMenu = menus->addMenu("视图(&V)");
    auto license = menus->addMenu("授权(&L)");
    auto helpMenu = menus->addMenu("帮助(&H)");
    businessMenus_ = {file, edit, execution};
    auto toolbar = addToolBar("流程操作");
    toolbar->setMovable(false);
    auto action = [&](QMenu *menu, QString label, const QKeySequence &key, auto callback) {
        auto item = menu->addAction(label);
        if (!key.isEmpty())
            item->setShortcut(key);
        connect(item, &QAction::triggered, this, callback);
        return item;
    };
    newAction_ = action(file, "新建配方", QKeySequence::New, [this] { newRecipe(); });
    openAction_ = action(file, "打开配方", QKeySequence::Open, [this] {
        auto path =
            QFileDialog::getOpenFileName(this, "打开配方", directory_, "JP-Vision 配方 (recipe.json)");
        if (!path.isEmpty() && confirmDiscard())
            try {
                openRecipe(QFileInfo(path).absolutePath());
            } catch (const std::exception &e) {
                showError(e);
            }
    });
    saveAction_ = action(file, "保存", QKeySequence::Save, [this] { saveRecipe(false); });
    action(file, "另存为", QKeySequence::SaveAs, [this] {
        if (authorized_ && !busy_)
            saveRecipe(true);
    });
    action(file, "导出复现包", {}, [this] {
        if (authorized_ && !busy_)
            exportRepro();
    });
    auto examples = file->addMenu("示例配方");
    auto example = [&](QString label, QString recipe, QString image) {
        action(examples, label, {}, [this, recipe, image] {
            if (!authorized_ || busy_ || !confirmDiscard())
                return;
            try {
                auto base = QCoreApplication::applicationDirPath() + "/samples/";
                openRecipe(base + "recipes/" + recipe + ".vrecipe");
                openImage(base + "images/" + image + ".png");
            } catch (const std::exception &e) {
                showError(e);
            }
        });
    };
    example("目标计数", "parts-count", "three-parts");
    example("灰度转换 / 图像处理", "grayscale", "three-parts");
    example("圆查找", "circles", "three-parts");
    example("直线查找", "lines", "three-lines");
    example("形态学与区域形状分析", "regions", "three-parts");
    example("直线卡尺", "line-caliper", "single-edge");
    example("圆查找／集成测量（两个节点）", "circle-measurement", "three-parts");
    example("圆查找／卡尺拟合与剔除点", "circle-caliper-measurement", "three-parts");
    example("圆卡尺", "circle-caliper", "three-parts");
    example("灰度模板匹配", "template-match", "three-parts");
    file->addSeparator();
    action(file, "退出", QKeySequence::Quit, [this] { close(); });
    auto undo = scene_->undoStack().createUndoAction(this, "撤销"),
         redo = scene_->undoStack().createRedoAction(this, "重做");
    undo->setShortcut(QKeySequence::Undo);
    redo->setShortcut(QKeySequence::Redo);
    edit->addAction(undo);
    edit->addAction(redo);
    edit->addAction(view_->deleteSelectionAction());
    edit->addSeparator();
    interfaceAction_ = action(edit, "公开输入输出…", {}, [this] { configureInterfaces(); });
    imageAction_ = action(execution, "打开图像", QKeySequence("Ctrl+I"), [this] {
        auto path =
            QFileDialog::getOpenFileName(this, "打开调试图像", {}, "图像 (*.png *.bmp *.jpg *.jpeg *.tif)");
        if (!path.isEmpty())
            try {
                openImage(path);
            } catch (const std::exception &e) {
                showError(e);
            }
    });
    validateAction_ = action(execution, "校验流程", QKeySequence("Ctrl+Shift+V"), [this] { validate(); });
    runAction_ = action(execution, "执行", QKeySequence("Ctrl+R"), [this] { run(); });
    cancelAction_ = action(execution, "取消", QKeySequence("Esc"), [this] {
        if (run_.value) {
            api_->run_cancel(run_.value);
            resultLabel_->setText("取消收尾中…");
        }
    });
    cancelAction_->setEnabled(false);
    roiAction_ = action(execution, "编辑 ROI", {}, [this] { roi(); });
    roiAction_->setCheckable(true);

    action(license, "导出机器请求…", {}, [this] { exportRequest(); });
    action(license, "导入许可证…", {}, [this] { importLicense(); });
    action(license, "刷新授权", {}, [this] { refreshLicense(); });
    for (auto item : {newAction_, openAction_, saveAction_})
        toolbar->addAction(item);
    toolbar->addSeparator();
    for (auto item : {imageAction_, validateAction_, runAction_, cancelAction_})
        toolbar->addAction(item);
    toolbar->addSeparator();
    toolbar->addWidget(new QLabel(" 预期数量 "));
    expected_ = new QSpinBox;
    expected_->setRange(0, 1000000);
    expected_->setValue(3);
    toolbar->addWidget(expected_);
    connect(expected_, &QSpinBox::valueChanged, this, [this] {
        if (!loading_)
            clearResult();
    });

    auto spacer = new QWidget;
    spacer->setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Preferred);
    toolbar->addWidget(spacer);
    toolbar->addAction(roiAction_);
    auto parametersAction = toolbar->addAction(StudioTheme::icon("settings"), "参数设置");
    connect(parametersAction, &QAction::triggered, this, [this] {
        parameterTabs_->setCurrentIndex(0);
        inspector_->setFocus();
    });
    auto configurationAction = toolbar->addAction(StudioTheme::icon("settings"), "运行配置");
    connect(configurationAction, &QAction::triggered, this, &StudioWindow::configureRun);

    toolbar->setIconSize({20, 20});
    toolbar->setToolButtonStyle(Qt::ToolButtonTextBesideIcon);
    for (auto pair : {std::pair<QAction *, QString>{newAction_, "new"},
                      {openAction_, "open"},
                      {saveAction_, "save"},
                      {imageAction_, "image"},
                      {validateAction_, "check"},
                      {runAction_, "play"},
                      {cancelAction_, "cancel"},
                      {roiAction_, "roi"}})
        pair.first->setIcon(StudioTheme::icon(
            pair.second, pair.first == runAction_ || pair.first == newAction_ || pair.first == openAction_
                             ? QColor(StudioTheme::Accent)
                             : QColor(StudioTheme::Text)));
    if (auto primary = qobject_cast<QToolButton *>(toolbar->widgetForAction(runAction_)))
        primary->setObjectName("PrimaryTool");
    connect(newDocument, &QToolButton::clicked, this, &StudioWindow::newRecipe);
    action(viewMenu, "流程适应窗口", QKeySequence("Ctrl+0"), [this] { view_->zoomFitAll(); });
    action(viewMenu, "图像适应窗口", QKeySequence("Ctrl+F"), [this] { imageView_->fit(); });
    action(viewMenu, "全屏 / 还原", QKeySequence(Qt::Key_F11), [this] {
        if (isFullScreen())
            showNormal();
        else
            showFullScreen();
    });
    action(helpMenu, "操作说明", {}, [this] {
        QMessageBox::information(this, "使用 JP-Vision",
                                 "拖入组件并连接相同类型的端口。\n选中组件，在右侧直接编辑参数。\n模块可设置"
                                 "矩形、圆形或多边形 ROI。\nROI 内部拖动位置，控制点调整形状，Shift "
                                 "拖动重新框选。\n多边形点击顶点后双击或 Enter 完成，Esc "
                                 "取消。\n模板匹配模块可从图像学习模板。\n修改后点击执行；Ctrl+S "
                                 "保存配方，Ctrl+Z 撤销。");
    });
    action(helpMenu, "关于 JP-Vision", {}, [this] {
        QMessageBox::information(
            this, "JP-Vision",
            QString("JP-Vision %1\nOpenCV 5.0 · C++ Runtime · Qt Studio\n离线机器授权 · WPF SDK")
                .arg(JPV_VERSION));
    });
    view_->addAction(undo);
    view_->addAction(redo);
    readyLabel_ = new QLabel("●  正在检查授权");
    readyLabel_->setContentsMargins(10, 0, 6, 0);
    statusBar()->addWidget(readyLabel_);
    pixelLabel_ = new QLabel("X —  Y —");
    statusBar()->addWidget(pixelLabel_);
    resultLabel_ = new QLabel("等待执行");
    statusBar()->addWidget(resultLabel_, 1);
    statusBar()->addPermanentWidget(
        new QLabel(QString("OpenCV 5.0   |   本地执行   |   JP-Vision v%1").arg(JPV_VERSION)));
    statusBar()->setSizeGripEnabled(true);
    connect(this, &QWidget::windowTitleChanged, this, [this] { updateCaption(); });
    inspect(QtNodes::InvalidNodeId);
    setEnabledState(false);
}
void StudioWindow::updateCaption() {
    auto name = directory_.isEmpty() ? QString("未命名配方") : QFileInfo(directory_).fileName();
    if (dirty_)
        name += " · 未保存";
    recipeCaption_->setText(name);
    documentTab_->setText(name);
}
void StudioWindow::updateImageInfo() {
    auto image = imageView_->currentImage();
    histogram_->setImage(image);
    if (image.isNull()) {
        previewInfo_->setText("未加载图像");
        imageInfo_->setText("尺寸 —\n图像来源 —");
        return;
    }
    bool gray = image.format() == QImage::Format_Grayscale8;
    auto format = gray ? QString("Gray") : QString("RGB");
    previewInfo_->setText(
        QString("%1 × %2   |   %3   |   8-bit").arg(image.width()).arg(image.height()).arg(format));
    imageInfo_->setText(QString("尺寸       %1 × %2\n通道数    %3\n数据类型  8-bit\n图像来源  %4")
                            .arg(image.width())
                            .arg(image.height())
                            .arg(gray ? "1 (Gray)" : "3 (RGB)", imageName_));
}
void StudioWindow::updateIntermediate() {
    QSignalBlocker blocker(intermediate_);
    intermediate_->clear();
    intermediate_->addItem("中间结果", QString());
    auto nodes = outcome_.value("debugValues").toObject();
    for (auto id : model_->allNodeIds()) {
        auto business = model_->business(id);
        if (nodes.contains(business["id"].toString()))
            intermediate_->addItem(model_->descriptor(business["type"].toString())["label"].toString(),
                                   business["id"].toString());
    }
}
void StudioWindow::updateNodeResult(QtNodes::NodeId id) {
    resultTree_->clear();
    if (!model_->nodeExists(id)) {
        new QTreeWidgetItem(resultTree_, {"请选择节点", ""});
        return;
    }
    auto stable = model_->stableId(id);
    auto trace = outcome_.value("nodes").toObject().value(stable).toObject();
    if (trace.isEmpty()) {
        new QTreeWidgetItem(resultTree_, {"状态", "尚未执行"});
        return;
    }
    new QTreeWidgetItem(resultTree_, {"状态", trace["status"].toString()});
    new QTreeWidgetItem(resultTree_,
                        {"耗时", QString::number(trace["elapsedMs"].toDouble(), 'f', 3) + " ms"});
    std::function<void(QTreeWidgetItem *, QString, QJsonValue, int)> add =
        [&](QTreeWidgetItem *parent, QString key, QJsonValue value, int depth) {
            auto item =
                parent ? new QTreeWidgetItem(parent, {key, ""}) : new QTreeWidgetItem(resultTree_, {key, ""});
            if (depth > 3) {
                item->setText(1, "…");
                return;
            }
            if (value.isObject()) {
                auto object = value.toObject();
                for (auto it = object.begin(); it != object.end(); ++it)
                    add(item, MeasurementUi::fieldLabel(it.key()), it.value(), depth + 1);
            } else if (value.isArray()) {
                auto array = value.toArray();
                item->setText(1, QString::number(array.size()) + " 项");
                for (int i = 0; i < std::min(int(array.size()), 100); ++i)
                    add(item, QString::number(i + 1), array[i], depth + 1);
                if (array.size() > 100)
                    new QTreeWidgetItem(item, {"其余项", QString::number(array.size() - 100)});
            } else
                item->setText(1, value.isBool()     ? (value.toBool() ? "true" : "false")
                                 : value.isDouble() ? QString::number(value.toDouble(), 'g', 8)
                                 : value.isNull()   ? "无结果"
                                                    : value.toString());
        };
    auto values = outcome_.value("debugValues").toObject();
    auto measurement = outcome_.value("measurements").toObject().value(stable).toObject();
    if (!measurement.isEmpty())
        add(nullptr, "模块测量结果", measurement, 0);
    if (values.contains(stable))
        add(nullptr, "全部目标／输出", values.value(stable), 0);
    else
        new QTreeWidgetItem(resultTree_, {"全部目标", "开启保留中间结果后查看全部明细"});
    resultTree_->expandToDepth(2);
}
void StudioWindow::configureRun() {
    if (!authorized_ || busy_)
        return;
    QDialog dialog(this);
    dialog.setWindowTitle("运行配置");
    auto layout = new QVBoxLayout(&dialog);
    auto form = new QFormLayout;
    auto timeout = new QSpinBox;
    timeout->setRange(0, 3600000);
    timeout->setSuffix(" ms");
    timeout->setValue(timeoutMs_);
    auto debug = new QCheckBox("保留节点中间图与结果");
    debug->setChecked(debugImages_);
    form->addRow("超时（0 为不限）", timeout);
    form->addRow(debug);
    layout->addLayout(form);
    auto note = new QLabel("超时与取消在 OpenCV 节点前后检查；中间结果用于调试。");
    note->setWordWrap(true);
    layout->addWidget(note);
    auto buttons = new QDialogButtonBox(QDialogButtonBox::Ok | QDialogButtonBox::Cancel);
    layout->addWidget(buttons);
    connect(buttons, &QDialogButtonBox::accepted, &dialog, &QDialog::accept);
    connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
    if (dialog.exec() == QDialog::Accepted) {
        timeoutMs_ = timeout->value();
        debugImages_ = debug->isChecked();
        log_->appendPlainText(
            QString("运行配置 · 超时 %1 ms · 调试 %2").arg(timeoutMs_).arg(debugImages_ ? "开启" : "关闭"));
    }
}

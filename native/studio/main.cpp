#include "StudioWindow.h"
#include "StudioTheme.h"
#include <QApplication>
#include <QIcon>
#include <QImageReader>
#include <QMessageBox>
#include <QPalette>
#include <QStyleFactory>
#include <QThreadPool>
#include <QtNodes/StyleCollection>
int main(int argc, char **argv) {
    QApplication app(argc, argv);
    app.setApplicationName("JP-Vision");
    app.setOrganizationName("JP");
    app.setApplicationVersion(JPV_VERSION);
    const QIcon applicationIcon(":/JPVision/jpVision.ico");
    app.setWindowIcon(applicationIcon);
    app.setStyle(QStyleFactory::create("Fusion"));
    app.setFont(QFont("Microsoft YaHei UI", 10));
    QImageReader::setAllocationLimit(128);
    StudioTheme::apply();
    bool locked = app.arguments().contains("--locked-smoke"),
         smoke = app.arguments().contains("--smoke-test") || app.arguments().contains("--sample-smoke") ||
                 app.arguments().contains("--ui-smoke") || app.arguments().contains("--template-smoke") ||
                 app.arguments().contains("--draft-smoke") ||
                 app.arguments().contains("--processing-smoke") ||
                 app.arguments().contains("--measurement-smoke") || locked;
    if (smoke && applicationIcon.pixmap(32, 32).isNull())
        return 1;
    QString screenshot, store;
    int index = app.arguments().indexOf("--screenshot");
    if (index >= 0 && index + 1 < app.arguments().size())
        screenshot = app.arguments()[index + 1];
    index = app.arguments().indexOf("--license-store");
    if (index >= 0 && index + 1 < app.arguments().size())
        store = app.arguments()[index + 1];
    try {
        auto api = std::make_shared<NativeApi>(store);
        StudioWindow window(api, smoke, screenshot, locked);
        window.show();
        if (smoke && app.arguments().contains("--compact-smoke"))
            window.resize(1280, 800);
        int code = app.exec();
        QThreadPool::globalInstance()->waitForDone();
        return code;
    } catch (const std::exception &e) {
        if (!smoke)
            QMessageBox::critical(nullptr, "JP-Vision 启动失败", QString::fromUtf8(e.what()));
        return 1;
    }
}

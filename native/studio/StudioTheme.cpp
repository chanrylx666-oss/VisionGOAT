#include "StudioTheme.h"
#include <QApplication>
#include <QPainter>
#include <QPainterPath>
#include <QPalette>
#include <QHash>
#include <QJsonDocument>
#include <QtNodes/StyleCollection>

namespace StudioTheme {
QIcon icon(const QString &name, QColor color) {
    static QHash<QString, QIcon> icons;
    auto key = name + color.name(QColor::HexArgb);
    if (icons.contains(key))
        return icons.value(key);
    QIcon result;
    for (int size : {16, 20, 24, 32, 48}) {
        QPixmap pixmap(size, size);
        pixmap.fill(Qt::transparent);
        QPainter p(&pixmap);
        p.setRenderHint(QPainter::Antialiasing);
        p.scale(size / 24.0, size / 24.0);
        p.setPen(QPen(color, 1.7, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
        p.setBrush(Qt::NoBrush);
        auto line = [&](double x1, double y1, double x2, double y2) {
            p.drawLine(QPointF(x1, y1), QPointF(x2, y2));
        };
        if (name == "file" || name == "new") {
            QPainterPath path;
            path.moveTo(6, 3);
            path.lineTo(14, 3);
            path.lineTo(19, 8);
            path.lineTo(19, 21);
            path.lineTo(6, 21);
            path.closeSubpath();
            p.drawPath(path);
            line(14, 3, 14, 8);
            line(14, 8, 19, 8);
        } else if (name == "open") {
            QPainterPath path;
            path.moveTo(3, 7);
            path.lineTo(3, 20);
            path.lineTo(21, 20);
            path.lineTo(21, 7);
            path.lineTo(12, 7);
            path.lineTo(10, 4);
            path.lineTo(3, 4);
            path.closeSubpath();
            p.drawPath(path);
        } else if (name == "save") {
            p.drawRoundedRect(QRectF(4, 3, 16, 18), 1, 1);
            p.drawRect(QRectF(8, 3, 8, 6));
            p.drawRect(QRectF(8, 14, 8, 7));
        } else if (name == "image" || name == "ExternalImage" || name == "ToGray") {
            p.drawRoundedRect(QRectF(3, 4, 18, 16), 2, 2);
            p.drawEllipse(QPointF(8, 9), 1.5, 1.5);
            QPainterPath path;
            path.moveTo(4, 18);
            path.lineTo(10, 12);
            path.lineTo(13, 15);
            path.lineTo(17, 11);
            path.lineTo(21, 16);
            p.drawPath(path);
        } else if (name == "play") {
            p.setBrush(color);
            QPainterPath path;
            path.moveTo(7, 4);
            path.lineTo(20, 12);
            path.lineTo(7, 20);
            path.closeSubpath();
            p.drawPath(path);
        } else if (name == "cancel") {
            p.drawRoundedRect(QRectF(6, 6, 12, 12), 1, 1);
        } else if (name == "check" || name == "CompareNumber") {
            p.drawEllipse(QRectF(3, 3, 18, 18));
            line(7, 12, 10.5, 15.5);
            line(10.5, 15.5, 17, 8.5);
        } else if (name == "roi" || name == "CropRect") {
            p.setPen(QPen(color, 1.6, Qt::DashLine));
            p.drawRect(QRectF(4, 4, 16, 16));
            p.setPen(QPen(color, 1.5));
            for (auto point : {QPointF(4, 4), QPointF(20, 4), QPointF(4, 20), QPointF(20, 20)})
                p.drawRect(QRectF(point.x() - 1.5, point.y() - 1.5, 3, 3));
        } else if (name == "zoom" || name == "FindCircles" || name == "CircleCaliper" ||
                   name == "TemplateMatch") {
            p.drawEllipse(QRectF(4, 3, 13, 13));
            line(15, 15, 21, 21);
            if (name == "FindCircles" || name == "CircleCaliper")
                p.drawEllipse(QRectF(7, 6, 7, 7));
            else {
                line(8, 9.5, 13, 9.5);
                line(10.5, 7, 10.5, 12);
            }
        } else if (name == "CountFeatures" || name == "CountContours" || name == "AnalyzeContours") {
            line(4, 20, 21, 20);
            line(7, 16, 7, 10);
            line(12, 16, 12, 4);
            line(17, 16, 17, 7);
        } else if (name == "FindLines" || name == "LineCaliper") {
            line(4, 19, 20, 5);
            p.drawEllipse(QPointF(4, 19), 1.5, 1.5);
            p.drawEllipse(QPointF(20, 5), 1.5, 1.5);
        } else if (name == "BuildFeatureOverlay" || name == "FindContours" || name == "BuildContourOverlay") {
            p.setPen(QPen(color, 1.8, Qt::DashLine));
            p.drawEllipse(QRectF(4, 4, 16, 16));
        } else if (name == "select") {
            p.setBrush(color);
            QPainterPath path;
            path.moveTo(6, 3);
            path.lineTo(18, 14);
            path.lineTo(12, 14);
            path.lineTo(10, 20);
            path.closeSubpath();
            p.drawPath(path);
        } else if (name == "hand") {
            QPainterPath path;
            path.moveTo(7, 20);
            path.lineTo(3, 12);
            path.cubicTo(2, 10, 4, 9, 6, 11);
            path.lineTo(8, 13);
            path.lineTo(8, 5);
            path.cubicTo(8, 2, 11, 2, 11, 5);
            path.lineTo(11, 10);
            path.lineTo(11, 4);
            path.cubicTo(11, 1, 14, 1, 14, 4);
            path.lineTo(14, 10);
            path.lineTo(14, 5);
            path.cubicTo(14, 2, 17, 2, 17, 5);
            path.lineTo(17, 11);
            path.lineTo(17, 8);
            path.cubicTo(17, 5, 20, 5, 20, 8);
            path.lineTo(20, 15);
            path.lineTo(17, 21);
            p.drawPath(path);
        } else if (name == "fit") {
            for (int sx : {-1, 1})
                for (int sy : {-1, 1}) {
                    double x = 12 + sx * 8, y = 12 + sy * 8;
                    line(x, y, x - sx * 5, y);
                    line(x, y, x, y - sy * 5);
                }
        } else if (name == "plus") {
            line(5, 12, 19, 12);
            line(12, 5, 12, 19);
        } else if (name == "close") {
            line(6, 6, 18, 18);
            line(6, 18, 18, 6);
        } else if (name == "minimize") {
            line(5, 12, 19, 12);
        } else if (name == "maximize") {
            p.drawRect(QRectF(5, 5, 14, 14));
        } else if (name == "clear") {
            line(4, 6, 20, 6);
            line(9, 3, 15, 3);
            line(7, 6, 8, 21);
            line(17, 6, 16, 21);
            line(8, 21, 16, 21);
            line(10, 10, 10, 17);
            line(14, 10, 14, 17);
        } else if (name == "export") {
            line(12, 17, 12, 3);
            line(8, 7, 12, 3);
            line(12, 3, 16, 7);
            line(4, 15, 4, 21);
            line(4, 21, 20, 21);
            line(20, 21, 20, 15);
        } else if (name == "help") {
            p.drawEllipse(QRectF(3, 3, 18, 18));
            p.drawText(QRectF(3, 1, 18, 20), Qt::AlignCenter, "?");
        } else if (name == "GaussianBlur" || name == "MedianBlur" || name == "FilterContoursByArea" ||
                   name == "Threshold") {
            for (int y : {6, 12, 18})
                for (int x : {6, 12, 18})
                    p.drawEllipse(QPointF(x, y), (x == 12 && y == 12) ? 2 : 1, (x == 12 && y == 12) ? 2 : 1);
        } else {
            p.drawEllipse(QRectF(7, 7, 10, 10));
            for (int i = 0; i < 8; ++i) {
                p.save();
                p.translate(12, 12);
                p.rotate(i * 45);
                line(0, -7, 0, -10);
                p.restore();
            }
        }
        p.end();
        result.addPixmap(pixmap);
    }
    icons.insert(key, result);
    return result;
}
void apply() {
    QPalette palette;
    palette.setColor(QPalette::Window, QColor(Background));
    palette.setColor(QPalette::WindowText, QColor(Text));
    palette.setColor(QPalette::Base, QColor("#101012"));
    palette.setColor(QPalette::AlternateBase, QColor("#1B1B1E"));
    palette.setColor(QPalette::Text, QColor(Text));
    palette.setColor(QPalette::Button, QColor(Surface));
    palette.setColor(QPalette::ButtonText, QColor(Text));
    palette.setColor(QPalette::Highlight, QColor("#7D151C"));
    palette.setColor(QPalette::HighlightedText, Qt::white);
    palette.setColor(QPalette::Disabled, QPalette::Text, QColor("#77777E"));
    palette.setColor(QPalette::Disabled, QPalette::ButtonText, QColor("#77777E"));
    qApp->setPalette(palette);
    qApp->setStyleSheet(R"(
        QWidget { color:#F2F2F4; font-family:'Microsoft YaHei UI'; }
        QMainWindow { background:#09090B; }
        QFrame#Panel { background:qlineargradient(x1:0,y1:0,x2:0,y2:1,stop:0 #19191C,stop:1 #101012); border:1px solid #38383C; border-radius:6px; }
        QWidget#PanelHeader { background:qlineargradient(x1:0,y1:0,x2:0,y2:1,stop:0 #212124,stop:1 #141416); border-bottom:1px solid #38383C; border-top-left-radius:6px; border-top-right-radius:6px; }
        QLabel#PanelAccent { background:#FF3038; border-radius:1px; }
        QLabel#SectionTitle { color:#F2F2F4; font-weight:600; font-size:14px; }
        QLabel#Brand { color:#FAFAFA; font-weight:700; font-size:22px; }
        QLabel#TitleSubtitle { color:#D0D0D4; font-size:14px; }
        QLabel#FlowTitle { color:#FF4249; font-weight:700; font-size:16px; }
        QLabel#Muted { color:#A6A6AC; }
        QLabel#NodeHeading { color:#F4F4F6; font-size:14px; font-weight:600; padding-bottom:7px; }
        QLabel#EmptyNote { color:#A6A6AC; }
        QFrame#TitleBar { background:#0D0D0F; border-bottom:1px solid #38383C; }
        QMenuBar { background:#0C0C0E; border-top:1px solid #29292D; spacing:8px; padding-left:8px; }
        QMenuBar::item { padding:5px 12px; border-radius:2px; }
        QMenuBar::item:selected { background:#391316; color:#FFFFFF; }
        QMenu { background:#18181B; border:1px solid #53535A; padding:4px; }
        QMenu::item { padding:8px 28px; }
        QMenu::item:selected { background:#751820; }
        QMenu::separator { background:#38383C; height:1px; margin:4px 8px; }
        QToolBar { background:qlineargradient(x1:0,y1:0,x2:0,y2:1,stop:0 #1A1A1D,stop:1 #101012); border:1px solid #38383C; padding:4px 8px; spacing:8px; }
        QToolBar::separator { background:#3D3D42; width:1px; margin:5px 4px; }
        QToolButton { background:transparent; border:1px solid transparent; border-radius:4px; padding:5px; }
        QToolButton:hover { background:#292124; border-color:#71353A; }
        QToolButton:checked { background:#5B171D; border-color:#FF3038; color:#FFFFFF; }
        QToolButton#WindowClose:hover { background:#BC202A; border-color:#BC202A; }
        QToolButton#DocumentTab { background:#171719; border:1px solid #45454B; padding:7px 12px; }
        QToolButton#PrimaryTool { background:#50151B; border:1px solid #BD2730; color:#FFFFFF; }
        QToolButton#PrimaryTool:hover { background:#771921; border-color:#FF4249; }
        QPushButton { background:#19191C; border:1px solid #56565E; border-radius:4px; padding:6px 12px; }
        QPushButton:hover { background:#2A2124; border-color:#DC363E; }
        QPushButton:checked { background:#64181F; color:#FFFFFF; border-color:#FF3038; }
        QPushButton#PrimaryButton { background:qlineargradient(x1:0,y1:0,x2:0,y2:1,stop:0 #BA202A,stop:1 #68121A); border-color:#F0444C; font-weight:600; }
        QPushButton#PrimaryButton:hover { background:#AF222B; }
        QPushButton:disabled,QToolButton:disabled { color:#77777E; border-color:#333338; background:#121214; }
        QLineEdit,QSpinBox,QDoubleSpinBox,QComboBox { background:#101012; border:1px solid #57575F; border-radius:4px; padding:5px 7px; min-height:18px; selection-background-color:#841C25; }
        QLineEdit:focus,QSpinBox:focus,QDoubleSpinBox:focus,QComboBox:focus { border-color:#FF4249; }
        QComboBox::drop-down { border:0; width:22px; }
        QComboBox QAbstractItemView { background:#18181B; selection-background-color:#751820; }
        QSpinBox::up-button,QDoubleSpinBox::up-button { width:16px; border-bottom:1px solid #45454A; }
        QSpinBox::down-button,QDoubleSpinBox::down-button { width:16px; }
        QTreeWidget { background:transparent; border:0; outline:0; show-decoration-selected:1; }
        QTreeWidget::item { padding:3px 6px; height:18px; border:1px solid transparent; }
        QTreeWidget::item:selected { background:#57171E; color:#FFFFFF; border:1px solid #AD2E36; border-radius:3px; }
        QTreeWidget::item:hover { background:#2A1B1F; }
        QHeaderView::section { background:#212124; color:#D0D0D4; border:0; padding:5px; }
        QTableWidget { background:#101012; alternate-background-color:#1B1B1E; border:1px solid #45454B; gridline-color:#38383C; selection-background-color:#661A22; }
        QTabWidget::pane { border:0; background:transparent; }
        QTabBar::tab { background:#141416; color:#A6A6AC; border:0; border-bottom:2px solid #38383C; padding:9px 18px; }
        QTabBar::tab:selected { background:#38151B; color:#FFFFFF; border-bottom:2px solid #FF3038; }
        QScrollArea,QPlainTextEdit,QGraphicsView { background:transparent; border:0; }
        QPlainTextEdit { color:#CCCCD2; selection-background-color:#751820; }
        QScrollBar:vertical { background:#101012; width:7px; margin:0; }
        QScrollBar::handle:vertical { background:#55555D; min-height:32px; border-radius:3px; }
        QScrollBar:horizontal { background:#101012; height:7px; margin:0; }
        QScrollBar::handle:horizontal { background:#55555D; min-width:32px; border-radius:3px; }
        QScrollBar::add-line,QScrollBar::sub-line { width:0; height:0; }
        QSplitter::handle { background:transparent; }
        QStatusBar { background:#09090B; color:#B8B8BF; border-top:1px solid #38383C; }
        QCheckBox { spacing:7px; }
        QCheckBox::indicator { width:14px; height:14px; border:1px solid #7B7B83; border-radius:2px; background:#141416; }
        QCheckBox::indicator:checked { background:#D32630; border-color:#FF4249; image:url(:/JPVision/checkmark.png); }
        QCheckBox:focus { color:#FFFFFF; }
        QToolButton:focus,QPushButton:focus { border-color:#FF6066; }
        QToolTip { background:#252528; color:#FFFFFF; border:1px solid #7A373E; padding:6px; }
    )");
    auto connections = QtNodes::StyleCollection::connectionStyle().toJson();
    auto style = connections["ConnectionStyle"].toObject();
    style["NormalColor"] = Accent;
    style["SelectedColor"] = "#FFFFFF";
    style["HoveredColor"] = "#FF777D";
    style["ConstructionColor"] = "#D9D9DE";
    style["LineWidth"] = 2.0;
    style["UseDataDefinedColors"] = false;
    connections["ConnectionStyle"] = style;
    QtNodes::StyleCollection::setConnectionStyle(
        QtNodes::ConnectionStyle(QString::fromUtf8(QJsonDocument(connections).toJson())));
    auto nodes = QtNodes::StyleCollection::nodeStyle().toJson();
    auto ns = nodes["NodeStyle"].toObject();
    ns["ConnectionPointDiameter"] = 5.0;
    ns["ShadowEnabled"] = false;
    nodes["NodeStyle"] = ns;
    QtNodes::StyleCollection::setNodeStyle(QtNodes::NodeStyle(nodes));
}
} // namespace StudioTheme

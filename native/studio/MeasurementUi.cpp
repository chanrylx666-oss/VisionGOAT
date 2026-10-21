#include "MeasurementUi.h"
#include <QHash>

QString MeasurementUi::fieldLabel(const QString &key) {
    static const QHash<QString, QString> labels{{"kind", "目标类型"},
                                                {"featureKind", "测量类型"},
                                                {"count", "目标数量"},
                                                {"found", "找到目标"},
                                                {"passed", "数量合格"},
                                                {"selectedIndex", "当前目标索引"},
                                                {"selectedValid", "当前目标有效"},
                                                {"selected", "当前目标测量"},
                                                {"x", "X 坐标（px）"},
                                                {"y", "Y 坐标（px）"},
                                                {"centerX", "中心 X（px）"},
                                                {"centerY", "中心 Y（px）"},
                                                {"radius", "半径（px）"},
                                                {"diameter", "直径（px）"},
                                                {"area", "面积（px²）"},
                                                {"perimeter", "周长（px）"},
                                                {"x1", "起点 X（px）"},
                                                {"y1", "起点 Y（px）"},
                                                {"x2", "终点 X（px）"},
                                                {"y2", "终点 Y（px）"},
                                                {"length", "长度（px）"},
                                                {"angleDegrees", "角度（°）"},
                                                {"score", "匹配评分"},
                                                {"scale", "缩放倍率"},
                                                {"centroidX", "质心 X（px）"},
                                                {"centroidY", "质心 Y（px）"},
                                                {"centroidValid", "质心有效"},
                                                {"circularity", "圆度"},
                                                {"width", "宽度（px）"},
                                                {"height", "高度（px）"},
                                                {"rmsError", "拟合 RMS 误差（px）"},
                                                {"edgePoints", "有效边缘点"},
                                                {"corners", "模板角点"},
                                                {"caliper", "卡尺采样统计"},
                                                {"requestedCount", "卡尺数量"},
                                                {"sampledCount", "找到边缘点数"},
                                                {"retainedCount", "保留点数"},
                                                {"rejectedCount", "实际剔除点数"},
                                                {"missingCount", "未找到边缘卡尺"},
                                                {"rejectedPoints", "剔除点坐标"},
                                                {"method", "拟合方法"}};
    return labels.value(key, key);
}
QString MeasurementUi::summary(const QJsonObject &m) {
    QString text = QString("目标 %1 个 · 数量%2\n")
                       .arg(m["count"].toInt())
                       .arg(m["passed"].toBool() ? "合格" : "不合格");
    auto selected = m["selected"].toObject();
    if (!m["selectedValid"].toBool())
        text +=
            m["found"].toBool() ? "当前索引没有目标，请调整目标索引后执行。" : "未找到目标，本次无测量坐标。";
    else {
        auto number = [&](const char *key) { return QString::number(selected[key].toDouble(), 'f', 3); };
        auto kind = m["featureKind"].toString();
        if (kind == "circle")
            text += QString("圆心 (%1, %2) px\n半径 %3 px · 直径 %4 px")
                        .arg(number("x"), number("y"), number("radius"), number("diameter"));
        else if (kind == "line")
            text += QString("起点 (%1, %2) · 终点 (%3, %4) px\n长度 %5 px · 角度 %6°")
                        .arg(number("x1"), number("y1"), number("x2"), number("y2"), number("length"),
                             number("angleDegrees"));
        else if (kind == "match")
            text += QString("中心 (%1, %2) px\n评分 %3 · 角度 %4° · 尺度 %5")
                        .arg(number("centerX"), number("centerY"), number("score"), number("angleDegrees"),
                             number("scale"));
        else
            text += QString("面积 %1 px² · 周长 %2 px\n质心 (%3, %4) px · %5")
                        .arg(number("area"), number("perimeter"), number("centroidX"), number("centroidY"),
                             selected["centroidValid"].toBool() ? "有效" : "无效");
        if (selected.contains("rmsError"))
            text += "\n拟合 RMS 误差 " + number("rmsError") + " px";
    }
    auto stats = m["caliper"].toObject();
    if (!stats.isEmpty())
        text += QString("\n卡尺 %1 · 保留 %2 · 剔除 %3 · 缺失 %4")
                    .arg(stats["requestedCount"].toInt())
                    .arg(stats["retainedCount"].toInt())
                    .arg(stats["rejectedCount"].toInt())
                    .arg(stats["missingCount"].toInt());
    return text;
}

#pragma once
#include <QJsonObject>
#include <QString>

namespace MeasurementUi {
QString fieldLabel(const QString &key);
QString summary(const QJsonObject &measurement);
} // namespace MeasurementUi

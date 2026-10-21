#include "RunLog.h"
#include <QDateTime>
#include <QTimeZone>

void RunLog::appendPlainText(const QString &text) {
    auto time = QDateTime::currentDateTimeUtc().toTimeZone(QTimeZone("Asia/Shanghai")).toString("HH:mm:ss");
    auto entry = "[" + time + "]  " + (error(text) ? "!  " : "i  ") + text;
    entries_.append(entry);
    if (entries_.size() > 2000)
        entries_.removeFirst();
    if (!errorsOnly_ || error(entry))
        QPlainTextEdit::appendPlainText(entry);
}
void RunLog::setErrorsOnly(bool enabled) {
    errorsOnly_ = enabled;
    clear();
    for (auto entry : entries_)
        if (!enabled || error(entry))
            QPlainTextEdit::appendPlainText(entry);
}

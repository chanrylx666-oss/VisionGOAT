#pragma once
#include <QPlainTextEdit>
#include <QStringList>

class RunLog : public QPlainTextEdit {
    QStringList entries_;
    bool errorsOnly_ = false;
    static bool error(const QString &entry) {
        return entry.contains("错误") || entry.contains("失败") || entry.contains("Failed");
    }

  public:
    explicit RunLog(QWidget *parent = nullptr) : QPlainTextEdit(parent) {
        setReadOnly(true);
        setMaximumBlockCount(2000);
        setFont(QFont("Consolas", 9));
    }
    void appendPlainText(const QString &text);
    void setErrorsOnly(bool enabled);
    void clearEntries() {
        entries_.clear();
        clear();
    }
    QString allText() const {
        return entries_.join('\n');
    }
};

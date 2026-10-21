#pragma once
#include <QColor>
#include <QIcon>

namespace StudioTheme {
inline constexpr auto Background = "#09090B";
inline constexpr auto Surface = "#141416";
inline constexpr auto Border = "#38383C";
inline constexpr auto Text = "#F2F2F4";
inline constexpr auto Muted = "#A6A6AC";
inline constexpr auto Accent = "#FF3038";
QIcon icon(const QString &name, QColor color = QColor(Text));
void apply();
} // namespace StudioTheme

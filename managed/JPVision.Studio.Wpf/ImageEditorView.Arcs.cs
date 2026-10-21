using JPVision.Editor.Core;
using System.Windows;
using System.Windows.Media;

namespace JPVision.Studio;

public sealed partial class ImageEditorView
{
    private static EditorPoint Polar(double x, double y, double radius, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        return new(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
    }
    private static void DrawAnnulus(DrawingContext dc, Pen pen, double x, double y, double inner, double outer, double start, double end)
    {
        if (end - start >= 359.999)
        {
            if (inner > 0) dc.DrawEllipse(null, pen, new(x, y), inner, inner);
            dc.DrawEllipse(null, pen, new(x, y), outer, outer); return;
        }
        var path = new StreamGeometry();
        Point At(double radius, double angle) { var p = Polar(x, y, radius, angle); return new(p.X, p.Y); }
        using (var c = path.Open())
        {
            int steps = Math.Max(1, (int)Math.Ceiling((end - start) / 180));
            c.BeginFigure(At(outer, start), false, true);
            for (int i = 1; i <= steps; i++) c.ArcTo(At(outer, start + (end - start) * i / steps), new(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(At(inner, end), true, false);
            if (inner > 0) for (int i = 1; i <= steps; i++) c.ArcTo(At(inner, end - (end - start) * i / steps), new(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
            else c.LineTo(new(x, y), true, false);
        }
        dc.DrawGeometry(null, pen, path);
    }
}

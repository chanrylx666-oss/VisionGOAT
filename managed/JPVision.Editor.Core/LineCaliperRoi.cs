namespace JPVision.Editor.Core;

/// <summary>A rotated search strip taught by two line endpoints; persisted as an ordinary rotated rectangle.</summary>
public static class LineCaliperRoi
{
    public static RoiRegion FromEndpoints(EditorPoint start, EditorPoint end, double height)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 1 || !double.IsFinite(height) || height < 1)
            throw new ArgumentException("请拖动指定有效线段，并设置正的搜索高度。");
        return RoiRegion.RotatedRectangle((start.X + end.X) / 2, (start.Y + end.Y) / 2, length, height,
            Math.Atan2(dy, dx) * 180 / Math.PI);
    }
    public static (EditorPoint Start, EditorPoint End) Endpoints(RoiRegion roi)
    {
        if (roi.Shape is not ("rectangle" or "rotatedRectangle")) throw new ArgumentException("直线卡尺需要矩形搜索带。");
        double x = roi.Shape == "rectangle" ? roi.X + roi.Width / 2 : roi.X, y = roi.Shape == "rectangle" ? roi.Y + roi.Height / 2 : roi.Y;
        double angle = roi.AngleDegrees * Math.PI / 180, dx = Math.Cos(angle) * roi.Width / 2, dy = Math.Sin(angle) * roi.Width / 2;
        return (new(x - dx, y - dy), new(x + dx, y + dy));
    }
    public static EditorPoint[] Handles(RoiRegion roi)
    {
        var (start, end) = Endpoints(roi); double angle = roi.AngleDegrees * Math.PI / 180;
        double cx = (start.X + end.X) / 2, cy = (start.Y + end.Y) / 2;
        return new[] { start, end, new EditorPoint(cx - Math.Sin(angle) * roi.Height / 2, cy + Math.Cos(angle) * roi.Height / 2), new EditorPoint(cx, cy) };
    }
    public static RoiRegion Adjust(RoiRegion roi, int handle, EditorPoint point, double minimumHeight = 12, double maximumHeight = 512)
    {
        var (start, end) = Endpoints(roi);
        if (handle == 0) return FromEndpoints(point, end, roi.Height);
        if (handle == 1) return FromEndpoints(start, point, roi.Height);
        double cx = (start.X + end.X) / 2, cy = (start.Y + end.Y) / 2;
        if (handle == 2)
        {
            double angle = roi.AngleDegrees * Math.PI / 180;
            return FromEndpoints(start, end, Math.Clamp(2 * Math.Abs(-(point.X - cx) * Math.Sin(angle) + (point.Y - cy) * Math.Cos(angle)), minimumHeight, Math.Max(minimumHeight, maximumHeight)));
        }
        if (handle == 3) return FromEndpoints(new(start.X + point.X - cx, start.Y + point.Y - cy), new(end.X + point.X - cx, end.Y + point.Y - cy), roi.Height);
        throw new ArgumentOutOfRangeException(nameof(handle));
    }
}

using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

/// <summary>Image pixel coordinates are independent of display zoom and original frame origin.</summary>
public sealed partial class ImageEditorView : FrameworkElement
{
    public BitmapSource? Bitmap
    {
        get; private set;
    }
    public FrameInfo? Info
    {
        get; private set;
    }
    public byte[]? Pixels
    {
        get; private set;
    }
    public RoiRegion Roi { get; set; } = RoiRegion.None;
    public RoiRegion Template { get; set; } = RoiRegion.None;
    public JsonElement? Overlays
    {
        get; set;
    }
    public JsonObject? CaliperParameters
    {
        get; set;
    }
    public bool CircleCalipers
    {
        get; set;
    }
    public bool LineStrip { get; set; }
    public double CaliperScale { get; set; } = 1;
    public double LineDrawHeight { get; set; } = 60;
    public bool ShowOverlays { get; set; } = true;
    public bool EditEnabled
    {
        get; set;
    }
    public bool EditTemplate
    {
        get; private set;
    }
    public event Action<RoiRegion, bool>? RegionChanged;
    public event Action? EditCancelled;
    public event Action<string>? PixelChanged;
    public event Action<string>? Error;
    private string? drawing;
    private Action<RoiRegion>? drawingCommitted;
    private readonly List<EditorPoint> vertices = new();
    private Point offset, last, anchor;
    private double scale = 1;
    private int handle = -1;
    private RoiRegion original = RoiRegion.None;
    private bool moving, panning, drawingDrag;
    private ReadOnlyMemory<byte> previewPixels;
    internal (double Scale, double X, double Y) ViewTransform => (scale, offset.X, offset.Y);
    public ImageEditorView()
    {
        Focusable = true;
        SizeChanged += (_, _) => { if (Bitmap is not null) Fit(); };
    }
    public void SetFrame(VisionFrame frame) => SetPixels(frame.CopyPixels(), frame.Info);
    public void SetPixels(byte[] pixels, FrameInfo info)
    {
        Info = info;
        Pixels = pixels;
        previewPixels = default;
        Bitmap = BitmapSource.Create(info.Width, info.Height, 96, 96, info.PixelFormat == VisionPixelFormat.Gray8 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, pixels, info.Stride);
        Bitmap.Freeze();
        Fit();
    }
    internal void SetPreview(CameraPreviewFrame frame)
    {
        bool geometryChanged = Bitmap is null || Info?.Width != frame.Info.Width || Info?.Height != frame.Info.Height;
        Info = frame.Info; Pixels = null; previewPixels = frame.Frame.Pixels; Bitmap = frame.Bitmap;
        if (geometryChanged) Fit();
        else InvalidateVisual();
    }
    public void Clear()
    {
        Bitmap = null;
        Info = null;
        Pixels = null;
        previewPixels = default;
        Roi = Template = RoiRegion.None;
        Overlays = null;
        InvalidateVisual();
    }
    public void Fit()
    {
        if (Bitmap is null || ActualWidth < 1 || ActualHeight < 1)
            return;
        scale = Math.Min((ActualWidth - 20) / Bitmap.PixelWidth, (ActualHeight - 20) / Bitmap.PixelHeight);
        scale = Math.Max(.005, scale);
        offset = new((ActualWidth - Bitmap.PixelWidth * scale) / 2, (ActualHeight - Bitmap.PixelHeight * scale) / 2);
        InvalidateVisual();
    }
    public void Zoom(double factor, Point? at = null)
    {
        var p = at ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var w = World(p);
        scale = Math.Clamp(scale * factor, .005, 32);
        offset = new(p.X - w.X * scale, p.Y - w.Y * scale);
        InvalidateVisual();
    }
    private Point World(Point p) => new((p.X - offset.X) / scale, (p.Y - offset.Y) / scale);
    private EditorPoint Clamped(Point p) => new(Math.Clamp(p.X, 0, Math.Max(0, (Info?.Width ?? 1) - 1)), Math.Clamp(p.Y, 0, Math.Max(0, (Info?.Height ?? 1) - 1)));
    public void BeginDraw(string shape, bool template, Action<RoiRegion>? committed = null)
    {
        if (Bitmap is null)
            throw new InvalidOperationException("请先打开图像并连接图像输入。");
        original = template ? Template : Roi;
        drawing = shape;
        drawingCommitted = committed;
        EditTemplate = template;
        EditEnabled = true;
        vertices.Clear();
        Cursor = Cursors.Cross;
        Focus();
    }
    public void BeginAdjust(bool template)
    {
        drawing = null; drawingCommitted = null; vertices.Clear(); EditTemplate = template; EditEnabled = true; Cursor = Cursors.Arrow; Focus(); InvalidateVisual();
    }
    public void EndEdit()
    {
        drawing = null;
        drawingCommitted = null;
        vertices.Clear();
        EditEnabled = false;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 9, 11)), null, new Rect(RenderSize));
        if (Bitmap is null)
        {
            Label(dc, "打开外部图像，运行后选择算子查看结果", new(22, 25), Brushes.Gray, 13);
            return;
        }
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, offset.X, offset.Y));
        dc.DrawImage(Bitmap, new Rect(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight));
        if (ShowOverlays && Overlays is { ValueKind: JsonValueKind.Array } overlays)
        foreach (var item in overlays.EnumerateArray())
            DrawOverlay(dc, item);
        DrawRegion(dc, Roi, new SolidColorBrush(Color.FromRgb(255, 48, 56)), false);
        DrawRegion(dc, Template, Brushes.White, true);
        if (EditEnabled && CaliperParameters is not null)
            DrawCalipers(dc);
        if (vertices.Count > 0)
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                c.BeginFigure(new(vertices[0].X, vertices[0].Y), false, false);
                foreach (var p in vertices.Skip(1))
                    c.LineTo(new(p.X, p.Y), true, false);
                c.LineTo(World(last), true, false);
            }
            dc.DrawGeometry(null, new Pen(EditTemplate ? Brushes.White : Brushes.Red, 1.5 / scale), geometry);
        }
        dc.Pop();
        if (drawing is not null)
            Label(dc, drawing == "polygon" ? "逐点单击绘制 · 双击或 Enter 完成 · Esc 取消" : drawing == "lineCaliper" ? "从直线起点拖到终点 · 完成后拖动侧面控制点调整搜索高度 · Esc 取消" : "拖拽绘制 · Esc 取消", new(12, 10), Brushes.White, 12);
    }
    private void DrawRegion(DrawingContext dc, RoiRegion region, Brush brush, bool template)
    {
        if (region.Shape == "none")
            return;
        var pen = new Pen(brush, 1.5 / scale);
        if (template)
            pen.DashStyle = DashStyles.Dash;
        if (region.Shape == "rectangle")
            dc.DrawRectangle(null, pen, new(region.X, region.Y, region.Width, region.Height));
        else if (region.Shape == "circle")
            dc.DrawEllipse(null, pen, new(region.X, region.Y), region.Radius, region.Radius);
        else if (region.Shape == "annulus")
            DrawAnnulus(dc, pen, region.X, region.Y, region.InnerRadius, region.Radius, region.StartAngle, region.EndAngle);
        else
            dc.DrawGeometry(null, pen, Polygon(region.Points));
        if (LineStrip && !template && region.Shape is "rectangle" or "rotatedRectangle")
        {
            var (a, b) = LineCaliperRoi.Endpoints(region);
            var guide = new Pen(brush, 1 / scale) { DashStyle = DashStyles.Dash };
            dc.DrawLine(guide, new(a.X, a.Y), new(b.X, b.Y));
        }
        if (!template && EditEnabled && !CircleCalipers && region.Shape == "polygon" &&
            CaliperParameters?["caliperPlacement"]?.GetValue<string>() == "roiBoundary")
        for (int i = 0; i < region.Points.Count; i++)
        {
            var a = region.Points[i]; var b = region.Points[(i + 1) % region.Points.Count];
            var at = new Point((a.X + b.X) / 2 + 4 / scale, (a.Y + b.Y) / 2 + 4 / scale);
            Label(dc, (i + 1).ToString(), new Point(at.X + 1 / scale, at.Y + 1 / scale), Brushes.Black, 11 / scale);
            Label(dc, (i + 1).ToString(), at, Brushes.White, 11 / scale);
        }
        if (EditEnabled && EditTemplate == template)
        foreach (var p in Handles(region))
            dc.DrawRectangle(Brushes.White, new Pen(brush, 1 / scale), new Rect(p.X - 3.5 / scale, p.Y - 3.5 / scale, 7 / scale, 7 / scale));
    }
    private static StreamGeometry Polygon(IEnumerable<EditorPoint> points)
    {
        var array = points.ToArray();
        var path = new StreamGeometry();
        if (array.Length > 0)
        using (var c = path.Open())
        {
            c.BeginFigure(new(array[0].X, array[0].Y), false, true);
            foreach (var p in array.Skip(1))
                c.LineTo(new(p.X, p.Y), true, false);
        }
        path.Freeze();
        return path;
    }
    private EditorPoint[] Handles(RoiRegion r) => LineStrip && !EditTemplate && r.Shape is "rectangle" or "rotatedRectangle" ? LineCaliperRoi.Handles(r) : r.Shape switch
    {
        "rectangle" => new[] { new EditorPoint(r.X, r.Y), new EditorPoint(r.X + r.Width, r.Y), new EditorPoint(r.X + r.Width, r.Y + r.Height), new EditorPoint(r.X, r.Y + r.Height) },
        "circle" => new[] { new EditorPoint(r.X, r.Y), new EditorPoint(r.X + r.Radius, r.Y) },
        "annulus" => new[] { new EditorPoint(r.X, r.Y), new EditorPoint(r.X + r.InnerRadius, r.Y), new EditorPoint(r.X + r.Radius, r.Y), Polar(r.X, r.Y, r.Radius * 1.2, r.StartAngle), Polar(r.X, r.Y, r.Radius * 1.4, r.EndAngle) },
        "rotatedRectangle" => new[] { new EditorPoint(r.X, r.Y) }.Concat(r.Points).Append(Polar(r.X, r.Y, r.Width / 2 + 18, r.AngleDegrees)).ToArray(),
        "polygon" => r.Points.ToArray(),
        _ => Array.Empty<EditorPoint>()
    };
    private void DrawOverlay(DrawingContext dc, JsonElement item)
    {
        double N(string key, double fallback = 0) => item.TryGetProperty(key, out var n) ? n.GetDouble() : fallback;
        string S(string key, string fallback = "") => item.TryGetProperty(key, out var s) ? s.GetString()! : fallback;
        Brush color;
        try
        {
            color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(S("color", "#00FF00")));
        }
        catch { color = Brushes.Lime; }
        var pen = new Pen(color, Math.Clamp(N("lineWidth", 2), 1, 10) / scale);
        double ox = Info?.OriginX ?? 0, oy = Info?.OriginY ?? 0;
        string type = S("type");
        double x = N("x") - ox, y = N("y") - oy;
        if (type is "ellipse" or "circle")
            dc.DrawEllipse(null, pen, new(x, y), N("radiusX", N("radius")), N("radiusY", N("radius")));
        else if (type == "annulus")
        {
            double inner = N("innerRadius"), outer = N("outerRadius");
            DrawAnnulus(dc, pen, x, y, inner, outer, N("startAngle"), N("endAngle", 360));
        }
        else if (type == "rectangle")
            dc.DrawRectangle(null, pen, new Rect(x, y, N("width"), N("height")));
        else if (type == "text")
            Label(dc, S("text"), new(x, y), color, 12 / scale);
        else if (item.TryGetProperty("points", out var points))
        {
            var array = points.EnumerateArray().Select(p => new EditorPoint(p[0].GetDouble() - ox, p[1].GetDouble() - oy)).ToArray();
            var path = new StreamGeometry();
            if (array.Length > 0)
            using (var c = path.Open())
            {
                c.BeginFigure(new(array[0].X, array[0].Y), false, item.TryGetProperty("closed", out var closed) && closed.GetBoolean());
                foreach (var p in array.Skip(1))
                    c.LineTo(new(p.X, p.Y), true, false);
            }
            dc.DrawGeometry(null, pen, path);
        }
    }
    public bool AllowPartialCircleCalipers { get; set; }
    public bool EdgePairCalipers { get; set; }
    internal int DrawnCaliperPreviewCount { get; private set; }
    private void DrawCalipers(DrawingContext dc)
    {
        var windows = CaliperParameters?["edgePointSampling"]?.GetValue<bool>() == true ? CaliperLayout.EdgePoints(Roi, CaliperParameters) :
            EdgePairCalipers ? CaliperLayout.EdgePair(Roi, CaliperParameters?["reverseSearch"]?.GetValue<bool>() == true) is { } pair ? new[] { pair } : Array.Empty<CaliperWindow>() :
            // Preview the taught geometry just as the native sampler does. Editing bounds
            // are controlled separately; a clipped window still has a valid visible preview.
            CaliperLayout.Create(Roi, CaliperParameters!, CircleCalipers, Info!.Width, Info.Height, true);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(255, 48, 56)), 1 / scale);
        var visibleIndices = CaliperLayout.PreviewWindowIndices(windows.Count);
        DrawnCaliperPreviewCount = visibleIndices.Count;
        foreach (int index in visibleIndices)
        {
            var window = windows[index];
            double x = window.X, y = window.Y, nx = window.NormalX, ny = window.NormalY;
            double width = window.Width, height = window.Height;
            var points = new List<EditorPoint>();
            foreach (var corner in new[] { (-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5) })
                points.Add(new(x + height * corner.Item1 * nx - width * corner.Item2 * ny, y + height * corner.Item1 * ny + width * corner.Item2 * nx));
            dc.DrawGeometry(null, pen, Polygon(points));
            dc.DrawLine(pen, new(x - height / 2 * nx, y - height / 2 * ny), new(x + height / 2 * nx, y + height / 2 * ny));
            // Arrow direction matches the native sampler: inside to outside in boundary mode.
            double ax = x + height * .35 * nx, ay = y + height * .35 * ny;
            double arrow = Math.Min(height * .15, 4 / scale);
            dc.DrawLine(pen, new(ax, ay), new(ax - arrow * nx - arrow * .6 * ny, ay - arrow * ny + arrow * .6 * nx));
            dc.DrawLine(pen, new(ax, ay), new(ax - arrow * nx + arrow * .6 * ny, ay - arrow * ny - arrow * .6 * nx));
        }
    }
    private void Label(DrawingContext dc, string text, Point point, Brush brush, double size) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (Bitmap is null)
            return;
        last = e.GetPosition(this);
        anchor = World(last);
        if (e.ChangedButton == MouseButton.Middle)
        {
            panning = true;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || !EditEnabled)
            return;
        if (drawing == "polygon")
        {
            var p = Clamped(anchor);
            if (vertices.Count == 0 || Math.Abs(vertices[^1].X - p.X) + Math.Abs(vertices[^1].Y - p.Y) > 1)
                vertices.Add(p);
            if (e.ClickCount == 2)
                FinishPolygon();
            InvalidateVisual();
            return;
        }
        if (drawing is not null)
        {
            drawingDrag = true;
            CaptureMouse();
            return;
        }
        original = EditTemplate ? Template : Roi;
        var handles = Handles(original);
        handle = Array.FindIndex(handles, p => Math.Sqrt(Math.Pow(p.X - anchor.X, 2) + Math.Pow(p.Y - anchor.Y, 2)) < 9 / scale);
        moving = handle < 0 && original.Contains(anchor.X, anchor.Y);
        if (handle >= 0 || moving)
            CaptureMouse();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var screen = e.GetPosition(this);
        var point = World(screen);
        if (panning)
            offset += screen - last;
        var displayedPixels = Pixels is not null ? Pixels.AsSpan() : previewPixels.Span;
        if (Info is not null && !displayedPixels.IsEmpty)
        {
            int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
            if (x >= 0 && y >= 0 && x < Info.Width && y < Info.Height)
            {
                int at = y * Info.Stride + x * (Info.PixelFormat == VisionPixelFormat.Gray8 ? 1 : 3);
                PixelChanged?.Invoke($"({x + Info.OriginX}, {y + Info.OriginY}) · {displayedPixels[at]}");
            }
        }
        try
        {
            if (drawingDrag)
            {
                var a = Clamped(anchor);
                var b = Clamped(point);
                double radius = Math.Max(1, Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)));
                Assign(drawing switch {
                    "lineCaliper" => LineCaliperRoi.FromEndpoints(a, b, LineDrawHeight),
                    "circle" => RoiRegion.Circle(a.X, a.Y, radius),
                    "annulus" => RoiRegion.Annulus(a.X, a.Y, Math.Max(0, radius - 12), radius + 12),
                    "rotatedRectangle" => RoiRegion.RotatedRectangle((a.X + b.X) / 2, (a.Y + b.Y) / 2, Math.Max(1, Math.Abs(b.X - a.X)), Math.Max(1, Math.Abs(b.Y - a.Y)), 0),
                    _ => RoiRegion.Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) });
            }
            else if (moving)
            {
                var b = original.Bounds;
                bool positionedCircle = AllowPartialCircleCalipers && !EditTemplate;
                double dx = positionedCircle ? point.X - anchor.X : Math.Clamp(point.X - anchor.X, -b.Left, Math.Max(-b.Left, Info!.Width - b.Right)),
                    dy = positionedCircle ? point.Y - anchor.Y : Math.Clamp(point.Y - anchor.Y, -b.Top, Math.Max(-b.Top, Info!.Height - b.Bottom));
                Assign(CirclePositioning.Move(original, dx, dy));
            }
            else if (handle >= 0)
            {
                var c = AllowPartialCircleCalipers && !EditTemplate ? new EditorPoint(point.X, point.Y) : Clamped(point);
                if (LineStrip && !EditTemplate && original.Shape is "rectangle" or "rotatedRectangle")
                {
                    double sigma = double.TryParse(CaliperParameters?["sigma"]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 1;
                    double maximumHeight = CaliperParameters?["edgePointSampling"]?.GetValue<bool>() == true ? 32768 : 512;
                    Assign(LineCaliperRoi.Adjust(original, handle, c, Math.Max(8, 6 * sigma + 6) * Math.Max(1, CaliperScale), maximumHeight * CaliperScale));
                }
                else if (original.Shape == "circle")
                    Assign(handle == 0 ? RoiRegion.Circle(c.X, c.Y, original.Radius) : RoiRegion.Circle(original.X, original.Y, Math.Sqrt(Math.Pow(c.X - original.X, 2) + Math.Pow(c.Y - original.Y, 2))));
                else if (original.Shape == "annulus")
                {
                    double radius = Math.Sqrt(Math.Pow(c.X - original.X, 2) + Math.Pow(c.Y - original.Y, 2));
                    double start = original.StartAngle, end = original.EndAngle;
                    if (handle >= 3)
                    {
                        double angle = (Math.Atan2(c.Y - original.Y, c.X - original.X) * 180 / Math.PI + 360) % 360;
                        if (handle == 3) { start = angle; while (end <= start) end += 360; end = Math.Min(end, start + 360); start = Math.Min(start, end - 10); }
                        else { end = angle; while (end <= start) end += 360; end = Math.Clamp(end, start + 10, start + 360); }
                    }
                    Assign(RoiRegion.Annulus(handle == 0 ? c.X : original.X, handle == 0 ? c.Y : original.Y,
                        handle == 1 ? Math.Clamp(radius, 0, original.Radius - .5) : original.InnerRadius,
                        handle == 2 ? Math.Max(original.InnerRadius + .5, radius) : original.Radius, start, end));
                }
                else if (original.Shape == "rotatedRectangle")
                {
                    double angle = original.AngleDegrees, width = original.Width, height = original.Height;
                    if (handle == 5) angle = Math.Atan2(c.Y - original.Y, c.X - original.X) * 180 / Math.PI;
                    else if (handle > 0)
                    {
                        double radians = angle * Math.PI / 180, dx = c.X - original.X, dy = c.Y - original.Y;
                        width = Math.Max(1, 2 * Math.Abs(Math.Cos(radians) * dx + Math.Sin(radians) * dy));
                        height = Math.Max(1, 2 * Math.Abs(-Math.Sin(radians) * dx + Math.Cos(radians) * dy));
                    }
                    Assign(RoiRegion.RotatedRectangle(handle == 0 ? c.X : original.X, handle == 0 ? c.Y : original.Y, width, height, angle));
                }
                else if (original.Shape == "rectangle")
                {
                    var opposite = Handles(original)[(handle + 2) % 4];
                    Assign(RoiRegion.Rectangle(Math.Min(c.X, opposite.X), Math.Min(c.Y, opposite.Y), Math.Abs(c.X - opposite.X), Math.Abs(c.Y - opposite.Y)));
                }
                else
                {
                    var points = original.Points.ToArray();
                    points[handle] = c;
                    Assign(RoiRegion.Polygon(points));
                }
            }
        }
        catch (ArgumentException) { /* Keep the last valid polygon while moving a vertex. */ }
        last = screen;
        InvalidateVisual();
    }
    private void Assign(RoiRegion region)
    {
        if (EditTemplate)
            Template = region;
        else
            Roi = region;
    }
    private void NotifyRegionChanged(RoiRegion region, bool template)
    {
        var committed = drawingCommitted;
        drawingCommitted = null;
        if (committed is not null) committed(region);
        else RegionChanged?.Invoke(region, template);
    }
    internal void CommitRegion(RoiRegion region, bool template)
    {
        EditTemplate = template;
        Assign(region);
        drawing = null;
        vertices.Clear();
        Cursor = Cursors.Arrow;
        NotifyRegionChanged(region, template);
        InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        bool changed = drawingDrag || moving || handle >= 0;
        if (drawingDrag)
        {
            drawing = null;
            Cursor = Cursors.Arrow;
        }
        drawingDrag = moving = panning = false;
        handle = -1;
        ReleaseMouseCapture();
        if (changed)
            NotifyRegionChanged(EditTemplate ? Template : Roi, EditTemplate);
        InvalidateVisual();
    }
    public void CompletePolygon() => FinishPolygon();
    private void FinishPolygon()
    {
        try
        {
            var region = RoiRegion.Polygon(vertices);
            Assign(region);
            vertices.Clear();
            drawing = null;
            Cursor = Cursors.Arrow;
            NotifyRegionChanged(region, EditTemplate);
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && drawing == "polygon")
        {
            FinishPolygon();
            e.Handled = true;
        }
        if (e.Key == Key.Escape)
        {
            if (drawing is not null || moving || handle >= 0) Assign(original);
            vertices.Clear();
            drawing = null;
            drawingCommitted = null;
            drawingDrag = moving = false;
            handle = -1;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            InvalidateVisual();
            EditCancelled?.Invoke();
            e.Handled = true;
        }
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(this));
        e.Handled = true;
    }
}

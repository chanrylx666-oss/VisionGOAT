using JPVision.Sdk;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Wpf;

public sealed class VisionView : FrameworkElement
{
    private BitmapSource? bitmap;
    private IReadOnlyList<VisionOverlay> overlays = Array.Empty<VisionOverlay>();
    private FrameInfo? info;
    private double scale = 1;
    private Vector offset;
    private Point? drag;
    public static readonly DependencyProperty ShowOverlaysProperty = DependencyProperty.Register(nameof(ShowOverlays), typeof(bool), typeof(VisionView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowOverlays
    {
        get => (bool)GetValue(ShowOverlaysProperty); set => SetValue(ShowOverlaysProperty, value);
    }
    public event Action<string>? PixelPositionChanged;
    public VisionView()
    {
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => Fit();
    }
    public void PresentCopy(VisionFrame frame, IReadOnlyList<VisionOverlay>? graphics = null)
    {
        VerifyAccess();
        bool needsFit = info is null || info.Width != frame.Info.Width || info.Height != frame.Info.Height;
        info = frame.Info;
        bitmap = BitmapSource.Create(info.Width, info.Height, 96, 96, info.PixelFormat == VisionPixelFormat.Gray8 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, frame.CopyPixels(), info.Stride);
        bitmap.Freeze();
        overlays = graphics?.ToArray() ?? Array.Empty<VisionOverlay>();
        if (needsFit)
            Fit();
        else
            InvalidateVisual();
    }
    public void SetOverlays(IReadOnlyList<VisionOverlay> graphics)
    {
        VerifyAccess();
        overlays = graphics.ToArray();
        InvalidateVisual();
    }
    public void Fit()
    {
        if (info is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        scale = Math.Min(ActualWidth / info.Width, ActualHeight / info.Height) * .94;
        offset = new((ActualWidth - info.Width * scale) / 2, (ActualHeight - info.Height * scale) / 2);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 12, 14)), null, new Rect(RenderSize));
        if (bitmap is null || info is null)
            return;
        drawing.PushTransform(new MatrixTransform(new Matrix(scale, 0, 0, scale, offset.X, offset.Y)));
        drawing.DrawImage(bitmap, new Rect(0, 0, info.Width, info.Height));
        if (ShowOverlays)
            foreach (var overlay in overlays)
            {
                var color = (Color)ColorConverter.ConvertFromString(overlay.Color);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                var pen = new Pen(brush, overlay.LineWidth / scale);
                pen.Freeze();
                if (overlay.Kind == "polyline" && overlay.Points.Count > 0)
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        var first = overlay.Points[0];
                        context.BeginFigure(new(first.X - info.OriginX, first.Y - info.OriginY), false, overlay.Closed);
                        foreach (var point in overlay.Points.Skip(1))
                            context.LineTo(new(point.X - info.OriginX, point.Y - info.OriginY), true, false);
                    }
                    geometry.Freeze();
                    drawing.DrawGeometry(null, pen, geometry);
                }
                else if (overlay.Kind == "text")
                {
                    var text = new FormattedText(overlay.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 14 / scale, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawing.DrawText(text, new(overlay.X - info.OriginX, overlay.Y - info.OriginY));
                }
                else if (overlay.Kind == "circle" && overlay.RadiusX > 0 && overlay.RadiusY > 0)
                    drawing.DrawEllipse(null, pen, new(overlay.X - info.OriginX, overlay.Y - info.OriginY), overlay.RadiusX, overlay.RadiusY);
                else if (overlay.Kind == "annulus" && overlay.OuterRadius > 0)
                {
                    var center = new Point(overlay.X - info.OriginX, overlay.Y - info.OriginY);
                    DrawAnnulus(drawing, pen, center, overlay.InnerRadius, overlay.OuterRadius, overlay.StartAngle, overlay.EndAngle);
                }
                else if (overlay.Kind == "line" && overlay.Points.Count == 2)
                    drawing.DrawLine(pen, new(overlay.Points[0].X - info.OriginX, overlay.Points[0].Y - info.OriginY), new(overlay.Points[1].X - info.OriginX, overlay.Points[1].Y - info.OriginY));
                else if (overlay.Kind == "rectangle" && overlay.Width > 0 && overlay.Height > 0)
                    drawing.DrawRectangle(null, pen, new Rect(overlay.X - info.OriginX, overlay.Y - info.OriginY, overlay.Width, overlay.Height));
            }
        drawing.Pop();
    }
    private static void DrawAnnulus(DrawingContext drawing, Pen pen, Point center, double inner, double outer, double start, double end)
    {
        if (end - start >= 359.999)
        {
            if (inner > 0) drawing.DrawEllipse(null, pen, center, inner, inner);
            drawing.DrawEllipse(null, pen, center, outer, outer); return;
        }
        if (end <= start || !double.IsFinite(start) || !double.IsFinite(end)) return;
        Point At(double radius, double angle) => new(center.X + radius * Math.Cos(angle * Math.PI / 180), center.Y + radius * Math.Sin(angle * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            int count = Math.Max(1, (int)Math.Ceiling((end - start) / 180));
            context.BeginFigure(At(outer, start), false, true);
            for (int i = 1; i <= count; i++) context.ArcTo(At(outer, start + (end - start) * i / count), new(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(At(inner, end), true, false);
            if (inner > 0) for (int i = 1; i <= count; i++) context.ArcTo(At(inner, end - (end - start) * i / count), new(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        drawing.DrawGeometry(null, pen, geometry);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (bitmap is null)
            return;
        var mouse = e.GetPosition(this);
        double next = Math.Clamp(scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), .02, 100);
        offset = new(mouse.X - (mouse.X - offset.X) * next / scale, mouse.Y - (mouse.Y - offset.Y) * next / scale);
        scale = next;
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            drag = e.GetPosition(this);
            CaptureMouse();
        }
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        drag = null;
        ReleaseMouseCapture();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (drag is Point previous)
        {
            offset += point - previous;
            drag = point;
            InvalidateVisual();
        }
        if (info is null)
            return;
        int x = (int)Math.Floor((point.X - offset.X) / scale), y = (int)Math.Floor((point.Y - offset.Y) / scale);
        if (x >= 0 && y >= 0 && x < info.Width && y < info.Height && bitmap is not null)
        {
            int channels = info.PixelFormat == VisionPixelFormat.Gray8 ? 1 : 3;
            byte[] pixel = new byte[channels];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, channels, 0);
            PixelPositionChanged?.Invoke($"X {x + info.OriginX}  Y {y + info.OriginY}  " + (channels == 1 ? $"Gray {pixel[0]}" : $"R {pixel[2]} G {pixel[1]} B {pixel[0]}"));
        }
    }
    public static VisionFrame FromBitmap(BitmapSource source)
    {
        if ((long)source.PixelWidth * source.PixelHeight > 32L * 1024 * 1024)
            throw new ArgumentException("Image exceeds 32 megapixels.");
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        int stride = checked(converted.PixelWidth * 3);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return VisionFrame.CopyFrom(pixels, converted.PixelWidth, converted.PixelHeight, stride, VisionPixelFormat.Bgr24);
    }
}

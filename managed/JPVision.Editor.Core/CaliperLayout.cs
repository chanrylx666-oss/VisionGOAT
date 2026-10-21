using System.Globalization;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

/// <summary>A caliper in input-image coordinates; width follows the tangent and height follows the normal.</summary>
public sealed record CaliperWindow(double X, double Y, double NormalX, double NormalY, double Width, double Height);

/// <summary>Sampling size and validation shared by the numeric inspector and the drawing preview.</summary>
public sealed record EdgePointSamplingPlan(int StationCount, int ProfileLength, long ProjectedSamples, double MinimumSearchHeight,
    int MaximumPoints, bool CanExecute, string? Reason);

/// <summary>Shares caliper placement with the editor without depending on WPF or image pixels.</summary>
public static class CaliperLayout
{
    public const long EdgePointSamplingBudget = 16L * 1024 * 1024;

    /// <summary>Drawing indices only; the full sampling layout remains unchanged.</summary>
    public static IReadOnlyList<int> PreviewWindowIndices(int windowCount, int maximumVisible = 256)
    {
        if (windowCount < 1 || maximumVisible < 1) return Array.Empty<int>();
        int visible = Math.Min(windowCount, maximumVisible);
        if (visible == 1) return new[] { 0 };
        return Enumerable.Range(0, visible).Select(i => (int)((long)i * (windowCount - 1) / (visible - 1))).ToArray();
    }

    /// <summary>Counts all requested stations, including when a size or sampling budget prevents execution.</summary>
    public static EdgePointSamplingPlan DescribeEdgePoints(RoiRegion roi, JsonObject p)
    {
        double spacing = Number(p, "sampleSpacing", 4), width = Number(p, "projectionWidth", 5), sigma = Number(p, "sigma", 1), maximum = Number(p, "maximumPoints", 10000);
        double countValue = double.IsFinite(roi.Width) && double.IsFinite(spacing) && roi.Width >= 1 && spacing > 0 ? Math.Floor(roi.Width / spacing) + 1 : 0;
        int count = countValue is >= 1 and <= int.MaxValue ? (int)countValue : 0;
        double profileValue = double.IsFinite(roi.Height) && roi.Height >= 0 ? 2 * Math.Ceiling(roi.Height / 2) + 1 : 0;
        int profile = profileValue is >= 1 and <= int.MaxValue ? (int)profileValue : 0;
        long samples = count is > 0 and <= 32769 && profile is > 0 and <= 32769 && IsInteger(width, 1, 255) ? (long)count * profile * (int)width : 0;
        double minimumHeight = double.IsFinite(sigma) ? Math.Max(8, 6 * sigma + 6) : 8;
        string? reason = roi.Shape == "none" ? "MissingSearchRoi" : roi.Shape is not ("rectangle" or "rotatedRectangle") ? "UnsupportedRoiShape" :
            !double.IsFinite(roi.Width) || roi.Width < 1 ? "InvalidSearchLength" : !InRange(spacing, 1, 32768) ? "InvalidSampleSpacing" :
            !IsInteger(width, 1, 255) || width % 2 != 1 ? "InvalidProjectionWidth" : !InRange(sigma, 0, 10) ? "InvalidSigma" :
            !InRange(roi.Height, 8, 32768) ? "InvalidSearchHeight" : roi.Height < minimumHeight ? "InsufficientSmoothingHeight" :
            !IsInteger(maximum, 2, 32769) ? "InvalidMaximumPoints" : count < 1 || count > maximum ? "TooManySamplingStations" :
            samples > EdgePointSamplingBudget ? "SamplingBudgetExceeded" : null;
        return new(count, profile, samples, minimumHeight, IsInteger(maximum, 2, 32769) ? (int)maximum : 0, reason is null, reason);
    }

    /// <summary>Sampling stations for an irregular edge. These windows do not imply a fitted straight line.</summary>
    public static IReadOnlyList<CaliperWindow> EdgePoints(RoiRegion roi, JsonObject p)
    {
        var plan = DescribeEdgePoints(roi, p);
        if (!plan.CanExecute) return Array.Empty<CaliperWindow>();
        double spacing = Number(p, "sampleSpacing", 4), width = Number(p, "projectionWidth", 5);
        int count = plan.StationCount;
        double angle = roi.AngleDegrees * Math.PI / 180, tx = Math.Cos(angle), ty = Math.Sin(angle), sign = p["reverseSearch"]?.GetValue<bool>() == true ? -1 : 1;
        var (start, _) = LineCaliperRoi.Endpoints(roi);
        return Enumerable.Range(0, count).Select(i => new CaliperWindow(start.X + i * spacing * tx, start.Y + i * spacing * ty, -ty * sign, tx * sign, width, 2 * Math.Ceiling(roi.Height / 2))).ToArray();
    }

    public static CaliperWindow? EdgePair(RoiRegion roi, bool reverse = false)
    {
        if (roi.Shape is not ("rectangle" or "rotatedRectangle")) return null;
        double angle = roi.AngleDegrees * Math.PI / 180, nx = -Math.Sin(angle), ny = Math.Cos(angle);
        if (reverse) { nx = -nx; ny = -ny; }
        int width = Math.Max(1, (int)Math.Round(roi.Width, MidpointRounding.AwayFromZero)); if (width % 2 == 0) width++;
        return new(roi.Shape == "rectangle" ? roi.X + roi.Width / 2 : roi.X, roi.Shape == "rectangle" ? roi.Y + roi.Height / 2 : roi.Y,
            nx, ny, width, 2 * Math.Ceiling(roi.Height / 2));
    }
    /// <summary>
    /// Places calipers on an exact ROI boundary or on the legacy guide inside its bounding box.
    /// In boundary mode normals point out of the ROI. Invalid editing states yield no windows.
    /// Coordinates are local to the input image and do not include a frame origin.
    /// </summary>
    public static IReadOnlyList<CaliperWindow> Create(RoiRegion roi, JsonObject p, bool circle, int inputWidth, int inputHeight, bool allowPartialCircle = false)
    {
        if (roi is null || p is null || inputWidth < 1 || inputHeight < 1)
            return Array.Empty<CaliperWindow>();
        bool strip = !circle && Text(p, "caliperPlacement", "guide") == "roiCenter";
        if (strip)
        {
            if (roi.Shape is not ("rectangle" or "rotatedRectangle")) return Array.Empty<CaliperWindow>();
            p = p.DeepClone().AsObject(); p["searchLength"] = roi.Height;
        }
        if (circle && roi.Shape == "annulus")
        {
            p = p.DeepClone().AsObject(); p["startAngle"] = roi.StartAngle; p["endAngle"] = roi.EndAngle;
            p["searchLength"] = roi.Radius - roi.InnerRadius; p["caliperPlacement"] = "roiBoundary";
            roi = RoiRegion.Circle(roi.X, roi.Y, (roi.Radius + roi.InnerRadius) / 2);
        }

        double countValue = Number(p, "caliperCount", 24), width = Number(p, "projectionWidth", 5), height = Number(p, "searchLength", 60), sigma = Number(p, "sigma", 1);
        bool scaled = p["fixtureScaleApplied"]?.GetValue<bool>() == true;
        if (!IsInteger(countValue, 4, circle ? 256 : 32768) || !IsInteger(width, 1, scaled ? 631 : 63) || width % 2 != 1 || !InRange(height, scaled ? .01 : 8, scaled ? 51200 : 512) || !InRange(sigma, 0, 5) || !scaled && height < 6 * sigma + 6)
            return Array.Empty<CaliperWindow>();
        int count = (int)countValue;
        height = 2 * Math.Ceiling(height / 2);
        string? placement = Text(p, "caliperPlacement", "guide");
        if (strip)
        {
            if (!allowPartialCircle && !TryBounds(roi, inputWidth, inputHeight, out _)) return Array.Empty<CaliperWindow>();
            var (a, b) = LineCaliperRoi.Endpoints(roi);
            double stripAngle = roi.AngleDegrees * Math.PI / 180, sign = p["reverseSearch"]?.GetValue<bool>() == true ? -1 : 1;
            return Enumerable.Range(0, count).Select(i => new CaliperWindow(a.X + (b.X - a.X) * ((i + .5) / count), a.Y + (b.Y - a.Y) * ((i + .5) / count), -Math.Sin(stripAngle) * sign, Math.Cos(stripAngle) * sign, width, height)).ToArray();
        }
        if (placement == "roiBoundary" && circle && allowPartialCircle && roi.Shape == "circle" &&
            double.IsFinite(roi.X) && double.IsFinite(roi.Y) && InRange(roi.Radius, 1, 16384))
            return Circular(p, count, width, height, roi.X, roi.Y, roi.Radius);
        if (placement == "roiBoundary" && !circle && allowPartialCircle && TryEdge(roi, p, out var first, out var second, out double nx, out double ny))
            return Enumerable.Range(0, count).Select(i =>
            {
                double t = (i + .5) / count;
                double sign = p["reverseSearch"]?.GetValue<bool>() == true ? -1 : 1;
                return new CaliperWindow(first.X + (second.X - first.X) * t, first.Y + (second.Y - first.Y) * t, nx * sign, ny * sign, width, height);
            }).ToArray();
        if (placement is not ("guide" or "roiBoundary") || !TryBounds(roi, inputWidth, inputHeight, out var bounds))
            return Array.Empty<CaliperWindow>();

        if (placement == "roiBoundary")
        {
            if (circle)
                return roi.Shape == "circle" ? Circular(p, count, width, height, roi.X, roi.Y, roi.Radius) : Array.Empty<CaliperWindow>();
            if (!TryEdge(roi, p, out var a, out var b, out double normalX, out double normalY))
                return Array.Empty<CaliperWindow>();
            return Enumerable.Range(0, count).Select(i =>
            {
                double t = (i + .5) / count;
                double sign = p["reverseSearch"]?.GetValue<bool>() == true ? -1 : 1;
                return new CaliperWindow(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, normalX * sign, normalY * sign, width, height);
            }).ToArray();
        }

        double cx = bounds.Left + (bounds.Width - 1) / 2, cy = bounds.Top + (bounds.Height - 1) / 2;
        if (circle)
        {
            double radius = Number(p, "radius", 0);
            if (!InRange(radius, 0, 16384))
                return Array.Empty<CaliperWindow>();
            if (radius == 0)
                radius = .375 * Math.Min(bounds.Width - 1, bounds.Height - 1);
            return Circular(p, count, width, height, cx, cy, radius);
        }

        double degrees = Number(p, "angleDegrees", 0), length = Number(p, "guideLength", 0);
        if (!InRange(degrees, -180, 180) || !InRange(length, 0, 32768))
            return Array.Empty<CaliperWindow>();
        double angle = degrees * Math.PI / 180, tx = Math.Cos(angle), ty = Math.Sin(angle);
        if (length == 0)
            length = .9 * Math.Min(bounds.Width / Math.Max(1e-8, Math.Abs(tx)), bounds.Height / Math.Max(1e-8, Math.Abs(ty)));
        return Enumerable.Range(0, count).Select(i =>
        {
            double offset = length * (i / (double)(count - 1) - .5);
            double sign = p["reverseSearch"]?.GetValue<bool>() == true ? -1 : 1;
            return new CaliperWindow(cx + offset * tx, cy + offset * ty, -ty * sign, tx * sign, width, height);
        }).ToArray();
    }

    private static IReadOnlyList<CaliperWindow> Circular(JsonObject p, int count, double width, double height, double cx, double cy, double radius)
    {
        double start = Number(p, "startAngle", 0), end = Number(p, "endAngle", 360), range = end - start;
        if (!InRange(start, -360, 360) || !InRange(end, -360, 720) || !InRange(range, 10, 360) || !double.IsFinite(radius) || radius < 0)
            return Array.Empty<CaliperWindow>();
        int divisor = range >= 359.999 ? count : count - 1;
        return Enumerable.Range(0, count).Select(i =>
        {
            double theta = (start + range * i / divisor) * Math.PI / 180;
            double nx = Math.Cos(theta), ny = Math.Sin(theta);
            return new CaliperWindow(cx + radius * nx, cy + radius * ny, nx, ny, width, height);
        }).ToArray();
    }

    private static bool TryEdge(RoiRegion roi, JsonObject p, out EditorPoint a, out EditorPoint b, out double nx, out double ny)
    {
        a = b = default;
        nx = ny = 0;
        double orientation = 1;
        if (roi.Shape is "rectangle" or "rotatedRectangle")
        {
            if (roi.Shape == "rotatedRectangle")
            {
                int index = Text(p, "roiEdge", "top") switch { "top" => 0, "right" => 1, "bottom" => 2, _ => 3 };
                a = roi.Points[index]; b = roi.Points[(index + 1) % 4];
                double rx = b.X - a.X, ry = b.Y - a.Y, edgeLength = Math.Sqrt(rx * rx + ry * ry);
                nx = ry / edgeLength; ny = -rx / edgeLength; return double.IsFinite(nx) && double.IsFinite(ny) && edgeLength >= 1;
            }
            double right = roi.X + roi.Width, bottom = roi.Y + roi.Height;
            switch (Text(p, "roiEdge", "top"))
            {
                case "top": a = new(roi.X, roi.Y); b = new(right, roi.Y); break;
                case "right": a = new(right, roi.Y); b = new(right, bottom); break;
                case "bottom": a = new(right, bottom); b = new(roi.X, bottom); break;
                case "left": a = new(roi.X, bottom); b = new(roi.X, roi.Y); break;
                default: return false;
            }
        }
        else if (roi.Shape == "polygon")
        {
            double index = Number(p, "roiEdgeIndex", 0);
            if (!IsInteger(index, 0, roi.Points.Count - 1))
                return false;
            a = roi.Points[(int)index];
            b = roi.Points[((int)index + 1) % roi.Points.Count];
            double area = 0;
            for (int i = 0; i < roi.Points.Count; i++)
            {
                var first = roi.Points[i];
                var next = roi.Points[(i + 1) % roi.Points.Count];
                area += first.X * next.Y - first.Y * next.X;
            }
            if (!double.IsFinite(area) || Math.Abs(area) < 2)
                return false;
            orientation = Math.Sign(area);
        }
        else
            return false;
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length < 1)
            return false;
        nx = orientation * dy / length;
        ny = -orientation * dx / length;
        return true;
    }

    private static bool TryBounds(RoiRegion roi, int inputWidth, int inputHeight, out (double Left, double Top, double Width, double Height) bounds)
    {
        bounds = default;
        if (roi.Shape == "none")
        {
            bounds = (0, 0, inputWidth, inputHeight);
            return true;
        }
        double left, top, right, bottom;
        if (roi.Shape == "rectangle")
        {
            if (!InRange(roi.Width, 1, 32768) || !InRange(roi.Height, 1, 32768))
                return false;
            left = roi.X; top = roi.Y; right = left + roi.Width; bottom = top + roi.Height;
        }
        else if (roi.Shape == "circle")
        {
            if (!InRange(roi.Radius, 1, 16384) || !double.IsFinite(roi.X) || !double.IsFinite(roi.Y))
                return false;
            left = Math.Floor(roi.X - roi.Radius); top = Math.Floor(roi.Y - roi.Radius);
            right = Math.Ceiling(roi.X + roi.Radius) + 1; bottom = Math.Ceiling(roi.Y + roi.Radius) + 1;
        }
        else if (roi.Shape is "polygon" or "rotatedRectangle")
        {
            if (roi.Points is null || roi.Points.Count is < 3 or > 64 || roi.Points.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y)))
                return false;
            left = Math.Floor(roi.Points.Min(v => v.X)); top = Math.Floor(roi.Points.Min(v => v.Y));
            right = Math.Ceiling(roi.Points.Max(v => v.X)) + 1; bottom = Math.Ceiling(roi.Points.Max(v => v.Y)) + 1;
        }
        else
            return false;
        if (!InRange(left, 0, inputWidth) || !InRange(top, 0, inputHeight) || !InRange(right, left + 1, inputWidth) || !InRange(bottom, top + 1, inputHeight))
            return false;
        bounds = (left, top, right - left, bottom - top);
        return true;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool IsInteger(double value, double min, double max) => InRange(value, min, max) && value == Math.Truncate(value);
    private static double Number(JsonObject p, string key, double fallback)
    {
        if (p[key] is null)
            return fallback;
        if (p[key] is not JsonValue v)
            return double.NaN;
        if (v.TryGetValue<double>(out double number))
            return number;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : double.NaN;
    }
    private static string? Text(JsonObject p, string key, string fallback) => p[key] is null ? fallback :
        p[key] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
}

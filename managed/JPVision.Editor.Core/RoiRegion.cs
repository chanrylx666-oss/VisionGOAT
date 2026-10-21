using System.Text.Json.Nodes;
using System.Globalization;

namespace JPVision.Editor.Core;

public sealed record RoiRegion(string Shape, double X, double Y, double Width, double Height, double Radius, IReadOnlyList<EditorPoint> Points)
{
    public double InnerRadius { get; init; }
    public double StartAngle { get; init; }
    public double EndAngle { get; init; } = 360;
    public double AngleDegrees { get; init; }
    public string CoordinateSpace { get; init; } = "input";
    public string? ReferenceHash { get; init; }
    public static RoiRegion None { get; } = new("none", 0, 0, 0, 0, 0, Array.Empty<EditorPoint>());
    public static RoiRegion Rectangle(double x, double y, double width, double height) => new("rectangle", Math.Round(x), Math.Round(y), Math.Max(1, Math.Round(width)), Math.Max(1, Math.Round(height)), 0, Array.Empty<EditorPoint>());
    public static RoiRegion Circle(double x, double y, double radius) => new("circle", x, y, 0, 0, Math.Max(1, radius), Array.Empty<EditorPoint>());
    public static RoiRegion Annulus(double x, double y, double inner, double outer, double start = 0, double end = 360)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(inner) || !double.IsFinite(outer) ||
            inner < 0 || outer <= inner || !double.IsFinite(start) || !double.IsFinite(end) || end - start < 10 || end - start > 360)
            throw new ArgumentException("圆环须有递增的内外半径，角度跨度为 10～360°。");
        return new("annulus", x, y, 0, 0, outer, Array.Empty<EditorPoint>()) { InnerRadius = inner, StartAngle = start, EndAngle = end };
    }
    public static RoiRegion RotatedRectangle(double cx, double cy, double width, double height, double angle)
    {
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(width) || !double.IsFinite(height) ||
            width < 1 || height < 1 || !double.IsFinite(angle)) throw new ArgumentException("旋转矩形的中心、宽高或角度无效。");
        angle = Math.IEEERemainder(angle, 360); double radians = angle * Math.PI / 180;
        var points = new[] { new EditorPoint(-width / 2, -height / 2), new(width / 2, -height / 2), new(width / 2, height / 2), new(-width / 2, height / 2) }
            .Select(p => new EditorPoint(cx + Math.Cos(radians) * p.X - Math.Sin(radians) * p.Y, cy + Math.Sin(radians) * p.X + Math.Cos(radians) * p.Y)).ToArray();
        return new("rotatedRectangle", cx, cy, width, height, 0, points) { AngleDegrees = angle };
    }
    public static RoiRegion Polygon(IEnumerable<EditorPoint> vertices, bool referenceCoordinates = false)
    {
        var points = vertices.ToArray();
        if (points.Length < 3 || points.Length > 64 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < (referenceCoordinates ? -65536 : 0) || p.Y < (referenceCoordinates ? -65536 : 0) || p.X > (referenceCoordinates ? 65536 : 32768) || p.Y > (referenceCoordinates ? 65536 : 32768)))
            throw new ArgumentException("多边形须包含 3～64 个有效顶点。");
        double Cross(EditorPoint a, EditorPoint b, EditorPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        double area = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            if (Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) < 1)
                throw new ArgumentException("多边形存在重复或过近的顶点。");
            area += a.X * b.Y - a.Y * b.X;
            for (int j = i + 1; j < points.Length; j++)
            {
                if (j == i + 1 || (i == 0 && j == points.Length - 1))
                    continue;
                var c = points[j];
                var d = points[(j + 1) % points.Length];
                if (Math.Max(a.X, b.X) >= Math.Min(c.X, d.X) && Math.Max(c.X, d.X) >= Math.Min(a.X, b.X) && Math.Max(a.Y, b.Y) >= Math.Min(c.Y, d.Y) && Math.Max(c.Y, d.Y) >= Math.Min(a.Y, b.Y) && Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0)
                    throw new ArgumentException("多边形不能自相交。");
            }
        }
        if (Math.Abs(area) < 2)
            throw new ArgumentException("多边形没有有效面积。");
        return new("polygon", 0, 0, 0, 0, 0, points);
    }
    public JsonObject ToJson()
    {
        JsonObject result = Shape switch {
        "rectangle" => new() { ["shape"] = Shape, ["x"] = (int)X, ["y"] = (int)Y, ["width"] = (int)Width, ["height"] = (int)Height },
        "circle" => new() { ["shape"] = Shape, ["cx"] = X, ["cy"] = Y, ["radius"] = Radius },
        "annulus" => new() { ["shape"] = Shape, ["cx"] = X, ["cy"] = Y, ["innerRadius"] = InnerRadius, ["outerRadius"] = Radius, ["startAngle"] = StartAngle, ["endAngle"] = EndAngle },
        "rotatedRectangle" => new() { ["shape"] = Shape, ["cx"] = X, ["cy"] = Y, ["width"] = Width, ["height"] = Height, ["angleDegrees"] = AngleDegrees },
        "polygon" => new() { ["shape"] = Shape, ["points"] = new JsonArray(Points.Select(p => (JsonNode)new JsonArray(p.X, p.Y)).ToArray()) },
        _ => new() { ["shape"] = "none" }
        };
        if (CoordinateSpace == "reference") result["coordinateSpace"] = CoordinateSpace;
        if (ReferenceHash is not null) result["referenceHash"] = ReferenceHash;
        return result;
    }
    private static double Number(JsonNode node) => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
    public static RoiRegion FromJson(JsonNode? value)
    {
        var result = value?["shape"]?.GetValue<string>() switch {
        "rectangle" => Rectangle(Number(value["x"]!), Number(value["y"]!), Number(value["width"]!), Number(value["height"]!)),
        "circle" => Circle(Number(value["cx"]!), Number(value["cy"]!), Number(value["radius"]!)),
        "annulus" => Annulus(Number(value["cx"]!), Number(value["cy"]!), Number(value["innerRadius"]!), Number(value["outerRadius"]!), Number(value["startAngle"]!), Number(value["endAngle"]!)),
        "rotatedRectangle" => RotatedRectangle(Number(value["cx"]!), Number(value["cy"]!), Number(value["width"]!), Number(value["height"]!), Number(value["angleDegrees"]!)),
        "polygon" => Polygon(value["points"]!.AsArray().Select(p => new EditorPoint(Number(p![0]!), Number(p[1]!))), value["coordinateSpace"]?.GetValue<string>() == "reference"),
        _ => None
        };
        return result with { CoordinateSpace = value?["coordinateSpace"]?.GetValue<string>() ?? "input", ReferenceHash = value?["referenceHash"]?.GetValue<string>() };
    }
    public (double Left, double Top, double Right, double Bottom) Bounds => Shape switch
    {
        "rectangle" => (X, Y, X + Width, Y + Height),
        "circle" => (X - Radius, Y - Radius, X + Radius, Y + Radius),
        "annulus" => (X - Radius, Y - Radius, X + Radius, Y + Radius),
        "rotatedRectangle" => (Points.Min(p => p.X), Points.Min(p => p.Y), Points.Max(p => p.X), Points.Max(p => p.Y)),
        "polygon" => (Points.Min(p => p.X), Points.Min(p => p.Y), Points.Max(p => p.X), Points.Max(p => p.Y)),
        _ => (0, 0, 0, 0)
    };
    public bool Contains(double x, double y)
    {
        if (Shape == "rectangle")
            return x >= X && y >= Y && x < X + Width && y < Y + Height;
        if (Shape == "circle")
            return Math.Pow(x - X, 2) + Math.Pow(y - Y, 2) <= Radius * Radius;
        if (Shape == "annulus")
        {
            double radius = Math.Sqrt(Math.Pow(x - X, 2) + Math.Pow(y - Y, 2));
            double relative = (Math.Atan2(y - Y, x - X) * 180 / Math.PI - StartAngle + 1080) % 360;
            return radius >= InnerRadius && radius <= Radius && (EndAngle - StartAngle >= 359.999 || relative <= EndAngle - StartAngle);
        }
        if (Shape == "rotatedRectangle")
        {
            double radians = AngleDegrees * Math.PI / 180, dx = x - X, dy = y - Y;
            return Math.Abs(Math.Cos(radians) * dx + Math.Sin(radians) * dy) <= Width / 2 && Math.Abs(-Math.Sin(radians) * dx + Math.Cos(radians) * dy) <= Height / 2;
        }
        if (Shape != "polygon")
            return false;
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
    public RoiRegion Translate(double x, double y) => Shape switch { "rectangle" => Rectangle(X + x, Y + y, Width, Height), "circle" => Circle(X + x, Y + y, Radius),
        "annulus" => Annulus(X + x, Y + y, InnerRadius, Radius, StartAngle, EndAngle), "rotatedRectangle" => RotatedRectangle(X + x, Y + y, Width, Height, AngleDegrees),
        "polygon" => new("polygon", 0, 0, 0, 0, 0, Points.Select(p => new EditorPoint(p.X + x, p.Y + y)).ToArray()), _ => this };
}

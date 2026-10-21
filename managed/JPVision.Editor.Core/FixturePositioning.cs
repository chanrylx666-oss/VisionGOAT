using JPVision.Sdk;
using System.Globalization;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public static class FixturePositioning
{
    public static bool UsesFixture(JsonObject p, VisionFixture? fixture, bool circle) => fixture is not null &&
        (circle ? p["positionReference"]?.GetValue<string>() is "fixture" or "auto" : p["roiReference"]?.GetValue<string>() == "fixture");
    public static RoiRegion Resolve(RoiRegion reference, JsonObject p, VisionFixture fixture, FrameInfo frame)
    {
        if ((p.ContainsKey("expectedWidth") || p["caliperPlacement"]?.GetValue<string>() == "roiCenter") && reference.Shape == "rectangle")
            reference = RoiRegion.RotatedRectangle(reference.X + reference.Width / 2, reference.Y + reference.Height / 2, reference.Width, reference.Height, 0);
        if (reference.Shape == "none" && p.ContainsKey("matchSearchRadius"))
            return RoiRegion.Circle(fixture.Current.CenterX - frame.OriginX, fixture.Current.CenterY - frame.OriginY,
                Number(p, "matchSearchRadius", 80) * fixture.ScaleRatio);
        return Map(reference, point =>
        {
            var mapped = fixture.TransformPoint(new(point.X, point.Y));
            return new(mapped.X - frame.OriginX, mapped.Y - frame.OriginY);
        }, fixture.ScaleRatio, fixture.RotationDegrees);
    }
    public static RoiRegion Store(RoiRegion drawn, VisionFixture fixture, FrameInfo frame) => Map(drawn, point =>
    {
        var mapped = fixture.TransformPoint(new(point.X + frame.OriginX, point.Y + frame.OriginY), true);
        return new(mapped.X, mapped.Y);
    }, 1 / fixture.ScaleRatio, -fixture.RotationDegrees) with { CoordinateSpace = "reference", ReferenceHash = fixture.ReferenceHash };

    private static RoiRegion Map(RoiRegion roi, Func<EditorPoint, EditorPoint> map, double scale, double rotation)
    {
        if (roi.Shape == "none") return roi;
        if (roi.Shape is "circle" or "annulus" or "rotatedRectangle")
        {
            var center = map(new(roi.X, roi.Y));
            if (roi.Shape == "annulus")
            {
                double start = Math.IEEERemainder(roi.StartAngle + rotation, 360);
                return RoiRegion.Annulus(center.X, center.Y, roi.InnerRadius * scale, roi.Radius * scale, start, start + roi.EndAngle - roi.StartAngle);
            }
            if (roi.Shape == "rotatedRectangle") return RoiRegion.RotatedRectangle(center.X, center.Y, roi.Width * scale, roi.Height * scale, Math.IEEERemainder(roi.AngleDegrees + rotation, 360));
            return new("circle", center.X, center.Y, 0, 0, roi.Radius * scale, Array.Empty<EditorPoint>());
        }
        var points = roi.Shape == "rectangle" ? new[] { new EditorPoint(roi.X, roi.Y), new(roi.X + roi.Width, roi.Y),
            new(roi.X + roi.Width, roi.Y + roi.Height), new(roi.X, roi.Y + roi.Height) } : roi.Points;
        return new("polygon", 0, 0, 0, 0, 0, points.Select(map).ToArray());
    }
    public static JsonObject Parameters(JsonObject p, VisionFixture fixture)
    {
        var result = p.DeepClone().AsObject(); result["caliperPlacement"] = p["caliperPlacement"]?.GetValue<string>() == "roiCenter" ? "roiCenter" : "roiBoundary"; result["fixtureScaleApplied"] = true;
        int width = Math.Max(1, (int)Math.Round(Number(p, "projectionWidth", 5) * fixture.ScaleRatio, MidpointRounding.AwayFromZero));
        if (width % 2 == 0) width++;
        result["projectionWidth"] = width;
        result["searchLength"] = Number(p, "searchLength", 60) * fixture.ScaleRatio;
        if (p.ContainsKey("startAngle"))
        {
            double start = Math.IEEERemainder(Number(p, "startAngle", 0) + fixture.RotationDegrees, 360);
            result["startAngle"] = start;
            result["endAngle"] = start + Number(p, "endAngle", 360) - Number(p, "startAngle", 0);
        }
        if (p["roi"]?["shape"]?.GetValue<string>() == "rectangle" && p.ContainsKey("roiEdge"))
            result["roiEdgeIndex"] = p["roiEdge"]!.GetValue<string>() switch { "top" => 0, "right" => 1, "bottom" => 2, _ => 3 };
        return result;
    }
    private static double Number(JsonObject p, string key, double fallback) => p[key] is null ? fallback :
        double.Parse(p[key]!.ToJsonString(), CultureInfo.InvariantCulture);
}

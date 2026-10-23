using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>A circle in original-image pixel coordinates.</summary>
public sealed record VisionCircle(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("radius")] double Radius)
{
    [JsonPropertyName("coordinateSpace")] public string CoordinateSpace { get; init; } = "original";
    [JsonIgnore] public double CenterX => X;
    [JsonIgnore] public double CenterY => Y;
    [JsonIgnore] public double Diameter => 2 * Radius;
    [JsonIgnore] public double Area => Math.PI * Radius * Radius;
    [JsonIgnore] public double Perimeter => 2 * Math.PI * Radius;
    /// <summary>The measured circular contour in original-image pixels; available with older runtimes too.</summary>
    [JsonIgnore] public VisionCircleGeometry Circle => new(CenterX, CenterY, Radius) { CoordinateSpace = CoordinateSpace };
    /// <summary>
    /// Configured annular output region around the fitted circle. Its width is a tool setting,
    /// not a measurement of material thickness or inner/outer diameters. Null for older runtimes.
    /// </summary>
    [JsonPropertyName("outputAnnulus")] public VisionAnnulus? OutputAnnulus { get; init; }
    [JsonPropertyName("edgePoints")] public IReadOnlyList<VisionPoint> EdgePoints { get; init; } = Array.Empty<VisionPoint>();
    [JsonPropertyName("rmsError")] public double RmsError { get; init; }
    [JsonPropertyName("caliper")] public VisionCaliperDetails? Caliper { get; init; }
}

/// <summary>Measured circular contour, separate from a configured annular output region.</summary>
public sealed record VisionCircleGeometry(
    [property: JsonPropertyName("centerX")] double CenterX,
    [property: JsonPropertyName("centerY")] double CenterY,
    [property: JsonPropertyName("radius")] double Radius)
{
    [JsonPropertyName("type")] public string Type => "circle";
    [JsonPropertyName("coordinateSpace")] public string CoordinateSpace { get; init; } = "original";
    [JsonPropertyName("diameter")] public double Diameter => 2 * Radius;
}

/// <summary>An annular region in original-image pixels. Start/end angles are in degrees.</summary>
public sealed record VisionAnnulus(
    [property: JsonPropertyName("coordinateSpace"), JsonRequired] string CoordinateSpace,
    [property: JsonPropertyName("centerX"), JsonRequired] double CenterX,
    [property: JsonPropertyName("centerY"), JsonRequired] double CenterY,
    [property: JsonPropertyName("innerRadius"), JsonRequired] double InnerRadius,
    [property: JsonPropertyName("outerRadius"), JsonRequired] double OuterRadius,
    [property: JsonPropertyName("startAngle"), JsonRequired] double StartAngle,
    [property: JsonPropertyName("endAngle"), JsonRequired] double EndAngle);

/// <summary>A detected edge segment. Angle is measured clockwise from +X in image coordinates.</summary>
public sealed record VisionLine(
    [property: JsonPropertyName("x1")] double X1,
    [property: JsonPropertyName("y1")] double Y1,
    [property: JsonPropertyName("x2")] double X2,
    [property: JsonPropertyName("y2")] double Y2,
    [property: JsonPropertyName("length")] double Length,
    [property: JsonPropertyName("angleDegrees")] double AngleDegrees)
{
    [JsonIgnore] public double CenterX => (X1 + X2) / 2;
    [JsonIgnore] public double CenterY => (Y1 + Y2) / 2;
    /// <summary>Normalized ax+by+c=0; absent in older runtime results or for a degenerate line.</summary>
    [JsonPropertyName("equation")] public VisionLineEquation? Equation { get; init; }
    [JsonPropertyName("edgePoints")] public IReadOnlyList<VisionPoint> EdgePoints { get; init; } = Array.Empty<VisionPoint>();
    [JsonPropertyName("rmsError")] public double RmsError { get; init; }
    [JsonPropertyName("caliper")] public VisionCaliperDetails? Caliper { get; init; }
}

public sealed record VisionLineEquation(
    [property: JsonPropertyName("a"), JsonRequired] double A,
    [property: JsonPropertyName("b"), JsonRequired] double B,
    [property: JsonPropertyName("c"), JsonRequired] double C);

/// <summary>A template match in original-image coordinates; positive angles turn clockwise.</summary>
public sealed record VisionMatch(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("centerX")] double CenterX,
    [property: JsonPropertyName("centerY")] double CenterY,
    [property: JsonPropertyName("angleDegrees")] double AngleDegrees,
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("corners")] IReadOnlyList<VisionPoint> Corners)
{
    /// <summary>Stable template-bank entry ID; empty for legacy single-template results.</summary>
    [JsonPropertyName("templateId")] public string TemplateId { get; init; } = "";
    /// <summary>Name of the winning bank entry; empty for legacy single-template results.</summary>
    [JsonPropertyName("templateName")] public string TemplateName { get; init; } = "";
}

/// <summary>Region measurements in pixels. Contour analysis reports polygon area;
/// Blob analysis declares AreaKind=pixelCount and measures actual foreground pixels.</summary>
public sealed record VisionRegion(
    [property: JsonPropertyName("area")] double Area,
    [property: JsonPropertyName("perimeter")] double Perimeter,
    [property: JsonPropertyName("centroidX")] double CentroidX,
    [property: JsonPropertyName("centroidY")] double CentroidY,
    [property: JsonPropertyName("centroidValid")] bool CentroidValid,
    [property: JsonPropertyName("circularity")] double Circularity,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height)
{
    [JsonPropertyName("areaKind")] public string? AreaKind { get; init; }
    [JsonPropertyName("aspectRatio")] public double? AspectRatio { get; init; }
    [JsonPropertyName("extent")] public double? Extent { get; init; }
    /// <summary>Touches the current image or effective ROI boundary; null for legacy contour results.</summary>
    [JsonPropertyName("touchesBoundary")] public bool? TouchesBoundary { get; init; }
}

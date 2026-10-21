using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>A selected match pose. Unavailable selections have no position.</summary>
public sealed record VisionPose(
    [property: JsonPropertyName("available"), JsonRequired] bool Available,
    [property: JsonPropertyName("position"), JsonRequired] VisionMatchPosition? Position,
    [property: JsonPropertyName("modelHash"), JsonRequired] string ModelHash);

/// <summary>Reference-to-current similarity transform, in original-image pixels.</summary>
public sealed record VisionFixture(
    [property: JsonPropertyName("reference"), JsonRequired] VisionMatchPosition Reference,
    [property: JsonPropertyName("current"), JsonRequired] VisionMatchPosition Current,
    [property: JsonPropertyName("modelHash"), JsonRequired] string ModelHash,
    [property: JsonPropertyName("transform"), JsonRequired] string Transform,
    [property: JsonPropertyName("coordinateSpace"), JsonRequired] string CoordinateSpace,
    [property: JsonPropertyName("matrix"), JsonRequired] IReadOnlyList<double> Matrix)
{
    [JsonPropertyName("referenceHash")] public string? ReferenceHash { get; init; }
    [JsonIgnore] public double ScaleRatio => Current.Scale / Reference.Scale;
    [JsonIgnore] public double RotationDegrees => Current.AngleDegrees - Reference.AngleDegrees;
    public VisionPoint TransformPoint(VisionPoint point, bool inverse = false)
    {
        var a = inverse ? Current : Reference; var b = inverse ? Reference : Current;
        if (a.Scale <= 0 || b.Scale <= 0 || !double.IsFinite(a.Scale) || !double.IsFinite(b.Scale))
            throw new InvalidDataException("Invalid fixture scale.");
        double radians = (b.AngleDegrees - a.AngleDegrees) * Math.PI / 180, ratio = b.Scale / a.Scale;
        double x = point.X - a.CenterX, y = point.Y - a.CenterY;
        return new(b.CenterX + ratio * (Math.Cos(radians) * x - Math.Sin(radians) * y),
                   b.CenterY + ratio * (Math.Sin(radians) * x + Math.Cos(radians) * y));
    }
}

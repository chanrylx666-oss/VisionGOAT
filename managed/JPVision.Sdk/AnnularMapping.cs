using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>Map unwrapped plane pixels to the immediate source image's pixel plane.
/// X/Y are plane coordinates (including any downstream crop origin), not angle/radius values.</summary>
public sealed record VisionAnnularMapping
{
    [JsonPropertyName("sourceCoordinateSpace"), JsonRequired] public string SourceCoordinateSpace { get; init; } = "";
    [JsonPropertyName("sourcePlane"), JsonRequired] public string SourcePlane { get; init; } = "";
    [JsonPropertyName("centerX"), JsonRequired] public double CenterX { get; init; }
    [JsonPropertyName("centerY"), JsonRequired] public double CenterY { get; init; }
    [JsonPropertyName("angularOriginDegrees"), JsonRequired] public double AngularOriginDegrees { get; init; }
    [JsonPropertyName("angleStepDegrees"), JsonRequired] public double AngleStepDegrees { get; init; }
    [JsonPropertyName("radialOrigin"), JsonRequired] public double RadialOrigin { get; init; }
    [JsonPropertyName("radialStep"), JsonRequired] public double RadialStep { get; init; }
    [JsonPropertyName("width"), JsonRequired] public int Width { get; init; }
    [JsonPropertyName("height"), JsonRequired] public int Height { get; init; }

    public VisionPoint MapToSource(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Point is outside the unwrapped image plane.");
        double angle = (AngularOriginDegrees + AngleStepDegrees * x) * Math.PI / 180;
        double radius = RadialOrigin + RadialStep * y;
        return new(CenterX + radius * Math.Cos(angle), CenterY + radius * Math.Sin(angle));
    }
}

public sealed partial class InspectionResult
{
    /// <summary>Read the mapping retained on a published unwrapped Image or its downstream image.</summary>
    public VisionAnnularMapping GetAnnularMapping(string imageOutput)
    {
        if (Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("Annular mapping requires a succeeded run.");
        var image = Outputs.GetProperty(imageOutput);
        if (!image.TryGetProperty("coordinateMapping", out var json) ||
            !json.TryGetProperty("kind", out var kind) || kind.GetString() != "annularUnwrap")
            throw new InvalidDataException($"Image '{imageOutput}' has no annular unwrapping mapping.");
        var mapping = json.Deserialize<VisionAnnularMapping>(GeometryJsonOptions)!;
        if (mapping.Width <= 0 || mapping.Height <= 0 || mapping.SourceCoordinateSpace is not ("original" or "annularUnwrapped") ||
            new[] { mapping.CenterX, mapping.CenterY, mapping.AngularOriginDegrees, mapping.AngleStepDegrees, mapping.RadialOrigin, mapping.RadialStep }.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Invalid annular mapping metadata.");
        return mapping;
    }
}

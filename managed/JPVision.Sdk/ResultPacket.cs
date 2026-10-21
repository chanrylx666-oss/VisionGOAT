using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>A detector's complete current output in original-image pixel coordinates.</summary>
/// <typeparam name="T">VisionCircle, VisionLine, VisionRegion or VisionMatch.</typeparam>
public sealed record VisionResultPacket<T> where T : class
{
    [JsonPropertyName("coordinateSpace")] public string CoordinateSpace { get; init; } = "original";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("featureKind")] public string FeatureKind { get; init; } = "";
    /// <summary>All detected targets. An empty list means no target was found.</summary>
    [JsonPropertyName("features")] public IReadOnlyList<T> Features { get; init; } = Array.Empty<T>();
    /// <summary>Selection and detector diagnostics. Selected remains null for a missing target.</summary>
    [JsonPropertyName("measurement")] public VisionMeasurement<T> Measurement { get; init; } = new();
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("found")] public bool Found { get; init; }
    /// <summary>Only the configured inclusive count range; not a whole-recipe decision.</summary>
    [JsonPropertyName("passed")] public bool Passed { get; init; }
    /// <summary>
    /// Original overlay array, including its type, points, styling and text fields.
    /// Use this array to render circle, polyline, rectangle and text annotations without losing fields.
    /// </summary>
    [JsonPropertyName("overlay")] public JsonElement Overlay { get; init; }
    /// <summary>Associated image metadata, including available/origin/padding. Pixel access uses GetOutputImage.</summary>
    [JsonPropertyName("image")] public JsonElement? Image { get; init; }
    [JsonPropertyName("pose")] public VisionPose? Pose { get; init; }
    /// <summary>Selected match center carried with the associated image; null when absent.</summary>
    [JsonIgnore] public VisionMatchPosition? MatchPosition => Image is { ValueKind: JsonValueKind.Object } image &&
        image.TryGetProperty("matchPosition", out var position) && position.ValueKind != JsonValueKind.Null
        ? position.Deserialize<VisionMatchPosition>() : null;
}

/// <summary>Selected template center in original-image pixels; angle/scale describe the match only.</summary>
public sealed record VisionMatchPosition(
    [property: JsonPropertyName("centerX"), JsonRequired] double CenterX,
    [property: JsonPropertyName("centerY"), JsonRequired] double CenterY,
    [property: JsonPropertyName("angleDegrees"), JsonRequired] double AngleDegrees,
    [property: JsonPropertyName("scale"), JsonRequired] double Scale);

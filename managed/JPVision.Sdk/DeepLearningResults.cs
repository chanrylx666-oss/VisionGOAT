using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace JPVision.Sdk;

/// <summary>A defect region in the input coordinate plane; Score is a model score, not a calibrated probability.</summary>
public sealed record VisionDefectRegion
{
    [JsonPropertyName("label"), JsonRequired] public string Label { get; init; } = "";
    [JsonPropertyName("score"), JsonRequired] public double Score { get; init; }
    [JsonPropertyName("area"), JsonRequired] public double Area { get; init; }
    [JsonPropertyName("areaKind"), JsonRequired] public string AreaKind { get; init; } = "";
    [JsonPropertyName("perimeter"), JsonRequired] public double Perimeter { get; init; }
    [JsonPropertyName("centroidX"), JsonRequired] public double CentroidX { get; init; }
    [JsonPropertyName("centroidY"), JsonRequired] public double CentroidY { get; init; }
    [JsonPropertyName("centroidValid"), JsonRequired] public bool CentroidValid { get; init; }
    [JsonPropertyName("x"), JsonRequired] public int X { get; init; }
    [JsonPropertyName("y"), JsonRequired] public int Y { get; init; }
    [JsonPropertyName("width"), JsonRequired] public int Width { get; init; }
    [JsonPropertyName("height"), JsonRequired] public int Height { get; init; }
    [JsonPropertyName("circularity"), JsonRequired] public double Circularity { get; init; }
}

/// <summary>Current-run defect segmentation. Missing/failed inference is never a passing inspection.</summary>
public sealed record VisionDeepLearningDefects
{
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("featureKind"), JsonRequired] public string FeatureKind { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("dataAvailable"), JsonRequired] public bool DataAvailable { get; init; }
    [JsonPropertyName("valid"), JsonRequired] public bool Valid { get; init; }
    [JsonPropertyName("reason"), JsonRequired] public string Reason { get; init; } = "";
    [JsonPropertyName("modelKey"), JsonRequired] public string ModelKey { get; init; } = "";
    [JsonPropertyName("modelId"), JsonRequired] public string ModelId { get; init; } = "";
    [JsonPropertyName("modelVersion"), JsonRequired] public string ModelVersion { get; init; } = "";
    [JsonPropertyName("scoreKind"), JsonRequired] public string ScoreKind { get; init; } = "";
    [JsonPropertyName("count"), JsonRequired] public int Count { get; init; }
    [JsonPropertyName("found"), JsonRequired] public bool Found { get; init; }
    [JsonPropertyName("passed"), JsonRequired] public bool Passed { get; init; }
    [JsonPropertyName("maximumScore"), JsonRequired] public double? MaximumScore { get; init; }
    [JsonPropertyName("totalArea"), JsonRequired] public double? TotalArea { get; init; }
    [JsonPropertyName("tileCount"), JsonRequired] public int TileCount { get; init; }
    [JsonPropertyName("features"), JsonRequired] public IReadOnlyList<VisionDefectRegion> Features { get; init; } = Array.Empty<VisionDefectRegion>();
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
}

public sealed partial class InspectionResult
{
    /// <summary>Read published defect results or those attached to the output Image. Pixel data remains separate.</summary>
    public VisionDeepLearningDefects GetDeepLearningDefects(string name)
    {
        if (Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("Defect results require a succeeded run.");
        var json = ResolvePublishedValue(name, "Result");
        VisionDeepLearningDefects result;
        try { result = json.Deserialize<VisionDeepLearningDefects>(GeometryJsonOptions) ?? throw new InvalidDataException("Empty defect result."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid defect result contract: " + name, ex); }
        void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new InvalidDataException("Invalid defect result contract: " + name); }
        Require(result.ResultKind == "deepLearningDefect" && result.FeatureKind == "region" &&
            (result.CoordinateSpace == "original" || result.CoordinateSpace == "annularUnwrapped" && !string.IsNullOrWhiteSpace(result.CoordinatePlane)) &&
            result.DataAvailable == result.Valid && result.Count is >= 0 and <= 10000 && result.Features is not null &&
            result.Count == result.Features!.Count && result.Found == (result.Count > 0) &&
            result.Overlay.ValueKind == JsonValueKind.Array && result.TileCount is >= 0 and <= 4096 && !string.IsNullOrWhiteSpace(result.Reason));
        if (!result.DataAvailable)
        {
            Require(!result.Passed && result.Count == 0 && result.ScoreKind == "" && result.MaximumScore is null && result.TotalArea is null && result.TileCount == 0 && result.Overlay.GetArrayLength() == 0);
            return result;
        }
        Require(DeepLearningModelRepository.IsModelKey(result.ModelKey) && !string.IsNullOrWhiteSpace(result.ModelId) &&
            !string.IsNullOrWhiteSpace(result.ModelVersion) && result.TileCount > 0 && result.Passed == !result.Found &&
            result.ScoreKind is "probability" or "anomaly" &&
            result.Reason == (result.Found ? "DefectsFound" : "NoDefects") &&
            result.MaximumScore is double maximum && double.IsFinite(maximum) && maximum >= 0 &&
            (result.ScoreKind != "probability" || maximum <= 1) &&
            result.TotalArea is double area && double.IsFinite(area) && area >= 0 && area <= 32L * 1024 * 1024);
        double total = 0;
        foreach (var region in result.Features!)
        {
            Require(region is not null && region.Label == "defect" && region.AreaKind == "pixelCount" && double.IsFinite(region.Score) && region.Score >= 0 &&
                (result.ScoreKind != "probability" || region.Score <= 1) &&
                region.Score <= result.MaximumScore && double.IsFinite(region.Area) && region.Area >= 1 && region.Area == Math.Floor(region.Area) &&
                region.Area <= 32L * 1024 * 1024 && region.Width is >= 1 and <= 32768 && region.Height is >= 1 and <= 32768 &&
                region.Area <= (long)region.Width * region.Height && double.IsFinite(region.Perimeter) &&
                region.Perimeter >= 4 && region.Perimeter <= 4 * region.Area && region.Perimeter == Math.Floor(region.Perimeter) &&
                double.IsFinite(region.Circularity) && region.Circularity >= 0 && region.CentroidValid &&
                double.IsFinite(region.CentroidX) && double.IsFinite(region.CentroidY) &&
                region.CentroidX >= region.X && region.CentroidX <= (double)region.X + region.Width - 1 &&
                region.CentroidY >= region.Y && region.CentroidY <= (double)region.Y + region.Height - 1);
            total += region.Area;
        }
        Require(total == result.TotalArea);
        return result;
    }
}

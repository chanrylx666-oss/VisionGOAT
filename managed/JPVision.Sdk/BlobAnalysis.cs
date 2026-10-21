using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>Connected foreground regions. Area counts pixels; pixelEdges perimeter includes hole boundaries.</summary>
public sealed record VisionBlobAnalysis
{
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("featureKind"), JsonRequired] public string FeatureKind { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("dataAvailable"), JsonRequired] public bool DataAvailable { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("count"), JsonRequired] public int Count { get; init; }
    [JsonPropertyName("candidateCount"), JsonRequired] public int CandidateCount { get; init; }
    [JsonPropertyName("rejectedCount"), JsonRequired] public int RejectedCount { get; init; }
    [JsonPropertyName("totalArea"), JsonRequired] public double? TotalArea { get; init; }
    [JsonPropertyName("perimeterMethod"), JsonRequired] public string PerimeterMethod { get; init; } = "";
    [JsonPropertyName("found"), JsonRequired] public bool Found { get; init; }
    [JsonPropertyName("passed"), JsonRequired] public bool Passed { get; init; }
    [JsonPropertyName("features"), JsonRequired] public IReadOnlyList<VisionRegion> Features { get; init; } = Array.Empty<VisionRegion>();
    [JsonPropertyName("measurement"), JsonRequired] public VisionMeasurement<VisionRegion> Measurement { get; init; } = null!;
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
}

public sealed partial class InspectionResult
{
    /// <summary>Read a published Blob result or the latest result attached to a published Image.</summary>
    public VisionBlobAnalysis GetBlobAnalysis(string name)
    {
        if (Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("Blob analysis requires a succeeded run.");
        var json = ResolvePublishedValue(name, "Result");
        var packet = ReadResult<VisionRegion>(json, name);
        var result = json.Deserialize<VisionBlobAnalysis>(GeometryJsonOptions)!;
        bool validPlane = result.CoordinateSpace == "original" ||
            result.CoordinateSpace == "annularUnwrapped" && !string.IsNullOrWhiteSpace(result.CoordinatePlane);
        if (result.ResultKind != "blobAnalysis" || result.FeatureKind != "region" || !validPlane ||
            result.PerimeterMethod != "pixelEdges" || result.CandidateCount is < 0 or > 100000 ||
            result.Count is < 0 or > 10000 || result.CandidateCount < result.Count ||
            result.RejectedCount != result.CandidateCount - result.Count ||
            result.DataAvailable != packet.Measurement.DataAvailable ||
            result.CoordinateSpace != packet.Measurement.CoordinateSpace ||
            (result.CoordinatePlane ?? "") != (packet.Measurement.CoordinatePlane ?? ""))
            throw new InvalidDataException("Invalid Blob analysis contract: " + name);
        if (!result.DataAvailable)
        {
            if (result.Count != 0 || result.CandidateCount != 0 || result.TotalArea is not null || result.Passed || string.IsNullOrWhiteSpace(result.Reason))
                throw new InvalidDataException("Unavailable Blob analysis must not contain regions or an area total: " + name);
        }
        else
        {
            double total = 0;
            foreach (var region in packet.Features)
            {
                if (region.AreaKind != "pixelCount" || !region.CentroidValid || region.Width is < 1 or > 32768 ||
                    region.Height is < 1 or > 32768 || !double.IsFinite(region.Area) || region.Area < 1 ||
                    region.Area != Math.Floor(region.Area) || region.Area > 32L * 1024 * 1024 ||
                    !double.IsFinite(region.Perimeter) || region.Perimeter < 4 || region.Perimeter > 4 * region.Area ||
                    region.Perimeter != Math.Floor(region.Perimeter) ||
                    !double.IsFinite(region.CentroidX) || !double.IsFinite(region.CentroidY) ||
                    region.CentroidX < region.X || region.CentroidX > (double)region.X + region.Width - 1 ||
                    region.CentroidY < region.Y || region.CentroidY > (double)region.Y + region.Height - 1 ||
                    region.AspectRatio is not double aspect || !BlobClose(aspect, (double)region.Width / region.Height) ||
                    region.Extent is not double extent || !BlobClose(extent, region.Area / ((double)region.Width * region.Height)) ||
                    extent <= 0 || extent > 1 || region.TouchesBoundary is null ||
                    !BlobClose(region.Circularity, 4 * Math.PI * region.Area / (region.Perimeter * region.Perimeter)))
                    throw new InvalidDataException("Invalid Blob region geometry or pixel statistics: " + name);
                total += region.Area;
            }
            if (result.TotalArea is not double area || area > 32L * 1024 * 1024 || !BlobClose(area, total))
                throw new InvalidDataException("Blob area total does not match the accepted regions: " + name);
        }
        return result with { Features = packet.Features, Measurement = packet.Measurement, Overlay = packet.Overlay };
    }

    private static bool BlobClose(double first, double second) => double.IsFinite(first) && double.IsFinite(second) &&
        Math.Abs(first - second) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(first), Math.Abs(second)));
}

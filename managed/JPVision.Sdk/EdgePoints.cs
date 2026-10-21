using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>One edge point, ordered along the strip. Origin distinguishes image evidence from interpolation.</summary>
public sealed record VisionEdgePoint
{
    [JsonPropertyName("index"), JsonRequired] public int Index { get; init; }
    [JsonPropertyName("stationIndex"), JsonRequired] public int StationIndex { get; init; }
    [JsonPropertyName("x"), JsonRequired] public double X { get; init; }
    [JsonPropertyName("y"), JsonRequired] public double Y { get; init; }
    [JsonPropertyName("gradient"), JsonRequired] public double Gradient { get; init; }
    [JsonPropertyName("pathPosition"), JsonRequired] public double PathPosition { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "measured";
    [JsonPropertyName("adjusted")] public bool Adjusted { get; init; }
    [JsonPropertyName("rawX")] public double? RawX { get; init; }
    [JsonPropertyName("rawY")] public double? RawY { get; init; }
}

/// <summary>Measured edge points in the named image coordinate plane. This is not a fitted line or circle.</summary>
public sealed record VisionEdgePointSet
{
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("dataAvailable"), JsonRequired] public bool DataAvailable { get; init; }
    [JsonPropertyName("valid"), JsonRequired] public bool Valid { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("count"), JsonRequired] public int Count { get; init; }
    [JsonPropertyName("stationCount"), JsonRequired] public int StationCount { get; init; }
    [JsonPropertyName("missedCount"), JsonRequired] public int MissedCount { get; init; }
    [JsonPropertyName("coverage"), JsonRequired] public double Coverage { get; init; }
    [JsonPropertyName("points"), JsonRequired] public IReadOnlyList<VisionEdgePoint> Points { get; init; } = Array.Empty<VisionEdgePoint>();
    [JsonPropertyName("curveMode")] public string? CurveMode { get; init; }
    [JsonPropertyName("rawPoints")] public IReadOnlyList<VisionEdgePoint>? RawPoints { get; init; }
    [JsonPropertyName("rawCount")] public int? RawCount { get; init; }
    [JsonPropertyName("measuredCount")] public int? MeasuredCount { get; init; }
    [JsonPropertyName("rejectedCount")] public int? RejectedCount { get; init; }
    [JsonPropertyName("interpolatedCount")] public int? InterpolatedCount { get; init; }
    [JsonPropertyName("observedCoverage")] public double? ObservedCoverage { get; init; }
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
}

/// <summary>A measured edge point and its perpendicular foot on the infinite reference line.</summary>
public sealed record VisionPointLineDistance
{
    [JsonPropertyName("index"), JsonRequired] public int Index { get; init; }
    [JsonPropertyName("pointIndex")] public int? PointIndex { get; init; }
    [JsonPropertyName("stationIndex"), JsonRequired] public int StationIndex { get; init; }
    [JsonPropertyName("x"), JsonRequired] public double X { get; init; }
    [JsonPropertyName("y"), JsonRequired] public double Y { get; init; }
    [JsonPropertyName("projectionX"), JsonRequired] public double ProjectionX { get; init; }
    [JsonPropertyName("projectionY"), JsonRequired] public double ProjectionY { get; init; }
    [JsonPropertyName("distance"), JsonRequired] public double Distance { get; init; }
    [JsonPropertyName("signedDistance"), JsonRequired] public double SignedDistance { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "measured";
    [JsonPropertyName("adjusted")] public bool Adjusted { get; init; }
    [JsonPropertyName("coordinateSource")] public string? CoordinateSource { get; init; }
}

public sealed record VisionPointLineReference
{
    [JsonPropertyName("x1"), JsonRequired] public double X1 { get; init; }
    [JsonPropertyName("y1"), JsonRequired] public double Y1 { get; init; }
    [JsonPropertyName("x2"), JsonRequired] public double X2 { get; init; }
    [JsonPropertyName("y2"), JsonRequired] public double Y2 { get; init; }
    [JsonPropertyName("a"), JsonRequired] public double A { get; init; }
    [JsonPropertyName("b"), JsonRequired] public double B { get; init; }
    [JsonPropertyName("c"), JsonRequired] public double C { get; init; }
}

/// <summary>Per-point distances in pixels. PointSource records whether interpolation was explicitly included.</summary>
public sealed record VisionPointLineDistances
{
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("operation"), JsonRequired] public string Operation { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("unit"), JsonRequired] public string Unit { get; init; } = "";
    [JsonPropertyName("dataAvailable"), JsonRequired] public bool DataAvailable { get; init; }
    [JsonPropertyName("parametersAvailable")] public bool ParametersAvailable { get; init; } = true;
    [JsonPropertyName("valid"), JsonRequired] public bool Valid { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("statistic")] public string? Statistic { get; init; }
    [JsonPropertyName("pointCount"), JsonRequired] public int PointCount { get; init; }
    [JsonPropertyName("count"), JsonRequired] public int Count { get; init; }
    [JsonPropertyName("pointSource")] public string? PointSource { get; init; }
    [JsonPropertyName("measuredCount")] public int? MeasuredCount { get; init; }
    [JsonPropertyName("interpolatedCount")] public int? InterpolatedCount { get; init; }
    [JsonPropertyName("distances"), JsonRequired] public IReadOnlyList<VisionPointLineDistance> Distances { get; init; } = Array.Empty<VisionPointLineDistance>();
    [JsonPropertyName("referenceLine")] public VisionPointLineReference? ReferenceLine { get; init; }
    [JsonPropertyName("minimum"), JsonRequired] public double? Minimum { get; init; }
    [JsonPropertyName("maximum"), JsonRequired] public double? Maximum { get; init; }
    [JsonPropertyName("mean"), JsonRequired] public double? Mean { get; init; }
    [JsonPropertyName("rms"), JsonRequired] public double? Rms { get; init; }
    [JsonPropertyName("standardDeviation"), JsonRequired] public double? StandardDeviation { get; init; }
    [JsonPropertyName("range"), JsonRequired] public double? Range { get; init; }
    [JsonPropertyName("minimumPoint")] public VisionPoint? MinimumPoint { get; init; }
    [JsonPropertyName("maximumPoint")] public VisionPoint? MaximumPoint { get; init; }
    [JsonPropertyName("value"), JsonRequired] public double? Value { get; init; }
    [JsonPropertyName("lowerLimit")] public double? LowerLimit { get; init; }
    [JsonPropertyName("upperLimit")] public double? UpperLimit { get; init; }
    [JsonPropertyName("decision"), JsonRequired] public VisionToleranceDecision Decision { get; init; } = null!;
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
}

public sealed partial class InspectionResult
{
    /// <summary>Read a point-set result or the latest point set attached to a published Image.</summary>
    public VisionEdgePointSet GetEdgePoints(string name)
    {
        if (Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("Edge points require a succeeded run.");
        var result = ResolvePublishedValue(name, "PointSet").Deserialize<VisionEdgePointSet>(GeometryJsonOptions)!;
        if (result is null || result.ResultKind != "edgePointSet" || !EdgeCoordinateSpace(result.CoordinateSpace, result.CoordinatePlane) ||
            result.Points is null || result.Count != result.Points.Count || result.Count < 0 || result.StationCount < result.Count ||
            result.MissedCount != result.StationCount - result.Count || !double.IsFinite(result.Coverage) || result.Coverage < 0 || result.Coverage > 1 ||
            !EdgeClose(result.Coverage, result.StationCount == 0 ? 0 : (double)result.Count / result.StationCount) ||
            result.Valid && (!result.DataAvailable || result.Count == 0) || !result.DataAvailable && result.Count != 0 ||
            result.Overlay.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid edge point-set contract: " + name);
        ValidateEdgePointOrder(result.Points, result.StationCount, name);
        bool rawAliasesOutput = result.CurveMode == "raw" && result.RawPoints is null;
        if (rawAliasesOutput) result = result with { RawPoints = result.Points };
        bool reconstructedContract = result.CurveMode is not null || result.RawPoints is not null || result.RawCount is not null ||
            result.MeasuredCount is not null || result.RejectedCount is not null || result.InterpolatedCount is not null || result.ObservedCoverage is not null;
        if (reconstructedContract)
        {
            if (result.CurveMode is not ("raw" or "filter" or "reconstruct") || result.RawPoints is null ||
                result.RawCount is not int rawCount || rawCount != result.RawPoints.Count || rawCount < 0 || rawCount > result.StationCount ||
                result.MeasuredCount is not int measuredCount || measuredCount < 0 || measuredCount > rawCount ||
                result.RejectedCount != rawCount - measuredCount || result.InterpolatedCount != result.Count - measuredCount ||
                result.InterpolatedCount < 0 || result.ObservedCoverage is not double observed || !double.IsFinite(observed) ||
                !EdgeClose(observed, result.StationCount == 0 ? 0 : (double)measuredCount / result.StationCount) ||
                result.Valid && measuredCount == 0 || result.Points.Count(p => p.Origin == "measured") != measuredCount ||
                result.Points.Count(p => p.Origin == "interpolated") != result.InterpolatedCount ||
                result.CurveMode != "reconstruct" && result.InterpolatedCount != 0 ||
                result.CurveMode == "raw" && result.Points.Any(point => point.Adjusted))
                throw new InvalidDataException("Invalid edge reconstruction counts or coverage: " + name);
            // Raw output already is its observation list. Reuse it rather than expanding a second large array.
            if (rawAliasesOutput) return result;
            ValidateEdgePointOrder(result.RawPoints, result.StationCount, name);
            var rawByStation = result.RawPoints.ToDictionary(point => point.StationIndex);
            if (result.RawPoints.Any(point => point.Origin != "measured"))
                throw new InvalidDataException("Raw edge points must contain image measurements only: " + name);
            foreach (var point in result.Points.Where(point => point.Origin == "measured"))
                if (!rawByStation.TryGetValue(point.StationIndex, out var raw) ||
                    !EdgeClose(point.RawX ?? point.X, raw.X) || !EdgeClose(point.RawY ?? point.Y, raw.Y) ||
                    !EdgeClose(point.Gradient, raw.Gradient) || !EdgeClose(point.PathPosition, raw.PathPosition))
                    throw new InvalidDataException("A retained edge point does not match its raw observation: " + name);
        }
        else if (result.Points.Any(point => point.Origin != "measured"))
            throw new InvalidDataException("Interpolated edge points require reconstruction diagnostics: " + name);
        return result;
    }

    /// <summary>Read point-to-line distances or the measurement attached to a published Image.</summary>
    public VisionPointLineDistances GetPointLineDistances(string name)
    {
        if (Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("Point-to-line distances require a succeeded run.");
        var result = ResolvePublishedValue(name, "Measurement").Deserialize<VisionPointLineDistances>(GeometryJsonOptions)!;
        if (result is null || result.ResultKind != "pointLineDistances" || result.Operation != "pointSetToLine" || result.Unit != "px" ||
            !EdgeCoordinateSpace(result.CoordinateSpace, result.CoordinatePlane) || result.Distances is null || result.PointCount < 0 ||
            result.Count != result.Distances.Count || result.Distances.Any(point => point is null) || result.Count < 0 || result.Count > result.PointCount || result.Decision is null ||
            !Enum.IsDefined(result.Decision.Status) || result.Overlay.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid point-to-line distance contract: " + name);
        bool sourceContract = result.PointSource is not null || result.MeasuredCount is not null || result.InterpolatedCount is not null ||
            result.Distances.Any(point => point.PointIndex is not null || point.CoordinateSource is not null);
        if (sourceContract && (result.PointSource is not ("measuredOnly" or "all") ||
            result.MeasuredCount is not int measured || measured < 0 || result.InterpolatedCount is not int interpolated || interpolated < 0 ||
            measured + interpolated != result.Count || result.Distances.Count(point => point.Origin == "measured") != measured ||
            result.Distances.Count(point => point.Origin == "interpolated") != interpolated ||
            result.PointSource == "measuredOnly" && (interpolated != 0 || result.Distances.Any(point => point.Adjusted)) ||
            result.Distances.Any(point => point.PointIndex is not int sourceIndex || sourceIndex < 0 || sourceIndex >= result.PointCount ||
                point.CoordinateSource != (result.PointSource == "all" ? "processed" : "rawMeasured"))))
            throw new InvalidDataException("Invalid point-to-line point origins or counts: " + name);
        if (!sourceContract && result.Distances.Any(point => point.Origin != "measured"))
            throw new InvalidDataException("Interpolated distances require an explicit point source: " + name);
        double?[] statistics = { result.Minimum, result.Maximum, result.Mean, result.Rms, result.StandardDeviation, result.Range, result.Value };
        if (!result.Valid)
        {
            if (result.Count != 0 || statistics.Any(number => number is not null) || result.Decision.Status != InspectionDecision.Unknown ||
                result.MinimumPoint is not null || result.MaximumPoint is not null)
                throw new InvalidDataException("Unavailable point-to-line measurement must not contain distances or statistics.");
            return result;
        }
        if (!result.DataAvailable || !result.ParametersAvailable || result.Count == 0 || statistics.Any(number => number is not double value || !double.IsFinite(value) || value < 0) ||
            result.ReferenceLine is not { } line || !EdgeFinite(line.X1, line.Y1, line.X2, line.Y2, line.A, line.B, line.C))
            throw new InvalidDataException("Valid point-to-line measurement requires measured points and finite statistics.");
        double normalLength = Math.Sqrt(line.A * line.A + line.B * line.B);
        double lineLength = Math.Sqrt((line.X2 - line.X1) * (line.X2 - line.X1) + (line.Y2 - line.Y1) * (line.Y2 - line.Y1));
        if (normalLength < 1e-12 || lineLength < 1e-12)
            throw new InvalidDataException("The point-to-line reference line must be nondegenerate.");
        int previousStation = -1, previousPointIndex = -1;
        foreach (var point in result.Distances)
        {
            if (point is null || point.Index < 0 || point.Origin is not ("measured" or "interpolated") || point.StationIndex <= previousStation ||
                point.PointIndex is int sourceIndex && sourceIndex <= previousPointIndex ||
                point.Origin == "interpolated" && point.Adjusted || !EdgeFinite(point.X, point.Y, point.ProjectionX, point.ProjectionY, point.Distance, point.SignedDistance) ||
                point.Distance < 0 || !EdgeClose(point.Distance, Math.Abs(point.SignedDistance)) ||
                !EdgeClose(point.SignedDistance, (line.A * point.X + line.B * point.Y + line.C) / normalLength) ||
                !EdgeClose(point.ProjectionX, point.X - point.SignedDistance * line.A / normalLength) ||
                !EdgeClose(point.ProjectionY, point.Y - point.SignedDistance * line.B / normalLength))
                throw new InvalidDataException("Invalid perpendicular point-to-line projection: " + name);
            previousStation = point.StationIndex;
            if (point.PointIndex is int pointIndex) previousPointIndex = pointIndex;
        }
        double minimum = result.Distances.Min(point => point.Distance), maximum = result.Distances.Max(point => point.Distance);
        double mean = result.Distances.Average(point => point.Distance);
        double squareMean = result.Distances.Average(point => point.Distance * point.Distance);
        double deviation = Math.Sqrt(result.Distances.Average(point => (point.Distance - mean) * (point.Distance - mean)));
        if (!EdgeClose(result.Minimum!.Value, minimum) || !EdgeClose(result.Maximum!.Value, maximum) || !EdgeClose(result.Mean!.Value, mean) ||
            !EdgeClose(result.Rms!.Value, Math.Sqrt(squareMean)) || !EdgeClose(result.StandardDeviation!.Value, deviation) || !EdgeClose(result.Range!.Value, maximum - minimum))
            throw new InvalidDataException("Point-to-line statistics do not match the measured distances: " + name);
        double selected = result.Statistic switch
        {
            "minimum" => minimum, "maximum" => maximum, "mean" => mean, "rms" => Math.Sqrt(squareMean), "range" => maximum - minimum,
            _ => throw new InvalidDataException("Unsupported point-to-line statistic: " + name)
        };
        if (!EdgeClose(result.Value!.Value, selected)) throw new InvalidDataException("Point-to-line value does not match the selected statistic: " + name);
        return result;
    }

    private static void ValidateEdgePointOrder(IReadOnlyList<VisionEdgePoint> points, int stationCount, string name)
    {
        int previousStation = -1;
        double previousPosition = double.NegativeInfinity;
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point is null || point.Index != index || point.StationIndex <= previousStation || point.StationIndex >= stationCount ||
                point.Origin is not ("measured" or "interpolated") || !EdgeFinite(point.X, point.Y, point.Gradient, point.PathPosition) ||
                point.PathPosition < previousPosition || point.RawX.HasValue != point.RawY.HasValue ||
                point.RawX is double x && !double.IsFinite(x) || point.RawY is double y && !double.IsFinite(y) ||
                point.Adjusted && (point.RawX is null || point.RawY is null) ||
                !point.Adjusted && point.RawX is double rawX && !EdgeClose(point.X, rawX) ||
                !point.Adjusted && point.RawY is double rawY && !EdgeClose(point.Y, rawY) ||
                point.Origin == "interpolated" && (point.Adjusted || point.Gradient != 0 || point.RawX is not null || point.RawY is not null))
                throw new InvalidDataException("Invalid edge point coordinates, origins or station order: " + name);
            previousStation = point.StationIndex;
            previousPosition = point.PathPosition;
        }
    }
    private static bool EdgeCoordinateSpace(string space, string? plane) =>
        space == "original" || space == "annularUnwrapped" && !string.IsNullOrWhiteSpace(plane);
    private static bool EdgeFinite(params double[] values) => values.All(double.IsFinite);
    private static bool EdgeClose(double first, double second) => Math.Abs(first - second) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(first), Math.Abs(second)));
}

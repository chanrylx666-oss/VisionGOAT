using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

public sealed record VisionGeometricSource
{
    [JsonPropertyName("kind"), JsonRequired] public string Kind { get; init; } = "";
    [JsonPropertyName("point")] public VisionPoint? Point { get; init; }
    [JsonPropertyName("line")] public VisionLine? Line { get; init; }
    [JsonPropertyName("circle")] public VisionCircleGeometry? Circle { get; init; }
}
/// <summary>The original-image endpoints of the selected distance measurement, in pixels.</summary>
public sealed record VisionDistanceSegment(
    [property: JsonPropertyName("start"), JsonRequired] VisionPoint Start,
    [property: JsonPropertyName("end"), JsonRequired] VisionPoint End);

public sealed record VisionGeometricMeasurement
{
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("operation"), JsonRequired] public string Operation { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("unit"), JsonRequired] public string Unit { get; init; } = "";
    [JsonPropertyName("valid"), JsonRequired] public bool Valid { get; init; }
    [JsonPropertyName("parametersAvailable")] public bool ParametersAvailable { get; init; } = true;
    [JsonPropertyName("dataAvailable")] public bool DataAvailable { get; init; } = true;
    [JsonPropertyName("unavailableParameters")] public IReadOnlyList<string>? UnavailableParameters { get; init; }
    [JsonPropertyName("value"), JsonRequired] public double? Value { get; init; }
    [JsonPropertyName("point")] public VisionPoint? Point { get; init; }
    [JsonPropertyName("first"), JsonRequired] public VisionGeometricSource? First { get; init; }
    [JsonPropertyName("second"), JsonRequired] public VisionGeometricSource? Second { get; init; }
    [JsonPropertyName("firstIndex"), JsonRequired] public int FirstIndex { get; init; }
    [JsonPropertyName("secondIndex"), JsonRequired] public int SecondIndex { get; init; }
    [JsonPropertyName("lowerLimit"), JsonRequired] public double LowerLimit { get; init; }
    [JsonPropertyName("upperLimit"), JsonRequired] public double UpperLimit { get; init; }
    [JsonPropertyName("nominalValue")] public double? NominalValue { get; init; }
    [JsonPropertyName("deviation")] public double? Deviation { get; init; }
    [JsonPropertyName("decision"), JsonRequired] public VisionToleranceDecision Decision { get; init; } = null!;
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
    [JsonPropertyName("diagnostics")] public JsonElement? Diagnostics { get; init; }
    [JsonPropertyName("contrast")] public double? Contrast { get; init; }
    [JsonPropertyName("center")] public VisionPoint? Center { get; init; }
    /// <summary>The selected measurement definition; null for older measurement results.</summary>
    [JsonPropertyName("distanceDefinition")] public string? DistanceDefinition { get; init; }
    /// <summary>Acute angle between the selected lines; null when a line is unavailable.</summary>
    [JsonPropertyName("lineAngleDegrees")] public double? LineAngleDegrees { get; init; }
    [JsonPropertyName("parallelAngleToleranceDegrees")] public double? ParallelAngleToleranceDegrees { get; init; }
    /// <summary>The measured points on the selected centers, circumferences or infinite line; null when invalid.</summary>
    [JsonPropertyName("distanceSegment")] public VisionDistanceSegment? DistanceSegment { get; init; }
    [JsonPropertyName("distanceMode")] public string? DistanceMode { get; init; }
    [JsonPropertyName("centerDistance")] public double? CenterDistance { get; init; }
    [JsonPropertyName("edgeDistance")] public double? EdgeDistance { get; init; }
    [JsonPropertyName("relation")] public string? Relation { get; init; }
}
public sealed partial class InspectionResult
{
    public VisionGeometricMeasurement GetGeometricMeasurement(string name)
    {
        if (Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("Measurements require a succeeded run.");
        var json = ResolvePublishedValue(name, "Measurement");
        if (json.TryGetProperty("operation", out var operation) && operation.GetString() is "circleCenterDistance" or "circleEdgeDistance")
            return CircleMeasurement(GetGeometryMeasurement(name));
        var value = json.Deserialize<VisionGeometricMeasurement>(GeometryJsonOptions)!;
        value = value with
        {
            First = value.First?.Circle is null ? value.First : value.First with { Circle = value.First.Circle with { CoordinateSpace = value.CoordinateSpace } },
            Second = value.Second?.Circle is null ? value.Second : value.Second with { Circle = value.Second.Circle with { CoordinateSpace = value.CoordinateSpace } }
        };
        if (value.ResultKind != "geometryMeasurement" || value.Operation is not ("pointDistance" or "pointToLine" or "lineDistance" or "lineCircleDistance" or "lineAngle" or "lineIntersection" or "edgePairWidth") ||
            value.CoordinateSpace is not ("original" or "annularUnwrapped") || value.Unit != (value.Operation == "lineAngle" ? "deg" : "px") || value.Decision is null || !Enum.IsDefined(value.Decision.Status) ||
            !double.IsFinite(value.LowerLimit) || !double.IsFinite(value.UpperLimit) || value.ParametersAvailable && value.LowerLimit > value.UpperLimit || value.Overlay.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid geometric measurement contract: " + name);
        if (value.Valid)
        {
            if (!value.ParametersAvailable || !value.DataAvailable) throw new InvalidDataException("Valid measurement requires available parameters and data.");
            ValidateSource(json.GetProperty("first")); ValidateSource(json.GetProperty("second"));
            if (value.Operation == "lineIntersection")
            {
                ValidatePoint(json.GetProperty("point"));
                if (value.Value is not null) throw new InvalidDataException("An intersection outputs a point, not a scalar.");
            }
            else if (value.Value is not double number || !double.IsFinite(number) || number < 0)
                throw new InvalidDataException("Valid measurement requires a finite scalar.");
        }
        else if (value.Value is not null || value.Point is not null || value.Decision.Status != InspectionDecision.Unknown)
            throw new InvalidDataException("Unavailable measurement must have no value and an Unknown decision.");
        if (value.Operation == "lineDistance")
        {
            if (value.DistanceDefinition != "firstMidpointToSecondLine" ||
                value.ParallelAngleToleranceDegrees is not double tolerance || !double.IsFinite(tolerance) || tolerance < 0 || tolerance > 10 ||
                value.LineAngleDegrees is double angle && (!double.IsFinite(angle) || angle < 0 || angle > 90))
                throw new InvalidDataException("Invalid line distance definition or parallel angle tolerance.");
            if (value.Valid)
            {
                if (value.First!.Kind != "line" || value.Second!.Kind != "line" || value.DistanceSegment is null ||
                    value.LineAngleDegrees is not double measuredAngle || measuredAngle > tolerance)
                    throw new InvalidDataException("Valid line distance requires two nearly parallel lines and a measurement segment.");
                var segment = json.GetProperty("distanceSegment"); ValidatePoint(segment.GetProperty("start")); ValidatePoint(segment.GetProperty("end"));
            }
            else if (value.DistanceSegment is not null)
                throw new InvalidDataException("Invalid line distance must have no measurement segment.");
        }
        if (value.Operation == "lineCircleDistance")
        {
            ValidateCircularDistances(json, value.Valid, value.Value, value.DistanceMode, value.CenterDistance, value.EdgeDistance, value.DistanceSegment);
            if (value.DistanceDefinition != (value.DistanceMode == "edgeDistance" ? "circleBoundaryToLine" : "circleCenterToLine") ||
                value.Valid && (value.First!.Kind != "line" || value.Second!.Kind != "circle"))
                throw new InvalidDataException("Line-circle distance requires a line and a circle with the selected definition.");
        }
        return value;
    }
    private static VisionGeometricMeasurement CircleMeasurement(VisionGeometryMeasurement value) => new()
    {
        ResultKind = value.ResultKind, Operation = value.Operation, CoordinateSpace = value.CoordinateSpace, CoordinatePlane = value.CoordinatePlane, Unit = value.Unit,
        Valid = value.Valid, ParametersAvailable = value.ParametersAvailable, Value = value.Value,
        First = value.First is null ? null : new() { Kind = "circle", Circle = value.First },
        Second = value.Second is null ? null : new() { Kind = "circle", Circle = value.Second },
        FirstIndex = value.FirstIndex, SecondIndex = value.SecondIndex, LowerLimit = value.LowerLimit, UpperLimit = value.UpperLimit,
        NominalValue = value.NominalValue, Deviation = value.Deviation, Decision = value.Decision, Overlay = value.Overlay,
        DistanceMode = value.DistanceMode, DistanceDefinition = value.DistanceDefinition, CenterDistance = value.CenterDistance,
        EdgeDistance = value.EdgeDistance, DistanceSegment = value.DistanceSegment, Relation = value.Relation
    };
    private static void ValidateCircularDistances(JsonElement json, bool valid, double? selected, string? mode, double? center, double? edge, VisionDistanceSegment? segment)
    {
        if (mode is not ("centerDistance" or "edgeDistance")) throw new InvalidDataException("Unsupported circular distance mode.");
        if (valid)
        {
            if (center is not double c || !double.IsFinite(c) || c < 0 || edge is not double e || !double.IsFinite(e) || e < 0 ||
                segment is null || selected != (mode == "edgeDistance" ? e : c))
                throw new InvalidDataException("Valid circular distance requires both finite distances and the selected measurement segment.");
            var points = json.GetProperty("distanceSegment"); ValidatePoint(points.GetProperty("start")); ValidatePoint(points.GetProperty("end"));
        }
        else if (center is not null || edge is not null || segment is not null)
            throw new InvalidDataException("Unavailable circular distance must have no distances or segment.");
    }
    private static void ValidateSource(JsonElement source)
    {
        string kind = source.GetProperty("kind").GetString()!;
        if (kind == "point") ValidatePoint(source.GetProperty("point"));
        else if (kind == "line")
        {
            var line = source.GetProperty("line");
            foreach (string key in new[] { "x1", "y1", "x2", "y2", "length", "angleDegrees" })
                if (!double.IsFinite(line.GetProperty(key).GetDouble())) throw new InvalidDataException("Line coordinates must be finite.");
        }
        else if (kind == "circle")
        {
            var circle = source.GetProperty("circle");
            foreach (string key in new[] { "centerX", "centerY", "radius" })
                if (!double.IsFinite(circle.GetProperty(key).GetDouble())) throw new InvalidDataException("Circle coordinates must be finite.");
            if (circle.GetProperty("radius").GetDouble() <= 0) throw new InvalidDataException("Circle radius must be positive.");
        }
        else throw new InvalidDataException("Unsupported measurement geometry.");
    }
    private static void ValidatePoint(JsonElement point)
    {
        if (!double.IsFinite(point.GetProperty("x").GetDouble()) || !double.IsFinite(point.GetProperty("y").GetDouble()))
            throw new InvalidDataException("Point coordinates must be finite.");
    }
}

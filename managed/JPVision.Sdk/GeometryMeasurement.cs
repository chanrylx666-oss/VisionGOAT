using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>A valid measurement and its optional tolerance decision are independent.</summary>
public sealed record VisionGeometryMeasurement
{
    [JsonPropertyName("parametersAvailable")] public bool ParametersAvailable { get; init; } = true;
    [JsonPropertyName("resultKind"), JsonRequired] public string ResultKind { get; init; } = "";
    [JsonPropertyName("operation"), JsonRequired] public string Operation { get; init; } = "";
    [JsonPropertyName("coordinateSpace"), JsonRequired] public string CoordinateSpace { get; init; } = "";
    [JsonPropertyName("coordinatePlane")] public string? CoordinatePlane { get; init; }
    [JsonPropertyName("unit"), JsonRequired] public string Unit { get; init; } = "";
    [JsonPropertyName("valid"), JsonRequired] public bool Valid { get; init; }
    [JsonPropertyName("value"), JsonRequired] public double? Value { get; init; }
    [JsonPropertyName("first"), JsonRequired] public VisionCircleGeometry? First { get; init; }
    [JsonPropertyName("second"), JsonRequired] public VisionCircleGeometry? Second { get; init; }
    [JsonPropertyName("firstIndex"), JsonRequired] public int FirstIndex { get; init; }
    [JsonPropertyName("secondIndex"), JsonRequired] public int SecondIndex { get; init; }
    [JsonPropertyName("lowerLimit"), JsonRequired] public double LowerLimit { get; init; }
    [JsonPropertyName("upperLimit"), JsonRequired] public double UpperLimit { get; init; }
    [JsonPropertyName("nominalValue")] public double? NominalValue { get; init; }
    [JsonPropertyName("deviation")] public double? Deviation { get; init; }
    [JsonPropertyName("decision"), JsonRequired] public VisionToleranceDecision Decision { get; init; } = null!;
    [JsonPropertyName("overlay"), JsonRequired] public JsonElement Overlay { get; init; }
    [JsonPropertyName("distanceMode")] public string? DistanceMode { get; init; }
    [JsonPropertyName("distanceDefinition")] public string? DistanceDefinition { get; init; }
    [JsonPropertyName("centerDistance")] public double? CenterDistance { get; init; }
    [JsonPropertyName("edgeDistance")] public double? EdgeDistance { get; init; }
    [JsonPropertyName("distanceSegment")] public VisionDistanceSegment? DistanceSegment { get; init; }
    [JsonPropertyName("relation")] public string? Relation { get; init; }
}

public sealed record VisionToleranceDecision(
    [property: JsonPropertyName("status"), JsonRequired, JsonConverter(typeof(JsonStringEnumConverter<InspectionDecision>))] InspectionDecision Status,
    [property: JsonPropertyName("reason"), JsonRequired] string Reason);

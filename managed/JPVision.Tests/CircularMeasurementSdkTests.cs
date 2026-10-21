using JPVision.Sdk;
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CircularMeasurementSdkTests
{
    private static JsonObject Packet(bool lineCircle = true) => new()
    {
        ["resultKind"] = "geometryMeasurement", ["operation"] = lineCircle ? "lineCircleDistance" : "circleCenterDistance",
        ["coordinateSpace"] = "original", ["unit"] = "px", ["valid"] = true, ["value"] = lineCircle ? 8.0 : 10.0,
        ["firstIndex"] = 0, ["secondIndex"] = 0, ["lowerLimit"] = 0.0, ["upperLimit"] = 100.0,
        ["first"] = lineCircle
            ? new JsonObject { ["kind"] = "line", ["line"] = new JsonObject { ["x1"] = -20.0, ["y1"] = 0.0, ["x2"] = 20.0, ["y2"] = 0.0, ["length"] = 40.0, ["angleDegrees"] = 0.0 } }
            : new JsonObject { ["centerX"] = 0.0, ["centerY"] = 10.0, ["radius"] = 2.0 },
        ["second"] = lineCircle
            ? new JsonObject { ["kind"] = "circle", ["circle"] = new JsonObject { ["centerX"] = 0.0, ["centerY"] = 10.0, ["radius"] = 2.0 } }
            : new JsonObject { ["centerX"] = 10.0, ["centerY"] = 10.0, ["radius"] = 2.0 },
        ["distanceMode"] = lineCircle ? "edgeDistance" : "centerDistance",
        ["distanceDefinition"] = lineCircle ? "circleBoundaryToLine" : "circleCenters",
        ["centerDistance"] = 10.0, ["edgeDistance"] = lineCircle ? 8.0 : 6.0,
        ["distanceSegment"] = new JsonObject { ["start"] = new JsonObject { ["x"] = 0.0, ["y"] = lineCircle ? 0.0 : 10.0 }, ["end"] = new JsonObject { ["x"] = lineCircle ? 0.0 : 10.0, ["y"] = lineCircle ? 8.0 : 10.0 } },
        ["decision"] = new JsonObject { ["status"] = "Unknown", ["reason"] = "UnconfiguredTolerance" },
        ["overlay"] = new JsonArray(new JsonObject { ["type"] = "line", ["points"] = new JsonArray(new JsonArray(0.0, 0.0), new JsonArray(0.0, 8.0)) })
    };
    private static InspectionResult Read(JsonObject packet)
    {
        var data = new JsonObject
        {
            ["executionStatus"] = "Succeeded", ["decision"] = "Unknown", ["elapsedMs"] = 1, ["runId"] = "circular-sdk-fixture",
            ["outputs"] = new JsonObject { ["Measurement"] = packet.DeepClone(), ["Overlay"] = packet["overlay"]!.DeepClone() }
        };
        var handleType = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(handleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { handleType, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }
    [Fact]
    public void OlderCircleCenterPacketsRemainReadableThroughBothSdkMethods()
    {
        var packet = Packet(false);
        foreach (string key in new[] { "distanceMode", "distanceDefinition", "centerDistance", "edgeDistance", "distanceSegment" }) packet.Remove(key);
        using var result = Read(packet); var old = result.GetGeometryMeasurement("Measurement"); var unified = result.GetGeometricMeasurement("Measurement");
        Assert.True(old.Valid); Assert.Equal(10, old.Value); Assert.Equal(old.First, unified.First!.Circle); Assert.Equal(old.Second, unified.Second!.Circle);
        Assert.Null(unified.DistanceMode); Assert.Null(unified.DistanceSegment); Assert.Null(unified.EdgeDistance);
    }
    [Theory]
    [InlineData("value")] [InlineData("edgeDistance")] [InlineData("radius")]
    public void InvalidNewCircularContractsAreRejectedAtTheSdkBoundary(string field)
    {
        var packet = Packet();
        if (field == "value") packet["value"] = 10.0;
        if (field == "edgeDistance") packet["edgeDistance"] = null;
        if (field == "radius") packet["second"]!["circle"]!["radius"] = 0;
        using var result = Read(packet); Assert.Throws<InvalidDataException>(() => result.GetGeometricMeasurement("Measurement"));
    }
    [Fact]
    public void MissingOverlayColorDefaultsToGreenWhileExplicitColorsRemainUnchanged()
    {
        var packet = Packet(); using var result = Read(packet); Assert.Equal("#00FF00", Assert.Single(result.GetOverlays("Overlay")).Color);
        packet["overlay"]![0]!["color"] = "#123456"; using var custom = Read(packet); Assert.Equal("#123456", Assert.Single(custom.GetOverlays("Overlay")).Color);
    }
}

using JPVision.Sdk;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

/// <summary>Current and legacy detector contracts without a native runtime or license.</summary>
public sealed class MatchCenterSdkTests
{
    private static JsonObject Packet(bool current = true, bool found = true)
    {
        var feature = new JsonObject { ["kind"] = "circle", ["x"] = 106.75, ["y"] = -17.25, ["radius"] = 18.5 };
        var measurement = new JsonObject
        {
            ["featureKind"] = "circle", ["count"] = found ? 1 : 0, ["found"] = found, ["passed"] = found,
            ["selectedIndex"] = 0, ["selectedValid"] = found, ["selected"] = null
        };
        if (current)
        {
            feature["circle"] = new JsonObject
            {
                ["type"] = "circle", ["coordinateSpace"] = "original", ["centerX"] = 106.75,
                ["centerY"] = -17.25, ["radius"] = 18.5, ["diameter"] = 37
            };
            feature["outputAnnulus"] = Annulus(106.75, -17.25, 16.5, 20.5);
            measurement["position"] = new JsonObject
            {
                ["source"] = "matchCenter", ["centerX"] = 100, ["centerY"] = -20,
                ["offsetX"] = 6, ["offsetY"] = 2, ["searchCenterX"] = 106, ["searchCenterY"] = -18,
                ["angleDegrees"] = 0, ["scale"] = 1, ["transform"] = "translation"
            };
            measurement["caliper"] = new JsonObject
            {
                ["placement"] = "roiBoundary", ["requestedCount"] = 36,
                ["sampledCount"] = found ? 36 : 0, ["retainedCount"] = found ? 32 : 0,
                ["rejectedCount"] = found ? 4 : 0, ["missingCount"] = found ? 0 : 36,
                ["rejectedPoints"] = new JsonArray(), ["guides"] = new JsonArray(),
                ["searchAnnulus"] = Annulus(106, -18, 8, 28)
            };
        }
        if (found) measurement["selected"] = feature.DeepClone();
        var packet = new JsonObject
        {
            ["featureKind"] = "circle", ["count"] = found ? 1 : 0, ["found"] = found, ["passed"] = found,
            ["features"] = found ? new JsonArray(feature) : new JsonArray(),
            ["measurement"] = measurement, ["overlay"] = new JsonArray()
        };
        if (current)
        {
            packet["image"] = new JsonObject
            {
                ["available"] = true, ["originX"] = 60, ["originY"] = -50,
                ["matchPosition"] = new JsonObject
                {
                    ["centerX"] = 100, ["centerY"] = -20, ["angleDegrees"] = 0, ["scale"] = 1
                }
            };
        }
        return packet;
    }

    private static JsonObject Annulus(double x, double y, double inner, double outer) => new()
    {
        ["coordinateSpace"] = "original", ["centerX"] = x, ["centerY"] = y,
        ["innerRadius"] = inner, ["outerRadius"] = outer, ["startAngle"] = 0, ["endAngle"] = 360
    };

    private static InspectionResult Result(JsonObject packet)
    {
        var data = new JsonObject
        {
            ["executionStatus"] = "Succeeded", ["decision"] = "Unknown", ["elapsedMs"] = 1,
            ["runId"] = "match-center-sdk", ["outputs"] = new JsonObject { ["CircleResult"] = packet }
        };
        // A zero safe handle permits testing the public reader without loading a native DLL.
        var handleType = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(handleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { handleType, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }

    [Fact]
    public void PublicResultSeparatesTemplateReferenceFromMeasuredCircleAndBothAnnuli()
    {
        using var result = Result(Packet());
        var packet = result.GetResult<VisionCircle>("CircleResult");
        var circle = Assert.Single(packet.Features);
        Assert.Same(circle, packet.Measurement.Selected);
        Assert.Equal(106.75, circle.Circle.CenterX);
        Assert.Equal(-17.25, circle.Circle.CenterY);
        Assert.Equal("circle", circle.Circle.Type);
        Assert.Equal("original", circle.Circle.CoordinateSpace);
        Assert.Equal(18.5, circle.Circle.Radius);
        Assert.Equal(37, circle.Circle.Diameter);

        var reference = Assert.IsType<VisionPositionReference>(packet.Measurement.Position);
        Assert.Equal("matchCenter", reference.Source);
        Assert.Equal("translation", reference.Transform);
        Assert.Equal(100, reference.CenterX);
        Assert.Equal(-20, reference.CenterY);
        Assert.Equal(6, reference.OffsetX);
        Assert.Equal(2, reference.OffsetY);
        Assert.Equal(106, reference.SearchCenterX);
        Assert.Equal(-18, reference.SearchCenterY);
        Assert.NotEqual(reference.SearchCenterX, circle.Circle.CenterX);

        var search = Assert.IsType<VisionAnnulus>(packet.Measurement.Caliper!.SearchAnnulus);
        Assert.Equal("original", search.CoordinateSpace);
        Assert.Equal(reference.SearchCenterX, search.CenterX);
        Assert.Equal(8, search.InnerRadius);
        Assert.Equal(28, search.OuterRadius);
        var output = Assert.IsType<VisionAnnulus>(circle.OutputAnnulus);
        Assert.Equal(circle.CenterX, output.CenterX);
        Assert.Equal(circle.CenterY, output.CenterY);
        Assert.Equal(16.5, output.InnerRadius);
        Assert.Equal(20.5, output.OuterRadius);
        Assert.Equal(0, output.StartAngle);
        Assert.Equal(360, output.EndAngle);
        Assert.Equal(4, output.OuterRadius - output.InnerRadius);
        Assert.Equal(100, packet.MatchPosition!.CenterX);
        Assert.Equal(-20, packet.MatchPosition.CenterY);
        Assert.Equal(1, packet.MatchPosition.Scale);
    }

    [Fact]
    public void LegacyResultKeepsDerivedMeasuredGeometryWithoutInventingOptionalContext()
    {
        using var result = Result(Packet(current: false));
        var packet = result.GetResult<VisionCircle>("CircleResult");
        var circle = Assert.Single(packet.Features);
        Assert.Equal(106.75, circle.Circle.CenterX);
        Assert.Equal(37, circle.Circle.Diameter);
        Assert.Null(circle.OutputAnnulus);
        Assert.Null(packet.Measurement.Position);
        Assert.Null(packet.Measurement.Caliper);
        Assert.Null(packet.MatchPosition);
    }

    [Fact]
    public void UnfittedCircleCanKeepSearchReferenceWithoutInventingMeasuredOutput()
    {
        using var result = Result(Packet(found: false));
        var packet = result.GetResult<VisionCircle>("CircleResult");
        Assert.Empty(packet.Features);
        Assert.False(packet.Measurement.SelectedValid);
        Assert.Null(packet.Measurement.Selected);
        Assert.NotNull(packet.Measurement.Position);
        Assert.Equal(106, packet.Measurement.Caliper!.SearchAnnulus!.CenterX);
        Assert.Equal(0, packet.Measurement.Caliper.RetainedCount);
    }

    [Fact]
    public void DerivedCircleFollowsUpdatedMeasurementRatherThanCachedGeometry()
    {
        var circle = new VisionCircle(12, 20, 5);
        var refined = circle with { X = 12.75, Radius = 5.25 };
        Assert.Equal(12.75, refined.Circle.CenterX);
        Assert.Equal(10.5, refined.Circle.Diameter);
        Assert.Equal(12, circle.Circle.CenterX);
        Assert.Equal(10, circle.Circle.Diameter);
    }

    [Fact]
    public void OptionalJsonNullsKeepLegacyReadersUsable()
    {
        var source = Packet();
        source["measurement"]!["position"] = null;
        source["measurement"]!["caliper"]!["searchAnnulus"] = null;
        source["image"]!["matchPosition"] = null;
        using var result = Result(source);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        Assert.Null(packet.Measurement.Position);
        Assert.Null(packet.Measurement.Caliper!.SearchAnnulus);
        Assert.Null(packet.MatchPosition);
    }

    [Fact]
    public void TypedReferencesAndRegionsRemainUsableAfterResultDisposal()
    {
        var result = Result(Packet());
        var packet = result.GetResult<VisionCircle>("CircleResult");
        result.Dispose();
        Assert.Equal(100, packet.MatchPosition!.CenterX);
        Assert.Equal(106, packet.Measurement.Position!.SearchCenterX);
        Assert.Equal(16.5, packet.Features[0].OutputAnnulus!.InnerRadius);
    }

    [Theory]
    [InlineData("outputAnnulus", "innerRadius")]
    [InlineData("searchAnnulus", "outerRadius")]
    [InlineData("position", "centerX")]
    [InlineData("matchPosition", "scale")]
    public void PresentButIncompleteOptionalStructuresDoNotInventZeroCoordinates(string structure, string missing)
    {
        var source = Packet();
        switch (structure)
        {
            case "outputAnnulus": source["features"]![0]!["outputAnnulus"]!.AsObject().Remove(missing); break;
            case "searchAnnulus": source["measurement"]!["caliper"]!["searchAnnulus"]!.AsObject().Remove(missing); break;
            case "position": source["measurement"]!["position"]!.AsObject().Remove(missing); break;
            case "matchPosition": source["image"]!["matchPosition"]!.AsObject().Remove(missing); break;
        }
        using var result = Result(source);
        Assert.Throws<JsonException>(() =>
        {
            var packet = result.GetResult<VisionCircle>("CircleResult");
            _ = packet.MatchPosition;
        });
    }
}

using JPVision.Sdk;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

/// <summary>Exercises the managed JSON boundary without loading native code or requiring a license.</summary>
public sealed class ResultPacketSdkTests
{
    private static JsonObject Packet(string kind = "circle", bool empty = false, int selectedIndex = 0)
    {
        var feature = kind switch
        {
            "circle" => new JsonObject { ["kind"] = kind, ["x"] = 127.25, ["y"] = 91.5, ["radius"] = 20.75 },
            "line" => new JsonObject { ["kind"] = kind, ["x1"] = 40.5, ["y1"] = 30.25, ["x2"] = 140.5, ["y2"] = 30.25, ["length"] = 100, ["angleDegrees"] = 0 },
            "match" => new JsonObject { ["kind"] = kind, ["x"] = 40.5, ["y"] = 30.25, ["width"] = 20, ["height"] = 10, ["centerX"] = 50.5, ["centerY"] = 35.25, ["angleDegrees"] = 0, ["scale"] = 1, ["score"] = .98,
                ["corners"] = new JsonArray(new JsonObject { ["x"] = 40.5, ["y"] = 30.25 }, new JsonObject { ["x"] = 60.5, ["y"] = 30.25 }, new JsonObject { ["x"] = 60.5, ["y"] = 40.25 }, new JsonObject { ["x"] = 40.5, ["y"] = 40.25 }) },
            "region" => new JsonObject { ["kind"] = kind, ["area"] = 200, ["perimeter"] = 60, ["centroidX"] = 50, ["centroidY"] = 35, ["centroidValid"] = true, ["circularity"] = .7, ["x"] = 40, ["y"] = 30, ["width"] = 20, ["height"] = 10 },
            _ => throw new ArgumentException("Unknown fixture kind.")
        };
        int count = empty ? 0 : 1;
        var measurement = new JsonObject
        {
            ["featureKind"] = kind, ["count"] = count, ["found"] = count > 0, ["passed"] = true,
            ["selectedIndex"] = selectedIndex, ["selectedValid"] = selectedIndex < count,
            ["selected"] = selectedIndex < count ? feature.DeepClone() : null
        };
        return new()
        {
            ["featureKind"] = kind, ["features"] = empty ? new JsonArray() : new JsonArray(feature),
            ["measurement"] = measurement, ["count"] = count, ["found"] = count > 0, ["passed"] = true,
            ["overlay"] = empty ? new JsonArray() : new JsonArray(
                new JsonObject { ["type"] = "circle", ["x"] = 127.25, ["y"] = 91.5, ["radius"] = 20.75, ["color"] = "#FF3038", ["lineWidth"] = 2 },
                new JsonObject { ["type"] = "text", ["x"] = 100, ["y"] = 50, ["text"] = "R20.75\nD41.50", ["color"] = "#FFFFFF" })
        };
    }

    private static InspectionResult ReadFixture(JsonObject packet, string status = "Succeeded", bool retainNode = true)
    {
        var ports = new JsonObject { ["count"] = packet["count"]!.DeepClone() };
        if (retainNode) ports["result"] = packet.DeepClone();
        var data = new JsonObject
        {
            ["executionStatus"] = status, ["decision"] = "Unknown", ["elapsedMs"] = 1,
            ["runId"] = "managed-packet-fixture", ["outputs"] = new JsonObject { ["ObjectResult"] = packet.DeepClone() },
            ["nodeOutputs"] = new JsonObject { ["detect"] = ports }
        };
        // The internal constructor normally receives a native ResultHandle. A zero safe handle
        // isolates JSON reading here; disposing it never invokes a native release function.
        var handleType = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(handleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { handleType, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }

    [Fact]
    public void CirclePacketPreservesMeasurementsAllFeaturesAndOriginalOverlayFields()
    {
        using var result = ReadFixture(Packet());
        var packet = result.GetResult<VisionCircle>("ObjectResult");
        Assert.Equal("circle", packet.FeatureKind); Assert.Equal(1, packet.Count);
        Assert.True(packet.Found); Assert.True(packet.Passed);
        var circle = Assert.Single(packet.Features);
        Assert.Equal(127.25, circle.CenterX); Assert.Equal(91.5, circle.CenterY); Assert.Equal(20.75, circle.Radius);
        Assert.Equal(circle, packet.Measurement.Selected); Assert.True(packet.Measurement.SelectedValid);
        Assert.Equal(2, packet.Overlay.GetArrayLength());
        Assert.Equal("circle", packet.Overlay[0].GetProperty("type").GetString());
        Assert.False(packet.Overlay[0].TryGetProperty("kind", out _));
        Assert.Equal("R20.75\nD41.50", packet.Overlay[1].GetProperty("text").GetString());
        Assert.Equal(result.Outputs.GetProperty("ObjectResult").GetProperty("overlay").GetRawText(), packet.Overlay.GetRawText());
        Assert.Throws<NotSupportedException>(() => ((IList<VisionCircle>)packet.Features).Clear());
    }

    [Fact]
    public void AllSupportedKindsReadIntoTheirCorrespondingGeometryTypes()
    {
        using var line = ReadFixture(Packet("line"));
        Assert.Equal(100, Assert.Single(line.GetResult<VisionLine>("ObjectResult").Features).Length);
        using var match = ReadFixture(Packet("match"));
        Assert.Equal(.98, Assert.Single(match.GetResult<VisionMatch>("ObjectResult").Features).Score);
        using var region = ReadFixture(Packet("region"));
        Assert.Equal(200, Assert.Single(region.GetResult<VisionRegion>("ObjectResult").Features).Area);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 9)]
    public void MissingSelectionNeverInventsTargetCoordinates(bool empty, int index)
    {
        using var result = ReadFixture(Packet(empty: empty, selectedIndex: index));
        var packet = result.GetResult<VisionCircle>("ObjectResult");
        Assert.Equal(empty ? 0 : 1, packet.Count); Assert.Equal(!empty, packet.Found);
        Assert.True(packet.Passed); // A configured count range may legitimately accept zero targets.
        Assert.False(packet.Measurement.SelectedValid); Assert.Null(packet.Measurement.Selected);
        if (empty) { Assert.Empty(packet.Features); Assert.Equal(0, packet.Overlay.GetArrayLength()); }
        else Assert.Single(packet.Features);
    }

    [Fact]
    public void NodeReaderHonorsRetentionAndMissingNodeErrors()
    {
        using var retained = ReadFixture(Packet());
        Assert.Equal(127.25, Assert.Single(retained.GetNodeResult<VisionCircle>("detect").Features).CenterX);
        Assert.Throws<KeyNotFoundException>(() => retained.GetNodeResult<VisionCircle>("not-executed"));
        using var omitted = ReadFixture(Packet(), retainNode: false);
        Assert.Equal(1, omitted.GetResult<VisionCircle>("ObjectResult").Count);
        var error = Assert.Throws<KeyNotFoundException>(() => omitted.GetNodeResult<VisionCircle>("detect"));
        Assert.Contains("Debug", error.Message); Assert.Contains("preview", error.Message);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public void UnsuccessfulRunsRejectBothPacketReaders(string status)
    {
        using var result = ReadFixture(Packet(), status);
        Assert.Throws<InvalidOperationException>(() => result.GetResult<VisionCircle>("ObjectResult"));
        Assert.Throws<InvalidOperationException>(() => result.GetNodeResult<VisionCircle>("detect"));
    }

    [Fact]
    public void GeometryKindIsCheckedEvenWhenTheFeatureListIsEmpty()
    {
        using var result = ReadFixture(Packet(empty: true));
        Assert.Throws<InvalidDataException>(() => result.GetResult<VisionLine>("ObjectResult"));
        Assert.Throws<ArgumentException>(() => result.GetResult<object>("ObjectResult"));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("feature")]
    [InlineData("selectedKind")]
    [InlineData("count")]
    [InlineData("countType")]
    [InlineData("found")]
    [InlineData("passed")]
    [InlineData("selectedIndex")]
    [InlineData("selectedValidity")]
    [InlineData("overlay")]
    [InlineData("missingMeasurementField")]
    public void InconsistentOrIncompletePacketIsRejected(string fault)
    {
        var packet = Packet();
        switch (fault)
        {
            case "kind": packet["featureKind"] = "line"; break;
            case "feature": packet["features"]![0]!["kind"] = "line"; break;
            case "selectedKind": packet["measurement"]!["selected"]!["kind"] = "line"; break;
            case "count": packet["count"] = 2; break;
            case "countType": packet["count"] = "one"; break;
            case "found": packet["found"] = false; break;
            case "passed": packet["passed"] = false; break;
            case "selectedIndex": packet["measurement"]!["selectedIndex"] = 4; break;
            case "selectedValidity": packet["measurement"]!["selectedValid"] = false; break;
            case "overlay": packet["overlay"] = new JsonObject(); break;
            case "missingMeasurementField": packet["measurement"]!.AsObject().Remove("selected"); break;
        }
        using var result = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => result.GetResult<VisionCircle>("ObjectResult"));
    }

    [Fact]
    public void ReturnedPacketRemainsReadableAfterInspectionResultDisposal()
    {
        var result = ReadFixture(Packet());
        var packet = result.GetNodeResult<VisionCircle>("detect");
        result.Dispose();
        Assert.Equal("circle", packet.Overlay[0].GetProperty("type").GetString());
        Assert.Equal(20.75, packet.Measurement.Selected!.Radius);
    }

    private static void ReadAsKind(InspectionResult result, string kind)
    {
        switch (kind)
        {
            case "circle": result.GetResult<VisionCircle>("ObjectResult"); break;
            case "line": result.GetResult<VisionLine>("ObjectResult"); break;
            case "match": result.GetResult<VisionMatch>("ObjectResult"); break;
            case "region": result.GetResult<VisionRegion>("ObjectResult"); break;
            default: throw new ArgumentException("Unknown fixture kind.");
        }
    }

    [Theory]
    [InlineData("circle", "x")]
    [InlineData("circle", "y")]
    [InlineData("circle", "radius")]
    [InlineData("line", "x1")]
    [InlineData("line", "y1")]
    [InlineData("line", "x2")]
    [InlineData("line", "y2")]
    [InlineData("line", "length")]
    [InlineData("line", "angleDegrees")]
    [InlineData("match", "centerX")]
    [InlineData("match", "scale")]
    [InlineData("match", "score")]
    [InlineData("match", "corners")]
    [InlineData("region", "area")]
    [InlineData("region", "centroidX")]
    [InlineData("region", "centroidValid")]
    [InlineData("region", "width")]
    public void MissingGeometryFieldsCannotDeserializeIntoInventedZeroValues(string kind, string field)
    {
        var packet = Packet(kind);
        packet["features"]![0]!.AsObject().Remove(field);
        using var result = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => ReadAsKind(result, kind));
    }

    [Theory]
    [InlineData("circle", "radius")]
    [InlineData("line", "x1")]
    [InlineData("match", "centerY")]
    [InlineData("region", "centroidX")]
    public void MissingSelectedGeometryFieldsAreAlsoRejected(string kind, string field)
    {
        var packet = Packet(kind);
        packet["measurement"]!["selected"]!.AsObject().Remove(field);
        using var result = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => ReadAsKind(result, kind));
    }

    [Theory]
    [InlineData("circle", "radius")]
    [InlineData("line", "x1")]
    [InlineData("match", "centerY")]
    [InlineData("region", "centroidX")]
    public void SelectedTargetMustMatchItsFeatureListEntry(string kind, string field)
    {
        var packet = Packet(kind);
        packet["measurement"]!["selected"]![field] = 300.25;
        using var result = ReadFixture(packet);
        var error = Assert.Throws<InvalidDataException>(() => ReadAsKind(result, kind));
        Assert.Contains("features[selectedIndex]", error.Message);
    }

    [Fact]
    public void CaliperGeometryKeepsDetailedArraysAndSelectedUsesTheValidatedFeature()
    {
        var packet = Packet();
        var circle = packet["features"]![0]!.AsObject();
        circle["x"] = -127.25;
        circle["edgePoints"] = new JsonArray(new JsonObject { ["x"] = -106.5, ["y"] = 91.5 }, new JsonObject { ["x"] = -127.25, ["y"] = 112.25 },
            new JsonObject { ["x"] = -148, ["y"] = 91.5 }, new JsonObject { ["x"] = -127.25, ["y"] = 70.75 });
        circle["rmsError"] = .125;
        var caliper = new JsonObject
        {
            ["placement"] = "roiBoundary", ["requestedCount"] = 4, ["sampledCount"] = 4,
            ["retainedCount"] = 4, ["rejectedCount"] = 0, ["missingCount"] = 0,
            ["rejectedPoints"] = new JsonArray(), ["guides"] = new JsonArray(
                new JsonObject { ["x"] = -106.5, ["y"] = 91.5, ["normalX"] = 1, ["normalY"] = 0, ["width"] = 7, ["height"] = 32 },
                new JsonObject { ["x"] = -127.25, ["y"] = 112.25, ["normalX"] = 0, ["normalY"] = 1, ["width"] = 7, ["height"] = 32 },
                new JsonObject { ["x"] = -148, ["y"] = 91.5, ["normalX"] = -1, ["normalY"] = 0, ["width"] = 7, ["height"] = 32 },
                new JsonObject { ["x"] = -127.25, ["y"] = 70.75, ["normalX"] = 0, ["normalY"] = -1, ["width"] = 7, ["height"] = 32 })
        };
        circle["caliper"] = caliper.DeepClone();
        packet["measurement"]!["caliper"] = caliper;
        packet["measurement"]!["selected"] = circle.DeepClone();
        using var result = ReadFixture(packet);
        var typed = result.GetResult<VisionCircle>("ObjectResult");
        var feature = Assert.Single(typed.Features);
        Assert.Equal(-127.25, feature.CenterX); Assert.Equal(4, feature.EdgePoints.Count);
        Assert.Equal(.125, feature.RmsError); Assert.Equal("roiBoundary", typed.Measurement.Caliper!.Placement);
        Assert.Null(typed.Measurement.Caliper.FitSucceeded); Assert.Null(typed.Measurement.Caliper.FitFailureReason);
        Assert.Same(feature, typed.Measurement.Selected);
    }

    [Fact]
    public void InvalidNumericGeometryAndCornerArraysAreRejected()
    {
        var packet = Packet(); packet["features"]![0]!["radius"] = 0;
        using var zeroRadius = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => zeroRadius.GetResult<VisionCircle>("ObjectResult"));
        packet = Packet(); packet["features"]![0]!["x"] = JsonNode.Parse("1e400");
        using var nonFinite = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => nonFinite.GetResult<VisionCircle>("ObjectResult"));
        packet = Packet("match"); packet["features"]![0]!["corners"]!.AsArray().RemoveAt(0);
        using var invalidCorners = ReadFixture(packet);
        Assert.Throws<InvalidDataException>(() => invalidCorners.GetResult<VisionMatch>("ObjectResult"));
    }

    [Fact]
    public void CaliperEdgePointsCannotInventAZeroRmsWhenTheFieldIsMissing()
    {
        var packet = Packet();
        packet["features"]![0]!["edgePoints"] = new JsonArray(new JsonObject { ["x"] = 148, ["y"] = 91.5 });
        using var result = ReadFixture(packet);
        var error = Assert.Throws<InvalidDataException>(() => result.GetResult<VisionCircle>("ObjectResult"));
        Assert.Contains("rmsError", error.Message);
    }

    [Fact]
    public void EquivalentSelectedGeometryMayUseADifferentJsonPropertyOrder()
    {
        var packet = Packet("match");
        var selected = packet["measurement"]!["selected"]!.AsObject();
        var reordered = new JsonObject();
        foreach (var field in selected.Reverse()) reordered[field.Key] = field.Value?.DeepClone();
        packet["measurement"]!["selected"] = reordered;
        using var result = ReadFixture(packet);
        var typed = result.GetResult<VisionMatch>("ObjectResult");
        Assert.Same(Assert.Single(typed.Features), typed.Measurement.Selected);
    }
}

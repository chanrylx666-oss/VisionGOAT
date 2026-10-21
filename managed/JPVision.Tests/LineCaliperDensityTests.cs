using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class LineCaliperDensityTests : IDisposable
{
    private readonly string store;
    public LineCaliperDensityTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "line-caliper-density-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string to) => new()
    { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = "input" };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(string type, JsonObject parameters) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "line-caliper-density", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("line", type, parameters)),
        ["connections"] = new JsonArray(Edge("source", "line")),
        ["outputs"] = new JsonObject { ["Image"] = Output("line"), ["Result"] = Output("line", "result", "Result") }
    };
    private static JsonObject Parameters(int count = 1024, int width = 10918, int height = 287) => new()
    {
        ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
        ["roi"] = RoiRegion.RotatedRectangle(width / 2.0, height / 2.0, width, height, 0).ToJson(),
        ["caliperCount"] = count, ["projectionWidth"] = 5, ["searchLength"] = 30.0, ["sigma"] = 1.0,
        ["polarity"] = "darkToLight", ["edgeThreshold"] = 10.0, ["minimumCoverage"] = .8, ["maximumDeviation"] = 1.0,
        ["showOverlay"] = false
    };
    private string Save(JsonObject recipe)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, bool requireSuccess = true)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); var result = await session.RunAsync(frame);
        if (requireSuccess) Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static VisionFrame Gray(int width, int height, Func<int, int, byte> pixel)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = pixel(x, y);
        return VisionFrame.CopyFrom(bytes, width, height, width, VisionPixelFormat.Gray8, 700, -90);
    }

    [Fact]
    public async Task ALongStripCanFitItsActualEdgeUsing1024Calipers()
    {
        var p = Parameters(); using var frame = Gray(10918, 287, (_, y) => y >= 143 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe("FindLines", p), frame); var packet = result.GetResult<VisionLine>("Result");
        var line = Assert.Single(packet.Features); var details = packet.Measurement.Caliper!;
        Assert.True(details.FitSucceeded); Assert.Equal(1024, details.RequestedCount); Assert.Equal(1024, line.EdgePoints.Count);
        Assert.Equal(1024, details.ValidSamplingCalipers); Assert.Equal(128, details.FitIterationLimit);
        Assert.InRange(details.InitialMaximumDeviation!.Value, 0, 1); Assert.InRange(details.InitialRmsError!.Value, 0, 1);
        Assert.InRange(details.MaximumRetainedDeviation!.Value, 0, 1); Assert.Equal(line.RmsError, details.FinalRmsError!.Value, 8);
        Assert.Equal(10918.0 / 1024, details.EffectiveSamplingSpacing!.Value, 8);
        Assert.Equal("original", packet.CoordinateSpace); Assert.InRange(line.Length, 10900, 10918);
        Assert.All(line.EdgePoints, point => { Assert.InRange(point.X, 700, 11617); Assert.InRange(point.Y, 52, 54); });
    }

    private static JsonObject ForType(JsonObject parameters, string type)
    {
        if (type == "LineCaliper") parameters.Remove("method"); return parameters;
    }
    private static JsonElement Details(InspectionResult result) => result.GetOutput<JsonElement>("Result").GetProperty("measurement").GetProperty("caliper");

    [Theory]
    [InlineData("FindLines", "guide")] [InlineData("FindLines", "roiCenter")] [InlineData("FindLines", "roiBoundary")]
    [InlineData("LineCaliper", "guide")] [InlineData("LineCaliper", "roiCenter")] [InlineData("LineCaliper", "roiBoundary")]
    public async Task DenseLineSamplingUsesTheSelectedReferencePathInOriginalCoordinates(string type, string placement)
    {
        var p = ForType(Parameters(1024, 400, 200), type); p["caliperPlacement"] = placement; p["searchLength"] = 60.0;
        p["roi"] = placement switch
        {
            "guide" => RoiRegion.None.ToJson(),
            "roiCenter" => RoiRegion.RotatedRectangle(200, 80, 320, 40, 0).ToJson(),
            _ => RoiRegion.Rectangle(40, 80, 320, 60).ToJson()
        };
        p["polarity"] = "any";
        using var frame = Gray(400, 200, (_, y) => y >= 80 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(type, p), frame); var packet = result.GetResult<VisionLine>("Result");
        var line = Assert.Single(packet.Features); var details = packet.Measurement.Caliper!;
        Assert.True(details.FitSucceeded); Assert.Equal(1024, details.RequestedCount); Assert.Equal(1024, details.SampledCount);
        Assert.Equal(1024, line.EdgePoints.Count); Assert.Equal(placement, details.Placement);
        Assert.All(line.EdgePoints, point => { Assert.InRange(point.X, 700, 1099); Assert.InRange(point.Y, -11, -9); });
        var raw = Details(result); Assert.InRange(raw.GetProperty("fitIterations").GetInt32(), 1, 128);
        Assert.Equal(128, raw.GetProperty("fitIterationLimit").GetInt32());
        Assert.InRange(raw.GetProperty("maximumRetainedDeviation").GetDouble(), 0, 1.0);
        Assert.Equal(line.RmsError, raw.GetProperty("finalRmsError").GetDouble(), 8);
        double expectedSpacing = placement == "guide" ? .9 * 400 / 1023 : 320.0 / 1024;
        Assert.Equal(expectedSpacing, raw.GetProperty("effectiveSamplingSpacing").GetDouble(), 7);
    }

    [Theory]
    [InlineData("FindLines")] [InlineData("LineCaliper")]
    public async Task MaximumLineCountSamplesEveryRequestedPositionWithoutTruncation(string type)
    {
        var p = ForType(Parameters(32768, 32768, 32), type);
        p["roi"] = RoiRegion.RotatedRectangle(16384, 16, 32766, 20, 0).ToJson(); p["projectionWidth"] = 1;
        using var frame = Gray(32768, 32, (_, y) => y >= 16 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(type, p), frame); var packet = result.GetResult<VisionLine>("Result");
        var line = Assert.Single(packet.Features); var details = packet.Measurement.Caliper!;
        Assert.True(details.FitSucceeded); Assert.Equal(32768, details.RequestedCount); Assert.Equal(32768, details.SampledCount);
        Assert.Equal(32768, details.RetainedCount); Assert.Equal(32768, line.EdgePoints.Count); Assert.Equal(0, details.MissingCount);
        Assert.Equal(32768, details.Guides.Count); Assert.InRange(line.Length, 32764, 32766);
        Assert.All(line.EdgePoints, point => { Assert.InRange(point.X, 701, 33467); Assert.InRange(point.Y, -75, -73); });
        Assert.Equal(32766.0 / 32768, Details(result).GetProperty("effectiveSamplingSpacing").GetDouble(), 8);
    }

    [Fact]
    public async Task MoreThan253RequestedRemovalsAreAppliedBeforeTheDenseLineIsRefitted()
    {
        var p = Parameters(1024, 1200, 160); p["roi"] = RoiRegion.RotatedRectangle(600, 80, 1100, 40, 0).ToJson();
        p["removePointCount"] = 300; p["minimumCoverage"] = .5;
        using var frame = Gray(1200, 160, (_, y) => y >= 80 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe("FindLines", p), frame); var packet = result.GetResult<VisionLine>("Result");
        var line = Assert.Single(packet.Features); var details = packet.Measurement.Caliper!;
        Assert.True(details.FitSucceeded); Assert.Equal(300, details.RequestedRemovalCount); Assert.Equal(300, details.RejectedCount);
        Assert.Equal(1024, details.SampledCount); Assert.Equal(724, details.RetainedCount); Assert.Equal(724, line.EdgePoints.Count);
        Assert.Equal(300, details.RejectedPoints.Count); Assert.InRange(line.RmsError, 0, .01);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DenseSamplingExplainsMissingEdgesAndRejectedCurvesWithoutInventingAFittedLine(bool curved)
    {
        var p = Parameters(1024, 400, 160); p["roi"] = RoiRegion.RotatedRectangle(200, 80, 360, 60, 0).ToJson();
        p["maximumDeviation"] = .1; p["minimumCoverage"] = .95;
        using var frame = Gray(400, 160, (x, y) => curved && y >= 80 + 12 * Math.Sin(x / 18.0) ? (byte)230 : (byte)20);
        using var result = await Run(Recipe("FindLines", p), frame); var packet = result.GetResult<VisionLine>("Result"); var details = packet.Measurement.Caliper!;
        Assert.False(details.FitSucceeded); Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
        Assert.Equal(curved ? "InsufficientInliers" : "InsufficientEdgePoints", details.FitFailureReason);
        Assert.InRange(details.FitIterations!.Value, 0, 128); Assert.Equal(128, Details(result).GetProperty("fitIterationLimit").GetInt32());
        if (curved)
        {
            Assert.True(details.SampledCount >= 1000); Assert.True(details.RejectedCount > 0);
            Assert.True(Details(result).GetProperty("initialMaximumDeviation").GetDouble() > .1);
            Assert.True(Details(result).GetProperty("initialRmsError").GetDouble() > .1);
        }
        else Assert.Equal(0, details.SampledCount);
    }

    [Theory]
    [InlineData("FindLines", "guide")] [InlineData("FindLines", "roiCenter")] [InlineData("FindLines", "roiBoundary")]
    [InlineData("LineCaliper", "guide")] [InlineData("LineCaliper", "roiCenter")] [InlineData("LineCaliper", "roiBoundary")]
    public async Task EveryLineSamplingPathRejectsMoreThan16MiProjectionSamplesBeforeProcessing(string type, string placement)
    {
        var p = ForType(Parameters(32768, 400, 200), type); p["caliperPlacement"] = placement;
        p["projectionWidth"] = 63; p["searchLength"] = 512.0;
        p["roi"] = placement == "guide" ? RoiRegion.None.ToJson() : placement == "roiCenter" ?
            RoiRegion.RotatedRectangle(200, 100, 400, 512, 0).ToJson() : RoiRegion.Rectangle(0, 0, 400, 200).ToJson();
        using var frame = Gray(400, 200, (_, y) => y >= 100 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(type, p), frame, requireSuccess: false);
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Equal(5, result.Data.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("budget", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Outputs.EnumerateObject());
    }

    [Theory]
    [InlineData("MeasureLineDistance")] [InlineData("MeasureGeometry")]
    public async Task InlineAAndBLineSelectionsCanBothUse1024Calipers(string type)
    {
        var p = new JsonObject(); if (type == "MeasureGeometry") p["operation"] = "lineDistance";
        foreach (string side in new[] { "first", "second" })
        {
            p[side + "Roi"] = RoiRegion.RotatedRectangle(600, side == "first" ? 50 : 130, 1100, 24, 0).ToJson();
            p[side + "CaliperCount"] = 1024; p[side + "ProjectionWidth"] = 5; p[side + "Sigma"] = 1.0;
            p[side + "Polarity"] = side == "first" ? "darkToLight" : "lightToDark";
            p[side + "MinimumCoverage"] = .8; p[side + "MaximumDeviation"] = 1.0;
        }
        using var frame = Gray(1200, 180, (_, y) => y >= 50 && y < 130 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(type, p), frame); var distance = result.GetGeometricMeasurement("Result");
        Assert.True(distance.Valid); Assert.InRange(distance.Value!.Value, 79.5, 80.5); Assert.Equal("original", distance.CoordinateSpace);
        Assert.Equal(1024, distance.First!.Line!.EdgePoints.Count); Assert.Equal(1024, distance.Second!.Line!.EdgePoints.Count);
        Assert.InRange(distance.First.Line.Y1, -41, -39); Assert.InRange(distance.Second.Line.Y1, 39, 41);
    }

    [Fact]
    public async Task InlineMeasurementsAlsoRejectOversizedLineSamplingWork()
    {
        var p = new JsonObject
        {
            ["firstRoi"] = RoiRegion.RotatedRectangle(200, 80, 400, 512, 0).ToJson(),
            ["secondRoi"] = RoiRegion.RotatedRectangle(200, 160, 320, 24, 0).ToJson(),
            ["firstCaliperCount"] = 32768, ["firstProjectionWidth"] = 63, ["firstSearchLength"] = 512.0
        };
        using var frame = Gray(400, 240, (_, y) => y >= 80 && y < 160 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe("MeasureLineDistance", p), frame, requireSuccess: false);
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(5, result.Data.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("budget", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LineSchemasExpandWhileCircleSchemasAndDefaultsRemainUnchanged()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog());
        JsonObject Setting(string type, string name) => catalog[type].Parameters.OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == name);
        foreach (string type in new[] { "FindLines", "LineCaliper" })
        {
            Assert.Equal(32768, Setting(type, "caliperCount")["max"]!.GetValue<int>());
            Assert.Equal(24, Setting(type, "caliperCount")["default"]!.GetValue<int>());
            Assert.Equal(32766, Setting(type, "removePointCount")["max"]!.GetValue<int>());
            Assert.Equal(0, Setting(type, "removePointCount")["default"]!.GetValue<int>());
        }
        foreach (string type in new[] { "FindCircles", "CircleCaliper" })
        {
            Assert.Equal(256, Setting(type, "caliperCount")["max"]!.GetValue<int>());
            Assert.Equal(253, Setting(type, "removePointCount")["max"]!.GetValue<int>());
            Assert.Equal(24, Setting(type, "caliperCount")["default"]!.GetValue<int>());
        }
        Assert.Equal(32768, Setting("MeasureLineDistance", "firstCaliperCount")["max"]!.GetValue<int>());
        Assert.Equal(32768, Setting("MeasureLineCircleDistance", "firstCaliperCount")["max"]!.GetValue<int>());
        Assert.Equal(256, Setting("MeasureCircleDistance", "firstCaliperCount")["max"]!.GetValue<int>());
        Assert.Equal(256, Setting("MeasureLineCircleDistance", "secondCaliperCount")["max"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("FindLines", "caliperCount", 32769)] [InlineData("LineCaliper", "caliperCount", 32769)]
    [InlineData("FindLines", "removePointCount", 32767)] [InlineData("LineCaliper", "removePointCount", 32767)]
    [InlineData("FindCircles", "caliperCount", 257)] [InlineData("CircleCaliper", "caliperCount", 257)]
    public void CountsBeyondTheSelectedDetectorLimitsAreRejected(string type, string name, int value)
    {
        var p = type.Contains("Circle", StringComparison.Ordinal) ? new JsonObject { ["roi"] = RoiRegion.Circle(100, 100, 50).ToJson() } : ForType(Parameters(), type);
        p[name] = value; using var engine = new VisionEngine(store);
        var error = Assert.Throws<ArgumentException>(() => engine.LoadRecipe(Save(Recipe(type, p)))); Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData("MeasureCircleDistance", "first")] [InlineData("MeasureLineCircleDistance", "second")]
    [InlineData("MeasureGeometry", "first")]
    public void InlineCircleSelectionDoesNotInheritTheExpandedLineLimit(string type, string circleSide)
    {
        var p = new JsonObject
        {
            ["firstRoi"] = type == "MeasureLineCircleDistance" ? RoiRegion.RotatedRectangle(100, 50, 150, 24, 0).ToJson() : RoiRegion.Circle(100, 100, 40).ToJson(),
            ["secondRoi"] = RoiRegion.Circle(200, 100, 30).ToJson(), [circleSide + "CaliperCount"] = 257
        };
        if (type == "MeasureGeometry") { p["operation"] = "pointDistance"; p["firstObjectType"] = "circle"; }
        using var engine = new VisionEngine(store); var error = Record.Exception(() => { using var recipe = engine.LoadRecipe(Save(Recipe(type, p))); });
        Assert.NotNull(error); Assert.True(error is ArgumentException or VisionException, error!.ToString());
    }

    [Fact]
    public void ARemovalCountWithinTheNewNumericRangeMustStillLeaveEnoughPointsForCoverage()
    {
        var p = Parameters(32768, 32768, 32); p["projectionWidth"] = 1; p["removePointCount"] = 32766; p["minimumCoverage"] = .1;
        using var engine = new VisionEngine(store); var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe("FindLines", p))));
        Assert.Contains("too few points", error.Message);
    }

    [Fact]
    public void DenseLineCaliperPreviewUsesEveryRequestedWindowWhileCirclePreviewKeepsItsOwnLimit()
    {
        var roi = RoiRegion.RotatedRectangle(5459, 143.5, 10918, 287, 0); var p = Parameters();
        var windows = CaliperLayout.Create(roi, p, false, 10918, 287, allowPartialCircle: true);
        Assert.Equal(1024, windows.Count); Assert.Equal(10918.0 / 1024, windows[1].X - windows[0].X, 8);
        p["caliperCount"] = 32768; p["projectionWidth"] = 1; p["roi"] = RoiRegion.RotatedRectangle(5459, 100, 10918, 20, 0).ToJson();
        Assert.Equal(32768, CaliperLayout.Create(RoiRegion.FromJson(p["roi"]), p, false, 10918, 287, allowPartialCircle: true).Count);
        p["caliperPlacement"] = "roiBoundary";
        Assert.Empty(CaliperLayout.Create(RoiRegion.Circle(100, 100, 50), p, true, 400, 240, allowPartialCircle: true));
    }

    [Fact]
    public void PreviewDensityPreservesBothEndsWithoutReducingTheActualSamplingLayout()
    {
        foreach (int count in new[] { 0, 1, 256, 257, 1024, 32768 })
        {
            var indices = CaliperLayout.PreviewWindowIndices(count);
            Assert.Equal(Math.Min(count, 256), indices.Count);
            Assert.Equal(indices.Count, indices.Distinct().Count());
            if (count == 0) continue;
            Assert.Equal(0, indices[0]); Assert.Equal(count - 1, indices[^1]);
            Assert.All(indices, index => Assert.InRange(index, 0, count - 1));
            Assert.True(indices.SequenceEqual(indices.OrderBy(index => index)));
        }
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

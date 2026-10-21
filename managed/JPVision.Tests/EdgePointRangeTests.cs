using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class EdgePointRangeTests : IDisposable
{
    private readonly string store;
    public EdgePointRangeTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "edge-point-ranges-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonObject parameters) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "edge-point-ranges", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("points", "FindEdgePoints", parameters)),
        ["connections"] = new JsonArray(Edge("source", "points")),
        ["outputs"] = new JsonObject { ["Image"] = Output("points"), ["Points"] = Output("points", "result", "Result") }
    };
    private static JsonObject Parameters(double centerX = 1000, double centerY = 50, double width = 1800, double height = 60) => new()
    {
        ["roi"] = RoiRegion.RotatedRectangle(centerX, centerY, width, height, 0).ToJson(),
        ["projectionWidth"] = 5, ["sigma"] = 1.0, ["polarity"] = "darkToLight", ["edgeThreshold"] = 10.0,
        ["minimumCoverage"] = .5, ["showOverlay"] = false
    };
    private string Save(JsonObject recipe)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); var result = await session.RunAsync(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static VisionFrame Gray(int width, int height, Func<int, int, byte> pixel)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = pixel(x, y);
        return VisionFrame.CopyFrom(bytes, width, height, width, VisionPixelFormat.Gray8, 700, -90);
    }

    [Fact]
    public async Task SpacingAbove256PixelsProducesAllConfiguredStations()
    {
        var p = Parameters(); p["sampleSpacing"] = 600.0;
        using var frame = Gray(2000, 120, (_, y) => y >= 50 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(p), frame); var points = result.GetEdgePoints("Points");
        Assert.True(points.Valid); Assert.Equal(4, points.StationCount); Assert.Equal(4, points.Count);
        Assert.Equal(new[] { 800.0, 1400.0, 2000.0, 2600.0 }, points.Points.Select(point => point.X));
        Assert.All(points.Points, point => Assert.InRange(point.Y, -41, -39));
    }

    [Theory]
    [InlineData("mean")] [InlineData("median")]
    public async Task A255PixelProjectionWindowDetectsTheSameRealEdge(string statistic)
    {
        var p = Parameters(500, 60, 600, 40); p["sampleSpacing"] = 200.0;
        p["projectionWidth"] = 255; p["projectionStatistic"] = statistic;
        using var frame = Gray(1000, 120, (_, y) => y >= 60 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(p), frame); var points = result.GetEdgePoints("Points");
        Assert.True(points.Valid); Assert.Equal(4, points.StationCount); Assert.Equal(4, points.Count);
        Assert.All(points.Points, point => Assert.InRange(point.Y, -31, -29));
    }

    [Theory]
    [InlineData(4096)] [InlineData(32768)]
    public async Task ASearchHeightAbove2048IsUsableWhenItsSamplingBudgetIsSmall(int height)
    {
        var p = Parameters(32, height / 2.0, 32, height); p["sampleSpacing"] = 16.0; p["projectionWidth"] = 1;
        using var frame = Gray(64, height, (_, y) => y >= height / 2 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(p), frame); var points = result.GetEdgePoints("Points");
        Assert.True(points.Valid); Assert.Equal(3, points.StationCount); Assert.Equal(3, points.Count);
        Assert.All(points.Points, point => Assert.InRange(point.Y, height / 2.0 - 91, height / 2.0 - 89));
    }

    [Theory]
    [InlineData(10918)] [InlineData(32768)]
    public async Task MoreThanTenThousandStationsAndTheirDistancesRemainComplete(int width)
    {
        var p = Parameters(width / 2.0, 30, width, 20);
        p["sampleSpacing"] = 1.0; p["projectionWidth"] = 1; p["maximumPoints"] = width + 1;
        var definition = Recipe(p);
        var line = new JsonObject
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
            ["roi"] = RoiRegion.RotatedRectangle(width / 2.0, 80, width, 20, 0).ToJson(),
            ["caliperCount"] = 32, ["projectionWidth"] = 5, ["sigma"] = 1.0,
            ["polarity"] = "lightToDark", ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .8, ["showOverlay"] = false
        };
        definition["nodes"]!.AsArray().Add(Node("line", "FindLines", line));
        definition["nodes"]!.AsArray().Add(Node("distance", "MeasurePointLineDistances", new() { ["showOverlay"] = false }));
        definition["connections"]!.AsArray().Add(Edge("points", "line"));
        definition["connections"]!.AsArray().Add(Edge("line", "distance"));
        definition["outputs"]!["Distances"] = Output("distance", "result", "Result");
        definition["outputs"]!["DistanceImage"] = Output("distance");
        using var frame = Gray(width, 120, (_, y) => y >= 30 && y < 80 ? (byte)230 : (byte)20);
        using var result = await Run(definition, frame); var points = result.GetEdgePoints("Points");
        var distances = result.GetPointLineDistances("Distances");
        Assert.True(points.Valid); Assert.Equal(width + 1, points.StationCount); Assert.Equal(width - 1, points.Count);
        Assert.True(points.Count > 10000); Assert.Equal(2, points.MissedCount); Assert.True(distances.Valid);
        Assert.Equal(points.Count, distances.PointCount); Assert.Equal(points.Count, distances.Count);
        Assert.Equal(points.Points.Select(point => point.StationIndex), distances.Distances.Select(point => point.StationIndex));
        Assert.Equal("original", points.CoordinateSpace); Assert.Equal("original", distances.CoordinateSpace);
        Assert.All(distances.Distances, point => Assert.InRange(point.Distance, 49.5, 50.5));
        Assert.InRange(distances.Mean!.Value, 49.5, 50.5); Assert.Empty(result.GetOverlays("DistanceImage"));
        Assert.Equal(points.Count, result.GetEdgePoints("DistanceImage").Count);
        Assert.Equal(distances.Count, result.GetPointLineDistances("DistanceImage").Count);
    }

    [Fact]
    public async Task MaximumSpacingFitsTheMaximumLegalRoiWidthWithoutInventingTheOutsideEndpoint()
    {
        var p = Parameters(16384, 30, 32768, 20); p["sampleSpacing"] = 32768.0; p["projectionWidth"] = 1;
        using var frame = Gray(32768, 60, (_, y) => y >= 30 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(p), frame); var points = result.GetEdgePoints("Points");
        Assert.True(points.Valid); Assert.Equal(2, points.StationCount); Assert.Equal(1, points.Count);
        Assert.Equal(0, Assert.Single(points.Points).StationIndex); Assert.Equal(700, points.Points[0].X);
    }

    [Fact]
    public void TheWiderLegalRangesDoNotChangePhysicalLimitsOrExistingDefaults()
    {
        var spec = new NodeCatalog(VisionEngine.GetNodeCatalog())["FindEdgePoints"];
        JsonObject Setting(string name) => spec.Parameters.OfType<JsonObject>().Single(parameter => parameter["name"]!.GetValue<string>() == name);
        void Range(string name, double minimum, double maximum, double value)
        {
            var p = Setting(name); Assert.Equal(minimum, p["min"]!.GetValue<double>());
            Assert.Equal(maximum, p["max"]!.GetValue<double>()); Assert.Equal(value, p["default"]!.GetValue<double>());
        }
        Range("sampleSpacing", 1, 32768, 4); Range("projectionWidth", 1, 255, 5); Range("maximumPoints", 2, 32769, 10000);
        Range("sigma", 0, 10, 1); Range("edgeThreshold", 0, 255, 10); Range("minimumCoverage", 0, 1, .5);
        Assert.True(Setting("projectionWidth")["odd"]!.GetValue<bool>());
        Assert.Equal(32768, ParameterEditor.Parse(Setting("sampleSpacing"), "32768").GetValue<double>());
        Assert.Equal(255, ParameterEditor.Parse(Setting("projectionWidth"), "255").GetValue<long>());
        Assert.Equal(32769, ParameterEditor.Parse(Setting("maximumPoints"), "32769").GetValue<long>());
        Assert.Throws<ArgumentException>(() => ParameterEditor.Parse(Setting("projectionWidth"), "254"));
    }

    [Theory]
    [InlineData("sampleSpacing", 32769)] [InlineData("sampleSpacing", 0)]
    [InlineData("projectionWidth", 256)] [InlineData("maximumPoints", 32770)] [InlineData("maximumPoints", 1)]
    [InlineData("sigma", 11)] [InlineData("edgeThreshold", 256)] [InlineData("minimumCoverage", 2)]
    public void ValuesAboveTheActualParameterLimitsAreStillRejected(string name, int value)
    {
        var p = Parameters(); p[name] = value; using var engine = new VisionEngine(store);
        Exception error = name is "projectionWidth" or "maximumPoints"
            ? Assert.Throws<ArgumentException>(() => engine.LoadRecipe(Save(Recipe(p))))
            : Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe(p))));
        Assert.Contains(name, error.Message);
    }

    [Theory]
    [InlineData("evenWidth")] [InlineData("height")] [InlineData("samplingBudget")]
    [InlineData("stationLimit")] [InlineData("sigmaSupport")]
    public void InvalidSearchGeometryAndOverBudgetConfigurationsFailBeforeSampling(string condition)
    {
        var p = Parameters();
        switch (condition)
        {
            case "evenWidth": p["projectionWidth"] = 254; break;
            case "height": p["roi"]!["height"] = 32769.0; break;
            case "samplingBudget":
                p["roi"] = RoiRegion.RotatedRectangle(16384, 16384, 32768, 32768, 0).ToJson();
                p["sampleSpacing"] = 1.0; p["projectionWidth"] = 255; p["maximumPoints"] = 32769; break;
            case "stationLimit":
                p["roi"] = RoiRegion.RotatedRectangle(16384, 30, 32768, 20, 0).ToJson();
                p["sampleSpacing"] = 1.0; p["projectionWidth"] = 1; p["maximumPoints"] = 32768; break;
            case "sigmaSupport": p["roi"]!["height"] = 8.0; p["sigma"] = 10.0; break;
        }
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe(p))));
        Assert.Contains(condition switch
        {
            "evenWidth" => "odd", "height" => "range", "samplingBudget" => "16 Mi", "stationLimit" => "maximumPoints", _ => "smoothing"
        }, error.Message);
    }

    [Fact]
    public async Task AZeroGradientThresholdWithTheWidestProjectionCannotInventPointsInAFlatImage()
    {
        var p = Parameters(500, 60, 600, 40); p["sampleSpacing"] = 200.0; p["projectionWidth"] = 255;
        p["edgeThreshold"] = 0.0; p["minimumCoverage"] = 0.0;
        using var frame = Gray(1000, 120, (_, _) => 100); using var result = await Run(Recipe(p), frame);
        var points = result.GetEdgePoints("Points"); Assert.False(points.Valid); Assert.True(points.DataAvailable); Assert.Empty(points.Points);
    }

    [Fact]
    public void SamplingEstimateKeepsTheTrueStationCountWhenTheConfiguredMaximumWouldRejectIt()
    {
        var roi = RoiRegion.RotatedRectangle(5459, 30, 10918, 20, 0);
        var p = Parameters(5459, 30, 10918, 20); p["sampleSpacing"] = 1.0; p["projectionWidth"] = 1;
        var plan = CaliperLayout.DescribeEdgePoints(roi, p);
        Assert.Equal(10919, plan.StationCount); Assert.Equal(21, plan.ProfileLength); Assert.Equal(10919L * 21, plan.ProjectedSamples);
        Assert.Equal(10000, plan.MaximumPoints); Assert.False(plan.CanExecute); Assert.Equal("TooManySamplingStations", plan.Reason);
        Assert.Empty(CaliperLayout.EdgePoints(roi, p));
        p["maximumPoints"] = 10919; var allowed = CaliperLayout.DescribeEdgePoints(roi, p);
        Assert.True(allowed.CanExecute); Assert.Null(allowed.Reason); Assert.Equal(10919, CaliperLayout.EdgePoints(roi, p).Count);
    }

    [Fact]
    public void SamplingEstimateShowsTheFullWorkBudgetWithoutClampingLargeRoiDimensions()
    {
        var roi = RoiRegion.RotatedRectangle(16384, 16384, 32768, 32768, 0);
        var p = Parameters(16384, 16384, 32768, 32768); p["sampleSpacing"] = 1.0; p["projectionWidth"] = 255; p["maximumPoints"] = 32769;
        var plan = CaliperLayout.DescribeEdgePoints(roi, p);
        Assert.Equal(32769, plan.StationCount); Assert.Equal(32769, plan.ProfileLength);
        Assert.Equal(32769L * 32769 * 255, plan.ProjectedSamples); Assert.Equal(12, plan.MinimumSearchHeight);
        Assert.False(plan.CanExecute); Assert.Equal("SamplingBudgetExceeded", plan.Reason);
        Assert.Empty(CaliperLayout.EdgePoints(roi, p));
    }

    [Fact]
    public void SamplingEstimateAndCaliperPreviewAcceptSparseMaximumHeightWindows()
    {
        var roi = RoiRegion.RotatedRectangle(32, 16384, 32, 32768, 0);
        var p = Parameters(32, 16384, 32, 32768); p["sampleSpacing"] = 16.0; p["projectionWidth"] = 1;
        var plan = CaliperLayout.DescribeEdgePoints(roi, p); var windows = CaliperLayout.EdgePoints(roi, p);
        Assert.True(plan.CanExecute); Assert.Equal(3, plan.StationCount); Assert.Equal(32769, plan.ProfileLength);
        Assert.Equal(3L * 32769, plan.ProjectedSamples); Assert.Equal(3, windows.Count);
        Assert.All(windows, window => { Assert.Equal(32768, window.Height); Assert.Equal(1, window.Width); });
    }

    [Fact]
    public void PositionedPreviewKeepsAnExpandedTeachingStripWhenTheSamplingBudgetAllowsIt()
    {
        var roi = RoiRegion.RotatedRectangle(20000, 30, 40000, 20, 0);
        var p = Parameters(20000, 30, 40000, 20); p["projectionWidth"] = 1; p["maximumPoints"] = 20000;
        var plan = CaliperLayout.DescribeEdgePoints(roi, p);
        Assert.True(plan.CanExecute); Assert.Equal(10001, plan.StationCount);
        Assert.Equal(10001L * 21, plan.ProjectedSamples);
        Assert.Equal(10001, CaliperLayout.EdgePoints(roi, p).Count);
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

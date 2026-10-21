using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class MeasurementTests : IDisposable
{
    private readonly string store;
    public MeasurementTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Project root missing.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Definition(JsonObject? parameters = null, bool branch = false)
    {
        JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
        JsonObject Edge(string node, string port, string target) => new() { ["fromNode"] = node, ["fromPort"] = port, ["toNode"] = target, ["toPort"] = "input" };
        JsonObject Output(string node, string port, string type) => new() { ["node"] = node, ["port"] = port, ["type"] = type };
        var p = new JsonObject { ["minimumRadius"] = 25, ["maximumRadius"] = 35, ["voteThreshold"] = 18, ["minimumCount"] = 2, ["maximumCount"] = 2 };
        if (parameters is not null) foreach (var pair in parameters) p[pair.Key] = pair.Value?.DeepClone();
        var nodes = new JsonArray(Node("source", "ExternalImage"), Node("circle", "FindCircles", p));
        var edges = new JsonArray(Edge("source", "image", "circle"));
        var outputs = new JsonObject
        {
            ["Features"] = Output("circle", "features", "Features"), ["CircleMeasurement"] = Output("circle", "measurement", "Measurement"),
            ["ObjectCount"] = Output("circle", "count", "Int"), ["Found"] = Output("circle", "found", "Bool"),
            ["InspectionPassed"] = Output("circle", "passed", "Bool"), ["ResultOverlay"] = Output("circle", "overlay", "Overlay")
        };
        if (branch)
        {
            nodes.Add(Node("judge", "CompareNumber", new() { ["expected"] = 2 }));
            nodes.Add(Node("counter", "CountFeatures"));
            edges.Add(Edge("circle", "count", "judge")); edges.Add(Edge("circle", "features", "counter"));
            outputs["BranchPassed"] = Output("judge", "value", "Bool"); outputs["BranchCount"] = Output("counter", "value", "Int");
        }
        return new() { ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "inspection", ["recipeId"] = "direct-measurement-test", ["recipeVersion"] = 1,
            ["nodes"] = nodes, ["connections"] = edges, ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = outputs, ["decisionOutput"] = "InspectionPassed", ["resources"] = new JsonArray() };
    }
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private static VisionFrame Image(bool empty = false, int originX = 0, int originY = 0)
    {
        byte[] pixels = new byte[360 * 240];
        if (!empty) for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
            if ((x - 100) * (x - 100) + (y - 110) * (y - 110) <= 900 || (x - 250) * (x - 250) + (y - 125) * (y - 125) <= 900) pixels[y * 360 + x] = 240;
        return VisionFrame.CopyFrom(pixels, 360, 240, 360, VisionPixelFormat.Gray8, originX, originY);
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, bool debug = false)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = debug });
    }
    [Fact]
    public async Task OneDetectorPublishesMeasurementsCountDecisionAndOverlayWithoutDebug()
    {
        using var frame = Image(); using var result = await Run(Definition(branch: true), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Equal(2, result.GetOutput<int>("ObjectCount")); Assert.True(result.GetOutput<bool>("Found"));
        Assert.True(result.GetOutput<bool>("BranchPassed")); Assert.Equal(2, result.GetOutput<int>("BranchCount"));
        var measurement = result.GetOutput<JsonElement>("CircleMeasurement");
        Assert.True(measurement.GetProperty("selectedValid").GetBoolean()); Assert.Equal(2, measurement.GetProperty("count").GetInt32());
        var circle = measurement.GetProperty("selected"); Assert.InRange(circle.GetProperty("centerX").GetDouble(), 98, 102);
        Assert.InRange(circle.GetProperty("centerY").GetDouble(), 108, 112);
        double radius = circle.GetProperty("radius").GetDouble(); Assert.Equal(radius * 2, circle.GetProperty("diameter").GetDouble(), 8);
        Assert.Equal(Math.PI * radius * radius, circle.GetProperty("area").GetDouble(), 8); Assert.Equal(2 * Math.PI * radius, circle.GetProperty("perimeter").GetDouble(), 8);
        Assert.Equal(2, result.GetCircles().Count); Assert.Equal(2, result.GetOverlays().Count(x => x.Kind == "circle"));
        Assert.Equal(measurement.GetRawText(), result.Data.GetProperty("measurements").GetProperty("circle").GetRawText());
        Assert.False(result.Data.TryGetProperty("debugValues", out _));
        var typed = result.GetMeasurement<VisionCircle>("CircleMeasurement");
        Assert.Equal(2, typed.Count); Assert.NotNull(typed.Selected); Assert.InRange(typed.Selected.CenterX, 98, 102);
        Assert.Equal(typed.Selected.Radius * 2, typed.Selected.Diameter, 8);
        Assert.Equal(typed.Selected, result.GetNodeMeasurement<VisionCircle>("circle").Selected);
        Assert.Throws<InvalidDataException>(() => result.GetNodeMeasurement<VisionLine>("circle"));
        Assert.Throws<KeyNotFoundException>(() => result.GetNodeMeasurement<VisionCircle>("unexecuted"));
    }
    [Theory]
    [InlineData(true, 0)] [InlineData(false, 8)]
    public async Task MissingSelectionIsNullAndDoesNotInventCoordinates(bool empty, int index)
    {
        using var frame = Image(empty); using var result = await Run(Definition(new() { ["resultIndex"] = index }), frame);
        var m = result.GetOutput<JsonElement>("CircleMeasurement"); Assert.False(m.GetProperty("selectedValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("selected").ValueKind);
        Assert.Equal(!empty, result.GetOutput<bool>("Found")); Assert.Equal(empty ? InspectionDecision.Fail : InspectionDecision.Pass, result.Decision);
        if (empty) { Assert.Empty(result.GetCircles()); Assert.Empty(result.GetOverlays()); }
    }
    [Fact]
    public async Task SelectedTargetReportsImageOriginAndModuleRoiOffset()
    {
        using var frame = Image(originX: 700, originY: -40);
        using var result = await Run(Definition(new() { ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 50, ["y"] = 60, ["width"] = 270, ["height"] = 140 }, ["resultIndex"] = 1 }), frame);
        var selected = result.GetOutput<JsonElement>("CircleMeasurement").GetProperty("selected");
        Assert.InRange(selected.GetProperty("x").GetDouble(), 948, 952); Assert.InRange(selected.GetProperty("y").GetDouble(), 83, 87);
    }
    [Fact]
    public async Task IntegratedMarkingOptionsDoNotChangeDetection()
    {
        using var frame = Image(); using var annotated = await Run(Definition(new() { ["labelResults"] = true, ["overlayColor"] = "#FF0000", ["overlayLineWidth"] = 3 }), frame);
        Assert.Equal(2, annotated.GetCircles().Count); Assert.Equal(2, annotated.GetOverlays().Count(x => x.Kind == "text"));
        Assert.All(annotated.GetOverlays(), o => Assert.Equal("#FF0000", o.Color));
        using var hidden = await Run(Definition(new() { ["showOverlay"] = false }), frame);
        Assert.Equal(2, hidden.GetCircles().Count); Assert.Empty(hidden.GetOverlays()); Assert.Equal(InspectionDecision.Pass, hidden.Decision);
    }
    [Fact]
    public async Task ZeroCountCanBeExplicitlyRequiredAndProcessingRemainsUnknown()
    {
        using var frame = Image(empty: true); var definition = Definition(new() { ["minimumCount"] = 0, ["maximumCount"] = 0 });
        using var inspection = await Run(definition, frame); Assert.Equal(InspectionDecision.Pass, inspection.Decision);
        definition["kind"] = "processing"; definition["decisionOutput"] = "";
        using var processing = await Run(definition, frame); Assert.Equal(InspectionDecision.Unknown, processing.Decision); Assert.True(processing.GetOutput<bool>("InspectionPassed"));
    }
    [Fact]
    public void InvalidCountRangeIsRejectedDuringLoad()
    {
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Definition(new() { ["minimumCount"] = 3, ["maximumCount"] = 2 }))));
        Assert.Contains("Minimum count must not exceed maximum count", error.Message);
    }
    private static JsonObject CaliperDefinition(string type, bool wrongPolarity = false)
    {
        var definition = Definition(); var node = definition["nodes"]![1]!.AsObject();
        node["type"] = type;
        var p = new JsonObject { ["caliperCount"] = 36, ["projectionWidth"] = 7, ["searchLength"] = 40,
            ["removePointCount"] = 4, ["maximumDeviation"] = 2.0, ["minimumCoverage"] = 0.5,
            ["minimumCount"] = 1, ["maximumCount"] = 1,
            ["polarity"] = wrongPolarity ? "darkToLight" : "lightToDark" };
        if (type != "LineCaliper") { p["radius"] = 45; p["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 180, ["cy"] = 120, ["radius"] = 75 }; }
        else { p["polarity"] = "darkToLight"; p["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 30, ["y"] = 80, ["width"] = 290, ["height"] = 80 }; }
        if (type == "FindCircles") p["method"] = "caliper";
        node["parameters"] = p; return definition;
    }
    private static VisionFrame CaliperImage(bool line = false)
    {
        byte[] pixels = new byte[360 * 240];
        for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
            pixels[y * 360 + x] = (line ? y >= 120 : (x - 180) * (x - 180) + (y - 120) * (y - 120) <= 2500) ? (byte)230 : (byte)15;
        return VisionFrame.CopyFrom(pixels, 360, 240, 360, VisionPixelFormat.Gray8);
    }
    [Theory]
    [InlineData("FindCircles")] [InlineData("CircleCaliper")] [InlineData("LineCaliper")]
    public async Task AdjustableCalipersRemoveRequestedWorstPointsAndReportActualCounts(string type)
    {
        using var frame = CaliperImage(type == "LineCaliper"); using var result = await Run(CaliperDefinition(type), frame);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        VisionCaliperDetails? stats;
        if (type == "LineCaliper")
        {
            var m = result.GetNodeMeasurement<VisionLine>("circle"); stats = m.Caliper;
            Assert.NotNull(m.Selected); Assert.InRange(m.Selected.Y1, 118.8, 120.2); Assert.InRange(m.Selected.RmsError, 0, 0.5);
            Assert.Equal(stats!.RetainedCount, m.Selected.EdgePoints.Count);
        }
        else
        {
            var m = result.GetMeasurement<VisionCircle>("CircleMeasurement"); stats = m.Caliper;
            Assert.NotNull(m.Selected); Assert.InRange(m.Selected.Radius, 49, 51); Assert.InRange(m.Selected.RmsError, 0, 0.5);
            Assert.All(m.Selected.EdgePoints, p => Assert.InRange(Math.Abs(Math.Sqrt(Math.Pow(p.X - m.Selected.X, 2) + Math.Pow(p.Y - m.Selected.Y, 2)) - m.Selected.Radius), 0, 2));
            Assert.Equal(stats!.RetainedCount, m.Selected.EdgePoints.Count);
            Assert.NotNull(m.Selected.Caliper);
        }
        Assert.NotNull(stats); Assert.Equal(36, stats.RequestedCount); Assert.Equal(36, stats.SampledCount);
        Assert.Equal(32, stats.RetainedCount); Assert.Equal(4, stats.RejectedCount); Assert.Equal(0, stats.MissingCount);
        Assert.Equal(4, stats.RejectedPoints.Count);
    }
    [Fact]
    public async Task FailedCaliperSelectionStillReportsSamplingDiagnosticsWithoutFakeCircle()
    {
        using var frame = CaliperImage(); using var result = await Run(CaliperDefinition("FindCircles", wrongPolarity: true), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Fail, result.Decision);
        var m = result.GetNodeMeasurement<VisionCircle>("circle"); Assert.Null(m.Selected); Assert.False(m.Found);
        Assert.NotNull(m.Caliper); Assert.Equal(36, m.Caliper.MissingCount); Assert.Equal(0, m.Caliper.SampledCount);
    }
    [Theory]
    [InlineData("projectionWidth", 6)] [InlineData("removePointCount", 35)] [InlineData("searchLength", 8)]
    public void InvalidCaliperDimensionsAndRejectionBudgetAreRejected(string parameter, int value)
    {
        var definition = CaliperDefinition("FindCircles"); definition["nodes"]![1]!["parameters"]![parameter] = value;
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
    }
    [Fact]
    public async Task ADownstreamFailureClearsDirectMeasurements()
    {
        var definition = Definition(); var nodes = definition["nodes"]!.AsArray();
        nodes.Add(new JsonObject { ["id"] = "badCrop", ["type"] = "CropRect", ["version"] = 1, ["parameters"] = new JsonObject { ["x"] = 500, ["y"] = 0, ["width"] = 40, ["height"] = 40 } });
        definition["connections"]!.AsArray().Add(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "badCrop", ["toPort"] = "input" });
        definition["outputs"]!["Z_BadImage"] = new JsonObject { ["node"] = "badCrop", ["port"] = "image", ["type"] = "Image" };
        using var frame = Image(); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.Outputs.EnumerateObject()); Assert.Empty(result.Data.GetProperty("measurements").EnumerateObject());
        Assert.Throws<InvalidOperationException>(() => result.GetNodeMeasurement<VisionCircle>("circle"));
    }
    [Fact]
    public async Task RemovingBadBoundarySamplesImprovesTheMeasuredCircleFit()
    {
        byte[] pixels = new byte[360 * 240];
        for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
        {
            double dx = x - 180, dy = y - 120;
            double radius = Math.Abs(Math.Atan2(dy, dx)) < .15 ? 63 : 50;
            pixels[y * 360 + x] = dx * dx + dy * dy <= radius * radius ? (byte)230 : (byte)15;
        }
        using var frame = VisionFrame.CopyFrom(pixels, 360, 240, 360, VisionPixelFormat.Gray8);
        var definition = CaliperDefinition("FindCircles"); var p = definition["nodes"]![1]!["parameters"]!.AsObject();
        p["caliperCount"] = 48; p["projectionWidth"] = 1; p["searchLength"] = 60; p["maximumDeviation"] = 100; p["removePointCount"] = 0;
        using var original = await Run(definition, frame); var biased = original.GetNodeMeasurement<VisionCircle>("circle").Selected!;
        p["removePointCount"] = 3;
        using var corrected = await Run(definition, frame); var m = corrected.GetNodeMeasurement<VisionCircle>("circle");
        Assert.NotNull(m.Selected); Assert.InRange(m.Selected.Radius, 49.2, 50.8); Assert.InRange(m.Selected.CenterX, 179.2, 180.8);
        Assert.True(m.Selected.RmsError < biased.RmsError * .5); Assert.Equal(3, m.Caliper!.RejectedCount);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

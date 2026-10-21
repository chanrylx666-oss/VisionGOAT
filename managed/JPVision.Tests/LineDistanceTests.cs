using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class LineDistanceTests : IDisposable
{
    private readonly string store;
    public LineDistanceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/line-distance-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string port, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Parameters(JsonObject d, string id) => d["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();
    private static JsonObject Definition(double degrees = 0, double skew = 0, bool reverse = false, bool generic = false)
    {
        double r = degrees * Math.PI / 180, nx = -Math.Sin(r), ny = Math.Cos(r);
        JsonObject Line(double side, double along, double angle, bool reversed) => new()
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
            ["roi"] = RoiRegion.RotatedRectangle(220 + nx * side + Math.Cos(r) * along, 180 + ny * side + Math.Sin(r) * along, side < 0 ? 220 : 160, 30, angle).ToJson(),
            ["caliperCount"] = 32, ["projectionWidth"] = 5, ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .8,
            ["polarity"] = side < 0 || reversed ? "darkToLight" : "lightToDark"
        };
        var p = new JsonObject { ["toleranceEnabled"] = true, ["lowerLimit"] = 71.5, ["upperLimit"] = 72.5 };
        if (generic) p["operation"] = "lineDistance";
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "line-distance", ["recipeVersion"] = 1,
            ["kind"] = "inspection", ["decisionOutput"] = "Decision", ["resources"] = new JsonArray(),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("a", "FindLines", Line(-36, -15, degrees, false)),
                Node("b", "FindLines", Line(36, 35, degrees + skew + (reverse ? 180 : 0), reverse)),
                Node("measure", generic ? "MeasureGeometry" : "MeasureLineDistance", p)),
            ["connections"] = new JsonArray(Edge("source", "image", "a"), Edge("source", "image", "b"),
                Edge("a", "features", "measure", "first"), Edge("b", "features", "measure", "second")),
            ["outputs"] = new JsonObject
            {
                ["Distance"] = new JsonObject { ["node"] = "measure", ["port"] = "result", ["type"] = "Result" },
                ["Decision"] = new JsonObject { ["node"] = "measure", ["port"] = "decision", ["type"] = "Decision" },
                ["Overlay"] = new JsonObject { ["node"] = "measure", ["port"] = "overlay", ["type"] = "Overlay" }
            }
        };
    }
    private static VisionFrame Frame(double degrees = 0, double skew = 0, bool blank = false)
    {
        const int w = 440, h = 360; var pixels = new byte[w * h];
        double r = degrees * Math.PI / 180, s = (degrees + skew) * Math.PI / 180;
        double bx = 220 - Math.Sin(r) * 36 + Math.Cos(r) * 35, by = 180 + Math.Cos(r) * 36 + Math.Sin(r) * 35;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double a = -(x - 220) * Math.Sin(r) + (y - 180) * Math.Cos(r) + 36;
            double b = -(x - bx) * Math.Sin(s) + (y - by) * Math.Cos(s);
            pixels[y * w + x] = blank ? (byte)20 : (byte)Math.Clamp(125 + Math.Min(a, -b) * 180, 20, 230);
        }
        return VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80);
    }
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject d, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = false });
    }
    [Theory]
    [InlineData(0, false, false)] [InlineData(28, false, false)] [InlineData(90, false, false)]
    [InlineData(-40, true, false)] [InlineData(28, true, true)]
    public async Task TwoIndependentFindLinesReturnPerpendicularDistanceInOriginalCoordinates(double degrees, bool reverse, bool generic)
    {
        using var frame = Frame(degrees); using var result = await Run(Definition(degrees, reverse: reverse, generic: generic), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        var m = result.GetGeometricMeasurement("Distance"); Assert.True(m.Valid); Assert.Equal("lineDistance", m.Operation);
        Assert.Equal("px", m.Unit); Assert.InRange(m.Value!.Value, 71.7, 72.3);
        var raw = result.GetOutput<JsonElement>("Distance");
        Assert.Equal("firstMidpointToSecondLine", raw.GetProperty("distanceDefinition").GetString());
        Assert.Equal("firstMidpointToSecondLine", m.DistanceDefinition); Assert.NotNull(m.DistanceSegment); Assert.NotNull(m.LineAngleDegrees);
        var start = raw.GetProperty("distanceSegment").GetProperty("start"); var end = raw.GetProperty("distanceSegment").GetProperty("end");
        double ax = start.GetProperty("x").GetDouble(), ay = start.GetProperty("y").GetDouble(), bx = end.GetProperty("x").GetDouble(), by = end.GetProperty("y").GetDouble();
        Assert.Equal((m.First!.Line!.X1 + m.First.Line.X2) / 2, ax, 8); Assert.Equal((m.First.Line.Y1 + m.First.Line.Y2) / 2, ay, 8);
        Assert.Equal(new VisionPoint(ax, ay), m.DistanceSegment!.Start); Assert.Equal(new VisionPoint(bx, by), m.DistanceSegment.End);
        var line = m.Second!.Line!; double dx = line.X2 - line.X1, dy = line.Y2 - line.Y1;
        Assert.InRange(Math.Abs((bx - ax) * dx + (by - ay) * dy) / line.Length, 0, 1e-8);
        Assert.InRange(Math.Abs(line.Equation!.A * bx + line.Equation.B * by + line.Equation.C), 0, 1e-8);
        Assert.Equal(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), m.Value.Value, 8);
        Assert.True(ax > 900); Assert.InRange(raw.GetProperty("lineAngleDegrees").GetDouble(), 0, .1);
        Assert.Contains(result.GetOverlays("Overlay"), o => o.Kind == "text");
        using var exported = JsonDocument.Parse(result.Data.GetRawText());
        Assert.Equal(m.Value.Value, exported.RootElement.GetProperty("outputs").GetProperty("Distance").GetProperty("value").GetDouble());
    }
    [Theory]
    [InlineData(.5, true)] [InlineData(3.0, false)]
    public async Task ParallelAngleToleranceAcceptsSlightSkewAndRejectsNonparallelLines(double skew, bool valid)
    {
        using var frame = Frame(0, skew); using var result = await Run(Definition(skew: skew), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var m = result.GetGeometricMeasurement("Distance");
        Assert.Equal(valid, m.Valid);
        var raw = result.GetOutput<JsonElement>("Distance"); Assert.InRange(raw.GetProperty("lineAngleDegrees").GetDouble(), skew - .1, skew + .1);
        if (valid)
        {
            double ax = (m.First!.Line!.X1 + m.First.Line.X2) / 2, ay = (m.First.Line.Y1 + m.First.Line.Y2) / 2;
            var eq = m.Second!.Line!.Equation!; Assert.Equal(Math.Abs(eq.A * ax + eq.B * ay + eq.C), m.Value!.Value, 7);
        }
        else
        {
            Assert.Null(m.Value); Assert.Equal(InspectionDecision.Unknown, result.Decision); Assert.Equal("NonParallelLines", m.Decision.Reason);
            Assert.Equal(JsonValueKind.Null, raw.GetProperty("distanceSegment").ValueKind); Assert.Empty(result.GetOverlays("Overlay"));
        }
    }
    [Fact]
    public async Task AngleAndDistanceToleranceLimitsAreInclusiveWithoutDisplayRounding()
    {
        using var frame = Frame(0, .5); var d = Definition(skew: .5);
        using var baseline = await Run(d, frame); double measured = baseline.GetGeometricMeasurement("Distance").Value!.Value;
        double angle = baseline.GetOutput<JsonElement>("Distance").GetProperty("lineAngleDegrees").GetDouble();
        var p = Parameters(d, "measure"); p["parallelAngleToleranceDegrees"] = angle; p["lowerLimit"] = measured; p["upperLimit"] = measured;
        using var exact = await Run(d, frame); Assert.Equal(InspectionDecision.Pass, exact.Decision);
        p["parallelAngleToleranceDegrees"] = angle - .0001;
        using var invalid = await Run(d, frame); Assert.Null(invalid.GetGeometricMeasurement("Distance").Value);
        p["parallelAngleToleranceDegrees"] = 1.0; p["upperLimit"] = measured - .000001; p["lowerLimit"] = 0;
        using var failed = await Run(d, frame); Assert.Equal(InspectionDecision.Fail, failed.Decision);
    }
    [Fact]
    public async Task MissingCurrentLineOrInvalidIndexCannotReuseAnEarlierDistance()
    {
        var d = Definition(); using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession();
        using var good = Frame(); using (var result = await session.RunAsync(good)) Assert.True(result.GetGeometricMeasurement("Distance").Valid);
        using var blank = Frame(blank: true); using var missing = await session.RunAsync(blank); var m = missing.GetGeometricMeasurement("Distance");
        Assert.False(m.Valid); Assert.Null(m.Value); Assert.Equal(InspectionDecision.Unknown, missing.Decision); Assert.Empty(m.Overlay.EnumerateArray());
        Parameters(d, "measure")["secondIndex"] = 1; using var outOfRange = await Run(d, good);
        Assert.False(outOfRange.GetGeometricMeasurement("Distance").Valid);
    }
    [Fact]
    public async Task CoincidentLinesHaveARealZeroDistanceAndNoToleranceRemainsUnknown()
    {
        var d = Definition(); d["connections"]![3]!["fromNode"] = "a"; Parameters(d, "measure")["toleranceEnabled"] = false;
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.True(m.Valid); Assert.InRange(m.Value!.Value, 0, 1e-10); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Equal("UnconfiguredTolerance", m.Decision.Reason);
    }
    [Fact]
    public void EditorConnectsBothVisibleResultPortsAndSavesTheMeasurement()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var flow = new FlowDocument(catalog);
        flow.Add("ExternalImage", 0, 0, "source"); flow.Add("FindLines", 240, 0, "a"); flow.Add("FindLines", 240, 180, "b");
        flow.Add("MeasureLineDistance", 500, 90, "distance");
        flow.Connect("source", "image", "a", "input"); flow.Connect("source", "image", "b", "input");
        flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
        flow.Publish("Distance", "distance", "result", false); flow.Publish("Decision", "distance", "decision", true);
        Assert.Empty(flow.PublicInterfaceIssue()); Assert.Equal(3, flow.Spec("distance").Inputs.Count); Assert.Single(flow.Spec("distance").VisibleOutputs);
        Assert.Equal("input", Assert.Single(flow.Spec("distance").VisibleInputs).Id);
        Assert.All(flow.Spec("distance").Inputs.Where(p => p.Id is "first" or "second"), p => Assert.True(p.Hidden));
        Assert.Contains(flow.Spec("distance").ScalarFields, f => f.Field == "value" && f.Nullable && f.Type == "Double");
        var reopened = new FlowDocument(catalog); reopened.Load(flow.ExecutionRecipe().ToJsonString());
        Assert.Equal(4, reopened.Connections.Count); Assert.Equal("Decision", reopened.Definition["decisionOutput"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(-1)] [InlineData(10.001)]
    public void InvalidAngleToleranceIsRejectedBeforeExecution(double tolerance)
    {
        var d = Definition(); Parameters(d, "measure")["parallelAngleToleranceDegrees"] = tolerance;
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(d)));
    }
    [Fact]
    public async Task DistanceAndAngleCanDriveDownstreamParametersWithoutDebug()
    {
        var d = Definition(28);
        JsonObject Binding(string field) => new() { ["node"] = "measure", ["port"] = "result", ["field"] = field };
        d["nodes"]!.AsArray().Add(Node("consumer", "MeasureLineDistance", new()
        {
            ["toleranceEnabled"] = true, ["lowerLimit"] = Binding("value"), ["upperLimit"] = Binding("value"),
            ["parallelAngleToleranceDegrees"] = Binding("lineAngleDegrees")
        }));
        d["connections"]!.AsArray().Add(Edge("a", "features", "consumer", "first"));
        d["connections"]!.AsArray().Add(Edge("b", "features", "consumer", "second"));
        d["outputs"]!["Consumer"] = new JsonObject { ["node"] = "consumer", ["port"] = "result", ["type"] = "Result" };
        using var frame = Frame(28); using var result = await Run(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var m = result.GetGeometricMeasurement("Consumer");
        Assert.True(m.Valid); Assert.Equal(InspectionDecision.Pass, m.Decision.Status); Assert.Equal(m.Value, m.LowerLimit); Assert.Equal(m.Value, m.UpperLimit);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrIncompatibleSecondInputIsRejectedBeforeExecution(bool wrongType)
    {
        var d = Definition();
        if (wrongType) d["connections"]![3]!["fromPort"] = "count";
        else d["connections"]!.AsArray().RemoveAt(3);
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(d)));
    }
    [Fact]
    public async Task DistanceUsesInfiniteParallelLinesRatherThanTheGapBetweenSegmentEndpoints()
    {
        var d = Definition(); var p = Parameters(d, "b"); p["roi"] = RoiRegion.FromJson(p["roi"]).Translate(100, 0).ToJson();
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.True(m.Valid); Assert.InRange(m.Value!.Value, 71.7, 72.3);
        Assert.True(m.DistanceSegment!.End.X < Math.Min(m.Second!.Line!.X1, m.Second.Line.X2));
    }
    [Fact]
    public async Task IndependentParentCropsPreserveTheOriginalMeasurementCoordinates()
    {
        var d = Definition(); var nodes = d["nodes"]!.AsArray(); var edges = d["connections"]!.AsArray();
        nodes.Add(Node("cropA", "CropRect", new() { ["x"] = 50, ["y"] = 100, ["width"] = 350, ["height"] = 80 }));
        nodes.Add(Node("cropB", "CropRect", new() { ["x"] = 100, ["y"] = 190, ["width"] = 330, ["height"] = 100 }));
        edges[0]!["fromNode"] = "cropA"; edges[1]!["fromNode"] = "cropB";
        edges.Add(Edge("source", "image", "cropA")); edges.Add(Edge("source", "image", "cropB"));
        var a = Parameters(d, "a"); a["roi"] = RoiRegion.FromJson(a["roi"]).Translate(-50, -100).ToJson();
        var b = Parameters(d, "b"); b["roi"] = RoiRegion.FromJson(b["roi"]).Translate(-100, -190).ToJson();
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.True(m.Valid); Assert.InRange(m.Value!.Value, 71.7, 72.3);
        Assert.InRange(m.DistanceSegment!.Start.Y, 63.8, 64.2); Assert.InRange(m.DistanceSegment.End.Y, 135.8, 136.2);
        Assert.InRange(m.DistanceSegment.Start.X, 1104.8, 1105.2);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CircularDistanceTests : IDisposable
{
    private readonly string store;
    public CircularDistanceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/circular-distance-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }
    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string port, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Parameters(JsonObject d, string id) => d["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();
    private static JsonObject Circle(double x, double radius) => new()
    {
        ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
        ["roi"] = RoiRegion.Circle(x, 170, radius).ToJson(), ["caliperCount"] = 64, ["projectionWidth"] = 5,
        ["searchLength"] = 18.0, ["polarity"] = "any", ["removePointCount"] = 2, ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .5
    };
    private static JsonObject Definition(bool lineCircle, string? mode = null, double separation = 180, double ra = 32, double rb = 22)
    {
        var p = new JsonObject(); if (mode is not null) p["distanceMode"] = mode;
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "circular-distance", ["recipeVersion"] = 1,
            ["kind"] = "inspection", ["decisionOutput"] = "Decision", ["resources"] = new JsonArray(),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("a", "FindCircles", Circle(150, ra)),
                Node("b", "FindCircles", Circle(150 + separation, rb)), Node("line", "FindLines", new()
                {
                    ["method"] = "caliper", ["caliperPlacement"] = "roiCenter", ["caliperCount"] = 32, ["polarity"] = "lightToDark",
                    ["roi"] = RoiRegion.RotatedRectangle(240, 60, 300, 24, 0).ToJson()
                }), Node("measure", lineCircle ? "MeasureLineCircleDistance" : "MeasureCircleDistance", p)),
            ["connections"] = new JsonArray(Edge("source", "image", "a"), Edge("source", "image", "b"), Edge("source", "image", "line"),
                Edge(lineCircle ? "line" : "a", "features", "measure", "first"), Edge(lineCircle ? "a" : "b", "features", "measure", "second")),
            ["outputs"] = new JsonObject
            {
                ["Distance"] = new JsonObject { ["node"] = "measure", ["port"] = "result", ["type"] = "Result" },
                ["Decision"] = new JsonObject { ["node"] = "measure", ["port"] = "decision", ["type"] = "Decision" },
                ["Overlay"] = new JsonObject { ["node"] = "measure", ["port"] = "overlay", ["type"] = "Overlay" },
                ["CircleA"] = new JsonObject { ["node"] = "a", ["port"] = "result", ["type"] = "Result" },
                ["CircleB"] = new JsonObject { ["node"] = "b", ["port"] = "result", ["type"] = "Result" },
                ["Line"] = new JsonObject { ["node"] = "line", ["port"] = "result", ["type"] = "Result" }
            }
        };
    }
    private static VisionFrame Frame(double separation = 180, double ra = 32, double rb = 22, bool missing = false, double lineY = 60, double lineAngle = 0)
    {
        const int width = 480, height = 340; var pixels = new byte[width * height];
        bool aContainsB = separation + rb < ra, bContainsA = separation + ra < rb;
        double radians = lineAngle * Math.PI / 180;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double a = ra - Length(x - 150, y - 170), b = rb - Length(x - 150 - separation, y - 170);
            double shape = aContainsB ? Math.Min(a, -b) : bContainsA ? Math.Min(b, -a) : Math.Max(a, b);
            double halfPlane = (x - 240) * Math.Sin(radians) - (y - lineY) * Math.Cos(radians);
            double value = missing ? halfPlane : Math.Max(halfPlane, shape);
            pixels[y * width + x] = (byte)Math.Clamp(125 + value * 180, 20, 230);
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80);
    }
    private string Save(JsonObject d)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject d, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = false });
    }
    private static (VisionPoint Start, VisionPoint End) Segment(JsonElement packet)
    {
        var segment = packet.GetProperty("distanceSegment");
        VisionPoint Point(string key) => new(segment.GetProperty(key).GetProperty("x").GetDouble(), segment.GetProperty(key).GetProperty("y").GetDouble());
        return (Point("start"), Point("end"));
    }
    private static void AssertCirclePoint(VisionPoint point, VisionCircleGeometry circle) =>
        Assert.InRange(Math.Abs(Length(point.X - circle.CenterX, point.Y - circle.CenterY) - circle.Radius), 0, 1e-6);
    [Theory]
    [InlineData(null, 180)] [InlineData("centerDistance", 180)] [InlineData("edgeDistance", 126)]
    public async Task CircleCircleReadsBothDistancesAndOriginalEndpointsWhilePreservingTheExistingSdk(string? mode, double expected)
    {
        using var frame = Frame(); using var result = await Run(Definition(false, mode), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var m = result.GetGeometryMeasurement("Distance"); Assert.True(m.Valid);
        Assert.InRange(m.Value!.Value, expected - .5, expected + .5); var raw = result.GetOutput<JsonElement>("Distance");
        var unified = result.GetGeometricMeasurement("Distance"); Assert.Equal(m.Value, unified.Value); Assert.NotNull(unified.First!.Circle);
        Assert.NotNull(m.CenterDistance); Assert.NotNull(m.EdgeDistance); Assert.NotNull(m.DistanceSegment);
        Assert.InRange(raw.GetProperty("centerDistance").GetDouble(), 179.8, 180.2); Assert.InRange(raw.GetProperty("edgeDistance").GetDouble(), 125.5, 126.5);
        var segment = Segment(raw); Assert.True(segment.Start.X > 900); Assert.InRange(segment.Start.Y, 89.8, 90.2);
        Assert.Equal(m.Value.Value, Length(segment.End.X - segment.Start.X, segment.End.Y - segment.Start.Y), 7);
        if (mode == "edgeDistance") { AssertCirclePoint(segment.Start, m.First!); AssertCirclePoint(segment.End, m.Second!); }
        else { Assert.Equal(m.First!.CenterX, segment.Start.X, 8); Assert.Equal(m.Second!.CenterX, segment.End.X, 8); }
        Assert.All(result.GetOverlays("Overlay"), o => Assert.Equal("#00FF00", o.Color));
    }
    [Theory]
    [InlineData(45, 32, 22)] [InlineData(20, 65, 20)] [InlineData(0, 65, 20)] [InlineData(20, 20, 65)]
    public async Task CircleBoundaryDistanceHandlesIntersectingContainedAndConcentricCircles(double separation, double ra, double rb)
    {
        using var frame = Frame(separation, ra, rb); using var result = await Run(Definition(false, "edgeDistance", separation, ra, rb), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var m = result.GetGeometryMeasurement("Distance"); Assert.True(m.Valid);
        double d = Length(m.First!.CenterX - m.Second!.CenterX, m.First.CenterY - m.Second.CenterY);
        double expected = Math.Max(0, Math.Max(d - m.First.Radius - m.Second.Radius, Math.Abs(m.First.Radius - m.Second.Radius) - d));
        Assert.Equal(expected, m.Value!.Value, 7); var segment = Segment(result.GetOutput<JsonElement>("Distance"));
        AssertCirclePoint(segment.Start, m.First); AssertCirclePoint(segment.End, m.Second);
        Assert.Equal(expected, Length(segment.End.X - segment.Start.X, segment.End.Y - segment.Start.Y), 6);
    }
    [Theory]
    [InlineData(null, 110)] [InlineData("centerDistance", 110)] [InlineData("edgeDistance", 78)]
    public async Task LineCircleUsesTheInfiniteLineAndTheFittedCircle(string? mode, double expected)
    {
        using var frame = Frame(); using var result = await Run(Definition(true, mode), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var m = result.GetGeometricMeasurement("Distance"); Assert.True(m.Valid);
        Assert.Equal("lineCircleDistance", m.Operation); Assert.InRange(m.Value!.Value, expected - .4, expected + .4);
        Assert.NotNull(m.CenterDistance); Assert.NotNull(m.EdgeDistance); Assert.NotNull(m.DistanceSegment); Assert.NotNull(m.Second!.Circle);
        var raw = result.GetOutput<JsonElement>("Distance"); var segment = Segment(raw); var eq = m.First!.Line!.Equation!;
        Assert.InRange(Math.Abs(eq.A * segment.Start.X + eq.B * segment.Start.Y + eq.C), 0, 1e-6);
        var circle = result.GetResult<VisionCircle>("CircleA").Features.Single();
        Assert.InRange(raw.GetProperty("centerDistance").GetDouble(), 109.8, 110.2); Assert.InRange(raw.GetProperty("edgeDistance").GetDouble(), 77.6, 78.4);
        if (mode == "edgeDistance") AssertCirclePoint(segment.End, circle.Circle);
        else { Assert.Equal(circle.CenterX, segment.End.X, 8); Assert.Equal(circle.CenterY, segment.End.Y, 8); }
        Assert.All(result.GetOverlays("Overlay"), o => Assert.Equal("#00FF00", o.Color));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingCurrentCircleNeverReusesAnEarlierMeasurement(bool lineCircle)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition(lineCircle, "edgeDistance"))); await using var session = recipe.CreateSession();
        using var frame = Frame(); using (var found = await session.RunAsync(frame)) Assert.True(found.GetOutput<JsonElement>("Distance").GetProperty("valid").GetBoolean());
        using var blank = Frame(missing: true); using var absent = await session.RunAsync(blank); var packet = absent.GetOutput<JsonElement>("Distance");
        Assert.Equal(ExecutionStatus.Succeeded, absent.Status); Assert.Equal(InspectionDecision.Unknown, absent.Decision);
        Assert.False(packet.GetProperty("valid").GetBoolean());
        foreach (string key in new[] { "value", "centerDistance", "edgeDistance", "distanceSegment" }) Assert.Equal(JsonValueKind.Null, packet.GetProperty(key).ValueKind);
        Assert.Empty(absent.GetOverlays("Overlay"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ToleranceUsesTheSelectedDistanceModeAndIncludesItsUnroundedValue(bool lineCircle)
    {
        var d = Definition(lineCircle, "edgeDistance"); using var frame = Frame(); using var baseline = await Run(d, frame);
        double value = baseline.GetOutput<JsonElement>("Distance").GetProperty("value").GetDouble();
        var p = Parameters(d, "measure"); p["toleranceEnabled"] = true; p["lowerLimit"] = value; p["upperLimit"] = value;
        using var exact = await Run(d, frame); Assert.Equal(InspectionDecision.Pass, exact.Decision);
        p["upperLimit"] = value - .000001; p["lowerLimit"] = 0;
        using var failed = await Run(d, frame); Assert.Equal(InspectionDecision.Fail, failed.Decision);
    }
    [Theory]
    [InlineData("MeasureCircleDistance")] [InlineData("MeasureLineCircleDistance")]
    public void EditorConnectsBothVisibleResultsAndRetainsTheSelectedDistanceMode(string type)
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var flow = new FlowDocument(catalog);
        flow.Add(type == "MeasureCircleDistance" ? "FindCircles" : "FindLines", 0, 0, "a"); flow.Add("FindCircles", 0, 180, "b");
        flow.Add(type, 300, 90, "distance"); flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
        flow.SetParameter("distance", "distanceMode", JsonValue.Create("edgeDistance")); Assert.Single(flow.Spec("distance").VisibleOutputs);
        Assert.Contains(flow.Spec("distance").ScalarFields, f => f.Field == "centerDistance" && f.Nullable);
        Assert.Contains(flow.Spec("distance").ScalarFields, f => f.Field == "edgeDistance" && f.Nullable);
        var reopened = new FlowDocument(catalog); reopened.Load(flow.Definition.ToJsonString());
        Assert.Equal("edgeDistance", reopened.Node("distance")["parameters"]!["distanceMode"]!.GetValue<string>());
        Assert.Equal(type == "MeasureCircleDistance" ? "圆圆距离" : "线圆距离", reopened.Spec("distance").Label);
    }
    [Fact]
    public async Task DefaultResultLinesCirclesAnnuliAndFitPointsAreGreenAndExplicitColorsArePreserved()
    {
        var d = Definition(false); using var frame = Frame(); using var result = await Run(d, frame);
        foreach (string name in new[] { "CircleA", "CircleB", "Line" })
            Assert.All(result.GetOutput<JsonElement>(name).GetProperty("overlay").EnumerateArray(), o => Assert.Equal("#00FF00", o.GetProperty("color").GetString()));
        Parameters(d, "a")["overlayColor"] = "#123456"; using var custom = await Run(d, frame);
        Assert.All(custom.GetOutput<JsonElement>("CircleA").GetProperty("overlay").EnumerateArray(), o => Assert.Equal("#123456", o.GetProperty("color").GetString()));
    }
    [Fact]
    public async Task CoincidentCircleBoundariesHaveARealZeroAndACommonPointOnTheCircumference()
    {
        var d = Definition(false, "edgeDistance"); d["connections"]![4]!["fromNode"] = "a";
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometryMeasurement("Distance");
        Assert.True(m.Valid); Assert.Equal(0, m.Value); Assert.Equal("coincident", m.Relation);
        Assert.Equal(m.DistanceSegment!.Start, m.DistanceSegment.End); AssertCirclePoint(m.DistanceSegment.Start, m.First!);
    }
    [Fact]
    public async Task IntersectingLineAndCircleReturnZeroAtAnActualSharedBoundaryPoint()
    {
        var d = Definition(true, "edgeDistance"); Parameters(d, "line")["roi"] = RoiRegion.RotatedRectangle(240, 150, 300, 24, 0).ToJson();
        using var frame = Frame(lineY: 150); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.True(m.Valid); Assert.Equal(0, m.Value); Assert.Equal("intersecting", m.Relation);
        Assert.Equal(m.DistanceSegment!.Start, m.DistanceSegment.End); AssertCirclePoint(m.DistanceSegment.End, m.Second!.Circle!);
        var eq = m.First!.Line!.Equation!; Assert.InRange(Math.Abs(eq.A * m.DistanceSegment.Start.X + eq.B * m.DistanceSegment.Start.Y + eq.C), 0, 1e-6);
    }
    [Theory]
    [InlineData(35)] [InlineData(90)]
    public async Task RotatedAndVerticalLinesMeasureTheCircleInTheOriginalFrame(double degrees)
    {
        var d = Definition(true, "edgeDistance"); Parameters(d, "line")["roi"] = RoiRegion.RotatedRectangle(240, 170, 260, 24, degrees).ToJson();
        using var frame = Frame(lineY: 170, lineAngle: degrees); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.True(m.Valid); double expectedCenter = Math.Abs(90 * Math.Sin(degrees * Math.PI / 180));
        Assert.InRange(m.CenterDistance!.Value, expectedCenter - .3, expectedCenter + .3); Assert.InRange(m.Value!.Value, expectedCenter - 32.4, expectedCenter - 31.6);
        AssertCirclePoint(m.DistanceSegment!.End, m.Second!.Circle!);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OutOfRangeSelectionHasNoDistanceAndNoMeasurementSegment(bool lineCircle)
    {
        var d = Definition(lineCircle); Parameters(d, "measure")["secondIndex"] = 1;
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Distance");
        Assert.False(m.Valid); Assert.Null(m.Value); Assert.Null(m.CenterDistance); Assert.Null(m.EdgeDistance); Assert.Null(m.DistanceSegment);
        Assert.Equal("UnavailableSelection", m.Decision.Reason);
    }
    [Theory]
    [InlineData("centerDistance")] [InlineData("edgeDistance")]
    public async Task EachDerivedDistanceCanDriveAnUnroundedDownstreamTolerance(string mode)
    {
        var d = Definition(false, "edgeDistance");
        JsonObject Binding() => new() { ["node"] = "measure", ["port"] = "result", ["field"] = mode };
        d["nodes"]!.AsArray().Add(Node("consumer", "MeasureCircleDistance", new()
        { ["distanceMode"] = mode, ["toleranceEnabled"] = true, ["lowerLimit"] = Binding(), ["upperLimit"] = Binding() }));
        d["connections"]!.AsArray().Add(Edge("a", "features", "consumer", "first")); d["connections"]!.AsArray().Add(Edge("b", "features", "consumer", "second"));
        d["outputs"]!["Consumer"] = new JsonObject { ["node"] = "consumer", ["port"] = "result", ["type"] = "Result" };
        using var frame = Frame(); using var result = await Run(d, frame); var m = result.GetGeometricMeasurement("Consumer");
        Assert.True(m.Valid); Assert.Equal(InspectionDecision.Pass, m.Decision.Status); Assert.Equal(m.Value, m.LowerLimit); Assert.Equal(m.Value, m.UpperLimit);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class MeasurementToolsTests : IDisposable
{
    private readonly string store;
    public MeasurementToolsTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "measurement-tools-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string port, string to, string input = "input") => new() { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Base() => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "measurement-tools", ["recipeVersion"] = 1,
        ["kind"] = "inspection", ["decisionOutput"] = "Decision", ["resources"] = new JsonArray(),
        ["nodes"] = new JsonArray(Node("source", "ExternalImage")), ["connections"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject
        {
            ["Measurement"] = new JsonObject { ["node"] = "measure", ["port"] = "result", ["type"] = "Result" },
            ["Decision"] = new JsonObject { ["node"] = "measure", ["port"] = "decision", ["type"] = "Decision" }
        }
    };
    private static JsonObject Line(string edge, RoiRegion roi) => new()
    { ["roi"] = roi.ToJson(), ["roiEdge"] = edge, ["caliperPlacement"] = "roiBoundary", ["polarity"] = "lightToDark", ["searchLength"] = 20.0, ["projectionWidth"] = 3 };
    private static JsonObject Lines(string operation, bool parallel = false)
    {
        var d = Base(); var nodes = d["nodes"]!.AsArray(); var edges = d["connections"]!.AsArray();
        nodes.Add(Node("a", "LineCaliper", Line("top", RoiRegion.Rectangle(50, 90, 120, 30))));
        nodes.Add(Node("b", "LineCaliper", Line("left", RoiRegion.Rectangle(200, 25, 30, 50))));
        nodes.Add(Node("measure", "MeasureGeometry", new() { ["operation"] = operation }));
        edges.Add(Edge("source", "image", "a")); edges.Add(Edge("source", "image", "b"));
        edges.Add(Edge("a", "features", "measure", "first")); edges.Add(Edge(parallel ? "a" : "b", "features", "measure", "second"));
        return d;
    }
    private static VisionFrame LineFrame() => Frame((x, y) => x >= 200 || y >= 90 ? (byte)230 : (byte)15);
    private static VisionFrame Frame(Func<int, int, byte> pixel)
    {
        var pixels = new byte[360 * 240];
        for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++) pixels[y * 360 + x] = pixel(x, y);
        return VisionFrame.CopyFrom(pixels, 360, 240, 360, VisionPixelFormat.Gray8, 900, -80);
    }
    private string Save(JsonObject d)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Execute(JsonObject d, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame);
    }
    [Theory]
    [InlineData("pointToLine", 89.5, "px")] [InlineData("lineAngle", 90, "deg")]
    public async Task GeometryUsesRealIndependentLineInputsAndOriginalCoordinates(string operation, double expected, string unit)
    {
        using var frame = LineFrame(); using var result = await Execute(Lines(operation), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var measurement = result.GetGeometricMeasurement("Measurement");
        Assert.True(measurement.Valid); Assert.InRange(measurement.Value!.Value, expected - .2, expected + .2); Assert.Equal(unit, measurement.Unit);
        var line = measurement.Second!.Line!; var equation = line.Equation!; Assert.NotNull(equation);
        Assert.InRange(Math.Abs(equation.A * equation.A + equation.B * equation.B - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(equation.A * line.X1 + equation.B * line.Y1 + equation.C), 0, 1e-8);
        Assert.InRange(Math.Abs(equation.A * line.X2 + equation.B * line.Y2 + equation.C), 0, 1e-8);
    }
    [Fact]
    public async Task IntersectionUsesInfiniteLinesAndParallelLinesHaveNoFakeOrigin()
    {
        using var frame = LineFrame(); using var result = await Execute(Lines("lineIntersection"), frame);
        var measurement = result.GetGeometricMeasurement("Measurement"); Assert.True(measurement.Valid); Assert.Null(measurement.Value);
        Assert.InRange(measurement.Point!.Value.X, 1099.3, 1099.7); Assert.InRange(measurement.Point.Value.Y, 9.3, 9.7);
        using var parallel = await Execute(Lines("lineIntersection", true), frame);
        var absent = parallel.GetGeometricMeasurement("Measurement"); Assert.False(absent.Valid); Assert.Null(absent.Point);
        Assert.Equal(InspectionDecision.Unknown, parallel.Decision); Assert.Equal("ParallelLines", absent.Decision.Reason);
    }
    [Fact]
    public async Task NominalToleranceReportsRealDeviationAndUsesInclusiveLimits()
    {
        var d = Lines("pointToLine"); var p = d["nodes"]!.AsArray().Last()!["parameters"]!;
        p["toleranceEnabled"] = true; p["toleranceMode"] = "deviation"; p["nominalValue"] = 89.5; p["lowerDeviation"] = -.2; p["upperDeviation"] = .2;
        using var frame = LineFrame(); using var result = await Execute(d, frame);
        var measurement = result.GetGeometricMeasurement("Measurement"); Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.InRange(measurement.Deviation!.Value, -.2, .2); Assert.Equal(89.3, measurement.LowerLimit, 6); Assert.Equal(89.7, measurement.UpperLimit, 6);
    }
    [Theory]
    [InlineData(0, false, false)] [InlineData(37, false, false)] [InlineData(0, true, false)] [InlineData(37, false, true)]
    public async Task EdgePairMeasuresBothRealEdgesAfterRotationReverseAndParentCrop(double angle, bool reverse, bool crop)
    {
        var d = Base(); var nodes = d["nodes"]!.AsArray(); var edges = d["connections"]!.AsArray();
        string source = "source";
        if (crop) { source = "crop"; nodes.Add(Node(source, "CropRect", new() { ["x"] = 100, ["y"] = 50, ["width"] = 180, ["height"] = 160 })); edges.Add(Edge("source", "image", source)); }
        nodes.Add(Node("measure", "MeasureEdgePair", new() { ["roi"] = RoiRegion.RotatedRectangle(crop ? 50 : 150, crop ? 70 : 120, 5, 80, angle).ToJson(),
            ["reverseSearch"] = reverse, ["toleranceEnabled"] = true, ["lowerLimit"] = 19.4, ["upperLimit"] = 20.6 }));
        edges.Add(Edge(source, "image", "measure"));
        double r = angle * Math.PI / 180;
        using var frame = Frame((x, y) => (byte)Math.Clamp((10.5 - Math.Abs(-(x - 150) * Math.Sin(r) + (y - 120) * Math.Cos(r))) * 230, 15, 230));
        using var result = await Execute(d, frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var measurement = result.GetGeometricMeasurement("Measurement"); Assert.True(measurement.Valid); Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.InRange(measurement.Value!.Value, 19.4, 20.6);
        var a = measurement.First!.Point!.Value; var b = measurement.Second!.Point!.Value;
        Assert.Equal(new VisionPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2), measurement.Center);
        Assert.InRange((a.X + b.X) / 2, 1049.6, 1050.4); Assert.InRange((a.Y + b.Y) / 2, 39.6, 40.4);
        Assert.True(measurement.Contrast > 10); Assert.True(measurement.Diagnostics.HasValue);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task EdgePairAbsenceOrWrongPolarityIsUnknown(bool blank)
    {
        var d = Base(); d["nodes"]!.AsArray().Add(Node("measure", "MeasureEdgePair", new()
        { ["roi"] = RoiRegion.RotatedRectangle(150, 120, 5, 80, 0).ToJson(), ["firstPolarity"] = "lightToDark", ["secondPolarity"] = "darkToLight" }));
        d["connections"]!.AsArray().Add(Edge("source", "image", "measure"));
        using var frame = Frame((x, y) => blank ? (byte)15 : y >= 110 && y < 130 ? (byte)230 : (byte)15);
        using var result = await Execute(d, frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var measurement = result.GetGeometricMeasurement("Measurement"); Assert.False(measurement.Valid); Assert.Null(measurement.Value);
        Assert.Equal(InspectionDecision.Unknown, result.Decision); Assert.Equal("NoEdgePair", measurement.Decision.Reason);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

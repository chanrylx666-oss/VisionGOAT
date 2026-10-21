using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class GeometryDistanceTests : IDisposable
{
    private readonly string store;
    public GeometryDistanceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "geometry-distance-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string port, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Circle(double x) => new()
    {
        ["positionReference"] = "image", ["method"] = "caliper", ["caliperPlacement"] = "roiBoundary",
        ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = x, ["cy"] = 90.0, ["radius"] = 18.0 },
        ["caliperCount"] = 36, ["projectionWidth"] = 5, ["searchLength"] = 20.0,
        ["polarity"] = "lightToDark", ["maximumDeviation"] = 2.0
    };
    private static JsonObject Definition(bool tolerance = true) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "geometry-distance", ["recipeVersion"] = 1,
        ["kind"] = "inspection", ["resources"] = new JsonArray(),
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("a", "FindCircles", Circle(80)),
            Node("b", "FindCircles", Circle(220)), Node("distance", "MeasureCircleDistance", new()
            { ["toleranceEnabled"] = tolerance, ["lowerLimit"] = 139.0, ["upperLimit"] = 141.0 })),
        ["connections"] = new JsonArray(Edge("source", "image", "a"), Edge("source", "image", "b"),
            Edge("a", "features", "distance", "first"), Edge("b", "features", "distance", "second")),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject
        {
            ["Distance"] = new JsonObject { ["node"] = "distance", ["port"] = "result", ["type"] = "Result" },
            ["Decision"] = new JsonObject { ["node"] = "distance", ["port"] = "decision", ["type"] = "Decision" },
            ["Overlay"] = new JsonObject { ["node"] = "distance", ["port"] = "overlay", ["type"] = "Overlay" }
        }, ["decisionOutput"] = "Decision"
    };
    private static JsonObject Parameters(JsonObject d, string id) => d["nodes"]!.AsArray()
        .First(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return path;
    }
    private static VisionFrame Frame(bool second = true, int originX = 0, int originY = 0)
    {
        const int width = 360, height = 240;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if ((x - 80) * (x - 80) + (y - 90) * (y - 90) <= 324 ||
                second && (x - 220) * (x - 220) + (y - 90) * (y - 90) <= 324)
                pixels[y * width + x] = 230;
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, originX, originY);
    }
    private async Task<InspectionResult> Execute(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = false });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(900, -80)]
    public async Task RealTwoInputMeasurementReturnsOriginalCoordinatesWithoutDebug(int originX, int originY)
    {
        using var frame = Frame(originX: originX, originY: originY);
        using var result = await Execute(Definition(), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        var output = result.GetOutput<JsonElement>("Distance");
        var typed = result.GetGeometryMeasurement("Distance");
        Assert.True(typed.Valid); Assert.Equal(InspectionDecision.Pass, typed.Decision.Status);
        Assert.Equal(output.GetProperty("value").GetDouble(), typed.Value);
        Assert.True(output.GetProperty("valid").GetBoolean());
        Assert.InRange(output.GetProperty("value").GetDouble(), 139.8, 140.2);
        Assert.InRange(output.GetProperty("first").GetProperty("centerX").GetDouble(), originX + 79.8, originX + 80.2);
        Assert.InRange(output.GetProperty("second").GetProperty("centerY").GetDouble(), originY + 89.8, originY + 90.2);
        Assert.Equal("original", output.GetProperty("coordinateSpace").GetString());
        Assert.Equal("px", output.GetProperty("unit").GetString()); Assert.NotEmpty(result.GetOverlays("Overlay"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCircleOrOutOfRangeSelectionIsSuccessfulButUnknown(bool outOfRange)
    {
        var definition = Definition();
        if (outOfRange) Parameters(definition, "distance")["secondIndex"] = 1;
        using var frame = Frame(second: outOfRange); using var result = await Execute(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        var output = result.GetOutput<JsonElement>("Distance"); Assert.False(output.GetProperty("valid").GetBoolean());
        Assert.Null(result.GetGeometryMeasurement("Distance").Value);
        Assert.Equal(JsonValueKind.Null, output.GetProperty("value").ValueKind);
        Assert.Empty(output.GetProperty("overlay").EnumerateArray());
    }

    [Fact]
    public async Task NoToleranceIsUnknownAndOutOfToleranceIsFail()
    {
        using var frame = Frame(); using var unconfigured = await Execute(Definition(false), frame);
        Assert.Equal(InspectionDecision.Unknown, unconfigured.Decision);
        Assert.True(unconfigured.GetOutput<JsonElement>("Distance").GetProperty("valid").GetBoolean());
        var definition = Definition(); Parameters(definition, "distance")["lowerLimit"] = 150.0;
        Parameters(definition, "distance")["upperLimit"] = 160.0;
        using var ng = await Execute(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, ng.Status); Assert.Equal(InspectionDecision.Fail, ng.Decision);
    }

    [Fact]
    public async Task SharedSourceMayFeedBothInputsAndProducesRealZeroDistance()
    {
        var definition = Definition(false);
        definition["connections"]!.AsArray()[3]!["fromNode"] = "a";
        using var frame = Frame(); using var result = await Execute(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetOutput<JsonElement>("Distance"); Assert.True(packet.GetProperty("valid").GetBoolean());
        Assert.Equal(0, packet.GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("negative")]
    [InlineData("limits")]
    [InlineData("type")]
    public void InvalidInputOrParametersAreRejectedBeforeExecution(string scenario)
    {
        var definition = Definition();
        if (scenario == "missing") definition["connections"]!.AsArray().RemoveAt(3);
        if (scenario == "negative") Parameters(definition, "distance")["firstIndex"] = -1;
        if (scenario == "limits") Parameters(definition, "distance")["lowerLimit"] = 142.0;
        if (scenario == "type") definition["connections"]!.AsArray()[3]!["fromPort"] = "count";
        using var engine = new VisionEngine(store);
        if (scenario == "negative") Assert.Throws<ArgumentException>(() => engine.LoadRecipe(Save(definition)));
        else Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
    }

    [Fact]
    public void EditorProjectsBothCircleResultsAndPublishesDecision()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var flow = new FlowDocument(catalog);
        flow.Add("ExternalImage", 0, 0, "source"); flow.Add("FindCircles", 200, 0, "a"); flow.Add("FindCircles", 200, 140, "b");
        flow.Add("MeasureCircleDistance", 450, 0, "distance");
        flow.Connect("source", "image", "a", "input"); flow.Connect("source", "image", "b", "input");
        flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
        flow.Publish("Decision", "distance", "decision", true);
        Assert.Empty(flow.PublicInterfaceIssue()); Assert.Equal(3, flow.Spec("distance").Inputs.Count);
        Assert.Equal("input", Assert.Single(flow.Spec("distance").VisibleInputs).Id);
        Assert.All(flow.Spec("distance").Inputs.Where(p => p.Id is "first" or "second"), p => Assert.True(p.Hidden));
        Assert.Single(flow.Spec("distance").VisibleOutputs);
        string json = flow.ExecutionRecipe().ToJsonString(); var reopened = new FlowDocument(catalog);
        reopened.Load(json); Assert.Equal(4, reopened.Connections.Count);
        Assert.Equal("Decision", reopened.Definition["decisionOutput"]!.GetValue<string>());
    }

    [Fact]
    public async Task CurrentFrameNeverReusesPreviousDistanceAndToleranceIncludesBothEndpoints()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition(false)));
        await using var session = recipe.CreateSession(); using var good = Frame(); using var missing = Frame(false);
        using var first = await session.RunAsync(good);
        double measured = first.GetGeometryMeasurement("Distance").Value!.Value;
        using var second = await session.RunAsync(missing);
        Assert.Equal(InspectionDecision.Unknown, second.Decision); Assert.Null(second.GetGeometryMeasurement("Distance").Value);
        var exact = Definition(); Parameters(exact, "distance")["lowerLimit"] = measured;
        Parameters(exact, "distance")["upperLimit"] = measured;
        using var limits = await Execute(exact, good); Assert.Equal(InspectionDecision.Pass, limits.Decision);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

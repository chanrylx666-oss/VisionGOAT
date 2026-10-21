using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ParameterBindingTests : IDisposable
{
    private readonly string store;
    public ParameterBindingTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "field-binding-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Binding(string node, string field) => new() { ["node"] = node, ["port"] = "result", ["field"] = field };
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Pair() => new() { ["roi"] = RoiRegion.RotatedRectangle(60, 50, 5, 60, 0).ToJson() };
    private static JsonObject Recipe()
    {
        var target = Pair(); target["toleranceEnabled"] = true; target["lowerLimit"] = Binding("z_reference", "value"); target["upperLimit"] = Binding("z_reference", "value");
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "field-binding", ["recipeVersion"] = 1,
            ["kind"] = "inspection", ["decisionOutput"] = "Decision", ["resources"] = new JsonArray(),
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("a_target", "MeasureEdgePair", target), Node("z_reference", "MeasureEdgePair", Pair())),
            ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "a_target", ["toPort"] = "input" }, new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "z_reference", ["toPort"] = "input" }),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject { ["Measurement"] = new JsonObject { ["node"] = "a_target", ["port"] = "result", ["type"] = "Result" }, ["Decision"] = new JsonObject { ["node"] = "a_target", ["port"] = "decision", ["type"] = "Decision" } }
        };
    }
    private string Save(JsonObject d)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return path;
    }
    private static VisionFrame Frame(bool blank = false)
    {
        byte[] pixels = new byte[120 * 100];
        for (int y = 0; y < 100; y++) for (int x = 0; x < 120; x++) pixels[y * 120 + x] = !blank && y >= 40 && y < 60 ? (byte)230 : (byte)15;
        return VisionFrame.CopyFrom(pixels, 120, 100, 120, VisionPixelFormat.Gray8);
    }
    [Fact]
    public async Task BindingSchedulesUnpublishedSourceAndRetainsItWithoutDebug()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Recipe())); await using var session = recipe.CreateSession(); using var frame = Frame();
        using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        var m = result.GetGeometricMeasurement("Measurement"); Assert.True(m.Valid); Assert.Equal(m.Value, m.LowerLimit); Assert.Equal(m.Value, m.UpperLimit);
    }
    [Fact]
    public async Task UnavailableFieldSkipsDownstreamWithoutFakeZeroOrPreviousResult()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Recipe())); await using var session = recipe.CreateSession();
        using var goodFrame = Frame(); using (var good = await session.RunAsync(goodFrame)) Assert.Equal(InspectionDecision.Pass, good.Decision);
        using var blank = Frame(true); using var result = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        var m = result.GetGeometricMeasurement("Measurement"); Assert.False(m.Valid); Assert.Null(m.Value); Assert.Equal("UnavailableParameter", m.Decision.Reason);
        Assert.Equal("Skipped", result.Data.GetProperty("nodes").GetProperty("a_target").GetProperty("status").GetString());
    }
    [Fact]
    public async Task MissingLimitCannotTurnPlaceholderBoundsIntoASdkContractError()
    {
        var d = Recipe(); d["nodes"]![1]!["parameters"]!["lowerLimit"] = 1200.0;
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession(); using var blank = Frame(true);
        using var result = await session.RunAsync(blank); var measurement = result.GetGeometricMeasurement("Measurement");
        Assert.False(measurement.ParametersAvailable); Assert.False(measurement.Valid); Assert.Null(measurement.Value); Assert.Equal(InspectionDecision.Unknown, result.Decision);
    }
    [Theory]
    [InlineData("doesNotExist")] [InlineData("valid")]
    public void InvalidOrIncompatibleFieldRejectedBeforeExecution(string field)
    {
        var d = Recipe(); d["nodes"]![1]!["parameters"]!["upperLimit"] = Binding("z_reference", field);
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(d)));
    }
    [Fact]
    public async Task LegacyPublicInputDefaultCanBeReboundToAnUpstreamIntegerField()
    {
        var d = Recipe(); d["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("detect", "CircleCaliper"),
            Node("compare", "CompareNumber", new() { ["expected"] = Binding("detect", "measurement.count") }));
        d["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "detect", ["toPort"] = "input" },
            new JsonObject { ["fromNode"] = "detect", ["fromPort"] = "count", ["toNode"] = "compare", ["toPort"] = "input" });
        d["outputs"] = new JsonObject { ["Decision"] = new JsonObject { ["node"] = "compare", ["port"] = "value", ["type"] = "Bool" } };
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession(); using var blank = Frame(true);
        using var result = await session.RunAsync(blank); Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
    }
    [Fact]
    public void ParameterDependencyCycleRejected()
    {
        var d = Recipe(); d["nodes"]![2]!["parameters"]!["upperLimit"] = Binding("a_target", "value");
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(d)));
    }
    [Fact]
    public void EditorBindingsParticipateInAncestorsUndoCyclesAndDeletionDiagnostics()
    {
        var flow = new FlowDocument(new NodeCatalog(VisionEngine.GetNodeCatalog())); flow.Load(Recipe().ToJsonString());
        flow.Add("FindCircles", 100, 200, "unfinished");
        var selected = flow.RecipeToNode("a_target");
        Assert.Equal(new[] { "a_target", "source", "z_reference" }, selected["nodes"]!.AsArray().Select(n => FlowDocument.Id(n!)).Order().ToArray());
        string before = flow.Definition.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => flow.SetParameter("z_reference", "upperLimit", Binding("a_target", "value")));
        Assert.Equal(before, flow.Definition.ToJsonString());
        flow.SetParameter("a_target", "upperLimit", JsonValue.Create(100.0)); flow.Undo(); Assert.Equal(before, flow.Definition.ToJsonString());
        flow.Delete("z_reference"); Assert.Throws<InvalidOperationException>(() => flow.RecipeToNode("a_target"));
        flow.Undo(); Assert.Equal(before, flow.Definition.ToJsonString());
        var invalid = flow.Definition.DeepClone(); invalid!["nodes"]![2]!["parameters"]!["upperLimit"] = Binding("a_target", "value");
        Assert.Throws<ArgumentException>(() => flow.Load(invalid.ToJsonString())); Assert.Equal(before, flow.Definition.ToJsonString());
    }
    [Fact]
    public void CopyRemapsInternalBindingsAndInputsWithoutChangingPublicNames()
    {
        var flow = new FlowDocument(new NodeCatalog(VisionEngine.GetNodeCatalog())); flow.Load(Recipe().ToJsonString());
        string before = flow.Definition.ToJsonString();
        var ids = flow.DuplicateNodes(new[] { "z_reference", "a_target" });
        Assert.Equal(ids[0], flow.Node(ids[1])["parameters"]!["upperLimit"]!["node"]!.GetValue<string>());
        Assert.Contains(flow.Connections, e => e!["toNode"]!.GetValue<string>() == ids[1] && e["fromNode"]!.GetValue<string>() == "source");
        Assert.Equal("a_target", flow.Definition["outputs"]!["Measurement"]!["node"]!.GetValue<string>());
        var reload = new FlowDocument(flow.Catalog); reload.Load(flow.Definition.ToJsonString()); Assert.Equal(5, reload.Nodes.Count);
        flow.Undo(); Assert.Equal(before, flow.Definition.ToJsonString());
    }
    [Fact]
    public async Task LegacyDetectorWithUnavailableBoundParameterCannotPassExpectedZero()
    {
        var d = Recipe(); d["nodes"]![2]!["type"] = "CircleCaliper"; d["nodes"]![2]!["parameters"] = new JsonObject();
        var p = Pair(); p["lowerLimit"] = Binding("z_reference", "measurement.selected.radius");
        d["nodes"]![1]!["parameters"] = p;
        d["nodes"]!.AsArray().Add(Node("final", "CircleCaliper", new() { ["minimumCount"] = 0, ["maximumCount"] = 0 }));
        d["connections"]!.AsArray().Add(new JsonObject { ["fromNode"] = "a_target", ["fromPort"] = "image", ["toNode"] = "final", ["toPort"] = "input" });
        d["outputs"]!["Decision"] = new JsonObject { ["node"] = "final", ["port"] = "passed", ["type"] = "Bool" };
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession(); using var blank = Frame(true);
        using var result = await session.RunAsync(blank); Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Null(result.GetGeometricMeasurement("Measurement").Value);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

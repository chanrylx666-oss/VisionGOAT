using JPVision.Editor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ResultGraphTests
{
    private static JsonObject Port(string id, string type, bool hidden = false) => hidden
        ? new() { ["id"] = id, ["type"] = type, ["label"] = id, ["hidden"] = true }
        : new() { ["id"] = id, ["type"] = type, ["label"] = id };
    private static JsonArray ResultPorts() => new(
        Port("features", "Features", true), Port("measurement", "Measurement", true), Port("count", "Int", true),
        Port("found", "Bool", true), Port("passed", "Bool", true), Port("overlay", "Overlay", true),
        new JsonObject { ["id"] = "result", ["type"] = "Result", ["label"] = "全部结果" });
    private static JsonObject Descriptor(string type, string? inputType = null, JsonArray? outputs = null, bool? toolboxVisible = null)
    {
        var value = new JsonObject
        {
            ["type"] = type, ["label"] = type, ["category"] = "test", ["version"] = 1,
            ["inputs"] = inputType is null ? new JsonArray() : new JsonArray(Port("input", inputType)),
            ["outputs"] = outputs ?? new JsonArray(), ["parameters"] = new JsonArray()
        };
        if (toolboxVisible.HasValue) value["toolboxVisible"] = toolboxVisible.Value;
        return value;
    }
    private static NodeCatalog Catalog()
    {
        var values = new JsonArray(
            Descriptor("Detect", "Image", ResultPorts()),
            Descriptor("Relay", "Features", ResultPorts()),
            Descriptor("FeaturesSink", "Features"), Descriptor("MeasurementSink", "Measurement"),
            Descriptor("CountSink", "Int"), Descriptor("BoolSink", "Bool"), Descriptor("OverlaySink", "Overlay"),
            Descriptor("ResultSink", "Result"), Descriptor("ImageSink", "Image"),
            Descriptor("PlainSource", outputs: new JsonArray(Port("image", "Image"))),
            Descriptor("BuiltInCount", "Features", new JsonArray(Port("value", "Int")), false));
        using var json = JsonDocument.Parse(values.ToJsonString());
        return new NodeCatalog(json.RootElement);
    }
    private static FlowDocument FlowWithSink(string sinkType)
    {
        var flow = new FlowDocument(Catalog());
        flow.Add("Detect", 10, 20, "detect"); flow.Add(sinkType, 200, 20, "sink");
        return flow;
    }

    [Fact]
    public void GeometryNodesHaveOneVisibleResultAndKeepTheirLegacyPorts()
    {
        var spec = Catalog()["Detect"];
        var visible = Assert.Single(spec.VisibleOutputs);
        Assert.Equal("result", visible.Id); Assert.Equal("Result", visible.Type); Assert.Equal("全部结果", visible.Label);
        Assert.False(visible.Hidden); Assert.Equal(7, spec.Outputs.Count);
        Assert.Equal(6, spec.Outputs.Count(p => p.Hidden));
        Assert.False(new PortSpec("image", "Image", "图像").Hidden);
    }

    [Fact]
    public void NewAutomaticExportsPublishOnlyTheVisibleResult()
    {
        var flow = new FlowDocument(Catalog()); flow.Add("Detect", 10, 20, "detect");
        var recipe = flow.PublishedRecipe();
        var binding = Assert.Single(recipe["outputs"]!.AsObject());
        Assert.Equal("detect_result", binding.Key);
        Assert.Equal("result", binding.Value!["port"]!.GetValue<string>());
        Assert.Equal("Result", binding.Value["type"]!.GetValue<string>());
        Assert.Empty(flow.Definition["outputs"]!.AsObject());
    }

    [Fact]
    public void ValidLegacyExportsAndDecisionRemainUnchangedOnLoadAndPublish()
    {
        var catalog = Catalog(); var flow = new FlowDocument(catalog); flow.Add("Detect", 10, 20, "detect");
        flow.Publish("Measurement", "detect", "measurement", false); flow.Publish("Decision", "detect", "passed", true);
        string legacy = flow.Definition["outputs"]!.ToJsonString();
        var loaded = new FlowDocument(catalog); loaded.Load(flow.Definition.ToJsonString());
        Assert.Equal("", loaded.PublicInterfaceIssue());
        var published = loaded.PublishedRecipe();
        Assert.Equal(legacy, published["outputs"]!.ToJsonString());
        Assert.Equal("inspection", published["kind"]!.GetValue<string>());
        Assert.Equal("Decision", published["decisionOutput"]!.GetValue<string>());
        Assert.Null(published["outputs"]!["detect_result"]);
    }

    [Fact]
    public void MissingLegacyReferencesAreRepairedUsingTheVisibleResult()
    {
        var flow = new FlowDocument(Catalog()); flow.Add("Detect", 10, 20, "detect");
        flow.Definition["outputs"] = new JsonObject { ["OldCount"] = new JsonObject { ["node"] = "deleted", ["port"] = "count", ["type"] = "Int" } };
        Assert.NotEmpty(flow.PublicInterfaceIssue());
        var published = flow.PublishedRecipe();
        Assert.Single(published["outputs"]!.AsObject());
        Assert.Equal("Result", published["outputs"]!["detect_result"]!["type"]!.GetValue<string>());
        Assert.Null(published["outputs"]!["OldCount"]);
    }

    [Theory]
    [InlineData("FeaturesSink", "features", "Features")]
    [InlineData("MeasurementSink", "measurement", "Measurement")]
    [InlineData("CountSink", "count", "Int")]
    [InlineData("OverlaySink", "overlay", "Overlay")]
    public void ResultConnectionsResolveUniqueFieldsAndStoreLegacyPortIds(string sinkType, string portId, string type)
    {
        var flow = FlowWithSink(sinkType);
        var candidate = Assert.Single(flow.GetConnectionCandidates("detect", "result", "sink", "input"));
        Assert.Equal(portId, candidate.Id); Assert.Equal(type, candidate.Type); Assert.True(candidate.Hidden);
        flow.Connect("detect", "result", "sink", "input");
        var edge = Assert.Single(flow.Connections);
        Assert.Equal(portId, edge!["fromPort"]!.GetValue<string>()); Assert.Null(edge["fromField"]);
        var reloaded = new FlowDocument(flow.Catalog); reloaded.Load(flow.Definition.ToJsonString());
        Assert.Equal(portId, Assert.Single(reloaded.Connections)!["fromPort"]!.GetValue<string>());
    }

    [Fact]
    public void ResultToResultKeepsTheSingleResultPort()
    {
        var flow = FlowWithSink("ResultSink");
        var candidate = Assert.Single(flow.GetConnectionCandidates("detect", "result", "sink", "input"));
        Assert.Equal("result", candidate.Id); Assert.False(candidate.Hidden);
        flow.Connect("detect", "result", "sink", "input");
        Assert.Equal("result", Assert.Single(flow.Connections)!["fromPort"]!.GetValue<string>());
    }

    [Fact]
    public void AmbiguousBooleanFieldsRequireSelectionAndEitherLegacyFieldCanBeConnected()
    {
        var flow = FlowWithSink("BoolSink");
        var candidates = flow.GetConnectionCandidates("detect", "result", "sink", "input");
        Assert.Equal(new[] { "found", "passed" }, candidates.Select(p => p.Id));
        string before = flow.Definition.ToJsonString();
        var error = Assert.Throws<InvalidOperationException>(() => flow.Connect("detect", "result", "sink", "input"));
        Assert.Contains("请选择字段", error.Message); Assert.Equal(before, flow.Definition.ToJsonString());
        flow.Connect("detect", "found", "sink", "input");
        Assert.Equal("found", Assert.Single(flow.Connections)!["fromPort"]!.GetValue<string>());
        flow.Disconnect("sink", "input"); flow.Connect("detect", "passed", "sink", "input");
        Assert.Equal("passed", Assert.Single(flow.Connections)!["fromPort"]!.GetValue<string>());
    }

    [Fact]
    public void UnsupportedResultProjectionsAndUnknownPortsHaveNoCandidates()
    {
        var flow = FlowWithSink("ImageSink");
        Assert.Empty(flow.GetConnectionCandidates("detect", "result", "sink", "input"));
        Assert.Empty(flow.GetConnectionCandidates("detect", "missing", "sink", "input"));
        Assert.Empty(flow.GetConnectionCandidates("detect", "result", "sink", "missing"));
        Assert.Empty(flow.GetConnectionCandidates("detect", "count", "sink", "input"));
        Assert.Throws<InvalidOperationException>(() => flow.Connect("detect", "result", "sink", "input"));
        Assert.Empty(flow.Connections);
    }

    [Fact]
    public void OrdinaryPortsStillRequireExactlyMatchingTypes()
    {
        var flow = new FlowDocument(Catalog());
        flow.Add("PlainSource", 10, 20, "source"); flow.Add("ImageSink", 200, 20, "image"); flow.Add("FeaturesSink", 200, 100, "features");
        Assert.Equal("image", Assert.Single(flow.GetConnectionCandidates("source", "image", "image", "input")).Id);
        Assert.Empty(flow.GetConnectionCandidates("source", "image", "features", "input"));
        flow.Connect("source", "image", "image", "input");
        Assert.Equal("image", Assert.Single(flow.Connections)!["fromPort"]!.GetValue<string>());
    }

    [Fact]
    public void ResultProjectionsStillRejectOccupiedInputsAndCyclesWithoutChangingTheGraph()
    {
        var flow = new FlowDocument(Catalog());
        flow.Add("Relay", 10, 20, "a"); flow.Add("Relay", 200, 20, "b"); flow.Add("Relay", 400, 20, "c");
        flow.Connect("a", "result", "b", "input");
        string before = flow.Definition.ToJsonString();
        var occupied = Assert.Throws<InvalidOperationException>(() => flow.Connect("c", "result", "b", "input"));
        Assert.Contains("已连接", occupied.Message);
        var cycle = Assert.Throws<InvalidOperationException>(() => flow.Connect("b", "result", "a", "input"));
        Assert.Contains("循环", cycle.Message);
        Assert.Throws<InvalidOperationException>(() => flow.Connect("c", "result", "c", "input"));
        Assert.Equal(before, flow.Definition.ToJsonString());
    }

    [Fact]
    public void HiddenBuiltInModulesRemainAvailableToLegacyRecipes()
    {
        var catalog = Catalog();
        Assert.True(catalog["Detect"].ToolboxVisible); Assert.True(catalog["PlainSource"].ToolboxVisible);
        Assert.False(catalog["BuiltInCount"].ToolboxVisible); Assert.Contains(catalog.All, p => p.Type == "BuiltInCount");
        var flow = new FlowDocument(catalog);
        flow.Add("Detect", 10, 20, "detect"); flow.Add("BuiltInCount", 200, 20, "count");
        flow.Connect("detect", "features", "count", "input");
        var reloaded = new FlowDocument(catalog); reloaded.Load(flow.Definition.ToJsonString());
        Assert.Equal("BuiltInCount", reloaded.Node("count")["type"]!.GetValue<string>());
        Assert.Equal("value", reloaded.PublishedRecipe()["outputs"]!["count_value"]!["port"]!.GetValue<string>());
    }
}

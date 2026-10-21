using JPVision.Editor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CoordinateBindingEditorTests
{
    private static NodeCatalog Catalog()
    {
        using var json = JsonDocument.Parse("""
        [
          {"type":"Source","label":"来源","category":"测试","version":1,
           "inputs":[],"outputs":[{"id":"image","type":"Image","label":"图像"},{"id":"result","type":"Result","label":"结果"}],"parameters":[],
           "fields":{"result":{"centerX":{"type":"Double","label":"圆心X","nullable":true},"count":{"type":"Int","label":"数量","nullable":false}}}},
          {"type":"Target","label":"目标","category":"测试","version":1,
           "inputs":[{"id":"input","type":"Image","label":"图像"}],"outputs":[{"id":"image","type":"Image","label":"图像"}],
           "parameters":[{"name":"roi","type":"roi","label":"ROI","default":{"shape":"annulus","cx":100,"cy":100,"innerRadius":30,"outerRadius":60,"startAngle":0,"endAngle":360},"bindable":false},
                         {"name":"offset","type":"number","label":"偏移","default":0,"bindable":true}]}]
        """);
        return new NodeCatalog(json.RootElement);
    }
    private static FlowDocument Flow()
    {
        var flow = new FlowDocument(Catalog());
        flow.Add("Source", 0, 0, "image"); flow.Add("Source", 0, 100, "circle"); flow.Add("Target", 300, 0, "unwrap");
        flow.Connect("image", "image", "unwrap", "input");
        return flow;
    }
    private static JsonObject Center(string node = "circle") => new() { ["node"] = node, ["port"] = "result", ["field"] = "centerX", ["fallback"] = 100.0 };
    private static JsonObject Variable(string scope, string name, double fallback = 0) => new() { ["scope"] = scope, ["variable"] = name, ["fallback"] = fallback };

    [Fact]
    public void ExternalImageInputParameterDoesNotTurnItsContainerIntoABinding()
    {
        var flow = new FlowDocument(new NodeCatalog(JPVision.Sdk.VisionEngine.GetNodeCatalog()));
        flow.Add("ExternalImage", 0, 0, "source"); flow.Add("ToGray", 200, 0, "gray");
        flow.Connect("source", "image", "gray", "input");
        string json = flow.Definition.ToJsonString();
        var reloaded = new FlowDocument(flow.Catalog); reloaded.Load(json);
        Assert.Equal("InputImage", reloaded.GetParameterPath("source", "input")!.GetValue<string>());
        Assert.Equal(json, reloaded.Definition.ToJsonString());
    }

    [Fact]
    public void VariableNamesNodeAndFieldAreNotMistakenForAnOutputBinding()
    {
        var flow = Flow();
        flow.SetLocalVariables("unwrap", new()
        {
            ["node"] = new JsonObject { ["type"] = "Double", ["value"] = 100.0 },
            ["field"] = new JsonObject { ["type"] = "Double", ["value"] = 50.0 }
        });
        flow.SetParameterPath("unwrap", "roi.cx", Variable("local", "node", 100));
        var copied = flow.DuplicateNodes(new[] { "unwrap" });
        var reloaded = new FlowDocument(flow.Catalog); reloaded.Load(flow.Definition.ToJsonString());
        Assert.Equal(100, reloaded.Node(copied[0])["variables"]!["node"]!["value"]!.GetValue<double>());
    }

    [Fact]
    public void NestedCoordinateBindingsSurviveReloadAndParticipateInPreviewAncestors()
    {
        var flow = Flow(); flow.SetParameterPath("unwrap", "roi.cx", Center());
        Assert.Contains("circle", flow.Dependencies("unwrap"));
        Assert.Equal(100, flow.ResolvePreviewRoi("unwrap").X);
        Assert.Equal("circle", flow.GetParameterPath("unwrap", "roi.cx")!["node"]!.GetValue<string>());
        Assert.Contains(flow.ParameterSources("unwrap", "roi.cx"), source => source.Node == "circle" && source.Field.Field == "centerX");
        Assert.Equal(3, flow.RecipeToNode("unwrap")["nodes"]!.AsArray().Count);
        var input = flow.InputRecipe("unwrap");
        Assert.Equal(2, input["nodes"]!.AsArray().Count);
        Assert.Contains(input["previewNodes"]!.AsArray(), n => n!.GetValue<string>() == "circle");
        var reloaded = new FlowDocument(flow.Catalog); reloaded.Load(flow.Definition.ToJsonString());
        Assert.Equal(flow.Definition.ToJsonString(), reloaded.Definition.ToJsonString());
        flow.Undo(); Assert.Equal(100, flow.GetParameterPath("unwrap", "roi.cx")!.GetValue<int>());
    }

    [Fact]
    public void WholeRoiAssignmentsValidateNestedSubscriptionsBeforeChangingDocument()
    {
        var flow = Flow(); string before = flow.Definition.ToJsonString();
        var roi = flow.Node("unwrap")["parameters"]!["roi"]!.DeepClone().AsObject(); roi["cx"] = Center("missing");
        Assert.Throws<InvalidOperationException>(() => flow.SetParameter("unwrap", "roi", roi));
        Assert.Equal(before, flow.Definition.ToJsonString());
        roi["cx"] = Center(); flow.SetParameter("unwrap", "roi", roi);
        Assert.Contains("circle", flow.Dependencies("unwrap"));
    }

    [Fact]
    public void LocalExpressionReferencesAreRemappedWhenNodesAreDuplicated()
    {
        var flow = Flow();
        flow.SetGlobalVariables(new() { ["OffsetX"] = new JsonObject { ["type"] = "Double", ["value"] = 12.0 } });
        flow.SetLocalVariables("unwrap", new()
        {
            ["Center"] = new JsonObject
            {
                ["type"] = "Double", ["value"] = new JsonObject { ["expression"] = "cx + global.OffsetX", ["arguments"] = new JsonObject { ["cx"] = Center() } }
            }
        });
        flow.SetParameterPath("unwrap", "roi.cx", Variable("local", "Center", 100));
        var ids = flow.DuplicateNodes(new[] { "circle", "unwrap" });
        Assert.Equal(ids[0], flow.Node(ids[1])["variables"]!["Center"]!["value"]!["arguments"]!["cx"]!["node"]!.GetValue<string>());
        Assert.Contains(ids[0], flow.Dependencies(ids[1]));
        var reload = new FlowDocument(flow.Catalog); reload.Load(flow.Definition.ToJsonString());
        Assert.Equal(5, reload.Nodes.Count);
    }

    [Fact]
    public void VariableDeletionAndLocalCyclesAreRejectedAtomically()
    {
        var flow = Flow();
        flow.SetGlobalVariables(new() { ["OffsetX"] = new JsonObject { ["type"] = "Double", ["value"] = 5.0 } });
        flow.SetParameter("unwrap", "offset", Variable("global", "OffsetX"));
        string before = flow.Definition.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => flow.SetGlobalVariables(new())); Assert.Equal(before, flow.Definition.ToJsonString());
        var locals = new JsonObject
        {
            ["A"] = new JsonObject { ["type"] = "Double", ["value"] = Variable("local", "B") },
            ["B"] = new JsonObject { ["type"] = "Double", ["value"] = Variable("local", "A") }
        };
        Assert.Throws<InvalidOperationException>(() => flow.SetLocalVariables("unwrap", locals)); Assert.Equal(before, flow.Definition.ToJsonString());
    }

    [Fact]
    public void UserFunctionsAreSavedAndRecursiveDefinitionsAreRejected()
    {
        var flow = Flow();
        flow.SetFunctions(new() { ["shift"] = new JsonObject { ["parameters"] = new JsonArray("x", "dx"), ["expression"] = "x + dx" } });
        var reload = new FlowDocument(flow.Catalog); reload.Load(flow.Definition.ToJsonString());
        Assert.Equal("x + dx", reload.Definition["functions"]!["shift"]!["expression"]!.GetValue<string>());
        string before = flow.Definition.ToJsonString();
        Assert.Throws<InvalidOperationException>(() => flow.SetFunctions(new() { ["loop"] = new JsonObject { ["parameters"] = new JsonArray("x"), ["expression"] = "loop(x)" } }));
        Assert.Equal(before, flow.Definition.ToJsonString());
    }

    [Fact]
    public void PolygonVertexCoordinatesAcceptSubscriptionsWithoutLosingGeometry()
    {
        var flow = Flow();
        flow.SetParameter("unwrap", "roi", RoiRegion.Polygon(new[] { new EditorPoint(10, 10), new EditorPoint(100, 10), new EditorPoint(10, 100) }).ToJson());
        var binding = Center(); binding["fallback"] = 10.0;
        flow.SetParameterPath("unwrap", "roi.points.0.0", binding);
        Assert.Equal(10, flow.ResolvePreviewRoi("unwrap").Points[0].X);
        Assert.True(flow.GetParameterDefinition("unwrap", "roi.points.0.0")["bindable"]!.GetValue<bool>());
        Assert.Throws<InvalidOperationException>(() => flow.SetParameterPath("unwrap", "roi.points.7.0", binding));
    }
}

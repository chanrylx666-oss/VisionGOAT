using JPVision.Editor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class MatchPipelineGraphTests
{
    private static JsonObject Port(string id, string type, bool hidden = false) => new()
    {
        ["id"] = id, ["type"] = type, ["label"] = id, ["hidden"] = hidden
    };
    private static JsonArray GeometryOutputs(bool image) => new(
        new[]
        {
            Port("features", "Features", true), Port("measurement", "Measurement", true), Port("count", "Int", true),
            Port("found", "Bool", true), Port("passed", "Bool", true), Port("overlay", "Overlay", true)
        }.Concat(image ? new[] { Port("image", "Image", true) } : Array.Empty<JsonObject>())
         .Append(Port("result", "Result")).Select(p => (JsonNode)p).ToArray());
    private static JsonObject Node(string type, string? input, JsonArray outputs, JsonArray? parameters = null) => new()
    {
        ["type"] = type, ["label"] = type, ["category"] = "test", ["version"] = 1,
        ["inputs"] = input is null ? new JsonArray() : new JsonArray(Port("input", input)),
        ["outputs"] = outputs, ["parameters"] = parameters ?? new JsonArray()
    };
    private static NodeCatalog Catalog()
    {
        var nodes = new JsonArray(
            Node("ExternalImage", null, new JsonArray(Port("image", "Image"))),
            Node("FastTemplateMatch", "Image", GeometryOutputs(true)),
            Node("FindCircles", "Image", GeometryOutputs(false)),
            Node("BoolSink", "Bool", new JsonArray(Port("value", "Bool"))),
            Node("MaskSink", "Mask", new JsonArray(Port("mask", "Mask"))),
            Node("MaskSource", "Image", new JsonArray(Port("mask", "Mask", true), Port("result", "Result"))));
        using var json = JsonDocument.Parse(nodes.ToJsonString());
        return new NodeCatalog(json.RootElement);
    }
    private static FlowDocument Pipeline()
    {
        var flow = new FlowDocument(Catalog());
        flow.Add("ExternalImage", 10, 20, "source");
        flow.Add("FastTemplateMatch", 240, 20, "match");
        flow.Add("FindCircles", 470, 20, "circle");
        flow.Connect("source", "image", "match", "input");
        return flow;
    }

    [Fact]
    public void MatcherResultConnectsToCircleInputThroughItsDeclaredImagePort()
    {
        var flow = Pipeline();
        var candidate = Assert.Single(flow.GetConnectionCandidates("match", "result", "circle", "input"));
        Assert.Equal("image", candidate.Id); Assert.Equal("Image", candidate.Type); Assert.True(candidate.Hidden);
        flow.Connect("match", "result", "circle", "input");
        var edge = flow.Connections.Single(e => e!["toNode"]!.GetValue<string>() == "circle");
        Assert.Equal("image", edge!["fromPort"]!.GetValue<string>());
        Assert.Null(edge["fromField"]);
    }

    [Fact]
    public void AddingAnImageProjectionKeepsOnlyOneVisibleMatcherOutput()
    {
        var spec = Catalog()["FastTemplateMatch"];
        Assert.Equal("result", Assert.Single(spec.VisibleOutputs).Id);
        Assert.Equal(8, spec.Outputs.Count);
        Assert.True(spec.Outputs.Single(p => p.Id == "image").Hidden);
    }

    [Fact]
    public void CircleRoiPreviewPublishesTheActualMatchedImageBindingAndItsAncestors()
    {
        var flow = Pipeline(); flow.Connect("match", "result", "circle", "input");
        var preview = flow.InputRecipe("circle");
        var binding = Assert.Single(preview["outputs"]!.AsObject());
        Assert.Equal("InputPreview", binding.Key);
        Assert.Equal("match", binding.Value!["node"]!.GetValue<string>());
        Assert.Equal("image", binding.Value["port"]!.GetValue<string>());
        Assert.Equal("Image", binding.Value["type"]!.GetValue<string>());
        Assert.Equal(1, preview["schemaVersion"]!.GetValue<int>());
        Assert.Equal("processing", preview["kind"]!.GetValue<string>());
        Assert.Equal("", preview["decisionOutput"]!.GetValue<string>());
        Assert.Equal(new[] { "source", "match" }, preview["nodes"]!.AsArray().Select(n => FlowDocument.Id(n!)));
        var edge = Assert.Single(preview["connections"]!.AsArray());
        Assert.Equal("source", edge!["fromNode"]!.GetValue<string>());
        Assert.Equal("match", edge["toNode"]!.GetValue<string>());
        Assert.Null(preview["contentHash"]); Assert.Null(preview["previewNodes"]);
        // Native must publish a real frame for this typed Image binding; a Result metadata packet cannot replace it.
        Assert.Equal("Image", flow.Spec("match").Outputs.Single(p => p.Id == binding.Value["port"]!.GetValue<string>()).Type);
    }

    [Fact]
    public void ImageProjectionSurvivesPublishedRecipeAndLayoutReload()
    {
        var flow = Pipeline(); flow.Connect("match", "result", "circle", "input");
        flow.Move("circle", new EditorPoint(510, 90));
        var published = flow.PublishedRecipe();
        var output = Assert.Single(published["outputs"]!.AsObject());
        Assert.Equal("circle_result", output.Key); Assert.Equal("Result", output.Value!["type"]!.GetValue<string>());
        var loaded = new FlowDocument(flow.Catalog); loaded.Load(published.ToJsonString(), flow.LayoutJson().ToJsonString());
        var edge = loaded.Connections.Single(e => e!["toNode"]!.GetValue<string>() == "circle");
        Assert.Equal("image", edge!["fromPort"]!.GetValue<string>());
        Assert.Equal(new EditorPoint(510, 90), loaded.Positions["circle"]);
        Assert.Equal("", loaded.PublicInterfaceIssue());
        Assert.Equal("image", loaded.InputRecipe("circle")["outputs"]!["InputPreview"]!["port"]!.GetValue<string>());
    }

    [Fact]
    public void UndoAndRedoRestoreTheImageProjectionWithoutChangingTheUpstreamConnection()
    {
        var flow = Pipeline(); flow.Connect("match", "result", "circle", "input");
        Assert.Equal(2, flow.Connections.Count);
        flow.Undo(); Assert.Single(flow.Connections);
        Assert.Equal("match", flow.Connections[0]!["toNode"]!.GetValue<string>());
        flow.Redo(); Assert.Equal(2, flow.Connections.Count);
        var restored = flow.Connections.Single(e => e!["toNode"]!.GetValue<string>() == "circle");
        Assert.Equal("image", restored!["fromPort"]!.GetValue<string>());
    }

    [Fact]
    public void ImageProjectionsContinueToRejectOccupiedInputsAndCycles()
    {
        var flow = Pipeline(); flow.Connect("match", "result", "circle", "input");
        string before = flow.Definition.ToJsonString();
        var occupied = Assert.Throws<InvalidOperationException>(() => flow.Connect("source", "image", "circle", "input"));
        Assert.Contains("已连接", occupied.Message); Assert.Equal(before, flow.Definition.ToJsonString());

        flow.Add("FastTemplateMatch", 700, 20, "second");
        flow.Connect("match", "result", "second", "input");
        flow.Disconnect("match", "input");
        before = flow.Definition.ToJsonString();
        var cycle = Assert.Throws<InvalidOperationException>(() => flow.Connect("second", "result", "match", "input"));
        Assert.Contains("循环", cycle.Message); Assert.Equal(before, flow.Definition.ToJsonString());
    }

    [Fact]
    public void AResultWithoutADeclaredImageCannotBeConnectedAsPixels()
    {
        var flow = Pipeline();
        Assert.Empty(flow.GetConnectionCandidates("circle", "result", "match", "input"));
        Assert.Throws<InvalidOperationException>(() => flow.Connect("circle", "result", "match", "input"));
        Assert.Single(flow.Connections);
        flow.Add("MaskSink", 700, 20, "mask");
        // Declaring an Image does not grant an implicit Image-to-Mask conversion.
        Assert.Empty(flow.GetConnectionCandidates("match", "result", "mask", "input"));
    }

    [Fact]
    public void MaskProjectionUsesAnExplicitHiddenMaskPort()
    {
        var flow = Pipeline(); flow.Add("MaskSource", 700, 20, "maskSource"); flow.Add("MaskSink", 930, 20, "maskSink");
        flow.Connect("source", "image", "maskSource", "input");
        var candidate = Assert.Single(flow.GetConnectionCandidates("maskSource", "result", "maskSink", "input"));
        Assert.Equal("mask", candidate.Id); Assert.Equal("Mask", candidate.Type);
        flow.Connect("maskSource", "result", "maskSink", "input");
        Assert.Equal("mask", flow.InputRecipe("maskSink")["outputs"]!["InputPreview"]!["port"]!.GetValue<string>());
        Assert.Equal("Mask", flow.InputRecipe("maskSink")["outputs"]!["InputPreview"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ExplicitImageWiresAndPublicOutputsRemainValidAlongsideTheResultPort()
    {
        var flow = Pipeline(); flow.Connect("match", "image", "circle", "input");
        flow.Publish("LocatedImage", "match", "image", false);
        flow.Publish("MatchResults", "match", "result", false);
        string outputs = flow.Definition["outputs"]!.ToJsonString();
        var loaded = new FlowDocument(flow.Catalog); loaded.Load(flow.Definition.ToJsonString());
        Assert.Equal("", loaded.PublicInterfaceIssue()); Assert.Equal(outputs, loaded.PublishedRecipe()["outputs"]!.ToJsonString());
        Assert.Equal("Image", loaded.PublishedRecipe()["outputs"]!["LocatedImage"]!["type"]!.GetValue<string>());
        Assert.Contains("circle", loaded.ExecutionRecipe()["previewNodes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void AddingTheImageFieldDoesNotResolveAmbiguousBooleanResults()
    {
        var flow = Pipeline(); flow.Add("BoolSink", 700, 20, "judge");
        Assert.Equal(new[] { "found", "passed" }, flow.GetConnectionCandidates("match", "result", "judge", "input").Select(p => p.Id));
        Assert.Contains("请选择字段", Assert.Throws<InvalidOperationException>(() => flow.Connect("match", "result", "judge", "input")).Message);
        flow.Connect("match", "passed", "judge", "input");
        Assert.Equal("passed", flow.Connections.Single(e => e!["toNode"]!.GetValue<string>() == "judge")!["fromPort"]!.GetValue<string>());
    }
}

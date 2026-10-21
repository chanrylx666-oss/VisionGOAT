using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class EditorTests
{
    private readonly NodeCatalog catalog = new(VisionEngine.GetNodeCatalog());
    [Fact]
    public void RectangleCreatedInTheEditorCanBeReadBackBeforeSaving()
    {
        var region=RoiRegion.Rectangle(10,20,30,40);
        Assert.Equal(region,RoiRegion.FromJson(region.ToJson()));
    }
    [Fact]
    public void LoadingMalformedConnectionsRejectsThemWithoutReplacingCurrentFlow()
    {
        var flow=Chain(); string before=flow.Definition.ToJsonString(); var malformed=flow.Definition.DeepClone().AsObject();
        malformed["connections"]![0]!["fromNode"]="missing";
        Assert.Throws<ArgumentException>(()=>flow.Load(malformed.ToJsonString())); Assert.Equal(before,flow.Definition.ToJsonString());
        malformed=flow.Definition.DeepClone().AsObject(); malformed["connections"]!.AsArray().Add(new JsonObject { ["fromNode"]="gray",["fromPort"]="image",["toNode"]="gray",["toPort"]="input" });
        Assert.Throws<ArgumentException>(()=>flow.Load(malformed.ToJsonString())); Assert.Equal(before,flow.Definition.ToJsonString());
    }
    private FlowDocument Chain()
    {
        var flow = new FlowDocument(catalog);
        flow.Add("ExternalImage", 40, 40, "source"); flow.Add("ToGray", 250, 40, "gray"); flow.Add("Threshold", 470, 40, "binary");
        flow.Connect("source", "image", "gray", "input"); flow.Connect("gray", "image", "binary", "input"); return flow;
    }
    [Fact]
    public void ReplacingAFlowCanRunAndSaveWithoutDanglingPublicReferences()
    {
        var flow = Chain(); flow.Definition["outputs"] = new JsonObject { ["OldCount"] = new JsonObject { ["node"] = "deleted", ["port"] = "value", ["type"] = "Int" } };
        flow.Definition["kind"] = "inspection"; flow.Definition["decisionOutput"] = "Missing";
        var snapshot = flow.ExecutionRecipe(); Assert.Equal("processing", snapshot["kind"]!.GetValue<string>());
        Assert.Contains(snapshot["outputs"]!.AsObject(), o => o.Value!["node"]!.GetValue<string>() == "binary");
        Assert.NotNull(flow.Definition["outputs"]!["OldCount"]); Assert.NotEmpty(flow.PublicInterfaceIssue());
        var saved = flow.PublishedRecipe(); Assert.Null(saved["outputs"]!["OldCount"]); Assert.Equal("", saved["decisionOutput"]!.GetValue<string>());
    }
    [Fact]
    public void PreviewingNewBranchKeepsExistingPublicOutputNames()
    {
        var flow = Chain(); flow.Definition["outputs"] = new JsonObject { ["GrayImage"] = new JsonObject { ["node"] = "gray", ["port"] = "image", ["type"] = "Image" } };
        var snapshot = flow.ExecutionRecipe(); Assert.NotNull(snapshot["outputs"]!["GrayImage"]);
        Assert.Contains("binary", snapshot["previewNodes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
    [Fact]
    public void ACompletePublishedGraphKeepsTheSameRecipeHashInEditorAndSdk()
    {
        var flow=Chain(); var definition=flow.PublishedRecipe(); flow.Load(definition.ToJsonString());
        Assert.Null(flow.ExecutionRecipe()["previewNodes"]);
        Assert.Equal(VisionEngine.SealRecipe(flow.Definition.ToJsonString()),VisionEngine.SealRecipe(flow.ExecutionRecipe().ToJsonString()));
    }
    [Fact]
    public void ConnectionsRejectWrongTypesOccupiedInputsAndCycles()
    {
        var flow = Chain(); flow.Add("ToGray", 600, 100, "other");
        Assert.Throws<InvalidOperationException>(() => flow.Connect("binary", "mask", "other", "input"));
        Assert.Throws<InvalidOperationException>(() => flow.Connect("source", "image", "gray", "input"));
        flow.Connect("gray", "image", "other", "input"); flow.Disconnect("gray", "input");
        Assert.Throws<InvalidOperationException>(() => flow.Connect("other", "image", "gray", "input"));
    }
    [Fact]
    public void TemplateLearningAndRoiRemainSeparateAcrossUndoAndReload()
    {
        var flow = Chain(); string id = flow.Add("FastTemplateMatch", 650, 40, "match");
        var roi = RoiRegion.Circle(30, 30, 20); flow.SetParameter(id, "roi", roi.ToJson());
        byte[] pixels = Enumerable.Range(0, 64 * 64).Select(i => (byte)(i * 17 % 255)).ToArray();
        var model = TemplateLearning.Learn(pixels, 64, 64, 64, VisionPixelFormat.Gray8, RoiRegion.Rectangle(4, 6, 32, 30));
        flow.SetParameter(id, "model", model);
        Assert.Equal(30, flow.Node(id)["parameters"]!["roi"]!["cx"]!.GetValue<double>());
        flow.Undo(); Assert.Empty(flow.Node(id)["parameters"]!["model"]!.AsObject()); flow.Redo();
        var reloaded = new FlowDocument(catalog); reloaded.Load(flow.Definition.ToJsonString(), flow.LayoutJson().ToJsonString());
        Assert.Equal(model.ToJsonString(), reloaded.Node(id)["parameters"]!["model"]!.ToJsonString());
        Assert.Equal(roi.ToJson().ToJsonString(), reloaded.Node(id)["parameters"]!["roi"]!.ToJsonString());
    }
    [Fact]
    public void TeachingSnapshotUsesConnectedInputAndOmitsTheUntaughtMatcher()
    {
        var flow = Chain(); flow.Add("FastLocate", 680, 40, "match"); flow.Connect("gray", "image", "match", "input");
        var teaching = flow.InputRecipe("match");
        Assert.DoesNotContain(teaching["nodes"]!.AsArray(), n => n!["id"]!.GetValue<string>() == "match");
        Assert.Equal("gray", teaching["outputs"]!["InputPreview"]!["node"]!.GetValue<string>());
    }
    [Fact]
    public void PolygonMaskContainsOnlyDrawnPixelsAndBadRegionsAreRejected()
    {
        var poly = RoiRegion.Polygon(new[] { new EditorPoint(4, 4), new EditorPoint(24, 4), new EditorPoint(4, 24) });
        byte[] pixels = Enumerable.Range(0, 32 * 32).Select(i => (byte)(i * 7 % 255)).ToArray();
        var model = TemplateLearning.Learn(pixels, 32, 32, 32, VisionPixelFormat.Gray8, poly);
        var mask = Convert.FromBase64String(model["maskBase64"]!.GetValue<string>()); Assert.Contains((byte)0, mask); Assert.Contains((byte)255, mask);
        Assert.Throws<ArgumentException>(() => TemplateLearning.Learn(new byte[1024], 32, 32, 32, VisionPixelFormat.Gray8, poly));
        Assert.Throws<ArgumentException>(() => RoiRegion.Polygon(new[] {new EditorPoint(0,0),new EditorPoint(10,10),new EditorPoint(0,10),new EditorPoint(10,0)}));
    }
}

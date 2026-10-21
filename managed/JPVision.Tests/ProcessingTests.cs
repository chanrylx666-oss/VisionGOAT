using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ProcessingTests
{
    private readonly string root, store;

    public ProcessingTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
            directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private JsonObject GrayRecipe() => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1,
        ["recipeId"] = Guid.NewGuid().ToString(), ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "",
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject { ["GrayImage"] = new JsonObject { ["node"] = "gray", ["port"] = "image", ["type"] = "Image" } },
        ["nodes"] = new JsonArray(
            new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "gray", ["type"] = "ToGray", ["version"] = 1, ["parameters"] = new JsonObject() }),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "gray", ["toPort"] = "input" }),
        ["resources"] = new JsonArray()
    };

    private string Save(JsonObject definition)
    {
        var path = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalImageToGrayRunsWithoutBoolDecision(bool debug)
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(GrayRecipe()));
        Assert.Equal("processing", recipe.Description.GetProperty("kind").GetString());
        using var session = recipe.CreateSession();
        // BGR blue, green, red, white, plus a padded second row.
        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 9, 9,
                         255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 9, 9];
        using var frame = VisionFrame.CopyFrom(pixels, 4, 2, 14, VisionPixelFormat.Bgr24);
        using var result = await session.RunAsync(frame, new() { Debug = debug });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        using var gray = result.GetOutputImage("GrayImage");
        Assert.Equal(VisionPixelFormat.Gray8, gray.Info.PixelFormat);
        Assert.Equal(4, gray.Info.Width);
        Assert.Equal(2, gray.Info.Height);
        var actual = gray.CopyPixels();
        Assert.InRange(actual[0], (byte)28, (byte)30);
        Assert.InRange(actual[1], (byte)149, (byte)151);
        Assert.InRange(actual[2], (byte)75, (byte)77);
        Assert.Equal(255, actual[3]);
        if (debug)
        {
            using var intermediate = result.GetIntermediateImage("gray");
            Assert.Equal(VisionPixelFormat.Gray8, intermediate.Info.PixelFormat);
        }
    }

    [Fact]
    public async Task ProcessingBoolOutputDoesNotImplyInspectionPass()
    {
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe", "recipe.json")))!.AsObject();
        definition["kind"] = "processing";
        definition["decisionOutput"] = "";
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(definition));
        using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(new byte[120 * 50 * 3], 120, 50, 360, VisionPixelFormat.Bgr24);
        using var result = await session.RunAsync(frame, new() { Inputs = new Dictionary<string, object?> { ["ExpectedCount"] = 0 } });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(result.GetOutput<bool>("InspectionPassed"));
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
    }

    [Theory]
    [InlineData("inspection", "")]
    [InlineData("processing", "GrayImage")]
    public void InvalidDecisionContractsAreRejected(string kind, string decision)
    {
        var definition = GrayRecipe();
        definition["kind"] = kind;
        definition["decisionOutput"] = decision;
        using var engine = new VisionEngine(store);
        Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
    }

    [Fact]
    public void ProcessingStillRejectsDisconnectedAlgorithmInput()
    {
        var definition = GrayRecipe();
        definition["connections"] = new JsonArray();
        using var engine = new VisionEngine(store);
        Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreviewExecutesAnUnpublishedBinaryNodeAndRetainsItsActualPixels(bool debug)
    {
        var definition = GrayRecipe();
        definition["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "binary", ["type"] = "Threshold", ["version"] = 1, ["parameters"] = new JsonObject() });
        definition["connections"]!.AsArray().Add(new JsonObject { ["fromNode"] = "gray", ["fromPort"] = "image", ["toNode"] = "binary", ["toPort"] = "input" });
        definition["previewNodes"] = new JsonArray("binary");
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); using var session = recipe.CreateSession();
        byte[] pixels = [255,0,0, 0,255,0, 0,0,255, 255,255,255];
        using var frame = VisionFrame.CopyFrom(pixels, 4, 1, 12, VisionPixelFormat.Bgr24);
        using var result = await session.RunAsync(frame, new() { Debug = debug, PreviewNodeId = "binary" });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Equal("Succeeded", result.Data.GetProperty("nodes").GetProperty("binary").GetProperty("status").GetString());
        using var mask = result.GetIntermediateImage("binary"); Assert.Equal(new byte[] {0,255,0,255}, mask.CopyPixels());
        var metadata = result.GetNodeOutput<System.Text.Json.JsonElement>("binary", "mask"); Assert.Equal(2, metadata.GetProperty("foregroundPixels").GetInt32());
        Assert.True(result.Outputs.TryGetProperty("GrayImage", out _)); Assert.False(result.Outputs.TryGetProperty("binary", out _));
    }
}

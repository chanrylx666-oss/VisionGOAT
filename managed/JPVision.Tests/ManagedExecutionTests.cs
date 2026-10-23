using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ManagedExecutionTests : IDisposable
{
    private readonly string root;
    private readonly string store;
    public ManagedExecutionTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
        store = Path.Combine(root, "artifacts", "tests", "managed-execution-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private JsonObject Parts() => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe", "recipe.json")))!.AsObject();
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return path;
    }
    private static VisionFrame Frame()
    {
        byte[] pixels = new byte[120 * 50];
        for (int y = 10; y < 30; ++y)
            for (int n = 0; n < 3; ++n)
                for (int x = 5 + n * 40; x < 25 + n * 40; ++x) pixels[y * 120 + x] = 240;
        return VisionFrame.CopyFrom(pixels, 120, 50, 120, VisionPixelFormat.Gray8);
    }
    private async Task<InspectionResult> Run(string path, VisionExecutionBackend backend, VisionFrame frame, RunRequest request)
    {
        using var engine = new VisionEngine(store, backend);
        using var recipe = engine.LoadRecipe(path);
        await using var session = recipe.CreateSession();
        Assert.Equal(backend, recipe.ExecutionBackend);
        Assert.Equal(backend, session.ExecutionBackend);
        return await session.RunAsync(frame, request);
    }

    [Fact]
    public void DefaultEngineUsesManagedBusinessExecutionAndRejectsUnknownBackend()
    {
        using var engine = new VisionEngine(store);
        Assert.Equal(VisionExecutionBackend.Managed, engine.ExecutionBackend);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisionEngine(store, (VisionExecutionBackend)99));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "threshold")]
    [InlineData(true, "")]
    public async Task BusinessResultsPublicAliasesMasksAndRetainedImagesMatchLegacyExecutor(bool debug, string preview)
    {
        var definition = Parts();
        definition["outputs"]!["Mask"] = new JsonObject { ["node"] = "threshold", ["port"] = "mask", ["type"] = "Mask" };
        definition["outputs"]!["MaskAlias"] = definition["outputs"]!["Mask"]!.DeepClone();
        string path = Save(definition);
        using var frame = Frame();
        var request = new RunRequest { RunId = "parity", FrameId = "same-image", Debug = debug, PreviewNodeId = preview };
        using var managed = await Run(path, VisionExecutionBackend.Managed, frame, request);
        using var legacy = await Run(path, VisionExecutionBackend.Native, frame, request);
        Assert.Equal(ExecutionStatus.Succeeded, managed.Status);
        Assert.Equal(InspectionDecision.Pass, managed.Decision);
        Equivalent(legacy.Data, managed.Data);
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeNativeStrings").GetInt64());
        Assert.False(managed.GetOutput<JsonElement>("Mask").TryGetProperty("foregroundPixels", out _));
        using var mask = managed.GetOutputImage("Mask");
        using var alias = managed.GetOutputImage("MaskAlias");
        Assert.Equal(mask.CopyPixels(), alias.CopyPixels());
        if (debug || preview.Length != 0)
        {
            using var managedImage = managed.GetIntermediateImage("threshold");
            using var nativeImage = legacy.GetIntermediateImage("threshold");
            Assert.Equal(nativeImage.Info, managedImage.Info);
            Assert.Equal(nativeImage.CopyPixels(), managedImage.CopyPixels());
            using var input = managed.GetInputImage("threshold");
            using var nativeInput = legacy.GetInputImage("threshold");
            Assert.Equal(nativeInput.CopyPixels(), input.CopyPixels());
        }
    }

    [Fact]
    public async Task DisconnectedPreviewOnlyTerminalIsExecutedAndRetainedWithoutPublicUses()
    {
        var definition = Parts();
        definition["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "preview", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() });
        definition["previewNodes"] = new JsonArray("preview");
        string path = Save(definition);
        using var frame = Frame();
        var request = new RunRequest { RunId = "preview-only", FrameId = "frame", PreviewNodeId = "preview" };
        using var managed = await Run(path, VisionExecutionBackend.Managed, frame, request);
        using var legacy = await Run(path, VisionExecutionBackend.Native, frame, request);
        Assert.Equal(ExecutionStatus.Succeeded, managed.Status);
        Equivalent(legacy.Data, managed.Data);
        using var image = managed.GetIntermediateImage("preview");
        Assert.Equal(frame.CopyPixels(), image.CopyPixels());
    }

    [Fact]
    public async Task ManagedVariablesFunctionsAndPublicInputsPreserveRunIsolationAndSnapshots()
    {
        var definition = Parts();
        definition["variables"] = new JsonObject { ["Offset"] = new JsonObject { ["type"] = "Int", ["value"] = 0 } };
        definition["functions"] = new JsonObject { ["Add"] = new JsonObject { ["parameters"] = new JsonArray("a", "b"), ["expression"] = "a + b" } };
        var judge = definition["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "judge")!;
        judge["variables"] = new JsonObject { ["offset"] = new JsonObject { ["type"] = "Int", ["value"] = new JsonObject { ["variable"] = "Offset", ["scope"] = "global" } } };
        judge["parameters"]!["expected"] = new JsonObject
        {
            ["expression"] = "Add(n, local.offset)",
            ["arguments"] = new JsonObject { ["n"] = new JsonObject { ["input"] = "ExpectedCount" } }
        };
        string path = Save(definition);
        using var frame = Frame();
        foreach (int offset in new[] { 0, 1, 0 })
        {
            var request = new RunRequest { RunId = "variables", FrameId = "frame", Debug = true,
                Variables = new Dictionary<string, object?> { ["Offset"] = offset } };
            using var managed = await Run(path, VisionExecutionBackend.Managed, frame, request);
            using var legacy = await Run(path, VisionExecutionBackend.Native, frame, request);
            Assert.Equal(ExecutionStatus.Succeeded, managed.Status);
            Assert.Equal(offset == 0 ? InspectionDecision.Pass : InspectionDecision.Fail, managed.Decision);
            Equivalent(legacy.Data, managed.Data);
        }
    }

    [Fact]
    public async Task KernelFailureKeepsEarlierDebugImagesButClearsPublicAndNodeResults()
    {
        var definition = Parts();
        definition["kind"] = "processing"; definition["decisionOutput"] = "";
        definition["nodes"] = new JsonArray(
            new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "gray", ["type"] = "ToGray", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "bad", ["type"] = "CropRect", ["version"] = 1, ["parameters"] = new JsonObject { ["x"] = 500, ["y"] = 0, ["width"] = 10, ["height"] = 10 } });
        definition["connections"] = new JsonArray(
            new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "gray", ["toPort"] = "input" },
            new JsonObject { ["fromNode"] = "gray", ["fromPort"] = "image", ["toNode"] = "bad", ["toPort"] = "input" });
        definition["outputs"] = new JsonObject { ["Image"] = new JsonObject { ["node"] = "bad", ["port"] = "image", ["type"] = "Image" } };
        string path = Save(definition);
        using var frame = Frame();
        var request = new RunRequest { RunId = "failure", FrameId = "frame", Debug = true };
        using var managed = await Run(path, VisionExecutionBackend.Managed, frame, request);
        using var legacy = await Run(path, VisionExecutionBackend.Native, frame, request);
        Assert.Equal(ExecutionStatus.Failed, managed.Status);
        Equivalent(legacy.Data, managed.Data);
        Assert.Empty(managed.Outputs.EnumerateObject());
        Assert.Empty(managed.Data.GetProperty("nodeOutputs").EnumerateObject());
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeNativeStrings").GetInt64());
        using var image = managed.GetIntermediateImage("gray");
        Assert.Equal(frame.CopyPixels(), image.CopyPixels());
    }

    private static void Equivalent(JsonElement expected, JsonElement actual)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal) { "elapsedMs", "prepareMs", "executeMs", "resultMs" };
        void Compare(JsonElement left, JsonElement right, string path)
        {
            Assert.True(left.ValueKind == right.ValueKind, path + ": different value kind");
            if (left.ValueKind == JsonValueKind.Object)
            {
                var l = left.EnumerateObject().Where(p => !skipped.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value);
                var r = right.EnumerateObject().Where(p => !skipped.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value);
                Assert.Equal(l.Keys.Order(StringComparer.Ordinal), r.Keys.Order(StringComparer.Ordinal));
                foreach (var item in l)
                {
                    // These two identities intentionally differ between independently accepted runs.
                    if (item.Key == "frameId" && item.Value.ValueKind == JsonValueKind.Number) continue;
                    if (item.Key == "coordinatePlane" && item.Value.ValueKind == JsonValueKind.String && item.Value.GetString()!.Contains('/'))
                    { Assert.Equal(item.Value.GetString()!.Split('/', 2)[1], r[item.Key].GetString()!.Split('/', 2)[1]); continue; }
                    Compare(item.Value, r[item.Key], path + "/" + item.Key);
                }
            }
            else if (left.ValueKind == JsonValueKind.Array)
            {
                Assert.Equal(left.GetArrayLength(), right.GetArrayLength());
                for (int i = 0; i < left.GetArrayLength(); ++i) Compare(left[i], right[i], path + "/" + i);
            }
            else if (left.ValueKind == JsonValueKind.Number) Assert.Equal(left.GetDouble(), right.GetDouble());
            else Assert.True(left.GetRawText() == right.GetRawText(), path + ": " + left.GetRawText() + " != " + right.GetRawText());
        }
        Compare(expected, actual, "result");
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

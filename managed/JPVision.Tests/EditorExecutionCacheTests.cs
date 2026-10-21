using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class EditorExecutionCacheTests : IDisposable
{
    private readonly string root, store, cacheRoot;

    public EditorExecutionCacheTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
            directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
        store = Path.Combine(root, "artifacts", "tests", "execution-cache-" + Guid.NewGuid().ToString("N"));
        cacheRoot = Path.Combine(store, "plans");
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private JsonObject Definition() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(root, "samples", "recipes", "parts-count.vrecipe", "recipe.json")))!.AsObject();

    private static JsonObject Parameters(JsonObject definition, string id) => definition["nodes"]!.AsArray()
        .Single(node => node!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();

    private static VisionFrame Squares(int count)
    {
        const int width = 120, height = 50;
        byte[] pixels = new byte[width * height];
        for (int n = 0; n < count; n++)
            for (int y = 10; y < 30; y++)
                for (int x = 5 + 40 * n; x < 25 + 40 * n; x++)
                    pixels[y * width + x] = 230;
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8);
    }

    private static RunRequest Request(int expected, string frameId = "test-frame") => new()
    {
        FrameId = frameId,
        Inputs = new Dictionary<string, object?> { ["ExpectedCount"] = expected }
    };

    private static void Succeeded(InspectionResult result, int count, InspectionDecision decision)
    {
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        Assert.Equal(count, result.GetOutput<int>("ObjectCount"));
        Assert.Equal(decision, result.Decision);
    }

    private static EditorExecutionTiming Timing(EditorExecutionCache cache, bool hit)
    {
        var timing = Assert.IsType<EditorExecutionTiming>(cache.LastRunTiming);
        Assert.Equal(hit, timing.CacheHit);
        Assert.True(double.IsFinite(timing.PreparationMs)); Assert.True(timing.PreparationMs >= 0);
        Assert.True(double.IsFinite(timing.ExecutionMs)); Assert.True(timing.ExecutionMs >= 0);
        Assert.True(timing.TotalMs >= timing.PreparationMs);
        Assert.True(timing.TotalMs >= timing.ExecutionMs);
        return timing;
    }

    [Fact]
    public async Task ReusedPlanReadsFreshPixelsAndRunIdentity()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        string definition = Definition().ToJsonString();
        using var first = Squares(1);
        using var second = Squares(2);
        using var a = await cache.RunAsync(definition, first, Request(1, "frame-one") with { RunId = "run-one" });
        Succeeded(a, 1, InspectionDecision.Pass); Timing(cache, false);
        using var b = await cache.RunAsync(definition, second, Request(2, "frame-two") with { RunId = "run-two" });
        Succeeded(b, 2, InspectionDecision.Pass); Timing(cache, true);
        Assert.Equal("run-one", a.RunId); Assert.Equal("frame-one", a.Data.GetProperty("frameId").GetString());
        Assert.Equal("run-two", b.RunId); Assert.Equal("frame-two", b.Data.GetProperty("frameId").GetString());
        // The prior result remains independently owned while the cached session runs another frame.
        Succeeded(a, 1, InspectionDecision.Pass);
    }

    [Fact]
    public async Task ReusedPlanDoesNotRememberPreviousRunInputs()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = Squares(1);
        string definition = Definition().ToJsonString();
        using (var pass = await cache.RunAsync(definition, frame, Request(1)))
            Succeeded(pass, 1, InspectionDecision.Pass);
        using (var fail = await cache.RunAsync(definition, frame, Request(2)))
            Succeeded(fail, 1, InspectionDecision.Fail);
        Timing(cache, true);
        // An omitted override must resolve to the recipe default (3), not the previous value (2).
        using var defaultInput = await cache.RunAsync(definition, frame, new RunRequest());
        Succeeded(defaultInput, 1, InspectionDecision.Fail); Timing(cache, true);
        using var three = Squares(3);
        using var defaultsPass = await cache.RunAsync(definition, three, new RunRequest());
        Succeeded(defaultsPass, 3, InspectionDecision.Pass); Timing(cache, true);
    }

    [Fact]
    public async Task EditingParametersReplacesPlanAndDoesNotInvalidatePreviousResult()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = Squares(2);
        var definition = Definition();
        using var original = await cache.RunAsync(definition.ToJsonString(), frame, Request(2));
        Succeeded(original, 2, InspectionDecision.Pass);
        string originalFolder = Assert.Single(Directory.GetDirectories(cacheRoot));
        Parameters(definition, "threshold")["value"] = 250;
        using (var changed = await cache.RunAsync(definition.ToJsonString(), frame, Request(0)))
            Succeeded(changed, 0, InspectionDecision.Pass);
        Timing(cache, false);
        Assert.False(Directory.Exists(originalFolder));
        Assert.Single(Directory.GetDirectories(cacheRoot));
        await cache.DisposeAsync();
        Assert.Empty(Directory.GetFileSystemEntries(cacheRoot));
        Succeeded(original, 2, InspectionDecision.Pass);
        cache.Dispose();
    }

    [Fact]
    public async Task InvalidNewRecipeFailsInsteadOfRunningThePreviouslyCachedPlan()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = Squares(1);
        string good = Definition().ToJsonString();
        using (var initial = await cache.RunAsync(good, frame, Request(1)))
            Succeeded(initial, 1, InspectionDecision.Pass);
        var invalid = Definition();
        invalid["outputs"]!["ObjectCount"]!["node"] = "missing-node";
        var error = await Assert.ThrowsAsync<VisionException>(async () =>
        {
            using var unexpected = await cache.RunAsync(invalid.ToJsonString(), frame, Request(1));
        });
        Assert.Equal(2, error.Code);
        Assert.Null(cache.LastRunTiming);
        Assert.Empty(Directory.GetFileSystemEntries(cacheRoot));
        using var recovered = await cache.RunAsync(good, frame, Request(1));
        Succeeded(recovered, 1, InspectionDecision.Pass); Timing(cache, false);
    }

    [Fact]
    public async Task DebugAndPreviewRequestsTakeEffectWithoutRecompilingTheRecipe()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = Squares(1);
        string definition = Definition().ToJsonString();
        using (var plain = await cache.RunAsync(definition, frame, Request(1)))
            Assert.Throws<ArgumentException>(() => plain.GetIntermediateImage("gray"));
        using (var debug = await cache.RunAsync(definition, frame, Request(1) with { Debug = true }))
        {
            using var image = debug.GetIntermediateImage("gray");
            Assert.Equal(frame.CopyPixels(), image.CopyPixels()); Timing(cache, true);
        }
        using (var preview = await cache.RunAsync(definition, frame, Request(1) with { PreviewNodeId = "gray" }))
        {
            using var image = preview.GetIntermediateImage("gray");
            Assert.Equal(frame.CopyPixels(), image.CopyPixels()); Timing(cache, true);
        }
        using var nextPlain = await cache.RunAsync(definition, frame, Request(1));
        Assert.Throws<ArgumentException>(() => nextPlain.GetIntermediateImage("gray")); Timing(cache, true);
    }

    [Fact]
    public async Task PlanStorageIsBoundedAndDisposalPreservesCallerFilesAndEngine()
    {
        using var engine = new VisionEngine(store);
        Directory.CreateDirectory(cacheRoot);
        string sentinel = Path.Combine(cacheRoot, "caller-owned.txt");
        File.WriteAllText(sentinel, "keep");
        await using var cache = new EditorExecutionCache(engine, cacheRoot + Path.DirectorySeparatorChar);
        using var frame = Squares(1);
        var definition = Definition();
        for (int i = 0; i < 5; i++)
        {
            Parameters(definition, "threshold")["value"] = 120 + i;
            using var result = await cache.RunAsync(definition.ToJsonString(), frame, Request(1));
            Succeeded(result, 1, InspectionDecision.Pass); Timing(cache, false);
            Assert.Single(Directory.GetDirectories(cacheRoot));
        }
        await cache.DisposeAsync(); await cache.DisposeAsync(); cache.Dispose();
        Assert.Empty(Directory.GetDirectories(cacheRoot));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Throws<ObjectDisposedException>(() => { _ = cache.RunAsync(definition.ToJsonString(), frame, new()); });
        // The cache does not own the engine supplied by the caller.
        engine.ValidateRecipe(VisionEngine.SealRecipe(definition.ToJsonString()));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotCreatePlanAndNextRunSucceeds()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = Squares(1);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        string definition = Definition().ToJsonString();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.RunAsync(definition, frame, Request(1), cancel.Token));
        Assert.False(Directory.Exists(cacheRoot));
        using var result = await cache.RunAsync(definition, frame, Request(1));
        Succeeded(result, 1, InspectionDecision.Pass); Timing(cache, false);
    }

    private JsonObject SlowDefinition()
    {
        var definition = Definition();
        var nodes = definition["nodes"]!.AsArray();
        var connections = definition["connections"]!.AsArray();
        foreach (var edge in connections)
            if (edge!["toNode"]!.GetValue<string>() == "threshold") edge["fromNode"] = "slow7";
        for (int i = 0; i < 8; i++)
        {
            nodes.Add(new JsonObject
            {
                ["id"] = $"slow{i}", ["type"] = "GaussianBlur", ["version"] = 1,
                ["parameters"] = new JsonObject { ["kernel"] = 101, ["sigma"] = 10 }
            });
            connections.Add(new JsonObject
            {
                ["fromNode"] = i == 0 ? "gray" : $"slow{i - 1}", ["fromPort"] = "image",
                ["toNode"] = $"slow{i}", ["toPort"] = "input"
            });
        }
        return definition;
    }

    [Fact]
    public async Task ConcurrentExecutionIsRejectedAndCancellationDoesNotPoisonTheCache()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = VisionFrame.CopyFrom(new byte[2048 * 2048], 2048, 2048, 2048, VisionPixelFormat.Gray8);
        using var cancel = new CancellationTokenSource();
        string slow = SlowDefinition().ToJsonString();
        var pending = cache.RunAsync(slow, frame, Request(0), cancel.Token);
        Assert.Throws<InvalidOperationException>(() => { _ = cache.RunAsync(slow, frame, Request(0)); });
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeRuns").GetInt32());
        using var small = Squares(1);
        using var result = await cache.RunAsync(Definition().ToJsonString(), small, Request(1));
        Succeeded(result, 1, InspectionDecision.Pass); Timing(cache, false);
    }

    [Fact]
    public async Task DisposeAsyncWaitsForAcceptedExecutionAndLeavesItsResultUsable()
    {
        using var engine = new VisionEngine(store);
        await using var cache = new EditorExecutionCache(engine, cacheRoot);
        using var frame = VisionFrame.CopyFrom(new byte[512 * 512], 512, 512, 512, VisionPixelFormat.Gray8);
        var pending = cache.RunAsync(SlowDefinition().ToJsonString(), frame, Request(0));
        await cache.DisposeAsync();
        Assert.True(pending.IsCompleted);
        using var result = await pending;
        Succeeded(result, 0, InspectionDecision.Pass);
        Assert.Empty(Directory.GetFileSystemEntries(cacheRoot));
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeRuns").GetInt32());
        Assert.Throws<ObjectDisposedException>(() => { _ = cache.RunAsync(Definition().ToJsonString(), frame, Request(0)); });
    }

    public void Dispose()
    {
        string path = Path.GetFullPath(store);
        if (!string.Equals(Path.GetDirectoryName(path), Path.Combine(root, "artifacts", "tests"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("execution-cache-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test fixture directory.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}

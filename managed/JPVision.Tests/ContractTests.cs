using JPVision.Sdk;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace JPVision.Tests;

public sealed class ContractTests : IDisposable
{
    private readonly string root, store;
    public ContractTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
            directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }
    private VisionSession Session(out VisionEngine engine)
    {
        engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe"));
        return recipe.CreateSession();
    }
    private static VisionFrame Squares(int count = 3, bool padded = true)
    {
        const int width = 120, height = 50;
        int stride = width * 3 + (padded ? 8 : 0);
        byte[] pixels = new byte[stride * height];
        for (int n = 0; n < count; n++)
            for (int y = 10; y < 30; y++)
                for (int x = 5 + 40 * n; x < 25 + 40 * n; x++)
                    for (int c = 0; c < 3; c++)
                        pixels[y * stride + x * 3 + c] = 255;
        return VisionFrame.CopyFrom(pixels, width, height, stride, VisionPixelFormat.Bgr24);
    }
    [Fact] public void MissingLicenseRejectsNativeEngine() => Assert.Equal(100, Assert.Throws<VisionLicenseException>(() => new VisionEngine(store + "-missing")).Code);
    [Fact]
    public async Task ActualPixelsProducePassAndFail()
    {
        using var session = Session(out var engine);
        using (engine)
        {
            using var three = Squares();
            using var pass = await session.RunAsync(three);
            Assert.Equal(3, pass.GetOutput<int>("ObjectCount"));
            Assert.Equal(InspectionDecision.Pass, pass.Decision);
            using var two = Squares(2);
            using var fail = await session.RunAsync(two);
            Assert.Equal(2, fail.GetOutput<int>("ObjectCount"));
            Assert.Equal(InspectionDecision.Fail, fail.Decision);
            Assert.NotEmpty(fail.GetOverlays());
        }
    }
    [Fact]
    public async Task AcceptedRunOwnsPixelsAfterManagedFrameDispose()
    {
        using var session = Session(out var engine);
        using (engine)
        {
            var frame = Squares();
            var task = session.RunAsync(frame);
            frame.Dispose();
            using var result = await task;
            Assert.Equal(InspectionDecision.Pass, result.Decision);
        }
    }
    [Fact] public void ShortPixelBufferRejected() => Assert.Throws<ArgumentException>(() => VisionFrame.CopyFrom(new byte[10], 120, 50, 360, VisionPixelFormat.Bgr24));
    [Fact]
    public async Task EarlyCancellationDoesNotPoisonNextRun()
    {
        using var session = Session(out var engine);
        using (engine)
        {
            using var frame = Squares();
            using var token = new CancellationTokenSource();
            token.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.RunAsync(frame, cancellationToken: token.Token));
            using var result = await session.RunAsync(frame);
            Assert.Equal(InspectionDecision.Pass, result.Decision);
        }
    }
    [Fact]
    public void TamperedSignedPayloadRejectedAndExistingGrantPreserved()
    {
        var path = Path.Combine(root, "artifacts", "activation", "current.vlic");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var payload = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
        payload["customer"] = "tampered";
        envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        string invalid = Path.Combine(store, "invalid.vlic");
        File.WriteAllText(invalid, envelope.ToJsonString());
        Assert.Equal(102, Assert.Throws<VisionLicenseException>(() => VisionLicensing.Import(invalid, store)).Code);
        Assert.True(VisionLicensing.GetStatus(store).IsValid);
    }
    [Fact]
    public void DuplicateLicenseFieldsRejected()
    {
        string invalid = Path.Combine(store, "duplicate.vlic");
        File.WriteAllText(invalid, "{\"payload\":\"AAAA\",\"payload\":\"BBBB\",\"signature\":\"AAAA\"}");
        Assert.Equal(101, Assert.Throws<VisionLicenseException>(() => VisionLicensing.Import(invalid, store)).Code);
    }
    [Fact]
    public async Task IndependentSessionsDoNotMixFrames()
    {
        using var first = Session(out var engine1);
        using var second = Session(out var engine2);
        using (engine1)
        using (engine2)
        {
            using var one = Squares(1);
            using var three = Squares(3);
            var a = first.RunAsync(one);
            var b = second.RunAsync(three);
            using var ra = await a;
            using var rb = await b;
            Assert.Equal(1, ra.GetOutput<int>("ObjectCount"));
            Assert.Equal(3, rb.GetOutput<int>("ObjectCount"));
        }
    }
    [Fact]
    public async Task ContinuousRunsKeepValidResults()
    {
        using var session = Session(out var engine);
        using (engine)
        {
            using var frame = Squares();
            for (int i = 0; i < 30; i++)
            {
                using var warm = await session.RunAsync(frame);
            }
            var baseline = VisionEngine.GetDiagnostics();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            long initialPrivate = process.PrivateMemorySize64;
            var samples = new List<object>();
            var times = new List<double>();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                var start = Stopwatch.GetTimestamp();
                using (var result = await session.RunAsync(frame))
                {
                    Assert.Equal(3, result.GetOutput<int>("ObjectCount"));
                }
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if ((i + 1) % 100 == 0)
                {
                    var state = VisionEngine.GetDiagnostics();
                    Assert.Equal(baseline.GetProperty("activeNativeHandles").GetInt64(), state.GetProperty("activeNativeHandles").GetInt64());
                    Assert.Equal(0, state.GetProperty("activeNativeStrings").GetInt64());
                    Assert.Equal(0, state.GetProperty("activeRuns").GetInt32());
                    process.Refresh();
                    samples.Add(new
                    {
                        runs = i + 1,
                        privateBytes = process.PrivateMemorySize64,
                        workingSet = process.WorkingSet64,
                        native = state
                    });
                }
            }
            times.Sort();
            process.Refresh();
            long delta = process.PrivateMemorySize64 - initialPrivate;
            Assert.True(delta < 96L * 1024 * 1024, $"Unexpected private memory growth: {delta}");
            string output = Path.Combine(root, "artifacts", "tests", "performance.json");
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new
            {
                runs = times.Count,
                p50 = times[499],
                p95 = times[949],
                totalMs = watch.Elapsed.TotalMilliseconds,
                privateMemoryDelta = delta,
                samples
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }
    [Fact]
    public void LoadedRecipeExposesStableBusinessInterface()
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe"));
        var description = recipe.Description;
        Assert.Equal("Image", description.GetProperty("inputs").GetProperty("InputImage").GetProperty("type").GetString());
        Assert.Equal("Int", description.GetProperty("outputs").GetProperty("ObjectCount").GetProperty("type").GetString());
    }
    [Fact]
    public async Task ConcurrentLicenseImportsRemainAtomic()
    {
        var path = Path.Combine(root, "artifacts", "activation", "current.vlic");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => VisionLicensing.Import(path, store))));
        Assert.True(VisionLicensing.GetStatus(store).IsValid);
    }
    private string Variant(Action<JsonObject> edit)
    {
        var recipe = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe", "recipe.json")))!.AsObject();
        edit(recipe);
        string directory = Path.Combine(store, "recipes", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString()));
        return directory;
    }
    [Fact]
    public async Task RoiBlurAndMorphologyPreserveOriginalCoordinates()
    {
        string directory = Variant(r =>
        {
            var nodes = r["nodes"]!.AsArray();
            nodes.Add(JsonNode.Parse("{\"id\":\"roi\",\"type\":\"CropRect\",\"version\":1,\"parameters\":{\"x\":40,\"y\":5,\"width\":40,\"height\":35}}"));
            nodes.Add(JsonNode.Parse("{\"id\":\"blur\",\"type\":\"GaussianBlur\",\"version\":1,\"parameters\":{\"kernel\":3,\"sigma\":0}}"));
            nodes.Add(JsonNode.Parse("{\"id\":\"morph\",\"type\":\"Morphology\",\"version\":1,\"parameters\":{\"operation\":\"open\",\"kernel\":3,\"iterations\":1}}"));
            var edges = r["connections"]!.AsArray();
            foreach (var value in edges)
            {
                var e = value!.AsObject();
                if (e["toNode"]!.GetValue<string>() == "gray")
                    e["fromNode"] = "roi";
                if (e["toNode"]!.GetValue<string>() == "threshold")
                    e["fromNode"] = "blur";
                if (e["toNode"]!.GetValue<string>() == "find")
                    e["fromNode"] = "morph";
            }
            void Edge(string from, string port, string to) => edges.Add(new JsonObject { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = "input" });
            Edge("source", "image", "roi");
            Edge("gray", "image", "blur");
            Edge("threshold", "mask", "morph");
        });
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(directory);
        using var session = recipe.CreateSession();
        using var frame = Squares();
        using var result = await session.RunAsync(frame, new RunRequest { Debug = true, Inputs = new Dictionary<string, object?> { { "ExpectedCount", 1 } } });
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Equal(1, result.GetOutput<int>("ObjectCount"));
        using var roi = result.GetIntermediateImage("roi");
        Assert.Equal(40, roi.Info.OriginX);
        Assert.Equal(5, roi.Info.OriginY);
        var contour = Assert.Single(result.GetOverlays(), o => o.Kind == "polyline");
        Assert.All(contour.Points, p => { Assert.InRange(p.X, 45, 64); Assert.InRange(p.Y, 10, 29); });
    }
    [Fact]
    public async Task FailedRoiDoesNotLeavePreviousPass()
    {
        string directory = Variant(r => { var nodes = r["nodes"]!.AsArray(); nodes.Add(JsonNode.Parse("{\"id\":\"roi\",\"type\":\"CropRect\",\"version\":1,\"parameters\":{\"x\":500,\"y\":0,\"width\":20,\"height\":20}}")); var edges = r["connections"]!.AsArray(); foreach (var e in edges) if (e!["toNode"]!.GetValue<string>() == "gray") e["fromNode"] = "roi"; edges.Add(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "roi", ["toPort"] = "input" }); });
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(directory);
        using var session = recipe.CreateSession();
        using var frame = Squares();
        using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.Outputs.EnumerateObject());
        Assert.Equal("roi", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        byte[] larger = new byte[600 * 50 * 3];
        for (int y = 0; y < 20; y++)
            for (int x = 500; x < 520; x++)
                for (int c = 0; c < 3; c++)
                    larger[(y * 600 + x) * 3 + c] = 255;
        using var valid = VisionFrame.CopyFrom(larger, 600, 50, 1800, VisionPixelFormat.Bgr24);
        using var recovered = await session.RunAsync(valid, new RunRequest { Inputs = new Dictionary<string, object?> { { "ExpectedCount", 1 } } });
        Assert.Equal(InspectionDecision.Pass, recovered.Decision);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, recovered.Data.GetProperty("error").ValueKind);
    }
    [Fact]
    public async Task GrayImageAndExplicitInputsWork()
    {
        using var color = Squares();
        var bgr = color.CopyPixels();
        byte[] gray = new byte[120 * 50];
        for (int i = 0; i < gray.Length; i++)
            gray[i] = bgr[i * 3];
        using var frame = VisionFrame.CopyFrom(gray, 120, 50, 120, VisionPixelFormat.Gray8);
        using var session = Session(out var engine);
        using (engine)
        {
            using var result = await session.RunAsync(frame, new RunRequest { Inputs = new Dictionary<string, object?> { { "ExpectedCount", 2 } } });
            Assert.Equal(3, result.GetOutput<int>("ObjectCount"));
            Assert.Equal(InspectionDecision.Fail, result.Decision);
        }
    }
    [Fact]
    public async Task UnknownOrWrongTypeInputsRejected()
    {
        using var session = Session(out var engine);
        using (engine)
        {
            using var frame = Squares();
            await Assert.ThrowsAsync<ArgumentException>(async () => await session.RunAsync(frame, new RunRequest { Inputs = new Dictionary<string, object?> { { "Typo", 3 } } }));
            await Assert.ThrowsAsync<ArgumentException>(async () => await session.RunAsync(frame, new RunRequest { Inputs = new Dictionary<string, object?> { { "ExpectedCount", "three" } } }));
        }
    }
    private string SlowVariant() => Variant(r =>
    {
        var nodes = r["nodes"]!.AsArray();
        var edges = r["connections"]!.AsArray();
        foreach (var e in edges)
            if (e!["toNode"]!.GetValue<string>() == "threshold")
                e["fromNode"] = "slow7";
        for (int i = 0; i < 8; i++)
        {
            nodes.Add(new JsonObject { ["id"] = $"slow{i}", ["type"] = "GaussianBlur", ["version"] = 1, ["parameters"] = new JsonObject { ["kernel"] = 101, ["sigma"] = 10 } });
            edges.Add(new JsonObject { ["fromNode"] = i == 0 ? "gray" : $"slow{i - 1}", ["fromPort"] = "image", ["toNode"] = $"slow{i}", ["toPort"] = "input" });
        }
    });
    [Fact]
    public async Task BusyMidRunCancellationAndNextRunAreIsolated()
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(SlowVariant());
        using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(new byte[2048 * 2048], 2048, 2048, 2048, VisionPixelFormat.Gray8);
        using var cancel = new CancellationTokenSource();
        var first = session.RunAsync(frame, cancellationToken: cancel.Token);
        await Assert.ThrowsAsync<VisionException>(async () => await session.RunAsync(frame));
        await Task.Delay(10);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
        using var goodRecipe = engine.LoadRecipe(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe"));
        using var next = goodRecipe.CreateSession();
        using var small = Squares();
        using var result = await next.RunAsync(small);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
    }
    [Fact]
    public async Task TimeoutWaitsForNativeCompletionAndReleasesRun()
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(SlowVariant());
        using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(new byte[2048 * 2048], 2048, 2048, 2048, VisionPixelFormat.Gray8);
        await Assert.ThrowsAsync<TimeoutException>(async () => await session.RunAsync(frame, new RunRequest { TimeoutMs = 1 }));
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeRuns").GetInt32());
    }
    [Fact]
    public async Task DisposeDuringComputationCancelsAndWaitsSafely()
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(SlowVariant());
        var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(new byte[2048 * 2048], 2048, 2048, 2048, VisionPixelFormat.Gray8);
        var pending = session.RunAsync(frame);
        await session.DisposeAsync();
        session.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeRuns").GetInt32());
    }
    [Fact]
    public void ResourceHashesAndTraversalAreValidated()
    {
        string directory = Variant(r => r["resources"] = new JsonArray(new JsonObject { ["path"] = "../outside.txt", ["sha256"] = new string('0', 64) }));
        using var engine = new VisionEngine(store);
        Assert.Equal(2, Assert.Throws<VisionException>(() => engine.LoadRecipe(directory)).Code);
        directory = Variant(r => r["resources"] = new JsonArray(new JsonObject { ["path"] = "assets/model.bin", ["sha256"] = new string('0', 64) }));
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        File.WriteAllText(Path.Combine(directory, "assets", "model.bin"), "corrupted");
        Assert.Equal(2, Assert.Throws<VisionException>(() => engine.LoadRecipe(directory)).Code);
    }
    [Theory]
    [InlineData("wrong-machine", 104)]
    [InlineData("wrong-product", 105)]
    [InlineData("unknown-key", 103)]
    [InlineData("bad-version", 107)]
    public void SignedInvalidGrantsAreRejectedWithoutReplacingValidGrant(string name, int code)
    {
        string fixture = Path.Combine(root, "artifacts", "tests", "license-fixtures", name + ".vlic");
        Assert.Equal(code, Assert.Throws<VisionLicenseException>(() => VisionLicensing.Import(fixture, store)).Code);
        Assert.True(VisionLicensing.GetStatus(store).IsValid);
    }
    [Fact]
    public void MissingRuntimeFeatureRejectsNativeEngine()
    {
        string isolated = store + "-no-runtime";
        string fixture=Path.Combine(root,"artifacts","tests","license-fixtures","no-runtime.vlic");
        Assert.Equal(106,Assert.Throws<VisionLicenseException>(()=>VisionLicensing.Import(fixture,store)).Code);Assert.True(VisionLicensing.GetStatus(store).IsValid);
        Directory.CreateDirectory(isolated);File.Copy(fixture,Path.Combine(isolated,"license.vlic"));
        Assert.Equal(106, Assert.Throws<VisionLicenseException>(() => new VisionEngine(isolated)).Code);
    }
    [Fact]
    public void CorruptedOversizedLicenseAndUnsupportedRecipeRejected()
    {
        string path = Path.Combine(store, "bad.vlic");
        File.WriteAllText(path, new string('x', 65537));
        Assert.Equal(101, Assert.Throws<VisionLicenseException>(() => VisionLicensing.Import(path, store)).Code);
        File.WriteAllText(path, "not json");
        Assert.Equal(101, Assert.Throws<VisionLicenseException>(() => VisionLicensing.Import(path, store)).Code);
        using var engine = new VisionEngine(store);
        string directory = Variant(r => r["schemaVersion"] = 999);
        Assert.Equal(2, Assert.Throws<VisionException>(() => engine.LoadRecipe(directory)).Code);
        directory = Variant(r => { foreach (var node in r["nodes"]!.AsArray()) if (node!["type"]!.GetValue<string>() == "Threshold") node["parameters"]!["value"] = 999; });
        Assert.ThrowsAny<Exception>(() => engine.LoadRecipe(directory));
    }
    [Fact]
    public async Task OptionalInputsAndRenamedBusinessOutputsWork()
    {
        string directory = Variant(r => { var inputs = r["inputs"]!.AsObject(); inputs["UnusedLabel"] = new JsonObject { ["type"] = "String", ["required"] = false }; var outputs = r["outputs"]!.AsObject(); outputs["DetectedParts"] = outputs["ObjectCount"]!.DeepClone(); outputs.Remove("ObjectCount"); outputs["Graphics"] = outputs["ResultOverlay"]!.DeepClone(); outputs.Remove("ResultOverlay"); });
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(directory);
        using var session = recipe.CreateSession();
        using var frame = Squares();
        using var result = await session.RunAsync(frame);
        Assert.Equal(3, result.GetOutput<int>("DetectedParts"));
        Assert.NotEmpty(result.GetOverlays("Graphics"));
        Assert.Equal(InspectionDecision.Pass, result.Decision);
    }
    [Fact]
    public async Task PublicImageNameDoesNotShadowADifferentDebugNode()
    {
        string directory = Variant(r => r["outputs"]!["gray"] = new JsonObject { ["node"] = "source", ["port"] = "image", ["type"] = "Image" });
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(directory);
        using var session = recipe.CreateSession();
        using var frame = Squares();
        using var result = await session.RunAsync(frame, new RunRequest { Debug = true });
        using var intermediate = result.GetIntermediateImage("gray");
        using var image = result.GetOutputImage("gray");
        Assert.Equal(VisionPixelFormat.Gray8, intermediate.Info.PixelFormat);
        Assert.Equal(VisionPixelFormat.Bgr24, image.Info.PixelFormat);
    }
    public void Dispose()
    {/* Retain isolated test evidence; no recursive deletion of user data. */
    }
}

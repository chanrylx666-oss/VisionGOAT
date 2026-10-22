using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Xunit;

namespace JPVision.Tests;

// Identity is a plumbing fixture, not a trained defect model or an accuracy benchmark.
public sealed class DeepLearningRuntimeTests : IDisposable
{
    private readonly string directory;
    private readonly string store;
    private readonly string repository;
    private readonly string key;
    private readonly string projectRoot;
    public DeepLearningRuntimeTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        projectRoot = root!.FullName;
        directory = Path.Combine(root!.FullName, "artifacts", "tests", "dl-runtime-" + Guid.NewGuid());
        store = Path.Combine(directory, "license"); repository = Path.Combine(directory, "models");
        Directory.CreateDirectory(directory);
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
        key = InstallFixture(repository);
    }
    internal static string InstallFixture(string repository, int modelWidth = 64, int manifestWidth = 64, bool invalidModel = false)
    {
        byte[] model = invalidModel ? new byte[] { 1, 2, 3 } : IdentityOnnx(modelWidth);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["modelId"] = "identity-fixture", ["version"] = "1.0.0",
            ["task"] = "binarySegmentation", ["modelFile"] = "model.onnx",
            ["modelSha256"] = Convert.ToHexString(SHA256.HashData(model)).ToLowerInvariant(),
            ["input"] = new JsonObject { ["name"] = "image", ["width"] = manifestWidth, ["height"] = 64,
                ["channels"] = 1, ["layout"] = "NCHW", ["dtype"] = "float32", ["color"] = "Gray",
                ["scale"] = 1.0 / 255, ["mean"] = new JsonArray(0), ["std"] = new JsonArray(1) },
            ["output"] = new JsonObject { ["name"] = "scores", ["layout"] = "NCHW", ["dtype"] = "float32",
                ["activation"] = "none", ["scoreKind"] = "probability" },
            ["labels"] = new JsonArray("background", "defect")
        };
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        string key = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string folder = Path.Combine(repository, key); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), model);
        return key;
    }
    // ONNX protobuf: IR8/opset13, float32[1,1,64,W] Identity image -> scores.
    internal static byte[] IdentityOnnx(int width)
    {
        byte[] V(long n) { var b = new List<byte>(); do { byte x = (byte)(n & 127); n >>= 7; b.Add(n == 0 ? x : (byte)(x | 128)); } while (n != 0); return b.ToArray(); }
        byte[] Cat(params byte[][] b) => b.SelectMany(x => x).ToArray();
        byte[] I(int field, int n) => Cat(V(field << 3), V(n));
        byte[] B(int field, byte[] b) => Cat(V((field << 3) | 2), V(b.Length), b);
        byte[] S(int field, string s) => B(field, Encoding.UTF8.GetBytes(s));
        byte[] Info(string name) => Cat(S(1, name), B(2, B(1, Cat(I(1, 1), B(2,
            Cat(new[] { 1, 1, 64, width }.Select(x => B(1, I(1, x))).ToArray()))))));
        byte[] node = Cat(S(1, "image"), S(2, "scores"), S(4, "Identity"));
        byte[] graph = Cat(B(1, node), S(2, "JPVisionPlumbingFixture"), B(11, Info("image")), B(12, Info("scores")));
        return Cat(I(1, 8), S(2, "JPVision.Tests"), B(7, graph), B(8, I(2, 13)));
    }
    private JsonObject Definition(JsonObject? parameters = null) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "dl-test", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(
            new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "defect", ["type"] = "DeepLearningDefect", ["version"] = 1,
                ["parameters"] = Parameters(parameters) }),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "defect", ["toPort"] = "input" }),
        ["outputs"] = new JsonObject {
            ["Image"] = new JsonObject { ["node"] = "defect", ["port"] = "image", ["type"] = "Image" },
            ["Result"] = new JsonObject { ["node"] = "defect", ["port"] = "result", ["type"] = "Result" } }
    };
    private JsonObject Parameters(JsonObject? overrides)
    {
        var p = new JsonObject { ["modelRoot"] = repository, ["modelKey"] = key, ["minimumArea"] = 1, ["overlap"] = 16 };
        if (overrides is not null) foreach (var pair in overrides) p[pair.Key] = pair.Value?.DeepClone();
        return p;
    }
    private string Save(JsonObject definition)
    {
        string folder = Path.Combine(directory, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return folder;
    }
    private async Task<InspectionResult> Run(VisionFrame frame, JsonObject? parameters = null)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition(parameters)));
        await using var session = recipe.CreateSession(); return await session.RunAsync(frame);
    }
    private static VisionFrame Frame(int width = 130, int height = 98, bool defect = true)
    {
        byte[] pixels = new byte[width * height];
        if (defect) for (int y = 40; y <= 80 && y < height; y++) for (int x = 50; x <= 85 && x < width; x++) pixels[y * width + x] = 255;
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 700, -90);
    }
    [Fact]
    public void ToolboxHasOneImageInputAndOneImageOutput()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var spec = catalog["DeepLearningDefect"];
        Assert.True(spec.ToolboxVisible); Assert.Equal("Image", Assert.Single(spec.VisibleInputs).Type);
        Assert.Equal("Image", Assert.Single(spec.VisibleOutputs).Type);
    }
    [Fact]
    public async Task RealOnnxInferenceStitchesAcrossTilesAndKeepsCleanImage()
    {
        using var frame = Frame(); using var result = await Run(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        var packet = result.GetOutput<JsonElement>("Result");
        Assert.Equal("deepLearningDefect", packet.GetProperty("resultKind").GetString());
        Assert.Equal(1, packet.GetProperty("count").GetInt32()); Assert.False(packet.GetProperty("passed").GetBoolean());
        Assert.True(packet.GetProperty("tileCount").GetInt32() > 1);
        var region = Assert.Single(packet.GetProperty("features").EnumerateArray());
        Assert.Equal(1476, region.GetProperty("area").GetDouble());
        Assert.Equal(750, region.GetProperty("x").GetInt32()); Assert.Equal(-50, region.GetProperty("y").GetInt32());
        Assert.Equal(767.5, region.GetProperty("centroidX").GetDouble()); Assert.Equal(-30, region.GetProperty("centroidY").GetDouble());
        Assert.Equal(1.0, region.GetProperty("score").GetDouble(), 6);
        var typed = result.GetDeepLearningDefects("Result");
        Assert.Equal(1476, Assert.Single(typed.Features).Area);
        Assert.Equal(key, typed.ModelKey);
        Assert.Equal(typed.Count, result.GetDeepLearningDefects("Image").Count);
        Assert.All(packet.GetProperty("overlay").EnumerateArray(), o => Assert.Equal("#00FF00", o.GetProperty("color").GetString()));
        using var output = result.GetOutputImage("Image");
        Assert.Equal(frame.CopyPixels(), output.CopyPixels()); Assert.Equal(frame.Info, output.Info);
    }
    [Fact]
    public async Task NoDefectIsDistinctFromUnavailableOrFailure()
    {
        using var frame = Frame(defect: false); using var result = await Run(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        var p = result.GetOutput<JsonElement>("Result"); Assert.True(p.GetProperty("dataAvailable").GetBoolean());
        Assert.True(p.GetProperty("passed").GetBoolean()); Assert.Equal(0, p.GetProperty("count").GetInt32());
        Assert.Empty(p.GetProperty("features").EnumerateArray());
    }
    [Fact]
    public async Task ANewFrameDoesNotReusePreviousDefects()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition()));
        await using var session = recipe.CreateSession(); using var first = Frame(); using var empty = Frame(defect: false);
        using var a = await session.RunAsync(first); using var b = await session.RunAsync(empty);
        Assert.Equal(ExecutionStatus.Succeeded, a.Status); Assert.Equal(ExecutionStatus.Succeeded, b.Status);
        Assert.Equal(1, a.GetOutput<JsonElement>("Result").GetProperty("count").GetInt32());
        Assert.Equal(0, b.GetOutput<JsonElement>("Result").GetProperty("count").GetInt32());
    }
    [Fact]
    public async Task ModuleRoiClipsScoringButPreservesOriginalImage()
    {
        using var frame = Frame(); using var result = await Run(frame, new() { ["roi"] = RoiRegion.Rectangle(50, 40, 10, 10).ToJson() });
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        var p = result.GetOutput<JsonElement>("Result"); Assert.Equal(100, p.GetProperty("totalArea").GetDouble());
        var r = Assert.Single(p.GetProperty("features").EnumerateArray());
        Assert.Equal(750, r.GetProperty("x").GetInt32()); Assert.Equal(-50, r.GetProperty("y").GetInt32());
        using var output = result.GetOutputImage("Image"); Assert.Equal(frame.CopyPixels(), output.CopyPixels());
    }
    [Theory]
    [InlineData("missing")][InlineData("modelTampered")][InlineData("manifestTampered")][InlineData("onnxInvalid")][InlineData("shapeMismatch")]
    public async Task MissingTamperedAndIncompatibleModelsFailInsteadOfPassing(string problem)
    {
        var p = new JsonObject();
        if (problem == "missing") p["modelKey"] = new string('f', 64);
        else if (problem == "modelTampered") File.AppendAllText(Path.Combine(repository, key, "model.onnx"), "tampered");
        else if (problem == "manifestTampered") File.AppendAllText(Path.Combine(repository, key, "manifest.json"), " ");
        else p["modelKey"] = InstallFixture(repository, manifestWidth: problem == "shapeMismatch" ? 128 : 64, invalidModel: problem == "onnxInvalid");
        using var frame = Frame(); using var result = await Run(frame, p);
        Assert.NotEqual(ExecutionStatus.Succeeded, result.Status); Assert.NotEqual(InspectionDecision.Pass, result.Decision);
    }
    [Fact]
    public async Task MissingOptionalRuntimeFailsSafelyAndSameProcessCanRetryAfterInstallation()
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = projectRoot, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "run", "--project", "tools/DeepLearningProbe/DeepLearningProbe.csproj", "-c", "Debug", "--",
            projectRoot, Path.Combine(repository, key, "manifest.json"), Path.Combine(directory, "isolated-probe") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Optional runtime probe timed out."); }
        Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "isolated-probe", "verification.json")));
        Assert.True(evidence.RootElement.GetProperty("DataAvailable").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("cleanPixels").GetBoolean());
    }
    public void Dispose()
    {
        // Each test owns a unique directory below artifacts/tests. Never delete user models.
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

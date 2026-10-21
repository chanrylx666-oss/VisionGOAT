using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class FastMatchingTests : IDisposable
{
    private readonly string store;
    public FastMatchingTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("Project root missing.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }
    private static byte Pattern(int x, int y) => (byte)(30 + (x * 13 + y * 17 + x * y * 3) % 200);
    private static JsonObject Model(bool mask = false)
    {
        const int w = 48, h = 40; byte[] bytes = new byte[w * h], valid = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { bytes[y * w + x] = Pattern(x, y); valid[y * w + x] = !mask || (x - 24) * (x - 24) + (y - 20) * (y - 20) < 17 * 17 ? (byte)255 : (byte)0; }
        var model = new JsonObject { ["width"] = w, ["height"] = h, ["grayBase64"] = Convert.ToBase64String(bytes) };
        if (mask) model["maskBase64"] = Convert.ToBase64String(valid); return model;
    }
    private static JsonObject Definition(string type, JsonObject? p = null)
    {
        p ??= new(); p["model"] ??= Model(); p["scoreThreshold"] ??= .96;
        JsonObject Node(string id, string name, JsonObject parameters) => new() { ["id"] = id, ["type"] = name, ["version"] = 1, ["parameters"] = parameters };
        return new() { ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "fast-match-test", ["recipeVersion"] = 1,
            ["kind"] = "processing", ["decisionOutput"] = "", ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["nodes"] = new JsonArray(Node("source", "ExternalImage", new()), Node("match", type, p)),
            ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "match", ["toPort"] = "input" }),
            ["outputs"] = new JsonObject { ["Features"] = new JsonObject { ["node"] = "match", ["port"] = "features", ["type"] = "Features" }, ["MatchMeasurement"] = new JsonObject { ["node"] = "match", ["port"] = "measurement", ["type"] = "Measurement" }, ["ResultOverlay"] = new JsonObject { ["node"] = "match", ["port"] = "overlay", ["type"] = "Overlay" } }, ["resources"] = new JsonArray() };
    }
    private static VisionFrame Image(bool empty = false, bool gray = true, int originX = 0, int originY = 0)
    {
        const int w = 480, h = 320; int channels = gray ? 1 : 3, stride = w * channels + 5;
        byte[] bytes = new byte[stride * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            byte pixel = !empty && x >= 61 && x < 109 && y >= 73 && y < 113 ? Pattern(x - 61, y - 73)
                : !empty && x >= 317 && x < 365 && y >= 211 && y < 251 ? Pattern(x - 317, y - 211) : (byte)12;
            for (int c = 0; c < channels; c++) bytes[y * stride + x * channels + c] = pixel;
        }
        return VisionFrame.CopyFrom(bytes, w, h, stride, gray ? VisionPixelFormat.Gray8 : VisionPixelFormat.Bgr24, originX, originY);
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        using var recipe = engine.LoadRecipe(path); await using var session = recipe.CreateSession(); return await session.RunAsync(frame);
    }
    [Theory]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(4, true)] [InlineData(2, false)]
    public async Task FastMatchingFindsOddPixelTranslationsAndRefinesAtFullResolution(int factor, bool gray)
    {
        using var frame = Image(gray: gray); using var result = await Run(Definition("FastTemplateMatch", new() { ["searchDownsample"] = factor }), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        var matches = result.GetMatches(); Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.X == 61 && m.Y == 73); Assert.Contains(matches, m => m.X == 317 && m.Y == 211);
        Assert.All(matches, m => { Assert.InRange(m.Score, .96, 1); Assert.Equal(0, m.AngleDegrees); Assert.Equal(1, m.Scale); Assert.Equal(m.X + 24, m.CenterX); });
    }
    [Fact]
    public async Task FastLocateReturnsOnlyTheBestObjectWithMeasuredBox()
    {
        using var frame = Image(); using var result = await Run(Definition("FastLocate"), frame);
        var match = Assert.Single(result.GetMatches()); Assert.Equal(48, match.Width); Assert.Equal(40, match.Height);
        var measurement = result.GetMeasurement<VisionMatch>("MatchMeasurement"); Assert.True(measurement.SelectedValid); Assert.Equal(1, measurement.Count);
        Assert.Equal(match.CenterX, measurement.Selected!.CenterX); Assert.NotEmpty(result.GetOverlays());
    }
    [Fact]
    public async Task SearchRoiAndExternalOriginAreAppliedToFastMatchCoordinates()
    {
        using var frame = Image(originX: 600, originY: -20);
        using var result = await Run(Definition("FastTemplateMatch", new() { ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 280, ["y"] = 180, ["width"] = 130, ["height"] = 100 } }), frame);
        var m = Assert.Single(result.GetMatches()); Assert.Equal(917, m.X); Assert.Equal(191, m.Y);
    }
    [Theory]
    [InlineData("FastTemplateMatch")] [InlineData("FastLocate")]
    public async Task EmptyImageDoesNotInventAMatchedObject(string type)
    {
        using var frame = Image(empty: true); using var result = await Run(Definition(type), frame);
        Assert.Empty(result.GetMatches()); Assert.Empty(result.GetOverlays()); Assert.Null(result.GetNodeMeasurement<VisionMatch>("match").Selected);
    }
    [Fact]
    public async Task DrawnNonRectangularTemplateMaskIsUsedInMatching()
    {
        using var frame = Image(); using var result = await Run(Definition("FastTemplateMatch", new() { ["model"] = Model(mask: true), ["searchDownsample"] = 1 }), frame);
        Assert.Equal(2, result.GetMatches().Count); Assert.All(result.GetMatches(), m => Assert.InRange(m.Score, .96, 1));
    }
    [Fact]
    public void AnUntaughtFastTemplateIsRejected()
    {
        using var engine = new VisionEngine(store); var definition = Definition("FastTemplateMatch"); definition["nodes"]![1]!["parameters"]!["model"] = new JsonObject();
        var error = Assert.Throws<VisionException>(() => engine.ValidateRecipe(VisionEngine.SealRecipe(definition.ToJsonString())));
        Assert.Contains("Template has not been taught", error.Message);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

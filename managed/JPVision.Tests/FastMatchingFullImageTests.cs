using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class FastMatchingFullImageTests : IDisposable
{
    private const int Width = 5120, Height = 4096;
    private readonly string store;
    public FastMatchingFullImageTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "full-image-match-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static byte Pattern(int x, int y) => (byte)(30 + (x * 13 + y * 17 + x * y * 3) % 200);
    private static JsonObject Model(int width = 48, int height = 40, bool masked = false)
    {
        byte[] gray = new byte[width * height], mask = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            gray[y * width + x] = Pattern(x, y);
            mask[y * width + x] = !masked || (x - 24) * (x - 24) + (y - 20) * (y - 20) <= 17 * 17 ? (byte)255 : (byte)0;
        }
        var result = new JsonObject { ["width"] = width, ["height"] = height, ["grayBase64"] = Convert.ToBase64String(gray) };
        if (masked) result["maskBase64"] = Convert.ToBase64String(mask);
        return result;
    }
    private static VisionFrame Image((int X, int Y)[] targets, int width = Width, int height = Height,
        int modelWidth = 48, int modelHeight = 40, bool masked = false, bool noisy = false, bool weakerEarlyTargets = false)
    {
        byte[] pixels = new byte[width * height]; Array.Fill(pixels, (byte)12);
        if (noisy) for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)(20 + (x * 19 + y * 23) % 200);
        for (int n = 0; n < targets.Length; n++)
        for (int y = 0; y < modelHeight; y++) for (int x = 0; x < modelWidth; x++)
        {
            byte pixel = masked && (x - 24) * (x - 24) + (y - 20) * (y - 20) > 17 * 17 ? (byte)220 : Pattern(x, y);
            if (weakerEarlyTargets && n < targets.Length - 1) pixel = (byte)Math.Clamp(pixel + ((x * 7 + y * 11) % 21 - 10), 0, 255);
            pixels[(targets[n].Y + y) * width + targets[n].X + x] = pixel;
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 600, -20);
    }
    private static JsonObject Definition(string type, JsonObject parameters)
    {
        JsonObject Node(string id, string name, JsonObject? p = null) => new() { ["id"] = id, ["type"] = name, ["version"] = 1, ["parameters"] = p ?? new() };
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing", ["recipeId"] = "full-image-match-test", ["recipeVersion"] = 1,
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("match", type, parameters)),
            ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "match", ["toPort"] = "input" }),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject { ["Features"] = new JsonObject { ["node"] = "match", ["port"] = "features", ["type"] = "Features" }, ["Measurement"] = new JsonObject { ["node"] = "match", ["port"] = "measurement", ["type"] = "Measurement" } },
            ["decisionOutput"] = "", ["resources"] = new JsonArray()
        };
    }
    private async Task<InspectionResult> Run(string type, JsonObject parameters, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(Definition(type, parameters).ToJsonString()));
        using var recipe = engine.LoadRecipe(path); await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame, new() { Debug = false, TimeoutMs = 60000 });
        if (result.Status != ExecutionStatus.Succeeded)
        {
            string error = result.Data.GetProperty("error").GetRawText(); result.Dispose(); Assert.Fail(error);
        }
        return result;
    }
    private static JsonObject Parameters(int factor = 0, bool masked = false) => new() { ["model"] = Model(masked: masked), ["scoreThreshold"] = .99, ["searchDownsample"] = factor };
    private static byte SmoothPixel(int x, int y) => (byte)Math.Round(12 + 200 * Math.Exp(
        -Math.Pow(x - 23.5, 2) / (2 * 9 * 9) - Math.Pow(y - 19.5, 2) / (2 * 8 * 8)));
    private static JsonObject SmoothParameters()
    {
        byte[] gray = new byte[48 * 40];
        for (int y = 0; y < 40; y++) for (int x = 0; x < 48; x++) gray[y * 48 + x] = SmoothPixel(x, y);
        return new() { ["model"] = new JsonObject { ["width"] = 48, ["height"] = 40, ["grayBase64"] = Convert.ToBase64String(gray) },
            ["scoreThreshold"] = .90, ["searchDownsample"] = 1, ["candidateLimit"] = 8, ["maximumMatches"] = 8 };
    }
    private static VisionFrame SmoothImage((int X, int Y)[] targets, bool weakenLast = false)
    {
        const int width = 3000, height = 3000;
        byte[] pixels = Enumerable.Repeat((byte)12, width * height).ToArray();
        for (int n = 0; n < targets.Length; n++)
        for (int y = 0; y < 40; y++) for (int x = 0; x < 48; x++)
        {
            int noise = weakenLast && n == targets.Length - 1 ? ((x + y) % 2 == 0 ? 24 : -24) : 0;
            pixels[(targets[n].Y + y) * width + targets[n].X + x] = (byte)Math.Clamp(SmoothPixel(x, y) + noise, 0, 255);
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 600, -20);
    }
    [Theory]
    [InlineData("FastTemplateMatch", 0)] [InlineData("FastTemplateMatch", 1)]
    [InlineData("FastLocate", 0)] [InlineData("FastLocate", 1)]
    public async Task FullImageOverSixteenMegapixelsFindsTargetsAtTileSeamsAndLowerRight(string type, int factor)
    {
        var targets = new[] { (X: 1008, Y: 1011), (X: Width - 51, Y: Height - 45) };
        using var frame = Image(targets); using var result = await Run(type, Parameters(factor), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var matches = result.GetMatches(); Assert.Equal(type == "FastLocate" ? 1 : 2, matches.Count);
        if (type == "FastTemplateMatch") foreach (var at in targets) Assert.Contains(matches, m => m.X == at.X + 600 && m.Y == at.Y - 20);
        Assert.All(matches, m => { Assert.Contains(targets, at => m.X == at.X + 600 && m.Y == at.Y - 20); Assert.InRange(m.Score, .99, 1); });
    }
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public async Task ExplicitCoarseSearchRefinesAcrossTheWholeImage(int factor)
    {
        var targets = new[] { (X: 1008, Y: 1011), (X: Width - 51, Y: Height - 45) };
        using var frame = Image(targets); using var result = await Run("FastTemplateMatch", Parameters(factor), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(2, result.GetMatches().Count);
        foreach (var at in targets) Assert.Contains(result.GetMatches(), m => m.X == at.X + 600 && m.Y == at.Y - 20);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MaskedModelAndSourceValidityArePreservedDuringFullResolutionTiling(bool restrictSource)
    {
        var targets = new[] { (X: 1008, Y: 1011), (X: Width - 51, Y: Height - 45) };
        var p = Parameters(1, masked: true);
        if (restrictSource) p["roi"] = new JsonObject
        {
            ["shape"] = "polygon", ["points"] = new JsonArray(new JsonArray(0, 0), new JsonArray(Width - 1, 0),
                new JsonArray(Width - 1, Height - 70), new JsonArray(Width - 120, Height - 70), new JsonArray(Width - 120, Height - 1), new JsonArray(0, Height - 1))
        };
        using var frame = Image(targets, masked: true); using var result = await Run("FastTemplateMatch", p, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var matches = result.GetMatches(); Assert.Equal(restrictSource ? 1 : 2, matches.Count);
        Assert.Contains(matches, m => m.X == 1608 && m.Y == 991);
        if (restrictSource) Assert.DoesNotContain(matches, m => m.X == Width - 51 + 600 && m.Y == Height - 45 - 20);
    }
    [Fact]
    public async Task SmallTemplateIsNotSkippedBecauseTheWholeLargeImageHasLowVariance()
    {
        var at = (X: Width - 13, Y: Height - 17); var p = Parameters(0); p["model"] = Model(8, 8);
        using var frame = Image(new[] { at }, modelWidth: 8, modelHeight: 8); using var result = await Run("FastLocate", p, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var match = Assert.Single(result.GetMatches());
        Assert.Equal(at.X + 600, match.X); Assert.Equal(at.Y - 20, match.Y);
    }
    [Theory]
    [InlineData("FastLocate")] [InlineData("FastTemplateMatch")]
    public async Task CandidateLimitIsGlobalAndDoesNotPreferEarlierTilesOverBestScore(string type)
    {
        var targets = Enumerable.Range(0, 16).Select(i => (X: 50 + i * 250, Y: 200)).Append((X: Width - 51, Y: Height - 45)).ToArray();
        var p = Parameters(1); p["candidateLimit"] = 8; p["scoreThreshold"] = .90;
        if (type == "FastTemplateMatch") p["maximumMatches"] = 100;
        using var frame = Image(targets, weakerEarlyTargets: true); using var result = await Run(type, p, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var matches = result.GetMatches(); Assert.InRange(matches.Count, 1, type == "FastLocate" ? 1 : 8);
        Assert.Equal(Width - 51 + 600, matches[0].X); Assert.Equal(Height - 45 - 20, matches[0].Y);
        for (int i = 1; i < matches.Count; i++) Assert.True(matches[i - 1].Score >= matches[i].Score);
        Assert.Equal(matches.Count, matches.Select(m => (m.X, m.Y)).Distinct().Count());
    }
    [Fact]
    public async Task SeamDuplicatePeaksCannotEvictASeparateWeakerObjectBeforeGlobalNms()
    {
        // A 48x40 template partitions score positions at X=977 and Y=985, not at pixel 1024.
        // Each strong object touches four score tiles, giving almost-identical 1 px offset peaks.
        var targets = new[] { (X: 976, Y: 984), (X: 1953, Y: 1969), (X: 2800, Y: 2500) };
        using var frame = SmoothImage(targets, weakenLast: true);
        using var result = await Run("FastTemplateMatch", SmoothParameters(), frame);
        var matches = result.GetMatches(); Assert.Equal(3, matches.Count);
        foreach (var at in targets)
            Assert.Contains(matches, m => Math.Abs(m.X - at.X - 600) <= 1 && Math.Abs(m.Y - at.Y + 20) <= 1);
        Assert.InRange(matches[^1].Score, .90, .98);
    }
    [Fact]
    public async Task GlobalNmsKeepsNearbyObjectsWhoseBoxesRemainBelowTheOverlapThreshold()
    {
        var targets = new[] { (X: 380, Y: 500), (X: 423, Y: 500) };
        using var frame = SmoothImage(targets);
        using var result = await Run("FastTemplateMatch", SmoothParameters(), frame);
        var matches = result.GetMatches(); Assert.Equal(2, matches.Count);
        foreach (var at in targets) Assert.Contains(matches, m => Math.Abs(m.X - at.X - 600) <= 1 && Math.Abs(m.Y - at.Y + 20) <= 1);
    }
    [Fact]
    public async Task NondivisibleImageAndModelSizesKeepOriginalRefinedCoordinates()
    {
        const int width = 5003, height = 4001, modelWidth = 49, modelHeight = 41;
        var at = (X: width - modelWidth - 3, Y: height - modelHeight - 5);
        var p = Parameters(2); p["model"] = Model(modelWidth, modelHeight);
        using var frame = Image(new[] { at }, width, height, modelWidth, modelHeight);
        using var result = await Run("FastLocate", p, frame);
        var match = Assert.Single(result.GetMatches()); Assert.Equal(at.X + 600, match.X); Assert.Equal(at.Y - 20, match.Y);
        Assert.Equal(modelWidth, match.Width); Assert.Equal(modelHeight, match.Height);
    }
    [Fact]
    public async Task EightTimesCoarseSearchWithLargeEnoughModelRefinesBothObjects()
    {
        const int modelWidth = 128, modelHeight = 96;
        var targets = new[] { (X: 1008, Y: 1016), (X: Width - 136, Y: Height - 104) };
        var p = Parameters(8); p["model"] = Model(modelWidth, modelHeight);
        using var frame = Image(targets, modelWidth: modelWidth, modelHeight: modelHeight);
        using var result = await Run("FastTemplateMatch", p, frame);
        var matches = result.GetMatches(); Assert.Equal(2, matches.Count);
        foreach (var at in targets) Assert.Contains(matches, m => m.X == at.X + 600 && m.Y == at.Y - 20);
    }
    [Fact]
    public async Task FullImageWithoutTheTemplateReturnsNoFalseObject()
    {
        using var frame = Image(Array.Empty<(int, int)>(), noisy: true); using var result = await Run("FastTemplateMatch", Parameters(1), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Empty(result.GetMatches());
        var measurement = result.GetNodeMeasurement<VisionMatch>("match"); Assert.Equal(0, measurement.Count); Assert.Null(measurement.Selected);
    }
    [Fact]
    public async Task ExactThirtyTwoMegapixelInputRemainsSupported()
    {
        const int width = 8192, height = 4096; var at = (X: width - 51, Y: height - 45);
        using var frame = Image(new[] { at }, width, height); using var result = await Run("FastLocate", Parameters(1), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var match = Assert.Single(result.GetMatches()); Assert.Equal(at.X + 600, match.X); Assert.Equal(at.Y - 20, match.Y);
    }
    [Fact]
    public void ImageAndCandidateBudgetsStillRejectValuesBeyondTheirLimits()
    {
        const int width = 8193, height = 4096;
        Assert.Equal(5, Assert.Throws<VisionException>(() => VisionFrame.CopyFrom(new byte[width * height], width, height, width, VisionPixelFormat.Gray8)).Code);
        using var engine = new VisionEngine(store); var p = Parameters(); p["candidateLimit"] = 257;
        Assert.Throws<ArgumentException>(() => engine.ValidateRecipe(VisionEngine.SealRecipe(Definition("FastLocate", p).ToJsonString())));
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class MatcherCirclePipelineTests : IDisposable
{
    private readonly string store;
    public MatcherCirclePipelineTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "match-circle-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static byte[] Model()
    {
        var pixels = Enumerable.Repeat((byte)15, 128 * 96).ToArray();
        for (int y = 0; y < 96; y++) for (int x = 0; x < 128; x++)
        {
            if (x < 7 || x >= 121 || y < 7 || y >= 89) pixels[y * 128 + x] = (byte)(30 + (x * 13 + y * 17 + x * y * 3) % 200);
            if ((x - 36) * (x - 36) + (y - 48) * (y - 48) <= 18 * 18 ||
                (x - 94) * (x - 94) + (y - 48) * (y - 48) <= 12 * 12) pixels[y * 128 + x] = 230;
        }
        return pixels;
    }
    private static VisionFrame Image(bool matched = true, int originX = 0, int originY = 0, bool multiple = false, int objectX = 63, int objectY = 71, byte background = 15)
    {
        var pixels = Enumerable.Repeat(background, 600 * 400).ToArray();
        if (matched)
        {
            var model = Model();
            for (int y = 0; y < 96; y++) Buffer.BlockCopy(model, y * 128, pixels, (objectY + y) * 600 + objectX, 128);
            if (multiple) for (int y = 0; y < 96; y++) Buffer.BlockCopy(model, y * 128, pixels, (241 + y) * 600 + 343, 128);
        }
        // A strong unrelated circle must never become a target after an unsuccessful match.
        for (int y = 110; y < 190; y++) for (int x = 400; x < 480; x++)
            if ((x - 440) * (x - 440) + (y - 150) * (y - 150) <= 18 * 18) pixels[y * 600 + x] = 230;
        return VisionFrame.CopyFrom(pixels, 600, 400, 600, VisionPixelFormat.Gray8, originX, originY);
    }
    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string port, string to) => new() { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = "input" };
    private static JsonObject Output(string node, string port, string type) => new() { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Definition() => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing", ["recipeId"] = "match-circle", ["recipeVersion"] = 1,
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("match", "FastLocate", new()
        {
            ["model"] = new JsonObject { ["width"] = 128, ["height"] = 96, ["grayBase64"] = Convert.ToBase64String(Model()) },
            ["scoreThreshold"] = .96, ["searchDownsample"] = 1
        }), Node("circle", "FindCircles", new()
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 36.0, ["cy"] = 48.0, ["radius"] = 18.5 },
            ["searchLength"] = 16.0, ["caliperCount"] = 32, ["polarity"] = "lightToDark"
        })),
        ["connections"] = new JsonArray(Edge("source", "image", "match"), Edge("match", "image", "circle")),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject { ["CircleResult"] = Output("circle", "result", "Result"), ["MatchResult"] = Output("match", "result", "Result") },
        ["decisionOutput"] = "", ["resources"] = new JsonArray()
    };
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, RunRequest? request = null)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, request ?? new() { Debug = true });
    }
    private static JsonObject Parameters(JsonObject definition, string id) => definition["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();
    [Fact]
    public async Task MatchingThenCircleUsesLocatedPixelsAndOriginalCoordinates()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition())); await using var session = recipe.CreateSession();
        using var frame = Image(originX: 900, originY: -80); using var result = await session.RunAsync(frame, new() { Debug = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var match = result.GetResult<VisionMatch>("MatchResult"); Assert.Equal(1, match.Count);
        var circle = Assert.Single(result.GetResult<VisionCircle>("CircleResult").Features);
        Assert.InRange(circle.CenterX, 997.5, 1000.5); Assert.InRange(circle.CenterY, 37.5, 40.5); Assert.InRange(circle.Radius, 17, 19);
        using var input = result.GetIntermediateImage("match");
        Assert.Equal(128, input.Info.Width); Assert.Equal(96, input.Info.Height); Assert.Equal(963, input.Info.OriginX); Assert.Equal(-9, input.Info.OriginY);
        using var originalInput = result.GetInputImage("match"); Assert.Equal(600, originalInput.Info.Width); Assert.Equal(900, originalInput.Info.OriginX);
        using var circleInput = result.GetInputImage("circle"); Assert.Equal(input.CopyPixels(), circleInput.CopyPixels());
        Assert.Equal(963, circleInput.Info.OriginX); Assert.Equal(-9, circleInput.Info.OriginY);
    }
    [Theory]
    [InlineData("FastTemplateMatch")]
    [InlineData("TemplateMatch")]
    public async Task MultipleTargetsMeasureOnlyTheSelectedMatch(string type)
    {
        var definition = Definition(); var matcher = definition["nodes"]!.AsArray()[1]!; matcher["type"] = type;
        Parameters(definition, "match")["resultIndex"] = 1;
        if (type == "TemplateMatch") Parameters(definition, "match").Remove("searchDownsample");
        using var frame = Image(multiple: true); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var match = result.GetResult<VisionMatch>("MatchResult"); Assert.Equal(2, match.Count);
        var selected = match.Measurement.Selected!; Assert.NotEqual(match.Features[0].X, selected.X);
        var circle = Assert.Single(result.GetResult<VisionCircle>("CircleResult").Features);
        Assert.InRange(circle.CenterX, selected.X + 34.5, selected.X + 37.5);
        Assert.InRange(circle.CenterY, selected.Y + 46.5, selected.Y + 49.5);
    }
    [Fact]
    public async Task NoMatchOnTheNextFrameDoesNotReusePoseOrSearchUnrelatedCircles()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition())); await using var session = recipe.CreateSession();
        using (var first = Image()) using (var firstResult = await session.RunAsync(first, new() { Debug = true }))
            Assert.Single(firstResult.GetResult<VisionCircle>("CircleResult").Features);
        using var empty = Image(matched: false); using var result = await session.RunAsync(empty, new() { Debug = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var match = result.GetResult<VisionMatch>("MatchResult"); Assert.Equal(0, match.Count);
        var circle = result.GetResult<VisionCircle>("CircleResult"); Assert.Empty(circle.Features); Assert.Null(circle.Measurement.Selected); Assert.False(circle.Passed);
        Assert.False(circle.Image!.Value.GetProperty("available").GetBoolean());
        Assert.Throws<ArgumentException>(() => result.GetIntermediateImage("match"));
        Assert.Throws<ArgumentException>(() => result.GetInputImage("circle"));
        Assert.Throws<ArgumentException>(() => result.GetOutputImage("MatchResult"));
        using var originalInput = result.GetInputImage("match"); Assert.Equal(600, originalInput.Info.Width);
    }
    [Fact]
    public async Task OutOfRangeSelectionDoesNotFallBackToTheBestTarget()
    {
        var definition = Definition(); Parameters(definition, "match")["resultIndex"] = 10;
        using var frame = Image(); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var match = result.GetResult<VisionMatch>("MatchResult"); Assert.Equal(1, match.Count); Assert.False(match.Measurement.SelectedValid);
        Assert.Empty(result.GetResult<VisionCircle>("CircleResult").Features); Assert.Throws<ArgumentException>(() => result.GetOutputImage("MatchResult"));
    }
    [Fact]
    public async Task ParentCropAndSearchRoiPreserveTheOriginalCircleCoordinates()
    {
        var definition = Definition();
        definition["nodes"]!.AsArray().Insert(1, Node("gray", "ToGray", new() { ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 20, ["y"] = 30, ["width"] = 550, ["height"] = 350 } }));
        definition["connections"] = new JsonArray(Edge("source", "image", "gray"), Edge("gray", "image", "match"), Edge("match", "image", "circle"));
        Parameters(definition, "match")["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 40, ["y"] = 38, ["width"] = 134, ["height"] = 102 };
        using var frame = Image(originX: 900, originY: -80); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var circle = Assert.Single(result.GetResult<VisionCircle>("CircleResult").Features);
        Assert.InRange(circle.CenterX, 997.5, 1000.5); Assert.InRange(circle.CenterY, 37.5, 40.5);
        using var matcherInput = result.GetInputImage("match"); Assert.Equal(550, matcherInput.Info.Width); Assert.Equal(920, matcherInput.Info.OriginX); Assert.Equal(-50, matcherInput.Info.OriginY);
        using var crop = result.GetOutputImage("MatchResult"); Assert.Equal(963, crop.Info.OriginX); Assert.Equal(-9, crop.Info.OriginY); Assert.Equal(128, crop.Info.Width);
    }
    [Fact]
    public async Task PaddingKeepsLocalRoiStableWhenTheTargetIsNearTheImageEdge()
    {
        var definition = Definition(); Parameters(definition, "match")["downstreamPadding"] = 20;
        Parameters(definition, "circle")["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 56.0, ["cy"] = 68.0, ["radius"] = 18.5 };
        using var frame = Image(objectX: 2, objectY: 3); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var circle = Assert.Single(result.GetResult<VisionCircle>("CircleResult").Features);
        Assert.InRange(circle.CenterX, 36.5, 39.5); Assert.InRange(circle.CenterY, 49.5, 52.5);
        using var crop = result.GetOutputImage("MatchResult"); Assert.Equal(168, crop.Info.Width); Assert.Equal(136, crop.Info.Height); Assert.Equal(-18, crop.Info.OriginX); Assert.Equal(-17, crop.Info.OriginY);
        Assert.All(crop.CopyPixels().Take(168), pixel => Assert.Equal((byte)0, pixel));
    }
    [Fact]
    public async Task PublicResultImageWorksWithoutDebugAndKeepsPixelOwnership()
    {
        using var frame = Image(); VisionFrame crop;
        using (var result = await Run(Definition(), frame, new() { Debug = false }))
        {
            crop = result.GetOutputImage("MatchResult"); Assert.Equal(128, crop.Info.Width);
            Assert.Throws<ArgumentException>(() => result.GetInputImage("match"));
        }
        using (crop) Assert.Equal(Model(), crop.CopyPixels());
    }
    [Fact]
    public async Task LocatedImagePreservesTheParentPolygonValidityMask()
    {
        var definition = Definition();
        definition["nodes"]!.AsArray().Insert(1, Node("gray", "ToGray", new() { ["roi"] = new JsonObject { ["shape"] = "polygon", ["points"] = new JsonArray(
            new JsonArray(0, 0), new JsonArray(143, 0), new JsonArray(143, 140), new JsonArray(190, 140),
            new JsonArray(190, 0), new JsonArray(599, 0), new JsonArray(599, 399), new JsonArray(0, 399)) } }));
        definition["connections"] = new JsonArray(Edge("source", "image", "gray"), Edge("gray", "image", "match"), Edge("match", "image", "circle"));
        var mask = Enumerable.Repeat((byte)255, 128 * 96).ToArray();
        for (int y = 0; y < 96; y++) for (int x = 0; x < 128; x++)
            if (x + 63 >= 143 && x + 63 <= 190 && y + 71 <= 140) mask[y * 128 + x] = 0;
        Parameters(definition, "match")["model"]!["maskBase64"] = Convert.ToBase64String(mask);
        Parameters(definition, "circle")["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 94.0, ["cy"] = 48.0, ["radius"] = 12.5 };
        using var frame = Image(); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(1, result.GetResult<VisionMatch>("MatchResult").Count);
        Assert.Empty(result.GetResult<VisionCircle>("CircleResult").Features);
        using var crop = result.GetOutputImage("MatchResult"); Assert.Equal((byte)0, crop.CopyPixels()[48 * 128 + 94]);
    }
    [Theory]
    [InlineData("ToGray")]
    [InlineData("GaussianBlur")]
    public async Task MissingMatchRemainsUnavailableThroughImageProcessing(string type)
    {
        var definition = Definition(); definition["nodes"]!.AsArray().Insert(2, Node("process", type));
        definition["connections"] = new JsonArray(Edge("source", "image", "match"), Edge("match", "image", "process"), Edge("process", "image", "circle"));
        using var frame = Image(matched: false); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Empty(result.GetResult<VisionCircle>("CircleResult").Features);
        Assert.False(result.GetNodeOutput<JsonElement>("process", "image").GetProperty("available").GetBoolean());
        Assert.Throws<ArgumentException>(() => result.GetIntermediateImage("process"));
    }
    [Fact]
    public async Task PaddedImageBorderCannotBecomeAFalseDetectedLine()
    {
        var definition = Definition(); Parameters(definition, "match")["downstreamPadding"] = 20;
        var detector = definition["nodes"]!.AsArray().Last()!; detector["type"] = "FindLines";
        detector["parameters"] = new JsonObject
        {
            ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 148, ["y"] = 20, ["width"] = 20, ["height"] = 96 },
            ["edgeLow"] = 10.0, ["edgeHigh"] = 20.0, ["voteThreshold"] = 10, ["minimumLength"] = 40.0
        };
        using var frame = Image(objectX: 470, background: 200); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(1, result.GetResult<VisionMatch>("MatchResult").Count);
        Assert.Empty(result.GetResult<VisionLine>("CircleResult").Features);
        using var located = result.GetOutputImage("MatchResult"); Assert.Equal((byte)200, located.CopyPixels()[20 * 168 + 149]);
    }
    [Fact]
    public async Task ARealLineWithinValidPixelsIsStillDetectedOnAPaddedImage()
    {
        var definition = Definition(); Parameters(definition, "match")["downstreamPadding"] = 20;
        var detector = definition["nodes"]!.AsArray().Last()!; detector["type"] = "FindLines";
        detector["parameters"] = new JsonObject
        {
            ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 148, ["y"] = 20, ["width"] = 20, ["height"] = 96 },
            ["edgeLow"] = 10.0, ["edgeHigh"] = 20.0, ["voteThreshold"] = 10, ["minimumLength"] = 40.0
        };
        using var original = Image(objectX: 2, background: 200); var pixels = original.CopyPixels();
        for (int y = 60; y < 180; y++) for (int x = 140; x < 150; x++) pixels[y * 600 + x] = 0;
        using var frame = VisionFrame.CopyFrom(pixels, 600, 400, 600, VisionPixelFormat.Gray8); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(1, result.GetResult<VisionMatch>("MatchResult").Count);
        Assert.Contains(result.GetResult<VisionLine>("CircleResult").Features, line => Math.Abs(line.CenterX - 139) <= 1 && line.Length >= 40);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

/// <summary>Real matching and circle fitting, including a measured feature outside the taught template.</summary>
public sealed class MatchCenterPipelineTests : IDisposable
{
    private const int Width = 360, Height = 240, ModelWidth = 32, ModelHeight = 24;
    private readonly string store;

    public MatchCenterPipelineTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "match-center-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static byte[] Model(bool embeddedCircle = false)
    {
        var pixels = new byte[ModelWidth * ModelHeight];
        for (int y = 0; y < ModelHeight; y++) for (int x = 0; x < ModelWidth; x++)
            pixels[y * ModelWidth + x] = (byte)(25 + (x * 13 + y * 17 + x * y * 3) % 190);
        if (embeddedCircle)
            for (int y = 3; y < 21; y++) for (int x = 7; x < 25; x++)
                pixels[y * ModelWidth + x] = (x - 16) * (x - 16) + (y - 12) * (y - 12) <= 49 ? (byte)230 : (byte)15;
        return pixels;
    }

    private static VisionFrame Image(bool matched = true, int objectX = 40, int objectY = 50,
        int originX = 0, int originY = 0, bool multiple = false, bool bgr = false, bool embeddedCircle = false)
    {
        var pixels = Enumerable.Repeat((byte)15, Width * Height).ToArray();
        void Object(int x, int y)
        {
            if (matched)
            {
                var model = Model(embeddedCircle);
                for (int row = 0; row < ModelHeight; row++)
                    Buffer.BlockCopy(model, row * ModelWidth, pixels, (y + row) * Width + x, ModelWidth);
            }
            if (!embeddedCircle)
                for (int row = 0; row < Height; row++) for (int col = 0; col < Width; col++)
                    if ((col - x - 82) * (col - x - 82) + (row - y - 51) * (row - y - 51) <= 18 * 18)
                        pixels[row * Width + col] = 230;
        }
        Object(objectX, objectY);
        if (multiple) Object(220, 135);
        if (!bgr) return VisionFrame.CopyFrom(pixels, Width, Height, Width, VisionPixelFormat.Gray8, originX, originY);
        var color = new byte[pixels.Length * 3];
        for (int i = 0; i < pixels.Length; i++) color[i * 3] = color[i * 3 + 1] = color[i * 3 + 2] = pixels[i];
        return VisionFrame.CopyFrom(color, Width, Height, Width * 3, VisionPixelFormat.Bgr24, originX, originY);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
        { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string to) => new()
        { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = "input" };
    private static JsonObject Output(string node) => new() { ["node"] = node, ["port"] = "result", ["type"] = "Result" };
    private static JsonObject Definition(string matchType = "FastLocate", bool embeddedCircle = false) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing",
        ["recipeId"] = "match-center-circle", ["recipeVersion"] = 1,
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("match", matchType, new()
        {
            ["model"] = new JsonObject { ["width"] = ModelWidth, ["height"] = ModelHeight,
                ["grayBase64"] = Convert.ToBase64String(Model(embeddedCircle)) },
            ["scoreThreshold"] = .995, ["downstreamImageMode"] = "source"
        }), Node("circle", "FindCircles", new()
        {
            ["positionReference"] = "matchCenter", ["matchOffsetX"] = 64.0, ["matchOffsetY"] = 38.0,
            ["method"] = "caliper", ["caliperPlacement"] = "guide",
            // Deliberately unrelated static center: positioning must replace it on every frame.
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 999.0, ["cy"] = 888.0, ["radius"] = 18.5 },
            ["searchLength"] = 20.0, ["caliperCount"] = 36, ["projectionWidth"] = 5,
            ["polarity"] = "lightToDark", ["maximumDeviation"] = 2.0,
            ["outputAnnulusWidth"] = 6.0
        })),
        ["connections"] = new JsonArray(Edge("source", "match"), Edge("match", "circle")),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject { ["MatchResult"] = Output("match"), ["CircleResult"] = Output("circle") },
        ["decisionOutput"] = "", ["resources"] = new JsonArray()
    };

    private static JsonObject Parameters(JsonObject definition, string id) => definition["nodes"]!.AsArray()
        .First(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!.AsObject();
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, bool debug = true)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); return await session.RunAsync(frame, new() { Debug = debug });
    }
    private static VisionCircle AssertFit(InspectionResult result, double x, double y)
    {
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        var circle = Assert.Single(packet.Features);
        Assert.InRange(circle.CenterX, x - .8, x + .8);
        Assert.InRange(circle.CenterY, y - .8, y + .8);
        Assert.InRange(circle.Radius, 17.2, 18.7);
        Assert.Equal(circle.Radius * 2, circle.Diameter, 8);
        Assert.True(circle.EdgePoints.Count >= 18);
        Assert.Equal("roiBoundary", packet.Measurement.Caliper!.Placement);
        return circle;
    }

    [Theory]
    [InlineData("FastLocate")]
    [InlineData("FastTemplateMatch")]
    [InlineData("TemplateMatch")]
    public async Task FullSourcePositionReferenceFindsRealCircleOutsideTemplateInOriginalCoordinates(string type)
    {
        using var frame = Image(originX: 900, originY: -80);
        using var result = await Run(Definition(type), frame, debug: false);
        var circle = AssertFit(result, 1022, 21);
        var matchPacket = result.GetResult<VisionMatch>("MatchResult");
        var match = matchPacket.Measurement.Selected!;
        Assert.Equal(940, match.X, 6); Assert.Equal(-30, match.Y, 6);
        Assert.True(circle.CenterX > match.X + match.Width);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        var position = packet.Measurement.Position!;
        Assert.Equal(match.CenterX, position.CenterX, 6);
        Assert.Equal(match.CenterY, position.CenterY, 6);
        Assert.Equal(match.CenterX + 64, position.SearchCenterX, 6);
        Assert.Equal(match.CenterY + 38, position.SearchCenterY, 6);
        Assert.InRange(circle.CenterX - position.SearchCenterX, 1.2, 2.8);
        Assert.InRange(circle.CenterY - position.SearchCenterY, .2, 1.8);
        Assert.Equal(match.CenterX, matchPacket.MatchPosition!.CenterX);
        using var full = result.GetOutputImage("MatchResult");
        Assert.Equal(Width, full.Info.Width); Assert.Equal(Height, full.Info.Height);
        Assert.Equal(900, full.Info.OriginX); Assert.Equal(-80, full.Info.OriginY);
        Assert.Equal(frame.CopyPixels(), full.CopyPixels());
    }

    [Fact]
    public async Task OutputAnnulusFollowsFitAndSearchAnnulusFollowsConfiguredReference()
    {
        using var frame = Image(); using var result = await Run(Definition(), frame);
        var circle = AssertFit(result, 122, 101);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        var search = packet.Measurement.Caliper!.SearchAnnulus!;
        var output = circle.OutputAnnulus!;
        Assert.Equal(packet.Measurement.Position!.SearchCenterX, search.CenterX);
        Assert.Equal(packet.Measurement.Position.SearchCenterY, search.CenterY);
        Assert.Equal(8.5, search.InnerRadius); Assert.Equal(28.5, search.OuterRadius);
        Assert.Equal(circle.CenterX, output.CenterX); Assert.Equal(circle.CenterY, output.CenterY);
        Assert.Equal(circle.Radius - 3, output.InnerRadius, 8);
        Assert.Equal(circle.Radius + 3, output.OuterRadius, 8);
        Assert.Equal("original", output.CoordinateSpace);
        Assert.Equal(6, output.OuterRadius - output.InnerRadius, 8);
        var raw = packet.Measurement.Selected!;
        Assert.Equal(raw.CenterX, raw.Circle.CenterX);
        Assert.Equal(raw.Diameter, raw.Circle.Diameter);
        var rawGeometry = result.Outputs.GetProperty("CircleResult").GetProperty("features")[0].GetProperty("circle");
        Assert.Equal(circle.CenterX, rawGeometry.GetProperty("centerX").GetDouble());
        Assert.Equal(circle.Diameter, rawGeometry.GetProperty("diameter").GetDouble());
    }

    [Fact]
    public async Task ConsecutiveTranslationsRepositionSearchWithoutReusingPreviousCenter()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition()));
        await using var session = recipe.CreateSession();
        using var first = Image(); using var a = await session.RunAsync(first);
        var previous = AssertFit(a, 122, 101);
        using var second = Image(objectX: 180, objectY: 110); using var b = await session.RunAsync(second);
        var moved = AssertFit(b, 262, 161);
        Assert.InRange(moved.CenterX - previous.CenterX, 139.8, 140.2);
        Assert.InRange(moved.CenterY - previous.CenterY, 59.8, 60.2);
        Assert.Equal(140, b.GetResult<VisionCircle>("CircleResult").Measurement.Position!.CenterX -
            a.GetResult<VisionCircle>("CircleResult").Measurement.Position!.CenterX, 6);
    }

    [Theory]
    [InlineData("FastTemplateMatch", 0)]
    [InlineData("FastTemplateMatch", 1)]
    [InlineData("TemplateMatch", 0)]
    [InlineData("TemplateMatch", 1)]
    public async Task SelectedMatchIndexDeterminesWhichOutsideCircleIsMeasured(string type, int selectedIndex)
    {
        var definition = Definition(type); Parameters(definition, "match")["resultIndex"] = selectedIndex;
        using var frame = Image(multiple: true); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var match = result.GetResult<VisionMatch>("MatchResult"); Assert.Equal(2, match.Count);
        Assert.Equal(selectedIndex, match.Measurement.SelectedIndex);
        var selected = match.Measurement.Selected!;
        AssertFit(result, selected.X + 82, selected.Y + 51);
        Assert.Equal(selected.CenterX, result.GetResult<VisionCircle>("CircleResult").Measurement.Position!.CenterX);
    }

    [Theory]
    [InlineData("ToGray")]
    [InlineData("CropRect")]
    public async Task ImageProcessingAfterMatchPreservesOriginalPositionContext(string type)
    {
        var definition = Definition();
        var p = type == "CropRect" ? new JsonObject { ["x"] = 100, ["y"] = 70, ["width"] = 200, ["height"] = 130 } : new JsonObject();
        definition["nodes"]!.AsArray().Insert(2, Node("process", type, p));
        definition["connections"] = new JsonArray(Edge("source", "match"), Edge("match", "process"), Edge("process", "circle"));
        using var frame = Image(originX: 900, originY: -80, bgr: true); using var result = await Run(definition, frame);
        AssertFit(result, 1022, 21);
        var input = result.GetNodeOutput<JsonElement>("process", "image");
        Assert.Equal(result.GetResult<VisionMatch>("MatchResult").Measurement.Selected!.CenterX,
            input.GetProperty("matchPosition").GetProperty("centerX").GetDouble());
        using var circleInput = result.GetInputImage("circle");
        Assert.Equal(type == "CropRect" ? 1000 : 900, circleInput.Info.OriginX);
        Assert.Equal(type == "CropRect" ? -10 : -80, circleInput.Info.OriginY);
    }

    [Fact]
    public async Task ParentCropAndMatchingSearchRoiRetainFullProcessedSourceAndOriginalReference()
    {
        var definition = Definition();
        definition["nodes"]!.AsArray().Insert(1, Node("process", "ToGray", new()
        {
            ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 20, ["y"] = 30, ["width"] = 300, ["height"] = 200 }
        }));
        Parameters(definition, "match")["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 18,
            ["y"] = 18, ["width"] = 36, ["height"] = 28 };
        definition["connections"] = new JsonArray(Edge("source", "process"), Edge("process", "match"), Edge("match", "circle"));
        using var frame = Image(originX: 900, originY: -80, bgr: true); using var result = await Run(definition, frame);
        AssertFit(result, 1022, 21);
        using var input = result.GetOutputImage("MatchResult");
        Assert.Equal(300, input.Info.Width); Assert.Equal(200, input.Info.Height);
        Assert.Equal(920, input.Info.OriginX); Assert.Equal(-50, input.Info.OriginY);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingMatchOrInvalidIndexNeverReusesCenterOrInspectsRemainingCircle(bool invalidIndex)
    {
        var definition = Definition(); if (invalidIndex) Parameters(definition, "match")["resultIndex"] = 5;
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        if (!invalidIndex)
        {
            using var first = Image(); using var previous = await session.RunAsync(first);
            AssertFit(previous, 122, 101);
        }
        using var frame = Image(matched: invalidIndex); using var result = await session.RunAsync(frame, new() { Debug = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
        Assert.Null(packet.Measurement.Position); Assert.Null(packet.MatchPosition);
        var matching = result.GetResult<VisionMatch>("MatchResult");
        Assert.Equal(invalidIndex ? 1 : 0, matching.Count);
        Assert.False(matching.Measurement.SelectedValid); Assert.Null(matching.MatchPosition);
        Assert.False(matching.Image!.Value.GetProperty("available").GetBoolean());
        Assert.Throws<ArgumentException>(() => result.GetOutputImage("MatchResult"));
    }

    [Fact]
    public async Task ExplicitMatchReferenceWithoutMatchingAncestorFailsWithUsefulError()
    {
        var definition = Definition(); definition["nodes"]!.AsArray().RemoveAt(1);
        definition["connections"] = new JsonArray(Edge("source", "circle"));
        definition["outputs"]!.AsObject().Remove("MatchResult");
        using var frame = Image(); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("circle", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("requires a selected upstream matching result", result.Data.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(result.Outputs.EnumerateObject());
    }

    [Theory]
    [InlineData("caliper", -14.0)]
    [InlineData("caliper", 600.0)]
    [InlineData("automatic", 600.0)]
    public async Task PositionOutsideImageReturnsEmptyInspectionInsteadOfRoiBoundsException(string method, double offset)
    {
        var definition = Definition(); var p = Parameters(definition, "circle");
        p["method"] = method; p["matchOffsetX"] = offset; p["matchOffsetY"] = 38.0;
        p["minimumCoverage"] = .9;
        using var frame = Image(objectX: 2, objectY: 30); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("CircleResult");
        Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
        Assert.NotNull(packet.Measurement.Position);
        if (method == "caliper") Assert.True(packet.Measurement.Caliper!.MissingCount > 0);
    }

    [Fact]
    public async Task NoDrawnRoiUsesConfiguredMatchSearchRadius()
    {
        var definition = Definition(); var p = Parameters(definition, "circle");
        p["roi"] = new JsonObject { ["shape"] = "none" }; p["matchSearchRadius"] = 18.5;
        using var frame = Image(); using var result = await Run(definition, frame);
        AssertFit(result, 122, 101);
        Assert.Equal(28.5, result.GetResult<VisionCircle>("CircleResult").Measurement.Caliper!.SearchAnnulus!.OuterRadius);
    }

    [Fact]
    public async Task AutomaticReferenceUsesCurrentUpstreamMatchWhenPresent()
    {
        var definition = Definition(); Parameters(definition, "circle")["positionReference"] = "auto";
        using var frame = Image(); using var result = await Run(definition, frame);
        AssertFit(result, 122, 101);
        Assert.Equal("matchCenter", result.GetResult<VisionCircle>("CircleResult").Measurement.Position!.Source);
    }

    [Fact]
    public async Task AutomaticReferenceWithoutMatcherKeepsManualCircleRoiUsable()
    {
        var definition = Definition(); definition["nodes"]!.AsArray().RemoveAt(1);
        definition["connections"] = new JsonArray(Edge("source", "circle"));
        definition["outputs"]!.AsObject().Remove("MatchResult");
        var p = Parameters(definition, "circle"); p["positionReference"] = "auto";
        p["caliperPlacement"] = "roiBoundary";
        p["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 120.0, ["cy"] = 100.0, ["radius"] = 18.5 };
        using var frame = Image(); using var result = await Run(definition, frame);
        AssertFit(result, 122, 101);
        Assert.Null(result.GetResult<VisionCircle>("CircleResult").Measurement.Position);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(100.0)]
    public async Task OutputAnnulusWidthIsConfiguredGeometryWithNonnegativeInnerRadius(double width)
    {
        var definition = Definition(); var p = Parameters(definition, "circle");
        if (width == 4) p.Remove("outputAnnulusWidth"); // Native backward-compatible default.
        else p["outputAnnulusWidth"] = width;
        using var frame = Image(); using var result = await Run(definition, frame);
        var circle = AssertFit(result, 122, 101);
        Assert.Equal(Math.Max(0, circle.Radius - width / 2), circle.OutputAnnulus!.InnerRadius, 8);
        Assert.Equal(circle.Radius + width / 2, circle.OutputAnnulus.OuterRadius, 8);
    }

    [Fact]
    public async Task LegacyRecipeDefaultsKeepTargetImageAndManualLocalCircleRoi()
    {
        var definition = Definition(embeddedCircle: true);
        Parameters(definition, "match").Remove("downstreamImageMode");
        var p = Parameters(definition, "circle"); p.Remove("positionReference");
        p["caliperPlacement"] = "roiBoundary";
        p["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 16.0, ["cy"] = 12.0, ["radius"] = 7.5 };
        p["searchLength"] = 16.0; p["projectionWidth"] = 3;
        using var frame = Image(originX: 900, originY: -80, embeddedCircle: true); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("CircleResult"); var circle = Assert.Single(packet.Features);
        Assert.InRange(circle.CenterX, 955.3, 956.7); Assert.InRange(circle.CenterY, -18.7, -17.3);
        Assert.InRange(circle.Radius, 6, 7.6); Assert.Null(packet.Measurement.Position);
        using var target = result.GetOutputImage("MatchResult");
        Assert.Equal(ModelWidth, target.Info.Width); Assert.Equal(ModelHeight, target.Info.Height);
        Assert.Equal(940, target.Info.OriginX); Assert.Equal(-30, target.Info.OriginY);
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

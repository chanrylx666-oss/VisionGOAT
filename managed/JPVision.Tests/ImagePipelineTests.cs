using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ImagePipelineTests : IDisposable
{
    private readonly string store;

    public ImagePipelineTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/image-pipeline-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string to, string port = "image", string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonArray nodes, JsonArray connections, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "image-pipeline", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = nodes, ["connections"] = connections, ["outputs"] = outputs
    };
    private static JsonObject Single(string type, JsonObject? p = null) => Recipe(
        new JsonArray(Node("source", "ExternalImage"), Node("tool", type, p)),
        new JsonArray(Edge("source", "tool")), new JsonObject { ["Image"] = Output("tool") });
    private string Save(JsonObject definition)
    {
        string directory = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return directory;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame, new() { Debug = false });
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        return result;
    }
    private static byte[] Pixels(VisionFrame frame)
    {
        var raw = frame.CopyPixels();
        int row = frame.Info.Width * (frame.Info.PixelFormat == VisionPixelFormat.Bgr24 ? 3 : 1);
        var compact = new byte[row * frame.Info.Height];
        for (int y = 0; y < frame.Info.Height; y++)
            Array.Copy(raw, y * frame.Info.Stride, compact, y * row, row);
        return compact;
    }
    private static JsonElement Pipeline(InspectionResult result, string output = "Image") =>
        result.GetOutput<JsonElement>(output).GetProperty("pipeline");

    [Theory]
    [InlineData("FindContours")]
    [InlineData("Morphology")]
    [InlineData("FilterContoursByArea")]
    [InlineData("AnalyzeContours")]
    public async Task InternalSegmentationNeverWritesIntoTheSharedGrayInput(string type)
    {
        const int size = 40;
        var pixels = Enumerable.Repeat((byte)35, size * size).ToArray();
        for (int y = 8; y < 32; y++) for (int x = 8; x < 32; x++) pixels[y * size + x] = 210;
        using var frame = VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8, 700, -25);
        using var result = await Run(Single(type), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(pixels, Pixels(frame));
        Assert.Equal(700, image.Info.OriginX); Assert.Equal(-25, image.Info.OriginY);
        Assert.All(Pixels(image), value => Assert.True(value is 0 or 255));
    }
    private static JsonObject CircleParameters(double x = 64, double y = 64, double radius = 30) => new()
    {
        ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
        ["roi"] = RoiRegion.Circle(x, y, radius).ToJson(), ["caliperCount"] = 64, ["projectionWidth"] = 5,
        ["searchLength"] = 16.0, ["polarity"] = "lightToDark", ["removePointCount"] = 2,
        ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .5, ["outputAnnulusWidth"] = 0.0
    };
    private static VisionFrame CircleFrame(bool missing = false)
    {
        const int size = 128;
        var pixels = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double distance = Math.Sqrt((x - 64) * (x - 64) + (y - 64) * (y - 64));
            pixels[y * size + x] = missing ? (byte)20 : (byte)Math.Clamp(125 + (30 - distance) * 160, 20, 230);
        }
        return VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8, 900, -80);
    }
    private static JsonObject CircleThenBlur(bool overlay = true)
    {
        var circle = CircleParameters(); circle["showOverlay"] = overlay;
        return Recipe(new JsonArray(Node("source", "ExternalImage"), Node("circle", "FindCircles", circle),
                Node("blur", "GaussianBlur", new() { ["kernel"] = 3, ["sigma"] = 1.0 })),
            new JsonArray(Edge("source", "circle"), Edge("circle", "blur")),
            new JsonObject { ["Image"] = Output("blur"), ["Circle"] = Output("circle", "result", "Result") });
    }

    [Fact]
    public void EveryVisibleToolHasOneImageOutputAndImageInputsWhileKeepingLegacyTypedPorts()
    {
        var entries = VisionEngine.GetNodeCatalog().EnumerateArray()
            .Where(n => !n.TryGetProperty("toolboxVisible", out var visible) || visible.GetBoolean()).ToArray();
        Assert.True(entries.Length >= 15);
        foreach (var node in entries)
        {
            var visible = Assert.Single(node.GetProperty("outputs").EnumerateArray(),
                p => !p.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean());
            Assert.Equal("image", visible.GetProperty("id").GetString());
            Assert.Equal("Image", visible.GetProperty("type").GetString());
            foreach (var input in node.GetProperty("inputs").EnumerateArray()
                .Where(p => !p.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean()))
                Assert.Equal("Image", input.GetProperty("type").GetString());
        }
        var circle = Assert.Single(entries, n => n.GetProperty("type").GetString() == "FindCircles");
        Assert.Contains(circle.GetProperty("outputs").EnumerateArray(), p =>
            p.GetProperty("id").GetString() == "features" && p.GetProperty("type").GetString() == "Features" && p.GetProperty("hidden").GetBoolean());
        var morphology = Assert.Single(entries, n => n.GetProperty("type").GetString() == "Morphology");
        var maskInput = Assert.Single(morphology.GetProperty("inputs").EnumerateArray());
        Assert.Contains(maskInput.GetProperty("acceptedTypes").EnumerateArray(), t => t.GetString() == "Mask");
    }

    [Fact]
    public async Task ColorImageCanFeedThresholdWithoutAnExtraGrayModule()
    {
        byte[] colors = { 0, 0, 255, 0, 255, 0, 255, 0, 0, 90, 90, 90, 180, 180, 180 };
        using var frame = VisionFrame.CopyFrom(colors, 5, 1, 15, VisionPixelFormat.Bgr24, 900, -80);
        using var result = await Run(Single("Threshold", new() { ["value"] = 100 }), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(VisionPixelFormat.Gray8, image.Info.PixelFormat);
        Assert.Equal(new byte[] { 0, 255, 0, 0, 255 }, Pixels(image));
        Assert.Equal(colors, Pixels(frame));
        Assert.True(result.GetOutput<JsonElement>("Image").GetProperty("binaryMask").GetBoolean());
        Assert.Equal(900, image.Info.OriginX); Assert.Equal(-80, image.Info.OriginY);
    }

    [Fact]
    public async Task MorphologySegmentsOrdinaryColorPixelsBeforeApplyingItsKernel()
    {
        const int size = 17;
        var pixels = Enumerable.Repeat((byte)40, size * size * 3).ToArray();
        for (int c = 0; c < 3; c++) { pixels[(8 * size + 8) * 3 + c] = 210; pixels[(2 * size + 2) * 3 + c] = 150; }
        using var frame = VisionFrame.CopyFrom(pixels, size, size, size * 3, VisionPixelFormat.Bgr24);
        using var result = await Run(Single("Morphology", new()
        { ["operation"] = "dilate", ["kernel"] = 3, ["imageThreshold"] = 160 }), frame);
        using var image = result.GetOutputImage("Image"); var actual = Pixels(image);
        Assert.Equal(VisionPixelFormat.Gray8, image.Info.PixelFormat);
        Assert.Equal(9, actual.Count(v => v == 255));
        Assert.Equal(0, actual[2 * size + 2]); Assert.Equal(255, actual[7 * size + 7]);
        Assert.All(actual, v => Assert.True(v is 0 or 255)); Assert.Equal(pixels, Pixels(frame));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ContourFilteringChangesRealPixelsPreservesHolesAndFeedsRegionAnalysis(bool legacyContours)
    {
        const int width = 100, height = 72;
        var pixels = Enumerable.Repeat((byte)20, width * height).ToArray();
        for (int y = 10; y < 50; y++) for (int x = 10; x < 50; x++)
            if (!(x is >= 20 and < 30 && y is >= 20 and < 30)) pixels[y * width + x] = 220;
        for (int y = 20; y < 28; y++) for (int x = 75; x < 83; x++) pixels[y * width + x] = 220;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("contours", "FindContours"),
                Node("filter", "FilterContoursByArea", new() { ["minimum"] = 500.0, ["maximum"] = 2000.0 }),
                Node("regions", "AnalyzeContours"), Node("gray", "ToGray")),
            new JsonArray(Edge("source", "contours"), Edge("contours", "filter", legacyContours ? "contours" : "image"),
                Edge("filter", "regions", legacyContours ? "contours" : "image"), Edge("regions", "gray")),
            new JsonObject { ["Image"] = Output("gray"), ["Regions"] = Output("regions", "features", "Features"),
                ["ContoursImage"] = Output("contours"), ["FilteredImage"] = Output("filter"), ["RegionImage"] = Output("regions") });
        using var frame = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80);
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image"); var actual = Pixels(image);
        foreach (string name in new[] { "ContoursImage", "FilteredImage", "RegionImage", "Image" })
        {
            using var stageImage = result.GetOutputImage(name);
            Assert.True(Pixels(stageImage)[25 * width + 25] == 0, name + " filled the original region's hole");
        }
        Assert.Equal(width, image.Info.Width); Assert.Equal(height, image.Info.Height);
        Assert.Equal(900, image.Info.OriginX); Assert.Equal(-80, image.Info.OriginY);
        Assert.Equal(255, actual[12 * width + 12]); Assert.Equal(0, actual[25 * width + 25]);
        Assert.Equal(0, actual[24 * width + 78]); Assert.Equal(1500, actual.Count(v => v == 255));
        var region = Assert.Single(result.GetRegions("Regions"));
        Assert.Equal(910, region.X); Assert.Equal(-70, region.Y); Assert.Equal(40, region.Width); Assert.Equal(40, region.Height);
        Assert.InRange(region.CentroidX, 929.49, 929.51); Assert.InRange(region.CentroidY, -50.51, -50.49);
        var contextRegion = Assert.Single(Pipeline(result).GetProperty("results").GetProperty("Features").EnumerateArray());
        Assert.Equal("region", contextRegion.GetProperty("kind").GetString());
        Assert.Equal(region.Area, Assert.Single(result.GetRegions("Image")).Area);
        Assert.Equal(region.CentroidX, Assert.Single(result.GetResult<VisionRegion>("Image").Features).CentroidX);
        Assert.Equal(pixels, Pixels(frame));
    }

    [Fact]
    public async Task ChangedMaskIsAnalyzedAgainInsteadOfReusingEarlierContours()
    {
        const int width = 64, height = 48; var pixels = new byte[width * height];
        for (int y = 10; y < 25; y++) for (int x = 10; x < 25; x++) pixels[y * width + x] = 255;
        for (int y = 15; y < 20; y++) for (int x = 42; x < 47; x++) pixels[y * width + x] = 255;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("contours", "FindContours"),
                Node("erode", "Morphology", new() { ["operation"] = "erode", ["kernel"] = 9 }), Node("regions", "AnalyzeContours")),
            new JsonArray(Edge("source", "contours"), Edge("contours", "erode"), Edge("erode", "regions")),
            new JsonObject { ["Image"] = Output("regions"), ["Regions"] = Output("regions", "features", "Features"),
                ["OriginalContours"] = Output("contours", "contours", "Contours") });
        using var frame = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8);
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(2, result.GetOutput<JsonElement>("OriginalContours").GetArrayLength());
        var region = Assert.Single(result.GetRegions("Regions"));
        Assert.Equal(14, region.X); Assert.Equal(14, region.Y); Assert.Equal(7, region.Width); Assert.Equal(7, region.Height);
        Assert.Equal(49, Pixels(image).Count(v => v == 255));
    }

    [Fact]
    public async Task GaussianOutputIsNoLongerBinaryAndTheNextContourToolAppliesItsThreshold()
    {
        const int size = 32; var pixels = new byte[size * size];
        for (int y = 8; y < 18; y++) for (int x = 8; x < 18; x++) pixels[y * size + x] = 255;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("contours", "FindContours"),
                Node("blur", "GaussianBlur", new() { ["kernel"] = 3, ["sigma"] = 1.0 }),
                Node("regions", "AnalyzeContours", new() { ["imageThreshold"] = 220 })),
            new JsonArray(Edge("source", "contours"), Edge("contours", "blur"), Edge("blur", "regions")),
            new JsonObject { ["Image"] = Output("regions"), ["Blur"] = Output("blur"), ["Regions"] = Output("regions", "features", "Features") });
        using var frame = VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8, 900, -80);
        using var result = await Run(definition, frame);
        Assert.False(result.GetOutput<JsonElement>("Blur").GetProperty("binaryMask").GetBoolean());
        var region = Assert.Single(result.GetRegions("Regions"));
        Assert.Equal(909, region.X); Assert.Equal(-71, region.Y); Assert.Equal(8, region.Width); Assert.Equal(8, region.Height);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DetectorGraphicsNeverChangeThePixelsSeenByTheFollowingImageProcessor(bool showOverlay)
    {
        using var frame = CircleFrame();
        using var baseline = await Run(Single("GaussianBlur", new() { ["kernel"] = 3, ["sigma"] = 1.0 }), frame);
        using var measured = await Run(CircleThenBlur(showOverlay), frame);
        using var directImage = baseline.GetOutputImage("Image"); using var measuredImage = measured.GetOutputImage("Image");
        Assert.Equal(Pixels(directImage), Pixels(measuredImage));
        var circle = Assert.Single(measured.GetResult<VisionCircle>("Circle").Features);
        Assert.InRange(circle.CenterX, 963.8, 964.2); Assert.InRange(circle.CenterY, -16.2, -15.8); Assert.InRange(circle.Radius, 29.6, 30.4);
        var pipeline = Pipeline(measured);
        Assert.Equal(pipeline.GetRawText(), measured.GetImagePipeline("Image").GetRawText());
        var carried = Assert.Single(pipeline.GetProperty("results").GetProperty("Features").EnumerateArray());
        Assert.Equal("circle", carried.GetProperty("kind").GetString()); Assert.Equal(circle.Radius, carried.GetProperty("radius").GetDouble());
        Assert.Equal(circle.Radius, Assert.Single(measured.GetCircles("Image")).Radius);
        Assert.Equal(circle.CenterX, Assert.Single(measured.GetResult<VisionCircle>("Image").Features).CenterX);
        Assert.Equal(circle.CenterY, measured.GetMeasurement<VisionCircle>("Image").Selected!.CenterY);
        Assert.Equal(circle.Radius, Assert.Single(measured.GetImageAttachedOutput<VisionCircle[]>("Image", "Features")).Radius);
        var graphics = pipeline.GetProperty("overlay").EnumerateArray().ToArray();
        if (showOverlay) Assert.NotEmpty(graphics); else Assert.Empty(graphics);
        Assert.Equal(graphics.Length, measured.GetOverlays("Image").Count);
        Assert.All(graphics, graphic => Assert.Equal("#00FF00", graphic.GetProperty("color").GetString()));
        // Published metadata contains the current result, not recursively nested copies of every upstream image context.
        var associated = pipeline.GetProperty("results").GetProperty("Result").GetProperty("image");
        Assert.False(associated.TryGetProperty("pipeline", out _));
    }

    [Fact]
    public async Task DownstreamMarkerRestylesOneCarriedCircleWithoutDuplicatingOrPaintingIt()
    {
        using var frame = CircleFrame();
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("circle", "FindCircles", CircleParameters()),
                Node("marker", "BuildFeatureOverlay", new() { ["color"] = "#FFAA00", ["lineWidth"] = 3.0 }), Node("gray", "ToGray")),
            new JsonArray(Edge("source", "circle"), Edge("circle", "marker"), Edge("marker", "gray")),
            new JsonObject { ["Image"] = Output("gray") });
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        var circle = Assert.Single(result.GetOverlays("Image"), o => o.Kind == "circle" && o.RadiusX > 20);
        Assert.Equal("#FFAA00", circle.Color); Assert.Equal(3, circle.LineWidth);
        Assert.Equal(Pixels(frame), Pixels(image));
    }

    [Fact]
    public async Task ReusingOneFrameForTwoRunsCreatesFreshContextsWithoutAccumulatingStages()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Single("Threshold")));
        await using var session = recipe.CreateSession(); using var frame = CircleFrame();
        using var first = await session.RunAsync(frame, new() { Debug = false, FrameId = "same-camera-frame" });
        using var second = await session.RunAsync(frame, new() { Debug = false, FrameId = "same-camera-frame" });
        Assert.Equal(ExecutionStatus.Succeeded, first.Status); Assert.Equal(ExecutionStatus.Succeeded, second.Status);
        var a = Pipeline(first); var b = Pipeline(second);
        Assert.NotEqual(a.GetProperty("frameId").GetUInt64(), b.GetProperty("frameId").GetUInt64());
        Assert.Equal(2, a.GetProperty("stageCount").GetInt32()); Assert.Equal(2, b.GetProperty("stageCount").GetInt32());
        using var firstImage = first.GetOutputImage("Image"); using var secondImage = second.GetOutputImage("Image");
        Assert.Equal(Pixels(firstImage), Pixels(secondImage));
    }

    [Fact]
    public async Task MissingCurrentTargetReplacesTheEarlierResultInsteadOfReusingItsCircle()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(CircleThenBlur()));
        await using var session = recipe.CreateSession(); using var present = CircleFrame(); using var blank = CircleFrame(true);
        using (var found = await session.RunAsync(present)) Assert.Single(found.GetResult<VisionCircle>("Circle").Features);
        using var absent = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, absent.Status); Assert.Empty(absent.GetResult<VisionCircle>("Circle").Features);
        var context = Pipeline(absent); Assert.Empty(context.GetProperty("results").GetProperty("Features").EnumerateArray());
        Assert.Empty(context.GetProperty("overlay").EnumerateArray());
        using var image = absent.GetOutputImage("Image"); Assert.All(Pixels(image), v => Assert.Equal(20, v));
    }

    [Fact]
    public async Task LegacyTwoDetectorMeasurementMergesBothBranchesAndReturnsTheCleanFullFrame()
    {
        const int width = 180, height = 120; var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double a = Math.Sqrt((x - 50) * (x - 50) + (y - 60) * (y - 60));
            double b = Math.Sqrt((x - 130) * (x - 130) + (y - 60) * (y - 60));
            pixels[y * width + x] = (byte)Math.Clamp(125 + (20 - Math.Min(a, b)) * 160, 20, 230);
        }
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("a", "FindCircles", CircleParameters(50, 60, 20)),
                Node("b", "FindCircles", CircleParameters(130, 60, 20)), Node("measure", "MeasureCircleDistance")),
            new JsonArray(Edge("source", "a"), Edge("source", "b"), Edge("a", "measure", "features", "first"),
                Edge("b", "measure", "features", "second")),
            new JsonObject { ["Image"] = Output("measure"), ["Distance"] = Output("measure", "result", "Result") });
        using var frame = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80);
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(width, image.Info.Width); Assert.Equal(height, image.Info.Height); Assert.Equal(pixels, Pixels(image));
        Assert.InRange(result.GetGeometryMeasurement("Distance").Value!.Value, 79.8, 80.2);
        Assert.Equal(result.GetGeometryMeasurement("Distance").Value, result.GetGeometryMeasurement("Image").Value);
        Assert.Equal(result.GetGeometryMeasurement("Distance").Value, result.GetGeometricMeasurement("Image").Value);
        var context = Pipeline(result); Assert.Equal(4, context.GetProperty("stageCount").GetInt32());
        var features = context.GetProperty("results").GetProperty("Features").EnumerateArray().ToArray();
        Assert.Equal(2, features.Length); Assert.InRange(features[0].GetProperty("centerX").GetDouble(), 949.8, 950.2);
        Assert.InRange(features[1].GetProperty("centerX").GetDouble(), 1029.8, 1030.2);
        Assert.NotEmpty(context.GetProperty("overlay").EnumerateArray());
        Assert.All(context.GetProperty("overlay").EnumerateArray(), graphic => Assert.Equal("#00FF00", graphic.GetProperty("color").GetString()));
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

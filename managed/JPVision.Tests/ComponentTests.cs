using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ComponentTests : IDisposable
{
    private readonly string root, store;
    public ComponentTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")))
            directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Project root not found.");
        store = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private JsonObject Recipe(string component, JsonObject? parameters = null, bool roi = true, int expected = 1)
    {
        JsonObject Node(string id, string type, JsonObject? p = null) => new()
        {
            ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new()
        };
        JsonObject Edge(string from, string port, string to) => new()
        {
            ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = "input"
        };
        var nodes = new JsonArray(Node("source", "ExternalImage"));
        var edges = new JsonArray();
        string imageNode = "source";
        if (roi)
        {
            nodes.Add(Node("roi", "CropRect", new() { ["x"] = 100, ["y"] = 50, ["width"] = 240, ["height"] = 180 }));
            edges.Add(Edge("source", "image", "roi"));
            imageNode = "roi";
        }
        if (component == "AnalyzeContours")
        {
            nodes.Add(Node("gray", "ToGray"));
            nodes.Add(Node("threshold", "Threshold"));
            nodes.Add(Node("morph", "Morphology", new() { ["operation"] = "open", ["kernel"] = 3 }));
            nodes.Add(Node("contours", "FindContours"));
            edges.Add(Edge(imageNode, "image", "gray"));
            edges.Add(Edge("gray", "image", "threshold"));
            edges.Add(Edge("threshold", "mask", "morph"));
            edges.Add(Edge("morph", "mask", "contours"));
            imageNode = "contours";
        }
        nodes.Add(Node("detect", component, parameters));
        nodes.Add(Node("count", "CountFeatures"));
        nodes.Add(Node("judge", "CompareNumber", new() { ["expected"] = expected }));
        nodes.Add(Node("overlay", "BuildFeatureOverlay"));
        edges.Add(Edge(imageNode, component == "AnalyzeContours" ? "contours" : "image", "detect"));
        edges.Add(Edge("detect", "features", "count"));
        edges.Add(Edge("count", "value", "judge"));
        edges.Add(Edge("detect", "features", "overlay"));
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "inspection", ["recipeId"] = "component-test",
            ["recipeVersion"] = 1, ["resources"] = new JsonArray(), ["nodes"] = nodes, ["connections"] = edges,
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject
            {
                ["Features"] = new JsonObject { ["node"] = "detect", ["port"] = "features", ["type"] = "Features" },
                ["ObjectCount"] = new JsonObject { ["node"] = "count", ["port"] = "value", ["type"] = "Int" },
                ["InspectionPassed"] = new JsonObject { ["node"] = "judge", ["port"] = "value", ["type"] = "Bool" },
                ["ResultOverlay"] = new JsonObject { ["node"] = "overlay", ["port"] = "overlay", ["type"] = "Overlay" }
            },
            ["decisionOutput"] = "InspectionPassed"
        };
    }

    private string Save(JsonObject recipe)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString()));
        return path;
    }

    private async Task<InspectionResult> Execute(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(definition));
        using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = true });
    }

    private static VisionFrame Image(Func<int, int, bool> white, bool gray = false)
    {
        const int width = 360, height = 240;
        int channels = gray ? 1 : 3, stride = width * channels + 8;
        byte[] pixels = new byte[height * stride];
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
                if (white(x, y))
                    for (int c = 0; c < channels; ++c)
                        pixels[y * stride + x * channels + c] = 255;
        return VisionFrame.CopyFrom(pixels, width, height, stride, gray ? VisionPixelFormat.Gray8 : VisionPixelFormat.Bgr24);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CircleDetectionReportsOriginalCoordinatesAndOverlay(bool gray)
    {
        using var frame = Image((x, y) => (x - 200) * (x - 200) + (y - 125) * (y - 125) <= 900, gray);
        using var result = await Execute(Recipe("FindCircles", new() { ["minimumRadius"] = 25, ["maximumRadius"] = 35, ["voteThreshold"] = 20 }), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        var circle = Assert.Single(result.GetOutput<JsonElement[]>("Features"));
        Assert.Equal("circle", circle.GetProperty("kind").GetString());
        Assert.InRange(circle.GetProperty("x").GetDouble(), 198, 202);
        Assert.InRange(circle.GetProperty("y").GetDouble(), 123, 127);
        Assert.InRange(circle.GetProperty("radius").GetDouble(), 28, 32);
        Assert.Equal(1, result.GetOutput<int>("ObjectCount"));
        Assert.InRange(Assert.Single(result.GetCircles()).Radius, 28, 32);
        Assert.Throws<InvalidDataException>(() => result.GetLines());
        var overlay = Assert.Single(result.GetOverlays(), x => x.Kind == "circle");
        Assert.InRange(overlay.X, 198, 202);
        Assert.InRange(overlay.RadiusX, 28, 32);
        var debug = result.Data.GetProperty("debugOverlays").GetProperty("detect").EnumerateArray().ToArray();
        Assert.Single(debug, item => item.GetProperty("type").GetString() == "circle");
        var band = Assert.Single(debug, item => item.GetProperty("type").GetString() == "annulus");
        Assert.Equal(circle.GetProperty("x").GetDouble(), band.GetProperty("x").GetDouble());
        Assert.Equal(4, band.GetProperty("outerRadius").GetDouble() - band.GetProperty("innerRadius").GetDouble(), 8);
    }

    [Fact]
    public async Task LinesHaveMeasuredLengthAngleAndOriginalEndpoints()
    {
        using var frame = Image((x, y) => x >= 120 && x <= 260 && y >= 110 && y <= 114);
        using var result = await Execute(Recipe("FindLines", new() { ["minimumLength"] = 100, ["voteThreshold"] = 30 }), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var lines = result.GetOutput<JsonElement[]>("Features");
        Assert.NotEmpty(lines);
        Assert.Equal(lines.Length, result.GetOutput<int>("ObjectCount"));
        Assert.Contains(lines, line => line.GetProperty("length").GetDouble() >= 130 && Math.Abs(line.GetProperty("angleDegrees").GetDouble()) <= 2);
        foreach (var line in lines)
        {
            Assert.Equal("line", line.GetProperty("kind").GetString());
            Assert.InRange(line.GetProperty("x1").GetDouble(), 118, 262);
            Assert.InRange(line.GetProperty("x2").GetDouble(), 118, 262);
            Assert.InRange(line.GetProperty("y1").GetDouble(), 108, 116);
            Assert.InRange(line.GetProperty("y2").GetDouble(), 108, 116);
        }
        Assert.Equal(lines.Length, result.GetOverlays().Count(x => x.Kind == "line"));
        Assert.Contains(result.GetLines(), line => line.Length >= 130 && Math.Abs(line.AngleDegrees) <= 2);
        var measured = result.GetNodeMeasurement<VisionLine>("detect");
        Assert.Equal(lines.Length, measured.Count); Assert.NotNull(measured.Selected);
        Assert.Equal((measured.Selected.X1 + measured.Selected.X2) / 2, measured.Selected.CenterX, 8);
    }

    [Fact]
    public async Task RegionAnalysisMeasuresAreaPerimeterCentroidAndBoundingBox()
    {
        using var frame = Image((x, y) => x >= 140 && x < 160 && y >= 90 && y < 110);
        using var result = await Execute(Recipe("AnalyzeContours"), frame);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        var region = Assert.Single(result.GetOutput<JsonElement[]>("Features"));
        Assert.Equal("region", region.GetProperty("kind").GetString());
        Assert.Equal(361, region.GetProperty("area").GetDouble(), 5);
        Assert.Equal(76, region.GetProperty("perimeter").GetDouble(), 5);
        Assert.Equal(149.5, region.GetProperty("centroidX").GetDouble(), 5);
        Assert.Equal(99.5, region.GetProperty("centroidY").GetDouble(), 5);
        Assert.True(region.GetProperty("centroidValid").GetBoolean());
        Assert.Equal(140, region.GetProperty("x").GetInt32());
        Assert.Equal(90, region.GetProperty("y").GetInt32());
        Assert.Equal(20, region.GetProperty("width").GetInt32());
        Assert.Equal(20, region.GetProperty("height").GetInt32());
        Assert.Equal(Math.PI / 4, region.GetProperty("circularity").GetDouble(), 5);
        Assert.Equal(361, Assert.Single(result.GetRegions()).Area, 5);
        Assert.Single(result.GetOverlays(), x => x.Kind == "rectangle");
        var measured = result.GetNodeMeasurement<VisionRegion>("detect");
        Assert.Equal(1, measured.Count); Assert.Equal(361, measured.Selected!.Area, 5);
    }

    [Theory]
    [InlineData("FindCircles")]
    [InlineData("FindLines")]
    [InlineData("AnalyzeContours")]
    public async Task EmptyImageReturnsEmptyFeaturesAndNoStaleDetection(string component)
    {
        using var frame = Image((_, _) => false);
        using var result = await Execute(Recipe(component, expected: 0), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Empty(result.GetOutput<JsonElement[]>("Features"));
        Assert.Empty(result.GetOverlays());
    }

    [Theory]
    [InlineData("FindCircles", "minimumRadius", 80, "maximumRadius", 20)]
    [InlineData("FindLines", "edgeLow", 200, "edgeHigh", 100)]
    public void CrossParameterValidationRejectsInvalidRanges(string type, string a, int av, string b, int bv)
    {
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe(type, new() { [a] = av, [b] = bv }))));
        Assert.Equal(2, error.Code);
        Assert.DoesNotContain("Unknown node", error.Message);
    }

    [Fact]
    public void FeaturePortsCannotFeedAnImageComponent()
    {
        using var engine = new VisionEngine(store);
        var recipe = Recipe("FindCircles");
        recipe["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "blur", ["type"] = "GaussianBlur", ["version"] = 1, ["parameters"] = new JsonObject() });
        recipe["connections"]!.AsArray().Add(new JsonObject { ["fromNode"] = "detect", ["fromPort"] = "features", ["toNode"] = "blur", ["toPort"] = "input" });
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(recipe)));
        Assert.Equal(2, error.Code);
        Assert.DoesNotContain("Unknown node", error.Message);
    }

    [Fact]
    public async Task SimpleRegisteredImageComponentRemovesImpulseNoiseAndPreservesRoi()
    {
        var definition = Recipe("AnalyzeContours");
        definition["nodes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "median", ["type"] = "MedianBlur", ["version"] = 1,
            ["parameters"] = new JsonObject { ["kernel"] = 3 }
        });
        definition["connections"]!.AsArray().Add(new JsonObject
        {
            ["fromNode"] = "roi", ["fromPort"] = "image", ["toNode"] = "median", ["toPort"] = "input"
        });
        definition["outputs"]!["CleanImage"] = new JsonObject { ["node"] = "median", ["port"] = "image", ["type"] = "Image" };
        using var frame = Image((x, y) => x == 200 && y == 120, true);
        using var result = await Execute(definition, frame);
        using var cleaned = result.GetOutputImage("CleanImage");
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(100, cleaned.Info.OriginX);
        Assert.Equal(50, cleaned.Info.OriginY);
        Assert.Equal(240, cleaned.Info.Width);
        Assert.All(cleaned.CopyPixels(), pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public async Task DegenerateContourUsesFiniteFallbackAndMarksCentroidInvalid()
    {
        var definition = Recipe("AnalyzeContours");
        var morph = definition["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "morph")!;
        morph["parameters"]!["kernel"] = 1;
        using var frame = Image((x, y) => x == 200 && y == 120);
        using var result = await Execute(definition, frame);
        var region = Assert.Single(result.GetRegions());
        Assert.False(region.CentroidValid);
        Assert.Equal(0, region.Area);
        Assert.Equal(0, region.Perimeter);
        Assert.Equal(0, region.Circularity);
        Assert.Equal(200, region.CentroidX);
        Assert.Equal(120, region.CentroidY);
    }

    [Fact]
    public async Task HoughImageLimitReportsFailureAndUnknownDecision()
    {
        const int width = 2112, height = 2112;
        using var frame = VisionFrame.CopyFrom(new byte[width * height], width, height, width, VisionPixelFormat.Gray8);
        using var result = await Execute(Recipe("FindCircles", new() { ["largeImageMode"] = "original" }, roi: false), frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Equal(5, result.Data.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(result.Outputs.EnumerateObject());
        Assert.Throws<InvalidOperationException>(() => result.GetCircles());
    }

    public void Dispose() { }
}

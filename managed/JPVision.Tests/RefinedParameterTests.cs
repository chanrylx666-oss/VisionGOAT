using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class RefinedParameterTests : IDisposable
{
    private readonly string store;
    public RefinedParameterTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/parameters-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }

    private string Save(string type, JsonObject parameters, bool mask = false)
    {
        JsonObject Node(string id, string kind, JsonObject? p = null) => new()
        { ["id"] = id, ["type"] = kind, ["version"] = 1, ["parameters"] = p ?? new() };
        JsonObject Edge(string from, string port, string to) => new()
        { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = "input" };
        var nodes = new JsonArray(Node("source", "ExternalImage"));
        var edges = new JsonArray();
        if (mask)
        {
            nodes.Add(Node("binary", "Threshold")); edges.Add(Edge("source", "image", "binary"));
        }
        nodes.Add(Node("tool", type, parameters)); edges.Add(Edge(mask ? "binary" : "source", mask ? "mask" : "image", "tool"));
        string outputType = type is "Threshold" or "Morphology" ? "Mask" : type is "LineCaliper" or "FindLines" or "FindCircles" ? "Features" : "Image";
        string port = outputType == "Mask" ? "mask" : outputType == "Features" ? "features" : "image";
        var recipe = new JsonObject
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing", ["decisionOutput"] = "", ["recipeId"] = "refined-parameters",
            ["recipeVersion"] = 1, ["resources"] = new JsonArray(), ["nodes"] = nodes, ["connections"] = edges,
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject { ["Output"] = new JsonObject { ["node"] = "tool", ["port"] = port, ["type"] = outputType } }
        };
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString()));
        return path;
    }

    private async Task<(byte[] Pixels, FrameInfo Info)> ImageResult(string type, JsonObject p, byte[] pixels, int w, int h,
        bool mask = false, VisionPixelFormat format = VisionPixelFormat.Gray8)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(type, p, mask));
        using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(pixels, w, h, w * (format == VisionPixelFormat.Bgr24 ? 3 : 1), format, 900, -80);
        using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        using var image = result.GetOutputImage("Output");
        Assert.Equal(900, image.Info.OriginX); Assert.Equal(-80, image.Info.OriginY);
        var copied = image.CopyPixels();
        var compact = new byte[image.Info.Width * image.Info.Height];
        Assert.Equal(VisionPixelFormat.Gray8, image.Info.PixelFormat);
        for (int y = 0; y < image.Info.Height; y++) Array.Copy(copied, y * image.Info.Stride, compact, y * image.Info.Width, image.Info.Width);
        Assert.Equal(pixels, frame.CopyPixels());
        return (compact, image.Info);
    }

    [Theory]
    [InlineData("blue", 20)] [InlineData("green", 80)] [InlineData("red", 190)]
    public async Task GrayConversionCanSelectAnActualColorChannel(string channel, byte expected)
    {
        var output = await ImageResult("ToGray", new() { ["channel"] = channel }, new byte[] { 20, 80, 190 }, 1, 1, format: VisionPixelFormat.Bgr24);
        Assert.Equal(expected, output.Pixels[0]);
    }

    [Fact]
    public async Task GaussianIndependentHeightControlsTheActualBlurDirection()
    {
        var pixels = new byte[17 * 17]; pixels[8 * 17 + 8] = 255;
        var row = await ImageResult("GaussianBlur", new() { ["kernel"] = 5, ["kernelHeight"] = 1, ["sigma"] = 1.0 }, pixels, 17, 17);
        var column = await ImageResult("GaussianBlur", new() { ["kernel"] = 1, ["kernelHeight"] = 5, ["sigma"] = 1.0, ["sigmaY"] = 1.0 }, pixels, 17, 17);
        Assert.True(row.Pixels[8 * 17 + 7] > 0); Assert.Equal(0, row.Pixels[7 * 17 + 8]);
        Assert.True(column.Pixels[7 * 17 + 8] > 0); Assert.Equal(0, column.Pixels[8 * 17 + 7]);
    }

    [Fact]
    public async Task GaussianBorderPolicyChangesOnlyTheExpectedBoundaryBehavior()
    {
        byte[] pixels = Enumerable.Repeat((byte)80, 17 * 17).ToArray();
        var replicated = await ImageResult("GaussianBlur", new() { ["borderMode"] = "replicate" }, pixels, 17, 17);
        var constant = await ImageResult("GaussianBlur", new() { ["borderMode"] = "constant" }, pixels, 17, 17);
        Assert.All(replicated.Pixels, v => Assert.Equal(80, v)); Assert.True(constant.Pixels[0] < 80);
        Assert.Equal(80, constant.Pixels[8 * 17 + 8]);
    }

    [Theory]
    [InlineData("otsu")] [InlineData("triangle")]
    public async Task AutomaticThresholdSeparatesTwoGrayLevelsWithoutUsingTheManualValue(string method)
    {
        byte[] pixels = Enumerable.Range(0, 31 * 31).Select(i => (byte)(i % 31 < 24 ? 30 : 200)).ToArray();
        var output = await ImageResult("Threshold", new() { ["method"] = method, ["value"] = 255 }, pixels, 31, 31);
        Assert.Equal(0, output.Pixels[15 * 31 + 8]); Assert.Equal(255, output.Pixels[15 * 31 + 28]);
    }

    [Theory]
    [InlineData("otsu")] [InlineData("triangle")]
    public async Task AutomaticThresholdComputesItsStatisticsOnlyInsideThePolygonRoi(string method)
    {
        var pixels = new byte[31 * 31];
        for (int y = 0; y < 31; y++) for (int x = 0; x < 31; x++) pixels[y * 31 + x] = (byte)(x + y <= 30 ? x < 8 ? 80 : 120 : 240);
        var roi = RoiRegion.Polygon(new[] { new EditorPoint(0, 0), new EditorPoint(30, 0), new EditorPoint(0, 30) }).ToJson();
        var output = await ImageResult("Threshold", new() { ["method"] = method, ["roi"] = roi }, pixels, 31, 31);
        Assert.Equal(0, output.Pixels[8 * output.Info.Width + 3]); Assert.Equal(255, output.Pixels[8 * output.Info.Width + 10]);
    }

    [Theory]
    [InlineData("adaptiveMean")] [InlineData("adaptiveGaussian")]
    public async Task LocalThresholdUsesWindowAndOffsetInsteadOfTheGlobalThreshold(string method)
    {
        var pixels = Enumerable.Repeat((byte)100, 31 * 31).ToArray(); pixels[15 * 31 + 15] = 210;
        var output = await ImageResult("Threshold", new() { ["method"] = method, ["blockSize"] = 15, ["offset"] = -3.0, ["value"] = 255 }, pixels, 31, 31);
        Assert.Equal(255, output.Pixels[15 * 31 + 15]); Assert.Equal(0, output.Pixels[0]);
        Assert.All(output.Pixels, v => Assert.True(v is 0 or 255));
    }

    [Theory]
    [InlineData("rectangle", 9)] [InlineData("cross", 5)] [InlineData("ellipse", 5)]
    public async Task MorphologyKernelShapeChangesThePixelSupport(string shape, int count)
    {
        var pixels = new byte[17 * 17]; pixels[8 * 17 + 8] = 255;
        var output = await ImageResult("Morphology", new() { ["operation"] = "dilate", ["kernelShape"] = shape }, pixels, 17, 17, true);
        Assert.Equal(count, output.Pixels.Count(v => v == 255));
    }

    [Fact]
    public async Task MorphologyCanUseARectangularKernelAndExplicitBorderValue()
    {
        var pixels = new byte[17 * 17]; pixels[8 * 17 + 8] = 255;
        var output = await ImageResult("Morphology", new() { ["operation"] = "dilate", ["kernel"] = 5, ["kernelHeight"] = 1 }, pixels, 17, 17, true);
        Assert.Equal(5, output.Pixels.Count(v => v == 255)); Assert.Equal(0, output.Pixels[7 * 17 + 8]);
        var full = Enumerable.Repeat((byte)255, 17 * 17).ToArray();
        var automatic = await ImageResult("Morphology", new() { ["operation"] = "erode" }, full, 17, 17, true);
        var zero = await ImageResult("Morphology", new() { ["operation"] = "erode", ["borderValue"] = 0 }, full, 17, 17, true);
        Assert.Equal(255, automatic.Pixels[0]); Assert.Equal(0, zero.Pixels[0]); Assert.Equal(255, zero.Pixels[8 * 17 + 8]);
    }

    [Theory]
    [InlineData("gradient")] [InlineData("topHat")] [InlineData("blackHat")]
    public async Task AdditionalMorphologyOperationsProduceBinaryMasks(string operation)
    {
        var pixels = Enumerable.Repeat((byte)(operation == "blackHat" ? 255 : 0), 17 * 17).ToArray();
        pixels[8 * 17 + 8] = (byte)(operation == "blackHat" ? 0 : 255);
        var output = await ImageResult("Morphology", new() { ["operation"] = operation }, pixels, 17, 17, true);
        Assert.True(output.Pixels.Count(v => v == 255) > 0); Assert.All(output.Pixels, v => Assert.True(v is 0 or 255));
    }

    [Fact]
    public async Task AddedDefaultsPreserveTheOldGaussianAndFixedThresholdBehavior()
    {
        byte[] pixels = Enumerable.Range(0, 17 * 17).Select(i => (byte)(i * 47 % 256)).ToArray();
        var old = await ImageResult("GaussianBlur", new(), pixels, 17, 17);
        var current = await ImageResult("GaussianBlur", new() { ["kernelHeight"] = 0, ["sigmaY"] = 0.0, ["borderMode"] = "reflect101" }, pixels, 17, 17);
        Assert.Equal(old.Pixels, current.Pixels);
        var threshold = await ImageResult("Threshold", new(), pixels, 17, 17);
        Assert.Equal(pixels.Select(p => (byte)(p > 128 ? 255 : 0)), threshold.Pixels);
    }

    [Theory]
    [InlineData("GaussianBlur", "kernelHeight", 2)] [InlineData("Morphology", "kernelHeight", 4)]
    [InlineData("Threshold", "blockSize", 8)] [InlineData("GaussianBlur", "kernel", 6)]
    public void InvalidOddWindowSizesAreRejectedBeforeExecution(string type, string field, int value)
    {
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(type, new() { [field] = value }, type == "Morphology")));
        Assert.Equal(2, error.Code); Assert.DoesNotContain("Unknown node parameter", error.Message);
        Assert.Contains("odd", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BinaryMorphologyDoesNotAcceptNonBinaryBoundaryGray()
    {
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save("Morphology", new() { ["borderValue"] = 128 }, true)));
        Assert.Contains("0 or 255", error.Message);
    }

    [Theory]
    [InlineData(3, false)] [InlineData(3, true)] [InlineData(5, false)] [InlineData(5, true)] [InlineData(7, false)] [InlineData(7, true)]
    public async Task AutomaticLineGradientOptionsExecuteAndPreserveOriginalCoordinates(int aperture, bool l2)
    {
        const int w = 128, h = 96;
        byte[] pixels = Enumerable.Range(0, w * h).Select(i => (byte)(i / w is >= 40 and <= 65 ? 230 : 20)).ToArray();
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save("FindLines", new() { ["edgeAperture"] = aperture, ["edgeL2Gradient"] = l2 }));
        using var session = recipe.CreateSession(); using var frame = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80);
        using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.NotEmpty(result.GetLines("Output"));
        Assert.All(result.GetLines("Output"), line => { Assert.InRange(line.X1, 900, 1027); Assert.InRange(line.X2, 900, 1027); Assert.InRange(line.Y1, -42, -14); Assert.InRange(line.Y2, -42, -14); });
    }

    private async Task<VisionLine> Caliper(bool median, bool subpixel)
    {
        const int size = 81;
        var pixels = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) pixels[y * size + x] = (byte)(y >= 55 ? 50 : 20);
        foreach (int cx in new[] { 10, 30, 50, 70 })
            for (int y = 14; y < 25; y++) for (int x = cx - 1; x <= cx + 1; x++) pixels[y * size + x] = 230;
        var p = new JsonObject
        {
            ["caliperPlacement"] = "guide", ["caliperCount"] = 4, ["guideLength"] = 60.0, ["searchLength"] = 64.0,
            ["projectionWidth"] = 9, ["sigma"] = 0.0, ["edgeThreshold"] = 5.0, ["polarity"] = "darkToLight",
            ["maximumDeviation"] = 2.0, ["projectionStatistic"] = median ? "median" : "mean", ["subpixelRefinement"] = subpixel
        };
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save("LineCaliper", p));
        using var session = recipe.CreateSession(); using var frame = VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8);
        using var result = await session.RunAsync(frame, new() { Debug = true });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); return Assert.Single(result.GetLines("Output"));
    }

    [Fact]
    public async Task MedianCaliperProjectionRejectsNarrowBrightScratchesAcrossTheSamplingWindow()
    {
        var mean = await Caliper(false, true); var median = await Caliper(true, true);
        Assert.InRange((mean.Y1 + mean.Y2) / 2, 12, 15); Assert.InRange((median.Y1 + median.Y2) / 2, 54, 55);
    }

    [Fact]
    public async Task SubpixelSwitchControlsPeakInterpolationWithoutChangingTheCaliperPositions()
    {
        var refined = await Caliper(true, true); var integer = await Caliper(true, false);
        Assert.InRange(Math.Abs((refined.Y1 + refined.Y2 - integer.Y1 - integer.Y2) / 2), .4, .6);
        Assert.Equal(refined.EdgePoints!.Select(p => p.X), integer.EdgePoints!.Select(p => p.X));
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

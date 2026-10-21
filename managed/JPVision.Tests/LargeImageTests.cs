using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class LargeImageTests : IDisposable
{
    private readonly string store;
    public LargeImageTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt")))
            root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests", Guid.NewGuid().ToString());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }
    private static JsonObject Definition(string type, JsonObject? parameters = null) => new()
    {
        ["schemaVersion"] = 1,
        ["minimumRuntimeAbi"] = 1,
        ["recipeId"] = "large-image",
        ["recipeVersion"] = 1,
        ["kind"] = "processing",
        ["decisionOutput"] = "",
        ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() }, new JsonObject { ["id"] = "detect", ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() }),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "detect", ["toPort"] = "input" }),
        ["outputs"] = new JsonObject { ["Features"] = new JsonObject { ["node"] = "detect", ["port"] = "features", ["type"] = "Features" }, ["Measurement"] = new JsonObject { ["node"] = "detect", ["port"] = "measurement", ["type"] = "Measurement" }, ["ResultOverlay"] = new JsonObject { ["node"] = "detect", ["port"] = "overlay", ["type"] = "Overlay" } }
    };
    private async Task<InspectionResult> Run(string type, VisionFrame frame, JsonObject? parameters = null)
    {
        string folder = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recipe.json"), VisionEngine.SealRecipe(Definition(type, parameters).ToJsonString()));
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(folder);
        await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new()
        {
            TimeoutMs = 30000
        });
    }
    private static VisionFrame Circles(int width = 5120, int height = 5120, int originX = 0, int originY = 0, bool bgr = false)
    {
        int channels = bgr ? 3 : 1, stride = width * channels + 3;
        byte[] pixels = new byte[stride * height];
        foreach (var at in new[] { (2157, 1639, 80), (3219, 3573, 105) })
        {
            for (int y = Math.Max(0, at.Item2 - at.Item3 - 2); y <= Math.Min(height - 1, at.Item2 + at.Item3 + 2); y++)
            for (int x = Math.Max(0, at.Item1 - at.Item3 - 2); x <= Math.Min(width - 1, at.Item1 + at.Item3 + 2); x++)
                if ((x - at.Item1) * (x - at.Item1) + (y - at.Item2) * (y - at.Item2) <= at.Item3 * at.Item3)
                for (int c = 0; c < channels; c++)
                    pixels[y * stride + x * channels + c] = 240;
        }
        return VisionFrame.CopyFrom(pixels, width, height, stride, bgr ? VisionPixelFormat.Bgr24 : VisionPixelFormat.Gray8, originX, originY);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A5120PixelImageCanFindCirclesInOriginalCoordinates(bool bgr)
    {
        using var frame = Circles(originX: 150, originY: -35, bgr: bgr);
        using var result = await Run("FindCircles", frame, new()
        {
            ["minimumRadius"] = 65,
            ["maximumRadius"] = 120,
            ["minimumDistance"] = 200,
            ["voteThreshold"] = 20
        });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        var circles = result.GetCircles();
        Assert.Equal(2, circles.Count);
        Assert.Contains(circles, c => Math.Abs(c.X - 2307) < 5 && Math.Abs(c.Y - 1604) < 5 && Math.Abs(c.Radius - 80) < 5);
        Assert.Contains(circles, c => Math.Abs(c.X - 3369) < 5 && Math.Abs(c.Y - 3538) < 5 && Math.Abs(c.Radius - 105) < 5);
        var measurement = result.GetOutput<JsonElement>("Measurement");
        var detection = measurement.GetProperty("detection");
        Assert.True(detection.GetProperty("downsampled").GetBoolean());
        Assert.True((long)detection.GetProperty("workWidth").GetInt32() * detection.GetProperty("workHeight").GetInt32() <= 4194304);
        Assert.False(measurement.TryGetProperty("caliper", out _));
        Assert.NotEmpty(result.GetOverlays());
        Assert.True(result.GetNodeMeasurement<VisionCircle>("detect").Detection!.Downsampled);
    }
    [Fact]
    public async Task A5120PixelImageFindsLongLinesWithOriginalLengths()
    {
        const int width = 5120, height = 3000;
        byte[] pixels = new byte[width * height];
        for (int y = 971; y <= 982; y++)
        for (int x = 173; x <= 4689; x++)
            pixels[y * width + x] = 240;
        using var frame = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 90, -20);
        using var result = await Run("FindLines", frame, new()
        {
            ["minimumLength"] = 4000,
            ["maximumGap"] = 15,
            ["voteThreshold"] = 100
        });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var lines = result.GetLines();
        Assert.NotEmpty(lines);
        Assert.All(lines, line => { Assert.InRange(line.Length, 4000, 4530); Assert.InRange(line.X1, 257, 278); Assert.InRange(line.X2, 4760, 4790); Assert.InRange(line.Y1, 945, 968); Assert.InRange(Math.Abs(line.AngleDegrees), 0, .2); });
    }
    [Theory]
    [InlineData("FindCircles")]
    [InlineData("FindLines")]
    public async Task ExplicitOriginalResolutionModeKeepsAUsefulSizeErrorAndNoResults(string type)
    {
        using var frame = Circles();
        using var result = await Run(type, frame, new()
        {
            ["largeImageMode"] = "original"
        });
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.Outputs.EnumerateObject());
        Assert.Contains("5120", result.Data.GetProperty("error").GetProperty("message").GetString());
    }
    [Fact]
    public async Task AdaptiveDetectionPreservesAPolygonMaskAndItsOriginalOffset()
    {
        using var frame = Circles(originX: 150, originY: -35);
        using var result = await Run("FindCircles", frame, new()
        {
            ["roi"] = new JsonObject { ["shape"] = "polygon", ["points"] = new JsonArray(new JsonArray(500, 500), new JsonArray(4100, 500), new JsonArray(500, 4100)) },
            ["minimumRadius"] = 65,
            ["maximumRadius"] = 120,
            ["voteThreshold"] = 20,
            ["minimumDistance"] = 200
        });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.X, 2302, 2312);
        Assert.InRange(circle.Y, 1599, 1609);
        Assert.InRange(circle.Radius, 75, 85);
        var resolution = result.GetNodeMeasurement<VisionCircle>("detect").Detection!;
        Assert.True(resolution.Downsampled);
        Assert.InRange(resolution.InputWidth, 3600, 3601);
        Assert.InRange(resolution.InputHeight, 3600, 3601);
    }
    [Fact]
    public async Task OriginalResolutionCanRunUsingAModuleRoiOnTheSameLargeImage()
    {
        using var frame = Circles();
        using var result = await Run("FindCircles", frame, new()
        {
            ["largeImageMode"] = "original",
            ["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 2000, ["y"] = 1500, ["width"] = 350, ["height"] = 300 },
            ["minimumRadius"] = 65,
            ["maximumRadius"] = 95,
            ["voteThreshold"] = 20
        });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.X, 2154, 2160);
        Assert.InRange(circle.Y, 1636, 1642);
        Assert.False(result.GetOutput<JsonElement>("Measurement").GetProperty("detection").GetProperty("downsampled").GetBoolean());
    }
    [Theory]
    [InlineData("FindCircles")]
    [InlineData("FindLines")]
    public async Task LargeBlankImageIsASuccessfulEmptyResult(string type)
    {
        const int w = 5120, h = 3000;
        using var frame = VisionFrame.CopyFrom(new byte[w * h], w, h, w, VisionPixelFormat.Gray8);
        using var result = await Run(type, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.GetOutput<JsonElement>("Features").EnumerateArray());
        Assert.Empty(result.GetOverlays());
    }
    public void Dispose()
    {
        if (Directory.Exists(store))
            Directory.Delete(store, true);
    }
}

using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class AdvancedRoiTests : IDisposable
{
    private readonly string store;
    public AdvancedRoiTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "advanced-roi-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Definition(JsonObject roi) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "advanced-roi", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "binary", ["type"] = "Threshold", ["version"] = 1, ["parameters"] = new JsonObject { ["roi"] = roi } }),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "binary", ["toPort"] = "input" }),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject { ["Mask"] = new JsonObject { ["node"] = "binary", ["port"] = "mask", ["type"] = "Mask" } }
    };
    private string Save(JsonObject d)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return path;
    }
    [Theory]
    [InlineData(0, 360)] [InlineData(0, 90)] [InlineData(300, 420)]
    public async Task AnnulusMaskMatchesItsActualSectorAndExcludesTheHole(double start, double end)
    {
        var roi = RoiRegion.Annulus(50, 50, 10, 30, start, end);
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition(roi.ToJson())));
        await using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(Enumerable.Repeat((byte)230, 10000).ToArray(), 100, 100, 100, VisionPixelFormat.Gray8, 900, -80);
        using var result = await session.RunAsync(frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        using var output = result.GetOutputImage("Mask"); var pixels = output.CopyPixels();
        Assert.Equal(920, output.Info.OriginX); Assert.Equal(-60, output.Info.OriginY);
        int expected = 0, actual = pixels.Count(p => p != 0);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++) if (roi.Contains(x, y)) expected++;
        Assert.Equal(expected, actual);
        Assert.Equal(0, pixels[(50 - 20) * output.Info.Stride + 50 - 20]);
    }
    [Fact]
    public async Task RotatedRectanglePreservesRotationAndMasksBoundingBoxCorners()
    {
        var roi = RoiRegion.RotatedRectangle(50, 50, 50, 20, 37);
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition(roi.ToJson())));
        await using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(Enumerable.Repeat((byte)230, 10000).ToArray(), 100, 100, 100, VisionPixelFormat.Gray8);
        using var result = await session.RunAsync(frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        using var output = result.GetOutputImage("Mask"); var pixels = output.CopyPixels();
        Assert.Equal(0, pixels[0]); Assert.InRange(pixels.Count(p => p != 0), 950, 1100);
        int at = (50 - output.Info.OriginY) * output.Info.Stride + 50 - output.Info.OriginX;
        Assert.Equal(255, pixels[at]);
    }
    [Theory]
    [InlineData("radii")] [InlineData("span")] [InlineData("narrow")] [InlineData("angle")]
    public void InvalidRoiGeometryIsRejected(string kind)
    {
        JsonObject roi = kind == "angle" ? RoiRegion.RotatedRectangle(50, 50, 20, 20, 30).ToJson() : RoiRegion.Annulus(50, 50, 10, 30).ToJson();
        if (kind == "radii") roi["innerRadius"] = 30;
        if (kind == "span") roi["endAngle"] = 0;
        if (kind == "narrow") roi["outerRadius"] = 9;
        if (kind == "angle") roi["angleDegrees"] = 900;
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Definition(roi))));
    }
    [Fact]
    public void RotatedAndAnnularReferenceRoisRoundtripAndDoNotAccumulatePose()
    {
        var a = new VisionMatchPosition(900, -80, 20, 1.2); var b = new VisionMatchPosition(1000, 40, -40, .8);
        var fixture = new VisionFixture(a, b, new string('a', 64), "similarity", "original", Array.Empty<double>()) { ReferenceHash = new string('b', 64) };
        var frame = new FrameInfo(500, 460, 500, VisionPixelFormat.Gray8, 900, -80);
        foreach (var original in new[] { RoiRegion.Annulus(940, 40, 8, 32, 300, 420), RoiRegion.RotatedRectangle(940, 40, 60, 20, 35) })
        {
            var projected = FixturePositioning.Resolve(original, new(), fixture, frame);
            var stored = FixturePositioning.Store(projected, fixture, frame);
            var loaded = RoiRegion.FromJson(stored.ToJson());
            Assert.Equal("reference", loaded.CoordinateSpace); Assert.Equal(fixture.ReferenceHash, loaded.ReferenceHash);
            Assert.InRange(Math.Abs(original.X - loaded.X), 0, 1e-8); Assert.InRange(Math.Abs(original.Y - loaded.Y), 0, 1e-8);
            Assert.InRange(Math.Abs(original.Radius - loaded.Radius), 0, 1e-8);
            var again = FixturePositioning.Resolve(original, new(), fixture, frame); Assert.Equal(projected.ToJson().ToJsonString(), again.ToJson().ToJsonString());
        }
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
    [Fact]
    public void RotatedRoiHandlesReferenceAndCurrentAnglesAcrossTheWrapBoundary()
    {
        var fixture = new VisionFixture(new(100, 100, 179, 1), new(110, 120, -179, 1), new string('a', 64), "similarity", "original", Array.Empty<double>());
        var original = RoiRegion.RotatedRectangle(130, 100, 40, 10, -179);
        var frame = new FrameInfo(500, 460, 500, VisionPixelFormat.Gray8, 0, 0);
        var projected = FixturePositioning.Resolve(original, new(), fixture, frame);
        Assert.InRange(projected.AngleDegrees, -177.00001, -176.99999);
        var stored = FixturePositioning.Store(projected, fixture, frame);
        Assert.InRange(stored.AngleDegrees, -179.00001, -178.99999);
        Assert.InRange(Math.Abs(stored.X - original.X), 0, 1e-8);
    }
}

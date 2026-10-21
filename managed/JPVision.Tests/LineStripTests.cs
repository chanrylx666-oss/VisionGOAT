using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class LineStripTests : IDisposable
{
    private readonly string store;
    public LineStripTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/line-strip-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }
    private static JsonObject Definition(JsonObject parameters) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "line-strip", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
            new JsonObject { ["id"] = "line", ["type"] = "FindLines", ["version"] = 1, ["parameters"] = parameters }),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "line", ["toPort"] = "input" }),
        ["outputs"] = new JsonObject { ["Result"] = new JsonObject { ["node"] = "line", ["port"] = "result", ["type"] = "Result" } }
    };
    private async Task<InspectionResult> Run(JsonObject parameters, bool blank = false, bool spikes = false)
    {
        double degrees = parameters["roi"]!["angleDegrees"]!.GetValue<double>(), angle = degrees * Math.PI / 180;
        double nx = -Math.Sin(angle), ny = Math.Cos(angle);
        const int w = 360, h = 260; byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double along = (x - 180) * Math.Cos(angle) + (y - 130) * Math.Sin(angle), offset = spikes && along < -80 ? 27 : 3;
            pixels[y * w + x] = blank ? (byte)20 : (byte)Math.Clamp(125 + ((x - 180) * nx + (y - 130) * ny - offset) * 200, 20, 230);
        }
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(Definition(parameters).ToJsonString()));
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(path); await using var session = recipe.CreateSession();
        using var frame = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 500, -20);
        return await session.RunAsync(frame);
    }
    private static JsonObject Parameters(double degrees, bool reverse = false) => new()
    {
        ["method"] = "caliper", ["caliperPlacement"] = "roiCenter", ["roi"] = RoiRegion.RotatedRectangle(180, 130, 220, 40, degrees).ToJson(),
        ["caliperCount"] = 32, ["projectionWidth"] = 5, ["searchLength"] = 8.0,
        ["reverseSearch"] = reverse, ["polarity"] = reverse ? "lightToDark" : "darkToLight",
        ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .8
    };
    [Theory]
    [InlineData(0.0, false)] [InlineData(25.0, false)] [InlineData(90.0, false)] [InlineData(-45.0, false)] [InlineData(25.0, true)]
    public async Task FindLinesFitsTheEdgeAcrossTheRoiCenterAndPreservesItsOriginalCoordinates(double degrees, bool reverse)
    {
        var parameters = Parameters(degrees, reverse);
        var roi = RoiRegion.FromJson(parameters["roi"]);
        using var result = await Run(parameters);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionLine>("Result"); var line = Assert.Single(packet.Features);
        double radians = degrees * Math.PI / 180, nx = -Math.Sin(radians), ny = Math.Cos(radians);
        Assert.InRange(Math.Abs((line.X1 - 680) * nx + (line.Y1 - 110) * ny - 3), 0, .3);
        Assert.InRange(Math.Abs((line.X2 - 680) * nx + (line.Y2 - 110) * ny - 3), 0, .3);
        Assert.InRange(line.Length, 211, 215); Assert.Equal(32, line.EdgePoints.Count);
        Assert.NotNull(line.Equation); Assert.InRange(line.RmsError, 0, .2);
        var stats = packet.Measurement.Caliper!; Assert.True(stats.FitSucceeded); Assert.Equal("roiCenter", stats.Placement);
        var preview = CaliperLayout.Create(roi, parameters, false, 360, 260);
        Assert.Equal(32, preview.Count); Assert.Equal(preview.Count, stats.Guides.Count);
        for (int i = 0; i < preview.Count; i++)
        {
            var a = preview[i]; var b = stats.Guides[i];
            Assert.Equal(a.X + 500, b.X, 7); Assert.Equal(a.Y - 20, b.Y, 7);
            Assert.Equal(a.NormalX, b.NormalX, 7); Assert.Equal(a.NormalY, b.NormalY, 7);
            Assert.Equal(40, b.Height); Assert.Equal(5, b.Width);
        }
        Assert.Contains(packet.Overlay.EnumerateArray(), item => item.GetProperty("type").GetString() == "line");
    }
    [Fact]
    public void EndpointAndHeightHandlesChangeOnlyTheIntendedGeometry()
    {
        var roi = LineCaliperRoi.FromEndpoints(new(40, 50), new(240, 150), 40);
        var ends = LineCaliperRoi.Endpoints(roi); Assert.Equal(40, ends.Start.X, 8); Assert.Equal(150, ends.End.Y, 8);
        var resized = LineCaliperRoi.Adjust(roi, 0, new(20, 70)); var changed = LineCaliperRoi.Endpoints(resized);
        Assert.Equal(240, changed.End.X, 8); Assert.Equal(150, changed.End.Y, 8); Assert.Equal(40, resized.Height);
        var point = LineCaliperRoi.Handles(resized)[2]; double angle = resized.AngleDegrees * Math.PI / 180;
        var taller = LineCaliperRoi.Adjust(resized, 2, new(point.X - Math.Sin(angle) * 10, point.Y + Math.Cos(angle) * 10));
        Assert.Equal(resized.Width, taller.Width, 8); Assert.Equal(resized.AngleDegrees, taller.AngleDegrees, 8); Assert.Equal(60, taller.Height, 8);
        var moved = LineCaliperRoi.Adjust(taller, 3, new(taller.X + 15, taller.Y - 5));
        Assert.Equal(taller.Width, moved.Width, 8); Assert.Equal(taller.Height, moved.Height, 8);
        Assert.Equal(taller.X + 15, moved.X, 8); Assert.Equal(taller.Y - 5, moved.Y, 8);
        Assert.Throws<ArgumentException>(() => LineCaliperRoi.FromEndpoints(new(10, 10), new(10, 10), 40));
    }
    [Theory]
    [InlineData(0)] [InlineData(4)]
    public async Task AClusterOfBadCalipersDoesNotDiscardTheGoodStraightEdge(int removal)
    {
        var p = Parameters(0); p["roi"] = RoiRegion.RotatedRectangle(180, 130, 220, 90, 0).ToJson(); p["removePointCount"] = removal;
        using var result = await Run(p, spikes: true);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionLine>("Result"); Assert.Equal(1, packet.Count);
        var line = Assert.Single(packet.Features); Assert.Equal(28, line.EdgePoints.Count);
        Assert.InRange(Math.Abs(line.Y1 - 113), 0, .2); Assert.InRange(Math.Abs(line.Y2 - 113), 0, .2); Assert.Equal(4, packet.Measurement.Caliper!.RejectedCount);
    }
    [Fact]
    public async Task ABlankStripDoesNotInventARoiCenterLine()
    {
        using var result = await Run(Parameters(25), blank: true);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionLine>("Result"); Assert.Equal(0, packet.Count); Assert.Null(packet.Measurement.Selected);
        Assert.False(packet.Measurement.Caliper!.FitSucceeded); Assert.Equal("InsufficientEdgePoints", packet.Measurement.Caliper.FitFailureReason);
        Assert.Empty(packet.Overlay.EnumerateArray());
    }
    [Fact]
    public void NewLineNodesDefaultToStripCalipersWhileLegacyRecipesRemainAutomatic()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var flow = new FlowDocument(catalog);
        flow.Add("FindLines", 0, 0, "line");
        Assert.Equal("caliper", flow.Node("line")["parameters"]!["method"]!.GetValue<string>());
        Assert.Equal("roiCenter", flow.Node("line")["parameters"]!["caliperPlacement"]!.GetValue<string>());
        var method = catalog["FindLines"].Parameters.OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "method");
        Assert.Equal("automatic", method["default"]!.GetValue<string>());
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

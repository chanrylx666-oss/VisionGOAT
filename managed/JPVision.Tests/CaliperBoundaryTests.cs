using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class CaliperBoundaryTests : IDisposable
{
    private readonly string store;
    public CaliperBoundaryTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt")))
            root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "caliper-boundary-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Circle(double x, double y, double r) => new() { ["shape"] = "circle", ["cx"] = x, ["cy"] = y, ["radius"] = r };
    private static JsonObject Rectangle(int x, int y, int w, int h) => new() { ["shape"] = "rectangle", ["x"] = x, ["y"] = y, ["width"] = w, ["height"] = h };
    private static JsonObject Polygon(bool reverse = false) => new()
    {
        ["shape"] = "polygon",
        ["points"] = reverse
            ? new JsonArray(new JsonArray(70, 80), new JsonArray(70, 160), new JsonArray(290, 160), new JsonArray(290, 80))
            : new JsonArray(new JsonArray(70, 80), new JsonArray(290, 80), new JsonArray(290, 160), new JsonArray(70, 160))
    };
    private static JsonObject Parameters(JsonObject roi) => new()
    {
        ["roi"] = roi,
        ["caliperPlacement"] = "roiBoundary",
        ["caliperCount"] = 32,
        ["searchLength"] = 24,
        ["projectionWidth"] = 5,
        ["polarity"] = "lightToDark",
        ["maximumDeviation"] = 2,
        ["minimumCoverage"] = .5
    };
    private static JsonObject Definition(string type, JsonObject parameters, JsonObject? upstreamRoi = null)
    {
        JsonObject Node(string id, string component, JsonObject? p = null) => new()
        {
            ["id"] = id,
            ["type"] = component,
            ["version"] = 1,
            ["parameters"] = p ?? new()
        };
        JsonObject Edge(string from, string to) => new()
        {
            ["fromNode"] = from,
            ["fromPort"] = "image",
            ["toNode"] = to,
            ["toPort"] = "input"
        };
        var nodes = new JsonArray(Node("source", "ExternalImage"));
        var edges = new JsonArray();
        string input = "source";
        if (upstreamRoi is not null)
        {
            nodes.Add(Node("parent", "ToGray", new()
            {
                ["roi"] = upstreamRoi
            }));
            edges.Add(Edge("source", "parent"));
            input = "parent";
        }
        nodes.Add(Node("detect", type, parameters));
        edges.Add(Edge(input, "detect"));
        JsonObject Output(string port, string dataType) => new()
        {
            ["node"] = "detect",
            ["port"] = port,
            ["type"] = dataType
        };
        return new()
        {
            ["schemaVersion"] = 1,
            ["minimumRuntimeAbi"] = 1,
            ["kind"] = "processing",
            ["recipeId"] = "caliper-boundary-tests",
            ["recipeVersion"] = 1,
            ["nodes"] = nodes,
            ["connections"] = edges,
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = new JsonObject { ["Features"] = Output("features", "Features"), ["Measurement"] = Output("measurement", "Measurement") },
            ["decisionOutput"] = "",
            ["resources"] = new JsonArray()
        };
    }
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return path;
    }
    private async Task<InspectionResult> Run(string type, JsonObject p, Func<int, int, byte> pixel, int ox = 0, int oy = 0, JsonObject? parent = null)
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(Definition(type, p, parent)));
        await using var session = recipe.CreateSession();
        byte[] bytes = new byte[360 * 240];
        for (int y = 0; y < 240; y++)
        for (int x = 0; x < 360; x++)
            bytes[y * 360 + x] = pixel(x, y);
        using var image = VisionFrame.CopyFrom(bytes, 360, 240, 360, VisionPixelFormat.Gray8, ox, oy);
        return await session.RunAsync(image);
    }
    private static JsonElement Caliper(InspectionResult result) => result.GetOutput<JsonElement>("Measurement").GetProperty("caliper");

    [Theory]
    [InlineData("CircleCaliper", 180, 120, 50)]
    [InlineData("CircleCaliper", 200.25, 105.75, 65)]
    [InlineData("FindCircles", 180, 120, 50)]
    [InlineData("FindCircles", 200.25, 105.75, 65)]
    public async Task CircularCalipersFollowActualRoiCenterAndRadius(string type, double cx, double cy, double radius)
    {
        var p = Parameters(Circle(cx, cy, radius));
        p["radius"] = 12;
        if (type == "FindCircles")
            p["method"] = "caliper";
        using var result = await Run(type, p, (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius ? (byte)230 : (byte)15, 700, -40);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.CenterX, cx + 699, cx + 701);
        Assert.InRange(circle.CenterY, cy - 41, cy - 39);
        Assert.InRange(circle.Radius, radius - 1, radius + 1);
        var stats = Caliper(result);
        Assert.Equal("roiBoundary", stats.GetProperty("placement").GetString());
        var guides = stats.GetProperty("guides").EnumerateArray().ToArray();
        Assert.Equal(32, guides.Length);
        foreach (var g in guides)
        {
            double nx = g.GetProperty("normalX").GetDouble(), ny = g.GetProperty("normalY").GetDouble();
            Assert.Equal(cx + 700 + radius * nx, g.GetProperty("x").GetDouble(), 8);
            Assert.Equal(cy - 40 + radius * ny, g.GetProperty("y").GetDouble(), 8);
            Assert.Equal(5, g.GetProperty("width").GetDouble());
            Assert.Equal(24, g.GetProperty("height").GetDouble());
        }
    }
    [Fact]
    public async Task FractionalSearchLengthReportsActualSampledSpan()
    {
        var p = Parameters(Circle(180, 120, 50));
        p["searchLength"] = 25.2;
        using var result = await Run("CircleCaliper", p, (x, y) => (x - 180) * (x - 180) + (y - 120) * (y - 120) <= 2500 ? (byte)230 : (byte)15);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Single(result.GetCircles());
        Assert.All(Caliper(result).GetProperty("guides").EnumerateArray(), g => Assert.Equal(26, g.GetProperty("height").GetDouble()));
    }
    [Theory]
    [InlineData("top", 0, -1)]
    [InlineData("right", 1, 0)]
    [InlineData("bottom", 0, 1)]
    [InlineData("left", -1, 0)]
    public async Task RectangleSelectedEdgesUseOutwardNormalsAndCrossBoundary(string edge, double nx, double ny)
    {
        var p = Parameters(Rectangle(70, 80, 220, 80));
        p["roiEdge"] = edge;
        using var result = await Run("LineCaliper", p, (x, y) => x >= 70 && x < 290 && y >= 80 && y < 160 ? (byte)230 : (byte)15, 500, -20);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var line = Assert.Single(result.GetLines());
        if (nx == 0)
        {
            double expectedY = edge == "top" ? 60 : 140;
            Assert.InRange(line.Y1, expectedY - 1, expectedY + 1);
            Assert.InRange(line.Y2, expectedY - 1, expectedY + 1);
        }
        else
        {
            double expectedX = edge == "left" ? 570 : 790;
            Assert.InRange(line.X1, expectedX - 1, expectedX + 1);
            Assert.InRange(line.X2, expectedX - 1, expectedX + 1);
        }
        var guides = Caliper(result).GetProperty("guides").EnumerateArray().ToArray();
        Assert.Equal(32, guides.Length);
        Assert.All(guides, g => { Assert.Equal(nx, g.GetProperty("normalX").GetDouble(), 8); Assert.Equal(ny, g.GetProperty("normalY").GetDouble(), 8); });
        var first = guides[0];
        if (nx == 0)
            Assert.InRange(first.GetProperty("x").GetDouble(), 570.01, 789.99);
        else
            Assert.InRange(first.GetProperty("y").GetDouble(), 60.01, 139.99);
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public async Task PolygonWindingKeepsSelectedEdgeOutward(bool reverse, int edgeIndex)
    {
        var p = Parameters(Polygon(reverse));
        p["roiEdgeIndex"] = edgeIndex;
        using var result = await Run("LineCaliper", p, (x, y) => x >= 70 && x < 290 && y >= 80 && y < 160 ? (byte)230 : (byte)15);
        var line = Assert.Single(result.GetLines());
        Assert.InRange(line.Y1, 79, 81);
        Assert.InRange(line.Y2, 79, 81);
        Assert.All(Caliper(result).GetProperty("guides").EnumerateArray(), g => { Assert.Equal(0, g.GetProperty("normalX").GetDouble(), 8); Assert.Equal(-1, g.GetProperty("normalY").GetDouble(), 8); });
    }
    [Fact]
    public async Task SlantedPolygonEdgeFollowsVerticesInsteadOfBoundingBox()
    {
        var roi = new JsonObject { ["shape"] = "polygon", ["points"] = new JsonArray(new JsonArray(60, 70), new JsonArray(290, 90), new JsonArray(250, 180), new JsonArray(80, 160)) };
        var p = Parameters(roi);
        p["roiEdgeIndex"] = 0;
        using var result = await Run("LineCaliper", p, (x, y) => x >= 60 && x < 290 && y >= 70 + (x - 60) * 20d / 230 && y < 175 ? (byte)230 : (byte)15);
        var line = Assert.Single(result.GetLines());
        Assert.InRange(line.AngleDegrees, 4.5, 5.5);
        double length = Math.Sqrt(230 * 230 + 20 * 20);
        foreach (var g in Caliper(result).GetProperty("guides").EnumerateArray())
        {
            Assert.Equal(20 / length, g.GetProperty("normalX").GetDouble(), 8);
            Assert.Equal(-230 / length, g.GetProperty("normalY").GetDouble(), 8);
            Assert.Equal(70 + (g.GetProperty("x").GetDouble() - 60) * 20 / 230, g.GetProperty("y").GetDouble(), 8);
        }
    }
    [Fact]
    public async Task NestedRoiOffsetsStayInOriginalCoordinates()
    {
        var p = Parameters(Circle(80, 70, 40));
        using var result = await Run("CircleCaliper", p, (x, y) => (x - 180) * (x - 180) + (y - 120) * (y - 120) <= 1600 ? (byte)230 : (byte)15, 900, -60, Rectangle(100, 50, 160, 140));
        var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.X, 1079, 1081);
        Assert.InRange(circle.Y, 59, 61);
        var first = Caliper(result).GetProperty("guides")[0];
        Assert.Equal(1120, first.GetProperty("x").GetDouble());
        Assert.Equal(60, first.GetProperty("y").GetDouble());
    }
    [Fact]
    public async Task BoundaryModeNeverDiscardsUpstreamValidityMask()
    {
        var p = Parameters(Circle(45, 45, 40));
        p["searchLength"] = 40;
        p["minimumCoverage"] = .1;
        using var result = await Run("CircleCaliper", p, (x, y) => (x - 180) * (x - 180) + (y - 120) * (y - 120) <= 2500 ? (byte)230 : (byte)15, parent: Circle(180, 120, 45));
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.GetCircles());
        Assert.Equal(0, Caliper(result).GetProperty("sampledCount").GetInt32());
    }
    [Fact]
    public async Task ImageBorderAndInvalidWindowSamplesDoNotCreateFakeEdges()
    {
        var p = Parameters(Rectangle(0, 0, 220, 80));
        p["roiEdge"] = "top";
        using var result = await Run("LineCaliper", p, (_, _) => 230);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.GetLines());
        Assert.Equal(0, Caliper(result).GetProperty("sampledCount").GetInt32());
        Assert.False(result.GetOutput<JsonElement>("Measurement").GetProperty("selectedValid").GetBoolean());
    }
    [Theory]
    [InlineData("CircleCaliper", "rectangle", 0)]
    [InlineData("FindCircles", "polygon", 0)]
    [InlineData("LineCaliper", "circle", 0)]
    [InlineData("LineCaliper", "none", 0)]
    [InlineData("LineCaliper", "polygon", 4)]
    public void InvalidReferenceShapesAndPolygonEdgeIndexAreRejected(string type, string shape, int index)
    {
        JsonObject roi = shape switch
        {
            "rectangle" => Rectangle(70, 80, 220, 80),
            "circle" => Circle(180, 120, 50),
            "polygon" => Polygon(),
            _ => new() { ["shape"] = "none" }
        };
        var p = Parameters(roi);
        if (type == "FindCircles")
            p["method"] = "caliper";
        if (type == "LineCaliper")
            p["roiEdgeIndex"] = index;
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Definition(type, p))));
        Assert.Contains(index == 4 ? "edge index" : "require", error.Message);
    }
    [Fact]
    public async Task PartiallyOutsideReferenceKeepsVisibleSamplingDiagnosticsWithoutInventingAnEdge()
    {
        using var result = await Run("CircleCaliper", Parameters(Circle(350, 120, 50)), (_, _) => 230);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.GetCircles());
        Assert.False(Caliper(result).GetProperty("fitSucceeded").GetBoolean());
        Assert.InRange(Caliper(result).GetProperty("validSamplingCalipers").GetInt32(), 16, 31);
    }
    [Fact]
    public async Task OmittedPlacementKeepsLegacyCenteredGuideAlgorithm()
    {
        var p = Parameters(Circle(180, 120, 70));
        p.Remove("caliperPlacement");
        p["radius"] = 45;
        p["searchLength"] = 30;
        using var result = await Run("CircleCaliper", p, (x, y) => (x - 180) * (x - 180) + (y - 120) * (y - 120) <= 2500 ? (byte)230 : (byte)15);
        var circle = Assert.Single(result.GetCircles());
        Assert.InRange(circle.Radius, 49, 51);
        Assert.False(Caliper(result).TryGetProperty("guides", out _));
    }
    public void Dispose()
    {
        if (Directory.Exists(store))
            Directory.Delete(store, true);
    }
}

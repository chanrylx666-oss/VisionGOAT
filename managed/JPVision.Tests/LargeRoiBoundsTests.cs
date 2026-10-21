using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class LargeRoiBoundsTests : IDisposable
{
    private readonly string store;

    public LargeRoiBoundsTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "large-roi-bounds-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string source, string target) => new()
    { ["fromNode"] = source, ["fromPort"] = "image", ["toNode"] = target, ["toPort"] = "input" };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonArray nodes, JsonArray edges, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "large-roi-bounds", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = nodes, ["connections"] = edges, ["outputs"] = outputs
    };
    private static JsonObject Single(string type, JsonObject parameters) => Recipe(
        new JsonArray(Node("source", "ExternalImage"), Node("target", type, parameters)),
        new JsonArray(Edge("source", "target")), new JsonObject
        {
            ["Image"] = Output("target"),
            ["Result"] = Output("target", "result", "Result")
        });
    private static JsonObject LineParameters(double cx, double cy, double width, double height, double angle = 0) => new()
    {
        ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
        ["roi"] = RoiRegion.RotatedRectangle(cx, cy, width, height, angle).ToJson(),
        ["caliperCount"] = 64, ["projectionWidth"] = 5, ["searchLength"] = 30.0,
        ["sigma"] = 1.0, ["polarity"] = "darkToLight", ["maximumDeviation"] = 1.5, ["minimumCoverage"] = .7
    };
    private static JsonObject ImageOnly(string type, JsonObject parameters)
    {
        var definition = Single(type, parameters); definition["outputs"]!.AsObject().Remove("Result"); return definition;
    }
    private static JsonObject CircleParameters(double cx, double cy, double radius) => new()
    {
        ["roi"] = RoiRegion.Circle(cx, cy, radius).ToJson(), ["caliperPlacement"] = "roiBoundary",
        ["caliperCount"] = 64, ["searchLength"] = 30.0, ["projectionWidth"] = 5,
        ["sigma"] = 1.0, ["polarity"] = "lightToDark", ["minimumCoverage"] = .4, ["maximumDeviation"] = 1.5
    };
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); var result = await session.RunAsync(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static VisionFrame Gray(int width, int height, Func<int, int, byte> pixel, int originX = 0, int originY = 0)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = pixel(x, y);
        return VisionFrame.CopyFrom(bytes, width, height, width, VisionPixelFormat.Gray8, originX, originY);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(.8)]
    public async Task FullWidthLongStripFitsInsideA10918By287ImageUsingValidSamples(double angle)
    {
        const int width = 10918, height = 287;
        double tangent = Math.Tan(angle * Math.PI / 180);
        using var frame = Gray(width, height, (x, y) => y >= 143 + tangent * (x - width / 2.0) ? (byte)230 : (byte)20, 700, -90);
        using var result = await Run(Single("FindLines", LineParameters(width / 2.0, height / 2.0, width, height, angle)), frame);
        var packet = result.GetResult<VisionLine>("Result"); var line = Assert.Single(packet.Features);
        Assert.True(packet.Measurement.Caliper!.FitSucceeded); Assert.Equal("original", packet.CoordinateSpace);
        Assert.InRange(line.Length, width * .9, width + 2);
        foreach (var point in line.EdgePoints)
        {
            Assert.InRange(point.X, 700, 700 + width - 1);
            Assert.InRange(point.Y, -90, -90 + height - 1);
            Assert.InRange(Math.Abs(point.Y + 90 - (143 + tangent * (point.X - 700 - width / 2.0))), 0, 1.0);
        }
    }

    [Theory]
    [InlineData("rectangle")] [InlineData("rotatedRectangle")] [InlineData("polygon")]
    [InlineData("circle")] [InlineData("annulus")]
    public async Task NonRectangularRoisMayTouchTheLastImageRowAndColumnWithoutAnExtraPixelError(string shape)
    {
        JsonObject roi = shape switch
        {
            "rectangle" => RoiRegion.Rectangle(0, 0, 240, 160).ToJson(),
            "rotatedRectangle" => RoiRegion.RotatedRectangle(120, 80, 240, 160, 0).ToJson(),
            "polygon" => RoiRegion.Polygon(new[] { new EditorPoint(0, 0), new(240, 0), new(240, 160), new(0, 160) }).ToJson(),
            "circle" => RoiRegion.Circle(80, 80, 80).ToJson(),
            _ => RoiRegion.Annulus(80, 80, 20, 80).ToJson()
        };
        using var frame = Gray(240, 160, (_, _) => 200, 700, -90);
        using var result = await Run(ImageOnly("ToGray", new() { ["roi"] = roi }), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(shape is "circle" or "annulus" ? 161 : 240, image.Info.Width);
        Assert.Equal(160, image.Info.Height); Assert.Equal(700, image.Info.OriginX); Assert.Equal(-90, image.Info.OriginY);
        Assert.Contains((byte)200, image.CopyPixels()); Assert.True(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
    }

    [Theory]
    [InlineData("rectangle")] [InlineData("rotatedRectangle")] [InlineData("polygon")]
    [InlineData("circle")] [InlineData("annulus")]
    public async Task OrdinaryImageRoisProcessOnlyTheirIntersectionAndDoNotChangeAParallelBranch(string shape)
    {
        JsonObject roi = shape switch
        {
            "rectangle" => RoiRegion.Rectangle(220, 140, 60, 50).ToJson(),
            "rotatedRectangle" => RoiRegion.RotatedRectangle(230, 145, 80, 60, 25).ToJson(),
            "polygon" => RoiRegion.Polygon(new[] { new EditorPoint(210, 120), new(280, 120), new(280, 200), new(210, 200) }).ToJson(),
            "circle" => RoiRegion.Circle(230, 145, 30).ToJson(),
            _ => RoiRegion.Annulus(230, 145, 5, 30).ToJson()
        };
        var definition = ImageOnly("ToGray", new() { ["roi"] = roi });
        definition["nodes"]!.AsArray().Add(Node("parallel", "ToGray"));
        definition["connections"]!.AsArray().Add(Edge("source", "parallel"));
        definition["outputs"]!["FullImage"] = Output("parallel");
        using var frame = Gray(240, 160, (_, _) => 200, 700, -90); using var result = await Run(definition, frame);
        using var selected = result.GetOutputImage("Image"); using var full = result.GetOutputImage("FullImage");
        Assert.InRange(selected.Info.Width, 1, 239); Assert.InRange(selected.Info.Height, 1, 159);
        Assert.InRange(selected.Info.OriginX, 700, 939); Assert.InRange(selected.Info.OriginY, -90, 69);
        Assert.True(selected.Info.OriginX + selected.Info.Width <= 940); Assert.True(selected.Info.OriginY + selected.Info.Height <= 70);
        Assert.Contains((byte)200, selected.CopyPixels());
        Assert.Equal(240, full.Info.Width); Assert.Equal(160, full.Info.Height);
        Assert.Equal(700, full.Info.OriginX); Assert.Equal(-90, full.Info.OriginY);
        Assert.All(full.CopyPixels(), value => Assert.Equal((byte)200, value));
        if (shape == "rectangle") { Assert.Equal(20, selected.Info.Width); Assert.Equal(20, selected.Info.Height); Assert.Equal(920, selected.Info.OriginX); Assert.Equal(50, selected.Info.OriginY); }
    }

    [Theory]
    [InlineData("rectangle")] [InlineData("rotatedRectangle")] [InlineData("circle")]
    [InlineData("polygon")] [InlineData("annulus")]
    public async Task EntirelyOutsideImageRoisAreUnavailableAndNeverFallBackToTheFullImage(string shape)
    {
        JsonObject roi = shape switch
        {
            "rectangle" => RoiRegion.Rectangle(500, 500, 30, 40).ToJson(),
            "rotatedRectangle" => RoiRegion.RotatedRectangle(500, 500, 30, 40, 30).ToJson(),
            "circle" => RoiRegion.Circle(500, 500, 30).ToJson(),
            "polygon" => RoiRegion.Polygon(new[] { new EditorPoint(500, 500), new(550, 500), new(550, 550) }).ToJson(),
            _ => RoiRegion.Annulus(500, 500, 10, 30).ToJson()
        };
        var definition = ImageOnly("Threshold", new() { ["roi"] = roi });
        definition["nodes"]!.AsArray().Add(Node("downstream", "ToGray"));
        definition["connections"]!.AsArray().Add(Edge("target", "downstream"));
        definition["outputs"]!["Downstream"] = Output("downstream");
        using var frame = Gray(240, 160, (_, _) => 230); using var result = await Run(definition, frame);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.False(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
        Assert.False(result.GetOutput<JsonElement>("Downstream").GetProperty("available").GetBoolean());
        Assert.Empty(result.GetOverlays("Downstream"));
    }

    [Theory]
    [InlineData("circle")] [InlineData("annulus")]
    public async Task AnIntersectingBoundingBoxDoesNotMakeAnEmptyShapeMaskAvailable(string shape)
    {
        var roi = shape == "circle" ? RoiRegion.Circle(30, 30, 20).ToJson() : RoiRegion.Annulus(8, 8, 15, 20).ToJson();
        using var frame = Gray(16, 16, (_, _) => 230);
        using var result = await Run(ImageOnly("Threshold", new() { ["roi"] = roi, ["invert"] = true }), frame);
        Assert.False(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
    }

    [Theory]
    [InlineData("CircleCaliper")] [InlineData("FindCircles")]
    public async Task PartiallyVisibleCircularBoundaryFitsOnlyActualSamplesAndPreservesTheOriginalCenter(string type)
    {
        var parameters = CircleParameters(350, 120, 50); if (type == "FindCircles") parameters["method"] = "caliper";
        using var frame = Gray(360, 240, (x, y) => Math.Pow(x - 350, 2) + Math.Pow(y - 120, 2) <= 2500 ? (byte)230 : (byte)20, 700, -90);
        using var result = await Run(Single(type, parameters), frame);
        var packet = result.GetResult<VisionCircle>("Result"); var circle = Assert.Single(packet.Features);
        Assert.True(packet.Measurement.DataAvailable); Assert.True(packet.Measurement.Caliper!.FitSucceeded);
        Assert.InRange(circle.CenterX, 1049, 1051); Assert.InRange(circle.CenterY, 29, 31); Assert.InRange(circle.Radius, 49, 51);
        Assert.InRange(circle.EdgePoints.Count, 26, 50);
        Assert.All(circle.EdgePoints, point => { Assert.InRange(point.X, 700, 1059); Assert.InRange(point.Y, -90, 149); });
    }

    [Fact]
    public async Task ALongPartiallyOutsideEdgePointStripKeepsTheVisibleWavyEdge()
    {
        const int width = 10918, height = 287;
        var p = new JsonObject { ["roi"] = RoiRegion.RotatedRectangle(width / 2.0, 10, width, 60, 0).ToJson(),
            ["sampleSpacing"] = 200.0, ["projectionWidth"] = 5, ["sigma"] = 1.0,
            ["edgeThreshold"] = 10.0, ["polarity"] = "darkToLight", ["minimumCoverage"] = .7 };
        using var frame = Gray(width, height, (x, y) => y >= 12 + 3 * Math.Sin(x / 700.0) ? (byte)230 : (byte)20, 700, -90);
        using var result = await Run(Single("FindEdgePoints", p), frame);
        var points = result.GetEdgePoints("Result"); Assert.True(points.DataAvailable); Assert.True(points.Valid);
        Assert.Equal(55, points.StationCount); Assert.InRange(points.Count, 53, 54);
        Assert.All(points.Points, point => Assert.InRange(Math.Abs(point.Y + 90 - (12 + 3 * Math.Sin((point.X - 700) / 700.0))), 0, 1.0));
    }

    [Theory]
    [InlineData("FindLines")] [InlineData("CircleCaliper")] [InlineData("FindEdgePoints")]
    public async Task AWhollyOutsideSearchPathProducesNoSyntheticGeometry(string type)
    {
        JsonObject p = type == "CircleCaliper" ? CircleParameters(2000, 2000, 50) :
            type == "FindLines" ? LineParameters(2000, 2000, 400, 60) :
            new() { ["roi"] = RoiRegion.RotatedRectangle(2000, 2000, 400, 60, 0).ToJson(), ["sampleSpacing"] = 10.0 };
        using var frame = Gray(360, 240, (_, y) => y >= 120 ? (byte)230 : (byte)20);
        using var result = await Run(Single(type, p), frame);
        if (type == "FindEdgePoints")
        {
            var points = result.GetEdgePoints("Result"); Assert.False(points.DataAvailable); Assert.False(points.Valid); Assert.Empty(points.Points);
        }
        else if (type == "CircleCaliper")
        {
            var packet = result.GetResult<VisionCircle>("Result"); Assert.False(packet.Measurement.DataAvailable); Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
        }
        else
        {
            var packet = result.GetResult<VisionLine>("Result"); Assert.False(packet.Measurement.DataAvailable); Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
        }
        Assert.Equal(InspectionDecision.Unknown, result.Decision); Assert.Empty(result.GetOverlays("Image"));
    }

    [Fact]
    public async Task AFullWidthLineOnACroppedUnwrapRetainsTheUnwrappedPlaneAndCropOrigin()
    {
        // The 40 source pixels are expanded to 287 radial rows. Permit that synthetic
        // image's amplified raster quantization; this case checks plane/origin transport.
        var lineParameters = LineParameters(5359, 123.5, 10718, 247);
        lineParameters["maximumDeviation"] = 6.0; lineParameters["edgeThreshold"] = 5.0;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", new()
            { ["roi"] = RoiRegion.Annulus(256, 256, 160, 200).ToJson(), ["outputWidth"] = 10918, ["outputHeight"] = 287 }),
            Node("crop", "CropRect", new() { ["x"] = 100, ["y"] = 20, ["width"] = 10718, ["height"] = 247 }),
            Node("target", "FindLines", lineParameters)),
            new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "crop"), Edge("crop", "target")),
            new JsonObject { ["Image"] = Output("target"), ["Result"] = Output("target", "result", "Result"), ["Unwrapped"] = Output("unwrap") });
        using var frame = Gray(513, 513, (x, y) => (byte)Math.Clamp(125 + 200 * (Math.Sqrt(Math.Pow(x - 256, 2) + Math.Pow(y - 256, 2)) - 180), 20, 230), 700, -90);
        using var result = await Run(definition, frame);
        var packet = result.GetResult<VisionLine>("Result"); var line = Assert.Single(packet.Features);
        Assert.Equal("annularUnwrapped", packet.CoordinateSpace); Assert.False(string.IsNullOrEmpty(packet.CoordinatePlane));
        Assert.Equal(result.GetOutput<JsonElement>("Unwrapped").GetProperty("coordinatePlane").GetString(), packet.CoordinatePlane);
        Assert.InRange(line.Y1, 139, 147); Assert.InRange(line.Y2, 139, 147); Assert.InRange(line.Length, 10400, 10720);
        using var image = result.GetOutputImage("Image"); Assert.Equal(100, image.Info.OriginX); Assert.Equal(20, image.Info.OriginY);
        Assert.Equal(10718, image.Info.Width); Assert.Equal(247, image.Info.Height);
        Assert.Equal("original", result.GetAnnularMapping("Image").SourceCoordinateSpace);
    }

    [Fact]
    public async Task ASubscribedCenterRemainsCorrectWhenItsCroppedReceiverClipsTheCircleRoi()
    {
        JsonObject Field(string name, double fallback) => new() { ["node"] = "detect", ["port"] = "result", ["field"] = "measurement.selected." + name, ["fallback"] = fallback };
        var roi = RoiRegion.Circle(130, 80, 70).ToJson(); roi["cx"] = Field("centerX", 130); roi["cy"] = Field("centerY", 80);
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("detect", "CircleCaliper", CircleParameters(330, 100, 50)),
                Node("crop", "CropRect", new() { ["x"] = 200, ["y"] = 20, ["width"] = 160, ["height"] = 200 }), Node("target", "ToGray", new() { ["roi"] = roi })),
            new JsonArray(Edge("source", "detect"), Edge("source", "crop"), Edge("crop", "target")),
            new JsonObject { ["Image"] = Output("target"), ["Circle"] = Output("detect", "result", "Result") });
        using var frame = Gray(360, 240, (x, y) => Math.Pow(x - 330, 2) + Math.Pow(y - 100, 2) <= 2500 ? (byte)230 : (byte)20, 700, -90);
        using var result = await Run(definition, frame); var circle = Assert.Single(result.GetResult<VisionCircle>("Circle").Features);
        var resolved = Assert.IsType<JsonElement>(result.GetResolvedParameters("target")).GetProperty("roi");
        Assert.Equal(circle.CenterX - 900, resolved.GetProperty("cx").GetDouble(), 8);
        Assert.Equal(circle.CenterY + 70, resolved.GetProperty("cy").GetDouble(), 8);
        using var image = result.GetOutputImage("Image"); Assert.InRange(image.Info.OriginX, 959, 961);
        Assert.Equal(1060, image.Info.OriginX + image.Info.Width); Assert.Contains((byte)230, image.CopyPixels());
        Assert.Equal("original", result.GetOutput<JsonElement>("Image").GetProperty("coordinateSpace").GetString());
    }

    [Theory]
    [InlineData("rectangle")] [InlineData("rotatedRectangle")]
    public async Task TheExisting32768PixelFrameDimensionStillAllowsATouchingFullWidthRoi(string shape)
    {
        var roi = shape == "rectangle" ? RoiRegion.Rectangle(0, 0, 32768, 16).ToJson() : RoiRegion.RotatedRectangle(16384, 8, 32768, 16, 0).ToJson();
        using var frame = Gray(32768, 16, (_, _) => 123);
        using var result = await Run(ImageOnly("ToGray", new() { ["roi"] = roi }), frame);
        using var image = result.GetOutputImage("Image"); Assert.Equal(32768, image.Info.Width); Assert.Equal(16, image.Info.Height);
        Assert.All(image.CopyPixels(), value => Assert.Equal((byte)123, value));
    }

    [Theory]
    [InlineData("CircleCaliper")] [InlineData("FindLines")] [InlineData("FindEdgePoints")]
    public async Task PartialSamplingRespectsAnUpstreamValidityMaskWithoutTurningItsBoundaryIntoAnEdge(string type)
    {
        var p = type == "CircleCaliper" ? CircleParameters(75, 75, 75) : type == "FindLines" ? LineParameters(75, 75, 180, 200) :
            new JsonObject { ["roi"] = RoiRegion.RotatedRectangle(75, 75, 180, 200, 0).ToJson(), ["sampleSpacing"] = 10.0 };
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("mask", "ToGray", new() { ["roi"] = RoiRegion.Circle(80, 80, 75).ToJson() }), Node("target", type, p)),
            new JsonArray(Edge("source", "mask"), Edge("mask", "target")),
            new JsonObject { ["Image"] = Output("target"), ["Result"] = Output("target", "result", "Result") });
        using var frame = Gray(160, 160, (_, _) => 230); using var result = await Run(definition, frame);
        if (type == "FindEdgePoints") Assert.Empty(result.GetEdgePoints("Result").Points);
        else if (type == "CircleCaliper") Assert.Empty(result.GetResult<VisionCircle>("Result").Features);
        else Assert.Empty(result.GetResult<VisionLine>("Result").Features);
        Assert.Empty(result.GetOverlays("Image"));
    }

    [Theory]
    [InlineData("FindContours")] [InlineData("FilterContoursByArea")] [InlineData("AnalyzeContours")]
    public async Task ContourInputAdaptersKeepAWhollyOutsideRoiUnavailable(string type)
    {
        var definition = ImageOnly(type, new() { ["roi"] = RoiRegion.Circle(500, 500, 30).ToJson() });
        if (type == "AnalyzeContours") definition["outputs"]!["Result"] = Output("target", "result", "Result");
        using var frame = Gray(240, 160, (_, _) => 230); using var result = await Run(definition, frame);
        Assert.False(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
        Assert.Empty(result.GetOverlays("Image"));
        if (type == "AnalyzeContours")
        {
            var packet = result.GetResult<VisionRegion>("Result"); Assert.False(packet.Measurement.DataAvailable); Assert.Empty(packet.Features);
        }
    }

    [Fact]
    public async Task TwoInlineMeasurementRoisMayCrossBothSidesOfTheImageWithoutChangingTheirMeasuredDistance()
    {
        var p = new JsonObject();
        foreach (string side in new[] { "first", "second" })
        {
            p[side + "Roi"] = RoiRegion.RotatedRectangle(180, side == "first" ? 80 : 160, 400, 40, 0).ToJson();
            p[side + "CaliperCount"] = 32; p[side + "ProjectionWidth"] = 5; p[side + "Sigma"] = 1.0;
            p[side + "Polarity"] = side == "first" ? "darkToLight" : "lightToDark";
            p[side + "MinimumCoverage"] = .7; p[side + "MaximumDeviation"] = 1.0;
        }
        using var frame = Gray(360, 240, (_, y) => y >= 80 && y < 160 ? (byte)230 : (byte)20, 700, -90);
        using var result = await Run(Single("MeasureLineDistance", p), frame);
        var measurement = result.GetGeometricMeasurement("Result"); Assert.True(measurement.Valid);
        Assert.InRange(measurement.Value!.Value, 79, 81); Assert.Equal("original", measurement.CoordinateSpace);
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class AnnularUnwrapTests : IDisposable
{
    private readonly string store;

    public AnnularUnwrapTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/annular-unwrap-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string to, string port = "image", string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonArray nodes, JsonArray edges, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "annular-unwrap", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = nodes, ["connections"] = edges, ["outputs"] = outputs
    };
    private static JsonObject Parameters(double inner = 10, double outer = 20, int width = 8, int height = 3,
        double start = 0, double end = 360) => new()
    {
        ["roi"] = RoiRegion.Annulus(32, 32, inner, outer, start, end).ToJson(),
        ["outputWidth"] = width, ["outputHeight"] = height, ["interpolation"] = "nearest"
    };
    private static JsonObject Single(JsonObject parameters) => Recipe(
        new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", parameters)),
        new JsonArray(Edge("source", "unwrap")), new JsonObject { ["Image"] = Output("unwrap") });
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
    private static byte[] Pixels(VisionFrame image)
    {
        int row = image.Info.Width * (image.Info.PixelFormat == VisionPixelFormat.Bgr24 ? 3 : 1);
        var raw = image.CopyPixels();
        var compact = new byte[row * image.Info.Height];
        for (int y = 0; y < image.Info.Height; y++) Array.Copy(raw, y * image.Info.Stride, compact, y * row, row);
        return compact;
    }
    private static VisionFrame Gray(Func<int, int, byte> pixel, int x = 0, int y = 0)
    {
        var pixels = new byte[65 * 65];
        for (int row = 0; row < 65; row++) for (int column = 0; column < 65; column++) pixels[row * 65 + column] = pixel(column, row);
        return VisionFrame.CopyFrom(pixels, 65, 65, 65, VisionPixelFormat.Gray8, x, y);
    }

    [Fact]
    public async Task ClockwiseColumnsSampleRightBottomLeftAndTopInOrder()
    {
        using var frame = Gray((x, y) => (byte)(x > 32 && Math.Abs(y - 32) < x - 32 ? 40 :
            y > 32 && Math.Abs(x - 32) < y - 32 ? 90 : x < 32 && Math.Abs(y - 32) < 32 - x ? 150 : 210));
        using var result = await Run(Single(Parameters(width: 4)), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(4, image.Info.Width); Assert.Equal(3, image.Info.Height);
        Assert.Equal(new byte[] { 40, 90, 150, 210 }, Pixels(image).Take(4));
        var mapping = result.GetAnnularMapping("Image");
        AssertPoint(42, 32, mapping.MapToSource(0, 0));
        AssertPoint(32, 52, mapping.MapToSource(1, 2));
        AssertPoint(12, 32, mapping.MapToSource(2, 2));
        AssertPoint(32, 12, mapping.MapToSource(3, 2));
    }

    [Fact]
    public async Task CounterclockwiseReversesAngularTravelWithoutDuplicatingTheFullCircleSeam()
    {
        using var frame = Gray((x, y) => (byte)(x > 32 && Math.Abs(y - 32) < x - 32 ? 40 :
            y > 32 && Math.Abs(x - 32) < y - 32 ? 90 : x < 32 && Math.Abs(y - 32) < 32 - x ? 150 : 210));
        var parameters = Parameters(width: 4); parameters["direction"] = "counterclockwise";
        using var result = await Run(Single(parameters), frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(new byte[] { 40, 210, 150, 90 }, Pixels(image).Take(4));
    }

    [Theory]
    [InlineData("clockwise", 30.0, 15.0)]
    [InlineData("counterclockwise", 150.0, -15.0)]
    public async Task SectorDirectionKeepsSamplingTheSelectedArc(string direction, double origin, double step)
    {
        using var frame = Gray((x, y) => (byte)(2 * x + y));
        var parameters = Parameters(width: 8, height: 1, start: 30, end: 150); parameters["direction"] = direction;
        using var result = await Run(Single(parameters), frame); using var image = result.GetOutputImage("Image");
        var actual = Pixels(image);
        for (int x = 0; x < 8; x++)
        {
            double theta = (origin + step * x) * Math.PI / 180;
            double expected = 2 * (32 + 15 * Math.Cos(theta)) + 32 + 15 * Math.Sin(theta);
            Assert.InRange(Math.Abs(actual[x] - expected), 0, 2);
        }
        var mapping = result.GetOutput<JsonElement>("Image").GetProperty("coordinateMapping");
        Assert.Equal(origin, mapping.GetProperty("angularOriginDegrees").GetDouble(), 8);
        Assert.Equal(step, mapping.GetProperty("angleStepDegrees").GetDouble(), 8);
        var typedMapping = result.GetAnnularMapping("Image");
        Assert.Equal(origin, typedMapping.AngularOriginDegrees); Assert.Equal(step, typedMapping.AngleStepDegrees);
        foreach (int column in new[] { 0, 3, 7 })
        {
            double theta = (origin + step * column) * Math.PI / 180;
            AssertPoint(32 + 15 * Math.Cos(theta), 32 + 15 * Math.Sin(theta), typedMapping.MapToSource(column, 0));
        }
    }

    [Theory]
    [InlineData("innerToOuter", 80, 120, 160)]
    [InlineData("outerToInner", 160, 120, 80)]
    public async Task RadialOrderControlsTheActualPixelRows(string order, byte first, byte middle, byte last)
    {
        using var frame = Gray((x, y) => (byte)Math.Clamp(Math.Round(Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)) * 8), 0, 255));
        var source = Pixels(frame);
        var parameters = Parameters(width: 4); parameters["radialOrder"] = order;
        using var result = await Run(Single(parameters), frame); using var image = result.GetOutputImage("Image");
        var actual = Pixels(image);
        Assert.Equal(first, actual[0]); Assert.Equal(middle, actual[4]); Assert.Equal(last, actual[8]);
        Assert.Equal(source, Pixels(frame));
    }

    [Theory]
    [InlineData(0, 360, 95, 10)]
    [InlineData(30, 150, 32, 10)]
    public async Task AutomaticDimensionsFollowMeanArcLengthAndRadialThickness(double start, double end, int width, int height)
    {
        using var frame = Gray((x, y) => 170);
        using var result = await Run(Single(Parameters(width: 0, height: 0, start: start, end: end)), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(width, image.Info.Width); Assert.Equal(height, image.Info.Height);
        Assert.All(Pixels(image), p => Assert.Equal(170, p));
    }

    [Fact]
    public async Task OneOutputRowUsesTheMeanRadiusRatherThanDividingByZero()
    {
        using var frame = Gray((x, y) => (byte)x);
        using var result = await Run(Single(Parameters(width: 4, height: 1)), frame);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(new byte[] { 47, 32, 17, 32 }, Pixels(image));
        var mapping = result.GetOutput<JsonElement>("Image").GetProperty("coordinateMapping");
        Assert.Equal(15, mapping.GetProperty("radialOrigin").GetDouble());
        Assert.Equal(0, mapping.GetProperty("radialStep").GetDouble());
    }

    [Fact]
    public async Task ColorChannelsAndTheCallerOwnedInputStayUnchanged()
    {
        var pixels = new byte[65 * 65 * 3];
        for (int y = 0; y < 65; y++) for (int x = 0; x < 65; x++)
        {
            int i = (y * 65 + x) * 3; pixels[i] = (byte)x; pixels[i + 1] = (byte)y; pixels[i + 2] = (byte)(x + y);
        }
        using var frame = VisionFrame.CopyFrom(pixels, 65, 65, 195, VisionPixelFormat.Bgr24);
        using var result = await Run(Single(Parameters(width: 4, height: 1)), frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(VisionPixelFormat.Bgr24, image.Info.PixelFormat);
        Assert.Equal(new byte[] { 47, 32, 79, 32, 47, 79, 17, 32, 49, 32, 17, 49 }, Pixels(image));
        Assert.Equal(pixels, Pixels(frame));
    }

    [Fact]
    public async Task LinearInterpolationUsesNeighborPixelsInsteadOfRoundingToOnePixel()
    {
        using var frame = Gray((x, y) => (byte)((x & 1) * 200));
        var nearest = Parameters(inner: 10.25, outer: 20.25, width: 4, height: 1);
        var linear = (JsonObject)nearest.DeepClone(); linear["interpolation"] = "linear";
        using var a = await Run(Single(nearest), frame); using var b = await Run(Single(linear), frame);
        using var ai = a.GetOutputImage("Image"); using var bi = b.GetOutputImage("Image");
        Assert.Equal(200, Pixels(ai)[0]); Assert.Equal(150, Pixels(bi)[0]);
    }

    [Fact]
    public async Task MappingKeepsOriginalCenterWhileTheOutputStartsANewCoordinatePlane()
    {
        using var frame = Gray((x, y) => (byte)x, 900, -80);
        using var result = await Run(Single(Parameters(width: 8, height: 3)), frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(0, image.Info.OriginX); Assert.Equal(0, image.Info.OriginY);
        var metadata = result.GetOutput<JsonElement>("Image");
        Assert.Equal("annularUnwrapped", metadata.GetProperty("coordinateSpace").GetString());
        Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("coordinatePlane").GetString()));
        var mapping = metadata.GetProperty("coordinateMapping");
        Assert.Equal(932, mapping.GetProperty("centerX").GetDouble()); Assert.Equal(-48, mapping.GetProperty("centerY").GetDouble());
        Assert.Equal(0, mapping.GetProperty("angularOriginDegrees").GetDouble());
        Assert.Equal(45, mapping.GetProperty("angleStepDegrees").GetDouble());
        Assert.Equal(10, mapping.GetProperty("radialOrigin").GetDouble()); Assert.Equal(5, mapping.GetProperty("radialStep").GetDouble());
        var typedMapping = result.GetAnnularMapping("Image");
        Assert.Equal("original", typedMapping.SourceCoordinateSpace);
        AssertPoint(942, -48, typedMapping.MapToSource(0, 0));
        AssertPoint(932, -28, typedMapping.MapToSource(2, 2));
        AssertPoint(912, -48, typedMapping.MapToSource(4, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => typedMapping.MapToSource(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => typedMapping.MapToSource(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => typedMapping.MapToSource(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => typedMapping.MapToSource(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => typedMapping.MapToSource(0, double.PositiveInfinity));
    }

    [Fact]
    public async Task DownstreamCropKeepsMappingInPlaneCoordinatesIncludingTheCropOrigin()
    {
        using var frame = Gray((x, y) => (byte)x, 900, -80);
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", Parameters(width: 64, height: 11)),
                Node("crop", "CropRect", new() { ["x"] = 8, ["y"] = 2, ["width"] = 16, ["height"] = 5 }), Node("gray", "ToGray")),
            new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "crop"), Edge("crop", "gray")),
            new JsonObject { ["Image"] = Output("gray"), ["Unwrapped"] = Output("unwrap") });
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(16, image.Info.Width); Assert.Equal(5, image.Info.Height);
        Assert.Equal(8, image.Info.OriginX); Assert.Equal(2, image.Info.OriginY);
        var mapping = result.GetAnnularMapping("Image"); var fullMapping = result.GetAnnularMapping("Unwrapped");
        Assert.Equal(64, mapping.Width); Assert.Equal(11, mapping.Height);
        Assert.Equal(fullMapping.MapToSource(8, 2), mapping.MapToSource(image.Info.OriginX, image.Info.OriginY));
        AssertPoint(932, -34, mapping.MapToSource(16, 4));
        Assert.Equal(40, Pixels(image)[0]);
        var fullMetadata = result.GetOutput<JsonElement>("Unwrapped"); var croppedMetadata = result.GetOutput<JsonElement>("Image");
        Assert.Equal(fullMetadata.GetProperty("coordinatePlane").GetString(), croppedMetadata.GetProperty("coordinatePlane").GetString());
        Assert.Equal("annularUnwrapped", croppedMetadata.GetProperty("coordinateSpace").GetString());
    }

    private static void AssertPoint(double x, double y, VisionPoint actual)
    {
        Assert.Equal(x, actual.X, 8); Assert.Equal(y, actual.Y, 8);
    }

    [Theory]
    [InlineData("width")] [InlineData("height")] [InlineData("direction")]
    [InlineData("interpolation")] [InlineData("shape")] [InlineData("radii")] [InlineData("arc")]
    public void InvalidSettingsAreRejectedAsRecipeErrors(string kind)
    {
        var parameters = Parameters();
        switch (kind)
        {
            case "width": parameters["outputWidth"] = -1; break;
            case "height": parameters["outputHeight"] = 100000; break;
            case "direction": parameters["direction"] = "diagonal"; break;
            case "interpolation": parameters["interpolation"] = "unknown"; break;
            case "shape": parameters["roi"] = RoiRegion.Circle(32, 32, 20).ToJson(); break;
            case "radii": parameters["roi"]!["innerRadius"] = 20; break;
            case "arc": parameters["roi"]!["endAngle"] = 5; break;
        }
        using var engine = new VisionEngine(store);
        if (kind is "width" or "height")
        {
            var boundsError = Assert.Throws<ArgumentException>(() => engine.LoadRecipe(Save(Single(parameters))));
            Assert.Contains(kind == "width" ? "outputWidth" : "outputHeight", boundsError.Message);
            Assert.Contains("out of range", boundsError.Message);
            return;
        }
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Single(parameters))));
        Assert.Equal(2, error.Code); Assert.DoesNotContain("Unknown node type", error.Message);
    }

    [Fact]
    public async Task OutOfBoundsSamplesStayInvalidThroughAnInvertedThreshold()
    {
        using var frame = Gray((x, y) => 20);
        var parameters = Parameters(width: 4); parameters["roi"]!["cx"] = 5;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", parameters),
                Node("binary", "Threshold", new() { ["invert"] = true })),
            new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "binary")),
            new JsonObject { ["Image"] = Output("binary"), ["Unwrapped"] = Output("unwrap") });
        using var result = await Run(definition, frame);
        using var raw = result.GetOutputImage("Unwrapped"); using var binary = result.GetOutputImage("Image");
        var before = Pixels(raw); var after = Pixels(binary);
        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(20, before[row * 4]); Assert.Equal(255, after[row * 4]);
            Assert.Equal(0, before[row * 4 + 2]); Assert.Equal(0, after[row * 4 + 2]);
        }
    }

    [Fact]
    public async Task SamplingRespectsAnUpstreamPolygonValidityMask()
    {
        using var frame = Gray((x, y) => 230);
        var triangle = RoiRegion.Polygon(new[] { new EditorPoint(0, 0), new EditorPoint(64, 0), new EditorPoint(0, 64) });
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"),
                Node("triangle", "Threshold", new() { ["roi"] = triangle.ToJson() }), Node("unwrap", "AnnularUnwrap", Parameters(width: 4))),
            new JsonArray(Edge("source", "triangle"), Edge("triangle", "unwrap")),
            new JsonObject { ["Image"] = Output("unwrap") });
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixels(image).Take(4));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixels(image).Skip(4).Take(4));
    }

    [Fact]
    public async Task AnEntirelyUnavailableRingDoesNotFallBackToTheOriginalImage()
    {
        using var frame = Gray((x, y) => 230);
        var parameters = Parameters(width: 4); parameters["roi"]!["cx"] = 1000;
        using var result = await Run(Single(parameters), frame);
        var metadata = result.GetOutput<JsonElement>("Image");
        Assert.Equal(4, metadata.GetProperty("width").GetInt32()); Assert.Equal(3, metadata.GetProperty("height").GetInt32());
        Assert.False(metadata.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("unavailableReason").GetString()));
        var error = Assert.Throws<ArgumentException>(() => result.GetOutputImage("Image"));
        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedOutputFailsBeforeAllocatingItsSamplingMaps()
    {
        using var frame = Gray((x, y) => 230);
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(Single(Parameters(width: 32766, height: 32766))));
        await using var session = recipe.CreateSession(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("unwrap", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("budget", result.Data.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(InspectionDecision.Unknown, result.Decision);
    }

    [Fact]
    public async Task UnwrappingClearsPreviouslyDetectedGeometryAndItsGraphics()
    {
        using var frame = Gray((x, y) => Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32)) < 15 ? (byte)230 : (byte)20);
        var circle = new JsonObject
        {
            ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = RoiRegion.Circle(32, 32, 15).ToJson(), ["caliperCount"] = 48, ["projectionWidth"] = 3,
            ["searchLength"] = 10.0, ["sigma"] = .5, ["polarity"] = "lightToDark", ["maximumDeviation"] = 1.0,
            ["minimumCoverage"] = .5, ["outputAnnulusWidth"] = 0.0
        };
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("circle", "FindCircles", circle),
                Node("unwrap", "AnnularUnwrap", Parameters()), Node("gray", "ToGray")),
            new JsonArray(Edge("source", "circle"), Edge("circle", "unwrap"), Edge("unwrap", "gray")),
            new JsonObject { ["Image"] = Output("gray"), ["Circle"] = Output("circle", "result", "Result") });
        using var result = await Run(definition, frame);
        Assert.Single(result.GetResult<VisionCircle>("Circle").Features);
        var pipeline = result.GetImagePipeline("Image");
        var results = pipeline.GetProperty("results");
        Assert.False(results.TryGetProperty("Features", out var features) && features.GetArrayLength() > 0);
        Assert.False(results.TryGetProperty("Pose", out _)); Assert.Empty(result.GetOverlays("Image"));
        Assert.Equal("annularUnwrapped", result.GetOutput<JsonElement>("Image").GetProperty("coordinateSpace").GetString());
    }

    [Fact]
    public async Task DownstreamThresholdAndRegionAnalysisUseTheUnwrappedCoordinateSpace()
    {
        using var frame = Gray((x, y) =>
        {
            double r = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
            double angle = Math.Atan2(y - 32, x - 32) * 180 / Math.PI;
            return r >= 12 && r <= 18 && angle >= 70 && angle <= 130 ? (byte)230 : (byte)20;
        }, 900, -80);
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", Parameters(width: 180, height: 21)),
                Node("binary", "Threshold"), Node("regions", "AnalyzeContours")),
            new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "binary"), Edge("binary", "regions")),
            new JsonObject { ["Image"] = Output("regions"), ["Regions"] = Output("regions", "result", "Result") });
        using var result = await Run(definition, frame); using var image = result.GetOutputImage("Image");
        Assert.Equal(180, image.Info.Width); Assert.Equal(21, image.Info.Height);
        var region = Assert.Single(result.GetRegions("Image"));
        Assert.InRange(region.CentroidX, 40, 60); Assert.InRange(region.CentroidY, 7, 13);
        Assert.Equal("annularUnwrapped", result.GetMeasurement<VisionRegion>("Image").CoordinateSpace);
        var packet = result.GetResult<VisionRegion>("Regions");
        Assert.Equal("annularUnwrapped", packet.Measurement.CoordinateSpace);
        Assert.Equal("annularUnwrapped", packet.CoordinateSpace);
        Assert.Equal(result.GetOutput<JsonElement>("Image").GetProperty("coordinatePlane").GetString(), packet.CoordinatePlane);
        Assert.Equal("annularUnwrapped", result.GetOutput<JsonElement>("Image").GetProperty("coordinateSpace").GetString());
        Assert.All(result.GetOverlays("Image"), graphic => Assert.Equal("#00FF00", graphic.Color));
    }

    [Fact]
    public async Task AFittedCircleAndItsNestedAnnulusReportTheUnwrappedPlane()
    {
        using var frame = Gray((x, y) =>
        {
            double radius = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
            double angle = Math.Atan2(y - 32, x - 32) * 180 / Math.PI;
            if (angle < 0) angle += 360;
            double u = angle * 128 / 360, v = (radius - 10) * 2;
            return Math.Sqrt((u - 64) * (u - 64) + (v - 20) * (v - 20)) <= 8 ? (byte)230 : (byte)20;
        });
        var circle = new JsonObject
        {
            ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = RoiRegion.Circle(64, 20, 8).ToJson(), ["caliperCount"] = 48, ["projectionWidth"] = 3,
            ["searchLength"] = 8.0, ["sigma"] = .3, ["polarity"] = "lightToDark", ["maximumDeviation"] = 2.5,
            ["minimumCoverage"] = .5, ["outputAnnulusWidth"] = 2.0
        };
        var parameters = Parameters(inner: 10, outer: 30, width: 128, height: 41); parameters["interpolation"] = "linear";
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrap", "AnnularUnwrap", parameters), Node("circle", "FindCircles", circle)),
            new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "circle")),
            new JsonObject { ["Image"] = Output("circle"), ["Circle"] = Output("circle", "result", "Result") });
        using var result = await Run(definition, frame);
        var fitted = Assert.Single(result.GetCircles("Image"));
        Assert.InRange(fitted.CenterX, 62, 66); Assert.InRange(fitted.CenterY, 18, 22); Assert.InRange(fitted.Radius, 6, 10);
        Assert.Equal("annularUnwrapped", fitted.CoordinateSpace);
        Assert.Equal("annularUnwrapped", fitted.Circle.CoordinateSpace);
        Assert.NotNull(fitted.OutputAnnulus); Assert.Equal("annularUnwrapped", fitted.OutputAnnulus.CoordinateSpace);
        var packet = result.GetResult<VisionCircle>("Circle");
        Assert.Equal("annularUnwrapped", packet.CoordinateSpace); Assert.Equal("annularUnwrapped", packet.Measurement.CoordinateSpace);
        Assert.Equal("annularUnwrapped", packet.Measurement.Selected!.Circle.CoordinateSpace);
        Assert.Equal("annularUnwrapped", packet.Measurement.Selected.OutputAnnulus!.CoordinateSpace);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LegacyLineMeasurementAcceptsOneUnwrappedPlaneAndRejectsTwoDifferentPlanes(bool separatePlanes)
    {
        using var frame = Gray((x, y) =>
        {
            double radius = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
            return radius >= 12 && radius <= 18 ? (byte)230 : (byte)20;
        });
        JsonObject Line(double y, string polarity) => new()
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiCenter", ["roi"] = RoiRegion.RotatedRectangle(90, y, 120, 10, 0).ToJson(),
            ["caliperCount"] = 32, ["projectionWidth"] = 3, ["polarity"] = polarity, ["searchLength"] = 10.0, ["sigma"] = .5,
            ["maximumDeviation"] = 4.0, ["minimumCoverage"] = .5
        };
        var nodes = new JsonArray(Node("source", "ExternalImage"), Node("unwrapA", "AnnularUnwrap", Parameters(width: 180, height: 41)),
            Node("a", "FindLines", Line(8, "darkToLight")), Node("b", "FindLines", Line(32, "lightToDark")),
            Node("measure", "MeasureLineDistance", new() { ["parallelAngleToleranceDegrees"] = 5.0 }));
        var edges = new JsonArray(Edge("source", "unwrapA"), Edge("unwrapA", "a"),
            Edge(separatePlanes ? "unwrapB" : "unwrapA", "b"), Edge("a", "measure", "features", "first"), Edge("b", "measure", "features", "second"));
        if (separatePlanes)
        {
            nodes.Add(Node("unwrapB", "AnnularUnwrap", Parameters(width: 180, height: 41)));
            edges.Add(Edge("source", "unwrapB"));
        }
        var definition = Recipe(nodes, edges, new JsonObject { ["Image"] = Output("measure"), ["Measurement"] = Output("measure", "measurement", "Measurement") });
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var result = await session.RunAsync(frame);
        if (separatePlanes)
        {
            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.Equal("measure", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
            Assert.Contains("coordinate planes", result.Data.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
            var measurement = result.GetGeometricMeasurement("Image");
            Assert.True(measurement.Valid); Assert.Equal("annularUnwrapped", measurement.CoordinateSpace);
            Assert.InRange(measurement.Value!.Value, 20, 28);
            Assert.Equal("annularUnwrapped", result.GetOutput<JsonElement>("Image").GetProperty("coordinateSpace").GetString());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(store)) Directory.Delete(store, true);
    }
}

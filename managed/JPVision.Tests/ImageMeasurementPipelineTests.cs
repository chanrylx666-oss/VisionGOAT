using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ImageMeasurementPipelineTests : IDisposable
{
    private const int Width = 480, Height = 340;
    private readonly string store;

    public ImageMeasurementPipelineTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts/tests/image-measurement-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };

    private static JsonObject Edge(string from, string port, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };

    private static JsonObject Output(string port, string type) => new()
    { ["node"] = "measure", ["port"] = port, ["type"] = type };

    private static JsonObject Definition(string type, JsonObject? parameters = null) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "image-measurement",
        ["recipeVersion"] = 1, ["kind"] = "inspection", ["decisionOutput"] = "Decision", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("measure", type, parameters)),
        ["connections"] = new JsonArray(Edge("source", "image", "measure")),
        ["outputs"] = new JsonObject
        {
            ["Image"] = Output("image", "Image"), ["Distance"] = Output("result", "Result"),
            ["Decision"] = Output("decision", "Decision"), ["Overlay"] = Output("overlay", "Overlay")
        }
    };

    private static JsonObject Parameters(JsonObject recipe) => recipe["nodes"]!.AsArray()
        .Single(node => node!["id"]!.GetValue<string>() == "measure")!["parameters"]!.AsObject();

    private static void Sampling(JsonObject p, string side, JsonObject roi, bool circle)
    {
        p[side + "Roi"] = roi;
        p[side + "CaliperCount"] = circle ? 64 : 32;
        p[side + "ProjectionWidth"] = 5;
        p[side + "SearchLength"] = 18.0;
        p[side + "Sigma"] = 1.0;
        p[side + "EdgeThreshold"] = 12.0;
        p[side + "Polarity"] = "any";
        p[side + "RemovePointCount"] = circle ? 2 : 0;
        p[side + "MaximumDeviation"] = 1.0;
        p[side + "MinimumCoverage"] = .75;
    }

    private static JsonObject CircleParameters(string? mode = null)
    {
        var p = new JsonObject();
        Sampling(p, "first", RoiRegion.Circle(150, 170, 32).ToJson(), true);
        Sampling(p, "second", RoiRegion.Circle(330, 170, 22).ToJson(), true);
        if (mode is not null) p["distanceMode"] = mode;
        return p;
    }

    private static JsonObject LineCircleParameters(string? mode = null)
    {
        var p = new JsonObject();
        Sampling(p, "first", RoiRegion.RotatedRectangle(240, 60, 300, 24, 0).ToJson(), false);
        Sampling(p, "second", RoiRegion.Circle(150, 170, 32).ToJson(), true);
        if (mode is not null) p["distanceMode"] = mode;
        return p;
    }

    private static JsonObject LineParameters()
    {
        var p = new JsonObject();
        Sampling(p, "first", RoiRegion.RotatedRectangle(220, 130, 300, 24, 0).ToJson(), false);
        Sampling(p, "second", RoiRegion.RotatedRectangle(220, 210, 300, 24, 0).ToJson(), false);
        return p;
    }

    private static byte[] CirclePixels(bool omitSecond = false, double shift = 0, bool color = false)
    {
        int channels = color ? 3 : 1;
        var pixels = new byte[Width * Height * channels];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            double ax = x - 150 - shift, bx = x - 330 - shift, dy = y - 170;
            double a = 32 - Math.Sqrt(ax * ax + dy * dy);
            double b = omitSecond ? double.NegativeInfinity : 22 - Math.Sqrt(bx * bx + dy * dy);
            byte value = (byte)Math.Clamp(125 + Math.Max(60 - y, Math.Max(a, b)) * 180, 20, 230);
            for (int c = 0; c < channels; c++) pixels[(y * Width + x) * channels + c] = value;
        }
        return pixels;
    }

    private static VisionFrame Frame(byte[] pixels, bool color = false) => VisionFrame.CopyFrom(pixels,
        Width, Height, Width * (color ? 3 : 1), color ? VisionPixelFormat.Bgr24 : VisionPixelFormat.Gray8, 900, -80);

    private static VisionFrame LineFrame()
    {
        var pixels = new byte[Width * Height];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            pixels[y * Width + x] = (byte)Math.Clamp(125 + Math.Min(y - 130, 210 - y) * 180, 20, 230);
        return Frame(pixels);
    }

    private string Save(JsonObject recipe)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString()));
        return path;
    }

    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = false });
    }

    private static JsonElement Pipeline(InspectionResult result) => result.GetOutput<JsonElement>("Image").GetProperty("pipeline");

    private static void AssertContext(InspectionResult result, bool valid, int featureCount)
    {
        var pipeline = Pipeline(result); var results = pipeline.GetProperty("results");
        Assert.Equal("original", pipeline.GetProperty("coordinateSpace").GetString());
        Assert.Equal("measure", pipeline.GetProperty("lastNodeId").GetString());
        Assert.Equal(valid, results.GetProperty("Result").GetProperty("valid").GetBoolean());
        Assert.Equal(valid, results.GetProperty("Measurement").GetProperty("valid").GetBoolean());
        Assert.Equal(featureCount, results.GetProperty("Features").GetArrayLength());
        Assert.Equal(result.GetOutput<JsonElement>("Distance").GetProperty("value").GetRawText(),
            results.GetProperty("Measurement").GetProperty("value").GetRawText());
        Assert.All(pipeline.GetProperty("overlay").EnumerateArray(),
            overlay => Assert.Equal("#00FF00", overlay.GetProperty("color").GetString()));
    }

    [Theory]
    [InlineData(null, 180)]
    [InlineData("centerDistance", 180)]
    [InlineData("edgeDistance", 126)]
    public async Task ImageToCircleDistanceFitsBothRoisAndKeepsTheExistingSdkContract(string? mode, double expected)
    {
        using var frame = Frame(CirclePixels());
        using var result = await Run(Definition("MeasureCircleDistance", CircleParameters(mode)), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var measurement = result.GetGeometryMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.InRange(measurement.Value!.Value, expected - .5, expected + .5);
        Assert.InRange(measurement.First!.CenterX, 1049.8, 1050.2);
        Assert.InRange(measurement.First.CenterY, 89.8, 90.2);
        Assert.InRange(measurement.First.Radius, 31.7, 32.3);
        Assert.InRange(measurement.Second!.CenterX, 1229.8, 1230.2);
        Assert.InRange(measurement.Second.Radius, 21.7, 22.3);
        Assert.Equal(measurement.Value, result.GetGeometricMeasurement("Distance").Value);
        AssertContext(result, true, 2);
    }

    [Theory]
    [InlineData(null, 110)]
    [InlineData("centerDistance", 110)]
    [InlineData("edgeDistance", 78)]
    public async Task ImageToLineCircleDistanceMeasuresTheDetectedLineAndCircle(string? mode, double expected)
    {
        using var frame = Frame(CirclePixels());
        using var result = await Run(Definition("MeasureLineCircleDistance", LineCircleParameters(mode)), frame);
        var measurement = result.GetGeometricMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.InRange(measurement.Value!.Value, expected - .4, expected + .4);
        Assert.Equal("line", measurement.First!.Kind); Assert.Equal("circle", measurement.Second!.Kind);
        Assert.InRange(measurement.First.Line!.Y1, -20.2, -19.8);
        AssertContext(result, true, 2);
    }

    [Theory]
    [InlineData("MeasureLineDistance")]
    [InlineData("MeasureGeometry")]
    public async Task ImageToParallelLineDistanceUsesTwoIndependentCaliperStrips(string type)
    {
        var p = LineParameters(); if (type == "MeasureGeometry") p["operation"] = "lineDistance";
        p["toleranceEnabled"] = true; p["lowerLimit"] = 79.5; p["upperLimit"] = 80.5;
        using var frame = LineFrame(); using var result = await Run(Definition(type, p), frame);
        var measurement = result.GetGeometricMeasurement("Distance");
        Assert.Equal(InspectionDecision.Pass, result.Decision); Assert.True(measurement.Valid);
        Assert.InRange(measurement.Value!.Value, 79.7, 80.3);
        Assert.InRange(measurement.First!.Line!.Y1, 49.8, 50.2);
        Assert.InRange(measurement.Second!.Line!.Y1, 129.8, 130.2);
        AssertContext(result, true, 2);
    }

    [Fact]
    public async Task GenericPointDistanceUsesDetectedCircleCenters()
    {
        using var frame = Frame(CirclePixels());
        using var result = await Run(Definition("MeasureGeometry", CircleParameters()), frame);
        var measurement = result.GetGeometricMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.Equal("pointDistance", measurement.Operation);
        Assert.InRange(measurement.Value!.Value, 179.8, 180.2);
        Assert.Equal("point", measurement.First!.Kind); Assert.Equal("point", measurement.Second!.Kind);
        AssertContext(result, true, 2);
    }

    [Fact]
    public async Task GenericPointToLineDetectsThePointSourceAndTheRequiredLine()
    {
        var p = new JsonObject { ["operation"] = "pointToLine" };
        Sampling(p, "first", RoiRegion.Circle(150, 170, 32).ToJson(), true);
        Sampling(p, "second", RoiRegion.RotatedRectangle(240, 60, 300, 24, 0).ToJson(), false);
        using var frame = Frame(CirclePixels()); using var result = await Run(Definition("MeasureGeometry", p), frame);
        var measurement = result.GetGeometricMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.InRange(measurement.Value!.Value, 109.8, 110.2);
        Assert.Equal("point", measurement.First!.Kind); Assert.Equal("line", measurement.Second!.Kind);
        AssertContext(result, true, 2);
    }

    [Theory]
    [InlineData("MeasureCircleDistance")]
    [InlineData("MeasureLineCircleDistance")]
    [InlineData("MeasureLineDistance")]
    [InlineData("MeasureGeometry")]
    public async Task UnconfiguredRoisReturnUnknownWithoutInventingMeasurementObjects(string type)
    {
        using var frame = Frame(CirclePixels()); using var result = await Run(Definition(type), frame);
        var measurement = result.GetGeometricMeasurement("Distance");
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.False(measurement.Valid); Assert.Null(measurement.Value);
        Assert.Empty(result.GetOverlays("Overlay")); AssertContext(result, false, 0);
        using var image = result.GetOutputImage("Image"); Assert.Equal(frame.CopyPixels(), image.CopyPixels());
    }

    [Fact]
    public async Task OneRoiWithoutEdgesCannotUseItsDrawnCenterAsAMeasurement()
    {
        using var frame = Frame(CirclePixels(omitSecond: true));
        using var result = await Run(Definition("MeasureCircleDistance", CircleParameters()), frame);
        Assert.False(result.GetGeometryMeasurement("Distance").Valid);
        Assert.Equal(InspectionDecision.Unknown, result.Decision); AssertContext(result, false, 0);
        Assert.Empty(result.GetOverlays("Overlay"));
    }

    [Fact]
    public async Task LaterFramesMustRefitCurrentPixelsAndCannotReuseEarlierObjects()
    {
        var definition = Definition("MeasureCircleDistance", CircleParameters());
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        using var goodFrame = Frame(CirclePixels()); using var good = await session.RunAsync(goodFrame);
        Assert.True(good.GetGeometryMeasurement("Distance").Valid);
        long firstFrameId = Pipeline(good).GetProperty("frameId").GetInt64();
        using var shiftedFrame = Frame(CirclePixels(shift: 70)); using var shifted = await session.RunAsync(shiftedFrame);
        Assert.False(shifted.GetGeometryMeasurement("Distance").Valid); AssertContext(shifted, false, 0);
        Assert.NotEqual(firstFrameId, Pipeline(shifted).GetProperty("frameId").GetInt64());
        using var restored = await session.RunAsync(goodFrame);
        Assert.True(restored.GetGeometryMeasurement("Distance").Valid); AssertContext(restored, true, 2);
        Assert.NotEqual(firstFrameId, Pipeline(restored).GetProperty("frameId").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputImageKeepsCleanPixelsWhileTheIndependentGreenOverlayCarriesTheResults(bool color)
    {
        var pixels = CirclePixels(color: color); using var frame = Frame(pixels, color);
        using var result = await Run(Definition("MeasureCircleDistance", CircleParameters()), frame);
        Assert.True(result.GetGeometryMeasurement("Distance").Valid);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(900, image.Info.OriginX); Assert.Equal(-80, image.Info.OriginY);
        Assert.Equal(Width, image.Info.Width); Assert.Equal(Height, image.Info.Height);
        Assert.Equal(color ? VisionPixelFormat.Bgr24 : VisionPixelFormat.Gray8, image.Info.PixelFormat);
        Assert.Equal(pixels, image.CopyPixels()); Assert.Equal(pixels, frame.CopyPixels());
        Assert.NotEmpty(Pipeline(result).GetProperty("overlay").EnumerateArray()); AssertContext(result, true, 2);
    }

    [Fact]
    public async Task InputCropRoisStayLocalButMeasuredCoordinatesRemainInTheOriginalFrame()
    {
        var definition = Definition("MeasureCircleDistance", CircleParameters());
        definition["nodes"]!.AsArray().Add(Node("crop", "CropRect", new()
        { ["x"] = 60, ["y"] = 20, ["width"] = 360, ["height"] = 260 }));
        var edges = definition["connections"]!.AsArray(); edges[0]!["fromNode"] = "crop";
        edges.Add(Edge("source", "image", "crop"));
        var p = Parameters(definition);
        p["firstRoi"] = RoiRegion.Circle(90, 150, 32).ToJson(); p["secondRoi"] = RoiRegion.Circle(270, 150, 22).ToJson();
        using var frame = Frame(CirclePixels()); using var result = await Run(definition, frame);
        var measurement = result.GetGeometryMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.InRange(measurement.First!.CenterX, 1049.8, 1050.2);
        Assert.InRange(measurement.Second!.CenterX, 1229.8, 1230.2);
        using var image = result.GetOutputImage("Image");
        Assert.Equal(960, image.Info.OriginX); Assert.Equal(-60, image.Info.OriginY);
        Assert.Equal(360, image.Info.Width); Assert.Equal(260, image.Info.Height); AssertContext(result, true, 2);
    }

    private static JsonObject LegacyDefinition()
    {
        var definition = Definition("MeasureCircleDistance"); var nodes = definition["nodes"]!.AsArray();
        JsonObject Circle(double x, double radius) => new()
        {
            ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = RoiRegion.Circle(x, 170, radius).ToJson(), ["caliperCount"] = 64,
            ["projectionWidth"] = 5, ["searchLength"] = 18.0, ["maximumDeviation"] = 1.0
        };
        nodes.Add(Node("a", "FindCircles", Circle(150, 32))); nodes.Add(Node("b", "FindCircles", Circle(330, 22)));
        definition["connections"] = new JsonArray(Edge("source", "image", "a"), Edge("source", "image", "b"),
            Edge("a", "features", "measure", "first"), Edge("b", "features", "measure", "second"));
        return definition;
    }

    [Fact]
    public async Task ExistingTypedTwoInputRecipesRemainExecutableWithTheirOriginalMeasurementGetter()
    {
        using var frame = Frame(CirclePixels()); using var result = await Run(LegacyDefinition(), frame);
        var measurement = result.GetGeometryMeasurement("Distance");
        Assert.True(measurement.Valid); Assert.InRange(measurement.Value!.Value, 179.8, 180.2);
        AssertContext(result, true, 2);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("both")]
    public void ImageAndLegacyObjectInputsCannotBeMixed(string selected)
    {
        var definition = LegacyDefinition(); var edges = definition["connections"]!.AsArray();
        if (selected != "both")
            for (int i = edges.Count - 1; i >= 0; i--)
                if (edges[i]!["toNode"]!.GetValue<string>() == "measure" &&
                    edges[i]!["toPort"]!.GetValue<string>() != selected) edges.RemoveAt(i);
        edges.Add(Edge("source", "image", "measure"));
        using var engine = new VisionEngine(store);
        Assert.Throws<VisionException>(() => engine.ValidateRecipe(VisionEngine.SealRecipe(definition.ToJsonString())));
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

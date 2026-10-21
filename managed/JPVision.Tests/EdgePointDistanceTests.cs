using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class EdgePointDistanceTests : IDisposable
{
    private readonly string store;
    public EdgePointDistanceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "edge-point-distance-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string source, string target, string input = "input") => new()
    { ["fromNode"] = source, ["fromPort"] = "image", ["toNode"] = target, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonArray nodes, JsonArray edges, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "edge-point-distance", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = nodes, ["connections"] = edges, ["outputs"] = outputs
    };
    private static (double X, double Y) At(double degrees, double tangent, double normal)
    {
        double angle = degrees * Math.PI / 180;
        return (200 + tangent * Math.Cos(angle) - normal * Math.Sin(angle), 150 + tangent * Math.Sin(angle) + normal * Math.Cos(angle));
    }
    private static JsonObject PointParameters(double degrees = 0, bool rectangle = false)
    {
        var center = At(degrees, 0, -25);
        return new()
        {
            ["roi"] = rectangle ? RoiRegion.Rectangle(80, 100, 240, 50).ToJson() : RoiRegion.RotatedRectangle(center.X, center.Y, 240, 50, degrees).ToJson(),
            ["sampleSpacing"] = 6.0, ["projectionWidth"] = 5, ["sigma"] = 1.0, ["edgeThreshold"] = 10.0,
            ["polarity"] = "darkToLight", ["minimumCoverage"] = .5
        };
    }
    private static JsonObject LineParameters(double degrees = 0)
    {
        var center = At(degrees, 0, 45);
        return new()
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
            ["roi"] = RoiRegion.RotatedRectangle(center.X, center.Y, 260, 22, degrees).ToJson(),
            ["caliperCount"] = 40, ["projectionWidth"] = 5, ["sigma"] = 1.0,
            ["polarity"] = "lightToDark", ["minimumCoverage"] = .8, ["maximumDeviation"] = 1.5
        };
    }
    private static JsonObject Definition(double degrees = 0, string order = "branches", JsonObject? pointParameters = null, JsonObject? measureParameters = null)
    {
        var nodes = new JsonArray(Node("source", "ExternalImage"), Node("points", "FindEdgePoints", pointParameters ?? PointParameters(degrees)),
            Node("line", "FindLines", LineParameters(degrees)), Node("distance", "MeasurePointLineDistances", measureParameters));
        JsonArray edges = order switch
        {
            "pointsFirst" => new(Edge("source", "points"), Edge("points", "line"), Edge("line", "distance")),
            "lineFirst" => new(Edge("source", "line"), Edge("line", "points"), Edge("points", "distance")),
            _ => new(Edge("source", "points"), Edge("source", "line"), Edge("points", "distance"), Edge("line", "distance", "line"))
        };
        return Recipe(nodes, edges, new JsonObject
        {
            ["Image"] = Output("distance"), ["Points"] = Output("points", "result", "Result"),
            ["PointImage"] = Output("points"), ["Measurement"] = Output("distance", "result", "Result"),
            ["Overlay"] = Output("distance", "overlay", "Overlay")
        });
    }
    private string Save(JsonObject definition)
    {
        string directory = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return directory;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame, new() { Debug = false });
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static VisionFrame Wave(double degrees = 0, bool gap = false, bool blank = false, int originX = 700, int originY = -90, bool invert = false)
    {
        const int width = 400, height = 300; var pixels = new byte[width * height];
        double angle = degrees * Math.PI / 180;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double tangent = (x - 200) * Math.Cos(angle) + (y - 150) * Math.Sin(angle);
            double normal = -(x - 200) * Math.Sin(angle) + (y - 150) * Math.Cos(angle);
            double boundary = -25 + 12 * Math.Sin(tangent / 27);
            bool bright = normal >= boundary && normal <= 45;
            if (gap && Math.Abs(tangent) < 24) bright = normal <= 45;
            byte value = blank ? (byte)20 : bright ? (byte)230 : (byte)20;
            pixels[y * width + x] = invert ? (byte)(255 - value) : value;
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, originX, originY);
    }
    private static byte[] Pixels(VisionFrame image)
    {
        byte[] raw = image.CopyPixels(), compact = new byte[image.Info.Width * image.Info.Height];
        for (int row = 0; row < image.Info.Height; row++) Array.Copy(raw, row * image.Info.Stride, compact, row * image.Info.Width, image.Info.Width);
        return compact;
    }

    [Theory]
    [InlineData(0.0)] [InlineData(27.0)] [InlineData(-35.0)] [InlineData(90.0)]
    public async Task RotatedSearchStripPreservesTheMeasuredWavyEdgeAndOriginalImageCoordinates(double degrees)
    {
        using var frame = Wave(degrees); using var result = await Run(Definition(degrees), frame);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.Equal("original", points.CoordinateSpace);
        Assert.Equal(41, points.StationCount); Assert.InRange(points.Count, 39, 41); Assert.InRange(points.Coverage, .95, 1);
        Assert.Equal(points.Count, result.GetEdgePoints("PointImage").Count); Assert.Equal(points.Count, result.GetEdgePoints("Image").Count);
        double angle = degrees * Math.PI / 180;
        foreach (var point in points.Points)
        {
            double x = point.X - 700 - 200, y = point.Y + 90 - 150;
            double tangent = x * Math.Cos(angle) + y * Math.Sin(angle), normal = -x * Math.Sin(angle) + y * Math.Cos(angle);
            Assert.InRange(Math.Abs(normal - (-25 + 12 * Math.Sin(tangent / 27))), 0, 1.2);
        }
        var distances = result.GetPointLineDistances("Measurement"); Assert.True(distances.Valid); Assert.Equal(points.Count, distances.Count);
        Assert.Equal(distances.Count, result.GetPointLineDistances("Image").Count);
        Assert.InRange(distances.Maximum!.Value - distances.Minimum!.Value, 22, 26);
        Assert.InRange(distances.Mean!.Value, 65, 75); Assert.InRange(distances.Rms!.Value, 65, 77);
        Assert.All(distances.Distances, point => Assert.InRange(point.Distance, 56, 84));
        using var output = result.GetOutputImage("Image"); Assert.Equal(Pixels(frame), Pixels(output));
        Assert.All(result.GetOverlays("Image"), overlay => Assert.Equal("#00FF00", overlay.Color));
    }

    [Theory]
    [InlineData("branches")] [InlineData("pointsFirst")] [InlineData("lineFirst")]
    public async Task IndependentBranchesAndBothSerialOrdersUseTheSameCurrentFramePointSetAndLine(string order)
    {
        using var frame = Wave(); using var result = await Run(Definition(order: order), frame);
        var points = result.GetEdgePoints("Points"); var measurement = result.GetPointLineDistances("Image");
        Assert.True(measurement.Valid); Assert.Equal(points.Points.Select(point => point.StationIndex), measurement.Distances.Select(point => point.StationIndex));
        for (int index = 0; index < points.Count; index++)
        {
            var point = points.Points[index]; var distance = measurement.Distances[index];
            Assert.Equal(point.X, distance.X, 8); Assert.Equal(point.Y, distance.Y, 8);
            Assert.InRange(distance.ProjectionY, 104, 106);
        }
    }

    [Fact]
    public async Task AxisAlignedRectangleSearchRegionWorksWithoutAHiddenLineFit()
    {
        using var frame = Wave(); using var result = await Run(Definition(pointParameters: PointParameters(rectangle: true)), frame);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.InRange(points.Count, 39, 41);
        Assert.True(points.Points.Max(point => point.Y) - points.Points.Min(point => point.Y) > 22);
    }

    [Fact]
    public async Task MissingEdgeStationsAreAbsentInsteadOfBecomingZeroCoordinatesOrBridgingCurveSegments()
    {
        using var frame = Wave(gap: true); using var result = await Run(Definition(), frame); var points = result.GetEdgePoints("Points");
        Assert.True(points.Valid); Assert.InRange(points.MissedCount, 6, 10); Assert.Equal(points.Count + points.MissedCount, points.StationCount);
        Assert.DoesNotContain(points.Points, point => Math.Abs(point.X - 900) < 17);
        var measurement = result.GetPointLineDistances("Measurement"); Assert.True(measurement.Valid); Assert.Equal(points.Count, measurement.Count);
        Assert.InRange(measurement.Minimum!.Value, 56, 64); Assert.InRange(measurement.Maximum!.Value, 78, 84);
        var curves = result.GetOverlays("PointImage").Where(overlay => overlay.Kind is "polyline" or "line").ToArray();
        Assert.NotEmpty(curves);
        foreach (var curve in curves)
            for (int index = 1; index < curve.Points.Count; index++)
                Assert.InRange(Math.Abs(curve.Points[index].X - curve.Points[index - 1].X), 0, 9);
    }

    [Theory]
    [InlineData("minimum")] [InlineData("maximum")] [InlineData("mean")] [InlineData("rms")] [InlineData("range")]
    public async Task SelectedStatisticMatchesTheActualPerPointDistances(string statistic)
    {
        using var frame = Wave(); using var result = await Run(Definition(measureParameters: new JsonObject { ["statistic"] = statistic }), frame);
        var measurement = result.GetPointLineDistances("Measurement"); Assert.True(measurement.Valid);
        double expected = statistic switch
        {
            "minimum" => measurement.Distances.Min(point => point.Distance), "maximum" => measurement.Distances.Max(point => point.Distance),
            "mean" => measurement.Distances.Average(point => point.Distance), "rms" => Math.Sqrt(measurement.Distances.Average(point => point.Distance * point.Distance)),
            _ => measurement.Distances.Max(point => point.Distance) - measurement.Distances.Min(point => point.Distance)
        };
        Assert.Equal(expected, measurement.Value!.Value, 8);
    }

    [Fact]
    public async Task ABlankFrameDoesNotReusePreviousPointsDistancesOrOverlays()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition())); await using var session = recipe.CreateSession();
        using var good = Wave(); using var first = await session.RunAsync(good); Assert.True(first.GetPointLineDistances("Measurement").Valid);
        using var blank = Wave(blank: true); using var absent = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, absent.Status); var points = absent.GetEdgePoints("Points"); Assert.Empty(points.Points); Assert.False(points.Valid);
        var measurement = absent.GetPointLineDistances("Measurement"); Assert.False(measurement.Valid); Assert.Empty(measurement.Distances);
        Assert.Null(measurement.Minimum); Assert.Null(measurement.Value); Assert.Equal(InspectionDecision.Unknown, measurement.Decision.Status);
        Assert.Empty(absent.GetOverlays("Overlay"));
        using var restored = await session.RunAsync(good); Assert.True(restored.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task ASearchRoiMustBeDrawnAndMissingGeometryProducesAnExplicitUnavailableResult()
    {
        var definition = Definition(pointParameters: new JsonObject()); using var frame = Wave(); using var result = await Run(definition, frame);
        var points = result.GetEdgePoints("Points"); Assert.False(points.Valid); Assert.Empty(points.Points); Assert.Equal("MissingSearchRoi", points.Reason);
        var measurement = result.GetPointLineDistances("Measurement"); Assert.False(measurement.Valid); Assert.Null(measurement.Value);
    }

    [Fact]
    public async Task CoverageThresholdCanInvalidateSparsePointsWithoutInventingOrDiscardingTheMeasuredCoordinates()
    {
        var parameters = PointParameters(); parameters["minimumCoverage"] = .95;
        using var frame = Wave(gap: true); using var result = await Run(Definition(pointParameters: parameters), frame);
        var points = result.GetEdgePoints("Points"); Assert.False(points.Valid); Assert.NotEmpty(points.Points); Assert.InRange(points.Coverage, .6, .9);
        Assert.False(result.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task ReverseSearchReversesTheRequiredPolarityAndKeepsPointOrderAlongTheStrip()
    {
        var parameters = PointParameters(); parameters["reverseSearch"] = true; parameters["polarity"] = "lightToDark";
        using var frame = Wave(); using var result = await Run(Definition(pointParameters: parameters), frame);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.InRange(points.Count, 39, 41);
        Assert.True(points.Points.First().X < points.Points.Last().X); Assert.True(result.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task WrongPolarityProducesNoPointsInsteadOfSelectingTheOppositeUnrelatedEdge()
    {
        var parameters = PointParameters(); parameters["polarity"] = "lightToDark";
        using var frame = Wave(); using var result = await Run(Definition(pointParameters: parameters), frame);
        Assert.Empty(result.GetEdgePoints("Points").Points); Assert.False(result.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task ZeroThresholdDoesNotTurnAFlatImageIntoInventedEdgePoints()
    {
        var parameters = PointParameters(); parameters["edgeThreshold"] = 0;
        using var frame = Wave(blank: true); using var result = await Run(Definition(pointParameters: parameters), frame);
        Assert.Empty(result.GetEdgePoints("Points").Points); Assert.False(result.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task MissingSubscribedCenterSkipsTheOperatorBeforeAnOutOfImageFallbackIsValidated()
    {
        var parameters = PointParameters();
        parameters["roi"]!["cx"] = new JsonObject { ["node"] = "circle", ["port"] = "result", ["field"] = "measurement.selected.centerX", ["fallback"] = 10000 };
        var circle = new JsonObject
        {
            ["roi"] = RoiRegion.Circle(200, 150, 40).ToJson(), ["caliperPlacement"] = "roiBoundary", ["caliperCount"] = 24,
            ["searchLength"] = 40.0, ["projectionWidth"] = 5, ["polarity"] = "lightToDark", ["minimumCoverage"] = .5
        };
        var definition = Definition(pointParameters: parameters);
        definition["nodes"]!.AsArray().Add(Node("circle", "CircleCaliper", circle)); definition["connections"]!.AsArray().Add(Edge("source", "circle"));
        using var frame = Wave(blank: true); using var result = await Run(definition, frame);
        var points = result.GetEdgePoints("Points"); Assert.False(points.Valid); Assert.Empty(points.Points);
        Assert.False(result.GetPointLineDistances("Measurement").Valid);
    }

    [Fact]
    public async Task TwoIndependentAnnularCoordinatePlanesCannotBeMixedByTheDistanceOperator()
    {
        JsonObject Unwrap() => new() { ["roi"] = RoiRegion.Annulus(200, 150, 40, 100).ToJson(), ["outputWidth"] = 300, ["outputHeight"] = 100 };
        var points = PointParameters(); points["roi"] = RoiRegion.RotatedRectangle(150, 50, 200, 40, 0).ToJson();
        var line = LineParameters(); line["roi"] = RoiRegion.RotatedRectangle(150, 50, 200, 40, 0).ToJson();
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrapA", "AnnularUnwrap", Unwrap()), Node("unwrapB", "AnnularUnwrap", Unwrap()),
            Node("points", "FindEdgePoints", points), Node("line", "FindLines", line), Node("distance", "MeasurePointLineDistances")),
            new JsonArray(Edge("source", "unwrapA"), Edge("source", "unwrapB"), Edge("unwrapA", "points"), Edge("unwrapB", "line"), Edge("points", "distance"), Edge("line", "distance", "line")),
            new JsonObject { ["Image"] = Output("distance") });
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        using var frame = Wave(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("coordinate", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(37.0)] [InlineData(-90.0)]
    public void EdgePointCaliperLayoutUsesExactSpacingAndReversesOnlyItsNormal(double degrees)
    {
        var roi = RoiRegion.RotatedRectangle(300, 200, 101, 40, degrees);
        var parameters = new JsonObject { ["sampleSpacing"] = 6.0, ["projectionWidth"] = 5 };
        var windows = CaliperLayout.EdgePoints(roi, parameters); Assert.Equal(17, windows.Count);
        double radians = degrees * Math.PI / 180;
        for (int index = 0; index < windows.Count; index++)
        {
            double position = -50.5 + index * 6;
            Assert.Equal(300 + position * Math.Cos(radians), windows[index].X, 9);
            Assert.Equal(200 + position * Math.Sin(radians), windows[index].Y, 9);
            Assert.Equal(-Math.Sin(radians), windows[index].NormalX, 9); Assert.Equal(Math.Cos(radians), windows[index].NormalY, 9);
        }
        parameters["reverseSearch"] = true; var reversed = CaliperLayout.EdgePoints(roi, parameters);
        for (int index = 0; index < windows.Count; index++)
        {
            Assert.Equal(windows[index].X, reversed[index].X); Assert.Equal(windows[index].Y, reversed[index].Y);
            Assert.Equal(-windows[index].NormalX, reversed[index].NormalX); Assert.Equal(-windows[index].NormalY, reversed[index].NormalY);
        }
    }

    [Fact]
    public void DenseLayoutKeepsAllDataAndRejectsConfigurationsAboveThePointLimit()
    {
        var roi = RoiRegion.RotatedRectangle(1000, 100, 1000, 40, 0);
        var parameters = new JsonObject { ["sampleSpacing"] = 1.0, ["projectionWidth"] = 5 };
        Assert.Equal(1001, CaliperLayout.EdgePoints(roi, parameters).Count);
        parameters["maximumPoints"] = 500; Assert.Empty(CaliperLayout.EdgePoints(roi, parameters));
    }

    [Fact]
    public void EditorCatalogKeepsBothOperatorsImageBasedAndRejectsNewConnectionCycles()
    {
        var catalog = new NodeCatalog(VisionEngine.GetNodeCatalog()); var flow = new FlowDocument(catalog); flow.Load(Definition().ToJsonString());
        Assert.Equal("image", Assert.Single(catalog["FindEdgePoints"].VisibleOutputs).Id);
        Assert.All(catalog["MeasurePointLineDistances"].VisibleInputs, input => Assert.Equal("Image", input.Type));
        Assert.Throws<InvalidOperationException>(() => flow.Connect("distance", "image", "points", "input"));
    }

    [Fact]
    public async Task CroppedInputOriginIsAddedExactlyOnceToEveryPointAndProjection()
    {
        var definition = Definition(); definition["nodes"]!.AsArray().Add(Node("crop", "CropRect", new JsonObject { ["x"] = 40, ["y"] = 50, ["width"] = 320, ["height"] = 220 }));
        foreach (var edge in definition["connections"]!.AsArray().OfType<JsonObject>()) if (edge["fromNode"]!.GetValue<string>() == "source") edge["fromNode"] = "crop";
        definition["connections"]!.AsArray().Add(Edge("source", "crop"));
        var pointsParameters = definition["nodes"]![1]!["parameters"]!; pointsParameters["roi"] = RoiRegion.RotatedRectangle(160, 75, 240, 50, 0).ToJson();
        definition["nodes"]![2]!["parameters"]!["roi"] = RoiRegion.RotatedRectangle(160, 145, 260, 22, 0).ToJson();
        using var frame = Wave(); using var result = await Run(definition, frame);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.InRange(points.Points.First().X, 779, 781);
        var measurement = result.GetPointLineDistances("Measurement"); Assert.True(measurement.Valid);
        Assert.All(measurement.Distances, sample => Assert.InRange(sample.ProjectionY, 104, 106));
    }

    [Fact]
    public async Task ToleranceUsesTheSelectedStatisticAndEveryDistanceRemainsAvailable()
    {
        var parameters = new JsonObject { ["statistic"] = "range", ["toleranceEnabled"] = true, ["lowerLimit"] = 21.0, ["upperLimit"] = 26.0 };
        using var frame = Wave(); using var pass = await Run(Definition(measureParameters: parameters), frame);
        var result = pass.GetPointLineDistances("Measurement"); Assert.True(result.Valid); Assert.Equal(InspectionDecision.Pass, result.Decision.Status);
        parameters["upperLimit"] = 22.0; using var fail = await Run(Definition(measureParameters: parameters.DeepClone().AsObject()), frame);
        var rejected = fail.GetPointLineDistances("Measurement"); Assert.True(rejected.Valid); Assert.Equal(InspectionDecision.Fail, rejected.Decision.Status); Assert.NotEmpty(rejected.Distances);
    }

    private const int FixtureModelWidth = 32, FixtureModelHeight = 24;
    private static byte[] FixtureModel()
    {
        var pixels = new byte[FixtureModelWidth * FixtureModelHeight];
        for (int y = 0; y < FixtureModelHeight; y++) for (int x = 0; x < FixtureModelWidth; x++)
            pixels[y * FixtureModelWidth + x] = (byte)(25 + (x * 13 + y * 17 + x * y * 3) % 190);
        return pixels;
    }
    private static JsonObject FixtureMatch(double angle) => new()
    {
        ["model"] = new JsonObject { ["width"] = FixtureModelWidth, ["height"] = FixtureModelHeight, ["grayBase64"] = Convert.ToBase64String(FixtureModel()) },
        ["angleMinimum"] = angle, ["angleMaximum"] = angle, ["scaleMinimum"] = 1.0, ["scaleMaximum"] = 1.0,
        ["scoreThreshold"] = .85, ["maximumMatches"] = 1, ["downstreamImageMode"] = "source"
    };
    private static VisionFrame FixtureWave(double angle, int left, int top, double referenceCenterX = 56, double referenceCenterY = 47)
    {
        const int width = 400, height = 300; double radians = angle * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        int rotatedWidth = (int)Math.Ceiling(FixtureModelWidth * Math.Abs(c) + FixtureModelHeight * Math.Abs(s) - 1e-8),
            rotatedHeight = (int)Math.Ceiling(FixtureModelHeight * Math.Abs(c) + FixtureModelWidth * Math.Abs(s) - 1e-8);
        double centerX = left + rotatedWidth / 2.0, centerY = top + rotatedHeight / 2.0;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double referenceX = referenceCenterX + c * (x - centerX) + s * (y - centerY),
                referenceY = referenceCenterY - s * (x - centerX) + c * (y - centerY);
            double boundary = 125 + 12 * Math.Sin((referenceX - 200) / 27);
            pixels[y * width + x] = referenceY >= boundary && referenceY <= 195 ? (byte)230 : (byte)20;
        }
        var model = FixtureModel();
        for (int y = 0; y < rotatedHeight; y++) for (int x = 0; x < rotatedWidth; x++)
        {
            double dx = x - (rotatedWidth - 1) / 2.0, dy = y - (rotatedHeight - 1) / 2.0;
            double mx = (FixtureModelWidth - 1) / 2.0 + c * dx + s * dy, my = (FixtureModelHeight - 1) / 2.0 - s * dx + c * dy;
            int ix = (int)Math.Floor(mx), iy = (int)Math.Floor(my);
            if (ix < 0 || iy < 0 || ix + 1 >= FixtureModelWidth || iy + 1 >= FixtureModelHeight) continue;
            double fx = mx - ix, fy = my - iy;
            double value = (1 - fy) * ((1 - fx) * model[iy * FixtureModelWidth + ix] + fx * model[iy * FixtureModelWidth + ix + 1]) +
                fy * ((1 - fx) * model[(iy + 1) * FixtureModelWidth + ix] + fx * model[(iy + 1) * FixtureModelWidth + ix + 1]);
            pixels[(top + y) * width + left + x] = (byte)Math.Round(value);
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8);
    }
    private async Task<JsonObject> TeachEdgeFixture()
    {
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("match", "TemplateMatch", FixtureMatch(0))),
            new JsonArray(Edge("source", "match")), new JsonObject { ["Pose"] = Output("match", "pose", "Pose") });
        using var frame = FixtureWave(0, 40, 35); using var result = await Run(definition, frame);
        var pose = result.GetOutput<VisionPose>("Pose"); Assert.True(pose.Available);
        return new() { ["centerX"] = pose.Position!.CenterX, ["centerY"] = pose.Position.CenterY, ["angleDegrees"] = 0.0, ["scale"] = 1.0, ["modelHash"] = pose.ModelHash };
    }
    private static JsonObject FixturedDefinition(JsonObject reference, double angle, double coverage = .5)
    {
        var points = PointParameters(rectangle: true); points["roiReference"] = "fixture"; points["minimumCoverage"] = coverage;
        var line = LineParameters(); line["roiReference"] = "fixture"; line["minimumCoverage"] = coverage;
        return Recipe(new JsonArray(Node("source", "ExternalImage"), Node("match", "TemplateMatch", FixtureMatch(angle)),
            Node("fixture", "PositionFixture", new JsonObject { ["reference"] = reference.DeepClone() }), Node("points", "FindEdgePoints", points),
            Node("line", "FindLines", line), Node("distance", "MeasurePointLineDistances")),
            new JsonArray(Edge("source", "match"), Edge("match", "fixture"), Edge("fixture", "points"), Edge("fixture", "line"), Edge("points", "distance"), Edge("line", "distance", "line")),
            new JsonObject { ["Image"] = Output("distance"), ["Points"] = Output("points", "result", "Result"), ["Measurement"] = Output("distance", "result", "Result"), ["Pose"] = Output("match", "pose", "Pose") });
    }

    [Fact]
    public async Task AxisAlignedTaughtStripFollowsAnActualMatchedFixtureRotationAndTranslation()
    {
        var reference = await TeachEdgeFixture(); const double angle = 27;
        using var frame = FixtureWave(angle, 128, 4, reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>());
        using var result = await Run(FixturedDefinition(reference, angle), frame);
        var pose = result.GetOutput<VisionPose>("Pose"); Assert.True(pose.Available);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.InRange(points.Count, 39, 41);
        double c = Math.Cos(angle * Math.PI / 180), s = Math.Sin(angle * Math.PI / 180);
        foreach (var point in points.Points)
        {
            double dx = point.X - pose.Position!.CenterX, dy = point.Y - pose.Position.CenterY;
            double x = reference["centerX"]!.GetValue<double>() + c * dx + s * dy,
                y = reference["centerY"]!.GetValue<double>() - s * dx + c * dy;
            Assert.InRange(Math.Abs(y - (125 + 12 * Math.Sin((x - 200) / 27))), 0, 1.5);
        }
        var distances = result.GetPointLineDistances("Measurement"); Assert.True(distances.Valid); Assert.InRange(distances.Range!.Value, 22, 26);
        Assert.All(distances.Distances, point => Assert.InRange(point.Distance, 56, 84));
    }

    [Fact]
    public async Task AFixturedStripWithNegativeCenterUsesItsVisibleStationsWithoutRecipeValidationFailure()
    {
        var reference = await TeachEdgeFixture(); reference["centerX"] = reference["centerX"]!.GetValue<double>() + 230;
        using var frame = FixtureWave(0, 40, 35, reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>());
        using var result = await Run(FixturedDefinition(reference, 0, .25), frame);
        var points = result.GetEdgePoints("Points"); Assert.True(points.Valid); Assert.InRange(points.Count, 10, 18);
        Assert.InRange(points.Coverage, .25, .45); Assert.All(points.Points, point => Assert.InRange(point.X, 0, 91));
        var distance = result.GetPointLineDistances("Measurement"); Assert.True(distance.Valid); Assert.Equal(points.Count, distance.Count);
    }

    [Fact]
    public async Task AFullyOffscreenFixturedStripOutputsNoInventedPointsAndAnUnknownDistance()
    {
        var reference = await TeachEdgeFixture(); reference["centerX"] = reference["centerX"]!.GetValue<double>() + 470;
        using var frame = FixtureWave(0, 40, 35, reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>());
        using var result = await Run(FixturedDefinition(reference, 0, .25), frame);
        var points = result.GetEdgePoints("Points"); Assert.False(points.Valid); Assert.Empty(points.Points); Assert.Equal(0, points.Coverage);
        var distance = result.GetPointLineDistances("Measurement"); Assert.False(distance.Valid); Assert.Empty(distance.Distances); Assert.Null(distance.Value);
        Assert.Equal(InspectionDecision.Unknown, distance.Decision.Status);
    }

    [Theory]
    [InlineData("projectionWidth", 4)] [InlineData("sampleSpacing", 0)] [InlineData("maximumPoints", 0)]
    public void InvalidSamplingParametersAreRejectedBeforeExecution(string key, int value)
    {
        var p = PointParameters(); p[key] = value; using var engine = new VisionEngine(store);
        Assert.ThrowsAny<Exception>(() => engine.LoadRecipe(Save(Definition(pointParameters: p))));
    }

    [Fact]
    public void SdkRejectsMismatchedPointCountsAndMissingCoordinatePlanes()
    {
        var packet = PointPacket(); packet["count"] = 2; using var mismatch = ReadPacket(packet, "PointSet");
        Assert.Throws<InvalidDataException>(() => mismatch.GetEdgePoints("Output"));
        packet = PointPacket(); packet["coordinateSpace"] = "annularUnwrapped"; using var wrongPlane = ReadPacket(packet, "PointSet");
        Assert.Throws<InvalidDataException>(() => wrongPlane.GetEdgePoints("Output"));
    }
    [Fact]
    public void SdkReadsSparseStationIndicesAndTypedPointSetFromImageMetadata()
    {
        var packet = PointPacket(); using var result = ReadPacket(packet, "PointSet", image: true);
        var points = result.GetEdgePoints("Output"); Assert.Equal(1, points.Count); Assert.Equal(2, points.Points[0].StationIndex); Assert.Equal(8, points.Points[0].X);
    }
    [Theory]
    [InlineData("distance")] [InlineData("projectionX")] [InlineData("mean")]
    public void SdkRejectsDistancesThatDoNotMatchTheProjectionOrStatistics(string field)
    {
        var packet = DistancePacket();
        if (field == "mean") packet["mean"] = 100;
        else packet["distances"]![0]![field] = 99;
        using var result = ReadPacket(packet, "Measurement"); Assert.Throws<InvalidDataException>(() => result.GetPointLineDistances("Output"));
    }
    [Fact]
    public void SdkReadsTypedPerPointProjectionAndStatisticsFromAnImageOutput()
    {
        using var result = ReadPacket(DistancePacket(), "Measurement", image: true); var distances = result.GetPointLineDistances("Output");
        Assert.True(distances.Valid); Assert.Equal(3.0, distances.Value!.Value); Assert.Equal(0.0, distances.Distances[0].ProjectionY);
    }
    private static JsonObject PointPacket() => new()
    {
        ["resultKind"] = "edgePointSet", ["coordinateSpace"] = "original", ["dataAvailable"] = true, ["valid"] = true,
        ["count"] = 1, ["stationCount"] = 3, ["missedCount"] = 2, ["coverage"] = 1.0 / 3,
        ["points"] = new JsonArray(new JsonObject { ["index"] = 0, ["stationIndex"] = 2, ["x"] = 8.0, ["y"] = 3.0, ["gradient"] = 23.0, ["pathPosition"] = 8.0 }), ["overlay"] = new JsonArray()
    };
    private static JsonObject DistancePacket() => new()
    {
        ["resultKind"] = "pointLineDistances", ["operation"] = "pointSetToLine", ["coordinateSpace"] = "original", ["unit"] = "px",
        ["dataAvailable"] = true, ["valid"] = true, ["statistic"] = "maximum", ["pointCount"] = 1, ["count"] = 1,
        ["distances"] = new JsonArray(new JsonObject { ["index"] = 0, ["stationIndex"] = 2, ["x"] = 8.0, ["y"] = 3.0, ["projectionX"] = 8.0, ["projectionY"] = 0.0, ["distance"] = 3.0, ["signedDistance"] = 3.0 }),
        ["referenceLine"] = new JsonObject { ["x1"] = 0.0, ["y1"] = 0.0, ["x2"] = 10.0, ["y2"] = 0.0, ["a"] = 0.0, ["b"] = 1.0, ["c"] = 0.0 },
        ["minimum"] = 3.0, ["maximum"] = 3.0, ["mean"] = 3.0, ["rms"] = 3.0, ["standardDeviation"] = 0.0, ["range"] = 0.0, ["value"] = 3.0,
        ["decision"] = new JsonObject { ["status"] = "Unknown", ["reason"] = "UnconfiguredTolerance" }, ["overlay"] = new JsonArray()
    };
    private static InspectionResult ReadPacket(JsonObject packet, string attachment, bool image = false)
    {
        var output = image ? new JsonObject { ["pipeline"] = new JsonObject { ["results"] = new JsonObject { [attachment] = packet } } } : packet;
        var data = new JsonObject { ["executionStatus"] = "Succeeded", ["decision"] = "Unknown", ["elapsedMs"] = 1, ["runId"] = "edge-points-sdk-fixture", ["outputs"] = new JsonObject { ["Output"] = output } };
        var type = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { type, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

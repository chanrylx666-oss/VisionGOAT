using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class FixturePipelineTests : IDisposable
{
    private const int W = 500, H = 460, MW = 32, MH = 24;
    private readonly string store;
    public FixturePipelineTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "fixture-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static byte[] Model()
    {
        var model = new byte[MW * MH];
        for (int y = 0; y < MH; y++) for (int x = 0; x < MW; x++) model[y * MW + x] = (byte)(25 + (x * 13 + y * 17 + x * y * 3) % 190);
        return model;
    }
    private static VisionPoint Map(VisionMatchPosition pose, double x, double y)
    {
        double a = pose.AngleDegrees * Math.PI / 180;
        return new(pose.CenterX + pose.Scale * (Math.Cos(a) * x - Math.Sin(a) * y),
                   pose.CenterY + pose.Scale * (Math.Sin(a) * x + Math.Cos(a) * y));
    }
    private static (VisionFrame Frame, VisionMatchPosition Pose, VisionPoint A, VisionPoint B) Image(double angle, double scale,
        bool matched = true, bool second = true, int originX = 0, int originY = 0, int left = 160, int top = 160)
    {
        double r = angle * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        int rw = (int)Math.Ceiling((MW * Math.Abs(c) + MH * Math.Abs(s)) * scale - 1e-8),
            rh = (int)Math.Ceiling((MH * Math.Abs(c) + MW * Math.Abs(s)) * scale - 1e-8);
        var pose = new VisionMatchPosition(left + rw / 2.0 + originX, top + rh / 2.0 + originY, angle, scale);
        var a = Map(pose, 68, 20); var b = Map(pose, 118, -26);
        var pixels = Enumerable.Repeat((byte)15, W * H).ToArray(); var model = Model();
        if (matched)
            for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
            {
                double dx = (x - (rw - 1) / 2.0) / scale, dy = (y - (rh - 1) / 2.0) / scale;
                double mx = (MW - 1) / 2.0 + c * dx + s * dy, my = (MH - 1) / 2.0 - s * dx + c * dy;
                int ix = (int)Math.Floor(mx), iy = (int)Math.Floor(my);
                if (ix < 0 || iy < 0 || ix + 1 >= MW || iy + 1 >= MH) continue;
                double fx = mx - ix, fy = my - iy;
                double value = (1 - fy) * ((1 - fx) * model[iy * MW + ix] + fx * model[iy * MW + ix + 1]) +
                    fy * ((1 - fx) * model[(iy + 1) * MW + ix] + fx * model[(iy + 1) * MW + ix + 1]);
                pixels[(top + y) * W + left + x] = (byte)Math.Round(value);
            }
        foreach (var circle in second ? new[] { a, b } : new[] { a })
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x + originX - circle.X, 2) + Math.Pow(y + originY - circle.Y, 2));
                if (distance < 18 * scale + 1) pixels[y * W + x] = (byte)Math.Clamp((18 * scale + .5 - distance) * 230, 15, 230);
            }
        return (VisionFrame.CopyFrom(pixels, W, H, W, VisionPixelFormat.Gray8, originX, originY), pose, a, b);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string port, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = port, ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port, string type) => new() { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Match(double angle, double scale) => new()
    {
        ["model"] = new JsonObject { ["width"] = MW, ["height"] = MH, ["grayBase64"] = Convert.ToBase64String(Model()) },
        ["angleMinimum"] = angle, ["angleMaximum"] = angle, ["scaleMinimum"] = scale, ["scaleMaximum"] = scale,
        ["scoreThreshold"] = .85, ["maximumMatches"] = 1, ["downstreamImageMode"] = "source"
    };
    private static JsonObject Base(double angle, double scale) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "fixture-pipeline", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("gray", "ToGray"), Node("match", "TemplateMatch", Match(angle, scale))),
        ["connections"] = new JsonArray(Edge("source", "image", "gray"), Edge("gray", "image", "match")),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = new JsonObject { ["Pose"] = Output("match", "pose", "Pose") }
    };
    private string Save(JsonObject d)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Execute(JsonObject d, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(d)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, new() { Debug = false });
    }
    private async Task<JsonObject> Reference(int originX = 0, int originY = 0)
    {
        var reference = Image(20, 1.2, originX: originX, originY: originY, left: 40, top: 50);
        using var frame = reference.Frame; using var result = await Execute(Base(20, 1.2), frame);
        var pose = result.GetOutput<VisionPose>("Pose"); Assert.True(pose.Available);
        return new() { ["centerX"] = pose.Position!.CenterX, ["centerY"] = pose.Position.CenterY,
            ["angleDegrees"] = pose.Position.AngleDegrees, ["scale"] = pose.Position.Scale, ["modelHash"] = pose.ModelHash };
    }
    private static JsonObject Pipeline(JsonObject reference, double angle, double scale)
    {
        var d = Base(angle, scale); var nodes = d["nodes"]!.AsArray(); var edges = d["connections"]!.AsArray();
        nodes.Add(Node("fixture", "PositionFixture", new() { ["reference"] = reference.DeepClone() }));
        edges.Add(Edge("gray", "image", "fixture")); edges.Add(Edge("match", "pose", "fixture", "position"));
        var taught = new VisionMatchPosition(reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>(), 20, 1.2);
        foreach (var item in new[] { (Id: "a", Point: Map(taught, 68, 20)), (Id: "b", Point: Map(taught, 118, -26)) })
        {
            nodes.Add(Node(item.Id, "FindCircles", new()
            {
                ["positionReference"] = "fixture", ["method"] = "caliper", ["caliperPlacement"] = "roiBoundary",
                ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = item.Point.X, ["cy"] = item.Point.Y, ["radius"] = 21.6 },
                ["caliperCount"] = 36, ["projectionWidth"] = 5, ["searchLength"] = 24.0, ["polarity"] = "lightToDark"
            }));
            edges.Add(Edge("fixture", "image", item.Id));
        }
        nodes.Add(Node("distance", "MeasureCircleDistance"));
        edges.Add(Edge("a", "features", "distance", "first")); edges.Add(Edge("b", "features", "distance", "second"));
        d["outputs"] = new JsonObject { ["Distance"] = Output("distance", "result", "Result"),
            ["A"] = Output("a", "result", "Result"), ["B"] = Output("b", "result", "Result"), ["Fixture"] = Output("fixture", "result", "Result") };
        return d;
    }
    [Theory]
    [InlineData(-40, .8, 0, 0)] [InlineData(0, 1, 0, 0)] [InlineData(60, 1.2, 0, 0)]
    [InlineData(-40, 1.2, 900, -80)] [InlineData(20, .8, 900, -80)]
    public async Task TwoCirclesFollowReferenceRotationScaleAndOriginalOrigin(double angle, double scale, int ox, int oy)
    {
        var reference = await Reference(ox, oy); var data = Image(angle, scale, originX: ox, originY: oy);
        using var frame = data.Frame; using var result = await Execute(Pipeline(reference, angle, scale), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var a = result.GetResult<VisionCircle>("A"); var b = result.GetResult<VisionCircle>("B");
        Assert.Single(a.Features); Assert.Single(b.Features);
        Assert.InRange(a.Features[0].X, data.A.X - .8, data.A.X + .8);
        Assert.InRange(a.Features[0].Y, data.A.Y - .8, data.A.Y + .8);
        Assert.InRange(b.Features[0].Radius, 18 * scale - .8, 18 * scale + .8);
        Assert.InRange(result.GetGeometryMeasurement("Distance").Value!.Value,
            Math.Sqrt(50 * 50 + 46 * 46) * scale - 1.2, Math.Sqrt(50 * 50 + 46 * 46) * scale + 1.2);
        Assert.NotNull(a.Measurement.Fixture); Assert.Null(a.Measurement.Position);
        var t = a.Measurement.Fixture!;
        var point = new VisionPoint(data.A.X, data.A.Y); var roundtrip = t.TransformPoint(t.TransformPoint(point, true));
        Assert.InRange(Math.Abs(point.X - roundtrip.X), 0, 1e-8); Assert.InRange(Math.Abs(point.Y - roundtrip.Y), 0, 1e-8);
        using var pixels = result.GetOutputImage("Fixture"); Assert.Equal(W, pixels.Info.Width); Assert.Equal(ox, pixels.Info.OriginX);
    }
    [Fact]
    public async Task FixtureReadsTheCurrentPoseFromOneImageConnection()
    {
        var reference = await Reference(900, -80); var d = Pipeline(reference, -40, 1.2);
        var edges = d["connections"]!.AsArray();
        for (int i = edges.Count - 1; i >= 0; i--)
            if (edges[i]!["toNode"]!.GetValue<string>() == "fixture") edges.RemoveAt(i);
        edges.Add(Edge("match", "image", "fixture"));
        d["outputs"]!["FixtureImage"] = Output("fixture", "image", "Image");
        var data = Image(-40, 1.2, originX: 900, originY: -80); using var frame = data.Frame;
        var before = frame.CopyPixels(); using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.True(result.GetGeometricMeasurement("Distance").Valid);
        var pose = result.GetImageAttachedOutput<VisionPose>("FixtureImage", "Pose");
        Assert.True(pose.Available); Assert.Equal(reference["modelHash"]!.GetValue<string>(), pose.ModelHash);
        using var image = result.GetOutputImage("FixtureImage"); Assert.Equal(before, image.CopyPixels());
        Assert.Equal(900, image.Info.OriginX); Assert.Equal(-80, image.Info.OriginY);
    }

    [Fact]
    public async Task ImageWithoutAPoseDoesNotCreateASuccessfulIdentityFixture()
    {
        var reference = await Reference(); var d = Pipeline(reference, 0, 1);
        var edges = d["connections"]!.AsArray();
        for (int i = edges.Count - 1; i >= 0; i--)
            if (edges[i]!["toNode"]!.GetValue<string>() == "fixture") edges.RemoveAt(i);
        edges.Add(Edge("source", "image", "fixture"));
        d["outputs"] = new JsonObject { ["Image"] = Output("fixture", "image", "Image") };
        using var frame = Image(0, 1).Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var output = result.GetOutput<JsonElement>("Image");
        Assert.False(output.GetProperty("available").GetBoolean());
        Assert.Equal("MissingImagePosition", output.GetProperty("unavailableReason").GetString());
        Assert.False(output.TryGetProperty("fixture", out _));
        Assert.Empty(result.GetOverlays("Image"));
    }

    [Fact]
    public async Task AnnularSectorFollowsFixtureAndStaleReferenceRoiIsRejected()
    {
        var reference = await Reference(); var d = Pipeline(reference, -40, .8);
        var roi = d["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == "a")!["parameters"]!["roi"]!;
        double x = roi["cx"]!.GetValue<double>(), y = roi["cy"]!.GetValue<double>();
        d["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == "a")!["parameters"]!["roi"] =
            new JsonObject { ["shape"] = "annulus", ["cx"] = x, ["cy"] = y, ["innerRadius"] = 9.6, ["outerRadius"] = 33.6,
                ["startAngle"] = 300.0, ["endAngle"] = 510.0 };
        var image = Image(-40, .8); using var frame = image.Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Single(result.GetResult<VisionCircle>("A").Features);
        var actual = result.GetResult<VisionCircle>("A").Measurement.Caliper!;
        Assert.InRange(actual.SearchAnnulus!.EndAngle - actual.SearchAnnulus.StartAngle, 209.99, 210.01);
        var referenceRoi = d["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == "a")!["parameters"]!["roi"]!;
        referenceRoi["coordinateSpace"] = "reference"; referenceRoi["referenceHash"] = new string('0', 64);
        using var stale = await Execute(d, frame); Assert.Equal(ExecutionStatus.Failed, stale.Status);
        Assert.Contains("redraw this ROI", stale.Data.GetProperty("error").GetProperty("message").GetString());
    }
    [Fact]
    public async Task NoMatchClearsFixtureAndMeasurementAndChangedModelRequiresReteaching()
    {
        var reference = await Reference(); var d = Pipeline(reference, 0, 1); var missing = Image(0, 1, matched: false);
        using var frame = missing.Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.False(result.GetGeometryMeasurement("Distance").Valid);
        Assert.False(result.Outputs.GetProperty("Fixture").GetProperty("image").GetProperty("available").GetBoolean());
        reference["modelHash"] = new string('0', 64); var found = Image(0, 1); using var foundFrame = found.Frame;
        using var wrong = await Execute(Pipeline(reference, 0, 1), foundFrame);
        Assert.Equal(ExecutionStatus.Failed, wrong.Status); Assert.Empty(wrong.Outputs.EnumerateObject());
        Assert.Contains("template changed", wrong.Data.GetProperty("error").GetProperty("message").GetString());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EdgePairFollowsRectangleAndCandidateWidthWithFixtureScale(bool rotated)
    {
        var reference = await Reference(); var d = Pipeline(reference, -40, .8);
        var pose = new VisionMatchPosition(reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>(), 20, 1.2);
        var center = Map(pose, 68, 20);
        var roi = rotated ? JPVision.Editor.Core.RoiRegion.RotatedRectangle(center.X, center.Y, 5, 70, 20)
            : JPVision.Editor.Core.RoiRegion.Rectangle(center.X - 2.5, center.Y - 35, 5, 70);
        d["nodes"]!.AsArray().Add(Node("pair", "MeasureEdgePair", new()
        { ["roi"] = roi.ToJson(), ["roiReference"] = "fixture", ["expectedWidth"] = 43.2, ["minimumWidth"] = 42.2, ["maximumWidth"] = 44.2 }));
        d["connections"]!.AsArray().Add(Edge("fixture", "image", "pair")); d["outputs"]!["Width"] = Output("pair", "result", "Result");
        var image = Image(-40, .8); using var frame = image.Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var measured = result.GetGeometricMeasurement("Width"); Assert.True(measured.Valid); Assert.InRange(measured.Value!.Value, 28, 29.6);
        var midpoint = new VisionPoint((measured.First!.Point!.Value.X + measured.Second!.Point!.Value.X) / 2,
            (measured.First.Point.Value.Y + measured.Second.Point.Value.Y) / 2);
        Assert.InRange(Math.Abs(midpoint.X - image.A.X), 0, .6); Assert.InRange(Math.Abs(midpoint.Y - image.A.Y), 0, .6);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LineSearchStripKeepsItsCenterAndHeightAfterFixtureRotationAndScale(bool rotated)
    {
        const int ox = 900, oy = -80;
        var reference = await Reference(ox, oy); var d = Base(-40, .8);
        var taught = new VisionMatchPosition(reference["centerX"]!.GetValue<double>(), reference["centerY"]!.GetValue<double>(), 20, 1.2);
        var center = Map(taught, 110, 70);
        var roi = rotated ? JPVision.Editor.Core.RoiRegion.RotatedRectangle(center.X, center.Y, 90, 40, 20)
            : JPVision.Editor.Core.RoiRegion.Rectangle(center.X - 45, center.Y - 20, 90, 40);
        var parameters = new JsonObject { ["method"] = "caliper", ["caliperPlacement"] = "roiCenter", ["roiReference"] = "fixture", ["roi"] = roi.ToJson(),
            ["searchLength"] = 8.0, ["caliperCount"] = 32, ["projectionWidth"] = 5, ["polarity"] = "darkToLight", ["maximumDeviation"] = 1.0 };
        d["nodes"]!.AsArray().Add(Node("fixture", "PositionFixture", new() { ["reference"] = reference.DeepClone() }));
        d["nodes"]!.AsArray().Add(Node("line", "FindLines", parameters));
        d["connections"]!.AsArray().Add(Edge("gray", "image", "fixture")); d["connections"]!.AsArray().Add(Edge("match", "pose", "fixture", "position"));
        d["connections"]!.AsArray().Add(Edge("fixture", "image", "line"));
        d["outputs"]!["Line"] = Output("line", "result", "Result");
        var image = Image(-40, .8, originX: ox, originY: oy); using var original = image.Frame;
        var at = Map(image.Pose, 110, 70); double radians = ((rotated ? 20 : 0) - 60) * Math.PI / 180;
        double tx = Math.Cos(radians), ty = Math.Sin(radians), nx = -ty, ny = tx;
        var pixels = original.CopyPixels();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            double along = (x + ox - at.X) * tx + (y + oy - at.Y) * ty, across = (x + ox - at.X) * nx + (y + oy - at.Y) * ny;
            if (Math.Abs(along) < 42 && Math.Abs(across) < 24) pixels[y * W + x] = (byte)Math.Clamp(125 + (across - 2) * 200, 20, 230);
        }
        using var frame = VisionFrame.CopyFrom(pixels, W, H, W, VisionPixelFormat.Gray8, ox, oy);
        using var result = await Execute(d, frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionLine>("Line"); var line = Assert.Single(packet.Features);
        Assert.InRange(Math.Abs((line.X1 - at.X) * nx + (line.Y1 - at.Y) * ny - 2), 0, .4);
        Assert.InRange(Math.Abs((line.X2 - at.X) * nx + (line.Y2 - at.Y) * ny - 2), 0, .4);
        var fixture = packet.Measurement.Fixture!; Assert.NotNull(fixture);
        var actualRoi = JPVision.Editor.Core.FixturePositioning.Resolve(roi, parameters, fixture, frame.Info);
        var actualParameters = JPVision.Editor.Core.FixturePositioning.Parameters(parameters, fixture);
        Assert.Equal("roiCenter", actualParameters["caliperPlacement"]!.GetValue<string>());
        var preview = JPVision.Editor.Core.CaliperLayout.Create(actualRoi, actualParameters, false, W, H, allowPartialCircle: true);
        var guides = packet.Measurement.Caliper!.Guides; Assert.Equal(32, guides.Count); Assert.Equal(32, preview.Count);
        for (int i = 0; i < guides.Count; i++)
        {
            Assert.Equal(preview[i].X + ox, guides[i].X, 6); Assert.Equal(preview[i].Y + oy, guides[i].Y, 6);
            Assert.Equal(preview[i].Height, guides[i].Height); Assert.Equal(preview[i].Width, guides[i].Width);
            Assert.Equal(preview[i].NormalX, guides[i].NormalX, 6); Assert.Equal(preview[i].NormalY, guides[i].NormalY, 6);
        }
    }
    [Theory]
    [InlineData("caliper")] [InlineData("automatic")] [InlineData("processor")] [InlineData("contours")]
    public async Task FullyOutsideFixtureSamplingIsUnavailableRatherThanAnExpectedZeroPass(string mode)
    {
        var reference = await Reference(); var d = Pipeline(reference, 0, 1);
        var p = d["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == "a")!["parameters"]!;
        p["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 10000.0, ["cy"] = 10000.0, ["radius"] = 20.0 };
        p["minimumCount"] = 0; p["maximumCount"] = 0;
        if (mode == "automatic") p["method"] = "automatic";
        if (mode == "processor")
        {
            d["nodes"]!.AsArray().Add(Node("outside", "ToGray", new() { ["roiReference"] = "fixture", ["roi"] = p["roi"]!.DeepClone() }));
            d["connections"]!.AsArray().First(e => e!["toNode"]!.GetValue<string>() == "a")!["fromNode"] = "outside";
            d["connections"]!.AsArray().Add(Edge("fixture", "image", "outside"));
        }
        d["kind"] = "inspection"; d["decisionOutput"] = "ZeroExpected"; d["outputs"]!["ZeroExpected"] = Output("a", "passed", "Bool");
        if (mode == "contours")
        {
            var nodes = d["nodes"]!.AsArray(); var edges = d["connections"]!.AsArray();
            nodes.Add(Node("mask", "Threshold")); nodes.Add(Node("outlines", "FindContours", new() { ["roiReference"] = "fixture", ["roi"] = p["roi"]!.DeepClone() }));
            nodes.Add(Node("count", "CountContours")); nodes.Add(Node("compare", "CompareNumber", new() { ["expected"] = 0 }));
            edges.Add(Edge("fixture", "image", "mask")); edges.Add(Edge("mask", "mask", "outlines")); edges.Add(Edge("outlines", "contours", "count")); edges.Add(Edge("count", "value", "compare"));
            d["outputs"]!["ZeroExpected"] = Output("compare", "value", "Bool");
        }
        var image = Image(0, 1); using var frame = image.Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.False(result.GetResult<VisionCircle>("A").Measurement.DataAvailable);
        Assert.Null(result.GetGeometryMeasurement("Distance").Value);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NoMatchCannotPassDownstreamZeroRuleButMatcherMayExplicitlyExpectZero(bool decideOnMatcher)
    {
        var reference = await Reference(); var d = Pipeline(reference, 0, 1);
        foreach (string id in new[] { "match", "a" })
        {
            var p = d["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!;
            p["minimumCount"] = 0; p["maximumCount"] = 0;
        }
        d["kind"] = "inspection"; d["decisionOutput"] = "ZeroExpected";
        d["outputs"]!["ZeroExpected"] = Output(decideOnMatcher ? "match" : "a", "passed", "Bool");
        var image = Image(0, 1, matched: false); using var frame = image.Frame; using var result = await Execute(d, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(decideOnMatcher ? InspectionDecision.Pass : InspectionDecision.Unknown, result.Decision);
        Assert.False(result.GetResult<VisionCircle>("A").Measurement.DataAvailable);
    }
}

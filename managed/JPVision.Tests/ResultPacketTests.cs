using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ResultPacketTests : IDisposable
{
    private readonly string store;
    public ResultPacketTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "result-packet-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Output(string node, string port, string type) => new() { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string node, string port, string target) => new() { ["fromNode"] = node, ["fromPort"] = port, ["toNode"] = target, ["toPort"] = "input" };
    private static JsonObject Definition(string type, JsonObject? p = null, bool oldPorts = false)
    {
        var outputs = new JsonObject { ["AllResults"] = Output("detect", "result", "Result") };
        if (oldPorts)
        {
            outputs["Features"] = Output("detect", "features", "Features"); outputs["Measurement"] = Output("detect", "measurement", "Measurement");
            outputs["Count"] = Output("detect", "count", "Int"); outputs["Found"] = Output("detect", "found", "Bool");
            outputs["Passed"] = Output("detect", "passed", "Bool"); outputs["Overlay"] = Output("detect", "overlay", "Overlay");
        }
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing", ["recipeId"] = "result-packet-tests", ["recipeVersion"] = 1,
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("detect", type, p)), ["connections"] = new JsonArray(Edge("source", "image", "detect")),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["outputs"] = outputs, ["decisionOutput"] = "", ["resources"] = new JsonArray()
        };
    }
    private string Save(JsonObject definition)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, RunRequest? request = null)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        return await session.RunAsync(frame, request ?? new() { Debug = false });
    }
    private static VisionFrame Circles(bool empty = false)
    {
        byte[] pixels = new byte[360 * 240];
        for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
            pixels[y * 360 + x] = !empty && ((x - 100) * (x - 100) + (y - 110) * (y - 110) <= 900 || (x - 250) * (x - 250) + (y - 125) * (y - 125) <= 900) ? (byte)230 : (byte)15;
        return VisionFrame.CopyFrom(pixels, 360, 240, 360, VisionPixelFormat.Gray8, 500, -20);
    }
    private static JsonObject CircleParameters() => new() { ["minimumRadius"] = 25, ["maximumRadius"] = 35, ["voteThreshold"] = 18, ["minimumCount"] = 2, ["maximumCount"] = 2, ["labelResults"] = true };
    private static byte Pattern(int x, int y) => (byte)(30 + (x * 13 + y * 17 + x * y * 3) % 200);
    private static JsonObject MatchingParameters()
    {
        byte[] model = new byte[48 * 40]; for (int y = 0; y < 40; y++) for (int x = 0; x < 48; x++) model[y * 48 + x] = Pattern(x, y);
        return new() { ["model"] = new JsonObject { ["width"] = 48, ["height"] = 40, ["grayBase64"] = Convert.ToBase64String(model) }, ["scoreThreshold"] = .96, ["searchDownsample"] = 1 };
    }
    private static VisionFrame MatchingImage(bool empty = false)
    {
        byte[] pixels = new byte[480 * 320];
        for (int y = 0; y < 320; y++) for (int x = 0; x < 480; x++)
            pixels[y * 480 + x] = !empty && x >= 61 && x < 109 && y >= 73 && y < 113 ? Pattern(x - 61, y - 73)
                : !empty && x >= 317 && x < 365 && y >= 211 && y < 251 ? Pattern(x - 317, y - 211) : (byte)12;
        return VisionFrame.CopyFrom(pixels, 480, 320, 480, VisionPixelFormat.Gray8, 300, -10);
    }
    [Fact]
    public async Task PublicPacketContainsAllCircleResultsWithoutDebugOrExtraModules()
    {
        using var frame = Circles(); using var result = await Run(Definition("FindCircles", CircleParameters(), oldPorts: true), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var packet = result.GetOutput<JsonElement>("AllResults");
        Assert.Equal("circle", packet.GetProperty("featureKind").GetString()); Assert.Equal(2, packet.GetProperty("count").GetInt32());
        Assert.True(packet.GetProperty("found").GetBoolean()); Assert.True(packet.GetProperty("passed").GetBoolean());
        var features = packet.GetProperty("features").EnumerateArray().ToArray(); Assert.Equal(2, features.Length);
        Assert.Contains(features, c => c.GetProperty("centerX").GetDouble() > 598 && c.GetProperty("centerX").GetDouble() < 602);
        Assert.All(features, c => { Assert.InRange(c.GetProperty("radius").GetDouble(), 25, 35); Assert.Equal(c.GetProperty("radius").GetDouble() * 2, c.GetProperty("diameter").GetDouble(), 8); });
        Assert.Equal(result.GetOutput<JsonElement>("Features").GetRawText(), packet.GetProperty("features").GetRawText());
        Assert.Equal(result.GetOutput<JsonElement>("Measurement").GetRawText(), packet.GetProperty("measurement").GetRawText());
        Assert.Equal(result.GetOutput<JsonElement>("Overlay").GetRawText(), packet.GetProperty("overlay").GetRawText());
        Assert.Equal(2, packet.GetProperty("overlay").EnumerateArray().Count(o => o.GetProperty("type").GetString() == "circle"));
        Assert.Equal(2, packet.GetProperty("overlay").EnumerateArray().Count(o => o.GetProperty("type").GetString() == "annulus"));
        Assert.Equal(packet.GetProperty("measurement").GetRawText(), result.Data.GetProperty("measurements").GetProperty("detect").GetRawText());
        Assert.False(result.Data.TryGetProperty("debugValues", out _));
        Assert.False(result.Data.GetProperty("nodeOutputs").GetProperty("detect").TryGetProperty("result", out _));
        Assert.Equal(2, result.GetOutput<int>("Count")); Assert.True(result.GetOutput<bool>("Found")); Assert.True(result.GetOutput<bool>("Passed"));
        var typed = result.GetResult<VisionCircle>("AllResults");
        Assert.Equal(2, typed.Count); Assert.Equal(2, typed.Features.Count); Assert.True(typed.Found); Assert.True(typed.Passed);
        for (int i = 0; i < features.Length; i++)
        {
            Assert.Equal(features[i].GetProperty("x").GetDouble(), typed.Features[i].X);
            Assert.Equal(features[i].GetProperty("y").GetDouble(), typed.Features[i].Y);
            Assert.Equal(features[i].GetProperty("radius").GetDouble(), typed.Features[i].Radius);
        }
        Assert.Equal(typed.Features[0].X, typed.Measurement.Selected!.X); Assert.Equal(typed.Features[0].Radius, typed.Measurement.Selected.Radius);
        Assert.Equal(packet.GetProperty("overlay").GetRawText(), typed.Overlay.GetRawText());
        Assert.Throws<KeyNotFoundException>(() => result.GetNodeResult<VisionCircle>("detect"));
    }
    [Theory]
    [InlineData("FastTemplateMatch", 2)] [InlineData("FastLocate", 1)]
    public async Task FastMatchingPacketsKeepScoresBoxesAndCorners(string type, int expected)
    {
        using var frame = MatchingImage(); using var result = await Run(Definition(type, MatchingParameters()), frame, new() { Debug = false, PreviewNodeId = "detect" });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var packet = result.GetOutput<JsonElement>("AllResults");
        Assert.Equal("match", packet.GetProperty("featureKind").GetString()); Assert.Equal(expected, packet.GetProperty("count").GetInt32());
        Assert.Equal(expected, packet.GetProperty("features").GetArrayLength());
        var overlays = packet.GetProperty("overlay").EnumerateArray().ToArray();
        Assert.Equal(expected, overlays.Count(o => o.GetProperty("type").GetString() == "polyline"));
        Assert.Equal(expected, overlays.Count(o => o.GetProperty("type").GetString() == "text"));
        foreach (var match in packet.GetProperty("features").EnumerateArray())
        {
            Assert.InRange(match.GetProperty("score").GetDouble(), .96, 1); Assert.Equal(48, match.GetProperty("width").GetDouble());
            Assert.Equal(40, match.GetProperty("height").GetDouble()); Assert.Equal(4, match.GetProperty("corners").GetArrayLength());
            Assert.InRange(match.GetProperty("x").GetDouble(), 361, 617); Assert.InRange(match.GetProperty("y").GetDouble(), 63, 201);
        }
        Assert.True(packet.GetProperty("measurement").GetProperty("selectedValid").GetBoolean());
        var typed = result.GetResult<VisionMatch>("AllResults"); var node = result.GetNodeResult<VisionMatch>("detect");
        Assert.Equal(expected, typed.Count); Assert.Equal(expected, node.Count); Assert.Equal(expected, typed.Features.Count); Assert.Equal(expected, node.Features.Count);
        for (int i = 0; i < expected; i++)
        {
            var raw = packet.GetProperty("features")[i]; var match = typed.Features[i]; var retained = node.Features[i];
            Assert.Equal(raw.GetProperty("x").GetDouble(), match.X); Assert.Equal(raw.GetProperty("y").GetDouble(), match.Y);
            Assert.Equal(raw.GetProperty("score").GetDouble(), match.Score); Assert.Equal(raw.GetProperty("centerX").GetDouble(), match.CenterX);
            Assert.Equal(raw.GetProperty("width").GetDouble(), match.Width); Assert.Equal(raw.GetProperty("height").GetDouble(), match.Height);
            Assert.Equal(match.X, retained.X); Assert.Equal(match.Y, retained.Y); Assert.Equal(match.Score, retained.Score);
            Assert.Equal(4, match.Corners.Count); Assert.Equal(4, retained.Corners.Count);
            for (int corner = 0; corner < 4; corner++)
            {
                Assert.Equal(raw.GetProperty("corners")[corner].GetProperty("x").GetDouble(), match.Corners[corner].X);
                Assert.Equal(raw.GetProperty("corners")[corner].GetProperty("y").GetDouble(), match.Corners[corner].Y);
                Assert.Equal(match.Corners[corner], retained.Corners[corner]);
            }
        }
        Assert.Equal(typed.Features[0].Score, typed.Measurement.Selected!.Score); Assert.Equal(typed.Measurement.Selected.Score, node.Measurement.Selected!.Score);
        Assert.Equal(packet.GetProperty("overlay").GetRawText(), typed.Overlay.GetRawText()); Assert.Equal(typed.Overlay.GetRawText(), node.Overlay.GetRawText());
    }
    [Fact]
    public async Task PacketIncludesBoundaryCalipersAndMeasuredEdgePoints()
    {
        var p = new JsonObject
        {
            ["method"] = "caliper", ["caliperPlacement"] = "roiBoundary", ["caliperCount"] = 24,
            ["searchLength"] = 24, ["projectionWidth"] = 5, ["polarity"] = "lightToDark",
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 100, ["cy"] = 110, ["radius"] = 30 }
        };
        using var frame = Circles(); using var result = await Run(Definition("FindCircles", p), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var packet = result.GetOutput<JsonElement>("AllResults");
        var circle = Assert.Single(packet.GetProperty("features").EnumerateArray()); Assert.Equal("caliper", circle.GetProperty("method").GetString());
        Assert.Equal(24, circle.GetProperty("edgePoints").GetArrayLength()); Assert.InRange(circle.GetProperty("radius").GetDouble(), 29, 31);
        var measurement = packet.GetProperty("measurement");
        Assert.Equal(24, measurement.GetProperty("caliper").GetProperty("guides").GetArrayLength());
        Assert.Equal("roiBoundary", measurement.GetProperty("caliper").GetProperty("placement").GetString());
        Assert.Equal(circle.GetRawText(), measurement.GetProperty("selected").GetRawText());
    }
    [Fact]
    public async Task DisablingMarksKeepsPacketMeasurementsAndFeatures()
    {
        var p = CircleParameters(); p["showOverlay"] = false;
        using var frame = Circles(); using var result = await Run(Definition("FindCircles", p), frame);
        var packet = result.GetOutput<JsonElement>("AllResults"); Assert.Equal(2, packet.GetProperty("count").GetInt32());
        Assert.Equal(2, packet.GetProperty("features").GetArrayLength()); Assert.True(packet.GetProperty("measurement").GetProperty("selectedValid").GetBoolean());
        Assert.Empty(packet.GetProperty("overlay").EnumerateArray());
    }
    [Theory]
    [InlineData("FindCircles")] [InlineData("FastTemplateMatch")] [InlineData("FastLocate")]
    public async Task EmptyPacketsDoNotInventGeometryOrOverlays(string type)
    {
        using var frame = type == "FindCircles" ? Circles(empty: true) : MatchingImage(empty: true);
        using var result = await Run(Definition(type, type == "FindCircles" ? CircleParameters() : MatchingParameters()), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); var packet = result.GetOutput<JsonElement>("AllResults");
        Assert.Equal(0, packet.GetProperty("count").GetInt32()); Assert.False(packet.GetProperty("found").GetBoolean()); Assert.False(packet.GetProperty("passed").GetBoolean());
        Assert.Empty(packet.GetProperty("features").EnumerateArray()); Assert.Empty(packet.GetProperty("overlay").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, packet.GetProperty("measurement").GetProperty("selected").ValueKind);
        Assert.False(packet.GetProperty("measurement").GetProperty("selectedValid").GetBoolean());
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ComplexNodePacketRetentionFollowsDebugOrSelectedPreview(bool debug)
    {
        using var frame = Circles(); using var result = await Run(Definition("FindCircles", CircleParameters()), frame, new() { Debug = debug, PreviewNodeId = debug ? "" : "detect" });
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(result.GetOutput<JsonElement>("AllResults").GetRawText(), result.GetNodeOutput<JsonElement>("detect", "result").GetRawText());
        var published = result.GetResult<VisionCircle>("AllResults"); var retained = result.GetNodeResult<VisionCircle>("detect");
        Assert.Equal(published.Count, retained.Count); Assert.Equal(2, retained.Features.Count);
        for (int i = 0; i < published.Count; i++)
        {
            Assert.Equal(published.Features[i].X, retained.Features[i].X); Assert.Equal(published.Features[i].Y, retained.Features[i].Y);
            Assert.Equal(published.Features[i].Radius, retained.Features[i].Radius);
        }
        Assert.Equal(published.Measurement.Selected!.X, retained.Measurement.Selected!.X);
        Assert.Equal(result.GetOutput<JsonElement>("AllResults").GetProperty("overlay").GetRawText(), retained.Overlay.GetRawText());
        Assert.Equal(published.Overlay.GetRawText(), retained.Overlay.GetRawText());
    }
    [Fact]
    public async Task ExistingSixTypedPortsAndTheirDownstreamConnectionsRemainCompatible()
    {
        var definition = Definition("FindCircles", CircleParameters(), oldPorts: true);
        definition["nodes"]!.AsArray().Add(Node("count", "CountFeatures")); definition["connections"]!.AsArray().Add(Edge("detect", "features", "count"));
        definition["nodes"]!.AsArray().Add(Node("judge", "CompareNumber", new() { ["expected"] = 2 })); definition["connections"]!.AsArray().Add(Edge("detect", "count", "judge"));
        definition["nodes"]!.AsArray().Add(Node("mark", "BuildFeatureOverlay")); definition["connections"]!.AsArray().Add(Edge("detect", "features", "mark"));
        definition["outputs"]!["OldCounter"] = Output("count", "value", "Int"); definition["outputs"]!["OldJudge"] = Output("judge", "value", "Bool"); definition["outputs"]!["OldMark"] = Output("mark", "overlay", "Overlay");
        definition["outputs"]!.AsObject().Remove("AllResults"); // No new port required by an old recipe.
        using var frame = Circles(); using var result = await Run(definition, frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(2, result.GetOutput<int>("OldCounter")); Assert.True(result.GetOutput<bool>("OldJudge"));
        var oldMarks = result.GetOverlays("OldMark");
        Assert.Equal(2, oldMarks.Count(o => o.Kind == "circle")); Assert.Equal(2, oldMarks.Count(o => o.Kind == "annulus"));
        Assert.Equal(2, result.GetCircles().Count);
        Assert.Equal(2, result.GetOverlays("Overlay").Count(o => o.Kind == "circle"));
        var bands = result.GetOverlays("Overlay").Where(o => o.Kind == "annulus").ToArray();
        Assert.Equal(2, bands.Length);
        Assert.All(bands, band => Assert.Equal(4, band.OuterRadius - band.InnerRadius, 8));
    }
    [Fact]
    public async Task FailedDownstreamNodeClearsPublicPacketsAndMeasurements()
    {
        var definition = Definition("FindCircles", CircleParameters());
        definition["nodes"]!.AsArray().Add(Node("bad", "CropRect", new() { ["x"] = 500, ["y"] = 0, ["width"] = 10, ["height"] = 10 }));
        definition["connections"]!.AsArray().Add(Edge("source", "image", "bad")); definition["outputs"]!["ZFailure"] = Output("bad", "image", "Image");
        using var frame = Circles(); using var result = await Run(definition, frame, new() { Debug = true });
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.Outputs.EnumerateObject()); Assert.Empty(result.Data.GetProperty("measurements").EnumerateObject()); Assert.Empty(result.Data.GetProperty("nodeOutputs").EnumerateObject());
    }
    [Theory]
    [InlineData("FindCircles", 0)]
    [InlineData("FindCircles", 1)]
    [InlineData("FindCircles", 3)]
    [InlineData("FindCircles", 4)]
    [InlineData("FindCircles", 6)]
    [InlineData("CircleCaliper", 0)]
    [InlineData("CircleCaliper", 4)]
    public async Task RemovingBiasedEdgePointsRefitsBeforeApplyingTheDeviationLimit(string type, int removalCount)
    {
        const int w = 360, h = 360; byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double dx = x - 180, dy = y - 180, angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            double radius = angle >= -2 && angle < 19 ? 95 : 64; // Four consecutive bad calipers bias the initial circle.
            pixels[y * w + x] = (byte)Math.Clamp((radius + .5 - Math.Sqrt(dx * dx + dy * dy)) * 230, 15, 230);
        }
        var p = new JsonObject { ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 180, ["cy"] = 180, ["radius"] = 78 },
            ["caliperCount"] = 64, ["projectionWidth"] = 3, ["searchLength"] = 90.0, ["removePointCount"] = removalCount,
            ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .8, ["polarity"] = "lightToDark", ["outputAnnulusWidth"] = 12.0 };
        if (type == "CircleCaliper") { p.Remove("method"); p.Remove("positionReference"); }
        using var frame = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80);
        using var result = await Run(Definition(type, p), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("AllResults");
        var raw = result.GetOutput<JsonElement>("AllResults").GetProperty("measurement").GetProperty("caliper");
        Assert.True(packet.Count == 1, "A valid circle was discarded: " + raw.GetRawText());
        var circle = Assert.Single(packet.Features);
        Assert.InRange(circle.X, 1079.8, 1080.2); Assert.InRange(circle.Y, 99.8, 100.2); Assert.InRange(circle.Radius, 63.8, 64.2);
        Assert.All(circle.EdgePoints, point => Assert.InRange(Math.Abs(Math.Sqrt(Math.Pow(point.X - circle.X, 2) + Math.Pow(point.Y - circle.Y, 2)) - circle.Radius), 0, 1.000001));
        int rejected = Math.Max(4, removalCount);
        Assert.Equal(64 - rejected, circle.EdgePoints.Count); Assert.Equal(rejected, packet.Measurement.Caliper!.RejectedCount);
        Assert.True(raw.GetProperty("fitSucceeded").GetBoolean()); Assert.Equal("", raw.GetProperty("fitFailureReason").GetString());
        Assert.True(packet.Measurement.Caliper.FitSucceeded); Assert.Equal("", packet.Measurement.Caliper.FitFailureReason);
        Assert.Equal(52, packet.Measurement.Caliper.MinimumRequiredPoints); Assert.Equal(removalCount, packet.Measurement.Caliper.RequestedRemovalCount);
        Assert.Equal(52, raw.GetProperty("minimumRequiredPoints").GetInt32());
        Assert.Single(packet.Overlay.EnumerateArray(), o => o.GetProperty("type").GetString() == "annulus");
    }
    [Theory]
    [InlineData(false, "InsufficientEdgePoints")]
    [InlineData(true, "InsufficientInliers")]
    public async Task AFailedCircleFitReportsItsReasonAndDoesNotPublishAnInventedRing(bool jagged, string reason)
    {
        const int w = 360, h = 360; byte[] pixels = new byte[w * h];
        if (jagged) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double dx = x - 180, dy = y - 180, angle = (Math.Atan2(dy, dx) + 2 * Math.PI) % (2 * Math.PI);
            double radius = (int)Math.Floor((angle + Math.PI / 64) / (Math.PI / 32)) % 2 == 0 ? 60 : 78;
            pixels[y * w + x] = (byte)Math.Clamp((radius + .5 - Math.Sqrt(dx * dx + dy * dy)) * 230, 15, 230);
        }
        var p = new JsonObject { ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 180, ["cy"] = 180, ["radius"] = 69 },
            ["caliperCount"] = 64, ["projectionWidth"] = 3, ["searchLength"] = 60.0, ["maximumDeviation"] = .5,
            ["minimumCoverage"] = .8, ["polarity"] = "lightToDark" };
        using var frame = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8);
        using var result = await Run(Definition("FindCircles", p), frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("AllResults"); Assert.Equal(0, packet.Count); Assert.False(packet.Found);
        Assert.Null(packet.Measurement.Selected); Assert.Empty(packet.Features);
        var raw = result.GetOutput<JsonElement>("AllResults").GetProperty("measurement").GetProperty("caliper");
        Assert.False(raw.GetProperty("fitSucceeded").GetBoolean()); Assert.Equal(reason, raw.GetProperty("fitFailureReason").GetString());
        Assert.False(packet.Measurement.Caliper!.FitSucceeded); Assert.Equal(reason, packet.Measurement.Caliper.FitFailureReason);
        Assert.Equal(52, raw.GetProperty("minimumRequiredPoints").GetInt32());
        Assert.Equal(jagged ? 64 : 0, raw.GetProperty("sampledCount").GetInt32());
        Assert.DoesNotContain(packet.Overlay.EnumerateArray(), o => o.GetProperty("type").GetString() is "annulus" or "circle");
    }
    [Theory]
    [InlineData(0.0)] [InlineData(16.0)]
    public async Task CaliperAnnulusAndLegacyMarkerUseTheCircleRefittedAfterPointRemoval(double bandWidth)
    {
        const int w = 360, h = 360; byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double dx = x - 180, dy = y - 180, angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            double distanceFromAxis = Math.Abs(Math.IEEERemainder(angle, 90));
            double radius = distanceFromAxis < 3 ? 74 : 64; // Four known protrusions at the sampled axes.
            pixels[y * w + x] = (byte)Math.Clamp((radius + .5 - Math.Sqrt(dx * dx + dy * dy)) * 230, 15, 230);
        }
        var p = new JsonObject { ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
            ["roi"] = new JsonObject { ["shape"] = "circle", ["cx"] = 180, ["cy"] = 180, ["radius"] = 68 },
            ["caliperCount"] = 40, ["projectionWidth"] = 3, ["searchLength"] = 40.0, ["removePointCount"] = 4,
            ["maximumDeviation"] = 20.0, ["polarity"] = "lightToDark", ["outputAnnulusWidth"] = bandWidth };
        var definition = Definition("FindCircles", p);
        definition["nodes"]!.AsArray().Add(Node("mark", "BuildFeatureOverlay")); definition["connections"]!.AsArray().Add(Edge("detect", "features", "mark"));
        definition["outputs"]!["Marker"] = Output("mark", "overlay", "Overlay");
        using var frame = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80);
        using var result = await Run(definition, frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var packet = result.GetResult<VisionCircle>("AllResults"); var circle = Assert.Single(packet.Features);
        Assert.InRange(circle.X, 1079.8, 1080.2); Assert.InRange(circle.Y, 99.8, 100.2); Assert.InRange(circle.Radius, 63.8, 64.2);
        Assert.Equal(36, circle.EdgePoints.Count); Assert.Equal(4, packet.Measurement.Caliper!.RejectedCount);
        Assert.InRange(circle.RmsError, 0, .2);
        Assert.All(packet.Measurement.Caliper.RejectedPoints, point => Assert.InRange(Math.Sqrt(Math.Pow(point.X - 1080, 2) + Math.Pow(point.Y - 100, 2)), 73, 75));
        var direct = packet.Overlay.EnumerateArray().Where(o => o.GetProperty("type").GetString() == "annulus").ToArray();
        var marked = result.GetOverlays("Marker").Where(o => o.Kind == "annulus").ToArray();
        if (bandWidth == 0) { Assert.Empty(direct); Assert.Empty(marked); return; }
        var band = Assert.Single(direct); var marker = Assert.Single(marked);
        Assert.Equal(circle.X, marker.X); Assert.Equal(circle.Y, marker.Y);
        Assert.Equal(circle.Radius - bandWidth / 2, marker.InnerRadius, 8); Assert.Equal(circle.Radius + bandWidth / 2, marker.OuterRadius, 8);
        Assert.Equal(band.GetProperty("innerRadius").GetDouble(), marker.InnerRadius, 8);
        Assert.Equal(band.GetProperty("outerRadius").GetDouble(), marker.OuterRadius, 8);
        Assert.Equal("#00FF00", marker.Color);
    }
    [Fact]
    public void CatalogExposesOneImagePortAndKeepsLegacyPortsAndInternalComponents()
    {
        var entries = VisionEngine.GetNodeCatalog().EnumerateArray().ToArray();
        string[] integrated = { "FindCircles", "FindLines", "CircleCaliper", "LineCaliper", "AnalyzeContours", "TemplateMatch", "FastTemplateMatch", "FastLocate" };
        foreach (string type in integrated)
        {
            var node = Assert.Single(entries, n => n.GetProperty("type").GetString() == type); var ports = node.GetProperty("outputs").EnumerateArray().ToArray();
            bool matching = type is "TemplateMatch" or "FastTemplateMatch" or "FastLocate";
            Assert.Equal(8 + (matching ? 1 : 0), ports.Length); Assert.Equal("features", ports[0].GetProperty("id").GetString());
            var visible = Assert.Single(ports, p => !p.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean());
            Assert.Equal("image", visible.GetProperty("id").GetString()); Assert.Equal("Image", visible.GetProperty("type").GetString()); Assert.Equal("图像与结果", visible.GetProperty("label").GetString());
            Assert.Equal(7 + (matching ? 1 : 0), ports.Count(p => p.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean()));
            Assert.True(Assert.Single(ports, p => p.GetProperty("id").GetString() == "result").GetProperty("hidden").GetBoolean());
            if (type is "TemplateMatch" or "FastTemplateMatch" or "FastLocate")
            {
                var padding = Assert.Single(node.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "downstreamPadding");
                Assert.Equal(0, padding.GetProperty("default").GetInt32()); Assert.Equal(0, padding.GetProperty("min").GetInt32()); Assert.Equal(2048, padding.GetProperty("max").GetInt32());
            }
        }
        string[] hiddenTypes = { "CropRect", "CircleCaliper", "CountFeatures", "BuildFeatureOverlay", "CompareNumber", "CountContours" };
        foreach (string type in hiddenTypes)
            Assert.False(Assert.Single(entries, n => n.GetProperty("type").GetString() == type).GetProperty("toolboxVisible").GetBoolean());
        Assert.True(Assert.Single(entries, n => n.GetProperty("type").GetString() == "FindCircles").GetProperty("toolboxVisible").GetBoolean());
        Assert.True(Assert.Single(entries, n => n.GetProperty("type").GetString() == "BuildContourOverlay").GetProperty("toolboxVisible").GetBoolean());
    }
    [Fact]
    public void PacketPortCannotBypassNativeConnectionTypeValidation()
    {
        var definition = Definition("FindCircles", CircleParameters()); definition["nodes"]!.AsArray().Add(Node("count", "CountFeatures")); definition["connections"]!.AsArray().Add(Edge("detect", "result", "count"));
        definition["outputs"]!["Count"] = Output("count", "value", "Int");
        using var engine = new VisionEngine(store); var error = Assert.Throws<VisionException>(() => engine.ValidateRecipe(VisionEngine.SealRecipe(definition.ToJsonString())));
        Assert.Contains("data types do not match", error.Message);
    }
    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

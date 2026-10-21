using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class EdgeReconstructionTests : IDisposable
{
    private readonly string root, store;

    public EdgeReconstructionTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) directory = directory.Parent;
        root = directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
        store = Path.Combine(root, "artifacts", "tests", "edge-reconstruction-" + Guid.NewGuid().ToString("N"));
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static (double X, double Y) At(double degrees, double tangent, double normal)
    {
        double radians = degrees * Math.PI / 180;
        return (200 + tangent * Math.Cos(radians) - normal * Math.Sin(radians),
            150 + tangent * Math.Sin(radians) + normal * Math.Cos(radians));
    }
    private static JsonObject Parameters(string mode = "reconstruct", double degrees = 0)
    {
        var center = At(degrees, 0, -25);
        return new()
        {
            ["roi"] = RoiRegion.RotatedRectangle(center.X, center.Y, 240, 70, degrees).ToJson(),
            ["sampleSpacing"] = 4.0, ["projectionWidth"] = 1, ["sigma"] = 1.0,
            ["edgeThreshold"] = 10.0, ["polarity"] = "darkToLight", ["minimumCoverage"] = .5,
            ["curveMode"] = mode, ["filterWindow"] = 11, ["outlierTolerance"] = 5.0,
            ["maximumGapLength"] = 80.0, ["smoothCurve"] = true
        };
    }
    private static JsonObject Definition(JsonObject? parameters = null, double degrees = 0, JsonObject? measurement = null)
    {
        var center = At(degrees, 0, 45);
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "edge-reconstruction", ["recipeVersion"] = 1,
            ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("points", "FindEdgePoints", parameters ?? Parameters(degrees: degrees)),
                Node("line", "FindLines", new()
                {
                    ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
                    ["roi"] = RoiRegion.RotatedRectangle(center.X, center.Y, 260, 22, degrees).ToJson(),
                    ["caliperCount"] = 40, ["projectionWidth"] = 5, ["sigma"] = 1.0,
                    ["polarity"] = "lightToDark", ["minimumCoverage"] = .8, ["maximumDeviation"] = 1.5
                }), Node("distance", "MeasurePointLineDistances", measurement)),
            ["connections"] = new JsonArray(Edge("source", "points"), Edge("source", "line"), Edge("points", "distance"), Edge("line", "distance", "line")),
            ["outputs"] = new JsonObject
            {
                ["Points"] = Output("points", "result", "Result"), ["PointImage"] = Output("points"),
                ["Distances"] = Output("distance", "result", "Result"), ["Image"] = Output("distance")
            }
        };
    }
    private string Save(JsonObject definition)
    {
        string directory = Path.Combine(store, Guid.NewGuid().ToString("N") + ".vrecipe"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString())); return directory;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static double Boundary(double tangent) => -25 + 8 * Math.Sin(tangent / 60);
    private static VisionFrame Wave(double degrees = 0, double gapHalfWidth = 0, bool spikes = false, bool endGap = false,
        bool blank = false, bool jitter = false, int originX = 700, int originY = -90)
    {
        const int width = 400, height = 300;
        var pixels = new byte[width * height]; double radians = degrees * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double tangent = (x - 200) * c + (y - 150) * s, normal = -(x - 200) * s + (y - 150) * c;
            double boundary = Boundary(tangent);
            if (spikes && (Math.Abs(tangent + 40) < 1.6 || Math.Abs(tangent - 32) < 1.6)) boundary += 20;
            if (jitter) boundary += ((int)Math.Floor((tangent + 120) / 4 + .001) & 1) == 0 ? 1.5 : -1.5;
            bool bright = normal >= boundary && normal <= 45;
            if (gapHalfWidth > 0 && Math.Abs(tangent) < gapHalfWidth || endGap && tangent < -90) bright = normal <= 45;
            pixels[y * width + x] = blank ? (byte)20 : bright ? (byte)230 : (byte)20;
        }
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, originX, originY);
    }
    private static JsonElement[] Points(JsonElement packet, string name = "points")
    {
        if (name == "rawPoints" && !packet.TryGetProperty(name, out _) && packet.GetProperty("curveMode").GetString() == "raw")
            return packet.GetProperty("points").EnumerateArray().ToArray();
        return packet.GetProperty(name).EnumerateArray().ToArray();
    }
    private static string Origin(JsonElement point) => point.TryGetProperty("origin", out var origin) ? origin.GetString()! : "measured";
    private static (double Tangent, double Normal) Local(JsonElement point, double degrees, int originX = 700, int originY = -90)
    {
        double radians = degrees * Math.PI / 180, x = point.GetProperty("x").GetDouble() - originX - 200,
            y = point.GetProperty("y").GetDouble() - originY - 150;
        return (x * Math.Cos(radians) + y * Math.Sin(radians), -x * Math.Sin(radians) + y * Math.Cos(radians));
    }
    private static void ConsistentCounts(JsonElement packet)
    {
        var points = Points(packet); var raw = Points(packet, "rawPoints");
        int measured = points.Count(p => Origin(p) == "measured"), interpolated = points.Length - measured;
        Assert.Equal(points.Length, packet.GetProperty("count").GetInt32());
        Assert.Equal(raw.Length, packet.GetProperty("rawCount").GetInt32());
        Assert.Equal(measured, packet.GetProperty("measuredCount").GetInt32());
        Assert.Equal(raw.Length - measured, packet.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(interpolated, packet.GetProperty("interpolatedCount").GetInt32());
        int stations = packet.GetProperty("stationCount").GetInt32();
        Assert.Equal(stations - points.Length, packet.GetProperty("missedCount").GetInt32());
        Assert.Equal(stations == 0 ? 0 : (double)measured / stations, packet.GetProperty("observedCoverage").GetDouble(), 10);
        Assert.Equal(stations == 0 ? 0 : (double)points.Length / stations, packet.GetProperty("coverage").GetDouble(), 10);
        Assert.Equal(Enumerable.Range(0, points.Length), points.Select(p => p.GetProperty("index").GetInt32()));
        Assert.Equal(points.Select(p => p.GetProperty("stationIndex").GetInt32()).OrderBy(i => i).Distinct(), points.Select(p => p.GetProperty("stationIndex").GetInt32()));
        foreach (var point in points.Where(p => Origin(p) == "interpolated"))
        {
            Assert.Equal(0, point.GetProperty("gradient").GetDouble());
            Assert.False(point.GetProperty("adjusted").GetBoolean());
            Assert.False(point.TryGetProperty("rawX", out var x) && x.ValueKind != JsonValueKind.Null);
            Assert.False(point.TryGetProperty("rawY", out var y) && y.ValueKind != JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task RawModeRemainsTheDefaultAndPreservesTheObservedSpikeCoordinates()
    {
        var p = Parameters(); p.Remove("curveMode");
        using var frame = Wave(spikes: true); using var result = await Run(Definition(p), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.Equal("raw", packet.GetProperty("curveMode").GetString()); Assert.Equal(0, packet.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(0, packet.GetProperty("interpolatedCount").GetInt32());
        Assert.True(Points(packet).Count(point => { var local = Local(point, 0); return Math.Abs(local.Normal - Boundary(local.Tangent)) > 12; }) >= 2);
        var raw = Points(packet, "rawPoints");
        Assert.Equal(raw.Select(p => p.GetProperty("x").GetDouble()), Points(packet).Select(p => p.GetProperty("x").GetDouble()));
        Assert.Equal(raw.Select(p => p.GetProperty("y").GetDouble()), Points(packet).Select(p => p.GetProperty("y").GetDouble()));
        var typed = result.GetEdgePoints("PointImage");
        Assert.True(typed.Valid); Assert.Same(typed.Points, typed.RawPoints);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(27.0)] [InlineData(90.0)] [InlineData(-35.0)]
    public async Task FilteringRejectsIsolatedSpikesWithoutFlatteningTheUnderlyingCurvedEdge(double degrees)
    {
        var p = Parameters("filter", degrees); p["smoothCurve"] = false;
        using var frame = Wave(degrees, spikes: true); using var result = await Run(Definition(p, degrees), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("valid").GetBoolean()); Assert.True(packet.GetProperty("rejectedCount").GetInt32() >= 2);
        Assert.Equal(0, packet.GetProperty("interpolatedCount").GetInt32());
        Assert.Contains(Points(packet, "rawPoints"), point => { var local = Local(point, degrees); return Math.Abs(local.Normal - Boundary(local.Tangent)) > 12; });
        foreach (var point in Points(packet))
        {
            var local = Local(point, degrees); Assert.InRange(Math.Abs(local.Normal - Boundary(local.Tangent)), 0, 2.0);
            Assert.False(point.GetProperty("adjusted").GetBoolean());
        }
        Assert.True(Points(packet).Max(point => Local(point, degrees).Normal) - Points(packet).Min(point => Local(point, degrees).Normal) > 12);
        Assert.True(result.GetEdgePoints("Image").Valid); Assert.True(result.GetPointLineDistances("Image").Valid);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(27.0)] [InlineData(90.0)]
    public async Task ReconstructionFillsOnlyInternalShortGapsAndKeepsPointProvenance(double degrees)
    {
        using var frame = Wave(degrees, gapHalfWidth: 8); using var result = await Run(Definition(degrees: degrees), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("valid").GetBoolean()); Assert.True(packet.GetProperty("interpolatedCount").GetInt32() >= 2);
        Assert.Contains(Points(packet), point => Origin(point) == "interpolated" && Math.Abs(Local(point, degrees).Tangent) < 5);
        foreach (var point in Points(packet))
        {
            var local = Local(point, degrees); Assert.InRange(Math.Abs(local.Normal - Boundary(local.Tangent)), 0, 2.0);
        }
        var measuredStations = Points(packet).Where(p => Origin(p) == "measured").Select(p => p.GetProperty("stationIndex").GetInt32()).ToHashSet();
        Assert.Equal(Points(packet, "rawPoints").Length, measuredStations.Count + packet.GetProperty("rejectedCount").GetInt32());
        Assert.All(result.GetOverlays("PointImage"), overlay => Assert.Equal("#00FF00", overlay.Color));
        using var output = result.GetOutputImage("PointImage"); Assert.Equal(frame.CopyPixels(), output.CopyPixels());
    }

    [Theory]
    [InlineData(0.0)] [InlineData(30.0)]
    public async Task ZeroOrTooSmallMaximumGapLeavesTheMissingStationsUnfilled(double maximumGap)
    {
        var p = Parameters(); p["maximumGapLength"] = maximumGap;
        using var frame = Wave(gapHalfWidth: 44); using var result = await Run(Definition(p), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("valid").GetBoolean()); Assert.Equal(0, packet.GetProperty("interpolatedCount").GetInt32());
        Assert.DoesNotContain(Points(packet), point => Math.Abs(Local(point, 0).Tangent) < 36);
    }

    [Fact]
    public async Task MissingEndpointsAreNotExtrapolatedEvenWhenTheMaximumGapIsLarge()
    {
        var p = Parameters(); p["maximumGapLength"] = 32768.0;
        using var frame = Wave(endGap: true); using var result = await Run(Definition(p), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("valid").GetBoolean()); Assert.Equal(0, packet.GetProperty("interpolatedCount").GetInt32());
        Assert.DoesNotContain(Points(packet), point => Local(point, 0).Tangent < -92);
    }

    [Fact]
    public async Task InterpolatedPointsDoNotRaiseTheObservedCoverageOrMakeASparseMeasurementValid()
    {
        var p = Parameters(); p["minimumCoverage"] = .98;
        using var frame = Wave(gapHalfWidth: 8); using var result = await Run(Definition(p), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("dataAvailable").GetBoolean()); Assert.True(packet.GetProperty("interpolatedCount").GetInt32() > 0);
        Assert.True(packet.GetProperty("coverage").GetDouble() >= .98); Assert.True(packet.GetProperty("observedCoverage").GetDouble() < .98);
        Assert.False(packet.GetProperty("valid").GetBoolean());
        var distance = result.GetPointLineDistances("Distances"); Assert.False(distance.Valid); Assert.Empty(distance.Distances);
        Assert.Null(distance.Maximum); Assert.Equal(InspectionDecision.Unknown, distance.Decision.Status);
    }

    [Fact]
    public async Task MeasurementDefaultsToRawQualifiedObservationsAndExplicitAllUsesTheDisplayedCurve()
    {
        using var frame = Wave(gapHalfWidth: 8, jitter: true);
        var definition = Definition();
        using var measured = await Run(definition, frame);
        var packet = measured.GetOutput<JsonElement>("Points"); var points = Points(packet);
        Assert.Contains(points, point => Origin(point) == "measured" && point.GetProperty("adjusted").GetBoolean());
        var defaultDistances = measured.GetOutput<JsonElement>("Distances");
        Assert.Equal("measuredOnly", defaultDistances.GetProperty("pointSource").GetString());
        Assert.Equal(packet.GetProperty("measuredCount").GetInt32(), defaultDistances.GetProperty("count").GetInt32());
        Assert.Equal(0, defaultDistances.GetProperty("interpolatedCount").GetInt32());
        foreach (var distance in defaultDistances.GetProperty("distances").EnumerateArray())
        {
            var point = points.Single(p => p.GetProperty("stationIndex").GetInt32() == distance.GetProperty("stationIndex").GetInt32());
            Assert.Equal("measured", Origin(distance)); Assert.False(distance.GetProperty("adjusted").GetBoolean());
            Assert.Equal(point.GetProperty("rawX").GetDouble(), distance.GetProperty("x").GetDouble(), 9);
            Assert.Equal(point.GetProperty("rawY").GetDouble(), distance.GetProperty("y").GetDouble(), 9);
        }
        definition["nodes"]![3]!["parameters"]!["pointSource"] = "all";
        using var all = await Run(definition, frame); var allPacket = all.GetOutput<JsonElement>("Points");
        var allDistances = all.GetOutput<JsonElement>("Distances");
        Assert.Equal(allPacket.GetProperty("count").GetInt32(), allDistances.GetProperty("count").GetInt32());
        Assert.Equal(allPacket.GetProperty("interpolatedCount").GetInt32(), allDistances.GetProperty("interpolatedCount").GetInt32());
        foreach (var distance in allDistances.GetProperty("distances").EnumerateArray())
        {
            var point = Points(allPacket).Single(p => p.GetProperty("stationIndex").GetInt32() == distance.GetProperty("stationIndex").GetInt32());
            Assert.Equal(Origin(point), Origin(distance));
            Assert.Equal(point.GetProperty("x").GetDouble(), distance.GetProperty("x").GetDouble(), 9);
            Assert.Equal(point.GetProperty("y").GetDouble(), distance.GetProperty("y").GetDouble(), 9);
        }
        Assert.True(measured.GetPointLineDistances("Image").Valid); Assert.True(all.GetPointLineDistances("Image").Valid);
    }

    [Fact]
    public async Task SmoothingReducesAlternatingPixelEdgeJitterInsteadOfOnlyReversingItsPhase()
    {
        const int width = 400, height = 300;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double boundary = 125 + (((int)Math.Floor((x - 80) / 4.0 + .001) & 1) == 0 ? 1.5 : -1.5);
            pixels[y * width + x] = y >= boundary && y <= 195 ? (byte)230 : (byte)20;
        }
        using var frame = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 700, -90);
        var p = Parameters("filter"); p["smoothCurve"] = true;
        using var result = await Run(Definition(p), frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(result.GetEdgePoints("Points").Valid);
        Assert.Equal(0, packet.GetProperty("rejectedCount").GetInt32());
        var raw = Points(packet, "rawPoints").ToDictionary(point => point.GetProperty("stationIndex").GetInt32());
        var interior = Points(packet).Where(point => point.GetProperty("stationIndex").GetInt32() is >= 6 and <= 54).ToArray();
        Assert.True(interior.Length >= 40);
        double JumpRms(Func<JsonElement, double> ordinate) => Math.Sqrt(interior.Skip(1)
            .Select((point, index) => Math.Pow(ordinate(point) - ordinate(interior[index]), 2)).Average());
        double rawRms = JumpRms(point => raw[point.GetProperty("stationIndex").GetInt32()].GetProperty("y").GetDouble());
        double smoothedRms = JumpRms(point => point.GetProperty("y").GetDouble());
        Assert.True(rawRms > 2.0, $"Synthetic image did not produce its intended measured jitter: {rawRms:R} px.");
        Assert.True(smoothedRms < rawRms * .5, $"Smoothing did not reduce measured jitter: raw RMS {rawRms:R} px, processed RMS {smoothedRms:R} px.");
        foreach (var point in interior)
        {
            var original = raw[point.GetProperty("stationIndex").GetInt32()];
            Assert.Equal(original.GetProperty("x").GetDouble(), point.GetProperty("rawX").GetDouble(), 9);
            Assert.Equal(original.GetProperty("y").GetDouble(), point.GetProperty("rawY").GetDouble(), 9);
        }
        using var output = result.GetOutputImage("PointImage"); Assert.Equal(frame.CopyPixels(), output.CopyPixels());
    }

    [Fact]
    public async Task CompletelyBlankFrameDoesNotReconstructOrReuseAnyPreviousCurve()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Definition())); await using var session = recipe.CreateSession();
        using var image = Wave(gapHalfWidth: 8); using var first = await session.RunAsync(image); Assert.True(first.GetEdgePoints("Points").Valid);
        using var blank = Wave(blank: true); using var absent = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, absent.Status); var packet = absent.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.Empty(Points(packet)); Assert.Empty(Points(packet, "rawPoints")); Assert.False(packet.GetProperty("valid").GetBoolean());
        var distance = absent.GetPointLineDistances("Distances"); Assert.False(distance.Valid); Assert.Empty(distance.Distances); Assert.Null(distance.Value);
        Assert.Empty(absent.GetOverlays("Image"));
        using var again = await session.RunAsync(image); Assert.True(again.GetEdgePoints("Points").Valid);
    }

    [Fact]
    public async Task CroppingAddsItsOriginExactlyOnceToMeasuredAndReconstructedCoordinates()
    {
        var p = Parameters(); p["roi"] = RoiRegion.RotatedRectangle(160, 75, 240, 70, 0).ToJson();
        var definition = Definition(p);
        definition["nodes"]!.AsArray().Add(Node("crop", "CropRect", new() { ["x"] = 40, ["y"] = 50, ["width"] = 320, ["height"] = 220 }));
        foreach (var connection in definition["connections"]!.AsArray())
            if (connection!["toNode"]!.GetValue<string>() == "points") connection["fromNode"] = "crop";
        definition["connections"]!.AsArray().Add(Edge("source", "crop"));
        using var frame = Wave(gapHalfWidth: 8); using var result = await Run(definition, frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(packet.GetProperty("interpolatedCount").GetInt32() > 0); Assert.True(result.GetEdgePoints("Points").Valid);
        foreach (var point in Points(packet))
        {
            var local = Local(point, 0); Assert.InRange(Math.Abs(local.Normal - Boundary(local.Tangent)), 0, 2.0);
        }
        Assert.All(result.GetPointLineDistances("Distances").Distances, item => Assert.InRange(item.ProjectionY, 104, 106));
    }

    [Fact]
    public async Task ReconstructedPointsNeverBridgeAConcaveRoiMaskHole()
    {
        var p = Parameters(); p["roi"] = RoiRegion.RotatedRectangle(140, 55, 240, 70, 0).ToJson();
        var definition = Definition(p);
        var mask = RoiRegion.Polygon(new EditorPoint[] { new(60, 70), new(340, 70), new(340, 210), new(210, 210), new(210, 100), new(190, 100), new(190, 210), new(60, 210) });
        definition["nodes"]!.AsArray().Add(Node("mask", "ToGray", new() { ["roi"] = mask.ToJson() }));
        foreach (var connection in definition["connections"]!.AsArray())
            if (connection!["toNode"]!.GetValue<string>() == "points") connection["fromNode"] = "mask";
        definition["connections"]!.AsArray().Add(Edge("source", "mask"));
        using var frame = Wave(); using var result = await Run(definition, frame);
        var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.True(result.GetEdgePoints("Points").Valid);
        Assert.DoesNotContain(Points(packet), point => point.GetProperty("x").GetDouble() > 892 && point.GetProperty("x").GetDouble() < 908);
    }

    [Fact]
    public async Task AnnularUnwrappedCoordinatesAndPlaneAreRetainedDuringGapReconstruction()
    {
        const int size = 300; var pixels = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = x - 150, dy = y - 150, radius = Math.Sqrt(dx * dx + dy * dy), angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (angle < 0) angle += 360;
            bool bright = radius is >= 80 and <= 110;
            if (angle > 170 && angle < 190) bright = radius <= 110;
            pixels[y * size + x] = bright ? (byte)230 : (byte)20;
        }
        using var frame = VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8, 700, -90);
        var p = Parameters(); p["roi"] = RoiRegion.RotatedRectangle(180, 40, 240, 60, 0).ToJson();
        var definition = Definition(p);
        definition["nodes"]!.AsArray().Add(Node("unwrap", "AnnularUnwrap", new()
        { ["roi"] = RoiRegion.Annulus(150, 150, 60, 130).ToJson(), ["outputWidth"] = 360, ["outputHeight"] = 140, ["interpolation"] = "nearest" }));
        foreach (var connection in definition["connections"]!.AsArray())
            if (connection!["toNode"]!.GetValue<string>() == "points") connection["fromNode"] = "unwrap";
        definition["connections"]!.AsArray().Add(Edge("source", "unwrap"));
        // The original reference line belongs to another plane, so publish only the point branch here.
        definition["nodes"]!.AsArray().RemoveAt(3); definition["nodes"]!.AsArray().RemoveAt(2);
        var connections = definition["connections"]!.AsArray();
        foreach (var connection in connections.Where(edge => edge!["toNode"]!.GetValue<string>() is "line" or "distance").ToArray()) connections.Remove(connection);
        definition["outputs"] = new JsonObject { ["Points"] = Output("points", "result", "Result"), ["PointImage"] = Output("points") };
        using var result = await Run(definition, frame); var packet = result.GetOutput<JsonElement>("Points"); ConsistentCounts(packet);
        Assert.Equal("annularUnwrapped", packet.GetProperty("coordinateSpace").GetString()); Assert.False(string.IsNullOrWhiteSpace(packet.GetProperty("coordinatePlane").GetString()));
        Assert.True(result.GetEdgePoints("PointImage").Valid); Assert.True(packet.GetProperty("interpolatedCount").GetInt32() >= 2);
        Assert.All(Points(packet), point => Assert.InRange(point.GetProperty("y").GetDouble(), 36, 42));
    }

    [Theory]
    [InlineData("filterWindow", 2)] [InlineData("filterWindow", 4)] [InlineData("filterWindow", 103)]
    [InlineData("outlierTolerance", 0)] [InlineData("outlierTolerance", 32769)]
    [InlineData("maximumGapLength", -1)] [InlineData("maximumGapLength", 32769)]
    public void InvalidReconstructionParametersAreRejectedBeforeSampling(string name, double value)
    {
        var p = Parameters(); p[name] = name == "filterWindow" ? JsonValue.Create((int)value) : JsonValue.Create(value);
        using var engine = new VisionEngine(store);
        Assert.ThrowsAny<Exception>(() => engine.LoadRecipe(Save(Definition(p))));
    }

    public void Dispose()
    {
        string path = Path.GetFullPath(store);
        if (!string.Equals(Path.GetDirectoryName(path), Path.Combine(root, "artifacts", "tests"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("edge-reconstruction-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture directory.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}

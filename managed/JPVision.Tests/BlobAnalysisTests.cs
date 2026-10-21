using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class BlobAnalysisTests : IDisposable
{
    private readonly string store;
    public BlobAnalysisTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "blob-analysis-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }
    private static JsonObject Node(string id, string type, JsonObject? p = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
    private static JsonObject Edge(string from, string to) => new()
    { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = "input" };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonObject? parameters = null) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "blob-analysis", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("blob", "BlobAnalysis", parameters)),
        ["connections"] = new JsonArray(Edge("source", "blob")),
        ["outputs"] = new JsonObject { ["Image"] = Output("blob"), ["Result"] = Output("blob", "result", "Result") }
    };
    private string Save(JsonObject recipe)
    {
        string path = Path.Combine(store, Guid.NewGuid() + ".vrecipe"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(recipe.ToJsonString())); return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, bool requireSuccess = true)
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); var result = await session.RunAsync(frame);
        if (requireSuccess) Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText()); return result;
    }
    private static VisionFrame Gray(int width, int height, Func<int, int, byte> pixel, int originX = 700, int originY = -90)
    {
        var bytes = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bytes[y * width + x] = pixel(x, y);
        return VisionFrame.CopyFrom(bytes, width, height, width, VisionPixelFormat.Gray8, originX, originY);
    }
    private static bool Ring(int x, int y) => x >= 10 && x < 17 && y >= 12 && y < 19 && !(x >= 12 && x < 15 && y >= 14 && y < 17);
    private static byte Objects(int x, int y, bool border = false) =>
        x >= 5 && x < 8 && y >= 5 && y < 8 || x >= 20 && x < 28 && y >= 10 && y < 12 ||
        x >= 40 && x < 45 && y >= 20 && y < 25 ||
        x >= 10 && x < 17 && y >= 30 && y < 37 && !(x >= 12 && x < 15 && y >= 32 && y < 35) ||
        border && x < 2 && y < 2 ? (byte)230 : (byte)20;
    private static JsonElement Packet(InspectionResult result) => result.GetOutput<JsonElement>("Result");
    private static IReadOnlyList<VisionRegion> Regions(InspectionResult result) => result.GetResult<VisionRegion>("Result").Features;

    [Fact]
    public async Task BlobAreaCountsForegroundPixelsAndKeepsHolesOutOfTheMeasuredArea()
    {
        using var frame = Gray(64, 48, (x, y) => x >= 10 && x < 17 && y >= 12 && y < 19 &&
            !(x >= 12 && x < 15 && y >= 14 && y < 17) ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(), frame);
        var blob = Assert.Single(result.GetResult<VisionRegion>("Result").Features);
        Assert.Equal(40, blob.Area); Assert.Equal(713, blob.CentroidX); Assert.Equal(-75, blob.CentroidY);
        Assert.Equal(710, blob.X); Assert.Equal(-78, blob.Y); Assert.Equal(7, blob.Width); Assert.Equal(7, blob.Height);
        Assert.Equal(40, blob.Perimeter); Assert.Equal(4 * Math.PI * 40 / (40 * 40), blob.Circularity, 8);
        var feature = Packet(result).GetProperty("features")[0];
        Assert.Equal("pixelCount", feature.GetProperty("areaKind").GetString()); Assert.Equal(40, feature.GetProperty("pixelCount").GetDouble());
        Assert.Equal(40.0 / 49, feature.GetProperty("extent").GetDouble(), 8); Assert.Equal(1, feature.GetProperty("aspectRatio").GetDouble());
        Assert.Equal("pixelEdges", Packet(result).GetProperty("perimeterMethod").GetString());
        var typed = result.GetBlobAnalysis("Result"); var attached = result.GetBlobAnalysis("Image");
        Assert.True(typed.DataAvailable); Assert.Equal(1, typed.Count); Assert.Equal(1, typed.CandidateCount); Assert.Equal(0, typed.RejectedCount);
        Assert.Equal(40, typed.TotalArea); Assert.Equal("pixelEdges", typed.PerimeterMethod); Assert.Equal("original", typed.CoordinateSpace);
        Assert.Equal(typed.Features, attached.Features); Assert.Equal(typed.TotalArea, attached.TotalArea);
        Assert.Equal("pixelCount", typed.Features[0].AreaKind); Assert.Equal(40.0 / 49, typed.Features[0].Extent!.Value, 8);
        Assert.Equal(1, typed.Features[0].AspectRatio); Assert.False(typed.Features[0].TouchesBoundary);
    }

    [Fact]
    public async Task ASingleForegroundPixelHasARealAreaAndCentroidInsteadOfAZeroContourArea()
    {
        using var frame = Gray(32, 24, (x, y) => x == 9 && y == 7 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(), frame); var blob = Assert.Single(Regions(result));
        Assert.Equal(1, blob.Area); Assert.Equal(4, blob.Perimeter); Assert.Equal(709, blob.CentroidX); Assert.Equal(-83, blob.CentroidY);
        Assert.Equal(1, blob.Width); Assert.Equal(1, blob.Height); Assert.True(blob.CentroidValid);
        Assert.Equal(Math.PI / 4, blob.Circularity, 8);
    }

    [Theory]
    [InlineData(4, 2)] [InlineData(8, 1)]
    public async Task ConnectivityControlsWhetherDiagonalNeighborsBelongToOneBlob(int connectivity, int count)
    {
        using var frame = Gray(32, 24, (x, y) => x >= 10 && x < 12 && y >= 10 && y < 12 || x >= 12 && x < 14 && y >= 12 && y < 14 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(new() { ["connectivity"] = connectivity }), frame); var blobs = Regions(result);
        Assert.Equal(count, blobs.Count); Assert.Equal(8, blobs.Sum(blob => blob.Area));
        if (connectivity == 8)
        {
            var blob = Assert.Single(blobs); Assert.Equal(711.5, blob.CentroidX); Assert.Equal(-78.5, blob.CentroidY);
            Assert.Equal(4, blob.Width); Assert.Equal(4, blob.Height); Assert.Equal(16, blob.Perimeter);
        }
        else Assert.All(blobs, blob => { Assert.Equal(4, blob.Area); Assert.Equal(8, blob.Perimeter); });
    }

    [Theory]
    [InlineData("fixed")] [InlineData("otsu")] [InlineData("triangle")]
    public async Task InternalThresholdMethodsSegmentGrayPixelsWithoutNeedingASeparateThresholdNode(string method)
    {
        using var frame = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(new() { ["imageThresholdMethod"] = method }), frame);
        Assert.Equal(40, Assert.Single(Regions(result)).Area);
    }

    [Fact]
    public async Task DarkObjectsCanBeSegmentedByInvertingTheInternalThreshold()
    {
        using var frame = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)20 : (byte)230);
        using var result = await Run(Recipe(new() { ["imageInvert"] = true }), frame);
        Assert.Equal(40, Assert.Single(Regions(result)).Area);
    }

    [Fact]
    public async Task BgrInputKeepsItsCleanPixelsWhileGreenBlobAnnotationsRemainSeparate()
    {
        const int width = 64, height = 48; var bytes = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 3; bool bright = Ring(x, y);
            bytes[offset] = bright ? (byte)230 : (byte)10; bytes[offset + 1] = bright ? (byte)220 : (byte)20; bytes[offset + 2] = bright ? (byte)210 : (byte)30;
        }
        using var frame = VisionFrame.CopyFrom(bytes, width, height, width * 3, VisionPixelFormat.Bgr24, 700, -90);
        using var result = await Run(Recipe(), frame); Assert.Equal(40, Assert.Single(Regions(result)).Area);
        using var image = result.GetOutputImage("Image"); Assert.Equal(VisionPixelFormat.Bgr24, image.Info.PixelFormat);
        Assert.Equal(bytes, image.CopyPixels()); var overlays = result.GetOverlays("Image"); Assert.NotEmpty(overlays);
        Assert.All(overlays, overlay => Assert.Equal("#00FF00", overlay.Color));
        Assert.Single(result.GetRegions("Image"));
    }

    [Fact]
    public async Task AnIncomingBinaryMaskIsNotThresholdedAgainEvenAtAnInternalThresholdOf255()
    {
        var definition = Recipe(new() { ["imageThreshold"] = 255 });
        definition["nodes"]!.AsArray().Add(Node("binary", "Threshold", new() { ["value"] = 128 }));
        definition["connections"] = new JsonArray(Edge("source", "binary"), Edge("binary", "blob"));
        using var frame = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)230 : (byte)20);
        using var result = await Run(definition, frame); Assert.Equal(40, Assert.Single(Regions(result)).Area);
        using var image = result.GetOutputImage("Image"); Assert.All(image.CopyPixels(), value => Assert.True(value is 0 or 255));
    }

    [Theory]
    [InlineData("area")] [InlineData("width")] [InlineData("height")]
    [InlineData("circularity")] [InlineData("aspectRatio")] [InlineData("extent")]
    public async Task InclusiveShapeFiltersSelectMeasuredPixelRegions(string filter)
    {
        var p = new JsonObject(); double[] expected;
        switch (filter)
        {
            case "area": p["minimumArea"] = 16.0; p["maximumArea"] = 25.0; expected = new[] { 25.0, 16.0 }; break;
            case "width": p["minimumWidth"] = 8; p["maximumWidth"] = 8; expected = new[] { 16.0 }; break;
            case "height": p["minimumHeight"] = 2; p["maximumHeight"] = 2; expected = new[] { 16.0 }; break;
            case "circularity": p["minimumCircularity"] = .7; expected = new[] { 25.0, 9.0 }; break;
            case "aspectRatio": p["minimumAspectRatio"] = 4.0; p["maximumAspectRatio"] = 4.0; expected = new[] { 16.0 }; break;
            default: p["maximumExtent"] = .9; expected = new[] { 40.0 }; break;
        }
        using var frame = Gray(64, 48, (x, y) => Objects(x, y)); using var result = await Run(Recipe(p), frame);
        Assert.Equal(expected, Regions(result).Select(blob => blob.Area));
        Assert.Equal(4, Packet(result).GetProperty("candidateCount").GetInt32());
        Assert.Equal(4 - expected.Length, Packet(result).GetProperty("rejectedCount").GetInt32());
        Assert.Equal(expected.Sum(), Packet(result).GetProperty("totalArea").GetDouble());
    }

    [Theory]
    [InlineData("areaDescending", "10,40,20,5")] [InlineData("areaAscending", "5,20,40,10")]
    [InlineData("xAscending", "5,10,20,40")] [InlineData("yAscending", "5,20,40,10")]
    [InlineData("labelAscending", "5,20,40,10")]
    public async Task SortingAndSelectedIndexReferToTheSameOrderedBlobList(string order, string xCoordinates)
    {
        using var frame = Gray(64, 48, (x, y) => Objects(x, y));
        using var result = await Run(Recipe(new() { ["sortOrder"] = order, ["resultIndex"] = 2 }), frame);
        var packet = result.GetResult<VisionRegion>("Result"); Assert.Equal(xCoordinates.Split(',').Select(int.Parse), packet.Features.Select(blob => blob.X - 700));
        Assert.Equal(4, packet.Count); Assert.Equal(2, packet.Measurement.SelectedIndex); Assert.Equal(packet.Features[2], packet.Measurement.Selected);
    }

    [Fact]
    public async Task BorderExclusionRemovesOnlyBlobsThatTouchTheImageValidityBoundary()
    {
        using var frame = Gray(64, 48, (x, y) => Objects(x, y, border: true));
        using var all = await Run(Recipe(), frame); Assert.Equal(5, Regions(all).Count);
        Assert.Single(Packet(all).GetProperty("features").EnumerateArray(), blob => blob.GetProperty("touchesBoundary").GetBoolean());
        using var filtered = await Run(Recipe(new() { ["excludeBorder"] = true }), frame);
        Assert.Equal(4, Regions(filtered).Count); Assert.Equal(1, Packet(filtered).GetProperty("rejectedCount").GetInt32());
    }

    [Fact]
    public async Task PartialCircularRoiCountsOnlyValidPixelsAndPreservesTheirAbsoluteCentroid()
    {
        var p = new JsonObject { ["roi"] = RoiRegion.Circle(0, 0, 10).ToJson() };
        using var frame = Gray(32, 32, (_, _) => 230); using var result = await Run(Recipe(p), frame);
        var blob = Assert.Single(Regions(result)); var samples = (from y in Enumerable.Range(0, 11) from x in Enumerable.Range(0, 11) where x * x + y * y <= 100 select (X: x, Y: y)).ToArray();
        Assert.Equal(90, samples.Length); Assert.Equal(samples.Length, blob.Area);
        Assert.Equal(700 + samples.Average(point => point.X), blob.CentroidX, 8); Assert.Equal(-90 + samples.Average(point => point.Y), blob.CentroidY, 8);
        Assert.True(Packet(result).GetProperty("features")[0].GetProperty("touchesBoundary").GetBoolean());
    }

    [Fact]
    public async Task PolygonValidityIsAppliedToThePixelAreaInsteadOfItsBoundingRectangle()
    {
        var roi = RoiRegion.Polygon(new[] { new EditorPoint(0, 0), new(20, 0), new(0, 20) });
        using var frame = Gray(32, 32, (_, _) => 230); using var result = await Run(Recipe(new() { ["roi"] = roi.ToJson() }), frame);
        Assert.Equal(231, Assert.Single(Regions(result)).Area);
    }

    [Fact]
    public async Task AnUnavailableRoiReturnsUnavailableResultsAndCannotInspectTheFullImage()
    {
        using var frame = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(new() { ["roi"] = RoiRegion.Circle(500, 500, 30).ToJson() }), frame);
        var packet = result.GetResult<VisionRegion>("Result"); Assert.Empty(packet.Features); Assert.False(packet.Measurement.DataAvailable);
        Assert.Null(packet.Measurement.Selected); Assert.Equal(JsonValueKind.Null, Packet(result).GetProperty("totalArea").ValueKind);
        Assert.False(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean()); Assert.Empty(result.GetOverlays("Image"));
        var typed = result.GetBlobAnalysis("Image"); Assert.False(typed.DataAvailable); Assert.Null(typed.TotalArea); Assert.Empty(typed.Features);
        Assert.False(string.IsNullOrWhiteSpace(typed.Reason)); Assert.Null(typed.Measurement.Selected);
    }

    [Fact]
    public async Task CroppedInputProducesPixelStatisticsInTheOriginalImageCoordinatePlane()
    {
        var definition = Recipe(new() { ["roi"] = RoiRegion.Rectangle(0, 0, 50, 30).ToJson() });
        definition["nodes"]!.AsArray().Add(Node("crop", "CropRect", new() { ["x"] = 20, ["y"] = 10, ["width"] = 80, ["height"] = 60 }));
        definition["connections"] = new JsonArray(Edge("source", "crop"), Edge("crop", "blob"));
        using var frame = Gray(120, 90, (x, y) => x >= 30 && x < 40 && y >= 20 && y < 25 ? (byte)230 : (byte)20);
        using var result = await Run(definition, frame); var packet = result.GetResult<VisionRegion>("Result"); var blob = Assert.Single(packet.Features);
        Assert.Equal("original", packet.CoordinateSpace); Assert.Equal(50, blob.Area); Assert.Equal(734.5, blob.CentroidX); Assert.Equal(-68, blob.CentroidY);
        Assert.Equal(730, blob.X); Assert.Equal(-70, blob.Y); Assert.Equal(10, blob.Width); Assert.Equal(5, blob.Height);
    }

    [Fact]
    public async Task BlobsAfterUnwrappingUseTheExpandedPlaneRatherThanPretendingToUseOriginalPixels()
    {
        var definition = Recipe(); definition["nodes"]!.AsArray().Add(Node("unwrap", "AnnularUnwrap", new()
            { ["roi"] = RoiRegion.Annulus(32, 32, 10, 20).ToJson(), ["outputWidth"] = 32, ["outputHeight"] = 11 }));
        definition["connections"] = new JsonArray(Edge("source", "unwrap"), Edge("unwrap", "blob"));
        definition["outputs"]!["Expanded"] = Output("unwrap");
        using var frame = Gray(65, 65, (_, _) => 230); using var result = await Run(definition, frame);
        var packet = result.GetResult<VisionRegion>("Result"); var blob = Assert.Single(packet.Features);
        Assert.Equal("annularUnwrapped", packet.CoordinateSpace); Assert.False(string.IsNullOrEmpty(packet.CoordinatePlane));
        Assert.Equal(result.GetOutput<JsonElement>("Expanded").GetProperty("coordinatePlane").GetString(), packet.CoordinatePlane);
        Assert.Equal(352, blob.Area); Assert.Equal(0, blob.X); Assert.Equal(0, blob.Y); Assert.Equal(15.5, blob.CentroidX); Assert.Equal(5, blob.CentroidY);
        var typed = result.GetBlobAnalysis("Image"); Assert.Equal(packet.CoordinateSpace, typed.CoordinateSpace); Assert.Equal(packet.CoordinatePlane, typed.CoordinatePlane);
    }

    [Fact]
    public async Task EmptyFramesClearPreviousBlobsSelectionsAndAnnotationsWithinTheSameSession()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(Recipe())); await using var session = recipe.CreateSession();
        using var good = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)230 : (byte)20); using var blank = Gray(64, 48, (_, _) => 20);
        using (var first = await session.RunAsync(good)) Assert.Equal(40, Assert.Single(Regions(first)).Area);
        using (var absent = await session.RunAsync(blank))
        {
            Assert.Equal(ExecutionStatus.Succeeded, absent.Status); var packet = absent.GetResult<VisionRegion>("Result");
            Assert.True(packet.Measurement.DataAvailable); Assert.Equal(0, packet.Count); Assert.Empty(packet.Features); Assert.Null(packet.Measurement.Selected);
            Assert.Equal(0, Packet(absent).GetProperty("candidateCount").GetInt32()); Assert.Equal(0, Packet(absent).GetProperty("totalArea").GetDouble());
            Assert.Empty(absent.GetOverlays("Image"));
            var typed = absent.GetBlobAnalysis("Image"); Assert.True(typed.DataAvailable); Assert.Equal(0, typed.TotalArea); Assert.Empty(typed.Features);
        }
        using var restored = await session.RunAsync(good); Assert.Equal(40, Assert.Single(Regions(restored)).Area);
    }

    [Theory]
    [InlineData(4, 4, true)] [InlineData(5, 8, false)] [InlineData(1, 3, false)]
    public async Task CountJudgmentRemainsSeparateFromTheMeasuredBlobList(int minimum, int maximum, bool passed)
    {
        using var frame = Gray(64, 48, (x, y) => Objects(x, y));
        using var result = await Run(Recipe(new() { ["minimumCount"] = minimum, ["maximumCount"] = maximum }), frame);
        var packet = result.GetResult<VisionRegion>("Result"); Assert.Equal(4, packet.Count); Assert.Equal(passed, packet.Passed);
        Assert.Equal(passed, packet.Measurement.Passed); Assert.Equal(4, packet.Features.Count);
    }

    [Theory]
    [InlineData(201, 1)] [InlineData(643, 2)]
    public async Task BlobLimitsRejectExcessiveCandidatesOrResultsWithoutSilentlyTruncatingThem(int dimension, int minimumArea)
    {
        using var frame = Gray(dimension, dimension, (x, y) => x % 2 == 0 && y % 2 == 0 ? (byte)230 : (byte)20);
        using var result = await Run(Recipe(new() { ["minimumArea"] = (double)minimumArea, ["showOverlay"] = false }), frame, requireSuccess: false);
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(5, result.Data.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(result.Outputs.EnumerateObject());
    }

    [Theory]
    [InlineData("Area")] [InlineData("Width")] [InlineData("Height")]
    [InlineData("Circularity")] [InlineData("AspectRatio")] [InlineData("Extent")]
    public void ReversedFilterIntervalsAreRejectedBeforeImageProcessing(string field)
    {
        var p = new JsonObject { ["minimum" + field] = field is "Width" or "Height" ? JsonValue.Create(3) : JsonValue.Create(.9),
            ["maximum" + field] = field is "Width" or "Height" ? JsonValue.Create(2) : JsonValue.Create(.5) };
        if (field == "Area") { p["minimumArea"] = 3.0; p["maximumArea"] = 2.0; }
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe(p))));
    }

    [Fact]
    public void ConnectivityOtherThanFourOrEightIsNotSilentlyReinterpreted()
    {
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(Recipe(new() { ["connectivity"] = 6 }))));
    }

    [Fact]
    public async Task BlobStatisticsRemainAvailableThroughTheImagePipelineAndLegacyFeaturePort()
    {
        var definition = Recipe(); definition["nodes"]!.AsArray().Add(Node("gray", "ToGray"));
        definition["connections"]!.AsArray().Add(Edge("blob", "gray")); definition["outputs"]!["Image"] = Output("gray");
        definition["outputs"]!["Features"] = Output("blob", "features", "Features");
        using var frame = Gray(64, 48, (x, y) => Ring(x, y) ? (byte)230 : (byte)20); using var result = await Run(definition, frame);
        var typed = result.GetBlobAnalysis("Image"); var legacy = result.GetResult<VisionRegion>("Result");
        Assert.Equal(legacy.Features, typed.Features); Assert.Equal(typed.Features, result.GetRegions("Features"));
        Assert.Equal("pixelCount", Assert.Single(result.GetRegions("Image")).AreaKind); Assert.Equal(40, typed.TotalArea);
        using var image = result.GetOutputImage("Image"); Assert.Equal(frame.CopyPixels(), image.CopyPixels());
    }

    [Fact]
    public async Task SubscribedBlobCentroidsPositionTheReceiverInItsInputPlaneAndDoNotReuseAnOldSelection()
    {
        JsonObject Field(string name, double fallback) => new() { ["node"] = "blob", ["port"] = "result", ["field"] = "measurement.selected." + name, ["fallback"] = fallback };
        var roi = RoiRegion.Annulus(13, 15, 1, 3).ToJson(); roi["cx"] = Field("centroidX", 13); roi["cy"] = Field("centroidY", 15);
        var definition = Recipe(); definition["nodes"]!.AsArray().Add(Node("a_unwrap", "AnnularUnwrap", new()
            { ["roi"] = roi, ["outputWidth"] = 8, ["outputHeight"] = 3 }));
        definition["connections"]!.AsArray().Add(Edge("source", "a_unwrap")); definition["outputs"]!["Image"] = Output("a_unwrap");
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        foreach (var offset in new[] { (X: 0, Y: 0), (X: 4, Y: 2) })
        {
            using var frame = Gray(64, 48, (x, y) => Ring(x - offset.X, y - offset.Y) ? (byte)230 : (byte)20);
            using var result = await session.RunAsync(frame); Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            var selected = Assert.Single(result.GetBlobAnalysis("Result").Features); var mapping = result.GetAnnularMapping("Image");
            Assert.Equal(selected.CentroidX, mapping.CenterX, 8); Assert.Equal(selected.CentroidY, mapping.CenterY, 8);
            var resolved = Assert.IsType<JsonElement>(result.GetResolvedParameters("a_unwrap")).GetProperty("roi");
            Assert.Equal(13 + offset.X, resolved.GetProperty("cx").GetDouble(), 8); Assert.Equal(15 + offset.Y, resolved.GetProperty("cy").GetDouble(), 8);
        }
        using var blank = Gray(64, 48, (_, _) => 20); using var absent = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, absent.Status); Assert.Equal(0, absent.GetBlobAnalysis("Result").Count);
        Assert.False(absent.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
        Assert.Equal("Skipped", absent.Data.GetProperty("nodes").GetProperty("a_unwrap").GetProperty("status").GetString());
        Assert.Null(absent.GetResolvedParameters("a_unwrap"));
    }

    [Fact]
    public async Task AnUnavailableSubscriptionSkipsBlobAnalysisInsteadOfUsingAnEditingFallbackOrAnOldParameter()
    {
        var definition = Recipe(new()
        {
            ["resultIndex"] = new JsonObject { ["node"] = "upstream", ["port"] = "result", ["field"] = "measurement.selected.label", ["fallback"] = 0 }
        });
        definition["nodes"]!.AsArray().Add(Node("upstream", "BlobAnalysis", new() { ["roi"] = RoiRegion.Rectangle(0, 0, 25, 25).ToJson() }));
        definition["connections"]!.AsArray().Add(Edge("source", "upstream")); definition["outputs"]!["Upstream"] = Output("upstream", "result", "Result");
        bool OtherBlob(int x, int y) => x >= 40 && x < 43 && y >= 30 && y < 33;
        using var good = Gray(64, 48, (x, y) => Ring(x, y) || OtherBlob(x, y) ? (byte)230 : (byte)20);
        using var missing = Gray(64, 48, (x, y) => OtherBlob(x, y) ? (byte)230 : (byte)20);
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition)); await using var session = recipe.CreateSession();
        using (var first = await session.RunAsync(good))
        {
            Assert.Equal(ExecutionStatus.Succeeded, first.Status); var blob = first.GetBlobAnalysis("Result");
            Assert.True(blob.DataAvailable); Assert.Equal(2, blob.Count); Assert.Equal(1, blob.Measurement.SelectedIndex);
            Assert.Equal(9, Assert.IsType<VisionRegion>(blob.Measurement.Selected).Area);
        }
        using (var absent = await session.RunAsync(missing))
        {
            Assert.Equal(ExecutionStatus.Succeeded, absent.Status); Assert.Equal(0, absent.GetBlobAnalysis("Upstream").Count);
            var blob = absent.GetBlobAnalysis("Result"); Assert.False(blob.DataAvailable); Assert.Equal(0, blob.Count);
            Assert.Null(blob.TotalArea); Assert.Empty(blob.Features); Assert.Null(blob.Measurement.Selected);
            Assert.Equal("Skipped", absent.Data.GetProperty("nodes").GetProperty("blob").GetProperty("status").GetString());
            Assert.False(absent.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
            Assert.Empty(absent.GetOverlays("Image")); Assert.Null(absent.GetResolvedParameters("blob"));
        }
        using var restored = await session.RunAsync(good); Assert.Equal(ExecutionStatus.Succeeded, restored.Status);
        Assert.True(restored.GetBlobAnalysis("Result").DataAvailable); Assert.Equal(2, restored.GetBlobAnalysis("Result").Count);
        Assert.Equal(9, Assert.IsType<VisionRegion>(restored.GetBlobAnalysis("Result").Measurement.Selected).Area);
    }

    private static JsonObject SdkBlobPacket()
    {
        var feature = new JsonObject
        {
            ["kind"] = "region", ["coordinateSpace"] = "annularUnwrapped", ["coordinatePlane"] = "expanded/a",
            ["area"] = 4.0, ["areaKind"] = "pixelCount", ["pixelCount"] = 4, ["perimeter"] = 8.0,
            ["centroidX"] = 10.5, ["centroidY"] = 20.5, ["centroidValid"] = true, ["circularity"] = Math.PI / 4,
            ["x"] = 10, ["y"] = 20, ["width"] = 2, ["height"] = 2, ["aspectRatio"] = 1.0, ["extent"] = 1.0, ["touchesBoundary"] = false
        };
        return new()
        {
            ["resultKind"] = "blobAnalysis", ["featureKind"] = "region", ["coordinateSpace"] = "annularUnwrapped", ["coordinatePlane"] = "expanded/a",
            ["dataAvailable"] = true, ["reason"] = "", ["count"] = 1, ["candidateCount"] = 1, ["rejectedCount"] = 0,
            ["totalArea"] = 4.0, ["perimeterMethod"] = "pixelEdges", ["found"] = true, ["passed"] = true,
            ["features"] = new JsonArray(feature), ["overlay"] = new JsonArray(),
            ["measurement"] = new JsonObject
            {
                ["coordinateSpace"] = "annularUnwrapped", ["coordinatePlane"] = "expanded/a", ["dataAvailable"] = true,
                ["featureKind"] = "region", ["count"] = 1, ["found"] = true, ["passed"] = true,
                ["selectedIndex"] = 0, ["selectedValid"] = true, ["selected"] = feature.DeepClone()
            }
        };
    }
    private static InspectionResult ReadBlobPacket(JsonObject packet)
    {
        var data = new JsonObject
        {
            ["executionStatus"] = "Succeeded", ["decision"] = "Unknown", ["elapsedMs"] = 1,
            ["runId"] = "blob-sdk-fixture", ["outputs"] = new JsonObject { ["Result"] = packet }
        };
        var handleType = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(handleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { handleType, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }

    [Fact]
    public void SdkRejectsFractionalPixelEdgePerimetersEvenWhenCircularityIsRecomputed()
    {
        using (var valid = ReadBlobPacket(SdkBlobPacket())) Assert.Equal(4, valid.GetBlobAnalysis("Result").TotalArea);
        var packet = SdkBlobPacket(); const double perimeter = 8.5;
        packet["features"]![0]!["perimeter"] = perimeter;
        packet["features"]![0]!["circularity"] = 4 * Math.PI * 4 / (perimeter * perimeter);
        packet["measurement"]!["selected"] = packet["features"]![0]!.DeepClone();
        using var malformed = ReadBlobPacket(packet); Assert.Throws<InvalidDataException>(() => malformed.GetBlobAnalysis("Result"));
    }

    [Fact]
    public void SdkRejectsMeasurementCoordinatesThatContradictTheBlobResultPlane()
    {
        using (var valid = ReadBlobPacket(SdkBlobPacket())) Assert.Equal("expanded/a", valid.GetBlobAnalysis("Result").CoordinatePlane);
        foreach (string field in new[] { "coordinateSpace", "coordinatePlane" })
        {
            var packet = SdkBlobPacket(); packet["measurement"]![field] = field == "coordinateSpace" ? "original" : "expanded/b";
            using var malformed = ReadBlobPacket(packet); Assert.Throws<InvalidDataException>(() => malformed.GetBlobAnalysis("Result"));
        }
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

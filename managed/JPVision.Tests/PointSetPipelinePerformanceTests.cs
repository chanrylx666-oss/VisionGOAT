using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

/// <summary>Public result contracts that must remain intact when pipeline packets are shared or cached.</summary>
public sealed class PointSetPipelinePerformanceTests : IDisposable
{
    private readonly string store;

    public PointSetPipelinePerformanceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "pointset-pipeline-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string from, string to, string input = "input") => new()
    { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = input };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Recipe(JsonArray nodes, JsonArray connections, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "pointset-pipeline-contracts", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = nodes, ["connections"] = connections, ["outputs"] = outputs
    };
    private string Save(JsonObject definition)
    {
        string directory = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return directory;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, bool debug = false)
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame, new() { Debug = debug });
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        return result;
    }
    private static JsonElement Pipeline(InspectionResult result, string name) =>
        result.GetOutput<JsonElement>(name).GetProperty("pipeline");
    private static string AttachedPacket(string rawPacket)
    {
        var packet = JsonNode.Parse(rawPacket)!;
        void RemoveNestedImageContexts(JsonNode? node)
        {
            if (node is JsonObject properties)
                foreach (var property in properties)
                {
                    // Attached packets retain image metadata, but never another complete image pipeline.
                    if (property.Key == "image" && property.Value is JsonObject image)
                    { image.Remove("pipeline"); image.Remove("context"); }
                    RemoveNestedImageContexts(property.Value);
                }
            else if (node is JsonArray items)
                foreach (var item in items) RemoveNestedImageContexts(item);
        }
        RemoveNestedImageContexts(packet);
        return packet.ToJsonString();
    }
    private static string[] OverlayGeometry(JsonElement items) => items.EnumerateArray().Select(item =>
    {
        var geometry = JsonNode.Parse(item.GetRawText())!.AsObject();
        geometry.Remove("color"); geometry.Remove("lineWidth");
        return geometry.ToJsonString();
    }).ToArray();
    private static byte[] Pixels(VisionFrame frame)
    {
        var raw = frame.CopyPixels();
        var compact = new byte[frame.Info.Width * frame.Info.Height];
        for (int y = 0; y < frame.Info.Height; y++)
            Array.Copy(raw, y * frame.Info.Stride, compact, y * frame.Info.Width, frame.Info.Width);
        return compact;
    }
    private static VisionFrame CircleFrame()
    {
        const int size = 128;
        var pixels = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double distance = Math.Sqrt((x - 64) * (x - 64) + (y - 64) * (y - 64));
            pixels[y * size + x] = (byte)Math.Clamp(125 + (30 - distance) * 160, 20, 230);
        }
        return VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8, 900, -80);
    }
    private static JsonObject CircleParameters() => new()
    {
        ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
        ["roi"] = RoiRegion.Circle(64, 64, 30).ToJson(), ["caliperCount"] = 64, ["projectionWidth"] = 5,
        ["searchLength"] = 16.0, ["polarity"] = "lightToDark", ["removePointCount"] = 2,
        ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .5, ["outputAnnulusWidth"] = 6.0
    };
    private static VisionFrame Wave(bool blank = false)
    {
        const int width = 400, height = 300;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            pixels[y * width + x] = !blank && y >= 125 + 12 * Math.Sin((x - 200) / 27.0) && y <= 195 ? (byte)230 : (byte)20;
        return VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 700, -90);
    }
    private static JsonObject PointParameters() => new()
    {
        ["roi"] = RoiRegion.RotatedRectangle(200, 125, 240, 50, 0).ToJson(),
        ["sampleSpacing"] = 6.0, ["projectionWidth"] = 5, ["sigma"] = 1.0,
        ["edgeThreshold"] = 10.0, ["polarity"] = "darkToLight", ["minimumCoverage"] = .5
    };
    private static JsonObject LineParameters() => new()
    {
        ["method"] = "caliper", ["caliperPlacement"] = "roiCenter",
        ["roi"] = RoiRegion.RotatedRectangle(200, 195, 260, 22, 0).ToJson(),
        ["caliperCount"] = 40, ["projectionWidth"] = 5, ["sigma"] = 1.0,
        ["polarity"] = "lightToDark", ["minimumCoverage"] = .8, ["maximumDeviation"] = 1.5
    };

    [Fact]
    public async Task RepeatedRestylingPreservesOverlayOrderAndDoesNotChangePublishedUpstreamStyles()
    {
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("circle", "FindCircles", CircleParameters()),
                Node("orange", "BuildFeatureOverlay", new() { ["color"] = "#FFAA00", ["lineWidth"] = 3.0 }),
                Node("white", "BuildFeatureOverlay", new() { ["color"] = "#FFFFFF", ["lineWidth"] = 4.0 }), Node("tail", "ToGray")),
            new JsonArray(Edge("source", "circle"), Edge("circle", "orange"), Edge("orange", "white"), Edge("white", "tail")),
            new JsonObject { ["Original"] = Output("circle"), ["FirstStyle"] = Output("orange"), ["Image"] = Output("tail") });
        using var frame = CircleFrame();
        using var result = await Run(definition, frame);
        var original = Pipeline(result, "Original").GetProperty("overlay");
        var firstStyle = Pipeline(result, "FirstStyle").GetProperty("overlay");
        var final = Pipeline(result, "Image").GetProperty("overlay");

        // The fitted circle, its annulus, and the measured points keep their initial display order.
        Assert.True(original.GetArrayLength() > 2);
        Assert.Equal(OverlayGeometry(original), OverlayGeometry(firstStyle));
        Assert.Equal(OverlayGeometry(original), OverlayGeometry(final));
        Assert.All(result.GetOverlays("Original"), item => Assert.Equal("#00FF00", item.Color));
        Assert.All(result.GetOverlays("FirstStyle").Where(item => item.Kind is "circle" or "annulus"),
            item => Assert.Equal("#FFAA00", item.Color));
        Assert.All(result.GetOverlays("Image").Where(item => item.Kind is "circle" or "annulus"),
            item => Assert.Equal("#FFFFFF", item.Color));
        // Detector-only diagnostics are carried through, rather than being silently restyled too.
        Assert.All(result.GetOverlays("Image").Where(item => item.Kind == "line"),
            item => Assert.Equal("#00FF00", item.Color));
        foreach (var (name, lineWidth) in new[] { ("Original", 2.0), ("FirstStyle", 3.0), ("Image", 4.0) })
        {
            var fit = Assert.Single(result.GetOverlays(name), item => item.Kind == "circle" && item.RadiusX > 20);
            Assert.Equal(lineWidth, fit.LineWidth);
            Assert.Single(result.GetOverlays(name), item => item.Kind == "annulus");
        }
        using var output = result.GetOutputImage("Image");
        Assert.Equal(Pixels(frame), Pixels(output));
    }

    [Fact]
    public async Task PublicAliasesKeepExactPointPacketsAndTypedResultsAcrossDebugAndImagePublications()
    {
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("points", "FindEdgePoints", PointParameters()), Node("tail", "ToGray")),
            new JsonArray(Edge("source", "points"), Edge("points", "tail")), new JsonObject
            {
                ["AResult"] = Output("points", "result", "Result"), ["ZResult"] = Output("points", "result", "Result"),
                ["PointSet"] = Output("points", "pointSet", "PointSet"),
                ["AImage"] = Output("tail"), ["ZImage"] = Output("tail")
            });
        using var frame = Wave();
        using var result = await Run(definition, frame, debug: true);
        string packet = result.GetOutput<JsonElement>("AResult").GetRawText();
        Assert.Equal(packet, result.GetOutput<JsonElement>("ZResult").GetRawText());
        Assert.Equal(packet, result.GetOutput<JsonElement>("PointSet").GetRawText());
        Assert.Equal(packet, result.Data.GetProperty("debugValues").GetProperty("points").GetRawText());
        var retained = result.Data.GetProperty("nodeOutputs").GetProperty("points");
        Assert.Equal(packet, retained.GetProperty("result").GetRawText());
        Assert.Equal(packet, retained.GetProperty("pointSet").GetRawText());
        Assert.Equal(result.GetOutput<JsonElement>("AImage").GetRawText(), result.GetOutput<JsonElement>("ZImage").GetRawText());
        var attached = Pipeline(result, "AImage").GetProperty("results");
        string contextPacket = AttachedPacket(packet);
        Assert.Equal(contextPacket, attached.GetProperty("Result").GetRawText());
        Assert.Equal(contextPacket, attached.GetProperty("PointSet").GetRawText());

        var expected = result.GetEdgePoints("AResult");
        Assert.True(expected.Valid); Assert.InRange(expected.Count, 39, 41);
        foreach (string alias in new[] { "ZResult", "PointSet", "AImage", "ZImage" })
        {
            var actual = result.GetEdgePoints(alias);
            Assert.Equal(expected.Points.ToArray(), actual.Points.ToArray());
            Assert.Equal(expected.RawPoints!.ToArray(), actual.RawPoints!.ToArray());
            Assert.Equal(expected.StationCount, actual.StationCount);
            Assert.Equal(expected.CoordinatePlane, actual.CoordinatePlane);
        }
        using var first = result.GetOutputImage("AImage");
        using var second = result.GetOutputImage("ZImage");
        Assert.Equal(Pixels(frame), Pixels(first)); Assert.Equal(Pixels(first), Pixels(second));
    }

    [Fact]
    public async Task FinalImageKeepsBothReleasedBranchResultsAndRemainsValidAfterAnotherRunAndSessionDisposal()
    {
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("points", "FindEdgePoints", PointParameters()),
                Node("line", "FindLines", LineParameters()), Node("distance", "MeasurePointLineDistances"),
                Node("tail1", "ToGray"), Node("tail2", "ToGray")),
            new JsonArray(Edge("source", "points"), Edge("source", "line"), Edge("points", "distance"),
                Edge("line", "distance", "line"), Edge("distance", "tail1"), Edge("tail1", "tail2")),
            new JsonObject { ["Image"] = Output("tail2") });
        using var frame = Wave();
        InspectionResult first;
        using (var engine = new VisionEngine(store))
        using (var recipe = engine.LoadRecipe(Save(definition)))
        await using (var session = recipe.CreateSession())
        {
            first = await session.RunAsync(frame, new() { Debug = false });
            Assert.True(first.Status == ExecutionStatus.Succeeded, first.Data.GetRawText());
            using var blank = Wave(blank: true);
            using var next = await session.RunAsync(blank, new() { Debug = false });
            Assert.Equal(ExecutionStatus.Succeeded, next.Status);
            Assert.False(next.GetEdgePoints("Image").Valid);
            Assert.False(next.GetPointLineDistances("Image").Valid);
        }
        VisionFrame retainedImage;
        using (first)
        {
            var points = first.GetEdgePoints("Image");
            var distances = first.GetPointLineDistances("Image");
            Assert.True(points.Valid); Assert.True(distances.Valid);
            Assert.InRange(points.Count, 39, 41);
            Assert.Equal(points.Points.Select(item => item.StationIndex), distances.Distances.Select(item => item.StationIndex));
            for (int index = 0; index < points.Count; index++)
            {
                Assert.Equal(points.Points[index].X, distances.Distances[index].X);
                Assert.Equal(points.Points[index].Y, distances.Distances[index].Y);
            }
            Assert.InRange(distances.Mean!.Value, 65, 75);
            Assert.InRange(distances.Range!.Value, 22, 26);
            Assert.Single(first.GetLines("Image"));
            Assert.Equal(6, Pipeline(first, "Image").GetProperty("stageCount").GetInt32());
            retainedImage = first.GetOutputImage("Image");
        }
        using (retainedImage)
        {
            Assert.Equal(700, retainedImage.Info.OriginX); Assert.Equal(-90, retainedImage.Info.OriginY);
            Assert.Equal(Pixels(frame), Pixels(retainedImage));
        }
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

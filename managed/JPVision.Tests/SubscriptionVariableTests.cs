using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class SubscriptionVariableTests : IDisposable
{
    private readonly string store;

    public SubscriptionVariableTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        store = Path.Combine(root!.FullName, "artifacts", "tests", "subscription-variables-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Edge(string source, string target) => new()
    { ["fromNode"] = source, ["fromPort"] = "image", ["toNode"] = target, ["toPort"] = "input" };
    private static JsonObject Output(string node, string port = "image", string type = "Image") => new()
    { ["node"] = node, ["port"] = port, ["type"] = type };
    private static JsonObject Field(string field, string node = "z_circle", JsonNode? fallback = null)
    {
        var result = new JsonObject { ["node"] = node, ["port"] = "result", ["field"] = "measurement.selected." + field };
        if (fallback is not null) result["fallback"] = fallback;
        return result;
    }
    private static JsonObject Variable(string name, string scope = "global") => new()
    { ["variable"] = name, ["scope"] = scope };
    private static JsonObject Typed(string type, JsonNode value) => new() { ["type"] = type, ["value"] = value };
    private static JsonObject Formula(string expression, JsonObject? arguments = null) => new()
    { ["expression"] = expression, ["arguments"] = arguments ?? new() };
    private static JsonObject CircleParameters(double cx = 120, double cy = 100, double radius = 45) => new()
    {
        ["roi"] = RoiRegion.Circle(cx, cy, radius).ToJson(), ["caliperPlacement"] = "roiBoundary",
        ["caliperCount"] = 48, ["searchLength"] = 60.0, ["projectionWidth"] = 5,
        ["sigma"] = 1.0, ["polarity"] = "lightToDark", ["minimumCoverage"] = .5,
        ["maximumDeviation"] = 2.5
    };
    private static JsonObject AnnularParameters() => new()
    {
        ["roi"] = RoiRegion.Annulus(120, 100, 40, 50).ToJson(),
        ["outputWidth"] = 32, ["outputHeight"] = 5, ["interpolation"] = "nearest"
    };
    private static JsonObject Recipe(JsonArray nodes, JsonArray edges, JsonObject outputs) => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["kind"] = "processing",
        ["recipeId"] = "subscription-variable-tests", ["recipeVersion"] = 1,
        ["nodes"] = nodes, ["connections"] = edges, ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["outputs"] = outputs
    };
    private static JsonObject FollowCircle()
    {
        var parameters = AnnularParameters();
        parameters["roi"]!["cx"] = Field("centerX", fallback: JsonValue.Create(120));
        parameters["roi"]!["cy"] = Field("centerY", fallback: JsonValue.Create(100));
        // The receiver sorts before its unpublished supplier: subscriptions must determine scheduling.
        return Recipe(new JsonArray(Node("source", "ExternalImage"), Node("a_unwrap", "AnnularUnwrap", parameters),
                Node("z_circle", "CircleCaliper", CircleParameters())),
            new JsonArray(Edge("source", "a_unwrap"), Edge("source", "z_circle")),
            new JsonObject { ["Image"] = Output("a_unwrap") });
    }
    private static JsonObject SingleUnwrap(JsonObject? parameters = null) => Recipe(
        new JsonArray(Node("source", "ExternalImage"), Node("a_unwrap", "AnnularUnwrap", parameters ?? AnnularParameters())),
        new JsonArray(Edge("source", "a_unwrap")), new JsonObject { ["Image"] = Output("a_unwrap") });
    private static JsonObject Unwrap(JsonObject definition) => definition["nodes"]![1]!["parameters"]!.AsObject();
    private string Save(JsonObject definition, bool seal = true)
    {
        string directory = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recipe.json"), seal ? VisionEngine.SealRecipe(definition.ToJsonString()) : definition.ToJsonString());
        return directory;
    }
    private static VisionFrame Disk(double cx = 120, double cy = 100, bool blank = false, int originX = 0, int originY = 0)
    {
        byte[] pixels = new byte[240 * 200];
        for (int y = 0; y < 200; y++) for (int x = 0; x < 240; x++)
            pixels[y * 240 + x] = !blank && Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2) <= 45 * 45 ? (byte)230 : (byte)15;
        return VisionFrame.CopyFrom(pixels, 240, 200, 240, VisionPixelFormat.Gray8, originX, originY);
    }
    private static VisionCircle Selected(InspectionResult result, string node = "z_circle") =>
        Assert.IsType<VisionCircle>(result.GetNodeMeasurement<VisionCircle>(node).Selected);

    [Fact]
    public async Task SubscribedCircleCenterUsesTheCurrentFrameAndSchedulesAnUnpublishedSupplier()
    {
        using var engine = new VisionEngine(store);
        using var recipe = engine.LoadRecipe(Save(FollowCircle()));
        await using var session = recipe.CreateSession();
        foreach (var center in new[] { (120.0, 100.0), (132.0, 91.0), (113.0, 110.0) })
        {
            using var frame = Disk(center.Item1, center.Item2);
            using var result = await session.RunAsync(frame, new() { Debug = false });
            Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
            var circle = Selected(result); var mapping = result.GetAnnularMapping("Image");
            Assert.InRange(circle.CenterX, center.Item1 - 1, center.Item1 + 1);
            Assert.InRange(circle.CenterY, center.Item2 - 1, center.Item2 + 1);
            Assert.Equal(circle.CenterX, mapping.CenterX, 8); Assert.Equal(circle.CenterY, mapping.CenterY, 8);
            var resolved = Assert.IsType<JsonElement>(result.GetResolvedParameters("a_unwrap"));
            Assert.Equal(circle.CenterX, resolved.GetProperty("roi").GetProperty("cx").GetDouble(), 8);
            Assert.Equal(circle.CenterY, resolved.GetProperty("roi").GetProperty("cy").GetDouble(), 8);
            Assert.Equal("Succeeded", result.Data.GetProperty("nodes").GetProperty("a_unwrap").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task MissingCircleSkipsTheReceiverAndNeverExecutesAnEditingFallbackOrAnOldCenter()
    {
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(FollowCircle()));
        await using var session = recipe.CreateSession();
        using var valid = Disk(132, 91);
        using (var first = await session.RunAsync(valid)) Assert.InRange(first.GetAnnularMapping("Image").CenterX, 131, 133);
        using var blank = Disk(blank: true); using var result = await session.RunAsync(blank);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.GetNodeMeasurement<VisionCircle>("z_circle").Selected);
        Assert.Equal("Skipped", result.Data.GetProperty("nodes").GetProperty("a_unwrap").GetProperty("status").GetString());
        Assert.False(result.GetOutput<JsonElement>("Image").GetProperty("available").GetBoolean());
        Assert.Null(result.GetResolvedParameters("a_unwrap"));
    }

    [Fact]
    public async Task OriginalCoordinatesAreConvertedToACroppedInputBeforeSampling()
    {
        var definition = FollowCircle();
        definition["nodes"]!.AsArray().Add(Node("crop", "CropRect", new() { ["x"] = 40, ["y"] = 30, ["width"] = 160, ["height"] = 140 }));
        definition["connections"] = new JsonArray(Edge("source", "z_circle"), Edge("source", "crop"), Edge("crop", "a_unwrap"));
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(originX: 700, originY: -90);
        using var result = await session.RunAsync(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        var circle = Selected(result); var mapping = result.GetAnnularMapping("Image");
        Assert.InRange(circle.CenterX, 819, 821); Assert.InRange(circle.CenterY, 9, 11);
        Assert.Equal(circle.CenterX, mapping.CenterX, 8); Assert.Equal(circle.CenterY, mapping.CenterY, 8);
        var resolved = Assert.IsType<JsonElement>(result.GetResolvedParameters("a_unwrap"));
        Assert.Equal(circle.CenterX - 740, resolved.GetProperty("roi").GetProperty("cx").GetDouble(), 8);
        Assert.Equal(circle.CenterY + 60, resolved.GetProperty("roi").GetProperty("cy").GetDouble(), 8);
        using var image = result.GetOutputImage("Image");
        Assert.Contains(image.CopyPixels(), value => value >= 200);
        Assert.Contains(image.CopyPixels(), value => value <= 20);
    }

    [Fact]
    public async Task GlobalRunOverridesAreTypedAndDoNotLeakIntoFollowingRuns()
    {
        var definition = SingleUnwrap();
        definition["variables"] = new JsonObject { ["CenterX"] = Typed("Double", JsonValue.Create(120.0)!), ["CenterY"] = Typed("Double", JsonValue.Create(100.0)!) };
        Unwrap(definition)["roi"]!["cx"] = Variable("CenterX"); Unwrap(definition)["roi"]!["cy"] = Variable("CenterY");
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk();
        using (var first = await session.RunAsync(frame))
        { Assert.Equal(120, first.GetAnnularMapping("Image").CenterX); Assert.Equal(120, first.GetGlobalVariable<double>("CenterX")); }
        using (var shifted = await session.RunAsync(frame, new() { Variables = new Dictionary<string, object?> { ["CenterX"] = 132.5 } }))
        { Assert.Equal(132.5, shifted.GetAnnularMapping("Image").CenterX); Assert.Equal(132.5, shifted.GetGlobalVariable<double>("CenterX")); }
        using (var restored = await session.RunAsync(frame))
        { Assert.Equal(120, restored.GetAnnularMapping("Image").CenterX); Assert.Equal(120, restored.GetGlobalVariable<double>("CenterX")); }
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        { using var _ = await session.RunAsync(frame, new() { Variables = new Dictionary<string, object?> { ["CenterX"] = true } }); });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        { using var _ = await session.RunAsync(frame, new() { Variables = new Dictionary<string, object?> { ["Unknown"] = 1 } }); });
    }

    [Fact]
    public async Task LocalVariablesComposeUpstreamCoordinatesWithOffsetsAndPureFunctions()
    {
        var definition = FollowCircle();
        definition["variables"] = new JsonObject { ["OffsetX"] = Typed("Double", JsonValue.Create(3.0)!), ["Margin"] = Typed("Double", JsonValue.Create(2.0)!) };
        definition["functions"] = new JsonObject
        { ["Shift"] = new JsonObject { ["parameters"] = new JsonArray("x", "dx"), ["expression"] = "x + dx" } };
        definition["nodes"]![1]!["variables"] = new JsonObject
        {
            ["anchorX"] = Typed("Double", Field("centerX")), ["anchorY"] = Typed("Double", Field("centerY")),
            ["adjustedX"] = Typed("Double", Formula("Shift(local.anchorX, global.OffsetX)"))
        };
        var roi = Unwrap(definition)["roi"]!;
        roi["cx"] = Variable("adjustedX", "local"); roi["cy"] = Variable("anchorY", "local");
        roi["innerRadius"] = Formula("r - global.Margin", new() { ["r"] = Field("radius") });
        roi["outerRadius"] = Formula("r + global.Margin", new() { ["r"] = Field("radius") });
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(132, 91, originX: 700, originY: -90);
        using var result = await session.RunAsync(frame);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Data.GetRawText());
        var circle = Selected(result); var mapping = result.GetAnnularMapping("Image");
        Assert.Equal(circle.CenterX + 3, mapping.CenterX, 8); Assert.Equal(circle.CenterY, mapping.CenterY, 8);
        Assert.Equal(circle.Radius - 2, mapping.RadialOrigin, 8);
        Assert.Equal(circle.Radius + 2, mapping.RadialOrigin + mapping.RadialStep * (mapping.Height - 1), 8);
        Assert.Equal(3, result.GetGlobalVariable<double>("OffsetX"));
        Assert.Equal(circle.CenterX, result.GetLocalVariable<double>("a_unwrap", "anchorX"), 8);
        Assert.Equal(circle.CenterY, result.GetLocalVariable<double>("a_unwrap", "anchorY"), 8);
        Assert.Equal(circle.CenterX + 3, result.GetLocalVariable<double>("a_unwrap", "adjustedX"), 8);
        var resolved = Assert.IsType<JsonElement>(result.GetResolvedParameters("a_unwrap"));
        Assert.Equal(circle.CenterX + 3 - 700, resolved.GetProperty("roi").GetProperty("cx").GetDouble(), 8);
        Assert.Equal(circle.CenterY + 90, resolved.GetProperty("roi").GetProperty("cy").GetDouble(), 8);
        using var blank = Disk(blank: true, originX: 700, originY: -90); using var missing = await session.RunAsync(blank);
        Assert.Equal("Skipped", missing.Data.GetProperty("nodes").GetProperty("a_unwrap").GetProperty("status").GetString());
        Assert.Null(missing.GetResolvedParameters("a_unwrap"));
        Assert.Throws<InvalidOperationException>(() => missing.GetLocalVariable<double>("a_unwrap", "anchorX"));
        Assert.Throws<InvalidOperationException>(() => missing.GetLocalVariable<double>("a_unwrap", "adjustedX"));
        Assert.Equal(3, missing.GetGlobalVariable<double>("OffsetX"));
    }

    [Fact]
    public async Task CoordinateSubscriptionCannotCrossFromOriginalPixelsToAnExpandedPlane()
    {
        var definition = FollowCircle();
        var first = AnnularParameters();
        definition["nodes"]!.AsArray().Add(Node("first_unwrap", "AnnularUnwrap", first));
        definition["connections"] = new JsonArray(Edge("source", "z_circle"), Edge("source", "first_unwrap"), Edge("first_unwrap", "a_unwrap"));
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("a_unwrap", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("coordinate", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoIndependentAnnularExpansionsDoNotShareASubscriptionCoordinatePlane()
    {
        var first = new JsonObject { ["roi"] = RoiRegion.Annulus(32, 32, 10, 30).ToJson(), ["outputWidth"] = 128, ["outputHeight"] = 41, ["interpolation"] = "linear" };
        var target = new JsonObject { ["roi"] = RoiRegion.Annulus(64, 20, 4, 10).ToJson(), ["outputWidth"] = 32, ["outputHeight"] = 5 };
        target["roi"]!["cx"] = Field("centerX"); target["roi"]!["cy"] = Field("centerY");
        var circle = CircleParameters(64, 20, 8); circle["projectionWidth"] = 3; circle["searchLength"] = 8.0; circle["sigma"] = .3;
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("unwrapA", "AnnularUnwrap", first),
                Node("unwrapB", "AnnularUnwrap", (JsonObject)first.DeepClone()), Node("z_circle", "CircleCaliper", circle), Node("a_unwrap", "AnnularUnwrap", target)),
            new JsonArray(Edge("source", "unwrapA"), Edge("source", "unwrapB"), Edge("unwrapA", "z_circle"), Edge("unwrapB", "a_unwrap")),
            new JsonObject { ["Image"] = Output("a_unwrap") });
        byte[] pixels = new byte[65 * 65];
        for (int y = 0; y < 65; y++) for (int x = 0; x < 65; x++)
        {
            double radius = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
            double angle = Math.Atan2(y - 32, x - 32) * 180 / Math.PI; if (angle < 0) angle += 360;
            double u = angle * 128 / 360, v = (radius - 10) * 2;
            pixels[y * 65 + x] = Math.Pow(u - 64, 2) + Math.Pow(v - 20, 2) <= 64 ? (byte)230 : (byte)20;
        }
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = VisionFrame.CopyFrom(pixels, 65, 65, 65, VisionPixelFormat.Gray8);
        using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("a_unwrap", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("coordinate", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("global")] [InlineData("local")] [InlineData("function")]
    public void VariableAndFunctionChangesAreIncludedInTheContentHash(string changed)
    {
        var definition = SingleUnwrap();
        definition["variables"] = new JsonObject { ["Offset"] = Typed("Double", JsonValue.Create(1.0)!) };
        definition["nodes"]![1]!["variables"] = new JsonObject { ["Offset"] = Typed("Double", JsonValue.Create(2.0)!) };
        definition["functions"] = new JsonObject { ["Shift"] = new JsonObject { ["parameters"] = new JsonArray("x"), ["expression"] = "x + 1" } };
        var sealedDefinition = JsonNode.Parse(VisionEngine.SealRecipe(definition.ToJsonString()))!.AsObject();
        using var engine = new VisionEngine(store);
        using (var unchanged = engine.LoadRecipe(Save(sealedDefinition, seal: false))) { }
        string originalHash = sealedDefinition["contentHash"]!.GetValue<string>();
        if (changed == "global") sealedDefinition["variables"]!["Offset"]!["value"] = 3.0;
        else if (changed == "local") sealedDefinition["nodes"]![1]!["variables"]!["Offset"]!["value"] = 3.0;
        else sealedDefinition["functions"]!["Shift"]!["expression"] = "x + 3";
        Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(sealedDefinition, seal: false)));
        string resealed = VisionEngine.SealRecipe(sealedDefinition.ToJsonString());
        Assert.NotEqual(originalHash, JsonNode.Parse(resealed)!["contentHash"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing-global")] [InlineData("missing-local")] [InlineData("wrong-field-type")]
    [InlineData("wrong-variable-type")] [InlineData("unknown-field")] [InlineData("syntax")]
    [InlineData("unknown-function")] [InlineData("unknown-symbol")] [InlineData("function-external-state")]
    [InlineData("function-recursion")] [InlineData("local-cycle")]
    public void InvalidSubscriptionsAndFunctionsFailBeforeImageExecution(string invalid)
    {
        var definition = FollowCircle(); var parameters = Unwrap(definition);
        switch (invalid)
        {
            case "missing-global": parameters["roi"]!["cx"] = Variable("missing"); break;
            case "missing-local": parameters["roi"]!["cx"] = Variable("missing", "local"); break;
            case "wrong-field-type": parameters["roi"]!["cx"] = new JsonObject { ["node"] = "z_circle", ["port"] = "result", ["field"] = "measurement.selectedValid" }; break;
            case "wrong-variable-type":
                definition["variables"] = new JsonObject { ["Center"] = Typed("Bool", JsonValue.Create(true)!) };
                parameters["roi"]!["cx"] = Variable("Center"); break;
            case "unknown-field": parameters["roi"]!["cx"] = Field("doesNotExist"); break;
            case "syntax": parameters["roi"]!["cx"] = Formula("1 + ; process()"); break;
            case "unknown-function": parameters["roi"]!["cx"] = Formula("Unknown(120)"); break;
            case "unknown-symbol": parameters["roi"]!["cx"] = Formula("unknown + 1"); break;
            case "function-external-state": definition["functions"] = new JsonObject { ["Unsafe"] = new JsonObject { ["parameters"] = new JsonArray("x"), ["expression"] = "x + global.Center" } }; break;
            case "function-recursion": definition["functions"] = new JsonObject { ["Again"] = new JsonObject { ["parameters"] = new JsonArray("x"), ["expression"] = "Again(x)" } }; break;
            case "local-cycle":
                definition["nodes"]![1]!["variables"] = new JsonObject { ["a"] = Typed("Double", Variable("b", "local")), ["b"] = Typed("Double", Variable("a", "local")) };
                parameters["roi"]!["cx"] = Variable("a", "local"); break;
        }
        using var engine = new VisionEngine(store); Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
    }

    [Fact]
    public void DependenciesHiddenInLocalVariablesCannotIntroduceAnExecutionCycle()
    {
        var a = Node("a_circle", "CircleCaliper", CircleParameters());
        var b = Node("b_circle", "CircleCaliper", CircleParameters());
        a["variables"] = new JsonObject { ["center"] = Typed("Double", Field("centerX", "b_circle")) };
        b["variables"] = new JsonObject { ["center"] = Typed("Double", Field("centerX", "a_circle")) };
        a["parameters"]!["roi"]!["cx"] = Variable("center", "local"); b["parameters"]!["roi"]!["cx"] = Variable("center", "local");
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), a, b), new JsonArray(Edge("source", "a_circle"), Edge("source", "b_circle")), new JsonObject { ["Image"] = Output("a_circle") });
        using var engine = new VisionEngine(store);
        var error = Assert.Throws<VisionException>(() => engine.LoadRecipe(Save(definition)));
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeDivisionByAnUpstreamZeroReportsTheReceiverAndDoesNotPublishFakeResults()
    {
        var definition = FollowCircle();
        Unwrap(definition)["roi"]!["cx"] = Formula("r / (r - r)", new() { ["r"] = Field("radius") });
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status); Assert.Equal(InspectionDecision.Unknown, result.Decision);
        Assert.Empty(result.Outputs.EnumerateObject());
        Assert.Equal("a_unwrap", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("zero", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatedLocalCoordinateReferencesKeepBoundedProvenance()
    {
        var definition = FollowCircle();
        var locals = new JsonObject { ["v0"] = Typed("Double", Field("centerX")) };
        for (int i = 1; i < 32; i++)
            locals["v" + i] = Typed("Double", Formula($"(local.v{i - 1} + local.v{i - 1}) / 2"));
        definition["nodes"]![1]!["variables"] = locals;
        Unwrap(definition)["roi"]!["cx"] = Variable("v31", "local");
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.InRange(result.GetLocalVariable<double>("a_unwrap", "v31"), 119, 121);
        Assert.InRange(result.GetResolvedParameters("a_unwrap")!.Value.GetProperty("roi").GetProperty("cx").GetDouble(), 119, 121);
    }

    [Theory]
    [InlineData("fixture")] [InlineData("matchCenter")]
    public async Task CoordinateSubscriptionsCannotAlsoApplyAnotherPositionTransform(string mode)
    {
        var definition = FollowCircle();
        if (mode == "fixture") Unwrap(definition)["roiReference"] = "fixture";
        else
        {
            var receiver = definition["nodes"]![1]!;
            receiver["type"] = "FindCircles";
            var parameters = CircleParameters(); parameters["method"] = "caliper"; parameters["positionReference"] = mode;
            parameters["roi"]!["cx"] = Field("centerX"); parameters["roi"]!["cy"] = Field("centerY");
            receiver["parameters"] = parameters;
        }
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("a_unwrap", result.Data.GetProperty("error").GetProperty("nodeId").GetString());
        Assert.Contains("subscriptions", result.Data.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnusedMissingExpressionBranchDoesNotOverrideTheActualDecision()
    {
        var missing = CircleParameters(); missing["roi"]!["cx"] = Field("centerX");
        var expected = Formula("true ? 0 : unused", new() { ["unused"] = new JsonObject { ["node"] = "missing", ["port"] = "count", ["field"] = "" } });
        var definition = Recipe(new JsonArray(Node("source", "ExternalImage"), Node("z_circle", "CircleCaliper", CircleParameters()),
                Node("missing", "CircleCaliper", missing), Node("compare", "CompareNumber", new() { ["expected"] = expected })),
            new JsonArray(Edge("source", "z_circle"), Edge("source", "missing"), new JsonObject { ["fromNode"] = "z_circle", ["fromPort"] = "count", ["toNode"] = "compare", ["toPort"] = "input" }),
            new JsonObject { ["Decision"] = Output("compare", "value", "Bool") });
        definition["kind"] = "inspection"; definition["decisionOutput"] = "Decision";
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession(); using var frame = Disk(blank: true); using var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Skipped", result.Data.GetProperty("nodes").GetProperty("missing").GetProperty("status").GetString());
        Assert.Equal("Succeeded", result.Data.GetProperty("nodes").GetProperty("compare").GetProperty("status").GetString());
        Assert.Equal(InspectionDecision.Pass, result.Decision);
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

using JPVision.Sdk;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

/// <summary>Fast matching must compare every enabled template/scale on the current input.</summary>
public sealed class FastTemplateBankTests : IDisposable
{
    private const int ModelWidth = 40, ModelHeight = 32, Width = 360, Height = 220;
    private readonly string store;

    public FastTemplateBankTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root missing.");
        store = Path.Combine(root.FullName, "artifacts", "tests", "fast-template-bank-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root.FullName, "artifacts", "activation", "current.vlic"), store);
    }

    private static byte[] Pixels(int variant = 0)
    {
        var gray = new byte[ModelWidth * ModelHeight];
        for (int y = 0; y < ModelHeight; y++)
            for (int x = 0; x < ModelWidth; x++)
                gray[y * ModelWidth + x] = (byte)(25 + (x * (13 + variant * 6) + y * (17 + variant * 4) + x * y * (3 + variant * 2)) % 200);
        return gray;
    }

    private static JsonObject Model(int variant = 0, bool masked = false)
    {
        var model = new JsonObject
        {
            ["width"] = ModelWidth, ["height"] = ModelHeight,
            ["grayBase64"] = Convert.ToBase64String(Pixels(variant))
        };
        if (masked)
        {
            var mask = new byte[ModelWidth * ModelHeight];
            for (int y = 0; y < ModelHeight; y++)
                for (int x = 0; x < ModelWidth; x++)
                    if (InsideMask(x, y)) mask[y * ModelWidth + x] = 255;
            model["maskBase64"] = Convert.ToBase64String(mask);
        }
        return model;
    }

    private static bool InsideMask(int x, int y) => Math.Pow((x - 19.5) / 18, 2) + Math.Pow((y - 15.5) / 14, 2) <= 1;
    private static JsonObject Entry(string id, int variant = 0, bool enabled = true, bool masked = false) => new()
    { ["id"] = id, ["name"] = "工件 " + id, ["enabled"] = enabled, ["model"] = Model(variant, masked) };
    private static JsonObject Bank(params JsonObject[] entries) => new() { ["templates"] = new JsonArray(entries.Cast<JsonNode?>().ToArray()) };
    private static JsonObject Parameters(JsonObject? bank = null) => new()
    {
        ["model"] = bank ?? Bank(Entry("first"), Entry("best", 1)),
        ["scoreThreshold"] = .90, ["searchDownsample"] = 1
    };
    private static JsonObject Node(string id, string type, JsonObject? parameters = null) => new()
    { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = parameters ?? new() };
    private static JsonObject Output(string port, string type) => new() { ["node"] = "match", ["port"] = port, ["type"] = type };
    private static JsonObject Definition(JsonObject parameters, string type = "FastTemplateMatch") => new()
    {
        ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "fast-template-bank", ["recipeVersion"] = 1,
        ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
        ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
        ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("match", type, parameters)),
        ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "match", ["toPort"] = "input" }),
        ["outputs"] = new JsonObject
        {
            ["Features"] = Output("features", "Features"), ["Measurement"] = Output("measurement", "Measurement"),
            ["Overlay"] = Output("overlay", "Overlay"), ["Image"] = Output("image", "Image"), ["Result"] = Output("result", "Result")
        }
    };

    private static byte[] Background() => Enumerable.Repeat((byte)12, Width * Height).ToArray();
    private static void Stamp(byte[] image, byte[] patch, int patchWidth, int patchHeight, int x, int y, bool weaker = false, bool masked = false)
    {
        for (int py = 0; py < patchHeight; py++)
            for (int px = 0; px < patchWidth; px++)
            {
                int value = patch[py * patchWidth + px];
                if (masked && !InsideMask(px, py)) value = 240;
                if (weaker) value = Math.Clamp(value + (px * 7 + py * 11) % 51 - 25, 0, 255);
                image[(y + py) * Width + x + px] = (byte)value;
            }
    }
    private static VisionFrame Frame(byte[] pixels, int originX = 0, int originY = 0) =>
        VisionFrame.CopyFrom(pixels, Width, Height, Width, VisionPixelFormat.Gray8, originX, originY);
    private string Save(JsonObject definition)
    {
        var path = Path.Combine(store, Guid.NewGuid() + ".vrecipe");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
        return path;
    }
    private async Task<InspectionResult> Run(JsonObject definition, VisionFrame frame, VisionExecutionBackend backend = VisionExecutionBackend.Managed)
    {
        using var engine = new VisionEngine(store, backend);
        using var recipe = engine.LoadRecipe(Save(definition));
        await using var session = recipe.CreateSession();
        var result = await session.RunAsync(frame);
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        return result;
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed, "FastTemplateMatch")]
    [InlineData(VisionExecutionBackend.Native, "FastTemplateMatch")]
    [InlineData(VisionExecutionBackend.Managed, "FastLocate")]
    [InlineData(VisionExecutionBackend.Native, "FastLocate")]
    public async Task EveryTemplateAndImageLocationCompetesForOneGlobalBest(VisionExecutionBackend backend, string type)
    {
        var pixels = Background();
        Stamp(pixels, Pixels(), ModelWidth, ModelHeight, 21, 35, weaker: true);
        Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 289, 173);
        using var frame = Frame(pixels);
        using var result = await Run(Definition(Parameters(), type), frame, backend);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal("best", match.TemplateId); Assert.Equal("工件 best", match.TemplateName);
        Assert.Equal(289, match.X); Assert.Equal(173, match.Y);
        Assert.Equal(309, match.CenterX); Assert.Equal(189, match.CenterY);
        Assert.Equal(ModelWidth, match.Width); Assert.Equal(ModelHeight, match.Height);
        Assert.Equal(1, match.Scale); Assert.InRange(match.Score, .99, 1);
        var measured = result.GetMeasurement<VisionMatch>("Measurement");
        Assert.Equal("best", measured.Selected!.TemplateId); Assert.Equal(1, measured.Count);
        Assert.Equal("best", Assert.Single(result.GetResult<VisionMatch>("Result").Features).TemplateId);
        var position = result.GetResult<VisionMatch>("Result").MatchPosition;
        Assert.NotNull(position); Assert.Equal(match.CenterX, position.CenterX); Assert.Equal(match.Scale, position.Scale);
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed)]
    [InlineData(VisionExecutionBackend.Native)]
    public async Task ADisabledHigherScoringTemplateCannotWin(VisionExecutionBackend backend)
    {
        var pixels = Background();
        Stamp(pixels, Pixels(), ModelWidth, ModelHeight, 21, 35, weaker: true);
        Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 289, 173);
        using var frame = Frame(pixels);
        using var result = await Run(Definition(Parameters(Bank(Entry("first"), Entry("best", 1, enabled: false)))), frame, backend);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal("first", match.TemplateId); Assert.Equal(21, match.X); Assert.Equal(35, match.Y);
        Assert.InRange(match.Score, .90, .99);
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed)]
    [InlineData(VisionExecutionBackend.Native)]
    public async Task EqualScoresPreferBankOrderRatherThanAlphabeticalId(VisionExecutionBackend backend)
    {
        var pixels = Background(); Stamp(pixels, Pixels(), ModelWidth, ModelHeight, 75, 51);
        using var frame = Frame(pixels);
        var definition = Definition(Parameters(Bank(Entry("z-first"), Entry("a-second"))));
        for (int repeat = 0; repeat < 3; repeat++)
        {
            using var result = await Run(definition, frame, backend);
            Assert.Equal("z-first", Assert.Single(result.GetMatches()).TemplateId);
        }
    }

    // cv::resize uses pixel centers (d + .5) * source/destination - .5 and edge replication.
    // Quantization may differ by one grayscale unit; scores are tested with a realistic tolerance.
    private static byte[] Resize(byte[] pixels, int width, int height, int sourceWidth = ModelWidth, int sourceHeight = ModelHeight)
    {
        var resized = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + .5) * sourceWidth / width - .5, 0, sourceWidth - 1);
                double sy = Math.Clamp((y + .5) * sourceHeight / height - .5, 0, sourceHeight - 1);
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1), y1 = Math.Min(y0 + 1, sourceHeight - 1);
                double dx = sx - x0, dy = sy - y0;
                resized[y * width + x] = (byte)Math.Round((1 - dy) * ((1 - dx) * pixels[y0 * sourceWidth + x0] + dx * pixels[y0 * sourceWidth + x1]) +
                    dy * ((1 - dx) * pixels[y1 * sourceWidth + x0] + dx * pixels[y1 * sourceWidth + x1]));
            }
        return resized;
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed, .75)]
    [InlineData(VisionExecutionBackend.Native, .75)]
    [InlineData(VisionExecutionBackend.Managed, 1.25)]
    [InlineData(VisionExecutionBackend.Native, 1.25)]
    public async Task ScaleSearchFindsSmallerAndLargerObjectsAndReportsTheirMeasuredBox(VisionExecutionBackend backend, double scale)
    {
        int width = (int)Math.Round(ModelWidth * scale), height = (int)Math.Round(ModelHeight * scale);
        var pixels = Background(); Stamp(pixels, Resize(Pixels(1), width, height), width, height, 277, 161);
        var parameters = Parameters(Bank(Entry("right-size", 1)));
        parameters["scaleMinimum"] = .75; parameters["scaleMaximum"] = 1.25; parameters["scaleStep"] = .25;
        using var frame = Frame(pixels, 600, -20);
        using var result = await Run(Definition(parameters), frame, backend);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal("right-size", match.TemplateId); Assert.Equal(scale, match.Scale, 8);
        Assert.Equal(width, match.Width); Assert.Equal(height, match.Height);
        Assert.Equal(877, match.X); Assert.Equal(141, match.Y);
        Assert.Equal(877 + width / 2.0, match.CenterX); Assert.Equal(141 + height / 2.0, match.CenterY);
        Assert.InRange(match.Score, .90, 1); Assert.Equal(4, match.Corners.Count);
        Assert.Equal(match.X, match.Corners.Min(p => p.X), 6);
        Assert.Equal(match.X + width, match.Corners.Max(p => p.X), 6);
    }

    [Fact]
    public async Task AScaleUpperEndpointIsSearchedEvenWhenTheStepDoesNotLandOnIt()
    {
        const double scale = 1.13;
        int width = (int)Math.Round(ModelWidth * scale), height = (int)Math.Round(ModelHeight * scale);
        var pixels = Background(); Stamp(pixels, Resize(Pixels(), width, height), width, height, 201, 115);
        var parameters = Parameters(Bank(Entry("endpoint")));
        parameters["scaleMinimum"] = .8; parameters["scaleMaximum"] = scale; parameters["scaleStep"] = .2;
        using var frame = Frame(pixels); using var result = await Run(Definition(parameters), frame);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal(scale, match.Scale, 8); Assert.Equal(201, match.X); Assert.Equal(115, match.Y);
    }

    [Fact]
    public async Task MaximumWidthTemplateAtDoubleScaleFindsTheLastPixelAtATileSeam()
    {
        const int modelWidth = 512, modelHeight = 40, scaledWidth = 1024, scaledHeight = 80;
        const int frameWidth = 2048, frameHeight = 160, left = frameWidth - scaledWidth, top = frameHeight - scaledHeight;
        var modelPixels = new byte[modelWidth * modelHeight];
        for (int y = 0; y < modelHeight; y++)
            for (int x = 0; x < modelWidth; x++) modelPixels[y * modelWidth + x] = (byte)(25 + (x * 13 + y * 17 + x * y * 3) % 200);
        var scaled = Resize(modelPixels, scaledWidth, scaledHeight, modelWidth, modelHeight);
        var pixels = Enumerable.Repeat((byte)12, frameWidth * frameHeight).ToArray();
        for (int y = 0; y < scaledHeight; y++)
            Buffer.BlockCopy(scaled, y * scaledWidth, pixels, (top + y) * frameWidth + left, scaledWidth);
        var entry = Entry("double-width");
        entry["model"] = new JsonObject { ["width"] = modelWidth, ["height"] = modelHeight, ["grayBase64"] = Convert.ToBase64String(modelPixels) };
        var parameters = Parameters(Bank(entry)); parameters["scaleMinimum"] = 2.0; parameters["scaleMaximum"] = 2.0;
        using var frame = VisionFrame.CopyFrom(pixels, frameWidth, frameHeight, frameWidth, VisionPixelFormat.Gray8);
        using var result = await Run(Definition(parameters), frame);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal(left, match.X); Assert.Equal(top, match.Y); Assert.Equal(scaledWidth, match.Width); Assert.Equal(scaledHeight, match.Height);
        Assert.Equal(2, match.Scale); Assert.InRange(match.Score, .99, 1);
    }

    [Fact]
    public async Task ATemplateThatLosesAllContrastWhenShrunkIsSkippedRatherThanReportingPerfectMatches()
    {
        var checkerboard = new byte[8 * 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++) checkerboard[y * 8 + x] = (x + y) % 2 == 0 ? (byte)0 : (byte)255;
        var entry = Entry("shrunk-flat");
        entry["model"] = new JsonObject { ["width"] = 8, ["height"] = 8, ["grayBase64"] = Convert.ToBase64String(checkerboard) };
        var parameters = Parameters(Bank(entry)); parameters["scaleMinimum"] = .5; parameters["scaleMaximum"] = .5;
        var pixels = Background(); Stamp(pixels, Pixels(), ModelWidth, ModelHeight, 119, 79);
        using var frame = Frame(pixels); using var result = await Run(Definition(parameters), frame);
        Assert.Empty(result.GetMatches()); Assert.Empty(result.GetOverlays("Overlay"));
        Assert.Null(result.GetMeasurement<VisionMatch>("Measurement").Selected);
    }

    [Fact]
    public async Task MaskedBankIgnoresDifferentPixelsOutsideTheDrawnTemplate()
    {
        var pixels = Background(); Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 249, 153, masked: true);
        using var frame = Frame(pixels);
        using var result = await Run(Definition(Parameters(Bank(Entry("masked", 1, masked: true), Entry("other")))), frame);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal("masked", match.TemplateId); Assert.Equal(249, match.X); Assert.Equal(153, match.Y); Assert.InRange(match.Score, .99, 1);
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed)]
    [InlineData(VisionExecutionBackend.Native)]
    public async Task BankSearchRoiRemainsLocalWhileReportedCoordinatesIncludeFrameOrigin(VisionExecutionBackend backend)
    {
        var pixels = Background();
        Stamp(pixels, Pixels(), ModelWidth, ModelHeight, 21, 35);
        Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 289, 173);
        var parameters = Parameters();
        parameters["roi"] = new JsonObject { ["shape"] = "rectangle", ["x"] = 270, ["y"] = 150, ["width"] = 80, ["height"] = 65 };
        using var frame = Frame(pixels, 600, -20); using var result = await Run(Definition(parameters), frame, backend);
        var match = Assert.Single(result.GetMatches());
        Assert.Equal("best", match.TemplateId); Assert.Equal(889, match.X); Assert.Equal(153, match.Y);
        Assert.Equal(909, match.CenterX); Assert.Equal(169, match.CenterY);
    }

    [Theory]
    [InlineData(VisionExecutionBackend.Managed)]
    [InlineData(VisionExecutionBackend.Native)]
    public async Task EmptyNextFrameNeverReusesThePreviousWinningTemplateOrPosition(VisionExecutionBackend backend)
    {
        using var engine = new VisionEngine(store, backend);
        using var recipe = engine.LoadRecipe(Save(Definition(Parameters())));
        await using var session = recipe.CreateSession();
        var pixels = Background(); Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 249, 153);
        using var first = Frame(pixels); using var empty = Frame(Background());
        using var matched = await session.RunAsync(first);
        Assert.Equal("best", Assert.Single(matched.GetMatches()).TemplateId);
        using var missing = await session.RunAsync(empty);
        Assert.Equal(ExecutionStatus.Succeeded, missing.Status); Assert.Empty(missing.GetMatches()); Assert.Empty(missing.GetOverlays("Overlay"));
        var measurement = missing.GetMeasurement<VisionMatch>("Measurement");
        Assert.Equal(0, measurement.Count); Assert.Null(measurement.Selected); Assert.False(measurement.SelectedValid);
        var packet = missing.GetResult<VisionMatch>("Result");
        Assert.Null(packet.MatchPosition); Assert.False(packet.Found);
    }

    [Fact]
    public async Task ASealedSavedBankRetainsIdsNamesEnableStateAndWinnerAfterReload()
    {
        var parameters = Parameters(Bank(Entry("unused", 0, enabled: false), Entry("winner", 1)));
        parameters["model"]!["templates"]![1]!["name"] = "零件 B，第二个模板";
        string path = Save(Definition(parameters));
        var pixels = Background(); Stamp(pixels, Pixels(1), ModelWidth, ModelHeight, 91, 87);
        using var frame = Frame(pixels);
        using var engine = new VisionEngine(store);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            using var recipe = engine.LoadRecipe(path); await using var session = recipe.CreateSession();
            using var result = await session.RunAsync(frame);
            var match = Assert.Single(result.GetMatches());
            Assert.Equal("winner", match.TemplateId); Assert.Equal("零件 B，第二个模板", match.TemplateName);
        }
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "recipe.json")))!;
        Assert.False(saved["nodes"]![1]!["parameters"]!["model"]!["templates"]![0]!["enabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("empty-bank")] [InlineData("too-many")] [InlineData("all-disabled")] [InlineData("duplicate-id")]
    [InlineData("id-empty")] [InlineData("id-non-ascii")] [InlineData("id-too-long")] [InlineData("id-invalid-character")]
    [InlineData("name-empty")] [InlineData("name-utf8-too-long")]
    [InlineData("missing-id")] [InlineData("missing-name")] [InlineData("missing-enabled")] [InlineData("missing-model")]
    [InlineData("enabled-not-boolean")] [InlineData("unknown-entry-field")] [InlineData("unknown-bank-field")]
    [InlineData("disabled-invalid-model")] [InlineData("disabled-flat-model")] [InlineData("bank-not-array")]
    [InlineData("mixed-bank-and-single-model")] [InlineData("entry-not-object")]
    public void InvalidOrAmbiguousBankDefinitionsAreRejectedBeforeRunning(string invalid)
    {
        var parameters = Parameters(); var bank = parameters["model"]!.AsObject();
        var entries = bank["templates"]!.AsArray(); var first = entries[0]!.AsObject();
        switch (invalid)
        {
            case "empty-bank": entries.Clear(); break;
            case "too-many": entries.Clear(); for (int i = 0; i < 9; i++) entries.Add(Entry("item-" + i)); break;
            case "all-disabled": foreach (var entry in entries) entry!["enabled"] = false; break;
            case "duplicate-id": entries[1]!["id"] = "first"; break;
            case "id-empty": first["id"] = ""; break;
            case "id-non-ascii": first["id"] = "工件"; break;
            case "id-too-long": first["id"] = new string('a', 65); break;
            case "id-invalid-character": first["id"] = "item.1"; break;
            case "name-empty": first["name"] = ""; break;
            case "name-utf8-too-long": first["name"] = new string('件', 43); break;
            case "missing-id": first.Remove("id"); break;
            case "missing-name": first.Remove("name"); break;
            case "missing-enabled": first.Remove("enabled"); break;
            case "missing-model": first.Remove("model"); break;
            case "enabled-not-boolean": first["enabled"] = "false"; break;
            case "unknown-entry-field": first["active"] = true; break;
            case "unknown-bank-field": bank["active"] = true; break;
            case "disabled-invalid-model": first["enabled"] = false; first["model"] = new JsonObject(); break;
            case "disabled-flat-model": first["enabled"] = false; first["model"]!["grayBase64"] = Convert.ToBase64String(new byte[ModelWidth * ModelHeight]); break;
            case "bank-not-array": bank["templates"] = new JsonObject(); break;
            case "mixed-bank-and-single-model": bank["width"] = ModelWidth; break;
            case "entry-not-object": entries[0] = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        using var engine = new VisionEngine(store);
        AssertInvalidRecipe(engine, parameters);
    }

    [Theory]
    [InlineData(.49, 1, .05)] [InlineData(1, 2.01, .05)] [InlineData(1, 1, .009)] [InlineData(1, 1, 1.01)]
    [InlineData(1.2, .9, .05)] [InlineData(.5, 2, .01)]
    public void InvalidScaleRangesAndCombinedTemplateScaleBudgetsAreRejected(double minimum, double maximum, double step)
    {
        var parameters = Parameters(); parameters["scaleMinimum"] = minimum; parameters["scaleMaximum"] = maximum; parameters["scaleStep"] = step;
        using var engine = new VisionEngine(store);
        AssertInvalidRecipe(engine, parameters);
    }

    [Fact]
    public void DisabledEntriesDoNotConsumeTheSearchPoseBudget()
    {
        var parameters = Parameters(Bank(Enumerable.Range(0, 8).Select(i => Entry("item-" + i, enabled: i == 7)).ToArray()));
        parameters["scaleMinimum"] = .5; parameters["scaleMaximum"] = 1.7; parameters["scaleStep"] = .01;
        using var engine = new VisionEngine(store);
        engine.ValidateRecipe(VisionEngine.SealRecipe(Definition(parameters).ToJsonString()));
    }

    [Theory]
    [InlineData(.5, 2.0)]   // 16 ordinary grid scales * 8 enabled templates = 128 poses.
    [InlineData(.51, 1.91)] // The added exact 1.0 scale must count toward the same budget.
    [InlineData(.5, 1.91)]  // The non-grid maximum endpoint must also count.
    public void CombinedBudgetCountsEveryEnabledTemplateAndAdditionalRequiredScale(double minimum, double maximum)
    {
        var parameters = Parameters(Bank(Enumerable.Range(0, 8).Select(i => Entry("item-" + i)).ToArray()));
        parameters["scaleMinimum"] = minimum; parameters["scaleMaximum"] = maximum; parameters["scaleStep"] = .1;
        using var engine = new VisionEngine(store);
        AssertInvalidRecipe(engine, parameters);
    }

    [Fact]
    public void IdAndUtf8NameMaximumLengthsAreInclusive()
    {
        var entry = Entry(new string('a', 64)); entry["name"] = new string('件', 42) + "AB"; // 128 UTF-8 bytes.
        using var engine = new VisionEngine(store);
        engine.ValidateRecipe(VisionEngine.SealRecipe(Definition(Parameters(Bank(entry))).ToJsonString()));
    }

    private static void AssertInvalidRecipe(VisionEngine engine, JsonObject parameters)
    {
        var error = Record.Exception(() => engine.ValidateRecipe(VisionEngine.SealRecipe(Definition(parameters).ToJsonString())));
        Assert.True(error is VisionException or ArgumentException,
            "Expected recipe validation rejection, got " + (error?.GetType().Name ?? "no error"));
    }

    public void Dispose()
    {
        // This GUID directory is created by this test and contains no user recipe or license issuer key.
        if (Directory.Exists(store)) Directory.Delete(store, true);
    }
}

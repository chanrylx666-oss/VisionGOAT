using JPVision.Sdk;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class DeepLearningModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "JPVision-model-tests-" + Guid.NewGuid().ToString("N"));
    public DeepLearningModelTests() => Directory.CreateDirectory(directory);
    private string RepositoryRoot => Path.Combine(directory, "repository");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static JsonObject Manifest(byte[] model) => new()
    {
        ["schemaVersion"] = 1, ["modelId"] = "test-defects", ["version"] = "1.0", ["task"] = "binarySegmentation",
        ["modelFile"] = "model.onnx", ["modelSha256"] = Hash(model),
        ["input"] = new JsonObject { ["name"] = "input", ["width"] = 64, ["height"] = 64, ["channels"] = 1,
            ["layout"] = "NCHW", ["dtype"] = "float32", ["color"] = "Gray", ["scale"] = 1d / 255,
            ["mean"] = new JsonArray(0), ["std"] = new JsonArray(1) },
        ["output"] = new JsonObject { ["name"] = "scores", ["layout"] = "NCHW", ["dtype"] = "float32", ["activation"] = "none", ["scoreKind"] = "probability" },
        ["labels"] = new JsonArray("background", "defect")
    };
    private string WriteBundle(JsonObject? manifest = null)
    {
        string source = Path.Combine(directory, "source-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(source);
        byte[] model = Encoding.UTF8.GetBytes("integrity-only test bytes; not a trained ONNX graph");
        File.WriteAllBytes(Path.Combine(source, "model.onnx"), model);
        File.WriteAllText(Path.Combine(source, "manifest.json"), (manifest ?? Manifest(model)).ToJsonString(), new UTF8Encoding(false));
        return Path.Combine(source, "manifest.json");
    }

    [Fact]
    public async Task ImportPinsExactManifestAndCopiesIndependentBytes()
    {
        string source = WriteBundle(); var repository = new DeepLearningModelRepository(RepositoryRoot);
        var model = await repository.ImportAsync(source);
        Assert.Equal(Hash(File.ReadAllBytes(source)), model.ModelKey);
        Assert.Equal(Path.Combine(RepositoryRoot, model.ModelKey), model.Directory);
        Assert.Equal("test-defects", model.Manifest.ModelId);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(source)!, "model.onnx"), "changed original");
        Assert.Equal(model.ModelKey, (await repository.GetAsync(model.ModelKey)).ModelKey);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task IdenticalImportsAreIdempotentAndPublishOnlyOneCompleteDirectory()
    {
        string source = WriteBundle(); var repository = new DeepLearningModelRepository(RepositoryRoot);
        var models = await Task.WhenAll(repository.ImportAsync(source), repository.ImportAsync(source));
        Assert.Equal(models[0].ModelKey, models[1].ModelKey);
        Assert.Single(Directory.GetDirectories(RepositoryRoot));
        Assert.Equal(2, Directory.GetFiles(models[0].Directory).Length);
    }

    [Fact]
    public async Task PreprocessingChangesCreateDifferentPinnedBundles()
    {
        string first = WriteBundle(); var second = JsonNode.Parse(File.ReadAllText(first))!.AsObject();
        second["input"]!["mean"]![0] = .25;
        string secondPath = WriteBundle(second); var repository = new DeepLearningModelRepository(RepositoryRoot);
        var a = await repository.ImportAsync(first); var b = await repository.ImportAsync(secondPath);
        Assert.NotEqual(a.ModelKey, b.ModelKey);
        Assert.Equal(a.Manifest.ModelSha256, b.Manifest.ModelSha256);
    }

    [Fact]
    public async Task FailedImportLeavesExistingModelUntouchedAndCleansOnlyItsStaging()
    {
        string source = WriteBundle(); var repository = new DeepLearningModelRepository(RepositoryRoot);
        var installed = await repository.ImportAsync(source);
        var corrupt = JsonNode.Parse(File.ReadAllText(source))!.AsObject(); corrupt["version"] = "bad"; corrupt["modelSha256"] = new string('f', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ImportAsync(WriteBundle(corrupt)));
        Assert.Equal(installed.ModelKey, (await repository.GetAsync(installed.ModelKey)).ModelKey);
        Assert.Single(Directory.GetDirectories(RepositoryRoot));
    }

    [Fact]
    public async Task CancellationBeforeImportCreatesNoRepositoryOrPartialModel()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeepLearningModelRepository(RepositoryRoot).ImportAsync(WriteBundle(), cancellation.Token));
        Assert.False(Directory.Exists(RepositoryRoot));
    }

    [Theory]
    [InlineData("model.onnx")]
    [InlineData("manifest.json")]
    public async Task TamperedInstalledFilesFailIntegrityChecks(string file)
    {
        var repository = new DeepLearningModelRepository(RepositoryRoot); var model = await repository.ImportAsync(WriteBundle());
        File.AppendAllText(Path.Combine(model.Directory, file), file == "manifest.json" ? " " : "tamper");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(model.ModelKey));
    }

    [Theory]
    [InlineData("../model.onnx")]
    [InlineData("C:\\other.onnx")]
    [InlineData("weights.onnx")]
    public async Task ModelFileCannotEscapeFixedBundle(string path)
    {
        string source = WriteBundle(); var manifest = JsonNode.Parse(File.ReadAllText(source))!.AsObject(); manifest["modelFile"] = path;
        File.WriteAllText(source, manifest.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => new DeepLearningModelRepository(RepositoryRoot).ImportAsync(source));
        Assert.False(Directory.Exists(RepositoryRoot));
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("task", "\"classification\"")]
    [InlineData("modelSha256", "\"bad\"")]
    [InlineData("labels", "[\"scratch\",\"dent\"]")]
    [InlineData("input", "null")]
    public void UnsupportedManifestFieldsAreRejected(string field, string value)
    {
        var manifest = Manifest([1, 2, 3]); manifest[field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Theory]
    [InlineData("width", "31")]
    [InlineData("height", "2049")]
    [InlineData("channels", "2")]
    [InlineData("color", "\"BGR\"")]
    [InlineData("layout", "\"NHWC\"")]
    [InlineData("dtype", "\"float16\"")]
    [InlineData("scale", "0")]
    [InlineData("mean", "[0,0]")]
    [InlineData("std", "[0]")]
    public void UnsupportedInputContractIsRejected(string field, string value)
    {
        var manifest = Manifest([1, 2, 3]); manifest["input"]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Fact]
    public void DuplicateKeysAndOversizedManifestAreRejected()
    {
        string json = Manifest([1]).ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(new byte[DeepLearningModelRepository.MaximumManifestBytes + 1]));
    }

    [Fact]
    public void AnomalyScoresCannotPretendToBeProbabilities()
    {
        var manifest = Manifest([1]); manifest["task"] = "anomalySegmentation";
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
        manifest["output"]!["scoreKind"] = "anomaly";
        Assert.Equal("anomalySegmentation", DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())).Task);
        manifest["output"]!["activation"] = "sigmoid";
        Assert.Throws<InvalidDataException>(() => DeepLearningModelManifest.Parse(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("ABC")]
    [InlineData("")]
    public async Task RepositoryRejectsUnpinnedModelKeys(string key)
        => await Assert.ThrowsAsync<ArgumentException>(() => new DeepLearningModelRepository(RepositoryRoot).GetAsync(key));

    private static JsonObject Defects() => new()
    {
        ["resultKind"] = "deepLearningDefect", ["featureKind"] = "region", ["coordinateSpace"] = "original", ["coordinatePlane"] = "",
        ["dataAvailable"] = true, ["valid"] = true, ["reason"] = "DefectsFound", ["modelKey"] = new string('a', 64), ["modelId"] = "fixture", ["modelVersion"] = "1",
        ["count"] = 1, ["found"] = true, ["passed"] = false, ["scoreKind"] = "probability", ["maximumScore"] = .9, ["totalArea"] = 4, ["tileCount"] = 1,
        ["features"] = new JsonArray(new JsonObject { ["label"] = "defect", ["score"] = .9, ["area"] = 4, ["areaKind"] = "pixelCount", ["perimeter"] = 8, ["centroidX"] = 10.5, ["centroidY"] = 20.5,
            ["centroidValid"] = true, ["x"] = 10, ["y"] = 20, ["width"] = 2, ["height"] = 2, ["circularity"] = Math.PI / 4 }),
        ["overlay"] = new JsonArray()
    };
    private static InspectionResult Result(JsonObject packet, bool attached = false, string status = "Succeeded")
    {
        JsonNode value = attached ? new JsonObject { ["pipeline"] = new JsonObject { ["results"] = new JsonObject { ["Result"] = packet } } } : packet;
        var data = new JsonObject { ["executionStatus"] = status, ["decision"] = "Unknown", ["elapsedMs"] = 1,
            ["runId"] = "deep-learning-contract", ["outputs"] = new JsonObject { ["result"] = value } };
        var handleType = typeof(InspectionResult).Assembly.GetType("JPVision.Sdk.ResultHandle", true)!;
        var handle = Activator.CreateInstance(handleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { IntPtr.Zero }, null)!;
        var constructor = typeof(InspectionResult).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { handleType, typeof(string) }, null)!;
        return (InspectionResult)constructor.Invoke(new[] { handle, data.ToJsonString() });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SdkReadsPublishedAndImageAttachedDefects(bool attached)
    {
        using var result = Result(Defects(), attached); var defects = result.GetDeepLearningDefects("result");
        Assert.Equal(4, defects.TotalArea); Assert.Equal(10.5, Assert.Single(defects.Features).CentroidX);
        Assert.False(defects.Passed);
    }

    [Theory]
    [InlineData("count", "0")]
    [InlineData("passed", "true")]
    [InlineData("valid", "false")]
    [InlineData("totalArea", "5")]
    [InlineData("maximumScore", "0.1")]
    [InlineData("maximumScore", "1.1")]
    [InlineData("scoreKind", "\"confidence\"")]
    [InlineData("tileCount", "0")]
    [InlineData("modelKey", "\"bad\"")]
    public void SdkRejectsContradictoryOrUnpinnedDefectResults(string field, string value)
    {
        var packet = Defects(); packet[field] = JsonNode.Parse(value); using var result = Result(packet);
        Assert.Throws<InvalidDataException>(() => result.GetDeepLearningDefects("result"));
    }

    [Fact]
    public void MissingInferenceCanNeverBeReturnedAsPassingInspection()
    {
        var packet = Defects(); packet["dataAvailable"] = false; packet["valid"] = false; packet["count"] = 0; packet["found"] = false;
        packet["features"] = new JsonArray(); packet["maximumScore"] = packet["totalArea"] = null; packet["scoreKind"] = ""; packet["tileCount"] = 0; packet["reason"] = "UnavailableInputImage";
        using (var missing = Result(packet.DeepClone().AsObject())) Assert.False(missing.GetDeepLearningDefects("result").Passed);
        packet["passed"] = true; using var invalid = Result(packet);
        Assert.Throws<InvalidDataException>(() => invalid.GetDeepLearningDefects("result"));
    }

    [Theory]
    [InlineData("scoreKind")]
    [InlineData("areaKind")]
    public void SdkRequiresScoreAndAreaSemantics(string missing)
    {
        var packet = Defects();
        if (missing == "scoreKind") packet.Remove(missing);
        else packet["features"]![0]!.AsObject().Remove(missing);
        using var result = Result(packet);
        Assert.Throws<InvalidDataException>(() => result.GetDeepLearningDefects("result"));
    }

    [Theory]
    [InlineData("perimeter", "3")]
    [InlineData("perimeter", "8.5")]
    [InlineData("perimeter", "17")]
    [InlineData("areaKind", "\"contourArea\"")]
    public void SdkRejectsNonPixelRegionMeasurements(string field, string value)
    {
        var packet = Defects(); packet["features"]![0]![field] = JsonNode.Parse(value);
        using var result = Result(packet);
        Assert.Throws<InvalidDataException>(() => result.GetDeepLearningDefects("result"));
    }

    [Fact]
    public void OnlyAnomalyScoresCanExceedOne()
    {
        var packet = Defects(); packet["maximumScore"] = 2.5; packet["features"]![0]!["score"] = 2.5;
        using (var probability = Result(packet.DeepClone().AsObject()))
            Assert.Throws<InvalidDataException>(() => probability.GetDeepLearningDefects("result"));
        packet["scoreKind"] = "anomaly";
        using var anomaly = Result(packet);
        Assert.Equal(2.5, anomaly.GetDeepLearningDefects("result").MaximumScore);
    }

    [Fact]
    public void UnavailableResultsCannotDeclareAValidScoreKind()
    {
        var packet = Defects(); packet["dataAvailable"] = false; packet["valid"] = false; packet["count"] = 0; packet["found"] = false;
        packet["features"] = new JsonArray(); packet["maximumScore"] = packet["totalArea"] = null; packet["tileCount"] = 0; packet["reason"] = "UnavailableInputImage";
        using var result = Result(packet);
        Assert.Throws<InvalidDataException>(() => result.GetDeepLearningDefects("result"));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(directory);
        if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVision.Sdk;

/// <summary>The supported portable contract for one-channel defect-score ONNX models.</summary>
public sealed record DeepLearningModelManifest
{
    [JsonPropertyName("schemaVersion"), JsonRequired] public int SchemaVersion { get; init; }
    [JsonPropertyName("modelId"), JsonRequired] public string ModelId { get; init; } = "";
    [JsonPropertyName("version"), JsonRequired] public string Version { get; init; } = "";
    [JsonPropertyName("task"), JsonRequired] public string Task { get; init; } = "";
    [JsonPropertyName("modelFile"), JsonRequired] public string ModelFile { get; init; } = "";
    [JsonPropertyName("modelSha256"), JsonRequired] public string ModelSha256 { get; init; } = "";
    [JsonPropertyName("input"), JsonRequired] public DeepLearningModelInput Input { get; init; } = null!;
    [JsonPropertyName("output"), JsonRequired] public DeepLearningModelOutput Output { get; init; } = null!;
    [JsonPropertyName("labels"), JsonRequired] public string[] Labels { get; init; } = [];

    /// <summary>Validate a manifest without running its graph. Tensor/operation compatibility is checked at inference.</summary>
    public static DeepLearningModelManifest Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > DeepLearningModelRepository.MaximumManifestBytes)
            throw new InvalidDataException("模型清单为空或超过 64 KiB。");
        try
        {
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
            RejectDuplicateKeys(json.RootElement);
            var value = json.RootElement.Deserialize<DeepLearningModelManifest>() ?? throw new InvalidDataException("模型清单为空。");
            value.Validate();
            return value;
        }
        catch (JsonException ex) { throw new InvalidDataException("模型清单不是有效的 JSON 合同。", ex); }
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("模型清单包含重复字段：" + property.Name);
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private void Validate()
    {
        static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
        if (SchemaVersion != 1 || !Name(ModelId) || !Name(Version) ||
            Task is not ("binarySegmentation" or "anomalySegmentation") || ModelFile != "model.onnx" ||
            !DeepLearningModelRepository.IsModelKey(ModelSha256) || Input is null || Output is null ||
            Labels is null || !Labels.SequenceEqual(new[] { "background", "defect" }))
            throw new InvalidDataException("不支持的模型清单版本、任务、文件、哈希或类别。");
        if (!Name(Input.Name) || Input.Width is < 32 or > 2048 || Input.Height is < 32 or > 2048 ||
            (long)Input.Width * Input.Height > 4 * 1024 * 1024 || Input.Channels is not (1 or 3) ||
            Input.Layout != "NCHW" || Input.Dtype != "float32" ||
            Input.Color != (Input.Channels == 1 ? "Gray" : "RGB") ||
            !double.IsFinite(Input.Scale) || Input.Scale <= 0 || Input.Scale > 1 ||
            Input.Mean is null || Input.Std is null || Input.Mean.Length != Input.Channels || Input.Std.Length != Input.Channels ||
            Input.Mean.Any(v => !double.IsFinite(v)) || Input.Std.Any(v => !double.IsFinite(v) || v <= 0))
            throw new InvalidDataException("模型输入须为固定尺寸的 Gray/RGB、NCHW、float32 张量，且归一化参数有效。");
        if (!Name(Output.Name) || Output.Layout != "NCHW" || Output.Dtype != "float32" ||
            Output.Activation is not ("none" or "sigmoid") || Output.ScoreKind is not ("probability" or "anomaly") ||
            (Task == "anomalySegmentation") != (Output.ScoreKind == "anomaly") ||
            Output.ScoreKind == "anomaly" && Output.Activation != "none")
            throw new InvalidDataException("模型输出须为单通道缺陷分数；异常分数不能声明为分类概率。");
    }
}

public sealed record DeepLearningModelInput
{
    [JsonPropertyName("name"), JsonRequired] public string Name { get; init; } = "";
    [JsonPropertyName("width"), JsonRequired] public int Width { get; init; }
    [JsonPropertyName("height"), JsonRequired] public int Height { get; init; }
    [JsonPropertyName("channels"), JsonRequired] public int Channels { get; init; }
    [JsonPropertyName("layout"), JsonRequired] public string Layout { get; init; } = "";
    [JsonPropertyName("dtype"), JsonRequired] public string Dtype { get; init; } = "";
    [JsonPropertyName("color"), JsonRequired] public string Color { get; init; } = "";
    [JsonPropertyName("scale"), JsonRequired] public double Scale { get; init; }
    [JsonPropertyName("mean"), JsonRequired] public double[] Mean { get; init; } = [];
    [JsonPropertyName("std"), JsonRequired] public double[] Std { get; init; } = [];
}

public sealed record DeepLearningModelOutput
{
    [JsonPropertyName("name"), JsonRequired] public string Name { get; init; } = "";
    [JsonPropertyName("layout"), JsonRequired] public string Layout { get; init; } = "";
    [JsonPropertyName("dtype"), JsonRequired] public string Dtype { get; init; } = "";
    [JsonPropertyName("activation"), JsonRequired] public string Activation { get; init; } = "";
    [JsonPropertyName("scoreKind"), JsonRequired] public string ScoreKind { get; init; } = "";
}

/// <summary>A model bundle pinned by the SHA-256 of its exact manifest bytes.</summary>
public sealed record InstalledDeepLearningModel(string ModelKey, string Directory, DeepLearningModelManifest Manifest)
{
    public override string ToString() => $"{Manifest.ModelId} · {Manifest.Version} · {ModelKey[..12]}";
}

/// <summary>Independent model storage; recipe resources remain small. Import validates bytes, not ONNX operator support.</summary>
public sealed class DeepLearningModelRepository
{
    public const int MaximumManifestBytes = 64 * 1024;
    public const long MaximumModelBytes = 512L * 1024 * 1024;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JP-Vision", "Models");
    public string Root { get; }

    public DeepLearningModelRepository(string? root = null)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? DefaultRoot : root);
        RejectReparsePoints(Root);
    }

    public static bool IsModelKey(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<InstalledDeepLearningModel> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(manifestPath);
        RejectReparsePoints(source);
        byte[] manifestBytes = await ReadManifestAsync(source, cancellationToken).ConfigureAwait(false);
        var manifest = DeepLearningModelManifest.Parse(manifestBytes);
        string key = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string modelSource = Path.Combine(Path.GetDirectoryName(source)!, "model.onnx");
        RejectReparsePoints(modelSource);
        RejectReparsePoints(Root);
        Directory.CreateDirectory(Root);
        string destination = Path.Combine(Root, key);
        if (Directory.Exists(destination)) return await GetAsync(key, cancellationToken).ConfigureAwait(false);
        string temporary = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await CopyAndVerifyAsync(modelSource, Path.Combine(temporary, "model.onnx"), manifest.ModelSha256, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "manifest.json"), manifestBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoints(Root);
            try { Directory.Move(temporary, destination); }
            catch (IOException) when (Directory.Exists(destination)) { return await GetAsync(key, cancellationToken).ConfigureAwait(false); }
            return new(key, destination, manifest);
        }
        finally
        {
            // Only our unpredictable staging directory is removable, never an installed bundle.
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    public async Task<InstalledDeepLearningModel> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!IsModelKey(key)) throw new ArgumentException("模型键必须为 64 位小写 SHA-256。", nameof(key));
        string directory = Path.Combine(Root, key);
        RejectReparsePoints(directory);
        byte[] bytes = await ReadManifestAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != key)
            throw new InvalidDataException("已安装模型清单被修改，哈希不匹配。");
        var manifest = DeepLearningModelManifest.Parse(bytes);
        await VerifyModelAsync(Path.Combine(directory, "model.onnx"), manifest.ModelSha256, cancellationToken).ConfigureAwait(false);
        return new(key, directory, manifest);
    }

    /// <summary>Lists verified bundles. Corrupt installed bundles are reported, never silently accepted.</summary>
    public async Task<IReadOnlyList<InstalledDeepLearningModel>> ListAsync(CancellationToken cancellationToken = default)
    {
        RejectReparsePoints(Root);
        if (!Directory.Exists(Root)) return Array.Empty<InstalledDeepLearningModel>();
        var models = new List<InstalledDeepLearningModel>();
        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = Path.GetFileName(directory);
            if (IsModelKey(key)) models.Add(await GetAsync(key, cancellationToken).ConfigureAwait(false));
        }
        return models.OrderBy(model => model.Manifest.ModelId, StringComparer.Ordinal).ThenBy(model => model.Manifest.Version, StringComparer.Ordinal).ToArray();
    }

    private static async Task<byte[]> ReadManifestAsync(string path, CancellationToken token)
    {
        RejectReparsePoints(path);
        await using var input = OpenInput(path, MaximumManifestBytes);
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static FileStream OpenInput(string path, long limit)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > limit) { stream.Dispose(); throw new InvalidDataException("模型文件为空或超过大小限制。"); }
        return stream;
    }

    private static async Task VerifyModelAsync(string source, string hash, CancellationToken token)
    {
        RejectReparsePoints(source);
        await using var input = OpenInput(source, MaximumModelBytes);
        byte[] actual = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        if (Convert.ToHexString(actual).ToLowerInvariant() != hash) throw new InvalidDataException("模型文件 SHA-256 不匹配。");
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, string hash, CancellationToken token)
    {
        RejectReparsePoints(source);
        await using (var input = OpenInput(source, MaximumModelBytes))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        await VerifyModelAsync(destination, hash, token).ConfigureAwait(false);
    }

    private static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("模型路径不能包含符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}

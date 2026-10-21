using JPVision.Sdk;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JPVision.Editor.Core;

public sealed record EditorExecutionTiming(bool CacheHit, double PreparationMs, double ExecutionMs, double TotalMs);

/// <summary>Reuses one compiled editor recipe. Frames, requests and results are never cached.</summary>
public sealed class EditorExecutionCache : IDisposable, IAsyncDisposable
{
    private readonly VisionEngine engine;
    private readonly string cacheRoot;
    private readonly object gate = new();
    private VisionRecipe? recipe;
    private VisionSession? session;
    private string? definition, folder;
    private bool disposed;
    private Task<InspectionResult>? active;
    private Task? disposal;
    private EditorExecutionTiming? lastRunTiming;

    public EditorExecutionCache(VisionEngine engine, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        this.engine = engine;
        this.cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
    }

    public EditorExecutionTiming? LastRunTiming { get { lock (gate) return lastRunTiming; } }

    /// <summary>The caller must keep the frame alive and request dictionaries unchanged until completion.</summary>
    public Task<InspectionResult> RunAsync(string definitionJson, VisionFrame frame, RunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is { IsCompleted: false })
                throw new InvalidOperationException("The editor execution cache is already running.");
            lastRunTiming = null;
            // Sealing, validation, loading and run acceptance can all be expensive for large recipes.
            var task = Task.Run(() => RunCoreAsync(definitionJson, frame, request, cancellationToken), cancellationToken);
            active = task;
            _ = task.ContinueWith(completed =>
            {
                lock (gate)
                    if (ReferenceEquals(active, completed)) active = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<InspectionResult> RunCoreAsync(string json, VisionFrame frame, RunRequest request, CancellationToken token)
    {
        var total = Stopwatch.StartNew();
        bool hit = session is not null && string.Equals(definition, json, StringComparison.Ordinal);
        if (!hit)
        {
            ReleaseEntry();
            if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
                throw new InvalidDataException("配方文件超过 16 MiB。");
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty("resources", out var resources) &&
                    resources.ValueKind == JsonValueKind.Array && resources.GetArrayLength() > 0)
                    throw new InvalidOperationException("当前编辑器使用内嵌模板；外部资源配方请通过 SDK 运行。");
            }
            token.ThrowIfCancellationRequested();
            string sealedJson = VisionEngine.SealRecipe(json);
            engine.ValidateRecipe(sealedJson);
            folder = Path.Combine(cacheRoot, "execution-" + Guid.NewGuid().ToString("N") + ".vrecipe");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "recipe.json"), sealedJson, new UTF8Encoding(false));
                token.ThrowIfCancellationRequested();
                recipe = engine.LoadRecipe(folder);
                session = recipe.CreateSession();
                definition = json;
            }
            catch { ReleaseEntry(); throw; }
        }
        token.ThrowIfCancellationRequested();
        double preparationMs = total.Elapsed.TotalMilliseconds;
        var execution = Stopwatch.StartNew();
        try
        {
            return await session!.RunAsync(frame, request, token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
                lastRunTiming = new(hit, preparationMs, execution.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds);
        }
    }

    private void ReleaseEntry()
    {
        session?.Dispose(); session = null;
        recipe?.Dispose(); recipe = null;
        definition = null;
        if (folder is null) return;
        string path = Path.GetFullPath(folder);
        // Delete only the private GUID directory created by this instance, never the supplied root.
        if (!string.Equals(Path.GetDirectoryName(path), cacheRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("execution-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid editor cache directory.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
        folder = null;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            disposal ??= DisposeCoreAsync(active);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Task<InspectionResult>? pending)
    {
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch { /* The caller of RunAsync observes its failure; disposal still releases the entry. */ }
        }
        ReleaseEntry();
    }
}

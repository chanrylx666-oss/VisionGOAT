using JPVision.Acquisition;
using JPVision.Editor.Core;
using JPVision.Sdk;
using JPVision.Studio;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private sealed record Metric(double Median, double P95, double Minimum, double Maximum);
    private sealed record Sample(double WallMs, double NativeMs, long ManagedAllocatedBytes, double PreparationMs, double ExecutionMs);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, object?> report = new();
    private static string runRoot = "";

    [STAThread]
    private static void Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task RunAsync(string[] args)
    {
        string repository = Path.GetFullPath(args[0]);
        runRoot = Path.GetFullPath(args[1]);
        int iterations = int.Parse(args[2]);
        if (iterations < 20) throw new ArgumentOutOfRangeException(nameof(iterations));
        Directory.CreateDirectory(runRoot);
        string native = Path.Combine(AppContext.BaseDirectory, "JPVision.Runtime.dll");
        report["startedAt"] = DateTimeOffset.Now;
        report["configuration"] = "Debug";
        report["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        report["logicalProcessors"] = Environment.ProcessorCount;
        report["nativeDll"] = new { path = native, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native))) };
        report["productSources"] = new[]
        {
            "managed/JPVision.Editor.Core/EditorExecutionCache.cs", "managed/JPVision.Studio.Wpf/CameraPreviewFrame.cs"
        }.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repository, path)))));
        report["protocol"] = "Same process and full-resolution synthetic images; warm 3 iterations per mode then alternate order for 20+ measured iterations. No camera. Rebuild reproduces editor Seal/Validate/write/LoadRecipe/CreateSession/dispose/delete; optimized calls production EditorExecutionCache. Both serialize definition every run and verify full-resolution output and blob area. Wall includes verification and result disposal. Preview creates/freeze bitmaps on STA but excludes acquisition, GPU composition and actual window rendering. Allocation is whole-process managed GC delta, not native/WPF total memory; disk cache warm, not OS-cold.";
        report["iterationsPerMode"] = iterations;
        report["warmupPerMode"] = 3;
        try
        {
            string store = Path.Combine(runRoot, "license-store");
            if (!VisionLicensing.Import(Path.Combine(repository, "artifacts/activation/current.vlic"), store).IsValid)
                throw new InvalidOperationException("Normal license import failed.");
            report["license"] = "Existing current.vlic imported into private GUID test store. No issuer or private key used.";
            const int size = 5120, radius = 1500;
            byte[] pixels = Disk(size, radius, out long area);
            report["input"] = new { width = size, height = size, pixelFormat = "Gray8", bytes = pixels.Length, foregroundPixels = area, radius };
            var camera = CameraFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8,
                new("SYNTHETIC", 1, 0, Stopwatch.GetTimestamp(), DateTimeOffset.Now));
            report["preview"] = Preview(camera, iterations);
            Save();
            using var engine = new VisionEngine(store);
            using var frame = VisionFrame.CopyFrom(pixels, size, size, size, VisionPixelFormat.Gray8);
            var workloads = new List<object>(); report["workloads"] = workloads;
            workloads.Add(await Compare(engine, frame, Definition(false), "source-gray", area, iterations)); Save();
            workloads.Add(await Compare(engine, frame, Definition(true), "blob-full-image", area, iterations)); Save();
            report["succeeded"] = true;
        }
        catch (Exception ex) { report["succeeded"] = false; report["error"] = ex.ToString(); throw; }
        finally { report["finishedAt"] = DateTimeOffset.Now; Save(); }
    }

    private static byte[] Disk(int size, int radius, out long area)
    {
        byte[] pixels = new byte[size * size]; area = 0;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            bool inside = (long)(x - size / 2) * (x - size / 2) + (long)(y - size / 2) * (y - size / 2) <= (long)radius * radius;
            pixels[y * size + x] = inside ? (byte)230 : (byte)20; if (inside) area++;
        }
        return pixels;
    }

    private static object Preview(CameraFrame camera, int iterations)
    {
        var old = new List<Sample>(); var direct = new List<Sample>();
        Sample Measure(bool legacy)
        {
            long allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew(); BitmapSource bitmap;
            if (legacy)
            {
                using var native = camera.ToVisionFrame(); byte[] copy = native.CopyPixels();
                bitmap = BitmapSource.Create(camera.Width, camera.Height, 96, 96, PixelFormats.Gray8, null, copy, camera.Stride);
                bitmap.Freeze();
            }
            else bitmap = CameraPreviewFrame.Create(camera).Bitmap;
            if (bitmap.PixelWidth != camera.Width || bitmap.PixelHeight != camera.Height || !bitmap.IsFrozen)
                throw new InvalidOperationException("Preview geometry/freeze changed.");
            byte[] pixel = new byte[1]; bitmap.CopyPixels(new Int32Rect(camera.Width / 2, camera.Height / 2, 1, 1), pixel, 1, 0);
            if (pixel[0] != 230) throw new InvalidOperationException("Preview pixel changed.");
            GC.KeepAlive(bitmap);
            return new(watch.Elapsed.TotalMilliseconds, 0, GC.GetTotalAllocatedBytes(true) - allocated, 0, 0);
        }
        for (int i = -3; i < iterations; i++)
        {
            Sample a, b;
            if ((i & 1) == 0) { a = Measure(true); b = Measure(false); } else { b = Measure(false); a = Measure(true); }
            if (i >= 0) { old.Add(a); direct.Add(b); }
        }
        Console.WriteLine($"Preview bitmap creation: old {Stats(old.Select(x => x.WallMs)).Median:F3} ms, direct {Stats(direct.Select(x => x.WallMs)).Median:F3} ms");
        return new { legacyRoundTrip = Summary(old), directSharedBitmap = Summary(direct), samplesLegacy = old, samplesDirect = direct };
    }

    private static async Task<object> Compare(VisionEngine engine, VisionFrame frame, JsonObject definition, string name, long area, int iterations)
    {
        await using var cache = new EditorExecutionCache(engine, Path.Combine(runRoot, name + "-cache"));
        bool blob = name == "blob-full-image";
        async Task<Sample> Measure(bool legacy)
        {
            long allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
            string json = definition.ToJsonString();
            var request = new RunRequest { PreviewNodeId = blob ? "detect" : "gray", TimeoutMs = 30000 };
            double preparation, execution, native;
            if (legacy)
            {
                json = VisionEngine.SealRecipe(json); engine.ValidateRecipe(json);
                string folder = Path.Combine(runRoot, "rebuild-" + Guid.NewGuid().ToString("N") + ".vrecipe");
                Directory.CreateDirectory(folder);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(folder, "recipe.json"), json, new UTF8Encoding(false));
                    using var recipe = engine.LoadRecipe(folder); await using var session = recipe.CreateSession();
                    preparation = watch.Elapsed.TotalMilliseconds; var executionWatch = Stopwatch.StartNew();
                    using var result = await session.RunAsync(frame, request); execution = executionWatch.Elapsed.TotalMilliseconds;
                    native = result.ElapsedMs; Check(result, frame.Info.Width, blob, area);
                }
                finally { File.Delete(Path.Combine(folder, "recipe.json")); Directory.Delete(folder, false); }
            }
            else
            {
                using var result = await cache.RunAsync(json, frame, request);
                var timing = cache.LastRunTiming!; preparation = timing.PreparationMs; execution = timing.ExecutionMs;
                native = result.ElapsedMs; Check(result, frame.Info.Width, blob, area);
            }
            return new(watch.Elapsed.TotalMilliseconds, native, GC.GetTotalAllocatedBytes(true) - allocated, preparation, execution);
        }
        for (int i = 0; i < 3; i++) { await Measure(true); await Measure(false); }
        var old = new List<Sample>(); var optimized = new List<Sample>();
        for (int i = 0; i < iterations; i++)
        {
            Sample a, b;
            if ((i & 1) == 0) { a = await Measure(true); b = await Measure(false); } else { b = await Measure(false); a = await Measure(true); }
            old.Add(a); optimized.Add(b);
        }
        Console.WriteLine($"{name}: rebuild {Stats(old.Select(x => x.WallMs)).Median:F3} ms, production cache {Stats(optimized.Select(x => x.WallMs)).Median:F3} ms");
        return new { name, rebuilt = Summary(old), productionCache = Summary(optimized), samplesRebuilt = old, samplesProductionCache = optimized };
    }

    private static void Check(InspectionResult result, int size, bool blob, long area)
    {
        if (result.Status != ExecutionStatus.Succeeded) throw new InvalidOperationException(result.Data.GetRawText());
        using var image = result.GetOutputImage("Image");
        if (image.Info.Width != size || image.Info.Height != size) throw new InvalidOperationException("Output resolution changed.");
        if (blob)
        {
            var packet = result.GetOutput<JsonElement>("Result");
            if (packet.GetProperty("count").GetInt32() != 1 || packet.GetProperty("features")[0].GetProperty("area").GetDouble() != area)
                throw new InvalidOperationException("Blob result changed.");
        }
    }

    private static JsonObject Definition(bool blob)
    {
        static JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
        static JsonObject Edge(string a, string b) => new() { ["fromNode"] = a, ["fromPort"] = "image", ["toNode"] = b, ["toPort"] = "input" };
        static JsonObject Output(string n, string p, string t) => new() { ["node"] = n, ["port"] = p, ["type"] = t };
        var nodes = new JsonArray(Node("source", "ExternalImage"), Node("gray", "ToGray"));
        var connections = new JsonArray(Edge("source", "gray"));
        var outputs = new JsonObject { ["Image"] = Output(blob ? "detect" : "gray", "image", "Image") };
        if (blob) { nodes.Add(Node("detect", "BlobAnalysis", new() { ["minimumArea"] = 100, ["showOverlay"] = true })); connections.Add(Edge("gray", "detect")); outputs["Result"] = Output("detect", "result", "Result"); }
        return new()
        {
            ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "editor-benchmark", ["recipeVersion"] = 1,
            ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
            ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
            ["nodes"] = nodes, ["connections"] = connections, ["outputs"] = outputs
        };
    }

    private static Metric Stats(IEnumerable<double> values)
    {
        double[] s = values.OrderBy(x => x).ToArray(); int n = s.Length;
        return new(n % 2 == 0 ? (s[n / 2 - 1] + s[n / 2]) / 2 : s[n / 2], s[(int)Math.Ceiling(n * .95) - 1], s[0], s[^1]);
    }
    private static object Summary(List<Sample> samples) => new
    {
        count = samples.Count, wallMs = Stats(samples.Select(x => x.WallMs)), nativeMs = Stats(samples.Select(x => x.NativeMs)),
        managedAllocatedBytes = Stats(samples.Select(x => (double)x.ManagedAllocatedBytes)), preparationMs = Stats(samples.Select(x => x.PreparationMs)), executionMs = Stats(samples.Select(x => x.ExecutionMs))
    };
    private static void Save() => File.WriteAllText(Path.Combine(runRoot, "editor-performance.json"), JsonSerializer.Serialize(report, JsonOptions));
}

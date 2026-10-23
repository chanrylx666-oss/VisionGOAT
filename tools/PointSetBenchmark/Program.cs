using JPVision.Sdk;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
string repository = Path.GetFullPath(Option("--repo", Directory.GetCurrentDirectory()));
string output = Path.GetFullPath(Option("--out", Path.Combine(repository, "artifacts/tests/pointset-benchmark-" + Guid.NewGuid().ToString("N"))));
int iterations = int.Parse(Option("--iterations", "5")), warmup = int.Parse(Option("--warmup", "3"));
if (iterations is < 1 or > 100 || warmup is < 0 or > 10) throw new ArgumentException("iterations 1..100; warmup 0..10");
bool debug = Option("--mode", "debug") == "debug";
string configuration = Option("--configuration", "Debug");
string recipePath = Option("--recipe", ""), imagePath = Option("--image", "");
string? recipeSha256 = null, imageSha256 = null;
bool real = recipePath.Length > 0 || imagePath.Length > 0;
if (real && (recipePath.Length == 0 || imagePath.Length == 0)) throw new ArgumentException("Both --recipe and --image are required.");
if (real && !debug) throw new ArgumentException("Real-recipe mode requires --mode debug to retain node results without changing public interfaces.");
Directory.CreateDirectory(output);
string runDirectory = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runDirectory);
string store = Path.Combine(runDirectory, "license-store");
VisionLicensing.Import(Path.Combine(repository, "artifacts/activation/current.vlic"), store);
int width = 10996, height = 300;
byte[] pixels = new byte[width * height * 3];
for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
{
    double edge = 95 + 4 * Math.Sin(x / 240.0) + 2 * Math.Sin(x / 90.0);
    byte value = (byte)Math.Clamp(125 + Math.Min(y - edge, 190 - y) * 180, 20, 230);
    int at = (y * width + x) * 3; pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
}
JsonObject Node(string id, string type, JsonObject? p = null) => new() { ["id"] = id, ["type"] = type, ["version"] = 1, ["parameters"] = p ?? new() };
JsonObject Edge(string from, string to, string input = "input") => new() { ["fromNode"] = from, ["fromPort"] = "image", ["toNode"] = to, ["toPort"] = input };
JsonObject Port(string node, string port, string type) => new() { ["node"] = node, ["port"] = port, ["type"] = type };
JsonObject Roi(double cy, double w, double h) => new() { ["shape"] = "rotatedRectangle", ["cx"] = 5498, ["cy"] = cy, ["width"] = w, ["height"] = h, ["angleDegrees"] = 0 };
var definition = new JsonObject
{
    ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "pointset-benchmark", ["recipeVersion"] = 1,
    ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
    ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
    ["nodes"] = new JsonArray(Node("source", "ExternalImage"), Node("median", "MedianBlur", new() { ["kernel"] = 3 }),
        Node("edge", "FindEdgePoints", new() { ["roi"] = Roi(95, 11001.5, 118.3), ["sampleSpacing"] = 4, ["projectionWidth"] = 5,
            ["sigma"] = 1.0, ["edgeThreshold"] = 8, ["polarity"] = "darkToLight", ["curveMode"] = "raw" }),
        Node("line", "FindLines", new() { ["method"] = "caliper", ["caliperPlacement"] = "roiCenter", ["roi"] = Roi(190, 10900, 30),
            ["caliperCount"] = 128, ["projectionWidth"] = 5, ["sigma"] = 1.0, ["edgeThreshold"] = 8, ["polarity"] = "lightToDark" }),
        Node("distance", "MeasurePointLineDistances", new() { ["displayEveryNth"] = 10 })),
    ["connections"] = new JsonArray(Edge("source", "median"), Edge("median", "edge"), Edge("median", "line"), Edge("edge", "distance"), Edge("line", "distance", "line")),
    ["outputs"] = new JsonObject { ["EdgePoints"] = Port("edge", "result", "Result"), ["Distances"] = Port("distance", "result", "Result"),
        ["Image"] = Port("distance", "image", "Image"), ["ProcessedInput"] = Port("median", "image", "Image") }
};
string edgeId = "edge", distanceId = "distance", medianId = "median";
if (real)
{
    string input = Path.GetFullPath(recipePath);
    if (Directory.Exists(input)) input = Path.Combine(input, "recipe.json");
    using (var bytes = File.OpenRead(input)) recipeSha256 = Convert.ToHexString(SHA256.HashData(bytes));
    using (var bytes = File.OpenRead(Path.GetFullPath(imagePath))) imageSha256 = Convert.ToHexString(SHA256.HashData(bytes));
    definition = JsonNode.Parse(File.ReadAllText(input))!.AsObject();
    string IdFor(string type) => definition["nodes"]!.AsArray().OfType<JsonObject>().Single(n => n["type"]!.GetValue<string>() == type)["id"]!.GetValue<string>();
    edgeId = IdFor("FindEdgePoints"); distanceId = IdFor("MeasurePointLineDistances"); medianId = IdFor("MedianBlur");
    using var stream = File.OpenRead(Path.GetFullPath(imagePath));
    var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    var bgr = new FormatConvertedBitmap(decoded, PixelFormats.Bgr24, null, 0);
    width = bgr.PixelWidth; height = bgr.PixelHeight;
    pixels = new byte[checked(width * height * 3)]; bgr.CopyPixels(pixels, width * 3, 0);
}
var requestInputs = definition["inputs"]!.AsObject().Where(p => p.Value!["type"]!.GetValue<string>() != "Image" && p.Value["default"] is not null)
    .ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value!["default"]));
string folder = Path.Combine(runDirectory, "benchmark.vrecipe"); Directory.CreateDirectory(folder);
File.WriteAllText(Path.Combine(folder, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(folder); await using var session = recipe.CreateSession();
using var frame = VisionFrame.CopyFrom(pixels, width, height, width * 3, VisionPixelFormat.Bgr24);
var samples = new List<object>(); var wall = new List<double>(); var native = new List<double>();
string? expectedHash = null; JsonObject? snapshot = null;
int? expectedStations = null, expectedPoints = null, expectedDistances = null;
var nodeSamples = new Dictionary<string, List<double>>();
for (int iteration = -warmup; iteration < iterations; iteration++)
{
    long allocation = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
    using var result = await session.RunAsync(frame, new RunRequest { Debug = debug, PreviewNodeId = distanceId, Inputs = requestInputs, TimeoutMs = 120000 });
    timer.Stop(); long allocated = GC.GetTotalAllocatedBytes(true) - allocation;
    if (result.Status != ExecutionStatus.Succeeded) throw new InvalidOperationException(result.Data.GetRawText());
    JsonElement edgePacket = real ? result.GetNodeOutput<JsonElement>(edgeId, "result") : result.GetOutput<JsonElement>("EdgePoints");
    JsonElement distancePacket = real ? result.GetNodeOutput<JsonElement>(distanceId, "result") : result.GetOutput<JsonElement>("Distances");
    var edge = real ? edgePacket.Deserialize<VisionEdgePointSet>()! : result.GetEdgePoints("EdgePoints");
    var distances = real ? distancePacket.Deserialize<VisionPointLineDistances>()! : result.GetPointLineDistances("Distances");
    if (!edge.Valid || !distances.Valid || edge.Count < 1 || distances.Count < 1 || distances.Count > edge.Count ||
        !real && (edge.StationCount != 2751 || edge.Count is < 2745 or > 2751 || distances.Count != edge.Count))
        throw new InvalidOperationException($"Incomplete synthetic result: stations={edge.StationCount}, points={edge.Count}, distances={distances.Count}");
    expectedStations ??= edge.StationCount; expectedPoints ??= edge.Count; expectedDistances ??= distances.Count;
    if (edge.StationCount != expectedStations || edge.Count != expectedPoints || distances.Count != expectedDistances)
        throw new InvalidOperationException("Repeated runs changed station/point/distance counts.");
    if (edge.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) ||
        distances.Distances.Any(d => !double.IsFinite(d.Distance))) throw new InvalidOperationException("Nonfinite result.");
    using var image = real ? result.GetIntermediateImage(distanceId) : result.GetOutputImage("Image");
    using var processed = real ? result.GetIntermediateImage(medianId) : result.GetOutputImage("ProcessedInput");
    byte[] returned = image.CopyPixels();
    if (image.Info != processed.Info || !returned.SequenceEqual(processed.CopyPixels()))
        throw new InvalidOperationException("Detection/measurement modified the processed image pixels.");
    JsonObject Clean(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject(); node.Remove("image");
        void Normalize(JsonNode? part)
        {
            if (part is JsonObject obj)
                foreach (string key in obj.Select(p => p.Key).ToArray())
                {
                    if (key is "runId" or "elapsedMs") { obj.Remove(key); continue; }
                    if (key == "frameId") { obj[key] = 0; continue; }
                    if (key.EndsWith("coordinatePlane", StringComparison.OrdinalIgnoreCase) && obj[key] is JsonValue plane && plane.TryGetValue<string>(out string? text))
                    {
                        int slash = text.IndexOf('/');
                        if (slash > 0 && long.TryParse(text[..slash], out _)) obj[key] = "0" + text[slash..];
                    }
                    Normalize(obj[key]);
                }
            else if (part is JsonArray array) foreach (var item in array) Normalize(item);
        }
        Normalize(node); return node;
    }
    var canonical = new JsonObject { ["edge"] = Clean(edgePacket), ["distance"] = Clean(distancePacket),
        ["image"] = new JsonObject { ["width"] = image.Info.Width, ["height"] = image.Info.Height, ["sha256"] = Convert.ToHexString(SHA256.HashData(returned)) } };
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString())));
    expectedHash ??= hash;
    if (hash != expectedHash) throw new InvalidOperationException("Canonical output changed between repeated runs.");
    snapshot ??= canonical;
    var nodeTimes = result.Data.GetProperty("nodes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("elapsedMs").GetDouble());
    var nodeTimings = result.Data.GetProperty("nodes").EnumerateObject().ToDictionary(p => p.Name,
        p => p.Value.EnumerateObject().Where(field => field.Name.EndsWith("Ms", StringComparison.Ordinal)).ToDictionary(field => field.Name, field => field.Value.GetDouble()));
    int bytes = Encoding.UTF8.GetByteCount(result.Data.GetRawText());
    Console.WriteLine($"{(iteration < 0 ? "warmup" : "sample")} {iteration}: wall={timer.Elapsed.TotalMilliseconds:F1} native={result.ElapsedMs:F1} ms points={edge.Count} JSON={bytes}");
    if (iteration >= 0)
    {
        wall.Add(timer.Elapsed.TotalMilliseconds); native.Add(result.ElapsedMs);
        foreach (var pair in nodeTimes) { if (!nodeSamples.TryGetValue(pair.Key, out var values)) nodeSamples[pair.Key] = values = new(); values.Add(pair.Value); }
        samples.Add(new { wallMs = timer.Elapsed.TotalMilliseconds, nativeMs = result.ElapsedMs, managedAllocatedBytes = allocated,
            resultJsonUtf8Bytes = bytes, nodeMs = nodeTimes, nodeTimings });
    }
}
double Median(List<double> list) { var values = list.OrderBy(v => v).ToArray(); int middle = values.Length / 2; return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle]; }
string dll = Path.Combine(AppContext.BaseDirectory, "JPVision.Runtime.dll");
string sdk = typeof(VisionEngine).Assembly.Location;
var report = new { configuration, debug, width, height, iterations, warmup, mode = real ? "user recipe and image, no camera, no UI" : "synthetic BGR full-resolution pointset, no camera, no UI",
    recipePath, imagePath, recipeSha256, imageSha256, stationCount = expectedStations, pointCount = expectedPoints, distanceCount = expectedDistances,
    protocol = "Same source and reused recipe/session. Wall includes native execution, final JSON retrieval/managed parse. Typed result validation, JSON byte-count, canonical snapshot and image hash happen after the timer. Whole-process GC allocation delta; not native allocations. No accuracy claim for real parts.",
    nativeDll = new { path = dll, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))) },
    sdkDll = new { path = sdk, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdk))) },
    outputSha256 = expectedHash, medianWallMs = Median(wall), medianNativeMs = Median(native), medianNodeMs = nodeSamples.ToDictionary(p => p.Key, p => Median(p.Value)), samples };
var options = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, options));
File.WriteAllText(Path.Combine(output, "canonical-output.json"), snapshot!.ToJsonString(options));
Console.WriteLine($"Saved {output}; median wall {Median(wall):F1} ms, native {Median(native):F1} ms, signature {expectedHash}");

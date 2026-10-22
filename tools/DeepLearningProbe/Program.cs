using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3) throw new ArgumentException("Expected: project-root manifest-path owned-test-directory");
string project = Path.GetFullPath(args[0]);
string scratch = Path.GetFullPath(args[2]);
string relative = Path.GetRelativePath(Path.Combine(project, "artifacts", "tests"), scratch);
if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
    throw new ArgumentException("Probe directory must be a child of artifacts/tests.");
Directory.CreateDirectory(scratch);
string license = Path.Combine(scratch, "license");
VisionLicensing.Import(Path.Combine(project, "artifacts", "activation", "current.vlic"), license);
var repo = new DeepLearningModelRepository(Path.Combine(scratch, "models"));
var imported = await repo.ImportAsync(Path.GetFullPath(args[1]));
JsonObject Definition(string type, bool dl) => new()
{
    ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "dl-trained-export", ["recipeVersion"] = 1,
    ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
    ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
    ["nodes"] = new JsonArray(new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() },
        new JsonObject { ["id"] = "process", ["type"] = type, ["version"] = 1, ["parameters"] = dl ? new JsonObject { ["modelRoot"] = repo.Root, ["modelKey"] = imported.ModelKey } : new JsonObject() }),
    ["connections"] = new JsonArray(new JsonObject { ["fromNode"] = "source", ["fromPort"] = "image", ["toNode"] = "process", ["toPort"] = "input" }),
    ["outputs"] = new JsonObject { ["Image"] = new JsonObject { ["node"] = "process", ["port"] = "image", ["type"] = "Image" } }
};
using var engine = new VisionEngine(license);
VisionRecipe Load(JsonObject d, string name)
{
    string folder = Path.Combine(scratch, name); Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "recipe.json"), VisionEngine.SealRecipe(d.ToJsonString())); return engine.LoadRecipe(folder);
}
byte[] pixels = Enumerable.Range(0, 65 * 43 * 3).Select(i => (byte)(i % 256)).ToArray();
using var frame = VisionFrame.CopyFrom(pixels, 65, 43, 65 * 3, VisionPixelFormat.Bgr24, 600, -50);
using var ordinary = Load(Definition("ToGray", false), "ordinary");
using var learned = Load(Definition("DeepLearningDefect", true), "learned");
await using var standard = ordinary.CreateSession(); await using var deep = learned.CreateSession();
// This helper owns its separate executable output. Never move the editor/testhost DLLs.
string dll = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"), backup = dll + ".probe-disabled-" + Guid.NewGuid().ToString("N");
File.Move(dll, backup);
try
{
    using var cv = await standard.RunAsync(frame);
    if (cv.Status != ExecutionStatus.Succeeded) throw new Exception("Ordinary CV requires optional ORT: " + cv.Data);
    using var absent = await deep.RunAsync(frame);
    if (absent.Status != ExecutionStatus.Failed || !absent.Data.GetRawText().Contains("DeepLearningRuntimeUnavailable")) throw new Exception("Missing ORT did not fail explicitly: " + absent.Data);
}
finally { File.Move(backup, dll); }
using var present = await deep.RunAsync(frame);
if (present.Status != ExecutionStatus.Succeeded) throw new Exception("Real exported U-Net failed: " + present.Data);
var result = present.GetDeepLearningDefects("Image");
using var image = present.GetOutputImage("Image");
if (!frame.CopyPixels().SequenceEqual(image.CopyPixels())) throw new Exception("Inference modified clean pixels.");
var evidence = new { model = result.ModelId, modelKey = result.ModelKey, result.ScoreKind, result.DataAvailable, result.Count, result.TileCount,
    result.MaximumScore, optionalRuntime = "CV succeeds without ORT; DL fails; same-session retry succeeds after restoring ORT", cleanPixels = true, accuracyClaim = "none" };
File.WriteAllText(Path.Combine(scratch, "verification.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(evidence));

using JPVision.Sdk;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class SdkUtf8ResultTests
{
    private static JsonElement ReadNativeJson(IntPtr pointer)
    {
        var native = typeof(VisionEngine).Assembly.GetType("JPVision.Sdk.Native", true)!;
        var method = native.GetMethod("ReadJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (JsonElement)method.Invoke(null, new object[] { pointer })!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void ParsedDataOwnsUnicodeAndNumbersAfterNativeStorageIsOverwrittenAndFreed()
    {
        string json = "{\"名称\":\"圆心／划痕🔍\",\"x\":2584.35170624,\"zero\":-0.0,\"points\":[[1.5,2],[3,4]]}  \r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(json + '\0');
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        JsonElement value;
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            value = ReadNativeJson(pointer);
            Marshal.Copy(Enumerable.Repeat((byte)'X', bytes.Length).ToArray(), 0, pointer, bytes.Length);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        Assert.Equal("圆心／划痕🔍", value.GetProperty("名称").GetString());
        Assert.Equal(2584.35170624, value.GetProperty("x").GetDouble());
        Assert.Equal("-0.0", value.GetProperty("zero").GetRawText());
        Assert.Equal(2, value.GetProperty("points").GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("{}garbage")]
    public void InvalidOrTrailingJsonIsRejected(string json)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(json);
        try { Assert.ThrowsAny<JsonException>(() => ReadNativeJson(pointer)); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    [Fact]
    public void MissingNativeJsonIsRejected() =>
        Assert.Throws<InvalidDataException>(() => ReadNativeJson(IntPtr.Zero));

    [Fact]
    public async Task ActualRunJsonRemainsReadableAfterResultAndSessionDisposal()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CMakeLists.txt"))) root = root.Parent;
        string store = Path.Combine(root!.FullName, "artifacts/tests/utf8-result-" + Guid.NewGuid());
        Directory.CreateDirectory(store);
        try
        {
            VisionLicensing.Import(Path.Combine(root.FullName, "artifacts/activation/current.vlic"), store);
            var definition = new JsonObject
            {
                ["schemaVersion"] = 1, ["minimumRuntimeAbi"] = 1, ["recipeId"] = "utf8-result", ["recipeVersion"] = 1,
                ["kind"] = "processing", ["decisionOutput"] = "", ["resources"] = new JsonArray(),
                ["inputs"] = new JsonObject { ["InputImage"] = new JsonObject { ["type"] = "Image", ["required"] = true } },
                ["nodes"] = new JsonArray(new JsonObject { ["id"] = "source", ["type"] = "ExternalImage", ["version"] = 1, ["parameters"] = new JsonObject() }),
                ["connections"] = new JsonArray(),
                ["outputs"] = new JsonObject { ["Image"] = new JsonObject { ["node"] = "source", ["port"] = "image", ["type"] = "Image" } }
            };
            string folder = Path.Combine(store, "sample.vrecipe"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "recipe.json"), VisionEngine.SealRecipe(definition.ToJsonString()));
            JsonElement retained;
            string runId = "点集／结果🔍-" + new string('测', 16);
            using (var engine = new VisionEngine(store))
            using (var recipe = engine.LoadRecipe(folder))
            await using (var session = recipe.CreateSession())
            using (var frame = VisionFrame.CopyFrom(new byte[16 * 12], 16, 12, 16, VisionPixelFormat.Gray8))
            using (var result = await session.RunAsync(frame, new() { RunId = runId, Debug = true }))
            {
                Assert.Equal(ExecutionStatus.Succeeded, result.Status);
                retained = result.Data;
            }
            GC.Collect(); GC.WaitForPendingFinalizers();
            Assert.Equal(runId, retained.GetProperty("runId").GetString());
            Assert.Equal(0, VisionEngine.GetDiagnostics().GetProperty("activeNativeStrings").GetInt64());
            Assert.Equal(16, retained.GetProperty("outputs").GetProperty("Image").GetProperty("width").GetInt32());
            Assert.Contains("Succeeded", retained.GetRawText());
        }
        finally { Directory.Delete(store, true); }
    }
}

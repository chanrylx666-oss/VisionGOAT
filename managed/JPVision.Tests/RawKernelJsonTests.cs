using JPVision.Sdk;
using JPVision.Sdk.Execution;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace JPVision.Tests;

public sealed class RawKernelJsonTests
{
    private static long Strings() => VisionEngine.GetDiagnostics().GetProperty("activeNativeStrings").GetInt64();

    [Fact]
    public void SharedNativeSlicesAreCopiedIntoAnOwnedResultBeforeTheirOwnerIsFreed()
    {
        long before = Strings();
        JsonElement result;
        RawKernelJson raw;
        using (var buffer = Native.TakeJsonBuffer(Native.NodeCatalog(out var pointer), pointer))
        {
            raw = RawKernelJson.ReadValue(buffer);
            Assert.Equal(before + 1, Strings());
            result = JsonSerializer.SerializeToElement(new { first = raw, alias = raw });
        }
        Assert.Equal(before, Strings());
        GC.Collect(); GC.WaitForPendingFinalizers();
        var first = result.GetProperty("first");
        var alias = result.GetProperty("alias");
        Assert.True(first.GetArrayLength() > 10);
        Assert.Equal(first.GetRawText(), alias.GetRawText());
        Assert.Contains(first.EnumerateArray(), item => item.GetProperty("type").GetString() == "FindCircles");
        Assert.Throws<ObjectDisposedException>(() => JsonSerializer.SerializeToElement(raw));
    }

    [Fact]
    public void InvalidNativeJsonCannotEnterTheRawWriterAndItsAllocationIsStillFreed()
    {
        long before = Strings();
        using (var buffer = Native.TakeJsonBuffer(Native.NodeCatalog(out var pointer), pointer))
        {
            Marshal.WriteByte(buffer.DangerousGetHandle(), (byte)'!');
            Assert.ThrowsAny<JsonException>(() => RawKernelJson.ReadValue(buffer));
        }
        Assert.Equal(before, Strings());
    }

    [Fact]
    public void FailedNativeStatusFreesANonNullResponseBeforeThrowing()
    {
        long before = Strings();
        Native.Check(Native.NodeCatalog(out var pointer));
        Assert.Throws<ArgumentException>(() => Native.TakeJsonBuffer(1, pointer));
        Assert.Equal(before, Strings());
    }

    [Fact]
    public async Task DisposingTheOwnerCannotFreeUtf8WhileAReaderIsUsingItsSpan()
    {
        long before = Strings();
        using var buffer = Native.TakeJsonBuffer(Native.NodeCatalog(out var pointer), pointer);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Task.Run(() => buffer.Read(bytes =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reader test was not released.");
            return bytes[0];
        }));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            buffer.Dispose();
            Assert.Equal(before + 1, Strings());
        }
        finally { release.Set(); }
        Assert.Equal((byte)'[', await reader);
        Assert.Equal(before, Strings());
    }
}

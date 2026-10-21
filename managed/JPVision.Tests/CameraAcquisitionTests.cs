using JPVision.Acquisition;
using JPVision.Sdk;
using System.Diagnostics;
using System.Threading.Channels;
using Xunit;

namespace JPVision.Tests;

public sealed class CameraAcquisitionTests : IDisposable
{
    private const string Serial = "FAKE-GIGE-A";
    private readonly string root, store;
    public CameraAcquisitionTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))) directory = directory.Parent;
        root = directory!.FullName; store = Path.Combine(root, "artifacts", "tests", "camera-acquisition-" + Guid.NewGuid());
        VisionLicensing.Import(Path.Combine(root, "artifacts", "activation", "current.vlic"), store);
    }

    private static CameraFrame Frame(ulong number = 1, byte value = 20, long? timestamp = null, string serial = Serial)
        => CameraFrame.CopyFrom(Enumerable.Repeat(value, 12).ToArray(), 4, 3, 4, VisionPixelFormat.Gray8,
            new(serial, number, number * 100, timestamp ?? Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private async Task Start(GlobalCameraService service)
    {
        await service.ConnectAsync(Serial); await service.StartAsync(); Assert.Equal(CameraState.Streaming, service.Status.State);
    }
    private static async Task<CameraFrame> Capture(GlobalCameraService service, FakeDevice device, ulong number, byte value = 20)
    {
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); device.Publish(Frame(number, value));
        return await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FrameCopiesBorrowedDriverPixelsAndConvertsPaddedRowsWithoutSharingTheBuffer()
    {
        byte[] borrowed = [1, 2, 90, 91, 3, 4, 92, 93];
        var metadata = new CameraFrameMetadata(Serial, 7, 70, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var frame = CameraFrame.CopyFrom(borrowed, 2, 2, 4, VisionPixelFormat.Gray8, metadata); Array.Fill(borrowed, (byte)255);
        Assert.Equal(new byte[] { 1, 2, 90, 91, 3, 4, 92, 93 }, frame.Pixels.ToArray());
        using var vision = frame.ToVisionFrame(); Assert.Equal(new byte[] { 1, 2, 3, 4 }, vision.CopyPixels());
        Assert.Equal(2, vision.Info.Stride); Assert.Equal(0, vision.Info.OriginX); Assert.Equal(0, vision.Info.OriginY);
        Assert.Equal(metadata, frame.Metadata);
    }

    [Theory]
    [InlineData(0, 3, 4, 12)] [InlineData(4, 0, 4, 12)] [InlineData(32769, 1, 32769, 1)]
    [InlineData(4, 3, 3, 9)] [InlineData(4, 3, 4, 11)] [InlineData(4, 3, 4, 13)]
    public void InvalidFrameGeometryAndBuffersAreRejected(int width, int height, int stride, int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CameraFrame.CopyFrom(new byte[length], width, height, stride, VisionPixelFormat.Gray8,
            new(Serial, 1, 0, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)));

    [Fact]
    public async Task ConnectingUsesTheSerialNumberInsteadOfTheEnumerationPosition()
    {
        var a = new FakeDevice(Serial); var b = new FakeDevice("FAKE-GIGE-B"); var provider = new FakeProvider(b, a);
        await using var service = new GlobalCameraService(provider, store); var devices = await service.EnumerateAsync();
        Assert.Equal("FAKE-GIGE-B", devices[0].Serial); await service.ConnectAsync(Serial);
        Assert.Equal(new[] { Serial }, provider.OpenedSerials); Assert.Equal(CameraState.Connected, service.Status.State);
        Assert.Equal(Serial, service.Status.Serial); Assert.Null(service.LatestFrame); Assert.Equal(0, b.StartCalls);
    }

    [Fact]
    public async Task CaptureIgnoresCachedPreviewAndLateFramesWithAnOldReceiveTimestamp()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        device.Publish(Frame(1, timestamp: 0)); await Until(() => service.Status.FramesReceived == 1);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); Assert.False(pending.IsCompleted);
        device.Publish(Frame(2, timestamp: 0)); await Until(() => service.Status.FramesReceived == 2); Assert.False(pending.IsCompleted);
        device.Publish(Frame(3, 30)); var captured = await pending;
        Assert.Equal(3UL, captured.Metadata.FrameNumber); Assert.Equal(Serial, captured.Metadata.Serial);
        Assert.All(captured.Pixels.ToArray(), pixel => Assert.Equal((byte)30, pixel));
    }

    [Fact]
    public async Task PreviewRetainsOnlyTheLatestSnapshotAndCountsUnobservedReplacements()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        device.Publish(Frame(1, 10)); device.Publish(Frame(2, 20)); device.Publish(Frame(3, 30));
        await Until(() => service.Status.FramesReceived == 3);
        Assert.Equal(2, service.Status.PreviewReplacements); Assert.Equal(3UL, service.LatestFrame!.Metadata.FrameNumber);
        device.Publish(Frame(4, 40)); await Until(() => service.Status.FramesReceived == 4); Assert.Equal(2, service.Status.PreviewReplacements);
        device.Publish(Frame(5, 50)); await Until(() => service.Status.FramesReceived == 5); Assert.Equal(3, service.Status.PreviewReplacements);
        Assert.Equal(5UL, service.LatestFrame!.Metadata.FrameNumber); Assert.True(service.Status.FramesPerSecond > 0);
    }

    [Fact]
    public async Task ConcurrentCapturesAreRejectedAndCompletionReleasesTheCaptureGate()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureNextAsync(TimeSpan.FromSeconds(3)));
        device.Publish(Frame(1)); Assert.Equal(1UL, (await pending).Metadata.FrameNumber);
        Assert.Equal(2UL, (await Capture(service, device, 2)).Metadata.FrameNumber);
    }

    [Fact]
    public async Task ATimeoutNeverReturnsThePreviewCacheAndTheNextRequestStillWorks()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        await Capture(service, device, 1); await Assert.ThrowsAsync<TimeoutException>(() => service.CaptureNextAsync(TimeSpan.FromMilliseconds(30)));
        Assert.Equal(1UL, service.LatestFrame!.Metadata.FrameNumber); Assert.Equal(2UL, (await Capture(service, device, 2)).Metadata.FrameNumber);
    }

    [Fact]
    public async Task CallerCancellationRemovesOnlyItsWaiterAndDoesNotStopPreviewOrPoisonTheNextCapture()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        await Capture(service, device, 1); using var cancellation = new CancellationTokenSource();
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3), cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(CameraState.Streaming, service.Status.State);
        Assert.Equal(1UL, service.LatestFrame!.Metadata.FrameNumber); Assert.Equal(2UL, (await Capture(service, device, 2)).Metadata.FrameNumber);
    }

    [Fact]
    public async Task StopRejectsPendingAndNewCapturesWhileItWaitsForTheLastDriverRead()
    {
        var device = new FakeDevice(Serial) { IgnoreNextReadCancellation = true };
        await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        await device.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(3)); var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3));
        var stopping = service.StopAsync();
        try
        {
            await device.ReadCancellation.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<CameraException>(() => service.CaptureNextAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally { device.Publish(Frame(99, 99)); await stopping.WaitAsync(TimeSpan.FromSeconds(3)); }
        await Assert.ThrowsAsync<CameraException>(() => pending); Assert.Null(service.LatestFrame); Assert.Equal(CameraState.Connected, service.Status.State);
        Assert.False(device.ControlOverlappedRead); Assert.Equal(0, device.ActiveReads); Assert.Equal(1, device.MaximumActiveReads);
        Assert.Equal(0, service.Status.FramesPerSecond);
    }

    [Fact]
    public async Task StopClearsThePreviewAndRestartRequiresAnotherFreshFrame()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var first = await Capture(service, device, 1, 10); await service.StopAsync();
        Assert.Null(service.LatestFrame); Assert.Equal(CameraState.Connected, service.Status.State); Assert.Equal(0, device.DisposeCalls);
        await service.StartAsync(); Assert.Null(service.LatestFrame); Assert.Equal(2UL, (await Capture(service, device, 2, 20)).Metadata.FrameNumber);
        Assert.All(first.Pixels.ToArray(), pixel => Assert.Equal((byte)10, pixel)); Assert.Equal(2, device.StartCalls);
    }

    [Fact]
    public async Task DisconnectClearsTheDeviceAndDisposesItAfterTheReadLoopStops()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        await Capture(service, device, 1); var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); await service.DisconnectAsync();
        await Assert.ThrowsAsync<CameraException>(() => pending); Assert.Equal(CameraState.Disconnected, service.Status.State);
        Assert.Null(service.Status.Serial); Assert.Null(service.LatestFrame); Assert.Equal(1, device.DisposeCalls); Assert.Equal(0, device.ActiveReads);
        Assert.False(device.ControlOverlappedRead); await Assert.ThrowsAsync<CameraException>(() => service.CaptureNextAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ReadFailureInvalidatesCachedPixelsAndRequiresDisconnectBeforeReconnect()
    {
        var first = new FakeDevice(Serial); var second = new FakeDevice(Serial); var provider = new FakeProvider(first, second);
        await using var service = new GlobalCameraService(provider, store); await Start(service); await Capture(service, first, 1);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); first.Fail(new CameraException("cable unplugged"));
        await Assert.ThrowsAsync<CameraException>(() => pending); Assert.Equal(CameraState.Faulted, service.Status.State);
        Assert.Null(service.LatestFrame); Assert.Contains("cable unplugged", service.Status.Error ?? "");
        await Assert.ThrowsAsync<CameraException>(() => service.StartAsync()); await service.DisconnectAsync(); await Start(service);
        first.Publish(Frame(99, 99)); Assert.Equal(2UL, (await Capture(service, second, 2, 20)).Metadata.FrameNumber);
        Assert.Equal(1, first.DisposeCalls); Assert.Equal(2UL, service.LatestFrame!.Metadata.FrameNumber);
    }

    [Fact]
    public async Task AFrameFromAnotherSerialFaultsTheStreamRatherThanBecomingADetectionInput()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); device.Publish(Frame(serial: "OTHER-CAMERA"));
        await Assert.ThrowsAsync<CameraException>(() => pending); Assert.Equal(CameraState.Faulted, service.Status.State); Assert.Null(service.LatestFrame);
    }

    [Fact]
    public async Task AReadFaultStopsTheDriverAfterLeavingTheReadAndKeepsTheServiceFaulted()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        await Capture(service, device, 1); var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3));
        device.Fail(new CameraException("synthetic read fault")); await Assert.ThrowsAsync<CameraException>(() => pending);
        await device.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, device.StopCalls); Assert.Equal(0, device.ActiveReads); Assert.False(device.ControlOverlappedRead);
        Assert.Equal(CameraState.Faulted, service.Status.State); Assert.Null(service.LatestFrame); Assert.Equal(0, device.DisposeCalls);
        await Assert.ThrowsAsync<CameraException>(() => service.StartAsync());
    }

    [Fact]
    public async Task AFutureReceiveTimestampFaultsInsteadOfSatisfyingANewFrameRequest()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3));
        device.Publish(Frame(timestamp: Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3600));
        await Assert.ThrowsAsync<CameraException>(() => pending); await device.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(CameraState.Faulted, service.Status.State); Assert.Equal(0, service.Status.FramesReceived); Assert.Null(service.LatestFrame);
        Assert.Equal(1, device.StopCalls); Assert.Equal(0, device.ActiveReads); Assert.False(device.ControlOverlappedRead);
    }

    [Fact]
    public async Task SettingsCanBeReadAndWrittenOnlyWhenTheCameraIsConnectedAndStopped()
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await service.ConnectAsync(Serial);
        var settings = await service.GetSettingsAsync(); Assert.Equal(5000, settings.Exposure!.Value); Assert.Equal(10, settings.Exposure.Minimum);
        Assert.Equal(100000, settings.Exposure.Maximum); Assert.True(settings.Exposure.Writable); await service.SetFloatAsync("ExposureTime", 1200);
        Assert.Equal(("ExposureTime", 1200.0), device.LastSet); await service.StartAsync();
        await Assert.ThrowsAsync<CameraException>(() => service.GetSettingsAsync());
        await Assert.ThrowsAsync<CameraException>(() => service.SetFloatAsync("Gain", 2));
        Assert.Equal(1, device.SetCalls); await service.StopAsync(); await service.SetFloatAsync("Gain", 2);
        Assert.Equal(("Gain", 2.0), device.LastSet); Assert.False(device.ControlOverlappedRead);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public async Task NonfiniteCameraSettingsAreRejectedBeforeTheDriverIsCalled(double value)
    {
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await service.ConnectAsync(Serial);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetFloatAsync("ExposureTime", value)); Assert.Equal(0, device.SetCalls);
    }

    [Fact]
    public async Task AnInvalidLicenseCannotEnumerateOpenStartOrCaptureADevice()
    {
        var provider = new FakeProvider(new FakeDevice(Serial)); await using var service = new GlobalCameraService(provider, store + "-missing");
        await Assert.ThrowsAsync<VisionLicenseException>(() => service.EnumerateAsync());
        await Assert.ThrowsAsync<VisionLicenseException>(() => service.ConnectAsync(Serial));
        await Assert.ThrowsAsync<VisionLicenseException>(() => service.StartAsync());
        await Assert.ThrowsAsync<VisionLicenseException>(() => service.CaptureNextAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, provider.EnumerateCalls); Assert.Empty(provider.OpenedSerials);
    }

    [Fact]
    public async Task DisposingStopsPendingWorkAndReleasesTheDeviceExactlyOnce()
    {
        var device = new FakeDevice(Serial); var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var pending = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); await service.DisposeAsync(); await service.DisposeAsync();
        await Assert.ThrowsAsync<CameraException>(() => pending); Assert.Equal(1, device.DisposeCalls); Assert.Equal(0, device.ActiveReads);
        Assert.Null(service.LatestFrame); Assert.Equal(CameraState.Disconnected, service.Status.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CaptureNextAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.EnumerateAsync());
    }

    [Fact]
    public async Task CapturedPixelsRunThroughTheRealSdkIndependentlyOfLaterPreviewFrames()
    {
        const int width = 120, height = 50, stride = 128; var borrowed = new byte[stride * height];
        for (int item = 0; item < 3; item++) for (int y = 10; y < 30; y++) for (int x = 5 + item * 40; x < 25 + item * 40; x++) borrowed[y * stride + x] = 255;
        var device = new FakeDevice(Serial); await using var service = new GlobalCameraService(new FakeProvider(device), store); await Start(service);
        var waiting = service.CaptureNextAsync(TimeSpan.FromSeconds(3)); device.Publish(CameraFrame.CopyFrom(borrowed, width, height, stride,
            VisionPixelFormat.Gray8, new(Serial, 42, 4200, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)));
        var acquired = await waiting; Array.Fill(borrowed, (byte)0);
        using var engine = new VisionEngine(store); using var recipe = engine.LoadRecipe(Path.Combine(root, "samples", "recipes", "parts-count.vrecipe"));
        await using var session = recipe.CreateSession(); var frame = acquired.ToVisionFrame();
        var running = session.RunAsync(frame, new() { RunId = "camera-owned-42", FrameId = Serial + "/42" }); frame.Dispose();
        device.Publish(Frame(43, 0)); await Until(() => service.Status.LatestFrame?.FrameNumber == 43);
        using var result = await running; Assert.Equal(ExecutionStatus.Succeeded, result.Status); Assert.Equal(InspectionDecision.Pass, result.Decision);
        Assert.Equal(3, result.GetOutput<int>("ObjectCount")); Assert.Equal("camera-owned-42", result.RunId);
        Assert.Equal(Serial + "/42", result.Data.GetProperty("frameId").GetString());
        Assert.Contains((byte)255, acquired.Pixels.ToArray()); Assert.All(service.LatestFrame!.Pixels.ToArray(), pixel => Assert.Equal((byte)0, pixel));
    }

    private sealed class FakeProvider : ICameraProvider
    {
        private readonly FakeDevice[] devices;
        private readonly Dictionary<string, Queue<FakeDevice>> available;
        public FakeProvider(params FakeDevice[] devices)
        {
            this.devices = devices;
            available = devices.GroupBy(device => device.Serial).ToDictionary(group => group.Key, group => new Queue<FakeDevice>(group));
        }
        public List<string> OpenedSerials { get; } = new();
        public int EnumerateCalls { get; private set; }
        public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); EnumerateCalls++;
            return Task.FromResult<IReadOnlyList<CameraDeviceInfo>>(devices.DistinctBy(device => device.Serial).Select(device => new CameraDeviceInfo(device.Serial, "Synthetic GigE", "192.0.2.1", "test")).ToArray());
        }
        public Task<ICameraDevice> OpenAsync(string serial, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); OpenedSerials.Add(serial);
            if (!available.TryGetValue(serial, out var queue) || queue.Count == 0) throw new CameraException("Synthetic device not found.");
            return Task.FromResult<ICameraDevice>(queue.Dequeue());
        }
    }

    private sealed class FakeDevice(string serial) : ICameraDevice
    {
        private sealed record Item(CameraFrame? Frame, Exception? Error);
        private readonly Channel<Item> incoming = Channel.CreateUnbounded<Item>(new() { SingleReader = true });
        private int activeReads, maximumActiveReads, disposed;
        public string Serial { get; } = serial;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int SetCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int ActiveReads => Volatile.Read(ref activeReads);
        public int MaximumActiveReads => Volatile.Read(ref maximumActiveReads);
        public bool IgnoreNextReadCancellation { get; init; }
        private bool ignoredCancellation;
        public bool ControlOverlappedRead { get; private set; }
        public (string Name, double Value)? LastSet { get; private set; }
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Publish(CameraFrame frame) => incoming.Writer.TryWrite(new(frame, null));
        public void Fail(Exception error) => incoming.Writer.TryWrite(new(null, error));
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ControlOverlappedRead |= ActiveReads != 0; StartCalls++; return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ControlOverlappedRead |= ActiveReads != 0; StopCalls++; Stopped.TrySetResult(); return Task.CompletedTask;
        }
        public async Task<CameraFrame?> ReadAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); int current = Interlocked.Increment(ref activeReads);
            int previous; do { previous = maximumActiveReads; if (current <= previous) break; } while (Interlocked.CompareExchange(ref maximumActiveReads, current, previous) != previous);
            FirstRead.TrySetResult(); using var cancellation = cancellationToken.Register(() => ReadCancellation.TrySetResult());
            try
            {
                Item item;
                if (IgnoreNextReadCancellation && !ignoredCancellation)
                {
                    ignoredCancellation = true; item = await incoming.Reader.ReadAsync(CancellationToken.None);
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(timeoutMs);
                    try { item = await incoming.Reader.ReadAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
                }
                if (item.Error is not null) throw item.Error; return item.Frame;
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }
        public Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ControlOverlappedRead |= ActiveReads != 0;
            return Task.FromResult(new CameraSettings(new(5000, 10, 100000, true), new(1, 0, 24, true), new(30, 1, 120, true)));
        }
        public Task SetFloatAsync(string name, double value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ControlOverlappedRead |= ActiveReads != 0; SetCalls++; LastSet = (name, value); return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            ControlOverlappedRead |= ActiveReads != 0; if (Interlocked.Exchange(ref disposed, 1) == 0) DisposeCalls++; return ValueTask.CompletedTask;
        }
    }

    public void Dispose() { if (Directory.Exists(store)) Directory.Delete(store, true); }
}

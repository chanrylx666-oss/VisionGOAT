using System.Diagnostics;
using JPVision.Sdk;

namespace JPVision.Acquisition;

/// <summary>One camera connection shared by image-source nodes. Preview never runs a recipe.</summary>
public sealed class GlobalCameraService(ICameraProvider provider, string? licenseStore = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private ICameraDevice? device;
    private CancellationTokenSource? streamCancellation;
    private Task? streamTask;
    private CameraState state;
    private string? serial, error;
    private CameraFrame? latest;
    private long received, replacements, observedSequence, started;
    private int generation;
    private bool disposed;
    private TaskCompletionSource<CameraFrame>? pending;
    private long requestedAt;

    public CameraStatus Status
    {
        get
        {
            lock (sync)
            {
                double elapsed = started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalSeconds;
                double fps = state == CameraState.Streaming && elapsed > 0 ? received / elapsed : 0;
                return new(state, serial, received, replacements, fps, latest?.Metadata, error);
            }
        }
    }

    /// <summary>Latest owned snapshot, or null. Access acknowledges the current preview slot.</summary>
    public CameraFrame? LatestFrame
    {
        get { lock (sync) { observedSequence = received; return latest; } }
    }

    private static void Authorize(string? store)
    {
        var license = VisionLicensing.GetStatus(store);
        if (!license.IsValid) throw new VisionLicenseException(license.Code, license.Message ?? "相机采集需要有效的运行库许可证。");
    }
    private void CheckDisposed() { lock (sync) ObjectDisposedException.ThrowIf(disposed, this); }

    public async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        CheckDisposed(); Authorize(licenseStore);
        return await provider.EnumerateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(string cameraSerial, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraSerial)) throw new ArgumentException("请选择相机序列号。", nameof(cameraSerial));
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckDisposed(); Authorize(licenseStore);
            if (device is not null) throw new InvalidOperationException("请先断开当前全局相机，再连接其他设备。");
            var opened = await provider.OpenAsync(cameraSerial, cancellationToken).ConfigureAwait(false);
            if (opened.Serial != cameraSerial || cancellationToken.IsCancellationRequested)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new CameraException("相机返回的序列号与选择的设备不一致。");
            }
            device = opened;
            lock (sync) { serial = cameraSerial; state = CameraState.Connected; error = null; latest = null; generation++; }
        }
        finally { operations.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckDisposed(); Authorize(licenseStore);
            lock (sync)
            {
                if (state == CameraState.Streaming) return;
                if (state != CameraState.Connected) throw new CameraException("请先连接相机；故障后请断开并重新连接。");
            }
            var current = device ?? throw new CameraException("全局相机尚未连接。");
            await current.StartAsync(cancellationToken).ConfigureAwait(false);
            // Once the driver started, keep ownership of its stream even if caller cancellation
            // arrived during the native call. StopAsync/DisposeAsync perform its paired teardown.
            streamCancellation = new();
            int currentGeneration;
            lock (sync)
            {
                latest = null; received = replacements = observedSequence = 0; started = Stopwatch.GetTimestamp();
                state = CameraState.Streaming; error = null; currentGeneration = ++generation;
            }
            var streamToken = streamCancellation.Token;
            streamTask = Task.Run(() => PumpAsync(current, currentGeneration, streamToken));
        }
        finally { operations.Release(); }
    }

    private async Task PumpAsync(ICameraDevice current, int currentGeneration, CancellationToken token)
    {
        long checkedLicense = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await current.ReadAsync(200, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(checkedLicense).TotalSeconds >= 5)
                {
                    Authorize(licenseStore); checkedLicense = Stopwatch.GetTimestamp();
                }
                if (frame is null) continue;
                if (frame.Metadata.Serial != current.Serial)
                    throw new CameraException("收到的相机帧不属于当前全局相机，采集已中止。");
                if (frame.Metadata.ReceivedTimestamp > Stopwatch.GetTimestamp())
                    throw new CameraException("相机帧的接收时钟无效，采集已中止。");
                lock (sync)
                {
                    if (token.IsCancellationRequested || state != CameraState.Streaming || currentGeneration != generation) return;
                    if (latest is not null && observedSequence != received) replacements++;
                    latest = frame; received++;
                    if (pending is not null && frame.Metadata.ReceivedTimestamp > requestedAt)
                    {
                        pending.TrySetResult(frame);
                        // The capture gate stays held until the waiting caller has observed completion.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (currentGeneration != generation || token.IsCancellationRequested) return;
                state = CameraState.Faulted; error = ex.Message; latest = null;
                pending?.TrySetException(new CameraException("相机取流失败：" + ex.Message));
            }
            // This pump no longer owns an active read. Stop here without taking
            // operations: Disconnect may already hold it while awaiting this pump.
            try { await current.StopAsync().ConfigureAwait(false); }
            catch (Exception stopError)
            {
                lock (sync) if (currentGeneration == generation) error += "；停止采集失败：" + stopError.Message;
            }
        }
    }

    /// <summary>Wait for a frame received after this call. Never returns the preview cache on timeout.</summary>
    public async Task<CameraFrame> CaptureNextAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        CheckDisposed(); Authorize(licenseStore); cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<CameraFrame> request;
        int requestGeneration;
        lock (sync)
        {
            if (state != CameraState.Streaming) throw new CameraException("全局相机未在取流，请连接设备并开始预览。");
            if (pending is not null) throw new InvalidOperationException("全局相机正等待上一次检测帧，请稍后重试。");
            requestedAt = Stopwatch.GetTimestamp();
            requestGeneration = generation;
            pending = request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            var frame = await request.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (state != CameraState.Streaming || requestGeneration != generation)
                    throw new CameraException("相机已停止、断开或发生故障，本次检测帧不再有效。");
                return frame;
            }
        }
        finally
        {
            lock (sync) if (ReferenceEquals(pending, request)) pending = null;
        }
    }

    private async Task StopCoreAsync()
    {
        // Cancel and invalidate the slot before awaiting the bounded read, so a late
        // conversion cannot satisfy a new request or repopulate a stopped preview.
        lock (sync)
        {
            generation++; latest = null;
            if (state != CameraState.Faulted) state = device is null ? CameraState.Disconnected : CameraState.Connected;
            pending?.TrySetException(new CameraException("相机取流已停止，本次等待没有有效图像。"));
        }
        streamCancellation?.Cancel();
        if (streamTask is not null) await streamTask.ConfigureAwait(false);
        streamTask = null;
        streamCancellation?.Dispose(); streamCancellation = null;
        if (device is not null)
        {
            try { await device.StopAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                lock (sync) { state = CameraState.Faulted; error = ex.Message; }
                throw;
            }
        }
        lock (sync) if (state != CameraState.Faulted) state = device is null ? CameraState.Disconnected : CameraState.Connected;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckDisposed(); await StopCoreAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        Exception? failure = null;
        try { await StopCoreAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        var closing = device; device = null;
        try { if (closing is not null) await closing.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            lock (sync) { latest = null; state = CameraState.Disconnected; serial = null; error = failure?.Message; }
        }
        if (failure is not null) throw new CameraException("相机停止时发生错误，连接已释放：" + failure.Message);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckDisposed(); await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    public async Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            if (Status.State != CameraState.Connected) throw new CameraException("请在相机已连接且停止取流后读取参数。");
            return await device!.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    public async Task SetFloatAsync(string name, double value, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckDisposed(); Authorize(licenseStore);
            if (Status.State != CameraState.Connected) throw new CameraException("请先停止取流，再设置相机参数。");
            await device!.SetFloatAsync(name, value, cancellationToken).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) { if (disposed) return; disposed = true; }
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }
}

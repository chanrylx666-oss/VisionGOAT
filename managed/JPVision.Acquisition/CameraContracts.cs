using JPVision.Sdk;

namespace JPVision.Acquisition;

public sealed record CameraDeviceInfo(string Serial, string Model, string IpAddress, string UserName)
{
    public override string ToString() => $"{Model} · {Serial} · {IpAddress}";
}

public sealed record CameraNumericSetting(double Value, double Minimum, double Maximum, bool Writable);
public sealed record CameraSettings(CameraNumericSetting? Exposure, CameraNumericSetting? Gain, CameraNumericSetting? FrameRate);
public enum CameraState { Disconnected, Connected, Streaming, Faulted }
public sealed record CameraFrameMetadata(string Serial, ulong FrameNumber, ulong DeviceTimestamp, long ReceivedTimestamp, DateTimeOffset ReceivedAt);
public sealed record CameraStatus(CameraState State, string? Serial, long FramesReceived, long PreviewReplacements,
    double FramesPerSecond, CameraFrameMetadata? LatestFrame, string? Error);

/// <summary>Owned, packed pixel snapshot. Consumers must not modify the underlying memory.</summary>
public sealed class CameraFrame
{
    private readonly byte[] pixels;
    public ReadOnlyMemory<byte> Pixels => pixels;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public VisionPixelFormat PixelFormat { get; }
    public CameraFrameMetadata Metadata { get; }

    private CameraFrame(byte[] pixels, int width, int height, int stride, VisionPixelFormat format, CameraFrameMetadata metadata)
    {
        Validate(pixels.LongLength, width, height, stride, format, metadata);
        this.pixels = pixels; Width = width; Height = height; Stride = stride; PixelFormat = format; Metadata = metadata;
    }

    private static void Validate(long length, int width, int height, int stride, VisionPixelFormat format, CameraFrameMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        int channels = format switch { VisionPixelFormat.Gray8 => 1, VisionPixelFormat.Bgr24 => 3, _ => throw new ArgumentOutOfRangeException(nameof(format)) };
        if (width is < 1 or > 32768 || height is < 1 or > 32768 || (long)width * height > 32L * 1024 * 1024 ||
            stride < (long)width * channels || (long)stride * height > 128L * 1024 * 1024 || length != (long)stride * height)
            throw new ArgumentOutOfRangeException(nameof(width), "相机图像超出尺寸、行跨度或像素预算。");
        if (string.IsNullOrWhiteSpace(metadata.Serial) || metadata.ReceivedTimestamp < 0)
            throw new ArgumentException("相机帧缺少设备或接收时间。", nameof(metadata));
    }

    public static CameraFrame CopyFrom(ReadOnlySpan<byte> pixels, int width, int height, int stride, VisionPixelFormat format, CameraFrameMetadata metadata)
    {
        Validate(pixels.Length, width, height, stride, format, metadata);
        return new(pixels.ToArray(), width, height, stride, format, metadata);
    }
    internal static CameraFrame FromOwned(byte[] pixels, int width, int height, int stride, VisionPixelFormat format, CameraFrameMetadata metadata)
        => new(pixels, width, height, stride, format, metadata);
    public VisionFrame ToVisionFrame() => VisionFrame.CopyFrom(pixels, Width, Height, Stride, PixelFormat);
}

/// <summary>Device operations are serialized by GlobalCameraService; ReadAsync has a bounded timeout.</summary>
public interface ICameraDevice : IAsyncDisposable
{
    string Serial { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<CameraFrame?> ReadAsync(int timeoutMs, CancellationToken cancellationToken = default);
    Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SetFloatAsync(string name, double value, CancellationToken cancellationToken = default);
}

public interface ICameraProvider
{
    Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);
    Task<ICameraDevice> OpenAsync(string serial, CancellationToken cancellationToken = default);
}

public sealed class CameraException(string message, int code = 4) : Exception(message)
{
    public int Code { get; } = code;
}

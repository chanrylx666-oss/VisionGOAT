using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using JPVision.Sdk;
using Microsoft.Win32.SafeHandles;

namespace JPVision.Acquisition;

/// <summary>Optional Windows x64 MVS adapter. The vendor runtime is loaded only when used.</summary>
public sealed class MvsCameraProvider : ICameraProvider
{
    public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] error = new byte[2048]; IntPtr json = IntPtr.Zero;
        try
        {
            MvsNative.Check(MvsNative.Enumerate(out json, error, (uint)error.Length), error);
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(json) ?? "[]");
            var devices = document.RootElement.EnumerateArray().Select(item => new CameraDeviceInfo(
                item.GetProperty("serial").GetString() ?? "", item.GetProperty("model").GetString() ?? "",
                item.GetProperty("ipAddress").GetString() ?? "", item.GetProperty("userName").GetString() ?? "")).ToArray();
            if (devices.Any(item => string.IsNullOrWhiteSpace(item.Serial)) || devices.Select(item => item.Serial).Distinct().Count() != devices.Length)
                throw new CameraException("MVS 返回了无效或重复的相机序列号。");
            return (IReadOnlyList<CameraDeviceInfo>)devices;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { throw MvsNative.Unavailable(ex); }
        finally { if (json != IntPtr.Zero) MvsNative.FreeString(json); }
    }, cancellationToken);

    public Task<ICameraDevice> OpenAsync(string serial, CancellationToken cancellationToken = default) => Task.Run<ICameraDevice>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] error = new byte[2048];
        try
        {
            MvsNative.Check(MvsNative.Open(serial, out var pointer, error, (uint)error.Length), error);
            var handle = new MvsCameraHandle(pointer);
            if (cancellationToken.IsCancellationRequested) { handle.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
            return new MvsCameraDevice(handle, serial);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { throw MvsNative.Unavailable(ex); }
    }, cancellationToken);
}

internal sealed class MvsCameraDevice(MvsCameraHandle handle, string serial) : ICameraDevice
{
    public string Serial { get; } = serial;
    private bool streaming;
    private void Check() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested(); Check();
        byte[] error = new byte[2048]; MvsNative.Check(MvsNative.Start(handle, error, (uint)error.Length), error);
        streaming = true;
    }, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested(); Check();
        if (!streaming) return;
        byte[] error = new byte[2048]; MvsNative.Check(MvsNative.Stop(handle, error, (uint)error.Length), error);
        streaming = false;
    }, cancellationToken);
    public Task<CameraFrame?> ReadAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Check();
        byte[] error = new byte[2048];
        var info = new MvsFrameInfo { StructSize = (uint)Marshal.SizeOf<MvsFrameInfo>() };
        IntPtr frame = IntPtr.Zero;
        try
        {
            int result = MvsNative.Read(handle, checked((uint)timeoutMs), ref info, out frame, error, (uint)error.Length);
            if (result == 1) return Task.FromResult<CameraFrame?>(null);
            MvsNative.Check(result, error); cancellationToken.ThrowIfCancellationRequested();
            if (info.StructSize != 64 || info.Data == IntPtr.Zero || frame == IntPtr.Zero ||
                info.Width is < 1 or > 32768 || info.Height is < 1 or > 32768 ||
                (ulong)info.Width * info.Height > 32UL * 1024 * 1024 || info.PixelFormat is not (1 or 2) ||
                info.Stride != info.Width * (info.PixelFormat == 1 ? 1U : 3U) ||
                info.Length != (ulong)info.Stride * info.Height || info.Length > 128UL * 1024 * 1024 ||
                info.ReceivedQpc < 0 || info.ReceivedQpc > Stopwatch.GetTimestamp())
                throw new CameraException("MVS 桥接返回了不支持的图像尺寸、像素格式或接收时间。");
            var pixels = new byte[checked((int)info.Length)]; Marshal.Copy(info.Data, pixels, 0, pixels.Length);
            var receivedAt = DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(info.ReceivedQpc);
            var metadata = new CameraFrameMetadata(Serial, info.FrameNumber, info.DeviceTimestamp, info.ReceivedQpc, receivedAt);
            return Task.FromResult<CameraFrame?>(CameraFrame.FromOwned(pixels, (int)info.Width, (int)info.Height,
                (int)info.Stride, (VisionPixelFormat)info.PixelFormat, metadata));
        }
        finally { if (frame != IntPtr.Zero) MvsNative.FreeFrame(frame); }
    }
    public Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested(); Check(); byte[] error = new byte[2048]; IntPtr json = IntPtr.Zero;
        try
        {
            MvsNative.Check(MvsNative.Settings(handle, out json, error, (uint)error.Length), error);
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(json) ?? "{}");
            CameraNumericSetting? Setting(string key)
            {
                var item = document.RootElement.GetProperty(key);
                if (item.ValueKind == JsonValueKind.Null) return null;
                var setting = new CameraNumericSetting(item.GetProperty("value").GetDouble(), item.GetProperty("min").GetDouble(),
                    item.GetProperty("max").GetDouble(), item.GetProperty("writable").GetBoolean());
                if (!double.IsFinite(setting.Value) || !double.IsFinite(setting.Minimum) || !double.IsFinite(setting.Maximum) ||
                    setting.Minimum > setting.Maximum || setting.Value < setting.Minimum || setting.Value > setting.Maximum)
                    throw new CameraException("MVS 返回了无效的参数范围：" + key);
                return setting;
            }
            return new CameraSettings(Setting("exposure"), Setting("gain"), Setting("frameRate"));
        }
        finally { if (json != IntPtr.Zero) MvsNative.FreeString(json); }
    }, cancellationToken);
    public Task SetFloatAsync(string name, double value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested(); Check(); byte[] error = new byte[2048];
        MvsNative.Check(MvsNative.SetFloat(handle, name, value, error, (uint)error.Length), error);
    }, cancellationToken);
    public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class MvsCameraHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MvsCameraHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
    protected override bool ReleaseHandle() { MvsNative.Close(handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MvsFrameInfo
{
    public uint StructSize, Width, Height, Stride, PixelFormat, Reserved;
    public ulong Length, FrameNumber, DeviceTimestamp;
    public long ReceivedQpc;
    public IntPtr Data;
}

internal static class MvsNative
{
    private const string Library = "JPVision.Camera.Mvs.dll";
    static MvsNative() => NativeLibrary.SetDllImportResolver(typeof(MvsNative).Assembly, (name, _, _) =>
        name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, Library)) : IntPtr.Zero);
    internal static CameraException Unavailable(Exception ex)
    {
        string path = Path.Combine(AppContext.BaseDirectory, Library);
        string reason = File.Exists(path)
            ? "已找到 JP-Vision 相机适配库，但其本机依赖或接口无法加载，请检查匹配的 x64 构建与 C++ 运行环境。"
            : "启动目录缺少 JP-Vision 相机适配库 JPVision.Camera.Mvs.dll，请重新生成解决方案并确认原生项目已生成、复制完成。这不代表 MVS SDK 未安装。";
        return new CameraException(reason + "\n" + ex.Message, 2);
    }
    internal static void Check(int code, byte[] error)
    {
        if (code == 0) return;
        int end = Array.IndexOf(error, (byte)0); string message = Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
        throw new CameraException(message.Length > 0 ? message : "MVS 相机操作失败，代码 " + code, code);
    }
    [DllImport(Library, EntryPoint = "jpc_mvs_enumerate", CallingConvention = CallingConvention.Cdecl)] internal static extern int Enumerate(out IntPtr json, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_string_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void FreeString(IntPtr json);
    [DllImport(Library, EntryPoint = "jpc_mvs_open", CallingConvention = CallingConvention.Cdecl)] internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string serial, out IntPtr camera, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_start", CallingConvention = CallingConvention.Cdecl)] internal static extern int Start(MvsCameraHandle camera, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_stop", CallingConvention = CallingConvention.Cdecl)] internal static extern int Stop(MvsCameraHandle camera, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_close", CallingConvention = CallingConvention.Cdecl)] internal static extern void Close(IntPtr camera);
    [DllImport(Library, EntryPoint = "jpc_mvs_read", CallingConvention = CallingConvention.Cdecl)] internal static extern int Read(MvsCameraHandle camera, uint timeoutMs, ref MvsFrameInfo info, out IntPtr frame, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_frame_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void FreeFrame(IntPtr frame);
    [DllImport(Library, EntryPoint = "jpc_mvs_settings", CallingConvention = CallingConvention.Cdecl)] internal static extern int Settings(MvsCameraHandle camera, out IntPtr json, [Out] byte[] error, uint capacity);
    [DllImport(Library, EntryPoint = "jpc_mvs_set_float", CallingConvention = CallingConvention.Cdecl)] internal static extern int SetFloat(MvsCameraHandle camera, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value, [Out] byte[] error, uint capacity);
}

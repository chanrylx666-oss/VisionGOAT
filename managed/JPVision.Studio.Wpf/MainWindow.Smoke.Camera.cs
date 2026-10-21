using JPVision.Acquisition;
using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeCameraCloseAsync(string scenario)
    {
        Require(flow is not null && !flow.Dirty && globalCamera is null, "camera close smoke requires the initial unmodified recipe");
        var provider = new SmokeCameraProvider();
        var closingService = globalCamera = new GlobalCameraService(provider, licenseStore);
        bool opened = scenario == "camera-close-stream";
        if (opened)
        {
            await closingService.ConnectAsync(SmokeCameraDevice.TestSerial);
            await closingService.StartAsync();
            Require(closingService.Status.State == CameraState.Streaming && closingService.LatestFrame is null, "close test did not start a stream with a bounded pending read");
        }
        StatusText.Text = opened ? "● 关闭验证 · 模拟相机持续取流，无帧" : "● 关闭验证 · 相机服务已创建，未连接";
        CaptureWindow(scenario);
        Closed += (_, _) =>
        {
            bool passed = globalCamera is null && closingService.Status.State == CameraState.Disconnected && (!opened || provider.Device.Disposed);
            File.WriteAllText(Path.ChangeExtension(CapturePath, ".closed.json"), JsonSerializer.Serialize(new
            {
                scenario, passed, state = closingService.Status.State.ToString(), globalServiceReleased = globalCamera is null,
                deviceOpened = opened, deviceDisposed = provider.Device.Disposed
            }));
        };
        Close();
    }

    private async Task SmokeCameraSourceAsync(bool mainLive = false)
    {
        Require(globalCamera is null && fileSourceSnapshot is not null, "camera smoke did not start in isolated file mode");
        var provider = new SmokeCameraProvider();
        globalCamera = new GlobalCameraService(provider, licenseStore);
        byte[] filePixels = fileSourceSnapshot!.CopyPixels();
        flow!.Add("ExternalImage", 30, 120, "source"); flow.Add("ToGray", 280, 120, "gray"); flow.Add("BlobAnalysis", 530, 120, "blob");
        flow.Connect("source", "image", "gray", "input"); flow.Connect("gray", "image", "blob", "input");
        flow.Publish("CameraImage", "gray", "image", false); flow.Publish("Blob", "blob", "result", false); Graph.Select("source");
        Require(InspectorControls<ComboBox>().Any(c => Equals(c.Tag, "camera:source")), "ExternalImage has no global source choice");
        string definition = flow.Definition.ToJsonString();
        ShowCameraWindow(); var window = cameraWindow!;
        Require(window.Background == FindResource("Background") && window.Foreground == FindResource("Text"), "camera manager did not use the application's readable dark palette");
        async Task Wait(Func<bool> done, string message)
        {
            for (int i = 0; !done() && i < 400; i++) await Task.Delay(10);
            Require(done(), message);
        }
        async Task Click(string tag)
        {
            var button = DialogControls<Button>(window).Single(b => Equals(b.Tag, tag));
            Require(button.IsEnabled, tag + " is unexpectedly disabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !busy && !cameraCommandBusy, tag + " did not complete");
        }
        await Click("camera:refresh"); await Click("camera:connect");
        Require(globalCamera.Status.Serial == SmokeCameraDevice.TestSerial && globalCamera.Status.State == CameraState.Connected, "camera was not selected by serial");
        var exposure = DialogControls<TextBox>(window).Single(t => Equals(t.Tag, "camera:ExposureTime"));
        Require(exposure.IsEnabled, "stopped camera exposure is not writable"); exposure.Text = "2500";
        await Click("camera:apply:ExposureTime"); Require(provider.Device.Exposure == 2500, "device exposure write was not routed");
        await Click("camera:start");
        Require(!exposure.IsEnabled, "streaming camera settings remained writable");
        provider.Device.Publish(1); await Wait(() => globalCamera.Status.LatestFrame?.FrameNumber == 1, "preview did not receive its first frame");
        CameraSourceChoice.SelectedIndex = 1;
        Require(useGlobalCamera && source is null && result is null && flow.Definition.ToJsonString() == definition, "source mode reused a file snapshot or modified the recipe");
        await Wait(() => displayedCameraMetadata?.FrameNumber == 1, "main image area did not display the live camera frame");
        Require(ImageChoice.SelectedIndex == 3 && ReferenceEquals(ImageView.Bitmap, window.PreviewBitmap), "camera views did not share the prepared bitmap");
        ImageView.Zoom(1.25);
        var liveTransform = ImageView.ViewTransform;
        provider.Device.Publish(100);
        await Wait(() => displayedCameraMetadata?.FrameNumber == 100, "main live preview did not replace its latest frame");
        bool zoomPreserved = ImageView.ViewTransform == liveTransform;
        Require(zoomPreserved && source is null && result is null, "live replacement reset zoom/pan or assigned a detection source");
        bool teachingRefused = false;
        try { await PrepareInputAsync("blob"); } catch (InvalidOperationException) { teachingRefused = true; }
        Require(teachingRefused, "camera teaching silently used the previous file image");
        Graph.Select("blob");
        var run = BusyAsync(RunOnceAsync); await Task.Delay(30);
        Require(!run.IsCompleted, "RunOnce reused the preview cache rather than waiting for a new frame");
        provider.Device.Publish(2); await run;
        Require(sourceCameraMetadata?.FrameNumber == 2 && result?.Status == ExecutionStatus.Succeeded, "run did not use the frame received after execution started");
        using (var output = result!.GetOutputImage("CameraImage")) Require(output.CopyPixels()[0] == 22, "algorithm input pixels did not come from fresh frame 2");
        Require(ImageInfo.Text.Contains("帧 2", StringComparison.Ordinal), "detected frame identity is hidden");
        Require(ImageInfo.Text.Contains($"{sourceCameraMetadata!.ReceivedAt.ToLocalTime():HH:mm:ss.fff}", StringComparison.Ordinal), "UTC camera receipt time was not displayed in local time");
        var frozenResult = result; byte[] frozenPixels = source!.CopyPixels();
        var detectedBitmap = ImageView.Bitmap;
        provider.Device.Publish(3);
        await Wait(() => window.PreviewMetadata?.FrameNumber == 3, "independent preview did not refresh");
        Require(ReferenceEquals(result, frozenResult) && sourceCameraMetadata?.FrameNumber == 2 && source.CopyPixels().SequenceEqual(frozenPixels), "live preview overwrote the fixed detection source/result");
        Require(ImageChoice.SelectedIndex == 0 && ReferenceEquals(ImageView.Bitmap, detectedBitmap) && displayedCameraMetadata is null, "live preview replaced the displayed detection result");
        await BusyAsync(() => PrepareInputAsync("blob")); ImageView.BeginAdjust(false);
        ImageView.CommitRegion(RoiRegion.Rectangle(10, 10, 230, 180), false); ImageView.BeginAdjust(false);
        var roi = ImageView.Roi; byte[] teachingPixels = inputFrame!.CopyPixels();
        provider.Device.Publish(4); await Wait(() => window.PreviewMetadata?.FrameNumber == 4, "preview stopped while editing ROI");
        Require(ImageView.EditEnabled && ImageView.Roi == roi && inputFrame.CopyPixels().SequenceEqual(teachingPixels) && sourceCameraMetadata?.FrameNumber == 2, "live preview changed the ROI teaching plane or pixels");
        ImageView.EndEdit(); CameraSourceChoice.SelectedIndex = 0;
        Require(!useGlobalCamera && source!.CopyPixels().SequenceEqual(filePixels) && inputFrame is null && result is null, "switching to file did not restore its owned snapshot and clear cached results");
        using (var config = JsonDocument.Parse(File.ReadAllText(CameraConfigurationPath)))
            Require(config.RootElement.GetProperty("SourceMode").GetString() == "file" && config.RootElement.GetProperty("Serial").GetString() == SmokeCameraDevice.TestSerial, "camera configuration was not persisted separately");
        Require(CameraConfigurationPath.StartsWith(Path.GetDirectoryName(CapturePath)!, StringComparison.OrdinalIgnoreCase), "smoke escaped its isolated camera config");
        var snapshot = BusyAsync(CaptureTeachingFrameAsync); await Task.Delay(30); provider.Device.Publish(5); await snapshot;
        Require(sourceCameraMetadata?.FrameNumber == 5 && source!.CopyPixels()[0] == 25, "explicit teaching snapshot reused an earlier live frame");
        await Wait(() => window.PreviewMetadata?.FrameNumber == 5, "teaching frame was not previewed");
        void CaptureManager(string suffix)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            using var stream = File.Create(Path.ChangeExtension(CapturePath, suffix)); encoder.Save(stream);
        }
        CaptureManager(".camera.png");
        window.Height = window.MinHeight; window.UpdateLayout();
        var settingScroll = DialogControls<ScrollViewer>(window).Single();
        Require(settingScroll.ScrollableHeight > 0, "small camera manager has no scrolling access to device settings");
        settingScroll.ScrollToBottom(); CaptureManager(".camera-small.png");
        window.Close(); Require(globalCamera.Status.State == CameraState.Streaming, "closing camera manager stopped the global stream");
        if (mainLive)
        {
            ImageChoice.SelectedIndex = 3;
            await Wait(() => displayedCameraMetadata?.FrameNumber == 5, "manual return to live failed after teaching");
            provider.Device.Publish(6);
            await Wait(() => displayedCameraMetadata?.FrameNumber == 6, "closing the manager stopped main-window live preview");
            Require(sourceCameraMetadata?.FrameNumber == 5 && source!.CopyPixels()[0] == 25, "returning to live changed the fixed teaching source");
            await globalCamera.StopAsync();
            await Wait(() => ImageView.Bitmap is null && ImageInfo.Text.Contains("停流", StringComparison.Ordinal), "stop left a stale live image");
            await globalCamera.StartAsync();
            provider.Device.Publish(7);
            await Wait(() => displayedCameraMetadata?.FrameNumber == 7, "main preview did not resume after stream restart");
            UpdateLayout();
            var liveCapture = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            liveCapture.Render(this);
            var liveEncoder = new PngBitmapEncoder(); liveEncoder.Frames.Add(BitmapFrame.Create(liveCapture));
            using (var liveStream = File.Create(Path.ChangeExtension(CapturePath, ".live.png"))) liveEncoder.Save(liveStream);
            File.WriteAllText(Path.ChangeExtension(CapturePath, ".live.json"), JsonSerializer.Serialize(new
            {
                displayedFrame = displayedCameraMetadata!.FrameNumber, fixedTeachingFrame = sourceCameraMetadata!.FrameNumber,
                sharedBitmap = ReferenceEquals(ImageView.Bitmap, cameraPreview?.Bitmap), mainPreviewAfterManagerClosed = cameraWindow is null,
                fullWidth = ImageView.Info!.Width, fullHeight = ImageView.Info.Height, zoomPreserved
            }));
            provider.Device.Fail();
            await Wait(() => globalCamera.Status.State == CameraState.Faulted && ImageView.Bitmap is null, "camera fault left a stale live image");
            Require(ImageInfo.Text.Contains("故障", StringComparison.Ordinal) && sourceCameraMetadata?.FrameNumber == 5, "fault hid its state or changed the fixed teaching snapshot");
        }
        await CloseCameraAsync(); Require(provider.Device.Disposed, "closing global camera did not release its device");
        ChangeImageSource(false); Graph.Select("blob");
        Log("相机 GUI 验证：模拟设备按 SN 连接，执行取新帧，实时预览不覆盖检测／ROI，文件源恢复、教学快照与独立配置通过；未连接真实硬件。");
    }

    private sealed class SmokeCameraProvider : ICameraProvider
    {
        internal SmokeCameraDevice Device { get; } = new();
        public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CameraDeviceInfo>>(new[] { new CameraDeviceInfo(SmokeCameraDevice.TestSerial, "GUI 模拟设备（无硬件）", "192.0.2.10", "camera-source") });
        public Task<ICameraDevice> OpenAsync(string serial, CancellationToken cancellationToken = default)
            => serial == SmokeCameraDevice.TestSerial ? Task.FromResult<ICameraDevice>(Device) : throw new CameraException("模拟相机序列号不匹配。");
    }
    private sealed class SmokeCameraDevice : ICameraDevice
    {
        internal const string TestSerial = "GUI-CAMERA-TEST";
        private readonly Channel<CameraFrame> frames = Channel.CreateUnbounded<CameraFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private bool streaming;
        internal bool Disposed { get; private set; }
        internal double Exposure { get; private set; } = 1000;
        private volatile bool fail;
        public string Serial => TestSerial;
        public Task StartAsync(CancellationToken cancellationToken = default) { streaming = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { streaming = false; return Task.CompletedTask; }
        public async Task<CameraFrame?> ReadAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (!streaming) return null;
            if (fail) throw new CameraException("模拟取流故障。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(timeoutMs);
            try { return await frames.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
        public Task<CameraSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CameraSettings(new(Exposure, 20, 100000, true), new(0, 0, 24, true), new(30, 1, 120, true)));
        public Task SetFloatAsync(string name, double value, CancellationToken cancellationToken = default)
        {
            if (streaming || name != "ExposureTime" || value is < 20 or > 100000) throw new CameraException("模拟参数写入无效。");
            Exposure = value; return Task.CompletedTask;
        }
        internal void Publish(ulong number)
        {
            byte[] pixels = Enumerable.Repeat((byte)(20 + number), 320 * 240).ToArray();
            for (int y = 40; y < 100; y++) Array.Fill(pixels, (byte)230, y * 320 + 40 + (int)number, 70);
            frames.Writer.TryWrite(CameraFrame.CopyFrom(pixels, 320, 240, 320, VisionPixelFormat.Gray8,
                new CameraFrameMetadata(Serial, number, number, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)));
        }
        internal void Fail() => fail = true;
        public ValueTask DisposeAsync() { streaming = false; Disposed = true; frames.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}

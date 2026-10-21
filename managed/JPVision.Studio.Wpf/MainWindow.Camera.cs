using JPVision.Acquisition;
using JPVision.Sdk;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private GlobalCameraService? globalCamera;
    private CameraWindow? cameraWindow;
    private VisionFrame? fileSourceSnapshot;
    private CameraFrameMetadata? sourceCameraMetadata;
    private bool useGlobalCamera, synchronizingSource, cameraCommandBusy, cameraConfigurationReady;
    private string cameraSerial = "";
    private sealed record CameraConfiguration(string SourceMode, string Serial);
    private bool CameraConfigurationEnabled => !launch.Contains("--smoke-test") || Argument("--camera-config") is not null || Argument("--case") is "camera-source" or "camera-main-live";
    private string CameraConfigurationPath => Path.GetFullPath(Argument("--camera-config") ?? (launch.Contains("--smoke-test")
        ? CapturePath + ".camera.json" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JP-Vision", "camera.json")));

    private void InitializeCameraConfiguration()
    {
        InitializeCameraDisplay();
        CameraConfiguration? saved = null;
        if (CameraConfigurationEnabled && Argument("--case") is not ("camera-source" or "camera-main-live") && File.Exists(CameraConfigurationPath))
            try
            {
                if (new FileInfo(CameraConfigurationPath).Length <= 16384)
                    saved = JsonSerializer.Deserialize<CameraConfiguration>(File.ReadAllText(CameraConfigurationPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log("未读取相机配置：" + ex.Message, true); }
        cameraSerial = saved?.Serial ?? "";
        cameraConfigurationReady = true;
        if (saved?.SourceMode == "camera" && Argument("--image") is null) ChangeImageSource(true, false);
        else UpdateCameraUi();
    }
    private void SaveCameraConfiguration()
    {
        if (!cameraConfigurationReady || !CameraConfigurationEnabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CameraConfigurationPath)!);
            WriteAtomic(CameraConfigurationPath, JsonSerializer.Serialize(new CameraConfiguration(useGlobalCamera ? "camera" : "file", cameraSerial)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("未保存相机配置：" + ex.Message, true); }
    }
    private GlobalCameraService CameraService => globalCamera ??= new GlobalCameraService(new MvsCameraProvider(), licenseStore);
    private void SetFileSource(VisionFrame frame)
    {
        if (liveCameraDisplay) FreezeCameraDisplay(1);
        if (!ReferenceEquals(source, fileSourceSnapshot)) source?.Dispose();
        fileSourceSnapshot?.Dispose(); fileSourceSnapshot = frame; source = frame;
        sourceCameraMetadata = null; useGlobalCamera = false;
        UpdateCameraUi(); SaveCameraConfiguration();
    }
    private void ChangeImageSource(bool camera, bool save = true)
    {
        if (useGlobalCamera != camera)
        {
            if (!ReferenceEquals(source, fileSourceSnapshot)) source?.Dispose();
            source = camera ? null : fileSourceSnapshot;
            sourceCameraMetadata = null; useGlobalCamera = camera;
            inputFrame?.Dispose(); inputFrame = null; inputNode = null; inputPosition = null; inputFixture = null;
            ImageView.EndEdit(); ClearResult(false);
            if (camera) SelectCameraDisplay();
            else FreezeCameraDisplay(1);
            if (source is null && !camera) ImageView.Clear();
            else ShowSource();
            BuildInspector();
        }
        UpdateCameraUi(); if (save) SaveCameraConfiguration();
    }
    private void CameraSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizingSource || ImageView is null || busy || cameraCommandBusy) return;
        Safe(() => ChangeImageSource(CameraSourceChoice.SelectedIndex == 1));
    }
    private void UpdateCameraUi()
    {
        if (CameraSourceChoice is null) return;
        synchronizingSource = true;
        CameraSourceChoice.SelectedIndex = useGlobalCamera ? 1 : 0;
        CameraSourceChoice.IsEnabled = licensed && !busy && !cameraCommandBusy;
        CameraManageButton.IsEnabled = CameraManageMenu.IsEnabled = licensed && !busy && !cameraCommandBusy;
        CameraSourceStatus.Text = useGlobalCamera ? sourceCameraMetadata is { } frame ? $"相机 {frame.Serial} · 固定帧 {frame.FrameNumber}" : "全局相机 · 未取固定帧" : "图像源：文件";
        synchronizingSource = false;
        cameraWindow?.RefreshState();
    }
    private void CameraManageClick(object sender, RoutedEventArgs e) => Safe(ShowCameraWindow);
    private void ShowCameraWindow()
    {
        EnsureEditor();
        if (cameraWindow is not null) { cameraWindow.Activate(); return; }
        cameraWindow = new CameraWindow(CameraService, () => busy || cameraCommandBusy || !licensed,
            RunCameraCommandAsync, serial => { cameraSerial = serial; SaveCameraConfiguration(); },
            () => BusyAsync(CaptureTeachingFrameAsync), cameraSerial) { Owner = this };
        cameraWindow.Closed += (_, _) => cameraWindow = null;
        cameraWindow.Show();
        RefreshCameraDisplay();
    }
    private async Task RunCameraCommandAsync(Func<Task> action)
    {
        if (busy || cameraCommandBusy) return;
        cameraCommandBusy = true; UpdateEnabled();
        try { EnsureEditor(); await action(); }
        catch (Exception ex) { cameraWindow?.ShowError(ex.Message); Log("相机：" + ex.Message, true); }
        finally { cameraCommandBusy = false; UpdateEnabled(); RefreshCameraDisplay(); if (closePending) { closePending = false; Close(); } }
    }
    private async Task CaptureCameraSourceAsync()
    {
        var frame = await CameraService.CaptureNextAsync(TimeSpan.FromSeconds(5), cancellation?.Token ?? default);
        var owned = frame.ToVisionFrame();
        if (!ReferenceEquals(source, fileSourceSnapshot)) source?.Dispose();
        source = owned; sourceCameraMetadata = frame.Metadata; cameraSerial = frame.Metadata.Serial;
        UpdateCameraUi(); SaveCameraConfiguration();
        Log($"相机检测输入：{frame.Metadata.Serial} · 帧 {frame.Metadata.FrameNumber} · 接收 {frame.Metadata.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz} · {frame.Width} × {frame.Height}");
    }
    private async Task CaptureTeachingFrameAsync()
    {
        if (!useGlobalCamera) ChangeImageSource(true);
        FreezeCameraDisplay(1);
        await CaptureCameraSourceAsync();
        inputFrame?.Dispose(); inputFrame = null; inputNode = null; inputPosition = null; inputFixture = null;
        ImageView.EndEdit(); ClearResult(false); ShowSource(); BuildInspector();
        Log("已固定相机教学图像；实时预览不会覆盖 ROI 或模板编辑。");
    }
    private void BuildGlobalSourcePanel()
    {
        Parameters.Children.Add(Note("所有外部图像节点共享本次图像源；选择全局相机后，主图区显示实时画面。执行或教学会固定图像，选择“实时画面”可返回预览。"));
        var choice = new ComboBox { ItemsSource = new[] { "文件图像", "全局相机" }, SelectedIndex = useGlobalCamera ? 1 : 0, Tag = "camera:source", Margin = new Thickness(0, 0, 0, 6) };
        choice.SelectionChanged += (_, _) => { if (!buildingInspector && !busy && !cameraCommandBusy) Safe(() => ChangeImageSource(choice.SelectedIndex == 1)); };
        Parameters.Children.Add(choice);
        var manage = new Button { Content = "全局相机管理…", Tag = "camera:manage", HorizontalAlignment = HorizontalAlignment.Left };
        manage.Click += CameraManageClick; Parameters.Children.Add(manage);
        if (useGlobalCamera)
        {
            var snapshot = new Button { Content = "取图用于教学", Tag = "camera:teach", HorizontalAlignment = HorizontalAlignment.Left };
            snapshot.Click += async (_, _) => await BusyAsync(CaptureTeachingFrameAsync); Parameters.Children.Add(snapshot);
        }
    }
    private async Task CloseCameraAsync()
    {
        cameraWindow?.Close();
        if (globalCamera is not null) { var service = globalCamera; globalCamera = null; await service.DisposeAsync(); }
        cameraPreview = null;
        RefreshCameraDisplay();
    }
}

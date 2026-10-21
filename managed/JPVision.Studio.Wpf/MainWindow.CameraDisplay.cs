using JPVision.Acquisition;
using System.Windows.Threading;

namespace JPVision.Studio;

public partial class MainWindow
{
    private readonly DispatcherTimer cameraDisplayTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private CameraPreviewFrame? cameraPreview;
    private CameraFrameMetadata? displayedCameraMetadata;
    private bool liveCameraDisplay, synchronizingCameraDisplay, cameraDisplayInitialized;

    private void InitializeCameraDisplay()
    {
        if (cameraDisplayInitialized) return;
        cameraDisplayInitialized = true;
        cameraDisplayTimer.Tick += CameraDisplayTick;
        cameraDisplayTimer.Start();
    }

    private void CameraDisplayTick(object? sender, EventArgs e)
    {
        try { RefreshCameraDisplay(); }
        catch (Exception ex)
        {
            bool wasLive = liveCameraDisplay;
            FreezeCameraDisplay();
            if (wasLive) { ImageView.Clear(); ImageInfo.Text = "实时预览显示失败，请检查运行日志。"; }
            cameraPreview = null;
            cameraWindow?.Present(null);
            Log("相机预览显示失败：" + ex.Message, true);
        }
    }

    private void SelectCameraDisplay()
    {
        liveCameraDisplay = true;
        synchronizingCameraDisplay = true;
        try { ImageChoice.SelectedIndex = 3; }
        finally { synchronizingCameraDisplay = false; }
        ImageView.EndEdit();
        ImageView.Roi = ImageView.Template = JPVision.Editor.Core.RoiRegion.None;
        ImageView.Overlays = null;
        displayedCameraMetadata = null;
        RefreshCameraDisplay();
    }

    private void FreezeCameraDisplay(int fixedChoice = 0)
    {
        bool wasLive = liveCameraDisplay;
        liveCameraDisplay = false;
        displayedCameraMetadata = null;
        synchronizingCameraDisplay = true;
        try { ImageChoice.SelectedIndex = fixedChoice; }
        finally { synchronizingCameraDisplay = false; }
        if (wasLive && source is null)
        {
            ImageView.Clear();
            ImageInfo.Text = "全局相机 · 尚无固定图像，请取图用于教学或执行流程。";
        }
        if (cameraWindow?.IsVisible != true) cameraPreview = null;
    }

    private bool TrySelectCameraDisplay()
    {
        if (synchronizingCameraDisplay) return true;
        if (ImageChoice.SelectedIndex != 3)
        {
            liveCameraDisplay = false;
            displayedCameraMetadata = null;
            return false;
        }
        if (!useGlobalCamera) ChangeImageSource(true);
        SelectCameraDisplay();
        return true;
    }

    private bool TryShowCameraDisplay()
    {
        if (!useGlobalCamera || !liveCameraDisplay || ImageChoice.SelectedIndex != 3) return false;
        RefreshCameraDisplay();
        return true;
    }

    private void RefreshCameraDisplay()
    {
        bool main = useGlobalCamera && liveCameraDisplay && ImageChoice.SelectedIndex == 3 && !busy && !cameraCommandBusy && !ImageView.EditEnabled;
        bool manager = cameraWindow?.IsVisible == true;
        if (!main && !manager) return;
        var status = globalCamera?.Status;
        if (status?.State != CameraState.Streaming)
        {
            cameraPreview = null;
            cameraWindow?.Present(null);
            if (main)
            {
                displayedCameraMetadata = null;
                if (ImageView.Bitmap is not null) ImageView.Clear();
                ImageInfo.Text = status?.State switch
                {
                    CameraState.Faulted => "实时画面 · 相机故障：" + status.Error,
                    CameraState.Connected => "实时画面 · 相机已停流，点击“开始预览”。",
                    _ => "实时画面 · 请在相机管理中连接设备并开始预览。"
                };
            }
            return;
        }
        var latest = globalCamera!.LatestFrame;
        if (latest is null)
        {
            cameraPreview = null;
            cameraWindow?.Present(null);
            if (main) { displayedCameraMetadata = null; ImageView.Clear(); ImageInfo.Text = "实时画面 · 正在等待相机帧…"; }
            return;
        }
        if (!ReferenceEquals(cameraPreview?.Frame, latest)) cameraPreview = CameraPreviewFrame.Create(latest);
        if (manager) cameraWindow!.Present(cameraPreview);
        if (!main || ReferenceEquals(displayedCameraMetadata, latest.Metadata)) return;
        ImageView.SetPreview(cameraPreview);
        displayedCameraMetadata = latest.Metadata;
        ImageInfo.Text = $"实时画面  │  {latest.Width} × {latest.Height}  │  {(latest.PixelFormat == JPVision.Sdk.VisionPixelFormat.Gray8 ? "Gray" : "BGR")} 8-bit  │  SN {latest.Metadata.Serial} · 帧 {latest.Metadata.FrameNumber} · {latest.Metadata.ReceivedAt.ToLocalTime():HH:mm:ss.fff}  │  {status.FramesPerSecond:0.0} fps";
    }

    private void StopCameraDisplay()
    {
        cameraDisplayTimer.Stop();
        cameraDisplayTimer.Tick -= CameraDisplayTick;
        cameraPreview = null;
        displayedCameraMetadata = null;
    }
}

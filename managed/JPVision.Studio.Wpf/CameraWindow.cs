using JPVision.Acquisition;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JPVision.Studio;

internal sealed class CameraWindow : Window
{
    private readonly GlobalCameraService service;
    private readonly Func<bool> locked;
    private readonly Func<Func<Task>, Task> command;
    private readonly Action<string> selected;
    private readonly Func<Task> teaching;
    private readonly ComboBox devices = new() { MinWidth = 380, Tag = "camera:devices", Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock frameInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ImageEditorView preview = new() { ClipToBounds = true };
    private readonly Dictionary<string, (TextBox Input, TextBlock Limits, Button Apply, CameraNumericSetting? Setting)> settings = new();
    private readonly Dictionary<string, Button> buttons = new();
    private string preferredSerial;
    private CameraPreviewFrame? presented;
    internal CameraFrameMetadata? PreviewMetadata => presented?.Frame.Metadata;
    internal System.Windows.Media.Imaging.BitmapSource? PreviewBitmap => preview.Bitmap;
    internal CameraWindow(GlobalCameraService service, Func<bool> locked, Func<Func<Task>, Task> command, Action<string> selected,
        Func<Task> teaching, string preferredSerial)
    {
        this.service = service; this.locked = locked; this.command = command; this.selected = selected; this.teaching = teaching; this.preferredSerial = preferredSerial;
        Title = "全局相机管理 · 海康 MVS"; Width = 1080; Height = 720; MinWidth = 900; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("Background"); Foreground = (Brush)FindResource("Text"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12;
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "按序列号连接海康 GigE 相机。此处持续预览；执行流程时等待触发之后的新帧，主窗口保留检测／教学快照。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var selection = new StackPanel { Orientation = Orientation.Horizontal };
        selection.Children.Add(devices); selection.Children.Add(Button("refresh", "枚举设备", RefreshDevicesAsync));
        selection.Children.Add(Button("connect", "连接", async () => { await service.ConnectAsync(SelectedSerial); await ReadSettingsAsync(); }));
        selection.Children.Add(Button("disconnect", "断开", () => service.DisconnectAsync())); top.Children.Add(selection);
        devices.SelectionChanged += (_, _) => { if (devices.SelectedItem is CameraDeviceInfo info) { this.preferredSerial = info.Serial; selected(info.Serial); } RefreshState(); };
        var operations = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        operations.Children.Add(Button("start", "开始预览", () => service.StartAsync())); operations.Children.Add(Button("stop", "停止预览", () => service.StopAsync()));
        var teach = new Button { Content = "取图用于教学", Tag = "camera:teach" }; buttons["teach"] = teach;
        teach.Click += async (_, _) => { if (!locked()) await teaching(); }; operations.Children.Add(teach); top.Children.Add(operations);
        top.Children.Add(state); top.Children.Add(error);
        var controls = new StackPanel { Width = 300, Margin = new Thickness(16, 0, 0, 0) };
        var settingScroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(settingScroll, Dock.Right); root.Children.Add(settingScroll);
        controls.Children.Add(new TextBlock { Text = "采集参数", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        controls.Children.Add(new TextBlock { Text = "停流后设置手动曝光／增益；设备支持时关闭对应自动调节。帧率仍受曝光与带宽限制。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        AddSetting(controls, "ExposureTime", "曝光时间（μs）"); AddSetting(controls, "Gain", "增益（dB）"); AddSetting(controls, "AcquisitionFrameRate", "采集帧率（fps）");
        controls.Children.Add(Button("settings", "读取设备参数", ReadSettingsAsync));
        controls.Children.Add(new TextBlock { Text = "关闭此窗口只关闭预览视图；全局连接与取流由“停止预览／断开”控制。不会自动连接或自动切换到其他相机。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        var picture = new DockPanel(); DockPanel.SetDock(frameInfo, Dock.Bottom); picture.Children.Add(frameInfo); picture.Children.Add(preview); root.Children.Add(picture);
        Content = root;
        Loaded += (_, _) => RefreshState();
    }
    private string SelectedSerial => (devices.SelectedItem as CameraDeviceInfo)?.Serial ?? throw new CameraException("请先枚举并选择相机序列号。");
    private Button Button(string name, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Tag = "camera:" + name };
        button.Click += async (_, _) => { if (!locked()) { error.Text = ""; await command(action); RefreshState(); } };
        buttons[name] = button; return button;
    }
    private async Task RefreshDevicesAsync()
    {
        var found = await service.EnumerateAsync(); devices.ItemsSource = found;
        devices.SelectedItem = found.FirstOrDefault(d => d.Serial == preferredSerial) ?? (preferredSerial.Length == 0 ? found.FirstOrDefault() : null);
        if (found.Count == 0) error.Text = "未发现海康 GigE 相机，请检查供电、网线、网卡与 MVS 驱动。";
        else if (devices.SelectedItem is null) error.Text = "已保存的序列号未出现在列表中；请选择目标设备，不会自动换机。";
    }
    private async Task ReadSettingsAsync()
    {
        var values = await service.GetSettingsAsync();
        Set("ExposureTime", values.Exposure); Set("Gain", values.Gain); Set("AcquisitionFrameRate", values.FrameRate); RefreshState();
        void Set(string name, CameraNumericSetting? setting)
        {
            var entry = settings[name]; entry.Input.Text = setting?.Value.ToString("0.########", CultureInfo.InvariantCulture) ?? "不支持";
            entry.Limits.Text = setting is null ? "设备未提供此节点。" : $"设备范围：{setting.Minimum:0.########} ～ {setting.Maximum:0.########}" + (setting.Writable ? "" : "（只读）");
            settings[name] = (entry.Input, entry.Limits, entry.Apply, setting);
        }
    }
    private void AddSetting(Panel parent, string name, string label)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 6) });
        var row = new DockPanel(); var apply = new Button { Content = "写入", Tag = "camera:apply:" + name };
        var input = new TextBox { Text = "未读取", Tag = "camera:" + name }; var limits = new TextBlock { Text = "连接后读取设备范围。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        DockPanel.SetDock(apply, Dock.Right); row.Children.Add(apply); row.Children.Add(input); parent.Children.Add(row); parent.Children.Add(limits);
        settings[name] = (input, limits, apply, null);
        apply.Click += async (_, _) => await command(async () =>
        {
            var capability = settings[name].Setting ?? throw new CameraException("设备未提供此参数。");
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < capability.Minimum || value > capability.Maximum)
                throw new CameraException($"请输入设备范围 {capability.Minimum}～{capability.Maximum} 内的有限数值。");
            await service.SetFloatAsync(name, value); await ReadSettingsAsync();
        });
    }
    internal void ShowError(string message) => error.Text = message;
    internal void RefreshState()
    {
        var status = service.Status; bool active = !locked();
        string name = status.State switch { CameraState.Connected => "已连接，已停流", CameraState.Streaming => "连续取流中", CameraState.Faulted => "故障", _ => "未连接" };
        state.Text = $"{name} · SN {status.Serial ?? "—"} · 收到 {status.FramesReceived} 帧 · {status.FramesPerSecond:0.0} fps · 预览替换 {status.PreviewReplacements} 帧";
        if (!string.IsNullOrWhiteSpace(status.Error)) error.Text = status.Error;
        devices.IsEnabled = active && status.State == CameraState.Disconnected;
        foreach (var item in buttons) item.Value.IsEnabled = active && (item.Key switch
        {
            "connect" => devices.SelectedItem is not null && status.State == CameraState.Disconnected,
            "disconnect" => status.State != CameraState.Disconnected,
            "start" => status.State == CameraState.Connected,
            "stop" or "teach" => status.State == CameraState.Streaming,
            "settings" => status.State == CameraState.Connected,
            "refresh" => status.State == CameraState.Disconnected,
            _ => true
        });
        foreach (var item in settings.Values) item.Input.IsEnabled = item.Apply.IsEnabled = active && status.State == CameraState.Connected && item.Setting?.Writable == true;
    }
    internal void Present(CameraPreviewFrame? frame)
    {
        RefreshState();
        if (frame is null && presented is not null)
        {
            presented = null; preview.Clear(); frameInfo.Text = "当前没有实时帧。主窗口的固定检测／教学图像保持不变。";
        }
        if (frame is null || ReferenceEquals(frame, presented)) return;
        preview.SetPreview(frame); presented = frame;
        var metadata = frame.Frame.Metadata;
        frameInfo.Text = $"{frame.Info.Width} × {frame.Info.Height} · SN {metadata.Serial} · 实时帧 {metadata.FrameNumber}\n接收 {metadata.ReceivedAt.ToLocalTime():HH:mm:ss.fff zzz}；预览不改变固定检测／教学图像。";
    }
}

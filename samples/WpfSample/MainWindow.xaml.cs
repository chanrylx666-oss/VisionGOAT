using JPVision.Sdk;
using JPVision.Wpf;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.WpfSample;

public partial class MainWindow : Window
{
    private VisionEngine? engine;
    private VisionRecipe? recipe;
    private VisionSession? session;
    private VisionFrame? frame;
    private InspectionResult? result;
    private CancellationTokenSource? cancellation;
    private bool busy, allowClose, closePending;
    private bool HasExpectedCount => recipe is not null && recipe.Description.GetProperty("inputs").TryGetProperty("ExpectedCount", out _);
    private string recipeDirectory = Path.Combine(AppContext.BaseDirectory, "samples", "recipes", "parts-count.vrecipe");
    public MainWindow()
    {
        InitializeComponent();
        var launch = Environment.GetCommandLineArgs();
        int recipeIndex = Array.IndexOf(launch, "--recipe");
        if (recipeIndex >= 0 && recipeIndex + 1 < launch.Length)
            recipeDirectory = Path.GetFullPath(launch[recipeIndex + 1]);
        ImageView.PixelPositionChanged += text => PixelText.Text = text;
        LicenseControl.StatusChanged += info =>
        {
            Commands.IsEnabled = info.IsValid && !busy;
            if (info.IsValid && engine is null)
            {
                try
                {
                    engine = new VisionEngine();
                    LoadRecipe(recipeDirectory);
                    int imageIndex = Array.IndexOf(launch, "--image");
                    LoadImage(imageIndex >= 0 && imageIndex + 1 < launch.Length ? launch[imageIndex + 1] :
                        Path.Combine(AppContext.BaseDirectory, "samples", "images", "three-parts.png"));
                    StateText.Text = "就绪 · 本机永久授权";
                }
                catch (Exception e) { Error(e); }
            }
        };
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("--smoke-test") && !args.Contains("--processing-smoke"))
                return;
            try
            {
                if (Icon is null)
                    throw new InvalidOperationException("Application icon resource was not loaded.");
                await LicenseControl.RefreshAsync();
                await RunOnceAsync();
                if (args.Contains("--processing-smoke"))
                {
                    if (result?.Status != ExecutionStatus.Succeeded || result.Decision != InspectionDecision.Unknown || DecisionText.Text != "处理完成")
                        throw new InvalidOperationException("WPF processing result or decision label mismatch.");
                    using var gray = result.GetOutputImage("GrayImage");
                    if (gray.Info.PixelFormat != VisionPixelFormat.Gray8 || gray.Info.Width != frame!.Info.Width || Expected.IsEnabled)
                        throw new InvalidOperationException("WPF processing image / inputs mismatch.");
                }
                else if (result?.Decision != InspectionDecision.Pass || result.GetOutput<int>("ObjectCount") != int.Parse(Expected.Text))
                    throw new InvalidOperationException("WPF smoke result mismatch.");
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                int index = Array.IndexOf(args, "--screenshot");
                if (index >= 0 && index + 1 < args.Length)
                    Capture(args[index + 1]);
                Application.Current.Shutdown(0);
            }
            catch (Exception e) { string path = Path.Combine(AppContext.BaseDirectory, "wpf-smoke-error.txt"); File.WriteAllText(path, e.ToString()); Application.Current.Shutdown(1); }
        };
    }
    private void LoadRecipe(string directory)
    {
        if (engine is null)
            return;
        var next = engine.LoadRecipe(directory);
        VisionSession nextSession;
        try
        {
            nextSession = next.CreateSession();
        }
        catch { next.Dispose(); throw; }
        session?.Dispose();
        recipe?.Dispose();
        session = nextSession;
        recipe = next;
        recipeDirectory = directory;
        RecipeText.Text = "配方 · " + Path.GetFileName(directory);
        var inputs = next.Description.GetProperty("inputs");
        if (inputs.TryGetProperty("ExpectedCount", out var expected) && expected.TryGetProperty("default", out var count))
            Expected.Text = count.GetInt32().ToString();
        Expected.IsEnabled = HasExpectedCount;
        ClearResult();
    }
    private void LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.EndInit();
        bitmap.Freeze();
        var next = VisionView.FromBitmap(bitmap);
        frame?.Dispose();
        frame = next;
        result?.Dispose();
        result = null;
        ImageView.PresentCopy(next);
        ImageTitle.Text = Path.GetFileName(path) + $" · {next.Info.Width} × {next.Info.Height}";
        DecisionText.Text = "等待检测";
        CountText.Text = "目标数量 —";
    }
    private void OpenImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true)
            try
            {
                LoadImage(dialog.FileName);
            }
            catch (Exception error) { Error(error); }
    }
    private void OpenRecipe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JP-Vision 配方|recipe.json" };
        if (dialog.ShowDialog(this) == true)
            try
            {
                LoadRecipe(Path.GetDirectoryName(dialog.FileName)!);
            }
            catch (Exception error) { Error(error); }
    }
    private void SetBusy(bool value)
    {
        busy = value;
        RunButton.IsEnabled = ContinuousButton.IsEnabled = ImageButton.IsEnabled = RecipeButton.IsEnabled = !value;
        Expected.IsEnabled = !value && HasExpectedCount;
        CancelButton.IsEnabled = value;
        StateText.Text = value ? "运行中" : "就绪";
    }
    private async Task RunOnceAsync()
    {
        if (frame is null || session is null)
            throw new InvalidOperationException("请先加载图像和有效配方。");
        var inputs = new Dictionary<string, object?>();
        if (HasExpectedCount)
        {
            if (!int.TryParse(Expected.Text, out int expected) || expected < 0)
                throw new ArgumentException("预期数量必须为非负整数。");
            inputs["ExpectedCount"] = expected;
        }
        result?.Dispose();
        result = null;
        DecisionText.Text = "检测中";
        CountText.Text = "目标数量 —";
        TimeText.Text = "算法耗时 —";
        ImageView.SetOverlays(Array.Empty<VisionOverlay>());
        result = await session.RunAsync(frame, new RunRequest { Debug = true, Inputs = inputs }, cancellation?.Token ?? CancellationToken.None);
        if (closePending)
        {
            result.Dispose();
            result = null;
            return;
        }
        bool succeeded = result.Status == ExecutionStatus.Succeeded;
        bool processing = recipe!.Description.GetProperty("kind").GetString() == "processing";
        DecisionText.Text = !succeeded ? "执行错误" : result.Decision switch
        {
            InspectionDecision.Pass => "OK / 合格",
            InspectionDecision.Fail => "NG / 不合格",
            _ => processing ? "处理完成" : "判定未知"
        };
        var color = !succeeded || result.Decision == InspectionDecision.Fail ? Color.FromRgb(255, 80, 88) :
            result.Decision == InspectionDecision.Pass ? Color.FromRgb(242, 242, 244) : Color.FromRgb(216, 216, 222);
        DecisionText.Foreground = new SolidColorBrush(color);
        CountText.Text = succeeded && result.Outputs.TryGetProperty("ObjectCount", out var countValue)
            ? "目标数量 " + countValue.ToString() : processing ? "图像处理 · 无数量判定" : "目标数量 —";
        TimeText.Text = $"算法耗时 {result.ElapsedMs:F2} ms";
        RunIdText.Text = "运行号 " + result.RunId[..Math.Min(8, result.RunId.Length)];
        Diagnostics.Text = result.Status == ExecutionStatus.Succeeded ? "执行成功。结果来自原生 OpenCV 引擎。" : result.Data.GetProperty("error").ToString();
        if (result.Status == ExecutionStatus.Succeeded && result.Outputs.TryGetProperty("Features", out var features) && features.GetArrayLength() > 0)
        {
            Diagnostics.Text = features[0].GetProperty("kind").GetString() switch
            {
                "circle" => string.Join(Environment.NewLine, result.GetCircles().Take(3).Select((c, i) => $"圆 {i + 1} · 圆心 ({c.X:F2}, {c.Y:F2}) px\n半径 {c.Radius:F2} · 直径 {c.Diameter:F2} px · 面积 {c.Area:F2} px²")),
                "line" => string.Join(Environment.NewLine, result.GetLines().Take(3).Select((l, i) => $"线段 {i + 1} · 长度 {l.Length:F1} px · 角度 {l.AngleDegrees:F1}°")),
                "region" => string.Join(Environment.NewLine, result.GetRegions().Take(3).Select((r, i) => $"区域 {i + 1} · 面积 {r.Area:F1} px² · 圆形度 {r.Circularity:F3}")),
                "match" => string.Join(Environment.NewLine, result.GetMatches().Take(3).Select((m, i) => $"模板 {i + 1} · 中心 ({m.CenterX:F1}, {m.CenterY:F1}) · 角度 {m.AngleDegrees:F1}° · 分数 {m.Score:F3}")),
                _ => Diagnostics.Text
            };
        }
        if (succeeded && result.Outputs.TryGetProperty("CircleMeasurement", out _)) {
            var measurement = result.GetMeasurement<VisionCircle>("CircleMeasurement");
            if (!measurement.SelectedValid)
                Diagnostics.Text = measurement.Found ? "当前目标索引超出检测列表，无测量坐标。" : "未找到圆，本次无测量坐标。";
            if (measurement.Caliper is { } caliper)
                Diagnostics.Text += $"\n卡尺 {caliper.RequestedCount} · 保留 {caliper.RetainedCount} · 剔除 {caliper.RejectedCount} · 缺失 {caliper.MissingCount}\n拟合 RMS {measurement.Selected?.RmsError.ToString("F3") ?? "—"} px";
        }
        if (succeeded && processing)
            foreach (var output in recipe.Description.GetProperty("outputs").EnumerateObject())
            {
                var type = output.Value.GetProperty("type").GetString();
                if (type != "Image" && type != "Mask")
                    continue;
                using var processed = result.GetOutputImage(output.Name);
                ImageView.PresentCopy(processed, result.GetOverlays());
                ImageTitle.Text = $"{output.Name} · {processed.Info.Width} × {processed.Info.Height} · {processed.Info.PixelFormat}";
                return;
            }
        ImageView.PresentCopy(frame, result.GetOverlays());
    }
    private async void SingleRun(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void ContinuousRun(object sender, RoutedEventArgs e) => await RunAsync(true);
    private async Task RunAsync(bool continuous)
    {
        if (busy)
            return;
        SetBusy(true);
        cancellation = new();
        try
        {
            do
            {
                await RunOnceAsync();
                if (continuous)
                    await Task.Delay(40, cancellation.Token);
            } while (continuous && !cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) { StateText.Text = "已取消，资源已收尾"; DecisionText.Text = "已取消"; }
        catch (Exception e) { Error(e); }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private void CancelRun(object sender, RoutedEventArgs e)
    {
        StateText.Text = "取消收尾中…";
        cancellation?.Cancel();
    }
    private void FitImage(object sender, RoutedEventArgs e) => ImageView.Fit();
    private void ToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (ImageView is not null)
            ImageView.ShowOverlays = OverlayToggle.IsChecked == true;
    }
    private void ClearResult()
    {
        result?.Dispose();
        result = null;
        DecisionText.Text = "等待检测";
        CountText.Text = "目标数量 —";
        TimeText.Text = "算法耗时 —";
        ImageView.SetOverlays(Array.Empty<VisionOverlay>());
    }
    private void Error(Exception e)
    {
        ClearResult();
        Diagnostics.Text = e.Message;
        DecisionText.Text = e is VisionLicenseException ? "授权不可用" : "操作失败";
        StateText.Text = "需要处理";
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!allowClose && busy)
        {
            e.Cancel = true;
            if (closePending)
                return;
            closePending = true;
            cancellation?.Cancel();
            if (session is not null)
                await session.DisposeAsync();
            allowClose = true;
            Close();
            return;
        }
        result?.Dispose();
        frame?.Dispose();
        session?.Dispose();
        recipe?.Dispose();
        engine?.Dispose();
    }
    private void Capture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        if (result is not null)
            File.WriteAllText(path + ".json", result.Data.GetRawText());
    }
}

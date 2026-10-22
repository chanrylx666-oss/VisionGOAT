using JPVision.Editor.Core;
using JPVision.Sdk;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

public partial class MainWindow : Window
{
    private readonly string[] launch;
    private readonly string? licenseStore;
    private VisionEngine? engine;
    private EditorExecutionCache? executionCache;
    private FlowDocument? flow;
    private VisionFrame? source, inputFrame;
    private VisionMatchPosition? inputPosition;
    private InspectionResult? result;
    private CancellationTokenSource? cancellation;
    private bool busy, licensed, buildingInspector, retainInput, closePending, allowClose;
    private string? inputNode, recipeDirectory;
    private string? observedFlowDefinition;
    private string imagePath = "";
    private NodeSpec? chosenType;
    private readonly Dictionary<string, object?> runInputs = new();
    private readonly System.Windows.Threading.DispatcherTimer licenseTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    public MainWindow(string[] args)
    {
        launch = args;
        licenseStore = Argument("--license-store");
        InitializeComponent();
        ImageChoice.SelectedIndex = 0;
        Graph.SelectionChanged += id => { try { ImageView.EndEdit(); BuildInspector(); DisplaySelectedResult(); } catch (Exception ex) { Error(ex); } };
        Graph.AddRequested += (type, p) => Safe(() => AddNode(type, p));
        Graph.Error += message => Error(new InvalidOperationException(message));
        Graph.ZoomChanged += value => GraphZoom.Text = $"{value:P0}";
        Graph.EditRequested += id => { _ = EditRoiAsync(); };
        ImageView.PixelChanged += text => PixelText.Text = text;
        ImageView.Error += text => Error(new InvalidOperationException(text));
        ImageView.EditCancelled += () => { if (flow is not null && Graph.SelectedId is string id) SetCaliperGuides(id); };
        ImageView.RegionChanged += (region, template) =>
        {
            if (flow is null || Graph.SelectedId is not string id)
                return;
            Safe(() => { retainInput = true; try { if (template) flow.SetTemplateRegion(id, region); else CommitRoi(id, region); } finally { retainInput = false; } BuildInspector(); });
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
                if (launch.Contains("--smoke-test"))
                    await SmokeAsync();
            }
            catch (Exception ex)
            {
                Error(ex);
                if (launch.Contains("--smoke-test"))
                {
                    WriteSmokeFailure(ex);
                    try { await CloseCameraAsync(); } catch (Exception stop) { Log("相机停止：" + stop.Message, true); }
                    allowClose = true; Application.Current.Shutdown(1);
                }
            }
        };
        Closing += OnClosing;
        PreviewKeyDown += OnKey;
        licenseTimer.Tick += async (_, _) => { if (!busy && !cameraCommandBusy) await RefreshLicenseAsync(); };
    }
    private string? Argument(string name)
    {
        int i = Array.IndexOf(launch, name);
        return i >= 0 && i + 1 < launch.Length ? launch[i + 1] : null;
    }
    private async Task InitializeAsync()
    {
        await RefreshLicenseAsync();
        licenseTimer.Start();
        if (!licensed)
        {
            BuildInspector();
            return;
        }
        engine = new VisionEngine(licenseStore);
        flow = new FlowDocument(new NodeCatalog(VisionEngine.GetNodeCatalog()));
        observedFlowDefinition = flow.Definition.ToJsonString();
        flow.Changed += FlowChanged;
        Graph.SetDocument(flow);
        BuildToolbox();
        string? recipe = Argument("--recipe");
        if (recipe is not null)
            LoadRecipe(recipe);
        UpdateTitle();
        string? image = Argument("--image");
        if (image is null)
            image = Path.Combine(AppContext.BaseDirectory, "samples/images/three-parts.png");
        if (File.Exists(image))
            LoadImage(image);
        InitializeCameraConfiguration();
        Graph.Fit();
        BuildInspector();
        Log("JP-Vision WPF 已就绪 · C++ 核心 " + VisionEngine.Version);
    }
    private async Task RefreshLicenseAsync()
    {
        bool wasLicensed = licensed;
        try
        {
            var info = await Task.Run(() => VisionLicensing.GetEditorStatus(licenseStore));
            var runtime = await Task.Run(() => VisionLicensing.GetStatus(licenseStore));
            licensed = info.IsValid && runtime.IsValid;
            StatusText.Text = licensed ? $"● 就绪 · {info.Customer} · 本机永久授权" : $"● 授权锁定 · {info.Status} · 点击“授权”导入许可证";
        }
        catch (Exception ex) { licensed = false; StatusText.Text = "运行库不可用：" + ex.Message; }
        UpdateEnabled();
        if (wasLicensed && !licensed)
        {
            try { await CloseCameraAsync(); } catch (Exception ex) { Log("相机停止：" + ex.Message, true); }
            ClearResult();
            ImageView.Clear();
        }
    }
    private void EnsureEditor()
    {
        if (!licensed || !VisionLicensing.GetEditorStatus(licenseStore).IsValid)
            throw new InvalidOperationException("编辑器未授权，请导入开发者为本机签发的许可证。");
    }
    private void UpdateEnabled()
    {
        foreach (var button in Toolbar.Children.OfType<Button>())
            button.IsEnabled = button == CancelButton ? busy : licensed && !busy && !cameraCommandBusy;
        Toolbox.IsEnabled = Graph.IsEnabled = Parameters.IsEnabled = licensed && !busy && !cameraCommandBusy;
        KeepPreviews.IsEnabled = !busy && !cameraCommandBusy;
        ImageChoice.IsEnabled = licensed && !busy && !cameraCommandBusy;
        UpdateCameraUi();
    }
    private void FlowChanged()
    {
        // Node positions and teaching outlines live in LayoutJson, not the execution definition.
        // Moving a card (including a slight movement during a click) cannot invalidate its output.
        string? definition = flow?.Definition.ToJsonString();
        bool executionChanged = !string.Equals(observedFlowDefinition, definition, StringComparison.Ordinal);
        observedFlowDefinition = definition;
        if (executionChanged) InvalidateFlowExecution();
        UpdateTitle();
        BuildInspector();
        Graph.Refresh();
    }
    private void InvalidateFlowExecution()
    {
        ClearResult(false);
        if (!retainInput)
        {
            inputFrame?.Dispose();
            inputFrame = null;
            inputNode = null;
            inputPosition = null; inputFixture = null;
            ImageView.EndEdit();
            ShowSource();
        }
    }
    private void UpdateTitle() => RecipeTitle.Text = (recipeDirectory is null ? "未命名配方" : Path.GetFileName(recipeDirectory)) + (flow?.Dirty == true ? " *" : "");
    private void ClearResult(bool showSource = true)
    {
        bindingPreview = null;
        bool hadResult = result is not null;
        result?.Dispose();
        result = null;
        Graph.NodeStates.Clear();
        Graph.Refresh();
        Results.Children.Clear();
        NodeState.Text = "等待执行";
        ImageView.Overlays = null;
        if (showSource && hadResult && !ImageView.EditEnabled && !retainInput)
            ShowSource();
        ImageView.InvalidateVisual();
    }
    private void Log(string text, bool error = false)
    {
        var row = new TextBlock { Text = $"[{DateTime.Now:HH:mm:ss}]  {text}", Foreground = error ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2, 0, 0, 0) };
        LogList.Items.Add(row);
        while (LogList.Items.Count > 250)
            LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(row);
    }
    private void Error(Exception ex)
    {
        string display = VisionErrorMessage.Describe(ex.Message,
            id => flow is not null && flow.Nodes.Any(n => FlowDocument.Id(n!) == id) ? flow.Spec(id).Label : null);
        Log(display, true);
        StatusText.Text = "● " + display.Replace('\n', ' ');
        if (!launch.Contains("--smoke-test"))
            MessageBox.Show(this, display, "JP-Vision", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Safe(Action action)
    {
        try
        {
            if (busy || cameraCommandBusy)
                return;
            action();
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task BusyAsync(Func<Task> action)
    {
        if (busy || cameraCommandBusy)
            return;
        busy = true;
        cancellation = new();
        UpdateEnabled();
        try
        {
            EnsureEditor();
            await action();
        }
        catch (OperationCanceledException) { ClearResult(); Log("运行已取消"); StatusText.Text = "● 已取消"; }
        catch (TimeoutException) { ClearResult(); Log("运行超时", true); StatusText.Text = "● 运行超时"; }
        catch (Exception ex) { ClearResult(); Error(ex); if (launch.Contains("--smoke-test")) throw; }
        finally { cancellation?.Dispose(); cancellation = null; busy = false; UpdateEnabled(); if (closePending) { closePending = false; Close(); } }
    }
    private IReadOnlyDictionary<string, object?> InputsFor(JsonObject definition)
    {
        var values = new Dictionary<string, object?>();
        foreach (var p in definition["inputs"]!.AsObject())
        if (p.Value!["type"]!.GetValue<string>() != "Image" && runInputs.TryGetValue(p.Key, out var value))
            values[p.Key] = value;
        return values;
    }
    private async Task<InspectionResult> ExecuteAsync(JsonObject definition, string preview, bool debug)
    {
        if (source is null || engine is null)
            throw new InvalidOperationException(useGlobalCamera ? "全局相机还没有固定图像。请连接并开始预览，再取图用于教学或执行流程。" : "请先打开外部图像。");
        var runFrame = source;
        string frameId = sourceCameraMetadata is { } cameraFrame ? $"camera:{cameraFrame.Serial}:{cameraFrame.FrameNumber}:{cameraFrame.ReceivedTimestamp}" : Guid.NewGuid().ToString();
        executionCache ??= new EditorExecutionCache(engine,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JP-Vision", "EditorRuns"));
        return await executionCache.RunAsync(definition.ToJsonString(), runFrame,
            new RunRequest { FrameId = frameId, Inputs = InputsFor(definition), Debug = debug, PreviewNodeId = preview, TimeoutMs = 30000 }, cancellation?.Token ?? default);
    }
    private Task RunOnceAsync() => RunOnceAsync(null);
    private async Task RunOnceAsync(string? target)
    {
        if (flow is null)
            throw new InvalidOperationException("编辑器尚未初始化。");
        FreezeCameraDisplay();
        var total = Stopwatch.StartNew();
        if (useGlobalCamera) await CaptureCameraSourceAsync();
        double captureMs = total.Elapsed.TotalMilliseconds;
        ImageView.EndEdit();
        inputFrame?.Dispose();
        inputFrame = null;
        inputNode = null;
        inputPosition = null; inputFixture = null;
        ClearResult(false);
        ShowSource();
        string selected = target ?? Graph.SelectedId ?? flow.Terminals().LastOrDefault() ?? "";
        var definition = target is null ? flow.ExecutionRecipe() : flow.RecipeToNode(target);
        if (flow.PublicInterfaceIssue() is string issue && issue.Length > 0)
            Log(issue + "；本次运行使用当前流程输出，保存后会修复接口。");
        result = await ExecuteAsync(definition, selected, KeepPreviews.IsChecked == true);
        var timing = executionCache?.LastRunTiming;
        var display = Stopwatch.StartNew();
        foreach (var node in result.Data.GetProperty("nodes").EnumerateObject())
            Graph.NodeStates[node.Name] = node.Value.GetProperty("status").GetString() switch { "Succeeded" => $"完成 · {node.Value.GetProperty("elapsedMs").GetDouble():F1} ms", "Skipped" => "跳过 · 数据不可用", _ => "错误" };
        if (result.Status != ExecutionStatus.Succeeded)
        {
            string message = result.Data.TryGetProperty("error", out var e) ? e.ToString() : result.Status.ToString();
            ClearResult();
            throw new InvalidOperationException(message);
        }
        Graph.Select(selected);
        Graph.Refresh();
        foreach (var entry in result.Data.GetProperty("measurements").EnumerateObject())
        {
            if (entry.Value.TryGetProperty("detection", out var resolution) && resolution.GetProperty("downsampled").GetBoolean())
                Log($"{flow.Spec(entry.Name).Label} · 大图检测 {resolution.GetProperty("inputWidth")} × {resolution.GetProperty("inputHeight")} → {resolution.GetProperty("workWidth")} × {resolution.GetProperty("workHeight")}；输出为原图坐标，精确测量建议缩小 ROI 使用原图／卡尺。");
        }
        StatusText.Text = result.Decision == InspectionDecision.Unknown ? $"● 处理完成 · {result.ElapsedMs:F1} ms" : $"● 检测{(result.Decision == InspectionDecision.Pass ? "通过" : "不通过")} · {result.ElapsedMs:F1} ms";
        Log($"执行完成 · 算法 {result.ElapsedMs:F1} ms · 总计 {total.Elapsed.TotalMilliseconds:F1} ms · {Graph.NodeStates.Count} 个算子");
        if (timing is not null)
            Log($"耗时分解 · 取图 {captureMs:F1} ms · 配方准备 {timing.PreparationMs:F1} ms（{(timing.CacheHit ? "复用" : "重建")}）· SDK 执行 {timing.ExecutionMs:F1} ms · 结果显示准备 {display.Elapsed.TotalMilliseconds:F1} ms");
    }
    private void LoadRecipe(string path)
    {
        EnsureEditor();
        if (flow is null)
            return;
        path = Path.GetFullPath(path);
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        string jsonFile = Directory.Exists(path) ? Path.Combine(path, "recipe.json") : path;
        if (new FileInfo(jsonFile).Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("配方文件超过 16 MiB。");
        string layout = Path.Combine(folder, "layout.json");
        flow.Load(File.ReadAllText(jsonFile), File.Exists(layout) ? File.ReadAllText(layout) : null);
        // Reopening even an identical definition resets per-run inputs to their recipe defaults.
        InvalidateFlowExecution();
        recipeDirectory = folder;
        runInputs.Clear();
        foreach (var input in flow.Definition["inputs"]!.AsObject())
        if (input.Value!["type"]!.GetValue<string>() != "Image")
            runInputs[input.Key] = JsonScalar(input.Value["default"]);
        UpdateTitle();
        Graph.Select(flow.Terminals().LastOrDefault());
        Graph.Fit();
        Log("打开配方：" + Path.GetFileName(folder));
    }
    private static object? JsonScalar(JsonNode? node)
    {
        if (node is null)
            return null;
        using var d = JsonDocument.Parse(node.ToJsonString());
        return d.RootElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => d.RootElement.TryGetInt64(out long n) ? n : d.RootElement.GetDouble(),
            JsonValueKind.String => d.RootElement.GetString(),
            _ => null
        };
    }
    private void LoadImage(string path)
    {
        EnsureEditor();
        if (new FileInfo(path).Length > 128L * 1024 * 1024)
            throw new InvalidOperationException("图像文件超过 128 MiB。");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32L * 1024 * 1024)
            throw new InvalidOperationException("图像超过 32 百万像素，请缩小后导入。");
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        int stride = checked(converted.PixelWidth * 3);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var frame = VisionFrame.CopyFrom(pixels, converted.PixelWidth, converted.PixelHeight, stride, VisionPixelFormat.Bgr24);
        SetFileSource(frame);
        imagePath = Path.GetFullPath(path);
        inputFrame?.Dispose();
        inputFrame = null;
        inputNode = null;
        inputPosition = null; inputFixture = null;
        ClearResult(false);
        ImageView.EndEdit();
        ShowSource();
        BuildInspector();
        Log($"输入图像：{Path.GetFileName(path)} · {converted.PixelWidth} × {converted.PixelHeight}");
    }
    private void ShowSource()
    {
        if (TryShowCameraDisplay()) return;
        if (source is null)
            return;
        ImageView.Roi = ImageView.Template = RoiRegion.None;
        ImageView.Overlays = null;
        ImageView.SetFrame(source);
        UpdateImageInfo(useGlobalCamera ? "固定相机图像" : "原图");
    }
    private void UpdateImageInfo(string mode)
    {
        if (ImageView.Info is not FrameInfo i)
            return;
        ImageInfo.Text = $"{mode}  │  {i.Width} × {i.Height}  │  {(i.PixelFormat == VisionPixelFormat.Gray8 ? "Gray" : "BGR")} 8-bit  │  原点 ({i.OriginX}, {i.OriginY})";
        if (useGlobalCamera && sourceCameraMetadata is { } frame) ImageInfo.Text += $"  │  SN {frame.Serial} · 帧 {frame.FrameNumber} · {frame.ReceivedAt.ToLocalTime():HH:mm:ss.fff}";
    }
    private async Task PrepareInputAsync(string id)
    {
        FreezeCameraDisplay(2);
        if (flow is null || source is null)
            throw new InvalidOperationException(useGlobalCamera ? "请先在全局相机管理中“取图用于教学”；ROI 与模板使用固定图像。" : "请先打开图像并连接算子。");
        inputFrame?.Dispose();
        inputFrame = null;
        inputNode = null;
        inputPosition = null; inputFixture = null;
        ImageView.EndEdit();
        using var preview = await ExecuteAsync(flow.InputRecipe(id), "", false);
        if (preview.Status != ExecutionStatus.Succeeded)
            throw new InvalidOperationException("上游算子执行失败。");
        bindingPreview = preview.Data.Clone();
        var frame = preview.GetOutputImage("InputPreview");
        inputFrame = frame;
        inputNode = id;
        inputPosition = preview.Outputs.GetProperty("InputPreview").TryGetProperty("matchPosition", out var position)
            ? position.Deserialize<VisionMatchPosition>() : null;
        ReadInputFixture(preview.Outputs.GetProperty("InputPreview"));
        ImageView.SetFrame(frame);
        ImageView.Roi = CurrentInputRoi(id);
        ImageView.Template = flow.TemplateRegions.GetValueOrDefault(id, RoiRegion.None);
        ImageView.Overlays = null;
        SetCaliperGuides(id);
        UpdateImageInfo("算子输入");
            ApplyImageCoordinateLabel(preview.Outputs.GetProperty("InputPreview"));
        BuildInspector();
    }
    private async Task EditRoiAsync()
    {
        if (flow is null || Graph.SelectedId is not string id || !flow.Spec(id).Parameters.Any(p => p!["type"]!.GetValue<string>() == "roi"))
        {
            Error(new InvalidOperationException("请选择支持 ROI 的图像算子。"));
            return;
        }
        await BusyAsync(async () => { EnsureManualRoi(id); await PrepareInputAsync(id); ImageView.BeginAdjust(false); InspectorTabs.SelectedIndex = 0; Log("ROI 使用当前算子的输入图坐标，可绘制后拖动及调整顶点。"); });
    }
    private void AddNode(string type, EditorPoint point)
    {
        EnsureEditor();
        if (flow is null)
            return;
        string id = flow.Add(type, point.X, point.Y);
        Graph.Select(id);
        Log("插入算子：" + flow.Spec(id).Label);
    }
    private bool ConfirmDiscard() => flow?.Dirty != true || MessageBox.Show(this, "当前配方尚未保存，是否放弃本次修改？", "JP-Vision", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void SaveRecipe(bool saveAs)
    {
        EnsureEditor();
        if (flow is null)
            return;
        string? target = recipeDirectory;
        if (saveAs || target is null || target.StartsWith(Path.Combine(AppContext.BaseDirectory, "samples"), StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new SaveFileDialog { Filter = "JP-Vision 配方文件夹|*.vrecipe", FileName = "process.vrecipe", AddExtension = true, DefaultExt = ".vrecipe" };
            if (dialog.ShowDialog(this) != true)
                return;
            target = dialog.FileName;
            if (File.Exists(target))
                throw new InvalidOperationException("此名称已被普通文件占用，请使用新名称。");
            if (Directory.Exists(target) && !string.Equals(target, recipeDirectory, StringComparison.OrdinalIgnoreCase) && MessageBox.Show(this, "将更新此文件夹中的配方，是否继续？", "JP-Vision", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
        }
        Directory.CreateDirectory(target);
        var definition = flow.PublishedRecipe();
        string sealedJson = VisionEngine.SealRecipe(definition.ToJsonString());
        WriteAtomic(Path.Combine(target, "recipe.json"), sealedJson);
        WriteAtomic(Path.Combine(target, "layout.json"), flow.LayoutJson().ToJsonString());
        flow.Definition["outputs"] = definition["outputs"]!.DeepClone();
        flow.Definition["kind"] = definition["kind"]!.DeepClone();
        flow.Definition["decisionOutput"] = definition["decisionOutput"]!.DeepClone();
        // Saving materializes the automatic interfaces used by ExecutionRecipe without a Changed event.
        observedFlowDefinition = flow.Definition.ToJsonString();
        recipeDirectory = target;
        flow.MarkSaved();
        UpdateTitle();
        Log("保存配方：" + target);
    }
    private static void WriteAtomic(string path, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        if (busy || cameraCommandBusy)
        {
            e.Cancel = true;
            closePending = true;
            cancellation?.Cancel();
            return;
        }
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        licenseTimer.Stop();
        if (globalCamera is not null)
        {
            e.Cancel = true;
            cameraCommandBusy = true; UpdateEnabled();
            try { await CloseCameraAsync(); }
            catch (Exception ex) { Log("退出时相机停止：" + ex.Message, true); }
            finally { cameraCommandBusy = false; allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        licenseTimer.Stop();
        StopCameraDisplay();
        try { executionCache?.Dispose(); }
        catch (Exception ex) { Log("退出时执行缓存清理：" + ex.Message, true); }
        result?.Dispose();
        inputFrame?.Dispose();
        if (!ReferenceEquals(source, fileSourceSnapshot)) fileSourceSnapshot?.Dispose();
        source?.Dispose();
        engine?.Dispose();
        base.OnClosed(e);
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (busy || cameraCommandBusy || e.OriginalSource is TextBox || e.OriginalSource is ComboBox)
            return;
        if (e.Key == Key.F5)
        {
            RunClick(this, new());
            e.Handled = true;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.S)
                SaveClick(this, new());
            else if (e.Key == Key.O)
                OpenRecipeClick(this, new());
            else if (e.Key == Key.N)
                NewClick(this, new());
            else if (e.Key == Key.Z)
                UndoClick(this, new());
            else if (e.Key == Key.Y)
                RedoClick(this, new());
            else if (e.Key == Key.D)
                DuplicateClick(this, new());
            else
                return;
            e.Handled = true;
        }
        if (e.Key == Key.Delete && Graph.IsKeyboardFocusWithin)
        {
            DeleteClick(this, new());
            e.Handled = true;
        }
    }
    private void NewClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); if (!ConfirmDiscard()) return; flow?.Reset(); recipeDirectory = null; runInputs.Clear(); Graph.Select(null); UpdateTitle(); });
    private static OpenFileDialog CreateRecipeOpenDialog()
    {
        // Shell item parsing requires native separators even when Directory.Exists accepts '/'.
        string samples = Path.Combine(AppContext.BaseDirectory, "samples", "recipes");
        return new OpenFileDialog
        {
            Filter = "JP-Vision 配方|recipe.json;*.json",
            InitialDirectory = Directory.Exists(samples) ? samples : AppContext.BaseDirectory,
            Title = "打开配方：选择 .vrecipe 文件夹中的 recipe.json"
        };
    }
    private void OpenRecipeClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); if (!ConfirmDiscard()) return; var dialog = CreateRecipeOpenDialog(); if (dialog.ShowDialog(this) == true) LoadRecipe(dialog.FileName); });
    private void SaveClick(object sender, RoutedEventArgs e) => Safe(() => SaveRecipe(false));
    private void SaveAsClick(object sender, RoutedEventArgs e) => Safe(() => SaveRecipe(true));
    private void OpenImageClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); var dialog = new OpenFileDialog { Filter = "图像|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*" }; if (dialog.ShowDialog(this) == true) LoadImage(dialog.FileName); });
    private void UndoClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); flow?.Undo(); });
    private void RedoClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); flow?.Redo(); });
    private void DeleteClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); if (Graph.SelectedId is string id) flow?.Delete(id); });
    private async void RunClick(object sender, RoutedEventArgs e) => await BusyAsync(RunOnceAsync);
    private void ValidateClick(object sender, RoutedEventArgs e) => Safe(() => { EnsureEditor(); if (flow is not null && engine is not null) { engine.ValidateRecipe(VisionEngine.SealRecipe(flow.ExecutionRecipe().ToJsonString())); Log("流程校验成功"); StatusText.Text = "● 流程校验成功"; } });
    private void CancelClick(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void RoiClick(object sender, RoutedEventArgs e) => await EditRoiAsync();
    private void InsertClick(object sender, RoutedEventArgs e) => Safe(() => { if (chosenType is not null) AddNode(chosenType.Type, new(40 + (flow?.Nodes.Count ?? 0) % 4 * 230, 45 + (flow?.Nodes.Count ?? 0) / 4 * 190)); });
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (flow is not null)
            BuildToolbox();
    }
    private void GraphZoomOutClick(object sender, RoutedEventArgs e) => Graph.Zoom(1 / 1.15);
    private void GraphZoomInClick(object sender, RoutedEventArgs e) => Graph.Zoom(1.15);
    private void GraphFitClick(object sender, RoutedEventArgs e) => Graph.Fit();
    private void ImageZoomOutClick(object sender, RoutedEventArgs e) => ImageView.Zoom(1 / 1.15);
    private void ImageZoomInClick(object sender, RoutedEventArgs e) => ImageView.Zoom(1.15);
    private void ImageFitClick(object sender, RoutedEventArgs e) => ImageView.Fit();
    private async void ImageChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImageView is null || buildingInspector || busy || cameraCommandBusy)
            return;
        if (TrySelectCameraDisplay()) return;
        if (ImageChoice.SelectedIndex == 1)
        {
            ImageView.EndEdit();
            ShowSource();
        }
        else if (ImageChoice.SelectedIndex == 2 && Graph.SelectedId is string id)
            await BusyAsync(() => PrepareInputAsync(id));
        else
            DisplaySelectedResult();
    }
    private void ShowMarksChanged(object sender, RoutedEventArgs e)
    {
        if (ImageView is null)
            return;
        ImageView.ShowOverlays = ShowMarks.IsChecked == true;
        ImageView.InvalidateVisual();
    }
    private void ClearLogClick(object sender, RoutedEventArgs e) => LogList.Items.Clear();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void HelpClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, "1. 打开图像，在工具箱双击或拖入算子。\n2. 从输出端口拖到相同类型的输入端口；右键输入可断开。\n3. 选择节点调整参数，点击 ROI 编辑后绘制或拖动区域。\n4. 快速匹配／定位内绘制模板，点击学习模板，再执行。\n5. 算子输出显示图像和标记；运行结果页显示坐标、尺寸和各端口值。\n6. 保存为 .vrecipe 文件夹，WPF 工程可通过 SDK 直接加载。\n\n中键拖动画布／图像，滚轮缩放，F5 执行。海康 GigE 相机：在“相机管理”枚举并按序列号连接，开始预览后选择“全局相机”；F5 等待新帧执行，ROI／模板教学先“取图用于教学”。", "JP-Vision 操作说明");
    private void AboutClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, "JP-Vision 0.7.2\nC# WPF 编辑器 · C++ / OpenCV 5 核心\nWindows x64 · 离线机器绑定永久授权", "JP-Vision");
}

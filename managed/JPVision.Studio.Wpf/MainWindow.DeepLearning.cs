using JPVision.Editor.Core;
using JPVision.Sdk;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private void BuildDeepLearningModelPanel(string id)
    {
        var document = flow!;
        var parameters = document.Node(id)["parameters"]!;
        string root = parameters["modelRoot"]?.GetValue<string>() ?? "";
        string key = parameters["modelKey"]?.GetValue<string>() ?? "";
        Header(Parameters, "缺陷模型");
        var body = new StackPanel();
        var status = Note(key.Length == 64 ? "当前模型：" + key[..12] + "…（运行时再次验证）" : "尚未选择模型，请导入云端训练导出的模型清单。", false);
        status.Tag = "deep-learning:model-status";
        body.Children.Add(status);
        var choice = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 4, 0, 6), Tag = "deep-learning:models" };
        body.Children.Add(choice);
        var row = new WrapPanel();
        var import = new Button { Content = "导入模型…", Tag = "deep-learning:import", Margin = new Thickness(0, 0, 5, 5) };
        var refresh = new Button { Content = "列出已安装模型", Tag = "deep-learning:refresh", Margin = new Thickness(0, 0, 5, 5) };
        var cancel = new Button { Content = "取消", IsEnabled = false, Tag = "deep-learning:cancel", Margin = new Thickness(0, 0, 0, 5) };
        row.Children.Add(import); row.Children.Add(refresh); row.Children.Add(cancel); body.Children.Add(row);
        body.Children.Add(Note("仅支持导出为单输入、单通道分数图的 ONNX 模型。导入检查清单与文件哈希；ONNX 张量和运行支持在首次执行时检查。"));
        body.Children.Add(Note("图像保持干净，缺陷区域以绿色独立标记。异常检测分数不是缺陷类型或校准概率；阈值和最小面积须根据独立验证集设置。"));
        body.Children.Add(Note("模型仓库：" + (string.IsNullOrWhiteSpace(root) ? DeepLearningModelRepository.DefaultRoot : root)));
        CancellationTokenSource? operation = null;
        bool loading = false;
        void Select(InstalledDeepLearningModel model)
        {
            if (!ReferenceEquals(flow, document) || !document.Nodes.OfType<JsonObject>().Any(node => FlowDocument.Id(node) == id)) return;
            EnsureEditor();
            int minimumDimension = Math.Min(model.Manifest.Input.Width, model.Manifest.Input.Height);
            int overlap = document.Node(id)["parameters"]!["overlap"]?.GetValue<int>() ?? 32;
            bool adjustOverlap = overlap >= minimumDimension;
            document.Change(() =>
            {
                document.Node(id)["parameters"]!["modelRoot"] = string.IsNullOrWhiteSpace(root) ? "" : Path.GetFullPath(root);
                document.Node(id)["parameters"]!["modelKey"] = model.ModelKey;
                if (adjustOverlap) document.Node(id)["parameters"]!["overlap"] = Math.Min(32, minimumDimension / 4);
            });
            Log($"缺陷模型已选择：{model.Manifest.ModelId} · {model.Manifest.Version}。首次执行时验证 ONNX 合同。");
            if (adjustOverlap) Log($"分块重叠已调整为 {Math.Min(32, minimumDimension / 4)} px，以适配 {model.Manifest.Input.Width} × {model.Manifest.Input.Height} 模型输入。");
        }
        choice.SelectionChanged += (_, _) => { if (!loading && choice.SelectedItem is InstalledDeepLearningModel model) Safe(() => Select(model)); };
        async Task LoadAsync(string? manifestPath)
        {
            if (operation is not null) return;
            using var cancellation = new CancellationTokenSource(); operation = cancellation;
            import.IsEnabled = refresh.IsEnabled = choice.IsEnabled = false; cancel.IsEnabled = true;
            status.Text = manifestPath is null ? "正在验证已安装模型…" : "正在导入并验证文件哈希…";
            try
            {
                var repository = new DeepLearningModelRepository(root);
                if (manifestPath is not null)
                {
                    var model = await Task.Run(() => repository.ImportAsync(manifestPath, cancellation.Token));
                    Select(model);
                }
                else
                {
                    var models = await Task.Run(() => repository.ListAsync(cancellation.Token));
                    loading = true;
                    try { choice.ItemsSource = models; choice.SelectedItem = models.FirstOrDefault(model => model.ModelKey == key); }
                    finally { loading = false; }
                    status.Text = $"{models.Count} 个模型文件完整性通过；选择后运行以验证 ONNX 合同。";
                }
            }
            catch (OperationCanceledException) { status.Text = "已取消模型操作，原模型选择保持不变。"; }
            catch (Exception ex) { status.Text = "模型操作失败：" + ex.Message; Log(status.Text, true); }
            finally { operation = null; import.IsEnabled = refresh.IsEnabled = choice.IsEnabled = true; cancel.IsEnabled = false; }
        }
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "JP-Vision 模型清单|manifest.json|JSON 清单|*.json", Title = "选择与 model.onnx 同目录的 manifest.json" };
            if (dialog.ShowDialog(this) == true) await LoadAsync(dialog.FileName);
        };
        refresh.Click += async (_, _) => await LoadAsync(null);
        cancel.Click += (_, _) => operation?.Cancel();
        body.Unloaded += (_, _) => operation?.Cancel();
        Parameters.Children.Add(body);
    }

    private bool AddDeepLearningResult(StackPanel target, JsonElement packet)
    {
        if (!packet.TryGetProperty("resultKind", out var kind) || kind.GetString() != "deepLearningDefect") return false;
        bool available = packet.TryGetProperty("dataAvailable", out var state) && state.GetBoolean();
        Header(target, "深度学习缺陷分割");
        target.Children.Add(Note("本次结果：" + (!available ? "不可用" : Int(packet, "count") == 0 ? "未发现达到筛选条件的缺陷" : "发现缺陷"), false));
        target.Children.Add(Note("坐标：" + ResultCoordinates(packet)));
        target.Children.Add(Note($"模型：{ScalarProperty(packet, "modelId")} · {ScalarProperty(packet, "modelVersion")}", false));
        target.Children.Add(Note($"缺陷数量：{ScalarProperty(packet, "count")} · 总面积：{ScalarProperty(packet, "totalArea")} px²", false));
        target.Children.Add(Note($"最大分数：{ScalarProperty(packet, "maximumScore")} · 推理分块：{ScalarProperty(packet, "tileCount")}", false));
        if (available && packet.TryGetProperty("scoreKind", out var scoreKind))
            target.Children.Add(Note(scoreKind.GetString() == "anomaly" ? "分数类型：异常分数，数值可超过 1，不代表缺陷类别或分类概率。" : "分数类型：0～1 的模型概率输出，尚未经过业务校准。"));
        if (!available) target.Children.Add(Note("本次没有有效推理结果，不能判为合格，也不沿用上一帧结果。原因：" + ScalarProperty(packet, "reason"), false));
        else target.Children.Add(Note("面积为前景像素数；分数含义由训练模型决定。缺陷标记与图像像素独立传递。"));
        if (packet.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            var rows = features.EnumerateArray().Select((region, index) => new DeepLearningDefectRow(index + 1,
                ScalarProperty(region, "label"), Num(region, "score"), Num(region, "area"), Num(region, "centroidX"), Num(region, "centroidY"),
                Int(region, "x"), Int(region, "y"), Int(region, "width"), Int(region, "height"))).ToArray();
            target.Children.Add(ResultTable(rows, "deep-learning:results", ("序号", "Index"), ("类别", "Label"), ("分数", "Score"),
                ("面积 px²", "Area"), ("质心 X", "CenterX"), ("质心 Y", "CenterY"), ("左上 X", "X"), ("左上 Y", "Y"), ("宽", "Width"), ("高", "Height")));
        }
        return true;
    }

    private sealed record DeepLearningDefectRow(int Index, string Label, double Score, double Area, double CenterX, double CenterY, int X, int Y, int Width, int Height);
}

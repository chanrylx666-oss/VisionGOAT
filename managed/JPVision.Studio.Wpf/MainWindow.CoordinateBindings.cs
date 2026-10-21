using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JPVision.Studio;

public partial class MainWindow
{
    private JsonElement? bindingPreview;

    private JsonObject EffectiveParameters(string id)
    {
        var defaults = flow!.Spec(id).Defaults();
        var stored = flow.Node(id)["parameters"]!.AsObject();
        foreach (var item in stored) defaults[item.Key] = PreviewConstant(item.Value, defaults[item.Key]);
        foreach (var parameter in flow.Spec(id).Parameters.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "roi"))
        {
            string key = parameter["name"]!.GetValue<string>();
            defaults[key] = flow.ResolvePreviewRoi(id, key).ToJson();
        }
        var snapshot = result?.Data ?? bindingPreview;
        if (snapshot is JsonElement data && data.TryGetProperty("nodes", out var nodes) && nodes.TryGetProperty(id, out var node) &&
            node.TryGetProperty("resolvedParameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            foreach (var item in parameters.EnumerateObject()) defaults[item.Name] = JsonNode.Parse(item.Value.GetRawText());
        return defaults;
    }

    private static bool IsDynamicParameter(JsonNode? value) => value is JsonObject o && (o.ContainsKey("node") && o.ContainsKey("field") || o.ContainsKey("input") || o.ContainsKey("variable") || o.ContainsKey("expression"));
    private static bool HasDynamicParameter(JsonNode? value) => IsDynamicParameter(value) || value is JsonObject o && o.Any(p => HasDynamicParameter(p.Value)) || value is JsonArray a && a.Any(HasDynamicParameter);
    private static JsonNode? PreviewConstant(JsonNode? value, JsonNode? fallback = null)
    {
        if (IsDynamicParameter(value)) return value!["fallback"]?.DeepClone() ?? fallback?.DeepClone() ?? JsonValue.Create(0.0);
        if (value is JsonObject o)
        {
            var copy = new JsonObject();
            foreach (var item in o) copy[item.Key] = PreviewConstant(item.Value, (fallback as JsonObject)?[item.Key]);
            return copy;
        }
        if (value is JsonArray a) return new JsonArray(a.Select((item, i) => PreviewConstant(item, fallback is JsonArray f && i < f.Count ? f[i] : null)).ToArray());
        return value?.DeepClone();
    }
    private string ActiveRoiKey(string id) => HasMeasurementRois(id) ? MeasurementSide(id) + "Roi" : "roi";
    private bool RoiHasSubscriptions(string id, string? roiKey = null) => HasDynamicParameter(flow!.Node(id)["parameters"]?[roiKey ?? ActiveRoiKey(id)]);
    private void EnsureManualRoi(string id)
    {
        if (RoiHasSubscriptions(id)) throw new InvalidOperationException("此 ROI 含坐标订阅。请先在参数区解除 ROI 订阅，再绘制或拖动；订阅结果不会被拖动覆盖。");
    }
    private void UpdateParameterPath(string id, string path, JsonNode? value)
    {
        EnsureEditor();
        retainInput = inputFrame is not null && inputNode == id;
        try
        {
            flow!.SetParameterPath(id, path, value);
            if (retainInput) { ImageView.EndEdit(); ImageView.Roi = CurrentInputRoi(id); SetCaliperGuides(id); }
        }
        finally { retainInput = false; }
    }
    private void BuildRoiSubscriptionState(string id, string roiKey)
    {
        if (!RoiHasSubscriptions(id, roiKey)) return;
        Parameters.Children.Add(Note("此 ROI 已订阅数据，绘制和拖动已锁定。数值为本次运行解析值；尚未运行时显示编辑预览值，执行不会使用预览值兜底。", false));
        var release = new Button { Content = "解除此 ROI 的全部订阅", Tag = "roi:release:" + roiKey, HorizontalAlignment = HorizontalAlignment.Left };
        release.Click += (_, _) => Safe(() => UpdateParameterPath(id, roiKey, EffectiveParameters(id)[roiKey]));
        Parameters.Children.Add(release);
    }
    private void BuildCoordinateEditor(string id, string path, string label, double current, string? tag = null)
    {
        JsonObject definition = flow!.GetParameterDefinition(id, path);
        var stored = flow.GetParameterPath(id, path);
        bool subscribed = IsDynamicParameter(stored);
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Note(label));
        var edit = new TextBox { Text = current.ToString("0.########", CultureInfo.InvariantCulture), IsReadOnly = subscribed, Tag = tag ?? "coordinate:" + path,
            ToolTip = subscribed ? "已订阅：" + SubscriptionLabel(stored!) + "。点击右侧订阅按钮修改或切换为手动。" : "按 Enter 或离开输入框保存" };
        System.Windows.Automation.AutomationProperties.SetName(edit, label);
        Grid.SetColumn(edit, 1); row.Children.Add(edit);
        var bind = new Button { Content = subscribed ? "●" : "↗", Tag = "subscribe:" + path, Padding = new Thickness(5, 2, 5, 2), MinWidth = 25, Margin = new Thickness(4, 0, 0, 0), ToolTip = subscribed ? "修改订阅 / 改为手动：" + SubscriptionLabel(stored!) : "订阅上游结果、变量或计算表达式" };
        bind.Click += (_, _) => Safe(() => ShowParameterBinding(id, path, definition)); Grid.SetColumn(bind, 2); row.Children.Add(bind); Parameters.Children.Add(row);
        void Commit()
        {
            if (buildingInspector || subscribed || edit.Text == current.ToString("0.########", CultureInfo.InvariantCulture)) return;
            Safe(() =>
            {
                try
                {
                    var number = ParameterEditor.Parse(definition, edit.Text);
                    var candidate = CurrentInputRoi(id).ToJson();
                    if (candidate is not null)
                    {
                        var segments = path.Split('.').Skip(1).ToArray(); JsonNode cursor = candidate;
                        for (int i = 0; i < segments.Length - 1; i++) cursor = cursor is JsonArray array ? array[int.Parse(segments[i], CultureInfo.InvariantCulture)]! : cursor[segments[i]]!;
                        if (cursor is JsonArray lastArray) lastArray[int.Parse(segments[^1], CultureInfo.InvariantCulture)] = number.DeepClone(); else cursor[segments[^1]] = number.DeepClone();
                        var region = RoiRegion.FromJson(candidate);
                        if ((IsLineStrip(id) || IsEdgePointSampler(id)) && path.EndsWith(".height", StringComparison.Ordinal))
                        {
                            var p = EffectiveParameters(id); double sigma = p["sigma"]?.GetValue<double>() ?? 1;
                            double scale = FixtureActive(id) ? inputFixture!.ScaleRatio : 1;
                            if (region.Height < Math.Max(8, 6 * sigma + 6) * Math.Max(1, scale) || region.Height > (IsEdgePointSampler(id) ? 32768 : 512) * scale)
                                throw new ArgumentException("直线卡尺搜索高度不满足当前平滑与位置修正范围。");
                        }
                    }
                    if ((FixtureActive(id) || MatchPositionActive(id) || HasMeasurementRois(id) && MeasurementFixtureActive(id)) && !RoiHasSubscriptions(id))
                    {
                        retainInput = inputFrame is not null && inputNode == id;
                        try { CommitRoi(id, RoiRegion.FromJson(candidate)); }
                        finally { retainInput = false; }
                    }
                    else UpdateParameterPath(id, path, number);
                }
                catch { edit.Text = current.ToString("0.########", CultureInfo.InvariantCulture); throw; }
            });
        }
        edit.LostKeyboardFocus += (_, _) => Commit();
        edit.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        if (subscribed) Parameters.Children.Add(Note("↳ " + SubscriptionLabel(stored!), false));
    }
    private static string SubscriptionLabel(JsonNode value) => value["expression"] is JsonNode expression ? "计算：" + expression.GetValue<string>() : value["variable"] is JsonNode variable ? (value["scope"]?.GetValue<string>() == "global" ? "全局变量 · " : "局部变量 · ") + variable.GetValue<string>() : value["input"] is JsonNode input ? "公开输入 · " + input.GetValue<string>() : $"{value["node"]}.{value["port"]}.{value["field"]}";
    private void BuildRoiCoordinateEditors(string id, string roiKey, RoiRegion region, string? tagPrefix = null)
    {
        BuildRoiSubscriptionState(id, roiKey);
        var storedRegion = flow!.ResolvePreviewRoi(id, roiKey);
        if (storedRegion.Shape != region.Shape)
        {
            Parameters.Children.Add(Note(storedRegion.Shape == "none"
                ? "当前范围由定位结果生成。绘制并保存检测 ROI 后，可设置该 ROI 的坐标订阅。"
                : "当前图形已经过定位变换。请使用图中手柄调整；数值订阅需切换为图像坐标基准。", false));
            return;
        }
        void Number(string field, string label, double value) => BuildCoordinateEditor(id, roiKey + "." + field, label, value, tagPrefix is null ? null : tagPrefix + field);
        switch (region.Shape)
        {
            case "rectangle": Number("x", "X", region.X); Number("y", "Y", region.Y); Number("width", "宽", region.Width); Number("height", "高", region.Height); break;
            case "circle": Number("cx", "圆心 X", region.X); Number("cy", "圆心 Y", region.Y); Number("radius", "半径", region.Radius); break;
            case "annulus": Number("cx", "圆心 X", region.X); Number("cy", "圆心 Y", region.Y); Number("innerRadius", "内半径", region.InnerRadius); Number("outerRadius", "外半径", region.Radius); Number("startAngle", "起始角（°）", region.StartAngle); Number("endAngle", "终止角（°）", region.EndAngle); break;
            case "rotatedRectangle": Number("cx", "中心 X", region.X); Number("cy", "中心 Y", region.Y); Number("width", "宽 / 长度", region.Width); Number("height", "高 / 搜索", region.Height); Number("angleDegrees", "旋转角（°）", region.AngleDegrees); break;
            case "polygon":
                for (int i = 0; i < region.Points.Count; i++) { BuildCoordinateEditor(id, roiKey + $".points.{i}.0", $"顶点 {i + 1} X", region.Points[i].X); BuildCoordinateEditor(id, roiKey + $".points.{i}.1", $"顶点 {i + 1} Y", region.Points[i].Y); }
                break;
        }
    }
}

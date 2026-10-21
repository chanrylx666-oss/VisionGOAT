using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JPVision.Studio;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> parameterGroupExpansion = new();

    private void BuildParameterGroups(string id, IEnumerable<JsonObject> parameters)
    {
        string component = flow!.Spec(id).Type;
        foreach (var group in parameters.GroupBy(p => ParameterEditor.Describe(component, p).Group))
        {
            var body = new StackPanel { Margin = new Thickness(8, 0, 3, 8) };
            foreach (var p in group) BuildParameter(id, p, body);
            string key = component + "/" + group.Key;
            bool expanded = parameterGroupExpansion.GetValueOrDefault(key, group.Key is not ("定位与跟随" or "目标选择与数量判定" or "尺寸公差" or "后续检测图像" or "显示与标记" or "高级资源限制"));
            var panel = new Expander { Header = group.Key + $" · {group.Count()}", Content = body, IsExpanded = expanded, Tag = "group:" + group.Key };
            panel.Expanded += (_, _) => parameterGroupExpansion[key] = true;
            panel.Collapsed += (_, _) => parameterGroupExpansion[key] = false;
            Parameters.Children.Add(panel);
        }
    }

    private void BuildParameter(string id, JsonObject parameter, Panel? target = null)
    {
        target ??= Parameters;
        string component = flow!.Spec(id).Type;
        string key = parameter["name"]!.GetValue<string>(), type = parameter["type"]!.GetValue<string>(), label = parameter["label"]!.GetValue<string>();
        if (key == "roiEdgeIndex") label = "多边形参考边";
        var help = ParameterEditor.Describe(component, parameter);
        JsonNode? value = flow.Node(id)["parameters"]?[key] ?? parameter["default"];
        var heading = new Grid { Margin = new Thickness(0, 6, 0, 4), ToolTip = help.Description.Length == 0 ? null : help.Description };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var reset = new Button { Content = "↺", Padding = new Thickness(5, 0, 5, 0), MinWidth = 24, Focusable = false, Tag = "reset:" + key, ToolTip = "恢复此参数默认值" };
        AutomationProperties.SetName(reset, "恢复" + label + "默认值");
        reset.Click += (_, _) => Safe(() => UpdateParameter(id, key, (parameter["editorDefault"] ?? parameter["default"])?.DeepClone()));
        Grid.SetColumn(reset, 1); heading.Children.Add(reset); target.Children.Add(heading);

        if (parameter["bindable"]?.GetValue<bool>() == true)
        {
            var bind = new Button { Content = "订阅结果 / 变量 / 计算…", Tag = "subscribe:" + key, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3) };
            bind.Click += (_, _) => Safe(() => ShowParameterBinding(id, key, parameter)); target.Children.Add(bind);
        }
        if (value is JsonObject binding)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Note(SubscriptionLabel(binding)));
            var constant = new Button { Content = "改为常量", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8, 0, 0, 0) };
            constant.Click += (_, _) => Safe(() => UpdateParameter(id, key, parameter["default"])); row.Children.Add(constant); target.Children.Add(row); return;
        }
        void AddHelp()
        {
            string limits = ParameterEditor.Limits(component, parameter);
            if (limits.Length > 0) { var note = Note(limits); note.FontSize = 10.5; target.Children.Add(note); }
            if (help.Description.Length > 0) { var note = Note(help.Description); note.FontSize = 10.5; note.Margin = new Thickness(0, 0, 0, 5); target.Children.Add(note); }
        }
        if (type == "bool")
        {
            var check = new CheckBox { IsChecked = value?.GetValue<bool>() == true, Content = "启用", Margin = new Thickness(0, 0, 0, 5), Tag = key, ToolTip = help.Description };
            AutomationProperties.SetName(check, label);
            check.Checked += (_, _) => { if (!buildingInspector) Safe(() => UpdateParameter(id, key, JsonValue.Create(true))); };
            check.Unchecked += (_, _) => { if (!buildingInspector) Safe(() => UpdateParameter(id, key, JsonValue.Create(false))); };
            target.Children.Add(check); AddHelp(); return;
        }
        if (type == "enum")
        {
            var values = parameter["choices"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            string ChoiceText(string v) => component == "BlobAnalysis" ? BlobEnumText(key, v) : key == "statistic" && component == "MeasurePointLineDistances" ? v switch { "minimum" => "最小距离", "maximum" => "最大距离", "mean" => "平均距离", "rms" => "均方根距离", "range" => "距离极差", _ => v } : key == "method" && component == "FindLines" ? v == "automatic" ? "自动找线（多候选）" : "卡尺拟合（单直线）" : EnumText(v);
            var combo = new ComboBox { ItemsSource = values.Select(ChoiceText).ToArray(), SelectedIndex = Array.IndexOf(values, value!.GetValue<string>()), Margin = new Thickness(0, 0, 0, 5), Tag = key };
            AutomationProperties.SetName(combo, label);
            combo.SelectionChanged += (_, _) => { if (!buildingInspector && combo.SelectedIndex >= 0) Safe(() => UpdateParameter(id, key, JsonValue.Create(values[combo.SelectedIndex]))); };
            target.Children.Add(combo); AddHelp(); return;
        }
        if (component == "BlobAnalysis" && key == "connectivity")
        {
            int[] values = { 4, 8 };
            int current = int.Parse(value!.ToJsonString(), CultureInfo.InvariantCulture);
            var combo = new ComboBox { ItemsSource = new[] { "4 邻域（上下左右）", "8 邻域（含对角）" }, SelectedIndex = Array.IndexOf(values, current), Margin = new Thickness(0, 0, 0, 5), Tag = key };
            AutomationProperties.SetName(combo, label);
            combo.SelectionChanged += (_, _) => { if (!buildingInspector && combo.SelectedIndex >= 0) Safe(() => UpdateParameter(id, key, JsonValue.Create(values[combo.SelectedIndex]))); };
            target.Children.Add(combo); AddHelp(); return;
        }
        if (type == "integer" && (key is "projectionWidth" or "roiEdgeIndex" || key == "borderValue" && component == "Morphology"))
        {
            var roi = RoiRegion.FromJson(EffectiveParameters(id)["roi"]);
            int minimumWidth = double.TryParse(parameter["min"]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double minimum) ? (int)Math.Ceiling(minimum) : 1;
            int maximumWidth = double.TryParse(parameter["max"]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double maximum) ? (int)Math.Floor(maximum) : 63;
            int[] values = key == "projectionWidth" ? Enumerable.Range(minimumWidth, Math.Max(0, maximumWidth - minimumWidth + 1)).Where(i => i % 2 == 1).ToArray() : key == "borderValue" ? new[] { -1, 0, 255 } : Enumerable.Range(0, roi.Points.Count).ToArray();
            int current = int.TryParse(value?.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : int.MinValue;
            var combo = new ComboBox { ItemsSource = values.Select(i => key == "projectionWidth" ? i + " px" : key == "borderValue" ? i == -1 ? "自动（-1）" : i == 0 ? "黑色（0）" : "白色（255）" : $"第 {i + 1} 条边 · 顶点 {i + 1} → {(i + 1) % roi.Points.Count + 1}").ToArray(), SelectedIndex = Array.IndexOf(values, current), Margin = new Thickness(0, 0, 0, 5), Tag = key };
            AutomationProperties.SetName(combo, label);
            combo.SelectionChanged += (_, _) => { if (!buildingInspector && combo.SelectedIndex >= 0) Safe(() => UpdateParameter(id, key, JsonValue.Create(values[combo.SelectedIndex]))); };
            target.Children.Add(combo);
            if (combo.SelectedIndex < 0) target.Children.Add(Note("当前值无效，请重新选择。", false));
            AddHelp(); return;
        }
        bool numeric = type is "integer" or "number";
        string display = numeric ? value?.ToJsonString() ?? "0" : value?.GetValue<string>() ?? "";
        var edit = new TextBox { Text = display, Margin = new Thickness(0, 0, 0, 5), Tag = key, ToolTip = help.Description };
        AutomationProperties.SetName(edit, label);
        void Commit(int direction = 0)
        {
            if (buildingInspector || direction == 0 && edit.Text == display) return;
            Safe(() =>
            {
                try
                {
                    var changed = direction == 0 ? ParameterEditor.Parse(parameter, edit.Text) : ParameterEditor.Next(component, parameter, edit.Text, direction);
                    UpdateParameter(id, key, changed);
                    display = numeric ? changed.ToJsonString() : changed.GetValue<string>();
                    edit.Text = display;
                }
                catch (ArgumentException) { edit.Text = display; throw; }
            });
        }
        edit.LostKeyboardFocus += (_, _) => Commit();
        edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (numeric && e.Key is Key.Up or Key.Down) { Commit(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        };
        if (numeric)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(edit);
            foreach (int direction in new[] { -1, 1 })
            {
                var step = new Button { Content = direction < 0 ? "−" : "+", Focusable = false, MinWidth = 26, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(3, 0, 0, 5), Tag = (direction < 0 ? "minus:" : "plus:") + key, ToolTip = $"步长 {help.Step.ToString(CultureInfo.InvariantCulture)} {help.Unit}" };
                AutomationProperties.SetName(step, (direction < 0 ? "减小" : "增大") + label);
                step.Click += (_, _) => Commit(direction); Grid.SetColumn(step, direction < 0 ? 1 : 2); row.Children.Add(step);
            }
            target.Children.Add(row);
        }
        else target.Children.Add(edit);
        AddHelp();
    }

    private IEnumerable<T> InspectorControls<T>() where T : DependencyObject
    {
        static IEnumerable<T> Walk(DependencyObject current)
        {
            if (current is T match) yield return match;
            IEnumerable<DependencyObject> children = current switch
            {
                Panel panel => panel.Children.Cast<DependencyObject>(),
                Decorator decorator when decorator.Child is not null => new[] { decorator.Child },
                ContentControl content when content.Content is DependencyObject child => new[] { child },
                _ => Array.Empty<DependencyObject>()
            };
            foreach (var child in children) foreach (var item in Walk(child)) yield return item;
        }
        return Walk(Parameters);
    }
}

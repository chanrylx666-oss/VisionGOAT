using JPVision.Editor.Core;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private void BuildInputBindings(string id)
    {
        if (flow!.Spec(id).Inputs.Count == 0) return;
        Header(Parameters, "输入来源");
        foreach (var input in flow.Spec(id).Inputs.Where(p => !p.Hidden || flow.Connections.Any(e => e!["toNode"]!.GetValue<string>() == id && e["toPort"]!.GetValue<string>() == p.Id)))
        {
            Parameters.Children.Add(Note(input.Label + " · " + input.Type));
            var candidates = flow.Nodes.Where(n => FlowDocument.Id(n!) != id)
                .SelectMany(n => flow.Spec(FlowDocument.Id(n!)).Outputs.Where(p => input.Accepts(p.Type))
                    .Select(p => (Id: FlowDocument.Id(n!), Port: p.Id, Label: flow.Spec(FlowDocument.Id(n!)).Label + " [" + FlowDocument.Id(n!) + "] · " + p.Label))).ToArray();
            var incoming = flow.Connections.FirstOrDefault(e => e!["toNode"]!.GetValue<string>() == id && e["toPort"]!.GetValue<string>() == input.Id);
            int selected = Array.FindIndex(candidates, c => c.Id == incoming?["fromNode"]?.GetValue<string>() && c.Port == incoming?["fromPort"]?.GetValue<string>());
            var combo = new ComboBox { ItemsSource = new[] { "未连接" }.Concat(candidates.Select(c => c.Label)).ToArray(), SelectedIndex = selected + 1, Margin = new Thickness(0, 0, 0, 8) };
            combo.SelectionChanged += (_, _) => { if (buildingInspector || combo.SelectedIndex < 0) return; Safe(() => { if (combo.SelectedIndex == 0) flow.Disconnect(id, input.Id); else { var c = candidates[combo.SelectedIndex - 1]; flow.ReplaceInput(c.Id, c.Port, id, input.Id); } }); };
            Parameters.Children.Add(combo);
        }
    }
    private void ShowParameterBinding(string id, string key, JsonObject parameter)
    {
        var choices = ParameterBindingChoices(id, key, parameter["type"]!.GetValue<string>());
        ShowValueBindingEditor("订阅参数 · " + parameter["label"], parameter["type"]!.GetValue<string>(), flow!.GetParameterPath(id, key), choices,
            value => UpdateParameterPath(id, key, value), parameter["default"]);
    }
    private List<(string Label, JsonObject Binding)> ParameterBindingChoices(string id, string? path, string target)
    {
        var choices = (path is null ? flow!.Nodes.Where(n => FlowDocument.Id(n!) != id)
            .SelectMany(n => flow.Spec(FlowDocument.Id(n!)).ScalarFields.Where(f => BindingTypesCompatible(target, f.Type))
                .Select(f => new ParameterSource(FlowDocument.Id(n!), f, flow.Spec(FlowDocument.Id(n!)).Label + " [" + FlowDocument.Id(n!) + "] · " + f.Label + $" ({f.Port}.{f.Field})")))
            : flow!.ParameterSources(id, path)).Select(s => (s.Label, Binding: new JsonObject
                { ["node"] = s.Node, ["port"] = s.Field.Port, ["field"] = s.Field.Field })).ToList();
        foreach (var p in flow.Definition["inputs"]!.AsObject())
        {
            string type = p.Value!["type"]!.GetValue<string>();
            if (BindingTypesCompatible(target, type)) choices.Add(("公开输入 · " + p.Key, new JsonObject { ["input"] = p.Key }));
        }
        AddVariables(flow.Definition["variables"] as JsonObject, "global", "全局变量");
        AddVariables(flow.Node(id)["variables"] as JsonObject, "local", "本算子局部变量");
        return choices;
        void AddVariables(JsonObject? variables, string scope, string label)
        {
            if (variables is null) return;
            foreach (var variable in variables)
                if (BindingTypesCompatible(target, variable.Value?["type"]?.GetValue<string>() ?? ""))
                    choices.Add((label + " · " + variable.Key, new JsonObject { ["variable"] = variable.Key, ["scope"] = scope }));
        }
    }
    private static bool BindingTypesCompatible(string target, string source) => target switch
    {
        "integer" or "Int" => source == "Int", "number" or "Double" => source is "Int" or "Double", "bool" or "Bool" => source == "Bool", "string" or "enum" or "String" => source == "String", _ => false
    };
    private void ShowValueBindingEditor(string title, string type, JsonNode? current, List<(string Label, JsonObject Binding)> choices, Action<JsonNode?> applyValue, JsonNode? defaultValue = null)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Note("订阅使用本次执行的输出；上游无目标时跳过，不使用上一次坐标。坐标订阅会按输入图原点换算，展开图与原图坐标不能混用。"));
        var mode = new ComboBox { ItemsSource = new[] { "手动常量", "订阅结果 / 输入 / 变量", "计算表达式" }, SelectedIndex = current is JsonObject o ? o.ContainsKey("expression") ? 2 : 1 : 0, Tag = "binding:mode" };
        panel.Children.Add(mode);
        var manual = new TextBox { Text = ScalarEditorText(current is JsonObject dynamic ? dynamic["fallback"] ?? defaultValue : current ?? defaultValue), Tag = "binding:constant", Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(manual);
        int selected = choices.FindIndex(c => current is JsonObject binding && BindingIdentity(c.Binding) == BindingIdentity(binding));
        var combo = new ComboBox { ItemsSource = choices.Select(c => c.Label).ToArray(), SelectedIndex = selected >= 0 ? selected : choices.Count > 0 ? 0 : -1, Margin = new Thickness(0, 8, 0, 12), Tag = "binding:source" };
        panel.Children.Add(combo);
        var expression = new TextBox { Text = (current as JsonObject)?["expression"]?.GetValue<string>() ?? "x + global.OffsetX", Margin = new Thickness(0, 8, 0, 8), Tag = "binding:expression" };
        var expressionPanel = new StackPanel(); expressionPanel.Children.Add(Note("表达式：如 x + global.OffsetX、local.OffsetY * 2、x > 100 ? x : 100。函数用名称调用，如 Offset(x, 10)。")); expressionPanel.Children.Add(expression);
        expressionPanel.Children.Add(Note("结果别名（如 x）在下方选来源；可使用 global.变量名 / local.变量名、pi，以及 abs / min / max / sqrt / sin / cos / clamp 等函数。"));
        var argumentRows = new StackPanel(); expressionPanel.Children.Add(argumentRows);
        var arguments = new List<(TextBox Name, ComboBox Source)>();
        var argumentChoices = Graph.SelectedId is string selectedNode
            ? new[] { "number", "bool", "string" }.SelectMany(target => ParameterBindingChoices(selectedNode, null, target)).DistinctBy(c => BindingIdentity(c.Binding)).ToList()
            : choices;
        void AddArgument(string name, JsonObject? value)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var alias = new TextBox { Text = name, ToolTip = "表达式中的别名" };
            int index = argumentChoices.FindIndex(c => value is not null && BindingIdentity(c.Binding) == BindingIdentity(value));
            var source = new ComboBox { ItemsSource = argumentChoices.Select(c => c.Label).ToArray(), SelectedIndex = index >= 0 ? index : argumentChoices.Count > 0 ? 0 : -1, Margin = new Thickness(6, 0, 6, 0) };
            var remove = new Button { Content = "×", MinWidth = 26 }; var entry = (alias, source);
            remove.Click += (_, _) => { arguments.Remove(entry); argumentRows.Children.Remove(row); };
            row.Children.Add(alias); Grid.SetColumn(source, 1); row.Children.Add(source); Grid.SetColumn(remove, 2); row.Children.Add(remove); argumentRows.Children.Add(row); arguments.Add(entry);
        }
        if (current is JsonObject expr && expr["arguments"] is JsonObject storedArguments)
            foreach (var argument in storedArguments) AddArgument(argument.Key, argument.Value as JsonObject);
        var add = new Button { Content = "＋ 添加结果别名", HorizontalAlignment = HorizontalAlignment.Left }; add.Click += (_, _) => AddArgument("x" + (arguments.Count + 1), null); expressionPanel.Children.Add(add);
        panel.Children.Add(expressionPanel);
        var state = Note("整数可作为数值来源；计算结果必须符合目标参数的类型与范围。"); panel.Children.Add(state);
        var apply = new Button { Content = "应用", Tag = "binding:apply", HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(apply);
        var dialog = new Window { Owner = this, Title = title, Content = new ScrollViewer { Content = panel }, Width = 720, MaxHeight = 800, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        void ModeChanged() { manual.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; combo.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; expressionPanel.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
        mode.SelectionChanged += (_, _) => ModeChanged(); ModeChanged();
        apply.Click += (_, _) =>
        {
            try
            {
                JsonNode? value;
                var fallback = ParseScalarEditor(type, manual.Text);
                if (mode.SelectedIndex == 0) value = fallback;
                else if (mode.SelectedIndex == 1)
                {
                    if (combo.SelectedIndex < 0) throw new ArgumentException("请选择一个可订阅来源。");
                    var binding = choices[combo.SelectedIndex].Binding.DeepClone().AsObject(); binding["fallback"] = fallback?.DeepClone(); value = binding;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(expression.Text)) throw new ArgumentException("请输入表达式。");
                    var args = new JsonObject();
                    foreach (var argument in arguments)
                    {
                        string name = argument.Name.Text.Trim();
                        if (!ValidIdentifier(name) || args.ContainsKey(name)) throw new ArgumentException("别名需唯一，且只能使用字母、数字、下划线，不能以数字开头。");
                        if (argument.Source.SelectedIndex < 0) throw new ArgumentException("请为别名选择来源。");
                        args[name] = argumentChoices[argument.Source.SelectedIndex].Binding.DeepClone();
                    }
                    value = new JsonObject { ["expression"] = expression.Text.Trim(), ["arguments"] = args, ["fallback"] = fallback?.DeepClone() };
                }
                applyValue(value); dialog.Close();
            }
            catch (Exception ex) { state.Text = ex.Message; }
        };
        dialog.ShowDialog();
    }
    private static string BindingIdentity(JsonObject value) => value.ContainsKey("node") ? $"n:{value["node"]}:{value["port"]}:{value["field"]}" : value.ContainsKey("input") ? "i:" + value["input"] : $"v:{value["scope"]}:{value["variable"]}";
    private async void RunSelectedClick(object sender, RoutedEventArgs e)
    {
        string? target = Graph.SelectedId;
        await BusyAsync(() => target is null ? Task.FromException(new InvalidOperationException("请先选择目标算子。")) : RunOnceAsync(target));
    }
    private void DuplicateClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        EnsureEditor(); if (Graph.SelectedIds.Count == 0) return;
        var copied = flow!.DuplicateNodes(Graph.SelectedIds); Graph.Select(copied.Last());
        Log($"已复制 {copied.Count} 个算子，内部连线和字段绑定已重映射。Shift＋单击可多选。");
    });
}

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private static bool ValidIdentifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");
    private static string ScalarEditorText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "0";
    private static JsonNode ParseScalarEditor(string type, string text)
    {
        if (type is "string" or "enum" or "String") return JsonValue.Create(text)!;
        if (type is "bool" or "Bool")
        {
            if (!bool.TryParse(text, out bool value)) throw new ArgumentException("布尔值请输入 true 或 false。");
            return JsonValue.Create(value)!;
        }
        if (type is "integer" or "Int")
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) throw new ArgumentException("请输入有效整数。");
            return JsonValue.Create(value)!;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new ArgumentException("请输入有限数值。");
        return JsonValue.Create(number)!;
    }

    private void VariablesClick(object sender, RoutedEventArgs e) => Safe(ShowVariablesDialog);
    private void ShowVariablesDialog()
    {
        EnsureEditor();
        if (flow is null) return;
        string? selected = Graph.SelectedId;
        var tabs = new TabControl { Tag = "variables:tabs" };
        var state = Note("全局变量保存于当前配方；局部变量仅供所选算子使用，每次运行重新计算。函数只进行参数计算，不执行脚本或访问文件。");
        var globals = (flow.Definition["variables"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        var locals = selected is null ? new JsonObject() : (flow.Node(selected)["variables"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        var functions = (flow.Definition["functions"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();
        var readers = new List<Action>();
        AddVariablesTab("全局变量（当前配方）", globals, false);
        AddVariablesTab(selected is null ? "局部变量（请先选择算子）" : "局部变量 · " + flow.Spec(selected).Label + " [" + selected + "]", locals, true);
        AddFunctionsTab();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "保存变量和函数", Tag = "variables:save" }; buttons.Children.Add(apply);
        var root = new DockPanel { Margin = new Thickness(16) }; DockPanel.SetDock(state, Dock.Top); root.Children.Add(state); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(tabs);
        var dialog = new Window { Owner = this, Title = "变量与计算函数", Width = 940, Height = 650, MinWidth = 760, MinHeight = 480, Content = root, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        apply.Click += (_, _) =>
        {
            try
            {
                foreach (var read in readers) read();
                flow.SetVariableDefinitions(globals, functions, selected, selected is null ? null : locals);
                Log("已保存配方全局变量、算子局部变量和计算函数；执行时校验引用及计算结果。");
                dialog.Close();
            }
            catch (Exception ex) { state.Text = ex.Message; }
        };
        dialog.ShowDialog();

        void AddVariablesTab(string title, JsonObject values, bool local)
        {
            var rows = new StackPanel(); var body = new DockPanel { Margin = new Thickness(10) };
            var add = new Button { Content = "＋ 添加变量", Tag = local ? "variables:add-local" : "variables:add-global", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !local || selected is not null };
            DockPanel.SetDock(add, Dock.Bottom); body.Children.Add(add); body.Children.Add(new ScrollViewer { Content = rows });
            tabs.Items.Add(new TabItem { Header = title, Content = body });
            var entries = new List<(TextBox Name, ComboBox Type, TextBox Literal, TextBlock Source, Func<JsonNode?> Value)>();
            void Add(string name, JsonObject definition)
            {
                JsonNode? dynamicValue = definition["value"]?.DeepClone();
                var row = new Grid { Margin = new Thickness(0, 5, 0, 8) };
                foreach (double width in new[] { 150.0, 105, double.NaN, 96, 35 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
                row.RowDefinitions.Add(new RowDefinition()); row.RowDefinitions.Add(new RowDefinition());
                var nameEdit = new TextBox { Text = name, ToolTip = "变量名：字母、数字和下划线", Tag = local ? "variable:local:name" : "variable:global:name" };
                string[] types = { "Double", "Int", "Bool", "String" };
                var type = new ComboBox { ItemsSource = types, SelectedIndex = Math.Max(0, Array.IndexOf(types, definition["type"]?.GetValue<string>())), Margin = new Thickness(6, 0, 6, 0), Tag = local ? "variable:local:type" : "variable:global:type" };
                var literal = new TextBox { Text = ScalarEditorText(IsDynamicParameter(dynamicValue) ? dynamicValue!["fallback"] : dynamicValue), IsReadOnly = IsDynamicParameter(dynamicValue), Tag = local ? "variable:local:value" : "variable:global:value" };
                var source = Note(IsDynamicParameter(dynamicValue) ? SubscriptionLabel(dynamicValue!) : "常量"); source.Margin = new Thickness(0, 3, 0, 0);
                var bind = new Button { Content = "订阅 / 计算", Visibility = local ? Visibility.Visible : Visibility.Collapsed, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(5, 0, 5, 0) };
                var remove = new Button { Content = "×", MinWidth = 28 };
                (TextBox Name, ComboBox Type, TextBox Literal, TextBlock Source, Func<JsonNode?> Value) entry = (nameEdit, type, literal, source, () => IsDynamicParameter(dynamicValue) ? dynamicValue?.DeepClone() : ParseScalarEditor(types[type.SelectedIndex], literal.Text));
                bind.Click += (_, _) =>
                {
                    if (selected is null) return;
                    string targetType = types[type.SelectedIndex];
                    ShowValueBindingEditor("局部变量 · " + nameEdit.Text, targetType, IsDynamicParameter(dynamicValue) ? dynamicValue : ParseScalarEditor(targetType, literal.Text), ParameterBindingChoices(selected, null, targetType), value =>
                    {
                        dynamicValue = value?.DeepClone(); literal.IsReadOnly = IsDynamicParameter(dynamicValue);
                        literal.Text = ScalarEditorText(IsDynamicParameter(dynamicValue) ? dynamicValue!["fallback"] : dynamicValue);
                        source.Text = IsDynamicParameter(dynamicValue) ? SubscriptionLabel(dynamicValue!) : "常量";
                    }, JsonValue.Create(targetType == "Bool" ? "false" : "0"));
                };
                remove.Click += (_, _) => { rows.Children.Remove(row); entries.Remove(entry); };
                row.Children.Add(nameEdit); Grid.SetColumn(type, 1); row.Children.Add(type); Grid.SetColumn(literal, 2); row.Children.Add(literal); Grid.SetColumn(bind, 3); row.Children.Add(bind); Grid.SetColumn(remove, 4); row.Children.Add(remove); Grid.SetRow(source, 1); Grid.SetColumn(source, 2); Grid.SetColumnSpan(source, 2); row.Children.Add(source); rows.Children.Add(row); entries.Add(entry);
            }
            foreach (var item in values) if (item.Value is JsonObject definition) Add(item.Key, definition);
            add.Click += (_, _) => Add("", new JsonObject { ["type"] = "Double", ["value"] = 0.0 });
            readers.Add(() =>
            {
                var replacement = new JsonObject();
                foreach (var entry in entries)
                {
                    string name = entry.Name.Text.Trim();
                    if (!ValidIdentifier(name) || name is "pi" or "true" or "false" or "global" or "local" || replacement.ContainsKey(name)) throw new ArgumentException("变量名无效或重复：" + name);
                    replacement[name] = new JsonObject { ["type"] = entry.Type.SelectedItem!.ToString(), ["value"] = entry.Value() };
                }
                values.Clear(); foreach (var item in replacement) values[item.Key] = item.Value?.DeepClone();
            });
        }
        void AddFunctionsTab()
        {
            var rows = new StackPanel(); var body = new DockPanel { Margin = new Thickness(10) };
            var hint = Note("函数名示例 Offset；参数填 x, dx；表达式填 x + dx。参数、表达式中的小数均使用英文标点。支持条件表达式 x > 0 ? x : 0。"); DockPanel.SetDock(hint, Dock.Top); body.Children.Add(hint);
            var add = new Button { Content = "＋ 添加计算函数", Tag = "variables:add-function", HorizontalAlignment = HorizontalAlignment.Left }; DockPanel.SetDock(add, Dock.Bottom); body.Children.Add(add); body.Children.Add(new ScrollViewer { Content = rows }); tabs.Items.Add(new TabItem { Header = "计算函数", Content = body });
            var entries = new List<(TextBox Name, TextBox Arguments, TextBox Expression)>();
            void Add(string name, JsonObject definition)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 8) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });
                var nameEdit = new TextBox { Text = name, ToolTip = "函数名", Tag = "function:name" }; var args = new TextBox { Text = definition["parameters"] is JsonArray array ? string.Join(", ", array.Select(p => p!.GetValue<string>())) : "", ToolTip = "参数名称，用英文逗号分隔", Margin = new Thickness(6, 0, 6, 0), Tag = "function:parameters" }; var expr = new TextBox { Text = definition["expression"]?.GetValue<string>() ?? "", ToolTip = "计算表达式", Tag = "function:expression" }; var remove = new Button { Content = "×", Margin = new Thickness(5, 0, 0, 0) };
                var entry = (nameEdit, args, expr); remove.Click += (_, _) => { entries.Remove(entry); rows.Children.Remove(row); }; row.Children.Add(nameEdit); Grid.SetColumn(args, 1); row.Children.Add(args); Grid.SetColumn(expr, 2); row.Children.Add(expr); Grid.SetColumn(remove, 3); row.Children.Add(remove); rows.Children.Add(row); entries.Add(entry);
            }
            foreach (var function in functions) if (function.Value is JsonObject definition) Add(function.Key, definition);
            add.Click += (_, _) => Add("", new JsonObject { ["parameters"] = new JsonArray("x", "dx"), ["expression"] = "x + dx" });
            readers.Add(() =>
            {
                var replacement = new JsonObject();
                foreach (var entry in entries)
                {
                    string name = entry.Name.Text.Trim(); string[] args = entry.Arguments.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (!ValidIdentifier(name) || replacement.ContainsKey(name) || args.Any(p => !ValidIdentifier(p)) || args.Distinct().Count() != args.Length || string.IsNullOrWhiteSpace(entry.Expression.Text)) throw new ArgumentException("函数名、参数或表达式无效：" + name);
                    replacement[name] = new JsonObject { ["parameters"] = new JsonArray(args.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()), ["expression"] = entry.Expression.Text.Trim() };
                }
                functions.Clear(); foreach (var item in replacement) functions[item.Key] = item.Value?.DeepClone();
            });
        }
    }
}

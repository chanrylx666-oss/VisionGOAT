using JPVision.Editor.Core;
using JPVision.Sdk;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JPVision.Studio;

public partial class MainWindow
{
    private Window Dialog(string title, UIElement content, double width = 520, double height = 430) => new() { Title = title, Owner = this, Width = width, Height = height, MinWidth = 400, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content, Icon = Icon };
    private async void LicenseClick(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Header(panel, "JP-Vision 离线授权");
        var status = Note("正在检查…");
        panel.Children.Add(status);
        panel.Children.Add(Note("只有开发者签发的、本机绑定的永久许可证才能解锁编辑器与视觉运行库。无需登录。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var export = new Button { Content = "导出机器请求" };
        var import = new Button { Content = "导入许可证", Style = (Style)FindResource("PrimaryButton") };
        row.Children.Add(export);
        row.Children.Add(import);
        panel.Children.Add(row);
        var window = Dialog("JP-Vision 授权", panel, 560, 330);
        async Task Refresh()
        {
            try
            {
                var info = await Task.Run(() => VisionLicensing.GetEditorStatus(licenseStore));
                var runtime = await Task.Run(() => VisionLicensing.GetStatus(licenseStore));
                status.Text = info.IsValid && runtime.IsValid ? $"已授权 · {info.Customer}\n永久授权 · 本机绑定 · 编辑器和运行库均可用" : $"尚未激活 · editor: {info.Status} · runtime: {runtime.Status}";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        export.Click += async (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "机器请求|*.vreq", FileName = "JP-Vision-request.vreq" };
            if (dialog.ShowDialog(window) != true)
                return;
            try
            {
                string request = await Task.Run(VisionLicensing.GetMachineRequest);
                WriteAtomic(dialog.FileName, request);
                status.Text = "机器请求已导出，请交给开发者签发许可证。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "JP-Vision 许可证|*.vlic" };
            if (dialog.ShowDialog(window) != true)
                return;
            import.IsEnabled = export.IsEnabled = false;
            try
            {
                await Task.Run(() => VisionLicensing.Import(dialog.FileName, licenseStore));
                await Refresh();
                await RefreshLicenseAsync();
            }
            catch (Exception ex) { status.Text = "激活失败：" + ex.Message; }
            finally { import.IsEnabled = export.IsEnabled = true; }
        };
        window.Loaded += async (_, _) => await Refresh();
        window.ShowDialog();
        if (licensed && engine is null)
            await InitializeAsync();
    }
    private void InterfacesClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        EnsureEditor();
        if (flow is null || flow.Nodes.Count == 0)
            throw new InvalidOperationException("请先添加算子。");
        var panel = new StackPanel { Margin = new Thickness(20) };
        Header(panel, "WPF / SDK 公开输出与判定");
        panel.Children.Add(Note("选择“全部结果”可供 C# 工程一次读取全部数据，也可以单独公开其中的字段。Bool 或三态 Decision 字段可以作为检测判定。"));
        var list = new ListBox { Height = 110 };
        panel.Children.Add(list);
        var candidates = flow.Nodes.SelectMany(n => { string id = FlowDocument.Id(n!); return flow.Spec(id).Outputs.OrderBy(p => p.Hidden).Select(p => (Id: id, Port: p.Id, Type: p.Type, Label: $"{flow.Spec(id).Label} [{id}] / {p.Label}{(p.Hidden ? "（结果字段）" : "")} · {p.Type}")); }).ToArray();
        var combo = new ComboBox { ItemsSource = candidates.Select(p => p.Label).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(combo);
        var name = new TextBox { Text = "Result", Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(name);
        var decision = new CheckBox { Content = "作为检测判定（Bool／三态 Decision）", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(decision);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var publish = new Button { Content = "添加／更新输出", Style = (Style)FindResource("PrimaryButton") };
        var remove = new Button { Content = "删除选中输出" };
        var processing = new Button { Content = "改为处理流程" };
        row.Children.Add(publish);
        row.Children.Add(remove);
        row.Children.Add(processing);
        panel.Children.Add(row);
        var state = Note("");
        panel.Children.Add(state);
        void Refresh()
        {
            list.ItemsSource = flow.Definition["outputs"]!.AsObject().Select(p => $"{p.Key} → {p.Value!["node"]} / {p.Value["port"]} · {p.Value["type"]}").ToArray();
            state.Text = flow.PublicInterfaceIssue();
        }
        publish.Click += (_, _) => { try { var binding = candidates[combo.SelectedIndex]; flow.Publish(name.Text.Trim(), binding.Id, binding.Port, decision.IsChecked == true); Refresh(); } catch (Exception ex) { state.Text = ex.Message; } };
        remove.Click += (_, _) => { if (list.SelectedIndex < 0) return; string key = flow.Definition["outputs"]!.AsObject().ElementAt(list.SelectedIndex).Key; flow.Change(() => { flow.Definition["outputs"]!.AsObject().Remove(key); if (flow.Definition["decisionOutput"]!.GetValue<string>() == key) { flow.Definition["kind"] = "processing"; flow.Definition["decisionOutput"] = ""; } }); Refresh(); };
        processing.Click += (_, _) => { flow.Change(() => { flow.Definition["kind"] = "processing"; flow.Definition["decisionOutput"] = ""; }); Refresh(); };
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex < 0) return; var binding = flow.Definition["outputs"]!.AsObject().ElementAt(list.SelectedIndex); name.Text = binding.Key; combo.SelectedIndex = Array.FindIndex(candidates, c => c.Id == binding.Value!["node"]!.GetValue<string>() && c.Port == binding.Value["port"]!.GetValue<string>()); decision.IsChecked = flow.Definition["decisionOutput"]!.GetValue<string>() == binding.Key; };
        Refresh();
        Dialog("公开输出接口", panel, 710, 500).ShowDialog();
    });
}

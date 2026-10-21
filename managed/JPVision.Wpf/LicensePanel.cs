using JPVision.Sdk;
using Microsoft.Win32;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Wpf;

public sealed class LicensePanel : StackPanel
{
    private readonly TextBlock status = new() { Text = "正在检查授权…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button export = new() { Content = "导出机器请求", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5) };
    private readonly Button import = new() { Content = "导入许可证", Padding = new Thickness(10, 5, 10, 5) };
    public string? LicenseStore
    {
        get; set;
    }
    public event Action<LicenseInfo>? StatusChanged;
    public LicensePanel()
    {
        Children.Add(status);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(export);
        row.Children.Add(import);
        Children.Add(row);
        Loaded += async (_, _) => await RefreshAsync();
        export.Click += async (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "JP-Vision 机器请求|*.vreq", FileName = "JP-Vision-request.vreq" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            try
            {
                string request = await Task.Run(VisionLicensing.GetMachineRequest);
                await System.IO.File.WriteAllTextAsync(dialog.FileName, request, new UTF8Encoding(false));
                status.Text = "机器请求已导出，请交给开发者签发许可证。";
            }
            catch (Exception e) { status.Text = e.Message; }
        };
        import.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "JP-Vision 许可证|*.vlic" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            try
            {
                export.IsEnabled = import.IsEnabled = false;
                await Task.Run(() => VisionLicensing.Import(dialog.FileName, LicenseStore));
                await RefreshAsync();
            }
            catch (Exception e) { status.Text = "激活失败：" + e.Message; }
            finally { export.IsEnabled = import.IsEnabled = true; }
        };
    }
    public async Task RefreshAsync()
    {
        try
        {
            var info = await Task.Run(() => VisionLicensing.GetStatus(LicenseStore));
            status.Text = info.IsValid ? $"已授权 · {info.Customer}\n永久授权 · 本机有效" : $"未授权 · {info.Status}\n请导出机器请求并导入签发的许可证。";
            StatusChanged?.Invoke(info);
        }
        catch (Exception e) { status.Text = "运行库不可用：" + e.Message; }
    }
}

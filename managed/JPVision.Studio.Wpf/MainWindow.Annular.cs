using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JPVision.Studio;

public partial class MainWindow
{
    private bool IsAnnularUnwrap(string id) => flow!.Spec(id).Type == "AnnularUnwrap";

    private void BuildAnnularRoiPanel(string id)
    {
        Header(Parameters, "展开圆环 / 扇环");
        var region = CurrentInputRoi(id);
        Parameters.Children.Add(Note("在输入图上绘制圆环，再拖动圆心、内外半径或角度手柄。这里只展开所选环带，不使用全图替代圆环。"));
        Parameters.Children.Add(new ComboBox { ItemsSource = new[] { "圆环／扇环" }, SelectedIndex = 0, IsEnabled = false, Margin = new Thickness(0, 0, 0, 7), Tag = "annular:shape" });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var draw = new Button { Content = "绘制 / 重画圆环", Tag = "annular:draw", Padding = new Thickness(8, 6, 8, 6), IsEnabled = !RoiHasSubscriptions(id) };
        var adjust = new Button { Content = "调整圆环", Tag = "annular:adjust", Padding = new Thickness(8, 6, 8, 6), IsEnabled = region.Shape == "annulus" && !RoiHasSubscriptions(id) };
        draw.Click += async (_, _) => await DrawAnnularRoiAsync(id);
        adjust.Click += async (_, _) => await EditRoiAsync();
        row.Children.Add(draw); row.Children.Add(adjust); Parameters.Children.Add(row);
        if (region.Shape != "annulus")
        {
            Parameters.Children.Add(Note("请绘制有效圆环：外半径必须大于内半径，角度跨度为 10～360°。", false));
            return;
        }
        BuildRoiCoordinateEditors(id, "roi", region, "annular:");
        Parameters.Children.Add(Note("0° 指向右侧，90° 指向下方；跨越 0° 时终止角可大于 360°。顺时针按起始角→终止角展开，逆时针按终止角→起始角展开同一扇环。"));
        Parameters.Children.Add(Note("输出横轴对应角度、纵轴对应半径。宽高设为 0 时自动计算；完整圆周不重复首尾接缝列。展开图使用独立像素坐标，不能直接把展开长度当作实物毫米。", false));

    }

    private async Task DrawAnnularRoiAsync(string id)
    {
        await DrawRegionAsync(id, "annulus", false);
        InspectorTabs.SelectedIndex = 0;
        Log("从圆心拖到外半径创建圆环，随后拖动内圆和角度手柄调整；Esc 取消。");
    }

    private static bool IsUnwrappedCoordinates(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("coordinateSpace", out var space) && space.GetString() == "annularUnwrapped";
    private static string ResultCoordinates(JsonElement value) => IsUnwrappedCoordinates(value) ? "展开图像素" : "原图像素";
    private void ApplyImageCoordinateLabel(JsonElement metadata)
    {
        if (IsUnwrappedCoordinates(metadata)) ImageInfo.Text += "  │  展开图坐标（X：角度，Y：半径）";
    }
}

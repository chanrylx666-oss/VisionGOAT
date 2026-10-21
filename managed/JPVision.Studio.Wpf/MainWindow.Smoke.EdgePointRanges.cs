using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeEdgePointRangesAsync()
    {
        const int width = 4096, height = 2400;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            byte value = (byte)Math.Clamp(125 + (y - 1200) * 180, 20, 230);
            Array.Fill(pixels, value, y * width, width);
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 800, -20); ShowSource();
        flow!.Add("ExternalImage", 40, 120, "source"); flow.Add("FindEdgePoints", 320, 120, "edge");
        flow.Connect("source", "image", "edge", "input");
        flow.Publish("EdgePoints", "edge", "result", false); flow.Publish("EdgeImage", "edge", "image", false);
        Graph.Select("edge");
        ParameterControl<Button>("edge-points:draw").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
        Require(!busy && ImageView.EditEnabled, "expanded edge-point range drawing did not prepare its input");
        ImageView.CommitRegion(RoiRegion.RotatedRectangle(2048, 1200, 4096, 60, 0), false); ImageView.EndEdit();
        var choices = ParameterControl<ComboBox>("projectionWidth").Items.Cast<string>().ToArray();
        Require(choices.Length == 128 && choices[0] == "1 px" && choices[^1] == "255 px" && choices.Contains("65 px"), "projection-width choices did not use the edge-point descriptor limits");
        ParameterNumber("sampleSpacing", "512"); ParameterChoice("projectionWidth", "65 px");
        ParameterNumber("coordinate:roi.height", "2304"); ParameterNumber("maximumPoints", "32769");
        ParameterChoice("polarity", "暗 → 亮");
        Require(RoiRegion.FromJson(flow.Node("edge")["parameters"]!["roi"]).Height == 2304, "numeric ROI height remained limited to 2048");
        var plan = CaliperLayout.DescribeEdgePoints(CurrentInputRoi("edge"), EffectiveParameters("edge"));
        Require(plan.CanExecute && plan.StationCount == 9 && plan.ProfileLength == 2305 && plan.ProjectedSamples == 9L * 2305 * 65, "expanded range sampling plan differs from runtime geometry");
        ParameterNumber("maximumPoints", "2");
        Require(ParameterControl<TextBlock>("edge-points:sampling-summary").Text.Contains("9 个采样站", StringComparison.Ordinal) &&
            ParameterControl<TextBlock>("edge-points:sampling-state").Text.Contains("超过当前上限 2", StringComparison.Ordinal), "station limit did not show the real requested count");
        ParameterNumber("maximumPoints", "32769"); ParameterNumber("sampleSpacing", "1");
        Require(ParameterControl<TextBlock>("edge-points:sampling-summary").Text.Contains("4097 个采样站", StringComparison.Ordinal) &&
            ParameterControl<TextBlock>("edge-points:sampling-state").Text.Contains("搜索量过大", StringComparison.Ordinal), "sampling budget displayed zero stations or omitted an actionable reason");
        ParameterNumber("sampleSpacing", "512");
        Require(ParameterControl<TextBlock>("edge-points:sampling-state").Text.Contains("可执行", StringComparison.Ordinal), "valid large sampling settings remained blocked");
        await BusyAsync(RunOnceAsync); VerifyEdgePointRanges();
        InspectorTabs.SelectedIndex = 0;
        Log("已验证边缘点集扩大范围：间距 512 px、切向投影宽 65 px、搜索高度 2304 px；数量及搜索量超限显示真实测站数。");
    }

    private void VerifyEdgePointRanges()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "expanded edge-point range flow did not execute");
        var points = result!.GetEdgePoints("EdgePoints");
        Require(points.Valid && points.StationCount == 9 && points.Count >= 7 && points.Points.All(p => p.X >= 800 && p.X <= 4896 && Math.Abs(p.Y - 1180) < .5), "expanded search height, spacing or width did not yield real source-coordinate points");
        var parameters = flow!.Node("edge")["parameters"]!;
        Require(double.Parse(parameters["sampleSpacing"]!.ToJsonString(), CultureInfo.InvariantCulture) == 512 && parameters["projectionWidth"]!.GetValue<int>() == 65 &&
            RoiRegion.FromJson(parameters["roi"]).Height == 2304 && parameters["maximumPoints"]!.GetValue<long>() == 32769, "expanded sampling values did not roundtrip");
        using var image = result.GetOutputImage("EdgeImage");
        Require(image.Info.Width == 4096 && image.Info.Height == 2400 && image.CopyPixels().SequenceEqual(source!.CopyPixels()), "edge annotations changed the clean input pixels");
        Require(Results.Children.OfType<DataGrid>().Single(g => Equals(g.Tag, "edge-points:table")).Items.Count == points.Count, "large sampling results are missing from the point table");
    }
}

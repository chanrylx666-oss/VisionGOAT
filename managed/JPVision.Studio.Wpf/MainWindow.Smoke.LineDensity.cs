using JPVision.Editor.Core;
using JPVision.Sdk;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeLineDensityAsync()
    {
        const int width = 10918, height = 287;
        byte[] Pixels(bool stepped)
        {
            var values = new byte[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                double edge = 143 + (stepped ? x < width / 2 ? -6 : 6 : 0);
                values[y * width + x] = (byte)Math.Clamp(125 + (y - edge) * 180, 20, 230);
            }
            return values;
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(Pixels(false), width, height, width, VisionPixelFormat.Gray8, 350, -20); ShowSource();
        flow!.Add("ExternalImage", 35, 120, "source"); flow.Add("FindLines", 335, 120, "line");
        flow.Connect("source", "image", "line", "input");
        flow.Publish("Line", "line", "result", false); flow.Publish("LineImage", "line", "image", false);
        Graph.Select("line"); await DrawLineCaliperAsync("line");
        ImageView.CommitRegion(RoiRegion.RotatedRectangle(width / 2.0, 143, width, 60, 0), false); ImageView.EndEdit();
        ParameterChoice("polarity", "暗 → 亮"); ParameterNumber("caliperCount", "32768"); ParameterChoice("projectionWidth", "63 px");
        Require(ParameterControl<TextBlock>("line:sampling-summary").Text.Contains("32768 把", StringComparison.Ordinal) &&
            ParameterControl<TextBlock>("line:sampling-state").Text.Contains("组合搜索量过大", StringComparison.Ordinal), "large line caliper settings omitted their real count or sampling budget feedback");
        ParameterNumber("caliperCount", "1024"); ParameterChoice("projectionWidth", "5 px"); ParameterNumber("removePointCount", "600");
        Require(ParameterControl<TextBlock>("line:sampling-state").Text.Contains("剔除 600 点", StringComparison.Ordinal) &&
            ParameterControl<TextBlock>("line:sampling-state").Text.Contains("512", StringComparison.Ordinal), "removal/coverage conflict was not explained before running");
        ParameterNumber("removePointCount", "0");
        Require(ParameterControl<TextBlock>("line:sampling-summary").Text.Contains("10.662", StringComparison.Ordinal) &&
            ParameterControl<TextBlock>("line:sampling-state").Text.Contains("可执行", StringComparison.Ordinal), "long-strip sampling spacing or executable state is incorrect");
        await EditRoiAsync();
        var windows = CaliperLayout.Create(CurrentInputRoi("line"), EffectiveParameters("line"), false, width, height, true);
        Require(windows.Count == 1024 && CaliperLayout.PreviewWindowIndices(windows.Count).Count == 256, "drawing preview truncated the real line sampling layout");
        ImageView.InvalidateVisual();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Require(ImageView.DrawnCaliperPreviewCount == 256, "dense line preview drew more than 256 calipers or none");
        ImageView.EndEdit(); await BusyAsync(RunOnceAsync); VerifyLineDensity();
        var saved = source; source = VisionFrame.CopyFrom(Pixels(true), width, height, width, VisionPixelFormat.Gray8, 350, -20); ShowSource();
        try
        {
            ParameterNumber("maximumDeviation", "0.1"); ParameterNumber("minimumCoverage", "0.8");
            await BusyAsync(RunOnceAsync);
            var packet = result!.GetResult<VisionLine>("Line"); var diagnostics = result.Data.GetProperty("measurements").GetProperty("line").GetProperty("caliper");
            Require(packet.Count == 0 && diagnostics.GetProperty("sampledCount").GetInt32() == 1024 && !diagnostics.GetProperty("fitSucceeded").GetBoolean(), "a stepped edge was treated as a successful line just because it had sampled points");
            Require(Results.Children.OfType<TextBlock>().Any(t => Equals(t.Tag, "line:fit-failure") && t.Text.Contains("保留点不足", StringComparison.Ordinal)) &&
                Results.Children.OfType<TextBlock>().Any(t => t.Text.Contains("不是屏幕像素", StringComparison.Ordinal)), "line fit failure omitted its cause or actual input-pixel tolerance");
            InspectorTabs.SelectedIndex = 1;
            if (InspectorTabs.Items[1] is TabItem tab && tab.Content is ScrollViewer scroll) scroll.ScrollToTop();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            CaptureWindow("line-density-failure");
            string failureCapture = Path.ChangeExtension(CapturePath, ".failure.png");
            File.Copy(CapturePath, failureCapture, true); File.Copy(CapturePath + ".json", failureCapture + ".json", true);
        }
        finally { source.Dispose(); source = saved; ShowSource(); }
        ParameterNumber("maximumDeviation", "2"); ParameterNumber("minimumCoverage", "0.5");
        await BusyAsync(RunOnceAsync); VerifyLineDensity();
        InspectorTabs.SelectedIndex = 0;
        Log("已验证 10918 × 287 长条图：1024 把卡尺实际采样和拟合，预览仅显示 256 窗；搜索量、剔点冲突与不直边失败均有说明。");
    }

    private void VerifyLineDensity()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "dense line flow did not execute");
        var packet = result!.GetResult<VisionLine>("Line"); var diagnostics = result.Data.GetProperty("measurements").GetProperty("line").GetProperty("caliper");
        Require(packet.Count == 1 && packet.Features.Single().EdgePoints.Count == 1024 && diagnostics.GetProperty("requestedCount").GetInt32() == 1024 &&
            diagnostics.GetProperty("sampledCount").GetInt32() == 1024 && diagnostics.GetProperty("retainedCount").GetInt32() == 1024 &&
            diagnostics.GetProperty("validSamplingCalipers").GetInt32() == 1024, "dense line sampling silently used fewer than the requested 1024 calipers");
        var line = packet.Features.Single();
        Require(Math.Abs(line.Y1 - 123) < .2 && Math.Abs(line.Y2 - 123) < .2 && line.RmsError < .1 && line.Equation is not null, "dense line fit is not in actual input-image coordinates");
        Require(Math.Abs(diagnostics.GetProperty("effectiveSamplingSpacing").GetDouble() - 10918.0 / 1024) < 1e-9 &&
            diagnostics.GetProperty("initialMaximumDeviation").GetDouble() < .1 && diagnostics.GetProperty("maximumRetainedDeviation").GetDouble() < .1,
            "line residual or actual sampling-spacing diagnostics are missing");
        Require(Results.Children.OfType<TextBlock>().Single(t => Equals(t.Tag, "line:fit-counts")).Text.Contains("有效采样 1024", StringComparison.Ordinal), "line results do not show effective sampling count");
        Require(Results.Children.OfType<TextBlock>().Any(t => Equals(t.Tag, "line:fit:initialMaximumDeviation")) &&
            Results.Children.OfType<TextBlock>().Any(t => Equals(t.Tag, "line:fit:maximumRetainedDeviation")), "initial/final geometric deviation is hidden");
        using var image = result.GetOutputImage("LineImage");
        Require(image.Info.Width == 10918 && image.Info.Height == 287 && image.CopyPixels().SequenceEqual(source!.CopyPixels()), "fitted line changed clean image pixels");
    }
}

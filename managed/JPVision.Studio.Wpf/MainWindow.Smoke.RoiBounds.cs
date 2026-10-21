using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeRoiBoundsAsync()
    {
        const int size = 640, width = 10918, height = 287;
        var pixels = new byte[size * size * 3];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double radius = Math.Sqrt((x - 320.0) * (x - 320.0) + (y - 320.0) * (y - 320.0));
            byte value = (byte)Math.Clamp(125 + (radius - 160) * 180, 20, 230);
            int at = (y * size + x) * 3;
            pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, size, size, size * 3, VisionPixelFormat.Bgr24, 700, -90); ShowSource();
        flow!.Add("ExternalImage", 20, 135, "source"); flow.Add("AnnularUnwrap", 250, 135, "unwrap");
        flow.Add("FindLines", 495, 25, "line"); flow.Add("FindEdgePoints", 495, 190, "edge"); flow.Add("ToGray", 735, 135, "crop");
        flow.Connect("source", "image", "unwrap", "input");
        foreach (string id in new[] { "line", "edge", "crop" }) flow.Connect("unwrap", "image", id, "input");
        flow.SetParameter("unwrap", "roi", RoiRegion.Annulus(320, 320, 100, 220).ToJson());
        flow.SetParameter("unwrap", "outputWidth", JsonValue.Create(width)); flow.SetParameter("unwrap", "outputHeight", JsonValue.Create(height));
        flow.SetParameter("line", "caliperCount", JsonValue.Create(64)); flow.SetParameter("line", "polarity", JsonValue.Create("darkToLight"));
        flow.SetParameter("line", "minimumCoverage", JsonValue.Create(.8));
        flow.SetParameter("edge", "sampleSpacing", JsonValue.Create(128)); flow.SetParameter("edge", "polarity", JsonValue.Create("darkToLight"));
        flow.SetParameter("edge", "minimumCoverage", JsonValue.Create(.8));
        var strip = LineCaliperRoi.FromEndpoints(new(0, 140), new(width, 146), 70);
        Require(strip.Bounds.Left < 0 && strip.Bounds.Right > width, "full-width sloped strip did not cover the reported boundary case");
        Graph.Select("line"); await DrawLineCaliperAsync("line");
        Require(ImageView.Info?.Width == width && ImageView.Info.Height == height && ImageInfo.Text.Contains("展开图坐标", StringComparison.Ordinal), "line drawing used the source ring image instead of the current unwrapped input");
        ImageView.CommitRegion(strip, false); ImageView.EndEdit();
        Graph.Select("edge");
        var draw = InspectorControls<Button>().Single(b => Equals(b.Tag, "edge-points:draw"));
        draw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
        Require(!busy && ImageView.Info?.Width == width && ImageView.Info.Height == height, "edge point drawing did not use the long input image");
        ImageView.CommitRegion(strip, false); ImageView.EndEdit();
        var sizeNote = InspectorControls<TextBlock>().Single(t => Equals(t.Tag, "roi:input-size"));
        Require(sizeNote.Text.Contains("10918 × 287", StringComparison.Ordinal), "ROI inspector does not show actual operator input dimensions");
        Require(InspectorControls<TextBlock>().Single(t => Equals(t.Tag, "roi:bounds")).Text.Contains("部分超界", StringComparison.Ordinal), "ROI boundary handling is not explained in the inspector");
        flow.SetParameter("crop", "roi", RoiRegion.Rectangle(width - 50, 0, 200, height).ToJson());
        flow.Publish("Unwrapped", "unwrap", "image", false); flow.Publish("Line", "line", "result", false);
        flow.Publish("LineImage", "line", "image", false); flow.Publish("EdgePoints", "edge", "result", false); flow.Publish("Crop", "crop", "image", false);
        string savedLine = flow.Node("line")["parameters"]!["roi"]!.ToJsonString();
        string savedEdge = flow.Node("edge")["parameters"]!["roi"]!.ToJsonString();
        await BusyAsync(RunOnceAsync); VerifyRoiBounds();
        Require(flow.Node("line")["parameters"]!["roi"]!.ToJsonString() == savedLine && flow.Node("edge")["parameters"]!["roi"]!.ToJsonString() == savedEdge, "execution silently shrank the taught sampling geometry");

        flow.SetParameter("crop", "roi", RoiRegion.Rectangle(width + 10, 0, 200, height).ToJson());
        Graph.Select("crop"); await BusyAsync(RunOnceAsync);
        Require(result!.Status == ExecutionStatus.Succeeded && !result.Outputs.GetProperty("Crop").GetProperty("available").GetBoolean(), "fully outside ROI crashed or fell back to the full image");
        Require(ImageInfo.Text.Contains("ROI 未覆盖", StringComparison.Ordinal) && !Results.Children.OfType<TextBlock>().Any(t => t.Text.Contains("未匹配到", StringComparison.Ordinal)), "outside ROI was incorrectly reported as a template matching failure");
        flow.SetParameter("crop", "roi", RoiRegion.Rectangle(width - 50, 0, 200, height).ToJson());
        Graph.Select("line"); await BusyAsync(RunOnceAsync); VerifyRoiBounds();
        InspectorTabs.SelectedIndex = 0;
        Log("已验证 10918 × 287 展开图：全宽斜向搜索带、真实点集、贴边裁剪及完全越界无效；保留原 ROI 与展开图坐标。");
    }

    private void VerifyRoiBounds()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "large ROI flow did not execute");
        var line = result!.GetResult<VisionLine>("Line");
        Require(line.Count == 1 && line.Features.Single().EdgePoints.Count >= 56, "a full-width sloped ROI did not fit the real edge line");
        Require(Math.Abs(line.Features.Single().Y1 - 143) < 3 && Math.Abs(line.Features.Single().Y2 - 143) < 3, "line coordinates are not in the current unwrapped image");
        var points = result.GetEdgePoints("EdgePoints");
        Require(points.Valid && points.StationCount == 86 && points.Count >= 80 && points.Points.All(p => p.X >= 0 && p.X < 10918 && Math.Abs(p.Y - 143) < 4), "edge point ROI did not preserve valid stations across the long image");
        var unwrapped = result.Outputs.GetProperty("Unwrapped"); string plane = unwrapped.GetProperty("coordinatePlane").GetString()!;
        foreach (string name in new[] { "Line", "EdgePoints", "LineImage", "Crop" })
        {
            var value = result.Outputs.GetProperty(name);
            Require(IsUnwrappedCoordinates(value) && value.GetProperty("coordinatePlane").GetString() == plane, "ROI processing changed the unwrapped coordinate plane: " + name);
        }
        using var input = result.GetOutputImage("Unwrapped"); using var output = result.GetOutputImage("LineImage");
        Require(input.Info.Width == 10918 && input.Info.Height == 287 && output.CopyPixels().SequenceEqual(input.CopyPixels()), "line fitting altered the clean long input image");
        using var crop = result.GetOutputImage("Crop");
        Require(crop.Info.Width == 50 && crop.Info.Height == 287 && crop.Info.OriginX == 10868 && crop.Info.OriginY == 0, "partly outside image-processing ROI did not crop only its valid intersection");
        Require(ImageInfo.Text.Contains("展开图坐标", StringComparison.Ordinal), "selected line output lost the unwrapped coordinate label");
    }
}

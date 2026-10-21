using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeImagePipelineAsync(string scenario)
    {
        bool lines = scenario == "image-inline-lines";
        const int width = 480, height = 340;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double signed = lines ? Math.Min(y - 100, 220 - y)
                : Math.Max(32 - Math.Sqrt((x - 150) * (x - 150) + (y - 170) * (y - 170)), 22 - Math.Sqrt((x - 330) * (x - 330) + (y - 170) * (y - 170)));
            pixels[y * width + x] = (byte)Math.Clamp(125 + signed * 180, 20, 230);
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80); ShowSource();
        flow!.Add("ExternalImage", 25, 130, "source"); flow.Add("ToGray", 260, 130, "gray"); flow.Connect("source", "image", "gray", "input");
        if (scenario == "image-process-chain")
        {
            flow.Add("Threshold", 500, 130, "binary"); flow.Add("Morphology", 740, 130, "morph"); flow.Add("AnalyzeContours", 980, 130, "region");
            flow.Connect("gray", "image", "binary", "input"); flow.Connect("binary", "image", "morph", "input"); flow.Connect("morph", "image", "region", "input");
            flow.Publish("Image", "region", "image", false); flow.Publish("Regions", "region", "result", false); Graph.Select("region");
            await BusyAsync(RunOnceAsync);
            Require(result!.GetResult<VisionRegion>("Regions").Count == 2, "direct image chain did not segment and analyze two regions");
            using var image = result.GetOutputImage("Image");
            Require(image.Info.PixelFormat == VisionPixelFormat.Gray8 && image.CopyPixels().All(p => p is 0 or 255), "image output contains burned-in overlay pixels");
            Require(ImageView.Overlays?.GetArrayLength() > 0 && ImageView.Info?.PixelFormat == VisionPixelFormat.Gray8, "processed mask or independent overlays are missing");
            return;
        }
        flow.Add(lines ? "MeasureLineDistance" : "MeasureCircleDistance", 500, 130, "distance");
        flow.Connect("gray", "image", "distance", "input");
        flow.Publish("Distance", "distance", "result", false); flow.Publish("Image", "distance", "image", false);
        Graph.Select("distance");
        await BusyAsync(RunOnceAsync);
        Require(!result!.GetGeometricMeasurement("Distance").Valid, "missing A/B ROI returned a synthetic successful measurement");
        foreach (string side in new[] { "first", "second" })
        {
            var choose = InspectorControls<Button>().Single(b => Equals(b.Tag, "measurement-side:" + side));
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            flow.SetParameter("distance", side + "CaliperCount", JsonValue.Create(48));
            flow.SetParameter("distance", side + "SearchLength", JsonValue.Create(24.0));
            flow.SetParameter("distance", side + "RemovePointCount", JsonValue.Create(2));
            flow.SetParameter("distance", side + "MaximumDeviation", JsonValue.Create(1.0));
            flow.SetParameter("distance", side + "Polarity", JsonValue.Create(lines && side == "first" ? "darkToLight" : "lightToDark"));
            await DrawMeasurementRoiAsync("distance");
            var region = lines ? RoiRegion.RotatedRectangle(240, side == "first" ? 100 : 220, 280, 24, 0)
                : RoiRegion.Circle(side == "first" ? 150 : 330, 170, side == "first" ? 32 : 22);
            ImageView.CommitRegion(region, false); ImageView.EndEdit();
            Require(RoiRegion.FromJson(flow.Node("distance")["parameters"]![side + "Roi"]).Shape == region.Shape, "A/B draw committed to the wrong side");
            Require(CaliperLayout.Create(region, ImageView.CaliperParameters!, !lines, width, height).Count == 48, "A/B caliper guides do not follow the active ROI");
        }
        var savedB = flow.Node("distance")["parameters"]!["secondRoi"]!.ToJsonString();
        flow.Undo(); Require(RoiRegion.FromJson(flow.Node("distance")["parameters"]!["secondRoi"]).Shape == "none", "A/B drawing did not create a single undo transaction");
        flow.Redo(); Require(flow.Node("distance")["parameters"]!["secondRoi"]!.ToJsonString() == savedB, "A/B redo did not restore the drawn ROI");
        Graph.Select("distance"); await BusyAsync(RunOnceAsync);
        var measured = result!.GetGeometricMeasurement("Distance");
        Require(measured.Valid && Math.Abs(measured.Value!.Value - (lines ? 120 : 180)) < .5, "module did not detect and fit both original-image objects");
        using var processed = result.GetOutputImage("Image");
        Require(processed.Info.OriginX == 900 && processed.Info.OriginY == -80 && processed.CopyPixels().SequenceEqual(pixels), "measurement image changed clean pixels or lost origin");
        Require(ImageView.Overlays?.EnumerateArray().Any(o => o.GetProperty("type").GetString() == "line") == true, "image result did not carry separate measurement annotations");
        Require(Results.Children.OfType<TextBlock>().Any(t => t.Text.Contains("180", StringComparison.Ordinal) || t.Text.Contains("120", StringComparison.Ordinal)), "hidden measurement result is no longer visible in the inspector");
    }
}

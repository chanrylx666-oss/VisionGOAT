using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeAnnularUnwrapAsync()
    {
        const int width = 360, height = 320;
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double dx = x - 180, dy = y - 160, radius = Math.Sqrt(dx * dx + dy * dy);
            var color = radius is >= 40 and <= 100 ? AnnularQuadrant(dx, dy) : ((byte)10, (byte)10, (byte)10);
            int index = (y * width + x) * 3;
            pixels[index] = color.Item1; pixels[index + 1] = color.Item2; pixels[index + 2] = color.Item3;
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width * 3, VisionPixelFormat.Bgr24, 700, -90); ShowSource();
        flow!.Add("ExternalImage", 25, 100, "source"); flow.Add("FindCircles", 260, 100, "circle");
        flow.Add("AnnularUnwrap", 500, 100, "unwrap"); flow.Add("ToGray", 745, 100, "gray");
        flow.Connect("source", "image", "circle", "input"); flow.Connect("circle", "image", "unwrap", "input"); flow.Connect("unwrap", "image", "gray", "input");
        foreach (var pair in new JsonObject { ["method"] = "caliper", ["caliperPlacement"] = "roiBoundary", ["roi"] = RoiRegion.Circle(180, 160, 100).ToJson(),
            ["caliperCount"] = 48, ["searchLength"] = 20.0, ["caliperThreshold"] = 5.0, ["polarity"] = "lightToDark", ["outputAnnulusWidth"] = 0.0 })
            flow.SetParameter("circle", pair.Key, pair.Value);
        flow.Publish("Circle", "circle", "result", false); flow.Publish("Unwrapped", "unwrap", "image", false); flow.Publish("Gray", "gray", "image", false);
        Graph.Select("unwrap");
        Require(InspectorControls<ComboBox>().Single(c => Equals(c.Tag, "annular:shape")).Items.Count == 1, "annular unwrap offered a full-image or rectangular ROI");
        var draw = InspectorControls<Button>().Single(b => Equals(b.Tag, "annular:draw"));
        draw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
        Require(!busy && ImageView.EditEnabled && ImageView.Info?.Width == width, "annular draw did not prepare its input");
        ImageView.CommitRegion(RoiRegion.Annulus(180, 160, 45, 95), false); ImageView.EndEdit();
        await EditRoiAsync();
        ImageView.CommitRegion(RoiRegion.Annulus(180, 160, 40, 100), false); ImageView.EndEdit();
        ParameterNumber("annular:innerRadius", "39");
        ParameterNumber("annular:innerRadius", "40");
        ParameterNumber("outputWidth", "360"); ParameterNumber("outputHeight", "61"); ParameterChoice("interpolation", "最近邻（保留像素值）");
        await BusyAsync(RunOnceAsync); VerifyAnnularUnwrap();
        Require(result!.GetResult<VisionCircle>("Circle").Count == 1, "smoke source did not produce the upstream circle annotation");
        Require(ImageView.Overlays?.GetArrayLength() is null or 0, "source-frame circle overlay leaked onto the unwrapped image");
        Graph.Select("gray"); Require(ImageInfo.Text.Contains("展开图坐标", StringComparison.Ordinal), "downstream image was mislabeled as original image coordinates");
        Graph.Select("unwrap");
    }

    private static (byte, byte, byte) AnnularQuadrant(double dx, double dy) => (dx >= 0, dy >= 0) switch
    {
        (true, true) => (20, 40, 230), (false, true) => (30, 180, 40),
        (false, false) => (220, 50, 30), _ => (50, 220, 210)
    };

    private void VerifyAnnularUnwrap()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "annular flow did not execute");
        var roi = RoiRegion.FromJson(flow!.Node("unwrap")["parameters"]!["roi"]);
        Require(roi.Shape == "annulus" && roi.InnerRadius == 40 && roi.Radius == 100 && roi.X == 180 && roi.Y == 160, "annular ROI drawing or numeric editing did not roundtrip");
        using var image = result!.GetOutputImage("Unwrapped"); var info = image.Info; var pixels = image.CopyPixels();
        Require(info.Width == 360 && info.Height == 61 && info.OriginX == 0 && info.OriginY == 0 && info.PixelFormat == VisionPixelFormat.Bgr24, "unwrapped dimensions, local origin or pixel format changed");
        foreach (int x in new[] { 45, 135, 225, 315 })
        {
            double angle = x * Math.PI / 180; var expected = AnnularQuadrant(Math.Cos(angle), Math.Sin(angle)); int index = 30 * info.Stride + 3 * x;
            Require(pixels[index] == expected.Item1 && pixels[index + 1] == expected.Item2 && pixels[index + 2] == expected.Item3, "annular angular sampling or clean pixels are incorrect");
        }
        var metadata = result.Outputs.GetProperty("Unwrapped");
        Require(IsUnwrappedCoordinates(metadata), "unwrapped coordinate metadata is missing");
        Require(!metadata.TryGetProperty("matchPosition", out _) && !metadata.TryGetProperty("fixture", out _), "source position leaked across nonlinear unwrapping");
        using var gray = result.GetOutputImage("Gray");
        Require(gray.Info.Width == 360 && gray.Info.Height == 61 && gray.Info.PixelFormat == VisionPixelFormat.Gray8, "downstream image processing did not accept the unwrapped image");
        Require(IsUnwrappedCoordinates(result.Outputs.GetProperty("Gray")), "downstream image processing lost the unwrapped coordinate plane");
        Require(ImageInfo.Text.Contains("展开图坐标", StringComparison.Ordinal), "unwrapped image coordinate label is missing");
    }
}

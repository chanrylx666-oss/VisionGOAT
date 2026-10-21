using JPVision.Editor.Core;
using JPVision.Sdk;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeCircularDistanceAsync(string scenario)
    {
        bool lineCircle = scenario.StartsWith("line-circle", StringComparison.Ordinal);
        bool empty = scenario.EndsWith("empty", StringComparison.Ordinal);
        bool centerMode = scenario.EndsWith("center", StringComparison.Ordinal);
        const int width = 480, height = 340; var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double a = 32 - Math.Sqrt((x - 150) * (x - 150) + (y - 170) * (y - 170));
            double b = 22 - Math.Sqrt((x - 330) * (x - 330) + (y - 170) * (y - 170));
            pixels[y * width + x] = (byte)Math.Clamp(125 + Math.Max(60 - y, Math.Max(a, b)) * 180, 20, 230);
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80); ShowSource();
        flow!.Add("ExternalImage", 25, 145, "source");
        foreach (string id in new[] { "a", "b" })
        {
            bool line = lineCircle && id == "a";
            flow.Add(line ? "FindLines" : "FindCircles", 300, id == "a" ? 35 : 245, id); flow.Connect("source", "image", id, "input");
            flow.SetParameter(id, "method", JsonValue.Create("caliper"));
            flow.SetParameter(id, "caliperCount", JsonValue.Create(line ? 32 : 64));
            flow.SetParameter(id, "polarity", JsonValue.Create("lightToDark"));
            flow.SetParameter(id, "maximumDeviation", JsonValue.Create(1.0));
            Graph.Select(id);
            if (line)
            {
                await DrawLineCaliperAsync(id); ImageView.CommitRegion(RoiRegion.RotatedRectangle(240, 60, 300, 24, 0), false);
            }
            else
            {
                flow.SetParameter(id, "removePointCount", JsonValue.Create(2));
                await DrawRegionAsync(id, "circle", false);
                ImageView.CommitRegion(RoiRegion.Circle(lineCircle || id == "a" ? 150 : 330, 170, lineCircle || id == "a" ? 32 : 22), false);
            }
            ImageView.EndEdit();
        }
        flow.Add(lineCircle ? "MeasureLineCircleDistance" : "MeasureCircleDistance", 600, 145, "distance");
        flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
        flow.Publish("Distance", "distance", "result", false); flow.Publish("Decision", "distance", "decision", true);
        flow.Publish("Overlay", "distance", "overlay", false); Graph.Select("distance");
        double expectedCenter = lineCircle ? 110 : 180, expectedEdge = lineCircle ? 78 : 126;
        await BusyAsync(RunOnceAsync);
        Require(result!.GetGeometricMeasurement("Distance") is { Valid: true, DistanceMode: "centerDistance" } initial && Math.Abs(initial.Value!.Value - expectedCenter) < .5, "new circular distance tool does not default to center measurement");
        void SelectMode(string label)
        {
            var choice = InspectorControls<ComboBox>().Single(c => c.Items.OfType<string>().Contains("圆周最短间距"));
            choice.SelectedItem = label;
        }
        SelectMode("圆周最短间距"); await BusyAsync(RunOnceAsync);
        Require(result!.GetGeometricMeasurement("Distance") is { Valid: true, DistanceMode: "edgeDistance" } edge && Math.Abs(edge.Value!.Value - expectedEdge) < .5, "distance mode selector did not measure the circumferences");
        if (centerMode) SelectMode("圆心距离");
        flow.SetParameter("distance", "toleranceEnabled", JsonValue.Create(true));
        double expected = centerMode ? expectedCenter : expectedEdge;
        flow.SetParameter("distance", "lowerLimit", JsonValue.Create(expected - .5)); flow.SetParameter("distance", "upperLimit", JsonValue.Create(expected + .5));
        await BusyAsync(RunOnceAsync);
        var measured = result!.GetGeometricMeasurement("Distance");
        Require(measured.Valid && measured.DistanceSegment is not null && result.Decision == InspectionDecision.Pass, "selected circular distance did not pass its tolerance");
        Require(result.GetOverlays("Overlay").Count > 0 && result.GetOverlays("Overlay").All(o => o.Color == "#00FF00"), "measurement result overlays are not all green");
        Require(Results.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith("圆周最短间距：", StringComparison.Ordinal)), "the secondary edge distance was not displayed");
        if (empty)
        {
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)Math.Clamp(125 + (60 - y) * 180, 20, 230);
            source.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 900, -80); ShowSource();
            await BusyAsync(RunOnceAsync);
            var absent = result!.GetGeometricMeasurement("Distance");
            Require(!absent.Valid && absent.Value is null && absent.CenterDistance is null && absent.EdgeDistance is null && absent.DistanceSegment is null && result.Decision == InspectionDecision.Unknown, "missing circles reused old distance values");
            Require(result.GetOverlays("Overlay").Count == 0, "missing current geometry retained an old measurement segment");
            // The image workflow also carries valid diagnostics from the current upstream line.
            // A missing circle must not leave the previous fitted circle or annulus on screen.
            Require(ImageView.Overlays?.EnumerateArray().All(o => o.GetProperty("type").GetString() != "annulus" &&
                (o.GetProperty("type").GetString() != "circle" || o.GetProperty("radius").GetDouble() <= 3)) == true,
                "missing current geometry retained an old fitted circle");
        }
        Require(Results.Children.OfType<Button>().Any(b => b.Content?.ToString() == "导出本次结果 JSON"), "circular distance JSON export is missing");
        string export = Path.ChangeExtension(CapturePath, ".result.json"); WriteAtomic(export, result!.Data.GetRawText());
        using var saved = JsonDocument.Parse(File.ReadAllText(export));
        Require(saved.RootElement.GetProperty("outputs").GetProperty("Distance").GetProperty("valid").GetBoolean() == !empty, "exported circular distance is stale");
    }
}

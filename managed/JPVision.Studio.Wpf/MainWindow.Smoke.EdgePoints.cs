using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeEdgePointsAsync()
    {
        const int width = 640, height = 260;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double edge = 95 + 8 * Math.Sin(x / 24.0) + 3 * Math.Sin(x / 9.0);
            pixels[y * width + x] = (byte)Math.Clamp(125 + Math.Min(y - edge, 190 - y) * 180, 20, 230);
        }
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 500, -30); ShowSource();
        flow!.Add("ExternalImage", 25, 130, "source"); flow.Add("FindEdgePoints", 290, 40, "edge");
        flow.Add("FindLines", 290, 235, "line"); flow.Add("MeasurePointLineDistances", 600, 130, "distance");
        flow.Connect("source", "image", "edge", "input"); flow.Connect("source", "image", "line", "input");
        flow.Connect("edge", "image", "distance", "input"); flow.Connect("line", "image", "distance", "line");
        flow.Publish("EdgePoints", "edge", "result", false); flow.Publish("Distances", "distance", "result", false); flow.Publish("DistanceImage", "distance", "image", false);
        Graph.Select("edge");
        var draw = InspectorControls<Button>().Single(b => Equals(b.Tag, "edge-points:draw"));
        draw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
        Require(!busy && ImageView.EditEnabled && ImageView.LineStrip && !ImageView.CircleCalipers, "edge point drawing did not prepare the search strip");
        ImageView.CommitRegion(RoiRegion.RotatedRectangle(320, 95, 500, 60, 0), false); ImageView.EndEdit();
        await EditRoiAsync(); ImageView.CommitRegion(RoiRegion.RotatedRectangle(320, 95, 520, 70, 0), false); ImageView.EndEdit();
        ParameterNumber("sampleSpacing", "6"); ParameterNumber("edgeThreshold", "8"); ParameterChoice("polarity", "暗 → 亮");
        var parameters = flow.Node("edge")["parameters"]!.AsObject();
        Require(!parameters.ContainsKey("method") && !parameters.ContainsKey("caliperPlacement"), "point set drawing inserted straight-line fit parameters");
        var preview = CaliperLayout.EdgePoints(CurrentInputRoi("edge"), EffectiveParameters("edge"));
        Require(preview.Count == 87 && preview[0].X == 60 && preview[^1].X == 576 && preview[0].NormalY == 1, "edge point sampling preview does not match the station spacing");
        Graph.Select("line"); flow.SetParameter("line", "polarity", JsonValue.Create("lightToDark"));
        flow.SetParameter("line", "caliperCount", JsonValue.Create(48)); await DrawLineCaliperAsync("line");
        ImageView.CommitRegion(RoiRegion.RotatedRectangle(320, 190, 520, 30, 0), false); ImageView.EndEdit();
        Graph.Select("distance"); ParameterNumber("displayEveryNth", "10"); ParameterChoice("statistic", "最大距离");
        await BusyAsync(RunOnceAsync); VerifyEdgePoints();
        Graph.Select("edge");
        Require(Results.Children.OfType<DataGrid>().Single(g => Equals(g.Tag, "edge-points:table")).Items.Count == 87, "edge point coordinates are not available in the results table");
        Graph.Select("distance");
        var saved = source; source = VisionFrame.CopyFrom(Enumerable.Repeat((byte)20, width * height).ToArray(), width, height, width, VisionPixelFormat.Gray8, 500, -30); ShowSource();
        try
        {
            await BusyAsync(RunOnceAsync);
            var empty = result!.GetOutput<JsonElement>("Distances");
            Require(!empty.GetProperty("valid").GetBoolean() && empty.GetProperty("distances").GetArrayLength() == 0 && empty.GetProperty("maximum").ValueKind == JsonValueKind.Null, "missing edges reused a previous point distance or manufactured zero");
        }
        finally { source.Dispose(); source = saved; ShowSource(); }
        await BusyAsync(RunOnceAsync); VerifyEdgePoints();
    }

    private void VerifyEdgePoints()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "edge-point flow did not execute");
        var edge = result!.GetOutput<JsonElement>("EdgePoints"); var measurement = result.GetOutput<JsonElement>("Distances");
        Require(edge.GetProperty("valid").GetBoolean() && edge.GetProperty("points").GetArrayLength() == 87, "irregular edge was not sampled into all real stations");
        Require(measurement.GetProperty("valid").GetBoolean() && measurement.GetProperty("distances").GetArrayLength() == 87, "point set did not measure against the selected line output");
        foreach (var point in measurement.GetProperty("distances").EnumerateArray())
        {
            double x = point.GetProperty("x").GetDouble(), y = point.GetProperty("y").GetDouble();
            double px = point.GetProperty("projectionX").GetDouble(), py = point.GetProperty("projectionY").GetDouble();
            double distance = point.GetProperty("distance").GetDouble();
            Require(x is >= 559 and <= 1077 && y is > 50 and < 80 && Math.Abs(py - 160) < .4 && Math.Abs(px - x) < .4, "point/line coordinates did not retain the source image origin");
            Require(Math.Abs(distance - Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y))) < 1e-7, "distance sample is not a perpendicular point-to-line measurement");
        }
        Require(measurement.GetProperty("maximum").GetDouble() - measurement.GetProperty("minimum").GetDouble() > 14, "irregular curve was flattened into a fitted straight line");
        Require(Results.Children.OfType<DataGrid>().Single(g => Equals(g.Tag, "point-line:table")).Items.Count == 87, "distance list is not displayed");
        using var clean = result.GetOutputImage("DistanceImage");
        Require(clean.Info.Width == 640 && clean.Info.Height == 260 && clean.CopyPixels().SequenceEqual(source!.CopyPixels()), "green measurement annotations altered the image pixels");
        Require(ImageView.Overlays?.EnumerateArray().Any(o => o.TryGetProperty("color", out var color) && color.GetString() == "#00FF00") == true, "green point/line distance annotations are missing");
    }
}

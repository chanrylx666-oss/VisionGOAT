using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeEdgeReconstructionAsync()
    {
        const int width = 640, height = 240;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double boundary = 90 + 8 * Math.Sin(x / 80.0);
            if (x is >= 180 and <= 186) boundary -= 36;
            if (x is >= 300 and <= 324) boundary = 0;
            pixels[y * width + x] = y >= boundary && y < 190 ? (byte)230 : (byte)20;
        }
        source!.Dispose();
        source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 500, -30);
        ShowSource();
        flow!.Add("ExternalImage", 25, 130, "source"); flow.Add("FindEdgePoints", 290, 40, "edge");
        flow.Add("FindLines", 290, 235, "line"); flow.Add("MeasurePointLineDistances", 600, 130, "distance");
        flow.Connect("source", "image", "edge", "input"); flow.Connect("source", "image", "line", "input");
        flow.Connect("edge", "image", "distance", "input"); flow.Connect("line", "image", "distance", "line");
        flow.Publish("EdgePoints", "edge", "result", false); flow.Publish("Distances", "distance", "result", false);
        flow.Publish("DistanceImage", "distance", "image", false);
        flow.SetParameter("edge", "roi", RoiRegion.Rectangle(40, 40, 560, 100).ToJson());
        flow.SetParameter("edge", "sampleSpacing", JsonValue.Create(4.0));
        flow.SetParameter("edge", "projectionWidth", JsonValue.Create(3));
        flow.SetParameter("edge", "polarity", JsonValue.Create("darkToLight"));
        Graph.Select("edge");
        Require(!InspectorControls<TextBox>().Any(control => Equals(control.Tag, "outlierTolerance")), "raw mode exposed inactive reconstruction controls");
        InspectorControls<Button>().Single(button => Equals(button.Tag, "edge-points:repair"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(flow.Node("edge")["parameters"]!["curveMode"]!.GetValue<string>() == "reconstruct", "repair preset did not enable reconstruction");
        ParameterNumber("filterWindow", "11"); ParameterNumber("outlierTolerance", "4"); ParameterNumber("maximumGapLength", "80");
        flow.SetParameter("line", "method", JsonValue.Create("caliper"));
        flow.SetParameter("line", "caliperPlacement", JsonValue.Create("roiCenter"));
        flow.SetParameter("line", "roi", RoiRegion.RotatedRectangle(320, 190, 560, 24, 0).ToJson());
        flow.SetParameter("line", "polarity", JsonValue.Create("lightToDark"));
        flow.SetParameter("line", "caliperCount", JsonValue.Create(40));
        Graph.Select("distance");
        await BusyAsync(RunOnceAsync);
        var points = result!.GetEdgePoints("EdgePoints");
        var measured = result.GetPointLineDistances("Distances");
        Require(points.Valid && points.RejectedCount > 0 && points.InterpolatedCount > 0, "spikes and a bounded gap were not repaired");
        Require(measured.Valid && measured.PointSource == "measuredOnly" && measured.Count == points.MeasuredCount && measured.InterpolatedCount == 0,
            "default measurement silently included interpolated points");
        Require(measured.Distances.All(point => point.Origin == "measured" && !point.Adjusted), "default measurement used smoothed coordinates");
        var retained = points.Points.Where(point => point.Origin == "measured").ToDictionary(point => point.StationIndex);
        foreach (var distance in measured.Distances)
        {
            var point = retained[distance.StationIndex];
            Require(Math.Abs(distance.X - point.RawX!.Value) < 1e-8 && Math.Abs(distance.Y - point.RawY!.Value) < 1e-8,
                "measured-only distance does not use the original observed coordinates");
        }
        Graph.Select("edge");
        var table = Results.Children.OfType<DataGrid>().Single(grid => Equals(grid.Tag, "edge-points:table"));
        Require(table.Columns.Any(column => Equals(column.Header, "来源")) && table.Columns.Any(column => Equals(column.Header, "原始 X")), "reconstruction provenance columns are missing");
        Require(Results.Children.OfType<Expander>().Any(expander => Equals(expander.Tag, "edge-points:raw")), "raw observations cannot be inspected");
        Graph.Select("distance"); ParameterChoice("pointSource", "修正／补全点集");
        await BusyAsync(RunOnceAsync); VerifyEdgeReconstruction();
    }

    private void VerifyEdgeReconstruction()
    {
        var points = result!.GetEdgePoints("EdgePoints"); var measurement = result.GetPointLineDistances("Distances");
        Require(points.Valid && points.CurveMode == "reconstruct" && points.RawPoints?.Count == points.RawCount && points.RejectedCount > 0 && points.InterpolatedCount > 0,
            "reconstruction metadata did not survive execution or save/reload");
        Require(measurement.Valid && measurement.PointSource == "all" && measurement.Count == points.Count && measurement.InterpolatedCount == points.InterpolatedCount,
            "explicit reconstructed measurement did not include the complete selected point set");
        Require(measurement.Distances.Any(point => point.Origin == "interpolated"), "interpolated distances lost their provenance");
        Require(Results.Children.OfType<DataGrid>().Single(grid => Equals(grid.Tag, "point-line:table")).Columns.Any(column => Equals(column.Header, "来源")),
            "distance table hides interpolated origins");
        using var clean = result.GetOutputImage("DistanceImage");
        Require(clean.CopyPixels().SequenceEqual(source!.CopyPixels()), "curve reconstruction changed input image pixels");
    }
}

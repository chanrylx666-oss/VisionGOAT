using JPVision.Editor.Core;
using JPVision.Sdk;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

public partial class MainWindow
{
    private void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Smoke: " + message);
    }
    private string CapturePath => Path.GetFullPath(Argument("--capture") ?? Path.Combine(AppContext.BaseDirectory, "wpf-smoke.png"));
    private async Task SmokeAsync()
    {
        string scenario = Argument("--case") ?? "direct-results";
        if (scenario == "license-lock")
        {
            Require(!licensed && engine is null && !Graph.IsEnabled, "license gate did not lock editor");
            CaptureWindow(scenario);
            allowClose = true;
            Application.Current.Shutdown();
            return;
        }
        Require(licensed && flow is not null && source is not null, "licensed editor/source unavailable");
        if (scenario is "camera-close-empty" or "camera-close-stream")
        {
            await SmokeCameraCloseAsync(scenario);
            return;
        }
        flow!.Reset();
        recipeDirectory = null;
        runInputs.Clear();
        if (scenario is "camera-source" or "camera-main-live")
            await SmokeCameraSourceAsync(scenario == "camera-main-live");
        else if (scenario == "blob-analysis")
            await SmokeBlobAnalysisAsync();
        else if (scenario == "line-density")
            await SmokeLineDensityAsync();
        else if (scenario == "edge-points-ranges")
            await SmokeEdgePointRangesAsync();
        else if (scenario == "roi-bounds")
            await SmokeRoiBoundsAsync();
        else if (scenario == "edge-points")
            await SmokeEdgePointsAsync();
        else if (scenario == "edge-reconstruction")
            await SmokeEdgeReconstructionAsync();
        else if (scenario == "parameter-subscriptions")
            await SmokeParameterSubscriptionsAsync();
        else if (scenario == "annular-unwrap")
            await SmokeAnnularUnwrapAsync();
        else if (scenario.StartsWith("image-", StringComparison.Ordinal))
            await SmokeImagePipelineAsync(scenario);
        else if (scenario.StartsWith("parameters-", StringComparison.Ordinal))
            await SmokeParameterDetailsAsync(scenario);
        else if (scenario == "direct-results")
        {
            flow.Add("ExternalImage", 35, 60, "source");
            flow.Add("ToGray", 270, 60, "gray");
            flow.Add("Threshold", 510, 60, "binary");
            flow.Connect("source", "image", "gray", "input");
            flow.Connect("gray", "image", "binary", "input");
            flow.Definition["outputs"] = new JsonObject { ["OldCount"] = new JsonObject { ["node"] = "deleted", ["port"] = "value", ["type"] = "Int" } };
            flow.Definition["kind"] = "inspection";
            flow.Definition["decisionOutput"] = "OldDecision";
            Graph.Select("binary");
            await BusyAsync(RunOnceAsync);
            Require(result?.Status == ExecutionStatus.Succeeded && result.Decision == InspectionDecision.Unknown, "direct chain did not execute");
            var binary = result!.GetNodeOutput<JsonElement>("binary", "mask");
            Require(binary.GetProperty("foregroundPixels").GetInt32() > 0 && ImageView.Info?.PixelFormat == VisionPixelFormat.Gray8, "binary output not displayed");
            Graph.Select("gray");
            Require(ImageView.Info?.PixelFormat == VisionPixelFormat.Gray8, "gray selected preview is not gray");
            Graph.Select("binary");
        }
        else if (scenario.StartsWith("circle-distance-", StringComparison.Ordinal) || scenario.StartsWith("line-circle-", StringComparison.Ordinal))
            await SmokeCircularDistanceAsync(scenario);
        else if (scenario is "line-distance" or "line-distance-angle" or "line-distance-empty")
        {
            const int w = 440, h = 360; const double degrees = 25;
            double skew = scenario == "line-distance-angle" ? 3 : 0;
            double r = degrees * Math.PI / 180, s = (degrees + skew) * Math.PI / 180, nx = -Math.Sin(r), ny = Math.Cos(r);
            double bx = 220 + nx * 36 + Math.Cos(r) * 35, by = 180 + ny * 36 + Math.Sin(r) * 35;
            var pixels = new byte[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                double a = (x - 220) * nx + (y - 180) * ny + 36;
                double b = -(x - bx) * Math.Sin(s) + (y - by) * Math.Cos(s);
                pixels[y * w + x] = (byte)Math.Clamp(125 + Math.Min(a, -b) * 180, 20, 230);
            }
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80); ShowSource();
            flow.Add("ExternalImage", 25, 130, "source");
            foreach (var item in new[] { (Id: "a", Side: -36.0, Along: -15.0, Angle: degrees), (Id: "b", Side: 36.0, Along: 35.0, Angle: degrees + skew) })
            {
                flow.Add("FindLines", 300, item.Id == "a" ? 30 : 230, item.Id); flow.Connect("source", "image", item.Id, "input");
                flow.SetParameter(item.Id, "caliperCount", JsonValue.Create(32));
                flow.SetParameter(item.Id, "polarity", JsonValue.Create(item.Id == "a" ? "darkToLight" : "lightToDark"));
                Graph.Select(item.Id); await DrawLineCaliperAsync(item.Id);
                ImageView.CommitRegion(RoiRegion.RotatedRectangle(220 + nx * item.Side + Math.Cos(r) * item.Along, 180 + ny * item.Side + Math.Sin(r) * item.Along, item.Id == "a" ? 220 : 160, 30, item.Angle), false);
                ImageView.EndEdit();
            }
            flow.Add("MeasureLineDistance", 600, 130, "distance");
            flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
            flow.SetParameter("distance", "toleranceEnabled", JsonValue.Create(true));
            flow.SetParameter("distance", "lowerLimit", JsonValue.Create(71.5)); flow.SetParameter("distance", "upperLimit", JsonValue.Create(72.5));
            flow.Publish("Distance", "distance", "result", false); flow.Publish("Decision", "distance", "decision", true);
            flow.Publish("Overlay", "distance", "overlay", false); Graph.Select("distance");
            await BusyAsync(RunOnceAsync);
            if (scenario == "line-distance-angle")
            {
                var invalid = result!.GetGeometricMeasurement("Distance");
                Require(!invalid.Valid && invalid.Value is null && invalid.Decision.Reason == "NonParallelLines", "skew lines produced a fake parallel distance");
                UpdateParameter("distance", "parallelAngleToleranceDegrees", JsonValue.Create(4.0)); await BusyAsync(RunOnceAsync);
                Require(result!.GetGeometricMeasurement("Distance").Valid, "editable angle tolerance was ignored");
                UpdateParameter("distance", "parallelAngleToleranceDegrees", JsonValue.Create(1.0)); await BusyAsync(RunOnceAsync);
            }
            else
            {
                var measurement = result!.GetGeometricMeasurement("Distance");
                Require(measurement.Valid && Math.Abs(measurement.Value!.Value - 72) < .3 && measurement.DistanceSegment is not null && result.Decision == InspectionDecision.Pass, "two fitted lines did not produce the real perpendicular distance");
                Require(ImageView.Overlays?.EnumerateArray().Any(o => o.GetProperty("type").GetString() == "text") == true, "distance segment or value was not displayed");
                if (scenario == "line-distance-empty")
                {
                    source.Dispose(); source = VisionFrame.CopyFrom(Enumerable.Repeat((byte)20, w * h).ToArray(), w, h, w, VisionPixelFormat.Gray8, 900, -80); ShowSource();
                    await BusyAsync(RunOnceAsync);
                    var absent = result!.GetGeometricMeasurement("Distance");
                    Require(!absent.Valid && absent.Value is null && absent.DistanceSegment is null && result.Decision == InspectionDecision.Unknown, "missing current edges reused previous distance");
                }
            }
            Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("测量位置：A 中点", StringComparison.Ordinal)), "distance measurement definition is hidden");
            if (scenario == "line-distance-angle")
                Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("两线夹角超出", StringComparison.Ordinal)), "nonparallel reason was not displayed");
            Require(Results.Children.OfType<System.Windows.Controls.Button>().Any(b => b.Content?.ToString() == "导出本次结果 JSON"), "result export button is missing");
            string export = Path.ChangeExtension(CapturePath, ".result.json"); WriteAtomic(export, result!.Data.GetRawText());
            using var exported = JsonDocument.Parse(File.ReadAllText(export));
            Require(exported.RootElement.GetProperty("outputs").GetProperty("Distance").GetProperty("valid").GetBoolean() == (scenario == "line-distance"), "exported line distance is stale");
        }
        else if (scenario is "line-strip" or "line-strip-roi" or "line-strip-empty")
        {
            const int w = 360, h = 260; var pixels = new byte[w * h];
            const double degrees = 25; double radians = degrees * Math.PI / 180, nx = -Math.Sin(radians), ny = Math.Cos(radians);
            bool empty = scenario == "line-strip-empty";
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                pixels[y * w + x] = empty ? (byte)20 : (byte)Math.Clamp(125 + ((x - 180) * nx + (y - 130) * ny - 3) * 200, 20, 230);
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 500, -20); ShowSource();
            flow.Add("ExternalImage", 25, 100, "source"); flow.Add("FindLines", 300, 100, "line"); flow.Add("BuildFeatureOverlay", 575, 100, "mark");
            flow.Connect("source", "image", "line", "input"); flow.Connect("line", "result", "mark", "input");
            flow.SetParameter("line", "caliperCount", JsonValue.Create(32)); flow.SetParameter("line", "polarity", JsonValue.Create("darkToLight"));
            flow.Publish("Line", "line", "result", false); flow.Publish("Marker", "mark", "overlay", false); Graph.Select("line");
            async Task ClickDraw()
            {
                var button = Parameters.Children.OfType<System.Windows.Controls.Button>().Single(b => b.Name == "DrawLineCaliper");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
                Require(!busy && ImageView.EditEnabled && ImageView.LineStrip && !ImageView.CircleCalipers, "line strip teaching mode was not prepared");
            }
            string before = flow.Definition.ToJsonString(); await ClickDraw();
            ImageView.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(ImageView)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Require(flow.Definition.ToJsonString() == before, "cancelled line strip teaching changed recipe");
            await ClickDraw();
            var roi = LineCaliperRoi.FromEndpoints(new(180 - 110 * Math.Cos(radians), 130 - 110 * Math.Sin(radians)), new(180 + 110 * Math.Cos(radians), 130 + 110 * Math.Sin(radians)), 60);
            ImageView.CommitRegion(roi, false); ImageView.EndEdit();
            string taught = flow.Definition.ToJsonString(); flow.Undo(); Require(flow.Definition.ToJsonString() == before, "line teaching is not one undo step");
            flow.Redo(); Require(flow.Definition.ToJsonString() == taught, "line teaching redo lost ROI");
            await EditRoiAsync();
            var handles = LineCaliperRoi.Handles(roi);
            roi = LineCaliperRoi.Adjust(roi, 0, new(handles[0].X - 4, handles[0].Y + 2)); ImageView.CommitRegion(roi, false);
            handles = LineCaliperRoi.Handles(roi);
            roi = LineCaliperRoi.Adjust(roi, 2, new(handles[2].X + nx * 10, handles[2].Y + ny * 10)); ImageView.CommitRegion(roi, false);
            ImageView.EndEdit(); Require(Math.Abs(flow.Node("line")["parameters"]!["searchLength"]!.GetValue<double>() - roi.Height) < 1e-8, "ROI and sampling search height diverged");
            await BusyAsync(RunOnceAsync);
            var packet = result!.GetResult<VisionLine>("Line"); Require(packet.Count == (empty ? 0 : 1), "line strip did not fit the expected number of lines");
            if (!empty)
            {
                var line = packet.Features.Single();
                Require(Math.Abs(line.AngleDegrees - 25) < .2 && line.EdgePoints.Count == 32 && line.RmsError < .2 && line.Equation is not null, "line data is not a fit of sampled edge points");
                Require(Math.Abs((line.X1 - 680) * nx + (line.Y1 - 110) * ny - 3) < .3 && Math.Abs((line.X2 - 680) * nx + (line.Y2 - 110) * ny - 3) < .3, "fitted line endpoints are not in original coordinates");
                Require(result.GetOverlays("Marker").Any(o => o.Kind == "line"), "downstream marker did not receive the fitted line");
            }
            else
            {
                Require(packet.Measurement.Selected is null && packet.Measurement.Caliper?.FitFailureReason == "InsufficientEdgePoints", "blank ROI produced an invented line");
                Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("未输出拟合直线的原因", StringComparison.Ordinal)), "line failure reason is hidden");
            }
            RequireCaliperParity("line", false);
            Log("直线卡尺验证图：25° 实际灰度边缘，搜索带调整、拟合与下游标记已检查。");
        }
        else if (scenario is "caliper-refit" or "caliper-fit-failure")
        {
            const int w = 360, h = 360; var pixels = new byte[w * h];
            bool success = scenario == "caliper-refit";
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                double dx = x - 180, dy = y - 180, angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                double radius = angle >= -2 && angle < 19 ? 95 : 64;
                if (!success)
                {
                    double radians = (Math.Atan2(dy, dx) + 2 * Math.PI) % (2 * Math.PI);
                    radius = (int)Math.Floor((radians + Math.PI / 64) / (Math.PI / 32)) % 2 == 0 ? 60 : 78;
                }
                pixels[y * w + x] = (byte)Math.Clamp((radius + .5 - Math.Sqrt(dx * dx + dy * dy)) * 230, 15, 230);
            }
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80); ShowSource();
            flow.Add("ExternalImage", 25, 100, "source"); flow.Add("FindCircles", 285, 100, "detect"); flow.Add("BuildFeatureOverlay", 560, 100, "mark");
            flow.Connect("source", "image", "detect", "input"); flow.Connect("detect", "result", "mark", "input");
            var settings = new JsonObject { ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
                ["roi"] = RoiRegion.Circle(180, 180, 78).ToJson(), ["caliperCount"] = 64, ["projectionWidth"] = 3, ["searchLength"] = 90.0,
                ["removePointCount"] = success ? 4 : 0, ["maximumDeviation"] = 1.0, ["minimumCoverage"] = .8,
                ["polarity"] = "lightToDark", ["outputAnnulusWidth"] = 12.0 };
            foreach (var pair in settings) flow.SetParameter("detect", pair.Key, pair.Value);
            flow.Publish("Circle", "detect", "result", false); flow.Publish("Marker", "mark", "overlay", false);
            Graph.Select("detect"); await BusyAsync(RunOnceAsync);
            var packet = result!.GetResult<VisionCircle>("Circle");
            Require(packet.Measurement.Caliper?.FitSucceeded == success && packet.Count == (success ? 1 : 0), "circle fit acceptance was not reported");
            Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => Equals(t.Tag, "circle:fit-counts") && t.Text.Contains("找到边缘 64 点", StringComparison.Ordinal)), "caliper counts are hidden when no circle is selected");
            if (success)
            {
                var circle = packet.Features.Single();
                Require(circle.EdgePoints.Count == 60 && Math.Abs(circle.X - 1080) < .2 && Math.Abs(circle.Y - 100) < .2 && Math.Abs(circle.Radius - 64) < .2,
                    "good circle was discarded after removing biased points");
                Require(result.GetOverlays("Marker").Count(o => o.Kind == "annulus") == 1, "refitted circle did not produce its downstream annulus");
            }
            else
            {
                Require(packet.Measurement.Caliper!.FitFailureReason == "InsufficientInliers" && packet.Measurement.Selected is null, "failed circle invented a result");
                Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => Equals(t.Tag, "circle:fit-failure") && t.Text.Contains("圆周点不足", StringComparison.Ordinal)), "circle failure reason is not visible in results");
                Require(!packet.Overlay.EnumerateArray().Any(o => o.GetProperty("type").GetString() is "annulus" or "circle") && result.GetOverlays("Marker").Count == 0,
                    "failed fit published a fake circle or annulus");
            }
        }
        else if (scenario == "large-inner-circle")
        {
            MakeLargeHoleSource();
            flow.Add("ExternalImage", 25, 100, "source"); flow.Add("FindCircles", 285, 100, "detect");
            flow.Add("CountFeatures", 560, 40, "count"); flow.Add("BuildFeatureOverlay", 560, 210, "mark");
            flow.Connect("source", "image", "detect", "input"); flow.Connect("detect", "result", "count", "input"); flow.Connect("detect", "result", "mark", "input");
            flow.SetParameter("detect", "positionReference", JsonValue.Create("image"));
            flow.SetParameter("detect", "minimumRadius", JsonValue.Create(35)); flow.SetParameter("detect", "maximumRadius", JsonValue.Create(60));
            flow.SetParameter("detect", "minimumDistance", JsonValue.Create(100.0)); flow.SetParameter("detect", "voteThreshold", JsonValue.Create(20.0));
            flow.Publish("Circle", "detect", "result", false); flow.Publish("MarkedOverlay", "mark", "overlay", false); flow.Publish("Count", "count", "value", false);
            Graph.Select("detect"); await BusyAsync(RunOnceAsync);
            Require(result!.GetResult<VisionCircle>("Circle").Count > 3, "small-circle sample did not reproduce bolt detections");
            string before = flow.Definition.ToJsonString();
            async Task ClickInnerCircle()
            {
                var button = Parameters.Children.OfType<System.Windows.Controls.Button>().SingleOrDefault(b => b.Name == "DrawInnerCircle");
                Require(button is not null, "inner-hole circle teaching is not exposed in the inspector");
                button!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                for (int i = 0; busy && i < 2000; i++) await Task.Delay(10);
                Require(!busy && ImageView.EditEnabled, "inner-hole draw action did not prepare its input");
            }
            await ClickInnerCircle();
            Require(flow.Definition.ToJsonString() == before, "starting a drawing changed the recipe before committing");
            ImageView.EndEdit();
            ImageView.CommitRegion(RoiRegion.Circle(2567, 2555, 1410), false);
            Require(flow.Node("detect")["parameters"]!["method"]!.GetValue<string>() == "automatic", "cancelled inner-hole teaching leaked into ordinary ROI drawing");
            flow.Undo(); Require(flow.Definition.ToJsonString() == before, "cancel/draw undo did not restore automatic recipe");
            await ClickInnerCircle();
            ImageView.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(ImageView)!, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            ImageView.CommitRegion(RoiRegion.Circle(2567, 2555, 1410), false); ImageView.EndEdit();
            Require(flow.Node("detect")["parameters"]!["method"]!.GetValue<string>() == "automatic", "Escape retained the inner-hole teaching callback");
            flow.Undo(); Require(flow.Definition.ToJsonString() == before, "Escape changed the recipe");
            await ClickInnerCircle(); ImageView.CommitRegion(RoiRegion.Circle(2567, 2555, 1410), false); ImageView.EndEdit();
            string taught = flow.Definition.ToJsonString();
            flow.Undo(); Require(flow.Definition.ToJsonString() == before, "inner-hole setup is not one undo operation");
            flow.Redo(); Require(flow.Definition.ToJsonString() == taught, "redo lost the inner-hole setup");
            await BusyAsync(RunOnceAsync);
            var packet = result!.GetResult<VisionCircle>("Circle"); var circle = packet.Features.Single();
            Require(Math.Abs(circle.X - 2560) < .5 && Math.Abs(circle.Y - 2560) < .5 && Math.Abs(circle.Radius - 1400) < .5,
                "fitted circle did not select the large bright hole in original image coordinates");
            Require(packet.Measurement.Caliper!.RejectedCount >= 4 && circle.EdgePoints.Count >= 50 && result.GetOutput<long>("Count") == 1,
                "single-hole fit did not retain valid samples after outlier rejection");
            var band = result.GetOverlays("MarkedOverlay").Single(o => o.Kind == "annulus");
            Require(band.X == circle.X && band.Y == circle.Y && Math.Abs((band.InnerRadius + band.OuterRadius) / 2 - circle.Radius) < 1e-8,
                "marker annulus is not centered on the final fitted hole");
            RequireCaliperParity("detect", true);
            flow.Change(() =>
            {
                var settings = flow.Node("detect")["parameters"]!;
                settings["caliperCount"] = 80; settings["projectionWidth"] = 7; settings["searchLength"] = 180.0;
                settings["removePointCount"] = 6; settings["maximumDeviation"] = 4.5;
            });
            await ClickInnerCircle(); ImageView.CommitRegion(RoiRegion.Circle(2562, 2560, 1405), false); ImageView.EndEdit();
            var tuned = flow.Node("detect")["parameters"]!;
            Require(tuned["caliperCount"]!.GetValue<int>() == 80 && tuned["projectionWidth"]!.GetValue<int>() == 7 &&
                tuned["searchLength"]!.GetValue<double>() == 180 && tuned["removePointCount"]!.GetValue<int>() == 6 &&
                tuned["maximumDeviation"]!.GetValue<double>() == 4.5, "re-teaching reset tuned sampling/fit parameters");
            flow.Undo(); flow.Undo();
        }
        else if (scenario == "caliper-result-annulus")
        {
            const int w = 360, h = 360; byte[] pixels = new byte[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                double dx = x - 180, dy = y - 180, angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                double radius = Math.Abs(Math.IEEERemainder(angle, 90)) < 3 ? 74 : 64;
                pixels[y * w + x] = (byte)Math.Clamp((radius + .5 - Math.Sqrt(dx * dx + dy * dy)) * 230, 15, 230);
            }
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8, 900, -80); ShowSource();
            flow.Add("ExternalImage", 25, 100, "source"); flow.Add("FindCircles", 285, 100, "detect");
            flow.Add("CountFeatures", 560, 40, "count"); flow.Add("BuildFeatureOverlay", 560, 210, "mark");
            flow.Connect("source", "image", "detect", "input"); flow.Connect("detect", "result", "count", "input"); flow.Connect("detect", "result", "mark", "input");
            var p = new JsonObject { ["method"] = "caliper", ["positionReference"] = "image", ["caliperPlacement"] = "roiBoundary",
                ["roi"] = RoiRegion.Circle(180, 180, 68).ToJson(), ["caliperCount"] = 40, ["projectionWidth"] = 3, ["searchLength"] = 40.0,
                ["removePointCount"] = 4, ["maximumDeviation"] = 20.0, ["polarity"] = "lightToDark", ["outputAnnulusWidth"] = 12.0 };
            foreach (var pair in p) flow.SetParameter("detect", pair.Key, pair.Value);
            flow.Publish("Circle", "detect", "result", false); flow.Publish("MarkedOverlay", "mark", "overlay", false); flow.Publish("Count", "count", "value", false);
            Graph.Select("detect"); await BusyAsync(RunOnceAsync);
            var fitted = result!.GetResult<VisionCircle>("Circle"); var circle = fitted.Features.Single();
            Require(Math.Abs(circle.Radius - 64) < .2 && fitted.Measurement.Caliper!.RejectedCount == 4 && circle.EdgePoints.Count == 36, "circle did not refit after removing four outliers");
            foreach (double width in new[] { 0.0, 24.0 })
            {
                UpdateParameter("detect", "outputAnnulusWidth", JsonValue.Create(width)); await BusyAsync(RunOnceAsync);
                var current = result!.GetResult<VisionCircle>("Circle");
                Require(current.Features[0].Radius == circle.Radius && current.Features[0].X == circle.X, "drawing width changed fitted geometry");
                Graph.Select("mark");
                Require(ImageView.Overlays?.EnumerateArray().Count(o => o.GetProperty("type").GetString() == "annulus") == (width == 0 ? 0 : 1), "downstream marker dropped or duplicated annulus");
                var band = result.GetOverlays("MarkedOverlay").SingleOrDefault(o => o.Kind == "annulus");
                Require(width == 0 ? band is null : band is not null && Math.Abs(band.OuterRadius - band.InnerRadius - width) < 1e-8 && band.X == circle.X && band.Y == circle.Y, "marker ring uses ROI instead of fitted circle");
                Graph.Select("detect");
                Require(ImageView.Overlays?.EnumerateArray().Count(o => o.GetProperty("type").GetString() == "annulus") == (width == 0 ? 0 : 1), "detector ring missing or duplicated");
            }
            Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("卡尺拟合（剔除后）", StringComparison.Ordinal)), "post-rejection fit not identified in results");
        }
        else if (scenario == "edgepair-binding")
        {
            const int w = 360, h = 240; var pixels = new byte[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y * w + x] = y >= 110 && y < 130 ? (byte)230 : (byte)15;
            source!.Dispose(); source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8); ShowSource();
            flow.Add("ExternalImage", 20, 90, "source");
            foreach (string id in new[] { "reference", "measure" })
            {
                flow.Add("MeasureEdgePair", id == "reference" ? 280 : 560, 90, id); flow.Connect("source", "image", id, "input");
                Graph.Select(id); await DrawRegionAsync(id, "rotatedRectangle", false);
                ImageView.CommitRegion(RoiRegion.RotatedRectangle(180, 120, 5, 80, 0), false); ImageView.EndEdit();
            }
            flow.SetParameter("measure", "toleranceEnabled", JsonValue.Create(true));
            flow.SetParameter("measure", "lowerLimit", JsonValue.Create(19.4));
            flow.SetParameter("measure", "upperLimit", new JsonObject { ["node"] = "reference", ["port"] = "result", ["field"] = "value" });
            flow.Publish("Width", "measure", "result", false); flow.Publish("Decision", "measure", "decision", true);
            flow.Add("FindCircles", 800, 90, "unfinished"); Graph.Select("measure"); await BusyAsync(() => RunOnceAsync("measure"));
            Require(!result!.Data.GetProperty("nodes").TryGetProperty("unfinished", out _) && result.GetGeometricMeasurement("Selected_result").Valid, "run to selected omitted ancestors or executed unrelated draft");
            flow.Delete("unfinished"); await BusyAsync(RunOnceAsync);
            var m = result!.GetGeometricMeasurement("Width"); Require(m.Valid && Math.Abs(m.Value!.Value - 20) < .6 && result.Decision == InspectionDecision.Pass, "field-bound width/tolerance failed");
            using var associated = result.GetOutputImage("Width"); Require(associated.Info.Width == w, "measurement Result has no real image projection");
            await EditRoiAsync(); Require(ImageView.Roi.Shape == "rotatedRectangle", "edge pair ROI edit missing"); ImageView.EndEdit();
        }
        else if (scenario is "roi-annulus" or "roi-sector" or "roi-rotated")
        {
            MakeCircleSource(); bool line = scenario == "roi-rotated";
            RoiRegion roi = line ? RoiRegion.RotatedRectangle(180, 180, 160, 80, 30) : RoiRegion.Annulus(180, 180, 50, 78, scenario == "roi-sector" ? 300 : 0, scenario == "roi-sector" ? 540 : 360);
            if (line)
            {
                byte[] pixels = new byte[360 * 360]; for (int y = 0; y < 360; y++) for (int x = 0; x < 360; x++) pixels[y * 360 + x] = roi.Contains(x, y) ? (byte)230 : (byte)15;
                source!.Dispose(); source = VisionFrame.CopyFrom(pixels, 360, 360, 360, VisionPixelFormat.Gray8); ShowSource();
            }
            flow.Add("ExternalImage", 30, 90, "source"); flow.Add(line ? "LineCaliper" : "CircleCaliper", 300, 90, "detect"); flow.Connect("source", "image", "detect", "input");
            flow.SetParameter("detect", "caliperPlacement", JsonValue.Create("roiBoundary")); flow.SetParameter("detect", "polarity", JsonValue.Create("any"));
            Graph.Select("detect"); await DrawRegionAsync("detect", roi.Shape, false); ImageView.CommitRegion(roi, false); ImageView.EndEdit();
            flow.Publish("Detected", "detect", "result", false); await BusyAsync(RunOnceAsync);
            Require(line ? result!.GetResult<VisionLine>("Detected").Count == 1 : result!.GetResult<VisionCircle>("Detected").Count == 1, "advanced ROI did not produce real fitted geometry");
            RequireCaliperParity("detect", !line);
        }
        else if (scenario is "fixture-circle-distance" or "fixture-image-distance")
        {
            var at = MakeMatchedCircleSource(false);
            flow.Add("ExternalImage", 20, 100, "source"); flow.Add("TemplateMatch", 230, 100, "match");
            flow.Connect("source", "image", "match", "input");
            Graph.Select("match"); await DrawRegionAsync("match", "rectangle", true);
            ImageView.CommitRegion(RoiRegion.Rectangle(at.X, at.Y, 128, 96), true);
            await BusyAsync(() => LearnTemplateAsync("match"));
            foreach (string key in new[] { "angleMinimum", "angleMaximum" }) flow.SetParameter("match", key, JsonValue.Create(0.0));
            foreach (string key in new[] { "scaleMinimum", "scaleMaximum" }) flow.SetParameter("match", key, JsonValue.Create(1.0));
            flow.Add("PositionFixture", 470, 100, "fixture");
            if (scenario == "fixture-image-distance")
                flow.Connect("match", "image", "fixture", "input");
            else
            {
                flow.Connect("source", "image", "fixture", "input"); flow.Connect("match", "result", "fixture", "position");
            }
            await BusyAsync(() => TeachFixtureAsync("fixture"));
            foreach (var item in new[] { (Id: "a", X: at.X + 36, Radius: 18.0), (Id: "b", X: at.X + 94, Radius: 12.0) })
            {
                flow.Add("FindCircles", 730, item.Id == "a" ? 40 : 205, item.Id);
                flow.Connect("fixture", "result", item.Id, "input");
                flow.SetParameter(item.Id, "positionReference", JsonValue.Create("fixture"));
                flow.SetParameter(item.Id, "method", JsonValue.Create("caliper"));
                flow.SetParameter(item.Id, "polarity", JsonValue.Create("lightToDark"));
                Graph.Select(item.Id); await DrawRegionAsync(item.Id, "circle", false);
                ImageView.CommitRegion(RoiRegion.Circle(item.X, at.Y + 48, item.Radius), false); ImageView.EndEdit();
            }
            flow.Add("MeasureCircleDistance", 995, 110, "distance");
            flow.Connect("a", "result", "distance", "first"); flow.Connect("b", "result", "distance", "second");
            flow.Publish("Distance", "distance", "result", false); flow.Publish("CircleA", "a", "result", false);
            flow.Publish("Overlay", "distance", "overlay", false);
            Graph.Select("distance"); await BusyAsync(RunOnceAsync);
            Require(Math.Abs(result!.GetGeometryMeasurement("Distance").Value!.Value - 58) < .4, "reference measurement failed");
            byte[] taughtPixels = source!.CopyPixels(); var taughtInfo = source.Info;
            const double angle = 30, factor = 1.2;
            double radians = angle * Math.PI / 180, c = Math.Cos(radians), s = Math.Sin(radians);
            var transformed = Enumerable.Repeat((byte)15, taughtPixels.Length).ToArray();
            for (int y = 0; y < taughtInfo.Height; y++) for (int x = 0; x < taughtInfo.Width; x++)
            {
                double dx = (x - 280) / factor, dy = (y - 200) / factor;
                double rx = at.X + 64 + c * dx + s * dy, ry = at.Y + 48 - s * dx + c * dy;
                int ix = (int)Math.Floor(rx), iy = (int)Math.Floor(ry);
                if (ix < 0 || iy < 0 || ix + 1 >= taughtInfo.Width || iy + 1 >= taughtInfo.Height) continue;
                double fx = rx - ix, fy = ry - iy; int row = iy * taughtInfo.Stride;
                transformed[y * taughtInfo.Stride + x] = (byte)Math.Round((1 - fy) * ((1 - fx) * taughtPixels[row + ix] + fx * taughtPixels[row + ix + 1]) +
                    fy * ((1 - fx) * taughtPixels[row + taughtInfo.Stride + ix] + fx * taughtPixels[row + taughtInfo.Stride + ix + 1]));
            }
            source.Dispose(); source = VisionFrame.CopyFrom(transformed, taughtInfo.Width, taughtInfo.Height, taughtInfo.Stride, VisionPixelFormat.Gray8); ShowSource();
            foreach (string key in new[] { "angleMinimum", "angleMaximum" }) flow.SetParameter("match", key, JsonValue.Create(angle));
            foreach (string key in new[] { "scaleMinimum", "scaleMaximum" }) flow.SetParameter("match", key, JsonValue.Create(factor));
            Graph.Select("a"); await BusyAsync(RunOnceAsync);
            var a = result!.GetResult<VisionCircle>("CircleA");
            Require(a.Count == 1 && a.Measurement.Fixture is not null && Math.Abs(a.Features[0].X - (280 - 28 * factor * c)) < 1.2,
                "circle ROI did not follow rotated fixture");
            Require(Math.Abs(result.GetGeometryMeasurement("Distance").Value!.Value - 58 * factor) < 1.2, "scaled distance did not use real pixels");
            flow.SetParameter("distance", "toleranceEnabled", JsonValue.Create(true)); flow.SetParameter("distance", "lowerLimit", JsonValue.Create(68.0)); flow.SetParameter("distance", "upperLimit", JsonValue.Create(71.0));
            flow.Publish("Decision", "distance", "decision", true);
            await EditRoiAsync(); Require(FixtureActive("a"), "fixture input context not available for ROI editing");
            var roi = CurrentInputRoi("a"); var effective = FixturePositioning.Parameters(flow.Node("a")["parameters"]!.AsObject(), inputFixture!);
            var guides = CaliperLayout.Create(roi, effective, true, source.Info.Width, source.Info.Height, true);
            Require(guides.Count == a.Measurement.Caliper!.Guides.Count, "fixture caliper preview missing");
            for (int i = 0; i < guides.Count; i++)
                Require(Math.Abs(guides[i].X - a.Measurement.Caliper.Guides[i].X) < 1e-7 &&
                    Math.Abs(guides[i].Y - a.Measurement.Caliper.Guides[i].Y) < 1e-7 &&
                    guides[i].Width == a.Measurement.Caliper.Guides[i].Width && guides[i].Height == a.Measurement.Caliper.Guides[i].Height,
                    "fixture caliper preview/native mismatch");
            ImageView.EndEdit(); Graph.Select("distance");
        }
        else if (scenario == "geometry-distance")
        {
            var at = MakeMatchedCircleSource(false);
            flow.Add("ExternalImage", 35, 90, "source");
            flow.Add("FindCircles", 285, 40, "a");
            flow.Add("FindCircles", 285, 205, "b");
            flow.Add("MeasureCircleDistance", 560, 100, "distance");
            foreach (var circle in new[] { (Id: "a", X: at.X + 36, Radius: 18.0), (Id: "b", X: at.X + 94, Radius: 12.0) })
            {
                flow.Connect("source", "image", circle.Id, "input");
                flow.SetParameter(circle.Id, "method", JsonValue.Create("caliper"));
                flow.SetParameter(circle.Id, "positionReference", JsonValue.Create("image"));
                flow.SetParameter(circle.Id, "caliperPlacement", JsonValue.Create("roiBoundary"));
                flow.SetParameter(circle.Id, "roi", RoiRegion.Circle(circle.X, at.Y + 48, circle.Radius).ToJson());
                flow.SetParameter(circle.Id, "polarity", JsonValue.Create("lightToDark"));
                flow.SetParameter(circle.Id, "searchLength", JsonValue.Create(16.0));
            }
            flow.Connect("a", "result", "distance", "first");
            flow.Connect("b", "result", "distance", "second");
            flow.SetParameter("distance", "toleranceEnabled", JsonValue.Create(true));
            flow.SetParameter("distance", "lowerLimit", JsonValue.Create(57.0));
            flow.SetParameter("distance", "upperLimit", JsonValue.Create(59.0));
            flow.Publish("Distance", "distance", "result", false);
            flow.Publish("Decision", "distance", "decision", true);
            Graph.Select("distance"); await BusyAsync(RunOnceAsync);
            var measurement = result!.GetGeometryMeasurement("Distance");
            Require(result.Decision == InspectionDecision.Pass && measurement.Valid && Math.Abs(measurement.Value!.Value - 58) < .3,
                "two input circle distance/tolerance did not execute");
            Require(flow.Spec("distance").VisibleInputs.Count == 1 && flow.Spec("distance").VisibleOutputs.Count == 1,
                "measurement input/output sockets are incorrect");
            Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("58", StringComparison.Ordinal)),
                "distance is not directly displayed");
        }
        else if (scenario == "single-result")
        {
            MakeCircleSource();
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add("FindCircles", 280, 65, "detect");
            flow.Connect("source", "image", "detect", "input");
            flow.SetParameter("detect", "method", JsonValue.Create("caliper"));
            flow.SetParameter("detect", "roi", RoiRegion.Circle(180, 180, 65.5).ToJson());
            flow.SetParameter("detect", "minimumCount", JsonValue.Create(1));
            flow.SetParameter("detect", "maximumCount", JsonValue.Create(1));
            Require(flow.Spec("detect").VisibleOutputs.Count == 1 && flow.Spec("detect").VisibleOutputs[0].Type == "Image", "detector still shows separate output sockets");
            var listed = Toolbox.Children.OfType<System.Windows.Controls.Expander>().SelectMany(e => ((System.Windows.Controls.ListBox)e.Content).Items.OfType<NodeSpec>()).ToArray();
            Require(listed.All(n => n.ToolboxVisible) && !listed.Any(n => n.Type == "CropRect") && listed.Any(n => n.Type == "BuildContourOverlay"), "toolbox contains embedded ROI or hides unique contour drawing");
            Graph.Select("detect");
            await BusyAsync(RunOnceAsync);
            var packet = result!.GetResult<VisionCircle>("detect_result");
            Require(packet.Count == 1 && packet.Found && packet.Passed && packet.Measurement.SelectedValid && packet.Overlay.GetArrayLength() > 0, "single result socket does not contain the complete packet");
            Require(Math.Abs(packet.Features[0].Radius - 64) < 2, "packet geometry is missing or incorrect");
            Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.StartsWith("半径：", StringComparison.Ordinal)) &&
                Results.Children.OfType<System.Windows.Controls.Expander>().Any(e => Equals(e.Header, "测量与诊断")), "measurement is not immediately visible in the result panel");

            // Hidden components are still loadable, and existing typed wires share the visible result socket.
            flow.Add("CountFeatures", 530, 65, "count");
            flow.Add("CompareNumber", 760, 65, "judge");
            flow.Connect("detect", "result", "count", "input");
            flow.Connect("detect", "result", "judge", "input");
            flow.SetParameter("judge", "expected", JsonValue.Create(1));
            flow.Publish("AllResults", "detect", "result", false);
            flow.Publish("Decision", "judge", "value", true);
            Require(flow.Connections.Any(e => e!["fromNode"]!.GetValue<string>() == "detect" && e["fromPort"]!.GetValue<string>() == "features") &&
                flow.Connections.Any(e => e!["fromNode"]!.GetValue<string>() == "detect" && e["fromPort"]!.GetValue<string>() == "count"), "result connection did not resolve compatible fields");
            Graph.Select("detect");
            await BusyAsync(RunOnceAsync);
            Require(result!.Decision == InspectionDecision.Pass && result.GetNodeOutput<int>("count", "value") == 1, "projected result fields did not execute");
        }
        else if (scenario is "match-circle" or "match-center-circle" or "fast-full-image")
        {
            bool large = scenario == "fast-full-image";
            bool centered = scenario == "match-center-circle";
            var position = MakeMatchedCircleSource(large);
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add("FastTemplateMatch", 310, 65, "match");
            flow.Connect("source", "image", "match", "input");
            Graph.Select("match");
            await DrawRegionAsync("match", "rectangle", true);
            ImageView.CommitRegion(RoiRegion.Rectangle(position.X, position.Y, 128, 96), true);
            await BusyAsync(() => LearnTemplateAsync("match"));
            flow.SetParameter("match", "scoreThreshold", JsonValue.Create(.96));
            string model = flow.Node("match")["parameters"]!["model"]!.ToJsonString();
            await DrawRegionAsync("match", "rectangle", false);
            ImageView.CommitRegion(RoiRegion.Rectangle(position.X - 10, position.Y - 10, 148, 116), false);
            var searchChoice = InspectorControls<System.Windows.Controls.ComboBox>().First(c => c.Items.OfType<string>().Contains("全图"));
            searchChoice.SelectedItem = "全图";
            Require(flow.Node("match")["parameters"]!["roi"]!["shape"]!.GetValue<string>() == "none", "selecting full-image search did not clear the search ROI");
            Require(flow.Node("match")["parameters"]!["model"]!.ToJsonString() == model, "search ROI changed the taught template");
            KeepPreviews.IsChecked = false;
            await BusyAsync(RunOnceAsync);
            var match = result!.GetNodeResult<VisionMatch>("match");
            Require(match.Count == 1 && match.Measurement.Selected is { } selected && Math.Abs(selected.X - position.X) < 1 && Math.Abs(selected.Y - position.Y) < 1, "full-image matching did not locate the taught object");
            Require(ImageView.Info?.Width == (large ? 5120 : 600), "matcher preview showed its crop instead of the source search image");
            if (!large)
            {
                if (!centered) flow.SetParameter("match", "downstreamImageMode", JsonValue.Create("target"));
                flow.Add("FindCircles", 595, 65, "circle");
                if (!centered) flow.SetParameter("circle", "positionReference", JsonValue.Create("image"));
                flow.Connect("match", "result", "circle", "input");
                Require(flow.Connections.Last()!["fromPort"]!.GetValue<string>() == "image", "result socket did not send real pixels to circle detection");
                flow.SetParameter("circle", "method", JsonValue.Create("caliper"));
                flow.SetParameter("circle", "searchLength", JsonValue.Create(16.0));
                flow.SetParameter("circle", "caliperCount", JsonValue.Create(32));
                flow.SetParameter("circle", "polarity", JsonValue.Create("lightToDark"));
                Graph.Select("circle");
                await DrawRegionAsync("circle", centered ? "rectangle" : "circle", false);
                if (centered) Require(InspectorControls<System.Windows.Controls.ComboBox>().Any(c => c.Items.OfType<string>().SequenceEqual(new[] { "圆形", "圆环／扇环" })), "prepared matching input did not refresh the circular ROI selector");
                Require(ImageView.Info?.Width == (centered ? 600 : 128) && ImageView.Info.OriginX == (centered ? 0 : 63) && ImageView.Info.OriginY == (centered ? 0 : 71), "circle ROI editing used the wrong image or origin");
                ImageView.CommitRegion(RoiRegion.Circle(centered ? 99 : 36, centered ? 119 : 48, 18.5), false);
                if (centered) Require(flow.Node("circle")["parameters"]!["matchOffsetX"]!.GetValue<double>() == -28 && flow.Node("circle")["parameters"]!["matchOffsetY"]!.GetValue<double>() == 0, "drawing did not record match-center offsets");
                await BusyAsync(RunOnceAsync);
                var circle = result!.GetNodeResult<VisionCircle>("circle");
                Require(circle.Count == 1 && circle.Measurement.Selected is { } geometry && Math.Abs(geometry.CenterX - 99) < 1.5 && Math.Abs(geometry.CenterY - 119) < 1.5, "circle center did not return original-image coordinates");
                Require(ImageView.Info?.Width == (centered ? 600 : 128) && ImageView.Overlays?.GetArrayLength() > 0, "located image and circle overlay did not display together");
                if (centered)
                {
                    Require(circle.Measurement.Position is { CenterX: 127, CenterY: 119 } && circle.Features[0].OutputAnnulus is not null, "template center or output annulus not exported");
                    Require(Results.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.StartsWith("内半径：", StringComparison.Ordinal)), "output annulus not displayed immediately");
                }
                RequireCaliperParity("circle", true);
                await EditRoiAsync();
                Require(ImageView.Roi.Shape == "circle" && ImageView.Info?.OriginX == (centered ? 0 : 63), "located ROI could not be edited after execution");
                if (centered)
                {
                    MakeMatchedCircleSource(false, 220, 150);
                    await BusyAsync(RunOnceAsync);
                    var moved = result!.GetNodeResult<VisionCircle>("circle");
                    Require(moved.Measurement.Position is { CenterX: 284, CenterY: 198 } && Math.Abs(moved.Features[0].CenterX - 256) < 1.5 && Math.Abs(moved.Features[0].CenterY - 198) < 1.5, "next frame did not follow the current template center");
                    await EditRoiAsync();
                    Require(ImageView.Roi.X == 256 && ImageView.Roi.Y == 198, "ROI preview retained the previous frame position");
                    RequireCaliperParity("circle", true);
                    ImageView.CommitRegion(RoiRegion.Circle(-5, 50, 18.5), false);
                    Require(flow.Node("circle")["parameters"]!["roi"]!["cx"]!.GetValue<double>() == 0 && flow.Node("circle")["parameters"]!["matchOffsetX"]!.GetValue<double>() == -289, "outside ROI center was saved as an invalid static recipe");
                    await BusyAsync(RunOnceAsync);
                    var outside = result!.GetNodeResult<VisionCircle>("circle");
                    Require(outside.Count == 0 && outside.Measurement.Position?.SearchCenterX == -5, "edited outside ROI did not execute as an empty inspection");
                    await EditRoiAsync();
                    Require(ImageView.Roi.X == -5 && ImageView.CaliperParameters is not null && ImageView.AllowPartialCircleCalipers, "outside ROI preview lost its position or calipers");
                    ImageView.CommitRegion(RoiRegion.Circle(256, 198, 18.5), false);
                    await BusyAsync(RunOnceAsync);
                    ImageChoice.SelectedIndex = 1;
                    MakeCircleSource();
                    await BusyAsync(RunOnceAsync);
                    Require(result!.GetNodeResult<VisionCircle>("circle").Count == 0 && inputPosition is null && inputFrame is null, "original image mode kept a previous matched position");
                    ImageChoice.SelectedIndex = 0;
                    Require(ImageView.Info is null && ImageInfo.Text.Contains("没有有效"), "unmatched result showed a stale detector image");
                    MakeMatchedCircleSource(false, 220, 150);
                    await BusyAsync(RunOnceAsync);
                    await EditRoiAsync();
                    ImageView.BeginAdjust(false);
                }
            }
        }
        else if (scenario.StartsWith("fast-teaching", StringComparison.Ordinal))
        {
            MakePatternSource();
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add("FastTemplateMatch", 310, 65, "match");
            flow.Connect("source", "image", "match", "input");
            Graph.Select("match");
            string shape = scenario.EndsWith("circle", StringComparison.Ordinal) ? "circle" : scenario.EndsWith("polygon", StringComparison.Ordinal) ? "polygon" : "rectangle";
            var region = shape == "circle" ? RoiRegion.Circle(85, 93, 17) : shape == "polygon" ? RoiRegion.Polygon(new[] { new EditorPoint(61, 73), new EditorPoint(109, 73), new EditorPoint(61, 113) }) : RoiRegion.Rectangle(61, 73, 48, 40);
            await DrawRegionAsync("match", shape, true);
            ImageView.CommitRegion(region, true);
            await BusyAsync(() => LearnTemplateAsync("match"));
            Require(flow.Node("match")["parameters"]!["model"]!["width"]!.GetValue<int>() == (shape == "circle" ? 34 : 48), "built-in teaching did not store model");
            Graph.Select("source");
            Require(ImageView.Roi.Shape=="none"&&ImageView.Template.Shape=="none","selection retained a previous node's ROI or template");
            Graph.Select("match");
            await BusyAsync(RunOnceAsync);
            var measurement = result!.GetNodeMeasurement<VisionMatch>("match");
            Require(measurement.Count == 2 && measurement.SelectedValid, "fast teaching did not locate objects");
            var values = result.GetNodeOutput<JsonElement>("match", "features");
            Require(values.EnumerateArray().Any(v => v.GetProperty("x").GetDouble() == (shape == "circle" ? 68 : 61) && v.GetProperty("y").GetDouble() == (shape == "circle" ? 76 : 73)), "refined location wrong");
            var original = flow.Node("match")["parameters"]!["model"]!.ToJsonString();
            flow.SetParameter("match", "scoreThreshold", JsonValue.Create(.95));
            flow.Undo();
            Require(flow.Node("match")["parameters"]!["model"]!.ToJsonString() == original, "undo lost learned model");
            await BusyAsync(RunOnceAsync);
            await DrawRegionAsync("match", "rectangle", false);
            ImageView.CommitRegion(RoiRegion.Rectangle(20, 20, 430, 285), false);
            Require(ImageView.Roi.Shape == "rectangle", "rectangle ROI could not be edited before save");
            await BusyAsync(RunOnceAsync);
        }
        else if (scenario == "large-image")
        {
            MakeLargeImageSource();
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add("FindCircles", 310, 65, "detect");
            flow.Connect("source", "image", "detect", "input");
            flow.SetParameter("detect", "minimumRadius", JsonValue.Create(65));
            flow.SetParameter("detect", "maximumRadius", JsonValue.Create(120));
            flow.SetParameter("detect", "minimumDistance", JsonValue.Create(200.0));
            flow.SetParameter("detect", "voteThreshold", JsonValue.Create(20.0));
            Graph.Select("detect");

            flow.SetParameter("detect", "largeImageMode", JsonValue.Create("original"));
            using (var failed = await ExecuteAsync(flow.ExecutionRecipe(), "detect", false))
            {
                Require(failed.Status == ExecutionStatus.Failed, "original mode ignored its working-image budget");
                Error(new InvalidOperationException(failed.Data.GetProperty("error").ToString()));
                Require(StatusText.Text.Contains("圆查找") && StatusText.Text.Contains("5120") &&
                    StatusText.Text.Contains("检测 ROI") && !StatusText.Text.Contains("\"code\""),
                    "editor displayed raw native JSON instead of an actionable error");
            }

            flow.SetParameter("detect", "largeImageMode", JsonValue.Create("adaptive"));
            await BusyAsync(RunOnceAsync);
            var value = result!.GetNodeMeasurement<VisionCircle>("detect");
            var resolution = value.Detection ?? throw new InvalidOperationException("Smoke: missing detection resolution");
            Require(value.Count == 2 && resolution.Downsampled, "large image did not find both circles");
            Require(resolution.InputWidth == 5120 && resolution.InputHeight == 5120 &&
                (long)resolution.WorkWidth * resolution.WorkHeight <= 4194304, "working resolution exceeded budget");
            var circles = result.GetNodeOutput<JsonElement>("detect", "features");
            Require(circles.EnumerateArray().Any(c => Math.Abs(c.GetProperty("centerX").GetDouble() - 2157) < 5 &&
                Math.Abs(c.GetProperty("centerY").GetDouble() - 1639) < 5 && Math.Abs(c.GetProperty("radius").GetDouble() - 80) < 5),
                "large-image results were not returned in original pixel coordinates");
            Require(ImageView.Info?.Width == 5120 && ImageView.Info.Height == 5120 &&
                ImageView.Overlays?.EnumerateArray().Count(o => o.GetProperty("type").GetString() == "circle") == 2 &&
                ImageView.Overlays?.EnumerateArray().Count(o => o.GetProperty("type").GetString() == "annulus") == 2,
                "large-image preview was reduced or overlays were missing");
        }
        else if (scenario == "caliper")
        {
            MakeCircleSource();
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add("FindCircles", 310, 65, "circle");
            flow.Connect("source", "image", "circle", "input");
            flow.SetParameter("circle", "method", JsonValue.Create("caliper"));
            flow.SetParameter("circle", "caliperPlacement", JsonValue.Create("guide"));
            flow.SetParameter("circle", "radius", JsonValue.Create(64.0));
            flow.SetParameter("circle", "caliperCount", JsonValue.Create(48));
            flow.SetParameter("circle", "removePointCount", JsonValue.Create(2));
            Graph.Select("circle");
            await DrawRegionAsync("circle", "circle", false);
            ImageView.CommitRegion(RoiRegion.Circle(180, 180, 125), false);
            await BusyAsync(RunOnceAsync);
            var value = result!.GetNodeMeasurement<VisionCircle>("circle");
            Require(value.Count == 1 && value.Selected is not null && Math.Abs(value.Selected.Radius - 64) < 2, "circle calipers did not fit radius");
            Require(value.Caliper?.RejectedCount >= 2, "point removal not reported");
        }
        else if (scenario.StartsWith("caliper-boundary-", StringComparison.Ordinal))
        {
            bool circle = scenario.EndsWith("circle", StringComparison.Ordinal);
            bool polygon = scenario.EndsWith("polygon", StringComparison.Ordinal);
            if (circle) MakeCircleSource(); else MakeBoundaryLineSource(polygon);
            flow.Add("ExternalImage", 35, 65, "source");
            flow.Add(circle ? "FindCircles" : "LineCaliper", 310, 65, "detect");
            flow.Connect("source", "image", "detect", "input");
            if (circle) flow.SetParameter("detect", "method", JsonValue.Create("caliper"));
            Require(flow.Node("detect")["parameters"]!["caliperPlacement"]!.GetValue<string>() == "roiBoundary", "new editor node did not default to ROI boundary following");
            flow.SetParameter("detect", "caliperCount", JsonValue.Create(32));
            flow.SetParameter("detect", "searchLength", JsonValue.Create(32.0));
            Graph.Select("detect");
            var widthChoice = InspectorControls<System.Windows.Controls.ComboBox>().First(c => c.Items.OfType<string>().Contains("5 px"));
            Require(widthChoice.Items.OfType<string>().All(s => int.Parse(s.Split(' ')[0]) % 2 == 1), "width selector offered an even width");
            widthChoice.SelectedItem = "7 px";
            Require(flow.Node("detect")["parameters"]!["projectionWidth"]!.GetValue<int>() == 7, "width selection was not committed");

            var roi = circle ? RoiRegion.Circle(180, 180, 65.5) : polygon ? BoundaryPolygon() : RoiRegion.Rectangle(70, 80, 220, 80);
            await DrawRegionAsync("detect", roi.Shape, false);
            ImageView.CommitRegion(roi, false);
            var before = CaliperLayout.Create(roi, ImageView.CaliperParameters!, circle, ImageView.Info!.Width, ImageView.Info.Height);
            var moved = circle ? RoiRegion.Circle(186, 184, 67) : polygon ? RoiRegion.Polygon(new[] { new EditorPoint(72, 79), roi.Points[1], roi.Points[2], roi.Points[3] }) : roi.Translate(2, -1);
            // During an active drag rendering must use the current ROI before the mouse-up commit.
            ImageView.Roi = moved;
            var live = CaliperLayout.Create(ImageView.Roi, ImageView.CaliperParameters!, circle, ImageView.Info.Width, ImageView.Info.Height);
            Require(before.Count == 32 && live.Count == 32 && Math.Abs(before[0].X - live[0].X) + Math.Abs(before[0].Y - live[0].Y) > 1, "live ROI adjustment left the calipers behind");
            ImageView.CommitRegion(moved, false);
            if (!circle && !polygon)
            {
                foreach (string edge in new[] { "top", "right", "bottom", "left" })
                {
                    UpdateParameter("detect", "roiEdge", JsonValue.Create(edge));
                    await BusyAsync(RunOnceAsync);
                    Require(result!.GetNodeMeasurement<VisionLine>("detect").Found, "rectangle edge did not produce a real line: " + edge);
                    RequireCaliperParity("detect", false);
                    await EditRoiAsync();
                }
            }
            else
            {
                await BusyAsync(RunOnceAsync);
                Require(circle ? result!.GetNodeMeasurement<VisionCircle>("detect").Found : result!.GetNodeMeasurement<VisionLine>("detect").Found, "boundary calipers did not find the actual image edge");
                RequireCaliperParity("detect", circle);
            }
        }
        else
            throw new ArgumentException("Unknown smoke scenario: " + scenario);
        string saved = Path.Combine(Path.GetDirectoryName(CapturePath)!, Path.GetFileNameWithoutExtension(CapturePath) + "-saved.vrecipe");
        recipeDirectory = saved;
        SaveRecipe(false);
        var preservedLayout = flow.LayoutJson().ToJsonString();
        LoadRecipe(saved);
        Require(flow.LayoutJson().ToJsonString() == preservedLayout, "saved drawing/layout did not roundtrip");
        if (scenario is "single-result" or "large-inner-circle" or "caliper-refit" or "caliper-fit-failure") Graph.Select("detect");
        if (scenario.StartsWith("line-strip", StringComparison.Ordinal)) Graph.Select("line");
        if (scenario.StartsWith("line-distance", StringComparison.Ordinal)) Graph.Select("distance");
        if (scenario.StartsWith("circle-distance-", StringComparison.Ordinal) || scenario.StartsWith("line-circle-", StringComparison.Ordinal)) Graph.Select("distance");
        if (scenario.StartsWith("parameters-", StringComparison.Ordinal)) Graph.Select("tool");
        if (scenario.StartsWith("image-inline-", StringComparison.Ordinal)) Graph.Select("distance");
        if (scenario == "image-process-chain") Graph.Select("region");
        if (scenario is "annular-unwrap" or "parameter-subscriptions") Graph.Select("unwrap");
        if (scenario == "edge-points") Graph.Select("distance");
        if (scenario == "edge-reconstruction") Graph.Select("distance");
        if (scenario == "roi-bounds") Graph.Select("line");
        if (scenario == "edge-points-ranges") Graph.Select("edge");
        if (scenario == "line-density") Graph.Select("line");
        if (scenario == "blob-analysis") Graph.Select("blob");
        if (scenario is "camera-source" or "camera-main-live") Graph.Select("blob");
        await BusyAsync(RunOnceAsync);
        Require(result?.Status == ExecutionStatus.Succeeded, "reloaded saved flow did not run");
        if (scenario == "annular-unwrap") VerifyAnnularUnwrap();
        if (scenario == "edge-points") VerifyEdgePoints();
        if (scenario == "edge-reconstruction") VerifyEdgeReconstruction();
        if (scenario == "roi-bounds") VerifyRoiBounds();
        if (scenario == "edge-points-ranges") VerifyEdgePointRanges();
        if (scenario == "line-density") VerifyLineDensity();
        if (scenario == "blob-analysis") VerifyBlobAnalysis();
        if (scenario.StartsWith("image-inline-", StringComparison.Ordinal)) Require(result!.GetGeometricMeasurement("Distance").Valid, "saved A/B ROI did not roundtrip");
        if (scenario.StartsWith("line-strip", StringComparison.Ordinal))
            Require(result!.GetResult<VisionLine>("Line").Count == (scenario == "line-strip-empty" ? 0 : 1), "saved line strip did not roundtrip");
        if (scenario.StartsWith("line-distance", StringComparison.Ordinal))
            Require(result!.GetGeometricMeasurement("Distance").Valid == (scenario == "line-distance"), "saved line distance or its invalid status did not roundtrip");
        if (scenario.StartsWith("circle-distance-", StringComparison.Ordinal) || scenario.StartsWith("line-circle-", StringComparison.Ordinal))
        {
            var circular = result!.GetGeometricMeasurement("Distance");
            Require(circular.Valid == !scenario.EndsWith("empty", StringComparison.Ordinal) && circular.DistanceMode == (scenario.EndsWith("center", StringComparison.Ordinal) ? "centerDistance" : "edgeDistance"), "saved circular distance mode or validity did not roundtrip");
        }
        if (scenario is "caliper-refit" or "caliper-fit-failure")
        {
            var packet = result!.GetResult<VisionCircle>("Circle"); bool success = scenario == "caliper-refit";
            Require(packet.Count == (success ? 1 : 0) && packet.Measurement.Caliper?.FitSucceeded == success, "saved fit acceptance changed after reload");
        }
        if (scenario == "large-inner-circle")
        {
            var circle = result!.GetResult<VisionCircle>("Circle").Features.Single();
            Require(Math.Abs(circle.Radius - 1400) < .5 && result.GetOutput<long>("Count") == 1, "saved large hole setup did not roundtrip");
        }
        if (scenario == "caliper-result-annulus")
        {
            var circle = result!.GetResult<VisionCircle>("Circle"); var band = result.GetOverlays("MarkedOverlay").Single(o => o.Kind == "annulus");
            Require(circle.Features[0].EdgePoints.Count == 36 && circle.Measurement.Caliper!.RejectedCount == 4 && Math.Abs(band.OuterRadius - band.InnerRadius - 24) < 1e-8, "saved fitted annulus did not roundtrip");
        }
        if (scenario is "fixture-circle-distance" or "fixture-image-distance") Require(result!.Decision == InspectionDecision.Pass, "reloaded fixture/tolerance chain failed");
        if (scenario == "match-circle")
        {
            Require(result!.GetResult<VisionCircle>("circle_result").Count == 1 && ImageView.Info?.OriginX == 63, "saved matcher-circle chain did not preserve its pixel context");
            RequireCaliperParity("circle", true);
        }
        if (scenario == "fast-full-image")
            Require(result!.GetResult<VisionMatch>("match_result").Count == 1 && ImageView.Info?.Width == 5120, "saved full-image matcher did not preserve its search range");
        if (scenario == "single-result")
            Require(result!.GetResult<VisionCircle>("AllResults").Count == 1 && result.GetNodeResult<VisionCircle>("detect").Found, "saved aggregate output did not roundtrip");
        Graph.Fit();
        InspectorTabs.SelectedIndex = 1;
        if (scenario.StartsWith("parameters-", StringComparison.Ordinal))
        {
            VerifyParameterDetails(scenario);
            InspectorTabs.SelectedIndex = 0;
        }
        if (scenario == "line-strip-roi") { await EditRoiAsync(); InspectorTabs.SelectedIndex = 0; }
        if (scenario.StartsWith("roi-", StringComparison.Ordinal)) { await EditRoiAsync(); InspectorTabs.SelectedIndex = 0; }
        if (scenario.StartsWith("caliper-boundary-", StringComparison.Ordinal))
        {
            RequireCaliperParity("detect", scenario.EndsWith("circle", StringComparison.Ordinal));
            await EditRoiAsync();
            InspectorTabs.SelectedIndex = 0;
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (scenario.StartsWith("parameters-", StringComparison.Ordinal))
        {
            var group = InspectorControls<System.Windows.Controls.Expander>().First(e => e.Tag is string tag && (scenario != "parameters-caliper" ? tag.StartsWith("group:", StringComparison.Ordinal) : tag == "group:边缘提取"));
            group.BringIntoView(new Rect(0, 0, 1, 1));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (scenario is "annular-unwrap" or "parameter-subscriptions" or "roi-bounds" or "edge-points-ranges" or "line-density")
        {
            InspectorTabs.SelectedIndex = 0;
            if (InspectorTabs.Items[0] is System.Windows.Controls.TabItem tab && tab.Content is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToTop();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        CaptureWindow(scenario);
        if (scenario.StartsWith("fixture-", StringComparison.Ordinal) || scenario.StartsWith("line-distance", StringComparison.Ordinal) || scenario.StartsWith("circle-distance-", StringComparison.Ordinal) || scenario.StartsWith("line-circle-", StringComparison.Ordinal))
        {
            var info = source!.Info; var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Gray8, null, source.CopyPixels(), info.Stride)));
            using var stream = File.Create(Path.ChangeExtension(CapturePath, ".input.png")); encoder.Save(stream);
        }
        allowClose = true;
        Application.Current.Shutdown();
    }
    private void MakePatternSource()
    {
        const int w = 480, h = 320;
        byte[] pixels = Enumerable.Repeat((byte)12, w * h).ToArray();
        foreach (var at in new[] { (61, 73), (317, 211) })
        for (int y = 0; y < 40; y++)
        for (int x = 0; x < 48; x++)
            pixels[(at.Item2 + y) * w + at.Item1 + x] = (byte)(30 + (13 * x + 17 * y + 3 * x * y) % 200);
        source?.Dispose();
        source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8);
        ShowSource();
    }
    private EditorPoint MakeMatchedCircleSource(bool large, int? objectX = null, int? objectY = null)
    {
        int width = large ? 5120 : 600, height = large ? 5120 : 400, left = objectX ?? (large ? 4883 : 63), top = objectY ?? (large ? 4901 : 71);
        byte[] pixels = Enumerable.Repeat((byte)15, width * height).ToArray();
        for (int y = 0; y < 96; y++) for (int x = 0; x < 128; x++)
        {
            byte value = 15;
            if (x < 7 || x >= 121 || y < 7 || y >= 89) value = (byte)(30 + (x * 13 + y * 17 + x * y * 3) % 200);
            if ((x - 36) * (x - 36) + (y - 48) * (y - 48) <= 18 * 18 || (x - 94) * (x - 94) + (y - 48) * (y - 48) <= 12 * 12) value = 230;
            pixels[(top + y) * width + left + x] = value;
        }
        source?.Dispose();
        source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8);
        if (large)
        {
            string path = Path.Combine(Path.GetDirectoryName(CapturePath)!, Path.GetFileNameWithoutExtension(CapturePath) + "-input-5120.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            LoadImage(path);
        }
        else ShowSource();
        return new(left, top);
    }
    private static RoiRegion BoundaryPolygon() => RoiRegion.Polygon(new[] { new EditorPoint(70, 80), new EditorPoint(290, 135), new EditorPoint(270, 200), new EditorPoint(50, 145) });
    private void MakeBoundaryLineSource(bool polygon)
    {
        const int w = 360, h = 240;
        var roi = polygon ? BoundaryPolygon() : RoiRegion.Rectangle(70, 80, 220, 80);
        byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            pixels[y * w + x] = roi.Contains(x, y) ? (byte)230 : (byte)15;
        source?.Dispose();
        source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8);
        ShowSource();
    }
    private void RequireCaliperParity(string id, bool circle)
    {
        var stats = circle ? result!.GetNodeMeasurement<VisionCircle>(id).Caliper : result!.GetNodeMeasurement<VisionLine>(id).Caliper;
        var frame = ImageView.Info!;
        var parameters = flow!.Node(id)["parameters"]!.AsObject();
        var roi = RoiRegion.FromJson(parameters["roi"]);
        if (result!.Data.GetProperty("measurements").GetProperty(id).TryGetProperty("position", out var reference))
        {
            roi = RoiRegion.Circle(reference.GetProperty("searchCenterX").GetDouble() - frame.OriginX,
                reference.GetProperty("searchCenterY").GetDouble() - frame.OriginY, roi.Shape == "circle" ? roi.Radius : parameters["matchSearchRadius"]!.GetValue<double>());
            parameters = parameters.DeepClone().AsObject(); parameters["caliperPlacement"] = "roiBoundary";
        }
        var expected = CaliperLayout.Create(roi, parameters, circle, frame.Width, frame.Height, allowPartialCircle: true);
        Require(stats?.Placement == (parameters["caliperPlacement"]?.GetValue<string>() == "roiCenter" ? "roiCenter" : "roiBoundary") && stats.Guides.Count == expected.Count && expected.Count > 0, "missing native sampling-window diagnostics");
        for (int i = 0; i < expected.Count; i++)
        {
            var a = expected[i]; var b = stats!.Guides[i];
            Require(Math.Abs(a.X + frame.OriginX - b.X) < 1e-8 && Math.Abs(a.Y + frame.OriginY - b.Y) < 1e-8 &&
                Math.Abs(a.NormalX - b.NormalX) < 1e-8 && Math.Abs(a.NormalY - b.NormalY) < 1e-8 && a.Width == b.Width && a.Height == b.Height,
                "preview/native sampling-window mismatch at index " + i);
        }
    }
    private void MakeLargeHoleSource()
    {
        const int w = 5120, h = 5120; byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double dx = x - 2560, dy = y - 2560, r = Math.Sqrt(dx * dx + dy * dy), angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            double holeRadius = Math.Abs(Math.IEEERemainder(angle, 90)) < 2 ? 1420 : 1400;
            pixels[y * w + x] = r > 2200 ? (byte)245 : (byte)Math.Clamp(160 + (holeRadius + .5 - r) * 80, 160, 240);
        }
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI / 12; int cx = (int)Math.Round(2560 + 1950 * Math.Cos(angle)), cy = (int)Math.Round(2560 + 1950 * Math.Sin(angle));
            for (int y = cy - 51; y <= cy + 51; y++) for (int x = cx - 51; x <= cx + 51; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= 50 * 50) pixels[y * w + x] = 15;
        }
        string path = Path.ChangeExtension(CapturePath, ".input.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, pixels, w)));
        using (var stream = File.Create(path)) encoder.Save(stream);
        LoadImage(path); Require(source?.Info.Width == w && source.Info.Height == h, "large hole image import changed dimensions");
    }
    private void MakeCircleSource()
    {
        const int w = 360, h = 360;
        byte[] pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double distance = Math.Sqrt((x - 180) * (x - 180) + (y - 180) * (y - 180));
            pixels[y * w + x] = (byte)Math.Clamp((64.5 - distance) * 255, 0, 255);
        }
        source?.Dispose();
        source = VisionFrame.CopyFrom(pixels, w, h, w, VisionPixelFormat.Gray8);
        ShowSource();
    }
    private void MakeLargeImageSource()
    {
        const int w = 5120, h = 5120;
        byte[] pixels = new byte[w * h];
        foreach (var at in new[] { (2157, 1639, 80), (3219, 3573, 105) })
        for (int y = at.Item2 - at.Item3 - 2; y <= at.Item2 + at.Item3 + 2; y++)
        for (int x = at.Item1 - at.Item3 - 2; x <= at.Item1 + at.Item3 + 2; x++)
            if ((x - at.Item1) * (x - at.Item1) + (y - at.Item2) * (y - at.Item2) <= at.Item3 * at.Item3)
                pixels[y * w + x] = 240;
        string path = Path.Combine(Path.GetDirectoryName(CapturePath)!, Path.GetFileNameWithoutExtension(CapturePath) + "-input-5120.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, pixels, w)));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        LoadImage(path);
        Require(source?.Info.Width == w && source.Info.Height == h, "large PNG import changed input dimensions");
    }
    private void CaptureWindow(string scenario)
    {
        UpdateLayout();
        Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(CapturePath))
            encoder.Save(stream);
        var record = new
        {
            scenario,
            licensed,
            nodes = flow?.Nodes.Count,
            selected = Graph.SelectedId,
            image = ImageInfo.Text,
            nodeState = NodeState.Text,
            status = StatusText.Text,
            executionStatus = result?.Status.ToString(),
            decision = result?.Decision.ToString(),
            measurement = result?.Data.GetProperty("measurements"),
            outputs = result?.Data.GetProperty("nodeOutputs"),
            savedRecipe = recipeDirectory
        };
        File.WriteAllText(CapturePath + ".json", JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void WriteSmokeFailure(Exception ex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
        File.WriteAllText(CapturePath + ".error.txt", ex.ToString());
    }
}

using JPVision.Editor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private readonly Dictionary<string, string> measurementSides = new();

    private bool HasMeasurementRois(string id) => flow!.Spec(id).Parameters.Any(p => p?["name"]?.GetValue<string>() == "firstRoi");
    private string MeasurementSide(string id) => measurementSides.GetValueOrDefault(id, "first");
    private string MeasurementObject(string id, string side)
    {
        string type = flow!.Spec(id).Type;
        if (type == "MeasureGeometry")
        {
            string operation = EffectiveParameters(id)["operation"]?.GetValue<string>() ?? "pointDistance";
            return operation == "pointDistance" || operation == "pointToLine" && side == "first"
                ? EffectiveParameters(id)[side + "ObjectType"]?.GetValue<string>() ?? "circle" : "line";
        }
        return type == "MeasureCircleDistance" ? "circle" : type == "MeasureLineCircleDistance" ? side == "first" ? "line" : "circle"
            : "line";
    }
    private bool MeasurementFixtureActive(string id) => inputFrame is not null && inputNode == id && inputFixture is not null &&
        (flow!.Node(id)["parameters"]?[MeasurementSide(id) + "RoiReference"]?.GetValue<string>() == "fixture" ||
         flow.Node(id)["parameters"]?[MeasurementSide(id) + "Roi"]?["coordinateSpace"]?.GetValue<string>() == "reference");
    private RoiRegion MeasurementRoi(string id)
    {
        var roi = RoiRegion.FromJson(EffectiveParameters(id)[MeasurementSide(id) + "Roi"]);
        if (!MeasurementFixtureActive(id)) return roi;
        if (roi.ReferenceHash is not null && roi.ReferenceHash != inputFixture!.ReferenceHash) return RoiRegion.None;
        return FixturePositioning.Resolve(roi, new JsonObject { ["caliperPlacement"] = MeasurementObject(id, MeasurementSide(id)) == "circle" ? "roiBoundary" : "roiCenter" }, inputFixture!, inputFrame!.Info);
    }
    private void CommitMeasurementRoi(string id, RoiRegion region)
    {
        EnsureManualRoi(id);
        string side = MeasurementSide(id);
        bool circle = MeasurementObject(id, side) == "circle";
        if (region.Shape != "none" && (circle ? region.Shape is not ("circle" or "annulus") : region.Shape is not ("rectangle" or "rotatedRectangle" or "polygon")))
            throw new ArgumentException(circle ? "圆测量请绘制圆形或圆环 ROI。" : "直线测量请绘制直线卡尺搜索带。");
        flow!.SetParameter(id, side + "Roi", (MeasurementFixtureActive(id) && region.Shape != "none" ? FixturePositioning.Store(region, inputFixture!, inputFrame!.Info) : region).ToJson());
        ImageView.Roi = region;
        SetMeasurementCaliperGuides(id);
    }
    private void SelectMeasurementSide(string id, string side)
    {
        ImageView.EndEdit();
        measurementSides[id] = side;
        if (inputNode == id && inputFrame is not null) ImageView.Roi = MeasurementRoi(id);
        BuildInspector();
        SetMeasurementCaliperGuides(id);
    }
    private void BuildMeasurementRoiPanel(string id)
    {
        Header(Parameters, "模块内检测 A / B");
        Parameters.Children.Add(Note("连接图像后，分别绘制 A、B 的检测 ROI。模块从真实图像采样、剔除异常点并拟合，再计算距离；不会把绘制的 ROI 当成检测结果。"));
        var sides = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string side in new[] { "first", "second" })
        {
            var choose = new Button { Content = (side == "first" ? "对象 A" : "对象 B") + (MeasurementSide(id) == side ? " · 当前" : ""), Tag = "measurement-side:" + side, Margin = new Thickness(0, 0, 6, 6) };
            choose.Click += (_, _) => Safe(() => SelectMeasurementSide(id, side));
            sides.Children.Add(choose);
        }
        Parameters.Children.Add(sides);
        string selected = MeasurementSide(id);
        var parameters = flow!.Node(id)["parameters"]!.AsObject();
        if (flow.Spec(id).Type == "MeasureGeometry")
        {
            var objectParameter = flow.Spec(id).Parameters.OfType<JsonObject>().FirstOrDefault(p => p["name"]?.GetValue<string>() == selected + "ObjectType");
            if (objectParameter is not null) BuildParameter(id, objectParameter);
        }
        bool circle = MeasurementObject(id, selected) == "circle";
        var referenceParameter = flow.Spec(id).Parameters.OfType<JsonObject>().FirstOrDefault(p => p["name"]?.GetValue<string>() == selected + "RoiReference");
        if (referenceParameter is not null) BuildParameter(id, referenceParameter);
        var region = MeasurementRoi(id);
        Parameters.Children.Add(Note((selected == "first" ? "A" : "B") + "：" + (circle ? "圆卡尺拟合" : "直线卡尺拟合") + (region.Shape == "none" ? " · 尚未绘制 ROI" : " · " + region.Shape), false));
        if (!circle) BuildLineSamplingSummary(id, region, MeasurementCaliperParameters(id), "line:" + selected);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var draw = new Button { Content = "绘制 / 重画", Tag = "measurement-draw:" + selected, Margin = new Thickness(0, 0, 5, 5) };
        var adjust = new Button { Content = "调整", Tag = "measurement-adjust:" + selected, IsEnabled = region.Shape != "none", Margin = new Thickness(0, 0, 5, 5) };
        var clear = new Button { Content = "清除", Tag = "measurement-clear:" + selected, Margin = new Thickness(0, 0, 0, 5) };
        var shape = new ComboBox { ItemsSource = circle ? new[] { "圆形", "圆环／扇环" } : new[] { "直线卡尺搜索带", "多边形选边" }, SelectedIndex = region.Shape is "annulus" or "polygon" ? 1 : 0, Margin = new Thickness(0, 0, 0, 6), Tag = "measurement-shape:" + selected };
        if (RoiHasSubscriptions(id)) shape.IsEnabled = draw.IsEnabled = adjust.IsEnabled = clear.IsEnabled = false;
        Parameters.Children.Add(shape);
        draw.Click += async (_, _) => await DrawMeasurementRoiAsync(id, shape.SelectedIndex == 1 ? circle ? "annulus" : "polygon" : null);
        adjust.Click += async (_, _) => await EditRoiAsync();
        clear.Click += (_, _) => Safe(() => CommitMeasurementRoi(id, RoiRegion.None));
        buttons.Children.Add(draw); buttons.Children.Add(adjust); buttons.Children.Add(clear); Parameters.Children.Add(buttons);
        Parameters.Children.Add(Note(circle ? "圆周决定卡尺布置位置；搜索高度跨越圆边缘。拟合圆由采到的有效点生成。" : "拖动两端可旋转和伸缩；搜索带高度决定卡尺搜索范围。运行后输出拟合直线。"));
        BuildRoiCoordinateEditors(id, selected + "Roi", region);
        string[] detectorKeys = { "CaliperCount", "ProjectionWidth", "SearchLength", "Sigma", "EdgeThreshold", "Polarity", "EdgeSelection", "RemovePointCount", "MaximumDeviation", "MinimumCoverage", "ProjectionStatistic", "SubpixelRefinement", "RoiEdgeIndex", "ReverseSearch" };
        var details = new StackPanel();
        foreach (string key in detectorKeys)
        {
            if (key == "RoiEdgeIndex" && region.Shape != "polygon" || !circle && region.Shape != "polygon" && key == "SearchLength" || circle && key == "ReverseSearch") continue;
            var parameter = flow.Spec(id).Parameters.OfType<JsonObject>().FirstOrDefault(p => p["name"]?.GetValue<string>() == selected + key);
            if (parameter is not null) BuildParameter(id, parameter, details);
        }
        Parameters.Children.Add(new Expander { Header = (selected == "first" ? "A" : "B") + " 检测参数", IsExpanded = true, Content = details });
    }
    private async Task DrawMeasurementRoiAsync(string id, string? shape = null)
    {
        await BusyAsync(async () =>
        {
            EnsureManualRoi(id);
            await PrepareInputAsync(id);
            bool circle = MeasurementObject(id, MeasurementSide(id)) == "circle";
            ImageView.LineDrawHeight = MeasurementRoi(id).Shape is "rectangle" or "rotatedRectangle" ? MeasurementRoi(id).Height : 40;
            ImageView.BeginDraw(shape ?? (circle ? "circle" : "lineCaliper"), false, region =>
            {
                retainInput = true;
                try { CommitMeasurementRoi(id, region); BuildInspector(); }
                finally { retainInput = false; }
            });
            Log("绘制测量对象 " + (MeasurementSide(id) == "first" ? "A" : "B") + " 的卡尺 ROI。");
        });
    }
    private void SetMeasurementCaliperGuides(string id)
    {
        bool circle = MeasurementObject(id, MeasurementSide(id)) == "circle";
        var roi = MeasurementRoi(id);
        var proxy = MeasurementCaliperParameters(id);
        bool fixtured = MeasurementFixtureActive(id);
        ImageView.EdgePairCalipers = false; ImageView.LineStrip = !circle && roi.Shape != "polygon"; ImageView.CircleCalipers = circle;
        ImageView.CaliperScale = fixtured ? inputFixture!.ScaleRatio : 1; ImageView.AllowPartialCircleCalipers = fixtured;
        ImageView.CaliperParameters = roi.Shape == "none" ? null : proxy;
        ImageView.InvalidateVisual();
    }
    private JsonObject MeasurementCaliperParameters(string id)
    {
        string side = MeasurementSide(id);
        bool circle = MeasurementObject(id, side) == "circle";
        var stored = EffectiveParameters(id);
        var roi = MeasurementRoi(id);
        var proxy = new JsonObject { ["caliperPlacement"] = circle || roi.Shape == "polygon" ? "roiBoundary" : "roiCenter", ["roi"] = roi.ToJson(), ["reverseSearch"] = false };
        foreach (string key in new[] { "caliperCount", "projectionWidth", "searchLength", "sigma", "edgeThreshold", "polarity", "edgeSelection", "removePointCount", "maximumDeviation", "minimumCoverage", "projectionStatistic", "subpixelRefinement", "roiEdgeIndex", "reverseSearch" })
            if (stored[side + char.ToUpperInvariant(key[0]) + key[1..]] is JsonNode value) proxy[key] = value.DeepClone();
        if (MeasurementFixtureActive(id)) proxy = FixturePositioning.Parameters(proxy, inputFixture!);
        if (!circle && roi.Shape is "rectangle" or "rotatedRectangle") proxy["searchLength"] = roi.Height;
        return proxy;
    }
    private static bool TryPipelineOverlay(JsonElement ports, out JsonElement overlay)
    {
        overlay = default;
        return ports.TryGetProperty("image", out var image) && image.TryGetProperty("pipeline", out var pipeline)
            && pipeline.TryGetProperty("overlay", out overlay) && overlay.ValueKind == JsonValueKind.Array;
    }
}

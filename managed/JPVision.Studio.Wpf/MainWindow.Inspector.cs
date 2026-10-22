using JPVision.Editor.Core;
using JPVision.Sdk;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

public partial class MainWindow
{
    private static readonly Dictionary<string, string> EnumLabels = new() { ["adaptive"] = "自动缩小工作图", ["original"] = "原图（配合 ROI）", ["automatic"] = "自动找圆", ["caliper"] = "卡尺拟合", ["none"] = "全图", ["rectangle"] = "矩形", ["circle"] = "圆形", ["polygon"] = "多边形", ["darkToLight"] = "暗 → 亮", ["lightToDark"] = "亮 → 暗", ["either"] = "任意极性", ["strongest"] = "最强边缘", ["first"] = "第一条边缘", ["last"] = "最后一条边缘", ["erode"] = "腐蚀", ["dilate"] = "膨胀", ["open"] = "开运算", ["close"] = "闭运算", ["gradient"] = "形态学梯度", ["tophat"] = "顶帽", ["blackhat"] = "黑帽", ["rect"] = "矩形", ["ellipse"] = "椭圆", ["cross"] = "十字" };
    private static string EnumText(string value) => value switch
    {
        "automatic" => "自动找圆（多候选）",
        "caliper" => "卡尺拟合（单圆测量）",
        "guide" => "手动指引",
        "roiBoundary" => "跟随 ROI 边界",
        "roiCenter" => "直线卡尺搜索带中心",
        "top" => "上边",
        "right" => "右边",
        "bottom" => "下边",
        "left" => "左边",
        "any" => "任意极性",
        "raw" => "原始边缘（不修复）",
        "filter" => "剔除跳点与平滑",
        "reconstruct" => "剔除跳点并补全曲线",
        "measuredOnly" => "实测合格原始点（默认）",
        "all" => "修正／补全点集",
        "luminance" => "亮度加权（默认）",
        "blue" => "蓝色 B 通道",
        "green" => "绿色 G 通道",
        "red" => "红色 R 通道",
        "reflect101" => "镜像（不重复边界）",
        "reflect" => "镜像（重复边界）",
        "replicate" => "复制边界像素",
        "constant" => "常量边界",
        "fixed" => "固定阈值",
        "otsu" => "Otsu 自动阈值",
        "triangle" => "Triangle 自动阈值",
        "adaptiveMean" => "局部均值阈值",
        "adaptiveGaussian" => "局部高斯阈值",
        "mean" => "均值投影",
        "median" => "中位数投影",
        "clockwise" => "顺时针（起始角 → 终止角）",
        "counterclockwise" => "逆时针（终止角 → 起始角）",
        "innerToOuter" => "由内向外",
        "outerToInner" => "由外向内",
        "nearest" => "最近邻（保留像素值）",
        "linear" => "双线性（平滑采样）",
        "topHat" => "顶帽（亮细节）",
        "blackHat" => "黑帽（暗细节）",
        "auto" => "自动跟随上游匹配中心",
        "matchCenter" => "必须使用上游匹配中心",
        "image" => "输入图 ROI（手动定位）",
        "fixture" => "跟随位置修正（平移／旋转／等比缩放）",
        "source" => "原图＋定位中心（推荐）",
        "target" => "模板裁图＋定位中心",
        "limits" => "绝对上下限", "deviation" => "名义值＋上下偏差", "pointDistance" => "点点距离", "pointToLine" => "点到无限直线距离",
        "lineDistance" => "线线距离（平行边垂距）", "centerDistance" => "圆心距离", "edgeDistance" => "圆周最短间距", "lineAngle" => "两线最小夹角（0～90°）", "lineIntersection" => "无限直线交点", "closestWidth" => "最接近期望宽度", "center" => "中心点", "start" => "起点", "end" => "终点",
        _ => EnumLabels.GetValueOrDefault(value, value)
    };
    private TextBlock Note(string text, bool muted = true) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource(muted ? "Muted" : "Text"), Margin = new Thickness(0, 0, 0, 8), LineHeight = 19 };
    private void Header(StackPanel target, string title) => target.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 10) });
    private void BuildToolbox()
    {
        if (flow is null)
            return;
        Toolbox.Children.Clear();
        string query = SearchBox.Text.Trim();
        foreach (var group in flow.Catalog.All.Where(n => n.ToolboxVisible && (query.Length == 0 || n.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Type.Contains(query, StringComparison.OrdinalIgnoreCase))).GroupBy(n => n.Category))
        {
            var list = new ListBox { ItemsSource = group.ToArray(), DisplayMemberPath = "Label" };
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is NodeSpec spec) chosenType = spec; };
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is NodeSpec spec) Safe(() => AddNode(spec.Type, new(40 + (flow.Nodes.Count % 4) * 230, 45 + (flow.Nodes.Count / 4) * 190))); };
            Point start = new();
            list.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(list);
            list.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && list.SelectedItem is NodeSpec spec && (e.GetPosition(list) - start).Length > 8) DragDrop.DoDragDrop(list, new DataObject("JPVision.Component", spec.Type), DragDropEffects.Copy); };
            Toolbox.Children.Add(new Expander { Header = group.Key, IsExpanded = true, Content = list });
        }
    }
    private void BuildInspector()
    {
        if (Parameters is null || buildingInspector)
            return;
        buildingInspector = true;
        try
        {
            Parameters.Children.Clear();
            if (!licensed)
            {
                Header(Parameters, "软件已锁定");
                Parameters.Children.Add(Note("仅允许开发者为本机签发许可证的客户使用。请点击顶部“授权”导出机器请求并导入许可证。"));
                return;
            }
            if (flow is null || Graph.SelectedId is not string id)
            {
                Header(Parameters, "选择算子");
                Parameters.Children.Add(Note("从工具箱插入算子，连接相同类型的端口。执行后选中节点即可查看其输出图像和结果。"));
                BuildRunInputs();
                return;
            }
            var spec = flow.Spec(id);
            var effective = EffectiveParameters(id);
            Header(Parameters, spec.Label);
            var variables = new Button { Content = "变量与计算函数…", Tag = "variables:open", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3) };
            variables.Click += VariablesClick; Parameters.Children.Add(variables);
            BuildInputBindings(id);
            Parameters.Children.Add(Note(spec.Type + "  ·  " + id));
            if (spec.Type == "ExternalImage")
            {
                BuildGlobalSourcePanel();
                Parameters.Children.Add(Note("打开 PNG、BMP、JPEG 或 TIFF 将切换到文件图像。这个节点输出本次运行传入的全局图像。"));
                var button = new Button { Content = "打开外部图像", Margin = new Thickness(0, 0, 0, 8) };
                button.Click += OpenImageClick;
                Parameters.Children.Add(button);
                if (!useGlobalCamera && imagePath.Length > 0)
                    Parameters.Children.Add(Note(Path.GetFileName(imagePath)));
            }
            if (spec.Parameters.Any(p => p!["type"]!.GetValue<string>() == "template"))
                BuildTemplatePanel(id);
            if (spec.Type == "PositionFixture") BuildFixturePanel(id);
            if (spec.Type == "DeepLearningDefect") BuildDeepLearningModelPanel(id);
            if (spec.Type == "BlobAnalysis") Parameters.Children.Add(Note("在模块内二值化并分析连通区域，可按面积、尺寸、形状和接触边界筛选。面积为前景像素数；绿色区域标记独立显示，输出图像保留干净像素和当前坐标平面。"));
            if (spec.Type is "FindLines" or "LineCaliper" && IsCaliper(id))
                BuildLineSamplingSummary(id, CurrentInputRoi(id), FixtureActive(id) ? FixturePositioning.Parameters(effective, inputFixture!) : effective);
            if (spec.Type == "FindCircles") BuildCircleTargetPanel(id);
            if (spec.Type == "FindLines") BuildLineTargetPanel(id);
            bool lineDistance = spec.Type == "MeasureLineDistance" || spec.Type == "MeasureGeometry" && effective["operation"]?.GetValue<string>() == "lineDistance";
            if (lineDistance)
                Parameters.Children.Add(Note("连接图像并分别绘制 A、B 直线检测 ROI，也兼容旧配方的两个拟合直线输入。测量 A 拟合线中点到 B 无限直线的垂直距离；夹角超过平行角度容差则无效，不输出 0。当前单位为原图像素 px。"));
            if (spec.Type == "MeasureCircleDistance")
                Parameters.Children.Add(Note("连接图像并分别绘制 A、B 圆检测 ROI，也兼容旧配方的两个圆结果输入。默认测圆心距，也可切换圆周最短间距；相交时为 0，包含时为圆周之间的间隙。两种距离同时输出，单位为原图 px。"));
            if (spec.Type == "MeasureLineCircleDistance")
                Parameters.Children.Add(Note("连接图像后，A 绘制直线搜索带、B 绘制圆 ROI；也兼容旧配方的直线与圆结果输入。默认测圆心到无限直线的垂距，也可切换圆周到直线的最短间距；相交时边缘间距为 0。单位为原图 px。"));
            if (spec.Type == "MeasurePointLineDistances")
                Parameters.Children.Add(Note("输入图像携带“边缘点集”的本次结果；参考线可在“输入来源 → 参考线”选择直线查找输出。若同一图像已携带两类结果，可只连接图像输入。逐点输出到无限直线的垂距及最小／最大／平均距离等统计。"));
            if (IsEdgePointSampler(id)) BuildEdgePointRoiPanel(id);
            else if (IsAnnularUnwrap(id)) BuildAnnularRoiPanel(id);
            else if (HasMeasurementRois(id)) BuildMeasurementRoiPanel(id);
            else if (spec.Parameters.Any(p => p!["type"]!.GetValue<string>() == "roi"))
                BuildRoiPanel(id);
            if (spec.Parameters.Any(p => p!["type"]!.GetValue<string>() == "roi")) BuildRoiBoundsNote(id);
            Header(Parameters, "算法参数");
            var orderedParameters = spec.Type == "LineCaliper"
                ? spec.Parameters.OrderBy(p => p!["name"]!.GetValue<string>() switch { "caliperPlacement" => 0, "roiEdge" or "roiEdgeIndex" => 1, _ => 2 })
                : spec.Parameters.AsEnumerable();
            var visibleParameters = new List<JsonObject>();
            foreach (var parameter in orderedParameters)
            {
                string type = parameter!["type"]!.GetValue<string>(), key = parameter["name"]!.GetValue<string>();
                if (spec.Type == "DeepLearningDefect" && key is "modelRoot" or "modelKey") continue;
                if (type is "template" or "roi" or "fixtureReference")
                    continue;
                if (HasMeasurementRois(id) && (key.StartsWith("first", StringComparison.Ordinal) || key.StartsWith("second", StringComparison.Ordinal)) && key is not ("firstIndex" or "secondIndex" or "firstPoint" or "secondPoint")) continue;
                if (!ParameterEditor.Visible(spec.Type, key, effective)) continue;
                if (key == "parallelAngleToleranceDegrees" && !lineDistance) continue;
                if (spec.Type == "MeasureGeometry")
                {
                    string operation = effective["operation"]?.GetValue<string>() ?? "pointDistance";
                    if (key == "firstPoint" && operation is not ("pointDistance" or "pointToLine")) continue;
                    if (key == "secondPoint" && operation != "pointDistance") continue;
                }
                string toleranceMode = effective["toleranceMode"]?.GetValue<string>() ?? "limits";
                if ((key is "nominalValue" or "lowerDeviation" or "upperDeviation") && toleranceMode != "deviation") continue;
                if ((key is "lowerLimit" or "upperLimit") && toleranceMode == "deviation") continue;
                if (key == "downstreamPadding" && effective["downstreamImageMode"]?.GetValue<string>() == "source")
                    continue;
                if ((key is "matchOffsetX" or "matchOffsetY") && FixtureActive(id)) continue;
                if ((key is "matchOffsetX" or "matchOffsetY" or "matchSearchRadius") && (effective["positionReference"]?.GetValue<string>() ?? "image") == "image")
                    continue;
                if (key == "caliperPlacement" && MatchPositionActive(id)) continue;
                // Hide parameters for the inactive circle method, keeping the method selector visible.
                if (spec.Type == "FindCircles")
                {
                    if (key is "method" or "minimumRadius" or "maximumRadius") continue;
                    bool caliper = effective["method"]?.GetValue<string>() == "caliper";
                    string[] automatic = { "largeImageMode", "dp", "minimumDistance", "edgeThreshold", "voteThreshold", "minimumRadius", "maximumRadius", "preBlurKernel", "preBlurSigma" };
                    string[] fitted = { "caliperPlacement", "caliperCount", "projectionWidth", "searchLength", "removePointCount", "maximumDeviation", "minimumCoverage", "sigma", "polarity", "edgeSelection", "radius", "startAngle", "endAngle", "caliperThreshold", "projectionStatistic", "subpixelRefinement" };
                    if (caliper && automatic.Contains(key) || !caliper && fitted.Contains(key))
                        continue;
                }
                if (spec.Type == "FindLines")
                {
                    if (key == "method") continue;
                    bool caliper = IsCaliper(id);
                    string[] automatic = { "largeImageMode", "edgeLow", "edgeHigh", "voteThreshold", "minimumLength", "maximumGap", "edgeAperture", "edgeL2Gradient" };
                    string[] fitted = { "caliperPlacement", "caliperCount", "projectionWidth", "searchLength", "removePointCount", "maximumDeviation", "minimumCoverage", "sigma", "polarity", "edgeSelection", "angleDegrees", "guideLength", "edgeThreshold", "roiEdge", "roiEdgeIndex", "reverseSearch", "projectionStatistic", "subpixelRefinement" };
                    if (caliper && automatic.Contains(key) || !caliper && fitted.Contains(key)) continue;
                }
                if (IsLineStrip(id) && key is "searchLength" or "roiEdge" or "roiEdgeIndex" or "angleDegrees" or "guideLength") continue;
                bool follow = FollowsRoiBoundary(id);
                if (follow && (key is "radius" or "guideLength" or "angleDegrees"))
                    continue;
                if (key == "roiEdge" && (!follow || RoiRegion.FromJson(effective["roi"]).Shape is not ("rectangle" or "rotatedRectangle")))
                    continue;
                if (key == "roiEdgeIndex" && (!follow || RoiRegion.FromJson(effective["roi"]).Shape != "polygon"))
                    continue;
                visibleParameters.Add(parameter.AsObject());
            }
            BuildParameterGroups(id, visibleParameters);
            if (FollowsRoiBoundary(id))
                Parameters.Children.Add(Note(spec.Type == "LineCaliper"
                    ? "卡尺沿选定 ROI 边布置，箭头从区域内指向外。拖动 ROI 或顶点后同步更新；窗口在边线两侧搜索，图外采样不会参与拟合。"
                    : "绘制圆形 ROI 后，圆心与半径直接决定卡尺位置。拖动圆心或半径后同步更新；窗口在圆周两侧搜索，图外采样不会参与拟合。"));
            if (IsCaliper(id))
            {
                double sigma = double.Parse((effective["sigma"] ?? JsonValue.Create(1.0))!.ToJsonString(), CultureInfo.InvariantCulture);
                Parameters.Children.Add(Note($"当前投影平滑要求卡尺高度至少 {Math.Max(8, 6 * sigma + 6):0.##} px。"));
                var settings = effective;
                int count = int.Parse((settings["caliperCount"] ?? JsonValue.Create(24))!.ToJsonString(), CultureInfo.InvariantCulture);
                double coverage = double.Parse((settings["minimumCoverage"] ?? JsonValue.Create(.5))!.ToJsonString(), CultureInfo.InvariantCulture);
                int required = Math.Max(spec.Type is "FindCircles" or "CircleCaliper" ? 3 : 2, (int)Math.Ceiling(count * coverage));
                Parameters.Children.Add(Note($"当前 {count} 把卡尺，拟合至少保留 {required} 点；指定剔除最多 {count - required} 点。实际丢点或偏差筛选也会减少可用点数。"));
            }
            BuildRunInputs();
            if ((spec.Type is "FindLines" or "FindCircles") && !IsCaliper(id))
                Parameters.Children.Add(Note("大图默认自动缩小检测工作图，返回坐标和尺寸仍为原图像素。小目标或精确测量请缩小检测 ROI，并使用原图／卡尺模式。"));
            if (FixtureActive(id)) Parameters.Children.Add(Note("ROI 保存于教学参考图的原图坐标。显示和卡尺使用本次位置修正；重新教学后请确认参考区域。"));
            else if (spec.Type == "FindCircles" && (effective["positionReference"]?.GetValue<string>() ?? "image") != "image")
                Parameters.Children.Add(Note("连接匹配结果后，圆 ROI 和卡尺随本次模板中心移动。偏移用于模板中心与待测圆心不重合的物品；拟合圆心由边缘重新测量。当前偏移使用原图 X/Y 轴，不自动旋转或缩放。"));
            if (spec.Type is "FindCircles" or "CircleCaliper")
                Parameters.Children.Add(Note("卡尺模式会在剔除异常点后重新拟合圆；结果圆环围绕该圆心，内外半径为拟合半径 ± 带宽的一半。带宽为原图像素，设为 0 仅画圆周；大图缩小显示时可加宽或放大查看。"));
            Header(Parameters, "输出端口");
            foreach (var port in spec.VisibleOutputs)
                Parameters.Children.Add(Note(port.Type == "Result" ? "全部结果 · 几何数据、测量值、数量、判定及图形标记" : $"{port.Id}  ·  {port.Type}"));
            Parameters.Children.Add(Note(IsAnnularUnwrap(id) ? "执行后选择“算子输出”查看展开图；后续算子在独立的展开图像素坐标中工作。" : "执行后切换到“运行结果”；经过圆环展开的图像使用展开图像素坐标，其余结果使用原图坐标。"));
            SetCaliperGuides(id);
        }
        finally { buildingInspector = false; }
    }
    private void UpdateParameter(string id, string key, JsonNode? value)
    {
        EnsureEditor();
        retainInput = ImageView.EditEnabled && inputFrame is not null && inputNode == id;
        try
        {
            flow!.SetParameter(id, key, value);
            if (retainInput)
            {
                ImageView.Roi = CurrentInputRoi(id);
                SetCaliperGuides(id);
                ImageView.InvalidateVisual();
            }
        }
        finally { retainInput = false; }
    }
    private ComboBox ShapeChoice(string shape = "rectangle", bool includeNone = false, bool advanced = true)
    {
        var labels = (includeNone ? new[] { "全图" } : Array.Empty<string>()).Concat(new[] { "矩形", "圆形", "多边形" })
            .Concat(advanced ? new[] { "旋转矩形", "圆环／扇环" } : Array.Empty<string>()).ToArray();
        string selected = shape switch { "circle" => "圆形", "polygon" => "多边形", "annulus" => "圆环／扇环", "rotatedRectangle" => "旋转矩形", "none" => includeNone ? "全图" : "矩形", _ => "矩形" };
        var combo = new ComboBox { ItemsSource = labels, SelectedIndex = Array.IndexOf(labels, selected), Margin = new Thickness(0, 0, 0, 7) };
        return combo;
    }
    private static string ShapeValue(ComboBox combo) => combo.SelectedItem?.ToString() switch { "圆形" => "circle", "多边形" => "polygon", "圆环／扇环" => "annulus", "旋转矩形" => "rotatedRectangle", "全图" => "none", _ => "rectangle" };
    private bool IsCaliper(string id) => flow!.Spec(id).Type is "CircleCaliper" or "LineCaliper" or "MeasureEdgePair" ||
        (flow.Spec(id).Type is "FindCircles" or "FindLines") && EffectiveParameters(id)["method"]?.GetValue<string>() == "caliper";
    private bool IsLineStrip(string id) => IsCaliper(id) && flow!.Spec(id).Type is "FindLines" or "LineCaliper" && EffectiveParameters(id)["caliperPlacement"]?.GetValue<string>() == "roiCenter";
    private bool MatchPositionActive(string id) => !FixtureActive(id) && flow!.Spec(id).Type == "FindCircles" && inputNode == id && inputFrame is not null &&
        CirclePositioning.UsesMatch(EffectiveParameters(id), inputPosition);
    private bool FollowsRoiBoundary(string id) => IsCaliper(id) && !IsLineStrip(id) && (EffectiveParameters(id)["caliperPlacement"]?.GetValue<string>() == "roiBoundary" || MatchPositionActive(id) || FixtureActive(id));
    private RoiRegion CurrentInputRoi(string id)
    {
        if (HasMeasurementRois(id)) return MeasurementRoi(id);
        var parameters = EffectiveParameters(id);
        var roi = RoiRegion.FromJson(parameters["roi"]);
        if (FixtureActive(id)) return roi.ReferenceHash is not null && roi.ReferenceHash != inputFixture!.ReferenceHash
            ? RoiRegion.None : FixturePositioning.Resolve(roi, parameters, inputFixture!, inputFrame!.Info);
        return flow!.Spec(id).Type == "FindCircles" && inputNode == id && inputFrame is not null
            ? CirclePositioning.Resolve(roi, parameters, inputPosition, inputFrame.Info) : roi;
    }
    private void CommitRoi(string id, RoiRegion region, Action<JsonObject>? configure = null)
    {
        EnsureManualRoi(id);
        if (IsEdgePointSampler(id) && region.Shape is not ("rectangle" or "rotatedRectangle")) throw new ArgumentException("边缘点集需要矩形或旋转矩形搜索带。");
        if (IsAnnularUnwrap(id) && region.Shape != "annulus") throw new ArgumentException("圆环展开需要圆环或扇环 ROI。");
        if (HasMeasurementRois(id)) { CommitMeasurementRoi(id, region); return; }
        var parameters = flow!.Node(id)["parameters"]!.AsObject();
        if (FixtureActive(id))
        {
            flow.Change(() => { parameters["roi"] = FixturePositioning.Store(region, inputFixture!, inputFrame!.Info).ToJson(); configure?.Invoke(parameters); if (IsLineStrip(id)) parameters["searchLength"] = RoiRegion.FromJson(parameters["roi"]).Height; });
            ImageView.Roi = CurrentInputRoi(id); return;
        }
        bool located = flow.Spec(id).Type == "FindCircles" && inputNode == id && inputFrame is not null &&
            (region.Shape is "circle" or "annulus") && CirclePositioning.UsesMatch(parameters, inputPosition);
        var offset = located ? CirclePositioning.Offset(region, inputPosition!, inputFrame!.Info) : (0.0, 0.0);
        if (located && (Math.Abs(offset.Item1) > 32768 || Math.Abs(offset.Item2) > 32768))
            throw new ArgumentException("圆心相对模板中心偏移超出 ±32768 px。");
        flow.Change(() =>
        {
            parameters["roi"] = (located ? CirclePositioning.StoredCircle(region) : region).ToJson();
            if (located)
            {
                parameters["matchOffsetX"] = offset.Item1;
                parameters["matchOffsetY"] = offset.Item2;
            }
            configure?.Invoke(parameters);
            if (IsLineStrip(id)) parameters["searchLength"] = RoiRegion.FromJson(parameters["roi"]).Height;
        });
        if (inputNode == id && inputFrame is not null) ImageView.Roi = CurrentInputRoi(id);
    }
    private void BuildCircleTargetPanel(string id)
    {
        var spec = flow!.Spec(id);
        var p = EffectiveParameters(id);
        Header(Parameters, "测量目标");
        BuildParameter(id, spec.Parameters.OfType<JsonObject>().Single(v => v["name"]!.GetValue<string>() == "method"));
        var draw = new Button { Name = "DrawInnerCircle", Content = "绘制内孔圆（亮 → 暗）", Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(8) };
        draw.IsEnabled = !RoiHasSubscriptions(id);
        draw.Click += async (_, _) => await DrawInnerCircleAsync(id);
        Parameters.Children.Add(draw);
        Parameters.Children.Add(Note("测白色内孔：从孔中心拖到孔的边缘。完成后沿圆周布置卡尺、剔除异常点并拟合一个圆；Esc 取消。首次切换使用 64 把卡尺、剔除 4 点，参数可调。"));
        if (p["method"]?.GetValue<string>() != "caliper")
        {
            string Size(string key, double factor = 1)
            {
                var value = p[key] ?? spec.Parameters.OfType<JsonObject>().Single(v => v["name"]!.GetValue<string>() == key)["default"];
                return double.TryParse(value?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    ? (factor * n).ToString("0.##", CultureInfo.InvariantCulture) : "绑定值";
            }
            Parameters.Children.Add(Note($"自动模式当前范围：半径 {Size("minimumRadius")}～{Size("maximumRadius")} px（直径 {Size("minimumRadius", 2)}～{Size("maximumRadius", 2)} px），使用原图像素。请按目标尺寸设置范围。", false));
            Parameters.Children.Add(Note("全图自动找圆会返回范围内的多个候选，包括螺丝孔等小圆。测一个指定孔请使用上方绘制入口，或设置符合目标的半径范围。"));
            foreach (string key in new[] { "minimumRadius", "maximumRadius" })
                BuildParameter(id, spec.Parameters.OfType<JsonObject>().Single(v => v["name"]!.GetValue<string>() == key));
        }
        else Parameters.Children.Add(Note("单圆测量：ROI 圆周指定待测边缘，卡尺高度决定两侧搜索带。其他物品可修改极性；重画保留已调的采样参数。输出 0 或 1 个圆，三目标示例的预期数量需改为 1。"));
    }
    private async Task DrawInnerCircleAsync(string id)
    {
        await BusyAsync(async () =>
        {
            EnsureManualRoi(id);
            await PrepareInputAsync(id);
            ImageView.BeginDraw("circle", false, region => CommitInnerCircle(id, region));
            InspectorTabs.SelectedIndex = 0;
            Log("请从白色孔的中心拖到内孔边缘；只选择需要测量的那条圆周。Esc 取消，不修改配方。");
        });
    }
    private void CommitInnerCircle(string id, RoiRegion region) => Safe(() =>
    {
        retainInput = true;
        try
        {
            CommitRoi(id, region, ConfigureInnerCircle);
            ImageView.Roi = CurrentInputRoi(id); SetCaliperGuides(id); BuildInspector();
        }
        finally { retainInput = false; }
        Log("已选定内孔圆：沿 ROI 圆周向外搜索亮→暗边缘，剔除异常点后拟合单圆。点击执行查看圆心、直径和结果圆环；如仍使用三目标示例，请将预期数量改为 1。");
    });
    private static void ConfigureInnerCircle(JsonObject p)
    {
        bool initialize = p["method"]?.GetValue<string>() != "caliper";
        p["method"] = "caliper"; p["caliperPlacement"] = "roiBoundary";
        p["polarity"] = "lightToDark"; p["edgeSelection"] = "strongest";
        p["startAngle"] = 0.0; p["endAngle"] = 360.0;
        // Re-teaching changes the circle and polarity, preserving tuned sampling and fit settings.
        if (!initialize) return;
        double radius = RoiRegion.FromJson(p["roi"]).Radius;
        p["caliperCount"] = 64; p["projectionWidth"] = 5;
        p["searchLength"] = Math.Clamp(2 * Math.Ceiling(radius * .04), 60, 256);
        p["removePointCount"] = 4; p["maximumDeviation"] = Math.Clamp(radius * .002, 2, 8);
        p["minimumCoverage"] = .6; p["sigma"] = 1.0; p["caliperThreshold"] = 12.0;
        p["outputAnnulusWidth"] = Math.Clamp(radius * .02, 4, 32);
    }
    private void BuildLineTargetPanel(string id)
    {
        Header(Parameters, "直线测量");
        BuildParameter(id, flow!.Spec(id).Parameters.OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == "method"));
        var draw = new Button { Name = "DrawLineCaliper", Content = "绘制直线卡尺搜索带", Padding = new Thickness(8), Margin = new Thickness(0, 2, 0, 8) };
        draw.IsEnabled = !RoiHasSubscriptions(id);
        draw.Click += async (_, _) => await DrawLineCaliperAsync(id);
        Parameters.Children.Add(draw);
        Parameters.Children.Add(Note("从待测直线的起点拖到终点。两端控制点调整长度和角度，侧面控制点调整搜索高度；卡尺箭头指示选边方向，可反转搜索。"));
        Parameters.Children.Add(Note("运行后按实际边缘点拟合一条直线，直接输出端点、长度、角度、直线方程及 RMS。数量、宽度、剔点和极性在下方调整。"));
    }
    private async Task DrawLineCaliperAsync(string id)
    {
        await BusyAsync(async () =>
        {
            EnsureManualRoi(id);
            await PrepareInputAsync(id);
            var preview = EffectiveParameters(id);
            preview["caliperPlacement"] = "roiCenter";
            ImageView.LineStrip = true; ImageView.CircleCalipers = false; ImageView.CaliperParameters = preview;
            var roi = CurrentInputRoi(id);
            ImageView.LineDrawHeight = IsLineStrip(id) && roi.Shape is "rectangle" or "rotatedRectangle" ? roi.Height : 60 * ImageView.CaliperScale;
            ImageView.BeginDraw("lineCaliper", false, region => Safe(() =>
            {
                if (region.Shape != "rotatedRectangle") throw new ArgumentException("请从直线起点拖到终点，完成有效搜索带。");
                retainInput = true;
                try
                {
                    CommitRoi(id, region, p =>
                    {
                        foreach (var pair in flow!.Spec(id).Defaults()) if (!p.ContainsKey(pair.Key)) p[pair.Key] = pair.Value?.DeepClone();
                        if (flow.Spec(id).Type == "FindLines") p["method"] = "caliper";
                        p["caliperPlacement"] = "roiCenter";
                    });
                    ImageView.Roi = CurrentInputRoi(id); BuildInspector(); SetCaliperGuides(id);
                }
                finally { retainInput = false; }
                Log("直线卡尺 ROI 已更新：沿搜索带中心采样，跨两侧搜索边缘；点击执行显示拟合直线。");
            }));
            InspectorTabs.SelectedIndex = 0;
            Log("从待测边缘的起点拖到终点，完成后可调整两端或搜索高度；Esc 取消。");
        });
    }
    private void BuildRoiPanel(string id)
    {
        if (flow is null) return;
        bool strip = IsLineStrip(id);
        Header(Parameters, strip ? "直线卡尺搜索带" : "检测 ROI");
        var region = CurrentInputRoi(id);
        bool positioned = (flow!.Spec(id).Type == "FindCircles" && FixtureActive(id)) || (flow.Spec(id).Type == "FindCircles" && inputNode == id && inputFrame is not null &&
            CirclePositioning.UsesMatch(EffectiveParameters(id), inputPosition));
        var shape = ShapeChoice(region.Shape, includeNone: true);
        if (strip) { shape.ItemsSource = new[] { "旋转矩形搜索带" }; shape.SelectedIndex = 0; shape.IsEnabled = false; }
        if (flow.Spec(id).Type == "MeasureEdgePair") { shape.ItemsSource = new[] { "矩形", "旋转矩形" }; shape.SelectedIndex = region.Shape == "rotatedRectangle" ? 1 : 0; }
        if ((FollowsRoiBoundary(id) || positioned) && flow.Spec(id).Type != "MeasureEdgePair")
        {
            bool circle = flow.Spec(id).Type != "LineCaliper";
            shape.ItemsSource = circle ? new[] { "圆形", "圆环／扇环" } : new[] { "矩形", "旋转矩形", "多边形" };
            shape.SelectedIndex = circle ? region.Shape == "annulus" ? 1 : 0 : region.Shape == "polygon" ? 2 : region.Shape == "rotatedRectangle" ? 1 : 0;
        }
        Parameters.Children.Add(shape);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var draw = new Button { Content = "绘制／重画", Padding = new Thickness(9, 6, 9, 6) };
        var adjust = new Button { Content = "调整", Padding = new Thickness(9, 6, 9, 6) };
        var clear = new Button { Content = "全图", Padding = new Thickness(9, 6, 9, 6) };
        if (RoiHasSubscriptions(id)) { shape.IsEnabled = false; draw.IsEnabled = adjust.IsEnabled = clear.IsEnabled = false; }
        bool matching = flow.Spec(id).Type is "FastTemplateMatch" or "FastLocate" or "TemplateMatch";
        if (matching) clear.Content = "全图搜索";
        if (positioned) clear.Content = "自动圆范围";
        if (strip) { clear.IsEnabled = false; clear.ToolTip = "直线卡尺需要搜索带。请绘制或调整搜索带。"; }
        if (FollowsRoiBoundary(id) && !positioned) { clear.IsEnabled = false; clear.ToolTip = "跟随边界需要绘制 ROI；使用全图时请切换为手动指引。"; }
        shape.SelectionChanged += (_, _) => { if (!buildingInspector && ShapeValue(shape) == "none") Safe(() => UpdateParameter(id, "roi", RoiRegion.None.ToJson())); };
        draw.Click += async (_, _) => { if (strip) await DrawLineCaliperAsync(id); else if (ShapeValue(shape) == "none") Safe(() => UpdateParameter(id, "roi", RoiRegion.None.ToJson())); else await DrawRegionAsync(id, ShapeValue(shape), false); };
        adjust.Click += async (_, _) => await EditRoiAsync();
        clear.Click += (_, _) => Safe(() => UpdateParameter(id, "roi", RoiRegion.None.ToJson()));
        row.Children.Add(draw);
        row.Children.Add(adjust);
        row.Children.Add(clear);
        Parameters.Children.Add(row);
        Parameters.Children.Add(Note(strip ? "搜索带中心线为卡尺布置线，高度决定两侧搜索范围。拖动带内可平移；拖动两端可旋转和伸缩。搜索高度以 ROI 高度为准。" : region.Shape == "none" ? "当前使用全图。绘制后可拖动区域、圆心、半径或多边形顶点。" : "使用当前算子输入图的局部坐标；输出保留当前图像的坐标平面与原点。"));
        if (matching) Parameters.Children.Add(Note("搜索范围与模板独立。默认向下游发送原图和当前目标的定位中心；也可选择模板裁图并调整留边。"));
        if (FixtureActive(id)) Parameters.Children.Add(Note("拖动 ROI 会逆变换到教学基准，下次运行从基准重新计算位置。"));
        else if (positioned) Parameters.Children.Add(Note($"当前模板中心（原图）：({inputPosition!.CenterX:0.##}, {inputPosition.CenterY:0.##})。拖动圆 ROI 将更新相对模板中心偏移。"));
        if (FixtureActive(id) && RoiRegion.FromJson(EffectiveParameters(id)["roi"]).ReferenceHash is string hash && hash != inputFixture!.ReferenceHash)
            Parameters.Children.Add(Note("定位基准已改变，此 ROI 已失效。请重新绘制后运行。", false));
        BuildRoiCoordinateEditors(id, "roi", region);
    }
    private async Task DrawRegionAsync(string id, string shape, bool template)
    {
        await BusyAsync(async () => { if (!template) EnsureManualRoi(id); await PrepareInputAsync(id); if (!template && flow!.Spec(id).Type == "FindCircles" && (MatchPositionActive(id) || FixtureActive(id)) && shape is not ("circle" or "annulus")) shape = "circle"; ImageView.BeginDraw(shape, template); Log(template ? "绘制模板区域；完成后点击“学习模板”。" : "绘制检测 ROI。多边形双击或 Enter 完成。"); });
    }
    private void BuildTemplatePanel(string id)
    {
        Header(Parameters, "内置模板绘制");
        var model = flow!.Node(id)["parameters"]?["model"]?.AsObject();
        if (model is not null && model.Count > 0)
        {
            int w = model["width"]!.GetValue<int>(), h = model["height"]!.GetValue<int>();
            byte[] pixels = Convert.FromBase64String(model["grayBase64"]!.GetValue<string>());
            if (model["maskBase64"] is JsonNode m)
            {
                var mask = Convert.FromBase64String(m.GetValue<string>());
                for (int i = 0; i < pixels.Length; i++)
                if (mask[i] == 0)
                    pixels[i] = 0;
            }
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, pixels, w);
            image.Freeze();
            Parameters.Children.Add(new Border { BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1), Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 8), Height = 92, Child = new Image { Source = image, Stretch = Stretch.Uniform } });
            Parameters.Children.Add(Note($"已学习 · {w} × {h} 像素 · {(model.ContainsKey("maskBase64") ? "含形状掩膜" : "矩形模板")}"));
        }
        else
            Parameters.Children.Add(Note("尚未学习。模板须包含物品边缘和少量背景，纯色区域无法匹配。"));
        var shape = ShapeChoice(flow.TemplateRegions.GetValueOrDefault(id, RoiRegion.None).Shape, advanced: false);
        Parameters.Children.Add(shape);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var draw = new Button { Content = "绘制模板", Padding = new Thickness(9, 6, 9, 6) };
        var learn = new Button { Content = "学习模板", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(9, 6, 9, 6) };
        draw.Click += async (_, _) => await DrawRegionAsync(id, ShapeValue(shape), true);
        learn.Click += async (_, _) => await BusyAsync(() => LearnTemplateAsync(id));
        row.Children.Add(draw);
        row.Children.Add(learn);
        Parameters.Children.Add(row);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var import = new Button { Content = "导入", Padding = new Thickness(9, 5, 9, 5) };
        var export = new Button { Content = "导出", Padding = new Thickness(9, 5, 9, 5), IsEnabled = model?.Count > 0 };
        var clear = new Button { Content = "清除", Padding = new Thickness(9, 5, 9, 5) };
        import.Click += (_, _) => Safe(() => ImportTemplate(id));
        export.Click += (_, _) => Safe(() => ExportTemplate(id));
        clear.Click += (_, _) => Safe(() => { retainInput = true; try { flow.Change(() => { flow.Node(id)["parameters"]!["model"] = new JsonObject(); flow.TemplateRegions.Remove(id); }); ImageView.Template = RoiRegion.None; ImageView.InvalidateVisual(); } finally { retainInput = false; } });
        actions.Children.Add(import);
        actions.Children.Add(export);
        actions.Children.Add(clear);
        Parameters.Children.Add(actions);
        Parameters.Children.Add(Note("模板区域与检测 ROI 独立。快速模块默认仅搜索位置；旋转或缩放可使用“灰度模板匹配”。"));
    }
    private async Task LearnTemplateAsync(string id)
    {
        if (flow is null || !flow.TemplateRegions.TryGetValue(id, out var region) || region.Shape == "none")
            throw new InvalidOperationException("请先在模块内绘制模板区域。");
        await PrepareInputAsync(id);
        var info = inputFrame!.Info;
        var model = TemplateLearning.Learn(inputFrame.CopyPixels(), info.Width, info.Height, info.Stride, info.PixelFormat, region);
        retainInput = true;
        try
        {
            flow.SetParameter(id, "model", model);
        }
        finally { retainInput = false; }
        ImageView.Template = region;
        ImageView.EditEnabled = true;
        ImageView.InvalidateVisual();
        Log($"模板已学习 · {model["width"]} × {model["height"]} · 可直接执行");
    }
    private void ImportTemplate(string id)
    {
        EnsureEditor();
        var dialog = new OpenFileDialog { Filter = "模板或图像|*.vtemplate;*.json;*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("模板文件过大。");
        JsonObject model;
        if (Path.GetExtension(dialog.FileName) is ".json" or ".vtemplate")
            model = JsonNode.Parse(File.ReadAllText(dialog.FileName))!.AsObject();
        else
        {
            using var stream = File.OpenRead(dialog.FileName);
            var bmp = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (bmp.PixelWidth > 512 || bmp.PixelHeight > 512)
                throw new InvalidOperationException("导入模板图像宽高不能超过 512 像素。");
            var gray = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
            byte[] bytes = new byte[gray.PixelWidth * gray.PixelHeight];
            gray.CopyPixels(bytes, gray.PixelWidth, 0);
            model = TemplateLearning.Learn(bytes, gray.PixelWidth, gray.PixelHeight, gray.PixelWidth, VisionPixelFormat.Gray8, RoiRegion.Rectangle(0, 0, gray.PixelWidth, gray.PixelHeight));
        }
        ValidateModel(model);
        UpdateParameter(id, "model", model);
        Log("模板已导入");
    }
    private static void ValidateModel(JsonObject model)
    {
        if (model.Any(p => p.Key is not "width" and not "height" and not "grayBase64" and not "maskBase64"))
            throw new ArgumentException("未知模板字段。");
        int w = model["width"]!.GetValue<int>(), h = model["height"]!.GetValue<int>();
        if (w < 4 || h < 4 || w > 512 || h > 512)
            throw new ArgumentException("模板宽高应为 4～512。");
        var pixels = Convert.FromBase64String(model["grayBase64"]!.GetValue<string>());
        if (pixels.Length != w * h)
            throw new ArgumentException("模板像素长度不匹配。");
        var mask = model["maskBase64"] is JsonNode m ? Convert.FromBase64String(m.GetValue<string>()) : Enumerable.Repeat((byte)255, w * h).ToArray();
        if (mask.Length != w * h || mask.Any(p => p != 0 && p != 255))
            throw new ArgumentException("模板掩膜无效。");
        var valid = pixels.Where((_, i) => mask[i] != 0).Select(p => (double)p).ToArray();
        if (valid.Length < 16 || valid.Average(p => p * p) - Math.Pow(valid.Average(), 2) < 1)
            throw new ArgumentException("模板缺少有效灰度对比。");
    }
    private void ExportTemplate(string id)
    {
        var dialog = new SaveFileDialog { Filter = "JP-Vision 模板|*.vtemplate", FileName = "object.vtemplate" };
        if (dialog.ShowDialog(this) == true)
            WriteAtomic(dialog.FileName, flow!.Node(id)["parameters"]!["model"]!.ToJsonString());
    }
    private void BuildRunInputs()
    {
        if (flow is null)
            return;
        var inputs = flow.Definition["inputs"]!.AsObject().Where(p => p.Value!["type"]!.GetValue<string>() != "Image").ToArray();
        if (inputs.Length == 0)
            return;
        Header(Parameters, "本次运行输入");
        foreach (var p in inputs)
        {
            Parameters.Children.Add(Note(p.Key == "ExpectedCount" ? "预期目标数量（ExpectedCount）" : p.Key));
            string type = p.Value!["type"]!.GetValue<string>();
            var edit = new TextBox { Text = runInputs.GetValueOrDefault(p.Key)?.ToString() ?? "", Margin = new Thickness(0, 0, 0, 8) };
            edit.LostKeyboardFocus += (_, _) => Safe(() => { object? value = type switch { "Int" => long.Parse(edit.Text, CultureInfo.InvariantCulture), "Double" => double.Parse(edit.Text, CultureInfo.InvariantCulture), "Bool" => bool.Parse(edit.Text), _ => edit.Text }; runInputs[p.Key] = value; ClearResult(); });
            Parameters.Children.Add(edit);
        }
    }
    private void SetCaliperGuides(string id)
    {
        if (flow is null)
            return;
        if (IsEdgePointSampler(id)) { SetEdgePointGuides(id); return; }
        if (HasMeasurementRois(id)) { SetMeasurementCaliperGuides(id); return; }
        string type = flow.Spec(id).Type;
        bool caliper = IsCaliper(id);
        ImageView.EdgePairCalipers = type == "MeasureEdgePair";
        ImageView.LineStrip = IsLineStrip(id);
        ImageView.CaliperScale = FixtureActive(id) ? inputFixture!.ScaleRatio : 1;
        var parameters = EffectiveParameters(id);
        ImageView.AllowPartialCircleCalipers = FixtureActive(id) || MatchPositionActive(id);
        if (caliper && parameters is not null && FixtureActive(id)) parameters = FixturePositioning.Parameters(parameters, inputFixture!);
        else if (caliper && type == "FindCircles" && parameters is not null && inputNode == id && CirclePositioning.UsesMatch(parameters, inputPosition))
        {
            parameters = parameters.DeepClone().AsObject();
            parameters["caliperPlacement"] = "roiBoundary";
        }
        ImageView.CaliperParameters = caliper ? parameters : null;
        ImageView.CircleCalipers = type is not ("LineCaliper" or "FindLines");
        ImageView.InvalidateVisual();
    }
    private void DisplaySelectedResult()
    {
        if (Results is null || ImageView is null)
            return;
        Results.Children.Clear();
        if (result is null || result.Status != ExecutionStatus.Succeeded || Graph.SelectedId is not string id || flow is null)
        {
            Results.Children.Add(Note("执行流程后，在流程图中选择算子查看其真实输出。"));
            NodeState.Text = "等待执行";
            if(!ImageView.EditEnabled) ShowSource();
            return;
        }
        if (!result.Data.GetProperty("nodes").TryGetProperty(id, out var trace))
        {
            Results.Children.Add(Note("本次没有执行该算子。"));
            NodeState.Text = "未执行";
            ShowSource();
            return;
        }
        NodeState.Text = trace.GetProperty("status").GetString() == "Skipped" ? "跳过 · 数据不可用" : $"完成 · {trace.GetProperty("elapsedMs").GetDouble():F1} ms";
        Header(Results, flow.Spec(id).Label);
        if (result.Data.TryGetProperty("nodeOutputs", out var outputs) && outputs.TryGetProperty(id, out var ports))
        {
            foreach (var port in flow.Spec(id).Outputs.Where(p => !p.Hidden || p.Type == "Result").OrderBy(p => p.Type == "Result" ? 0 : p.Type is "Image" or "Mask" ? 1 : 2))
            if (ports.TryGetProperty(port.Id, out var value))
            {
                if (port.Type is "Features" or "Overlay")
                {
                    var details = new StackPanel();
                    AddJson(details, value, 0);
                    Results.Children.Add(new Expander { Header = port.Id + " · " + port.Type, Content = details });
                }
                else
                {
                    Header(Results, port.Type == "Result" ? "全部结果" : port.Type == "Image" ? "图像与附带数据" : port.Id + " · " + port.Type);
                    if (port.Type == "Result") AddResultPacket(Results, value);
                    else AddJson(Results, value, 0);
                }
            }
        }
        if (result.Data.GetProperty("measurements").TryGetProperty(id, out var measurement) && (!result.Data.GetProperty("nodeOutputs").TryGetProperty(id, out var all) || !all.TryGetProperty("result", out _)))
        {
            Header(Results, "测量结果");
            if (measurement.TryGetProperty("resultKind", out var resultKind) && resultKind.GetString() == "geometryMeasurement") AddGeometryResult(Results, measurement);
            else AddJson(Results, measurement, 0);
        }
        var export = new Button { Content = "导出本次结果 JSON", Margin = new Thickness(0, 12, 0, 6) };
        export.Click += (_, _) => Safe(() => { var dialog = new SaveFileDialog { Filter = "结果 JSON|*.json", FileName = "result.json" }; if (dialog.ShowDialog(this) == true) WriteAtomic(dialog.FileName, result.Data.GetRawText()); });
        Results.Children.Add(export);
        if (TryShowCameraDisplay()) return;
        if (ImageChoice.SelectedIndex == 1)
        {
            ShowSource();
            return;
        }
        if (ImageView.EditEnabled && inputNode == id)
            return;
        inputFrame?.Dispose();
        inputFrame = null;
        inputNode = null;
        inputPosition = null; inputFixture = null;
        BuildInspector();
        ImageView.Roi = ImageView.Template = RoiRegion.None;
        ImageView.Overlays = null;
        string? imageNode = null;
        bool associatedImage = flow.Spec(id).Outputs.Any(p => p.Id == "image" && p.Type == "Image");
        bool detectorInput = associatedImage && !flow.Spec(id).ImagePipeline && flow.Spec(id).Outputs[0].Type != "Image";
        var imageEdge = flow.Connections.FirstOrDefault(e => e!["toNode"]!.GetValue<string>() == id && flow.Spec(e["fromNode"]!.GetValue<string>()).Outputs.Any(p => p.Id == e["fromPort"]!.GetValue<string>() && p.Type is "Image" or "Mask"));
        bool ownUnavailable = associatedImage && result.Data.GetProperty("nodeOutputs").TryGetProperty(id, out var ownPorts) &&
            ownPorts.TryGetProperty("image", out var ownImage) && ownImage.TryGetProperty("available", out var ownAvailable) && !ownAvailable.GetBoolean();
        if (ownUnavailable || (detectorInput && imageEdge is not null && result.Data.GetProperty("nodeOutputs").TryGetProperty(imageEdge["fromNode"]!.GetValue<string>(), out var imagePorts) &&
            imagePorts.TryGetProperty(imageEdge["fromPort"]!.GetValue<string>(), out var imageMetadata) && imageMetadata.TryGetProperty("available", out var available) && !available.GetBoolean())
            )
        {
            ImageView.Clear();
            bool annularSamplesUnavailable = ownUnavailable && result.Data.GetProperty("nodeOutputs").GetProperty(id).GetProperty("image").TryGetProperty("unavailableReason", out var unavailableReason) && unavailableReason.GetString() == "UnavailableAnnularSamples";
            bool roiUnavailable = ownUnavailable && result.Data.GetProperty("nodeOutputs").GetProperty(id).GetProperty("image").TryGetProperty("unavailableReason", out var roiReason) && roiReason.GetString() == "UnavailableRoi";
            if (annularSamplesUnavailable)
            {
                ImageInfo.Text = "圆环范围内没有有效图像像素";
                Results.Children.Add(Note("圆环范围内没有有效图像像素，请调整圆心、半径或上游 ROI。"));
            }
            else if (roiUnavailable)
            {
                ImageInfo.Text = "ROI 未覆盖当前输入图的有效像素";
                Results.Children.Add(Note("当前 ROI 与输入图没有有效交集，请按当前输入图的宽高调整位置或尺寸；本次不会改用全图。"));
            }
            else
            {
                ImageInfo.Text = "上游没有有效的定位图像";
                Results.Children.Add(Note("上游未匹配到选中的目标，本次不在全图继续检测。请检查匹配阈值、搜索范围和目标索引。"));
            }
            return;
        }
        if (associatedImage || detectorInput || flow.Spec(id).Outputs[0].Type is "Image" or "Mask")
            imageNode = id;
        else
        {
            imageNode = imageEdge?["fromNode"]!.GetValue<string>();
        }
        if (imageNode is not null)
            try
            {
                using var frame = detectorInput ? result.GetInputImage(id) : result.GetIntermediateImage(imageNode);
                ImageView.SetFrame(frame);
                if (flow.Spec(id).ImagePipeline && associatedImage && result.Data.GetProperty("nodeOutputs").TryGetProperty(id, out var pipelineNode) && pipelineNode.TryGetProperty("image", out var pipelineImage))
                {
                    try
                    {
                        inputFrame = result.GetInputImage(id); inputNode = id;
                        inputPosition = pipelineImage.TryGetProperty("matchPosition", out var match) ? match.Deserialize<VisionMatchPosition>() : null;
                        ReadInputFixture(pipelineImage); BuildInspector();
                    }
                    catch (Exception ex) when (ex is VisionException or ArgumentException) { inputFrame?.Dispose(); inputFrame = null; inputNode = null; }
                }
                if (detectorInput)
                {
                    inputFrame?.Dispose();
                    inputFrame = result.GetInputImage(id);
                    inputNode = id;
                    inputPosition = result.Data.GetProperty("nodeOutputs").GetProperty(id).TryGetProperty("image", out var metadata) &&
                        metadata.TryGetProperty("matchPosition", out var reference) ? reference.Deserialize<VisionMatchPosition>() : null;
                    if (result.Data.GetProperty("nodeOutputs").GetProperty(id).TryGetProperty("image", out var inputMetadata)) ReadInputFixture(inputMetadata);
                    BuildInspector();
                }
                UpdateImageInfo(detectorInput || imageNode != id ? "输入与结果标记" : "算子输出");
                if (result.Data.GetProperty("nodeOutputs").TryGetProperty(imageNode, out var displayPorts) && displayPorts.TryGetProperty("image", out var displayImage)) ApplyImageCoordinateLabel(displayImage);
            }
            catch (Exception ex) when (ex is VisionException or ArgumentException) { ShowSource(); Results.Children.Add(Note("该图像未保留。勾选“保留中间结果”后重新执行。")); }
        else
            ShowSource();
        if (flow.Spec(id).ImagePipeline && result.Data.GetProperty("nodeOutputs").TryGetProperty(id, out var pipelinePorts) && TryPipelineOverlay(pipelinePorts, out var carriedOverlay))
            ImageView.Overlays = carriedOverlay;
        else if (result.Data.TryGetProperty("debugOverlays", out var overlays) && overlays.TryGetProperty(id, out var selected))
            ImageView.Overlays = selected;
        else if (result.Data.TryGetProperty("nodeOutputs", out var outputNodes) && outputNodes.TryGetProperty(id, out var nodePorts) && nodePorts.TryGetProperty("overlay", out var selectedPort))
            ImageView.Overlays = selectedPort;
        else if (result.Data.GetProperty("measurements").TryGetProperty(id, out var measured) && measured.TryGetProperty("overlay", out var measurementOverlay))
            ImageView.Overlays = measurementOverlay;
        else if (result.Data.TryGetProperty("nodeOutputs", out var contourNodes) && contourNodes.TryGetProperty(id, out var contourPorts) && contourPorts.TryGetProperty("contours", out var contours))
        {
            var outlines = new JsonArray();
            foreach (var contour in contours.EnumerateArray())
                outlines.Add(new JsonObject { ["type"] = "polyline", ["closed"] = true, ["color"] = "#00FF00", ["lineWidth"] = 2, ["points"] = JsonNode.Parse(contour.GetRawText()) });
            using var document = JsonDocument.Parse(outlines.ToJsonString());
            ImageView.Overlays = document.RootElement.Clone();
            Results.Children.Insert(1, Note($"轮廓数量：{contours.GetArrayLength()}", false));
        }
        ImageView.InvalidateVisual();
    }
    private void AddResultPacket(StackPanel target, JsonElement packet)
    {
        if (AddDeepLearningResult(target, packet)) return;
        if (AddBlobResult(target, packet)) return;
        if (AddEdgePointResult(target, packet)) return;
        if (packet.TryGetProperty("resultKind", out var kind) && kind.GetString() is "geometryMeasurement" or "positionFixture")
        {
            if (kind.GetString() == "positionFixture") AddFixtureResult(target, packet);
            else AddGeometryResult(target, packet);
            return;
        }
        foreach (string field in new[] { "count", "found", "passed" })
            target.Children.Add(Note(ResultLabels[field] + "：" + ScalarText(packet.GetProperty(field)), false));
        if (packet.TryGetProperty("dataAvailable", out var available) && !available.GetBoolean())
            target.Children.Add(Note("数据不可用：上游无有效输入或有效采样区域不足。当前数量不能用于正常零目标判定。", false));
        var measurement = packet.GetProperty("measurement");
        if (measurement.TryGetProperty("caliper", out var caliper))
        {
            bool circleFit = packet.GetProperty("featureKind").GetString() == "circle";
            AddCaliperDiagnostics(target, packet, caliper, circleFit);
        }
        var selected = measurement.GetProperty("selected");
        Header(target, $"选中目标（{ResultCoordinates(packet)}）");
        if (selected.ValueKind == JsonValueKind.Object)
        {
            if (packet.GetProperty("featureKind").GetString() == "circle")
            {
                if (measurement.TryGetProperty("detection", out _))
                    target.Children.Add(Note("当前为自动找圆；需要按卡尺点集拟合时，将“查找方式”改为“卡尺拟合”。"));
            }
            string[] fields = packet.GetProperty("featureKind").GetString() switch
            {
                "circle" => new[] { "centerX", "centerY", "radius", "diameter", "area", "perimeter", "rmsError" },
                "line" => new[] { "centerX", "centerY", "x1", "y1", "x2", "y2", "length", "angleDegrees", "rmsError" },
                "match" => new[] { "centerX", "centerY", "score", "x", "y", "width", "height", "angleDegrees", "scale" },
                _ => new[] { "centroidX", "centroidY", "centroidValid", "area", "perimeter", "circularity", "x", "y", "width", "height" }
            };
            foreach (string field in fields)
                if (selected.TryGetProperty(field, out var value))
                {
                    string label = ResultLabels.GetValueOrDefault(field, field);
                    if (packet.GetProperty("featureKind").GetString() == "line")
                        label = field switch { "centerX" => "中点 X", "centerY" => "中点 Y", "x1" => "起点 X", "y1" => "起点 Y", "x2" => "终点 X", "y2" => "终点 Y", _ => label };
                    target.Children.Add(Note(label + "：" + ScalarText(value), false));
                }
            if (packet.GetProperty("featureKind").GetString() == "line" && selected.TryGetProperty("equation", out var equation) && equation.ValueKind == JsonValueKind.Object)
                target.Children.Add(Note($"直线方程 aX+bY+c=0：\na={equation.GetProperty("a").GetDouble():0.######}，b={equation.GetProperty("b").GetDouble():0.######}，c={equation.GetProperty("c").GetDouble():0.######}", false));
        }
        else
            target.Children.Add(Note(packet.GetProperty("found").GetBoolean() ? "目标索引超出范围，未选中目标。" : "未找到目标。"));
        if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty("outputAnnulus", out var annulus))
        {
            Header(target, $"输出圆环（{ResultCoordinates(packet)}）");
            target.Children.Add(Note($"内半径：{annulus.GetProperty("innerRadius").GetDouble():0.######} · 外半径：{annulus.GetProperty("outerRadius").GetDouble():0.######}", false));
        }
        if (measurement.TryGetProperty("position", out var position))
            target.Children.Add(Note($"定位模板中心：({position.GetProperty("centerX").GetDouble():0.##}, {position.GetProperty("centerY").GetDouble():0.##}) · 跟随本次匹配", false));
        foreach (var (field, title) in new[] { ("features", "全部目标"), ("measurement", "测量与诊断"), ("overlay", "图形标记") })
        {
            var details = new StackPanel();
            AddJson(details, packet.GetProperty(field), 0);
            target.Children.Add(new Expander { Header = title, Content = details });
        }
    }
    private static readonly Dictionary<string, string> ResultLabels = new() { ["detection"] = "检测分辨率", ["downsampled"] = "使用了降采样", ["inputWidth"] = "输入宽度", ["inputHeight"] = "输入高度", ["workWidth"] = "工作图宽度", ["workHeight"] = "工作图高度", ["scaleX"] = "水平缩放比例", ["scaleY"] = "垂直缩放比例", ["width"] = "宽度", ["height"] = "高度", ["count"] = "目标数量", ["found"] = "找到目标", ["passed"] = "数量判定", ["selected"] = "选中目标", ["selectedValid"] = "选中目标有效", ["selectedIndex"] = "目标索引", ["featureKind"] = "结果类型", ["radius"] = "半径", ["diameter"] = "直径", ["centerX"] = "圆心／中心 X", ["centerY"] = "圆心／中心 Y", ["area"] = "面积", ["perimeter"] = "周长", ["score"] = "匹配分数", ["angleDegrees"] = "角度（°）", ["scale"] = "尺度", ["length"] = "长度", ["rmsError"] = "拟合 RMS", ["requestedCount"] = "卡尺数量", ["sampledCount"] = "采样点数", ["retainedCount"] = "保留点数", ["rejectedCount"] = "剔除点数", ["missingCount"] = "未找到边缘", ["foregroundPixels"] = "前景像素数", ["originX"] = "原点 X", ["originY"] = "原点 Y", ["stride"] = "步长", ["pixelFormat"] = "像素格式" };
    private void AddJson(StackPanel target, JsonElement value, int depth)
    {
        if (depth > 5)
        {
            target.Children.Add(Note("更多数据请导出本次结果。"));
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        foreach (var p in value.EnumerateObject())
        {
            string name = p.Name switch { "features" => "全部目标", "measurement" => "测量与诊断", "overlay" => "图形标记", "placement" => "卡尺排列", "guides" => "采样窗口", "normalX" => "搜索方向 X", "normalY" => "搜索方向 Y", "available" => "图像可用", "binaryMask" => "二值图像", "padded" => "包含补边", "pipeline" => "随图像传递的结果", "stageCount" => "已执行模块数", "frameId" => "当前帧编号", "lastNodeId" => "最后执行模块", "results" => "检测与测量数据", "coordinateMapping" => "展开映射", "coordinatePlane" => "坐标平面", "coordinateSpace" => "坐标空间", _ => ResultLabels.GetValueOrDefault(p.Name, p.Name) };
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var child = new StackPanel { Margin = new Thickness(8, 4, 0, 0) };
                AddJson(child, p.Value, depth + 1);
                target.Children.Add(new Expander { Header = name, IsExpanded = p.Name is "selected" or "measurement", Content = child });
            }
            else
            {
                string text = p.Name switch
                {
                    "coordinateSpace" when p.Value.ValueKind == JsonValueKind.String => p.Value.GetString() switch { "annularUnwrapped" => "展开图坐标", "original" => "原图坐标", _ => ScalarText(p.Value) },
                    "placement" when p.Value.ValueKind == JsonValueKind.String => EnumText(p.Value.GetString()!),
                    _ => ScalarText(p.Value)
                };
                target.Children.Add(Note(name + "：" + text, false));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in value.EnumerateArray().Take(50))
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var child = new StackPanel();
                    AddJson(child, item, depth + 1);
                    target.Children.Add(new Expander { Header = $"目标 / 项目 {i++}", Content = child, IsExpanded = i == 1 });
                }
                else
                    target.Children.Add(Note(ScalarText(item)));
            }
            if (value.GetArrayLength() > 50)
                target.Children.Add(Note($"共 {value.GetArrayLength()} 项，界面展示前 50 项。"));
            if (value.GetArrayLength() == 0)
                target.Children.Add(Note("空结果"));
        }
        else
            target.Children.Add(Note(ScalarText(value), false));
    }
    private static string ScalarText(JsonElement value) => value.ValueKind switch { JsonValueKind.Null => "无目标", JsonValueKind.True => "是", JsonValueKind.False => "否", JsonValueKind.Number => value.TryGetInt64(out long n) ? n.ToString() : value.GetDouble().ToString("0.######", CultureInfo.InvariantCulture), JsonValueKind.String => value.GetString() ?? "", _ => value.ToString() };
}

using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace JPVision.Studio;

public partial class MainWindow
{
    private bool IsEdgePointSampler(string id) => flow!.Spec(id).Type == "FindEdgePoints";

    private void BuildEdgePointRoiPanel(string id)
    {
        Header(Parameters, "不规则边缘搜索带");
        Parameters.Children.Add(Note("沿待测边缘拖出搜索带。长度方向逐站采样，高度方向搜索边缘；保留实际边缘点与曲线，不强制拟合成直线或圆。"));
        var repair = new Button { Content = "启用曲线修复", Tag = "edge-points:repair", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6), ToolTip = "开启跳点剔除、柔顺处理和有界缺段补全；原始点仍保留，可在下方调整修复参数。" };
        repair.Click += (_, _) => Safe(() =>
        {
            flow!.SetParameter(id, "curveMode", JsonValue.Create("reconstruct"));
            Log("已开启边缘曲线修复；请调整跳点容差、邻域窗口和最大补全长度。点到线测量默认使用实测合格点的原始坐标。");
        });
        Parameters.Children.Add(repair);
        Parameters.Children.Add(Note("修复结果区分实测点、已平滑实测点与补全点。补全只根据缺段左右可靠点生成，不向两端外推；点到线测量默认仍使用实测合格点的原始坐标。"));
        var roi = CurrentInputRoi(id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var draw = new Button { Content = "绘制／重画搜索带", Tag = "edge-points:draw", Margin = new Thickness(0, 0, 5, 6) };
        var adjust = new Button { Content = "调整", Tag = "edge-points:adjust", IsEnabled = roi.Shape != "none", Margin = new Thickness(0, 0, 0, 6) };
        if (RoiHasSubscriptions(id)) draw.IsEnabled = adjust.IsEnabled = false;
        draw.Click += async (_, _) => await DrawEdgePointRoiAsync(id);
        adjust.Click += async (_, _) => await EditRoiAsync();
        row.Children.Add(draw); row.Children.Add(adjust); Parameters.Children.Add(row);
        Parameters.Children.Add(Note(roi.Shape == "none" ? "尚未绘制搜索带。此模块需要明确的区域，不会在全图任意选取边缘。" : "拖动两端调整长度和角度；拖动侧面调整搜索高度。箭头指示极性与第一／最后边缘的搜索方向。"));
        if (roi.Shape is "rectangle" or "rotatedRectangle")
        {
            var parameters = EffectiveParameters(id);
            var plan = CaliperLayout.DescribeEdgePoints(roi, parameters);
            double spacing = double.Parse(parameters["sampleSpacing"]!.ToJsonString(), CultureInfo.InvariantCulture);
            double width = double.Parse(parameters["projectionWidth"]!.ToJsonString(), CultureInfo.InvariantCulture);
            double coverage = double.Parse(parameters["minimumCoverage"]!.ToJsonString(), CultureInfo.InvariantCulture);
            int required = Math.Max(1, (int)Math.Ceiling(plan.StationCount * coverage));
            var summary = Note($"搜索带长 {roi.Width:0.##} px，高 {roi.Height:0.##} px；站间距 {spacing:0.##} px，共 {plan.StationCount} 个采样站。投影宽 {width:0} px 沿长度方向汇总，高度方向搜索边缘。至少需要 {required} 个有效点（{coverage:P0}）。", false);
            summary.Tag = "edge-points:sampling-summary"; Parameters.Children.Add(summary);
            var state = Note(plan.CanExecute ? "当前采样设置可执行。预览最多显示 256 个窗口，运行保留全部有效点。" : EdgePointSamplingReason(plan), false);
            state.Tag = "edge-points:sampling-state"; Parameters.Children.Add(state);
        }
        BuildRoiCoordinateEditors(id, "roi", roi);
    }

    private static string EdgePointSamplingReason(EdgePointSamplingPlan plan) => plan.Reason switch
    {
        "InvalidSearchLength" => "搜索带长度须为不小于 1 px 的有限数值。",
        "InvalidSampleSpacing" => "采样站间距须在 1～32768 px 范围内；增大间距可减少点数。",
        "InvalidProjectionWidth" => "投影宽度须为 1～255 px 的奇数。",
        "InvalidSigma" => "平滑 Sigma 须在 0～10 范围内。",
        "InvalidSearchHeight" => "搜索带高度须在 8～32768 px 范围内。",
        "InsufficientSmoothingHeight" => $"当前平滑设置要求搜索带高度至少 {plan.MinimumSearchHeight:0.##} px，请加高搜索带或减小 Sigma。",
        "InvalidMaximumPoints" => "采样站数量上限须在 2～32769 范围内。",
        "TooManySamplingStations" => $"请求 {plan.StationCount} 个采样站，超过当前上限 {plan.MaximumPoints}。请增大站间距、缩短搜索带，或提高数量上限。",
        "SamplingBudgetExceeded" => $"当前 {plan.StationCount} 个采样站的搜索量过大，暂不能执行。请增大站间距、降低搜索带高度或减小投影宽度。",
        _ => "请绘制有效搜索带并检查采样设置。"
    };

    private async Task DrawEdgePointRoiAsync(string id)
    {
        await BusyAsync(async () =>
        {
            EnsureManualRoi(id);
            await PrepareInputAsync(id);
            SetEdgePointGuides(id);
            var existing = CurrentInputRoi(id);
            ImageView.LineDrawHeight = existing.Shape is "rectangle" or "rotatedRectangle" ? existing.Height : 60;
            ImageView.BeginDraw("lineCaliper", false, region => Safe(() =>
            {
                if (region.Shape != "rotatedRectangle") throw new ArgumentException("请绘制有效的边缘搜索带。");
                retainInput = true;
                try { CommitRoi(id, region); ImageView.Roi = CurrentInputRoi(id); BuildInspector(); SetEdgePointGuides(id); }
                finally { retainInput = false; }
                Log("边缘搜索带已更新：逐站搜索真实边缘点；运行后可将点集连接到“点集到线距离”。");
            }));
            InspectorTabs.SelectedIndex = 0;
            Log("沿不规则边缘的起点拖到终点，再调整搜索带高度覆盖整条边；Esc 取消。");
        });
    }

    private void SetEdgePointGuides(string id)
    {
        var p = EffectiveParameters(id);
        p["edgePointSampling"] = true;
        p["caliperPlacement"] = "roiCenter";
        ImageView.EdgePairCalipers = false; ImageView.CircleCalipers = false; ImageView.LineStrip = true;
        ImageView.CaliperScale = 1; ImageView.AllowPartialCircleCalipers = true;
        ImageView.CaliperParameters = p;
        ImageView.InvalidateVisual();
    }

    private bool AddEdgePointResult(StackPanel target, JsonElement packet)
    {
        string? kind = packet.TryGetProperty("resultKind", out var type) ? type.GetString() : null;
        if (kind is not ("edgePointSet" or "pointLineDistances")) return false;
        bool valid = packet.TryGetProperty("valid", out var available) && available.GetBoolean();
        Header(target, kind == "edgePointSet" ? "边缘点集" : "点集到直线垂距");
        target.Children.Add(Note("结果有效：" + (valid ? "是" : "否"), false));
        target.Children.Add(Note("坐标：" + ResultCoordinates(packet)));
        if (!valid && packet.TryGetProperty("reason", out var reason)) target.Children.Add(Note(EdgePointReason(reason.GetString()), false));
        if (kind == "edgePointSet")
        {
            target.Children.Add(Note($"输出点：{ScalarProperty(packet, "count")} / 采样站：{ScalarProperty(packet, "stationCount")} · 未补缺失：{ScalarProperty(packet, "missedCount")}", false));
            if (packet.TryGetProperty("measuredCount", out _))
            {
                var summary = Note($"原始检出 {ScalarProperty(packet, "rawCount")} · 实测保留 {ScalarProperty(packet, "measuredCount")} · 剔除 {ScalarProperty(packet, "rejectedCount")} · 补全 {ScalarProperty(packet, "interpolatedCount")}", false);
                summary.Tag = "edge-points:reconstruction-summary"; target.Children.Add(summary);
                if (packet.TryGetProperty("observedCoverage", out var observed) && observed.ValueKind == JsonValueKind.Number)
                    target.Children.Add(Note($"实测合格比例：{observed.GetDouble():P1}；补全点不提高实测有效性。"));
            }
            else if (packet.TryGetProperty("coverage", out var coverage) && coverage.ValueKind == JsonValueKind.Number) target.Children.Add(Note($"有效比例：{coverage.GetDouble():P1}"));
            target.Children.Add(Note("结果标记默认绿色。来源列区分实测、已平滑与补全；原始坐标和完整点集可导出 JSON。未补全的测站仍保留缺口。"));
            if (packet.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            {
                var rows = EdgePointRows(points);
                target.Children.Add(ResultTable(rows, "edge-points:table", ("序号", "Index"), ("采样站", "StationIndex"), ("来源", "Origin"), ("X", "X"), ("Y", "Y"), ("原始 X", "RawX"), ("原始 Y", "RawY"), ("梯度", "Gradient")));
            }
            if (packet.TryGetProperty("rawPoints", out var rawPoints) && rawPoints.ValueKind == JsonValueKind.Array)
            {
                var expander = new Expander { Header = $"原始检测点（{rawPoints.GetArrayLength()}）", Tag = "edge-points:raw", IsExpanded = false };
                expander.Expanded += (_, _) => expander.Content ??= ResultTable(EdgePointRows(rawPoints), "edge-points:raw-table", ("序号", "Index"), ("采样站", "StationIndex"), ("X", "X"), ("Y", "Y"), ("梯度", "Gradient"));
                target.Children.Add(expander);
            }
        }
        else
        {
            target.Children.Add(Note($"测量点数：{ScalarProperty(packet, "count")}", false));
            if (packet.TryGetProperty("pointSource", out var pointSource))
                target.Children.Add(Note($"点来源：{(pointSource.GetString() == "all" ? "修正／补全点集" : "实测合格原始点")} · 实测 {ScalarProperty(packet, "measuredCount")} · 补全 {ScalarProperty(packet, "interpolatedCount")}", false));
            foreach (var (field, label) in new[] { ("minimum", "最小距离"), ("maximum", "最大距离"), ("mean", "平均距离"), ("rms", "均方根距离"), ("standardDeviation", "标准差"), ("range", "距离极差") })
                target.Children.Add(Note(label + "：" + ScalarProperty(packet, field) + " px", false));
            if (packet.TryGetProperty("decision", out var decision))
            {
                string state = decision.TryGetProperty("status", out var status) ? status.GetString() ?? "Unknown" : "Unknown";
                target.Children.Add(Note("判定：" + (state == "Pass" ? "通过" : state == "Fail" ? "不通过" : "未知"), false));
            }
            target.Children.Add(Note("每个点向选定的无限直线作垂线。距离始终非负；有符号距离按直线归一化法向区分两侧。结果不将不规则边缘拟合为直线。"));
            if (packet.TryGetProperty("distances", out var distances) && distances.ValueKind == JsonValueKind.Array)
            {
                var rows = distances.EnumerateArray().Select(p => new PointLineDistanceRow(Int(p, "index"), Int(p, "stationIndex"), EdgePointOrigin(p), Num(p, "x"), Num(p, "y"), Num(p, "projectionX"), Num(p, "projectionY"), Num(p, "distance"), Num(p, "signedDistance"))).ToArray();
                target.Children.Add(ResultTable(rows, "point-line:table", ("序号", "Index"), ("来源", "Origin"), ("X", "X"), ("Y", "Y"), ("垂距", "Distance"), ("有符号距离", "SignedDistance"), ("垂足 X", "ProjectionX"), ("垂足 Y", "ProjectionY")));
            }
        }
        return true;
    }

    private DataGrid ResultTable<T>(IReadOnlyList<T> rows, string tag, params (string Label, string Path)[] columns)
    {
        var grid = new DataGrid { ItemsSource = rows, Tag = tag, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = true, EnableRowVirtualization = true,
            EnableColumnVirtualization = true, MaxHeight = 285, MinHeight = 96, RowHeight = 27, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Text"),
            RowBackground = (Brush)FindResource("Surface"), AlternatingRowBackground = (Brush)FindResource("Background"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 3, 0, 8) };
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(grid, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("Background"))); header.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("Text"))); header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        grid.ColumnHeaderStyle = header;
        foreach (var (label, path) in columns) grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(path) { StringFormat = path == "Origin" ? null : path is "Index" or "StationIndex" ? "0" : "0.######" }, MinWidth = path == "Origin" ? 130 : path is "Index" or "StationIndex" ? 48 : 82 });
        return grid;
    }
    private static string ScalarProperty(JsonElement value, string key) => value.TryGetProperty(key, out var item) ? ScalarText(item) : "无结果";
    private static double Num(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : double.NaN;
    private static int Int(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.TryGetInt32(out int index) ? index : -1;
    private static double? OptionalEdgeNumber(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    private static string EdgePointOrigin(JsonElement point) => point.TryGetProperty("origin", out var origin) && origin.GetString() == "interpolated"
        ? "补全" : point.TryGetProperty("adjusted", out var adjusted) && adjusted.ValueKind == JsonValueKind.True ? "实测（已平滑）" : "实测";
    private static EdgePointRow[] EdgePointRows(JsonElement points) => points.EnumerateArray().Select(p =>
        new EdgePointRow(Int(p, "index"), Int(p, "stationIndex"), EdgePointOrigin(p), Num(p, "x"), Num(p, "y"), Num(p, "gradient"),
            OptionalEdgeNumber(p, "rawX") ?? (EdgePointOrigin(p) == "实测" ? Num(p, "x") : null),
            OptionalEdgeNumber(p, "rawY") ?? (EdgePointOrigin(p) == "实测" ? Num(p, "y") : null))).ToArray();
    private static string EdgePointReason(string? reason) => reason switch
    {
        "MissingSearchRoi" => "尚未绘制搜索带，请在算子参数内绘制后运行。",
        "InsufficientEdgePoints" or "InsufficientCoverage" => "有效边缘点不足，请检查搜索高度、极性、选边方式、阈值及最小有效比例。",
        "UnavailablePointSet" or "UnavailablePoints" or "MissingPointSet" or "InvalidPointSet" => "本次输入没有有效点集，请先连接“边缘点集”的输出，并检查有效比例。",
        "UnavailableLine" or "UnavailableSelection" or "MissingReferenceLine" or "ReferenceLineIndexOutOfRange" => "本次没有有效参考线；请在输入来源中选择“直线查找”的图像输出，并检查直线索引。",
        "DegenerateReferenceLine" => "参考线长度不足，无法计算点到线距离。",
        "NoEdgePoints" => "搜索带内没有找到满足条件的边缘点，请检查位置、极性和边缘阈值。",
        "NoQualifiedEdgePoints" => "没有通过连续性筛选的实测边缘点，请检查邻域窗口、跳点容差与搜索区域。",
        "NoMeasuredPoints" or "NoMeasuredEdgePoints" or "NoSelectedPoints" => "当前没有可用于测量的实测合格点；请检查曲线修复容差与点来源选项。",
        "UnavailableInputImage" => "本次输入图像不可用，请检查上游检测结果。",
        "UnavailableRoi" => "ROI 与当前输入图没有有效交集，请按当前输入图的宽高调整位置或尺寸。",
        "CoordinateSpaceMismatch" or "CoordinatePlaneMismatch" => "点集与参考线不在同一坐标平面，不能混用原图与展开图。",
        "UnavailableSamplingArea" => "搜索带没有足够的有效图像采样区域。",
        "UnavailableParameter" => "订阅参数本次不可用。",
        _ => reason ?? "本次没有有效结果。"
    };
    private sealed record EdgePointRow(int Index, int StationIndex, string Origin, double X, double Y, double Gradient, double? RawX, double? RawY);
    private sealed record PointLineDistanceRow(int Index, int StationIndex, string Origin, double X, double Y, double ProjectionX, double ProjectionY, double Distance, double SignedDistance);
}

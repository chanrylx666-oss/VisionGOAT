using System.Text.Json;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private void AddGeometryResult(StackPanel target, JsonElement packet)
    {
        bool valid = packet.GetProperty("valid").GetBoolean();
        string operation = packet.GetProperty("operation").GetString()!;
        Header(target, $"测量结果（{(IsUnwrappedCoordinates(packet) ? "展开图坐标" : "原图坐标")}）");
        target.Children.Add(Note("测量有效：" + (valid ? "是" : "否"), false));
        if (valid && packet.GetProperty("value").ValueKind == JsonValueKind.Number)
            target.Children.Add(Note($"{(operation == "lineDistance" ? "线线距离" : operation == "lineCircleDistance" ? "线圆距离" : operation is "circleCenterDistance" or "circleEdgeDistance" ? "圆圆距离" : "实测值")}：{packet.GetProperty("value").GetDouble():0.######} {packet.GetProperty("unit").GetString()}", false));
        if (operation == "lineDistance")
        {
            target.Children.Add(Note("测量位置：A 中点 → B 的垂足（无限直线）"));
            if (packet.TryGetProperty("lineAngleDegrees", out var angle) && angle.ValueKind == JsonValueKind.Number)
                target.Children.Add(Note($"两线夹角：{angle.GetDouble():0.######}° · 允许：{packet.GetProperty("parallelAngleToleranceDegrees").GetDouble():0.######}°", false));
        }
        if (packet.TryGetProperty("distanceMode", out var mode))
        {
            target.Children.Add(Note("距离方式：" + (mode.GetString() == "edgeDistance" ? "圆周最短间距" : operation == "lineCircleDistance" ? "圆心到直线垂距" : "两圆圆心距"), false));
            if (packet.GetProperty("centerDistance").ValueKind == JsonValueKind.Number)
                target.Children.Add(Note($"{(operation == "lineCircleDistance" ? "圆心到线" : "圆心距")}：{packet.GetProperty("centerDistance").GetDouble():0.######} px", false));
            if (packet.GetProperty("edgeDistance").ValueKind == JsonValueKind.Number)
                target.Children.Add(Note($"圆周最短间距：{packet.GetProperty("edgeDistance").GetDouble():0.######} px", false));
            if (packet.TryGetProperty("relation", out var relation) && relation.ValueKind == JsonValueKind.String)
                target.Children.Add(Note("几何关系：" + (relation.GetString() switch { "separate" => "分离", "contained" => "包含", "concentric" => "同心", "coincident" => "重合", "intersecting" => "相交", "tangent" => "相切", _ => relation.GetString() }), false));
        }
        if (packet.TryGetProperty("distanceSegment", out var segment) && segment.ValueKind == JsonValueKind.Object)
        {
            var start = segment.GetProperty("start"); var end = segment.GetProperty("end");
            target.Children.Add(Note($"测距起点：({start.GetProperty("x").GetDouble():0.######}, {start.GetProperty("y").GetDouble():0.######})", false));
            target.Children.Add(Note($"测距终点：({end.GetProperty("x").GetDouble():0.######}, {end.GetProperty("y").GetDouble():0.######})", false));
        }
        if (packet.TryGetProperty("point", out var point) && point.ValueKind == JsonValueKind.Object)
            target.Children.Add(Note($"交点：({point.GetProperty("x").GetDouble():0.######}, {point.GetProperty("y").GetDouble():0.######})", false));
        if (packet.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Object)
            target.Children.Add(Note($"中心：({center.GetProperty("x").GetDouble():0.######}, {center.GetProperty("y").GetDouble():0.######})", false));
        if (packet.TryGetProperty("nominalValue", out var nominal) && nominal.ValueKind == JsonValueKind.Number)
            target.Children.Add(Note($"名义值：{nominal.GetDouble():0.######} · 偏差：{ScalarText(packet.GetProperty("deviation"))}", false));
        var decision = packet.GetProperty("decision");
        string status = decision.GetProperty("status").GetString()!;
        string reason = decision.GetProperty("reason").GetString()!;
        string explanation = reason switch { "WithinTolerance" => "在公差内", "OutOfTolerance" => "超出公差", "UnconfiguredTolerance" => "未启用公差",
            "NoScalarTolerance" => "交点不作单数值公差判断", "UnavailableSelection" => "目标未找到或索引超出范围", "ParallelLines" => "两线平行，无交点",
            "NonParallelLines" => "两线夹角超出平行角度容差，距离无效", "NoEdgePair" => "没有符合极性及宽度要求的边缘对", "DegenerateLine" => "直线退化", "UnavailableParameter" => "绑定参数本次不可用", "UnavailableSamplingArea" => "没有有效的图像采样区域", "UnavailablePoint" => "几何中心不可用", _ => reason };
        target.Children.Add(Note("判定：" + (status == "Pass" ? "通过" : status == "Fail" ? "不通过" : "未知") + " · " + explanation, false));
        var details = new StackPanel(); AddJson(details, packet, 0);
        target.Children.Add(new Expander { Header = "全部数据与诊断", Content = details });
    }
    private void AddFixtureResult(StackPanel target, JsonElement packet)
    {
        Header(target, "位置修正"); var image = packet.GetProperty("image");
        if (!image.GetProperty("available").GetBoolean()) { target.Children.Add(Note("本次没有有效的定位目标。")); return; }
        var fixture = image.GetProperty("fixture"); var position = fixture.GetProperty("current");
        target.Children.Add(Note($"当前定位中心：({position.GetProperty("centerX").GetDouble():0.###}, {position.GetProperty("centerY").GetDouble():0.###})", false));
        target.Children.Add(Note($"角度：{position.GetProperty("angleDegrees").GetDouble():0.###}° · 尺度：{position.GetProperty("scale").GetDouble():0.###}", false));
        var details = new StackPanel(); AddJson(details, packet, 0);
        target.Children.Add(new Expander { Header = "教学基准与变换诊断", Content = details });
    }
}

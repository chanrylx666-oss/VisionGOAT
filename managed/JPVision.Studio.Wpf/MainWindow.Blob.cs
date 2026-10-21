using System.Text.Json;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private static string BlobEnumText(string key, string value) => (key, value) switch
    {
        ("imageThresholdMethod", "fixed") => "固定阈值",
        ("imageThresholdMethod", "otsu") => "Otsu 自动阈值",
        ("imageThresholdMethod", "triangle") => "Triangle 自动阈值",
        ("sortOrder", "areaDescending") => "面积从大到小",
        ("sortOrder", "areaAscending") => "面积从小到大",
        ("sortOrder", "xAscending") => "从左到右（质心 X）",
        ("sortOrder", "yAscending") => "从上到下（质心 Y）",
        ("sortOrder", "labelAscending") => "按连通区域标签",
        _ => EnumText(value)
    };

    private bool AddBlobResult(StackPanel target, JsonElement packet)
    {
        if (!packet.TryGetProperty("resultKind", out var kind) || kind.GetString() != "blobAnalysis") return false;
        bool available = packet.TryGetProperty("dataAvailable", out var state) && state.GetBoolean();
        int count = Int(packet, "count");
        Header(target, "Blob 连通区域");
        target.Children.Add(Note("数据有效：" + (available ? "是" : "否") + " · 区域数量：" + count, false));
        string decision = !available ? "未知" : packet.TryGetProperty("passed", out var passed) && passed.GetBoolean() ? "通过" : "不通过";
        target.Children.Add(Note("数量判定：" + decision, false));
        target.Children.Add(Note($"分割得到 {ScalarProperty(packet, "candidateCount")} 个候选；筛除 {ScalarProperty(packet, "rejectedCount")} 个。", false));
        target.Children.Add(Note("坐标：" + ResultCoordinates(packet)));
        var area = Note("总前景面积：" + (packet.TryGetProperty("totalArea", out var total) && total.ValueKind == JsonValueKind.Number ? ScalarText(total) + " px²" : "不可用"), false);
        area.Tag = "blob:total-area"; target.Children.Add(area);
        if (!available)
        {
            string reason = packet.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() ?? "" : "";
            target.Children.Add(Note(reason switch
            {
                "UnavailableParameter" => "本次订阅参数不可用，请检查上游输出；本次不沿用上次区域。",
                "UnavailableRoi" => "ROI 与当前输入图没有有效交集，请核对当前图像尺寸和 ROI 位置；本次不改用全图。",
                _ => "本次没有有效输入或 ROI 像素，不沿用上次区域，也不将缺失结果当作正常零目标。"
            }, false));
        }
        else if (count == 0)
            target.Children.Add(Note("本次没有符合筛选条件的区域。请核对亮／暗目标、阈值和筛选范围；空结果不会保留上次区域。", false));
        target.Children.Add(Note("面积为实际前景像素数，孔洞不计入面积。周长为像素边界周长，包含孔洞边界；圆度也按此周长计算。范围和位置使用当前输入图的实际像素。"));
        if (packet.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            var rows = features.EnumerateArray().Select((region, index) => new BlobRegionRow(index, Num(region, "area"), Num(region, "centroidX"), Num(region, "centroidY"),
                Int(region, "x"), Int(region, "y"), Int(region, "width"), Int(region, "height"), Num(region, "perimeter"), Num(region, "circularity"),
                Num(region, "aspectRatio"), Num(region, "extent"), region.TryGetProperty("touchesBoundary", out var border) && border.GetBoolean() ? "是" : "否")).ToArray();
            target.Children.Add(ResultTable(rows, "blob:table", ("序号", "Index"), ("面积 px²", "Area"), ("质心 X", "CenterX"), ("质心 Y", "CenterY"),
                ("左上 X", "Left"), ("左上 Y", "Top"), ("宽 px", "Width"), ("高 px", "Height"), ("像素边界周长", "Perimeter"),
                ("圆度", "Circularity"), ("宽高比", "AspectRatio"), ("填充率", "Extent"), ("接触边界", "TouchesBoundary")));
        }
        if (packet.TryGetProperty("measurement", out var measurement))
        {
            bool selected = measurement.TryGetProperty("selected", out var region) && region.ValueKind == JsonValueKind.Object;
            if (selected)
            {
                Header(target, "选中区域");
                target.Children.Add(Note($"质心：({ScalarProperty(region, "centroidX")}, {ScalarProperty(region, "centroidY")})\n外接矩形：({ScalarProperty(region, "x")}, {ScalarProperty(region, "y")}) · {ScalarProperty(region, "width")} × {ScalarProperty(region, "height")} px", false));
            }
            else if (available && count > 0) target.Children.Add(Note("当前目标索引超出区域数量，请调整“结果索引”。"));
            var details = new StackPanel(); AddJson(details, measurement, 0);
            target.Children.Add(new Expander { Header = "分析与筛选诊断", Content = details });
        }
        return true;
    }

    private sealed record BlobRegionRow(int Index, double Area, double CenterX, double CenterY, int Left, int Top, int Width, int Height,
        double Perimeter, double Circularity, double AspectRatio, double Extent, string TouchesBoundary);
}

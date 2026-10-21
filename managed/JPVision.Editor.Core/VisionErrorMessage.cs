using System.Text.Json;

namespace JPVision.Editor.Core;

public static class VisionErrorMessage
{
    public static string Describe(string text, Func<string, string?>? nodeLabel = null)
    {
        string message = text, node = "";
        if (text.TrimStart().StartsWith('{'))
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                var value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var reason) && reason.ValueKind == JsonValueKind.String)
                    message = reason.GetString()!;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("nodeId", out var id) && id.ValueKind == JsonValueKind.String)
                    node = id.GetString()!;
            }
            catch (JsonException) { /* A non-JSON message still carries useful diagnostic text. */ }
        string? label = node.Length == 0 ? null : nodeLabel?.Invoke(node);
        string title = node.Length == 0 ? "" : $"{(label is null || label == node ? node : label + "（" + node + "）")}：\n";
        if (message.Contains("Hough", StringComparison.OrdinalIgnoreCase) && (message.Contains("exceeds", StringComparison.OrdinalIgnoreCase) || message.Contains("4 megapixels", StringComparison.OrdinalIgnoreCase)))
        {
            string size = "";
            const string prefix = "input ";
            int start = message.IndexOf(prefix, StringComparison.Ordinal);
            int end = message.IndexOf(" exceeds", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                size = message[(start + prefix.Length)..end];
            return title + $"原图检测的输入图像或检测 ROI 过大{(size.Length > 0 ? "（" + size + "）" : "")}，超过 4,194,304 像素。\n" +
                "可将“大图处理”设为“自动缩小工作图”，或在模块内缩小“检测 ROI”后使用原图模式。\n" +
                "精确圆测量也可使用卡尺拟合；自动缩小会降低小目标和边缘定位的分辨率。";
        }
        if (message.Contains("Annular unwrapping requires an annulus ROI", StringComparison.Ordinal))
            message = "圆环展开需要圆环／扇环 ROI，请在模块内绘制圆环并设置内外半径和角度。";
        else if (message.Contains("Annular unwrapping output exceeds the sampling budget", StringComparison.Ordinal) ||
                 message.Contains("Annular unwrapping dimensions exceed the sampling limit", StringComparison.Ordinal))
            message = "圆环展开尺寸过大，请设置较小的展开宽高或减小圆环范围。";
        else if (message.Contains("different image coordinate planes", StringComparison.Ordinal))
            message = "不能混合不同坐标平面的结果。原图与展开图、两个独立展开模块的结果不能直接测距，请连接同一展开图分支。";
        else if (message.Contains("Adaptive threshold block size must be odd", StringComparison.Ordinal))
            message = "局部阈值窗口必须为 3～255 的奇数，例如 15、17、31。";
        else if (message.Contains("Kernel height must be zero or odd", StringComparison.Ordinal))
            message = "核高度必须为 0 或 1～101 的奇数；0 表示跟随核宽度。";
        else if (message.Contains("Circle pre-blur kernel size must be odd", StringComparison.Ordinal))
            message = "自动找圆的检测前平滑核必须为 1～31 的奇数；1 表示不进行空间平滑。";
        else if (message.Contains("Canny aperture size must be odd", StringComparison.Ordinal))
            message = "Canny 梯度核只能选择 3、5 或 7。";
        else if (message.Contains("Binary morphology border value must be", StringComparison.Ordinal))
            message = "二值形态学的边界灰度只能为 -1（自动）、0（黑色）或 255（白色）。";
        else if (message.Contains("Edge low threshold must be below high threshold", StringComparison.Ordinal))
            message = "边缘低阈值必须小于高阈值；请同时核对两个数值。";
        else if (message.Contains("Kernel size must be odd", StringComparison.Ordinal))
            message = "滤波／形态学核尺寸必须为参数允许范围内的奇数。";
        else if (message.Contains("Caliper projection width must be odd", StringComparison.Ordinal))
            message = "卡尺宽度必须是 1～63 的奇数，例如 5、7、9。请在算子参数中选择合法宽度。";
        else if (message.Contains("Caliper removal count leaves too few points", StringComparison.Ordinal))
            message = "指定剔除点数过多，剩余点不足以满足最低有效比例。请减小剔除点数、增加卡尺数量，或核对最小有效卡尺比例。";
        else if (message.Contains("Circular boundary calipers require a circle ROI", StringComparison.Ordinal))
            message = "跟随边界的圆卡尺需要圆形 ROI。请在模块内绘制圆形 ROI，或将“卡尺排列”切换为“手动指引”。";
        else if (message.Contains("Line boundary calipers require a rectangle or polygon ROI", StringComparison.Ordinal))
            message = "跟随边界的直线卡尺需要矩形或多边形 ROI。绘制 ROI 后选择要检测的边。";
        else if (message.Contains("Line strip calipers require a rectangle or rotated rectangle ROI", StringComparison.Ordinal))
            message = "请先在直线查找中绘制直线卡尺搜索带：从待测线起点拖到终点，再调整搜索高度。";
        else if (message.Contains("Line strip search height must not exceed 512 pixels", StringComparison.Ordinal))
            message = "直线卡尺搜索高度不能超过 512 px。请拖动搜索带侧面控制点或在 ROI 中修改搜索高度。";
        else if (message.Contains("Polygon caliper edge index exceeds ROI vertex count", StringComparison.Ordinal))
            message = "选中的多边形边已不存在。请在“多边形参考边”中重新选择一条边。";
        else if (message.Contains("Caliper search length is too short for the smoothing sigma", StringComparison.Ordinal))
            message = "卡尺高度不足以完成当前平滑。请增大卡尺高度，或减小投影平滑 Sigma；高度须满足：至少 6 × Sigma + 6 px（参数下限为 8 px）。";
        else if (message.Contains("This operation requires a line at input A", StringComparison.Ordinal))
            message = "测量输入 A 需要直线结果。请连接“直线查找”的全部结果，并选择有效的直线索引。";
        else if (message.Contains("This operation requires a line at input B", StringComparison.Ordinal))
            message = "测量输入 B 需要直线结果。请连接第二个“直线查找”的全部结果，并选择有效的直线索引。";
        else if (message.Contains("Line-circle distance requires a circle at input B", StringComparison.Ordinal))
            message = "线圆距离的输入 B 需要圆结果。请连接“圆查找”的全部结果，并选择有效的圆索引。";
        else if (message.Contains("Circle distance requires a circle at both selected indices", StringComparison.Ordinal))
            message = "圆圆距离的两个输入都需要圆结果。请连接两个“圆查找”的全部结果，并检查目标索引。";
        else if (message.Contains("Required input not connected", StringComparison.Ordinal))
            message = "算子的输入端口尚未连接，请连接相同类型的端口。";
        else if (message.Contains("Template has not", StringComparison.Ordinal))
            message = "模板尚未学习，请在算子参数内绘制模板并点击“学习模板”。";
        else if (message.Contains("image is unavailable", StringComparison.OrdinalIgnoreCase))
            message = "上游尚未匹配到选中的目标，不能设置或读取定位后的图像。请先执行匹配，并检查搜索范围、匹配阈值和目标索引。";
        else if (message.Contains("Matched image padding exceeds", StringComparison.Ordinal))
            message = "后续检测扩边后的图像超过处理上限。请减小扩边，或使用更小的匹配模板。";
        else if (message.Contains("Circle match-center reference requires", StringComparison.Ordinal))
            message = "圆查找需要上游匹配中心。请连接快速匹配／快速定位的全部结果；直接处理图像时，将“定位基准”设为自动或输入图 ROI。";
        else if (message.Contains("Match-center circle search requires", StringComparison.Ordinal))
            message = "跟随匹配中心需要圆形 ROI。请重画圆形 ROI，或清除 ROI 后设置定位搜索半径；使用矩形／多边形时切换到输入图 ROI。";
        return title + message;
    }
}

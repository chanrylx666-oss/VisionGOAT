using System.Globalization;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public sealed record ParameterHelp(string Group, string Description, double Step, string Unit);

public static class ParameterEditor
{
    public static ParameterHelp Describe(string component, JsonObject p)
    {
        string key = p["name"]!.GetValue<string>(), label = p["label"]!.GetValue<string>();
        string group = key switch
        {
            "input" or "channel" => "输入与通道",
            "positionReference" or "roiReference" or "matchOffsetX" or "matchOffsetY" or "matchSearchRadius" => "定位与跟随",
            "caliperPlacement" or "caliperCount" or "projectionWidth" or "searchLength" or "roiEdge" or "roiEdgeIndex" or "radius" or "guideLength" or "angleDegrees" or "startAngle" or "endAngle" or "reverseSearch" => "卡尺采样",
            "sampleSpacing" or "maximumPoints" => "边缘采样",
            "projectionStatistic" or "subpixelRefinement" or "sigma" when component is "CircleCaliper" or "LineCaliper" or "FindCircles" or "FindLines" or "MeasureEdgePair" or "FindEdgePoints" => "边缘提取",
            "polarity" or "firstPolarity" or "secondPolarity" or "edgeSelection" or "caliperThreshold" => "边缘提取",
            "maximumDeviation" or "removePointCount" or "minimumCoverage" => "拟合与剔除",
            "showOverlay" or "showPoints" or "showCurve" or "displayEveryNth" or "overlayColor" or "overlayLineWidth" or "labelResults" or "outputAnnulusWidth" or "color" or "lineWidth" => "显示与标记",
            "resultIndex" or "minimumCount" or "maximumCount" or "expected" => "目标选择与数量判定",
            "toleranceEnabled" or "toleranceMode" or "lowerLimit" or "upperLimit" or "nominalValue" or "lowerDeviation" or "upperDeviation" => "尺寸公差",
            "firstIndex" or "secondIndex" or "firstPoint" or "secondPoint" or "lineIndex" or "statistic" or "distanceMode" or "parallelAngleToleranceDegrees" => "测量定义",
            "scoreThreshold" or "searchDownsample" or "candidateLimit" or "overlapThreshold" or "maximumMatches" or "angleMinimum" or "angleMaximum" or "angleStep" or "scaleMinimum" or "scaleMaximum" or "scaleStep" => "匹配搜索与精度",
            "downstreamImageMode" or "downstreamPadding" => "后续检测图像",
            _ => component switch { "GaussianBlur" or "MedianBlur" => "滤波参数", "Threshold" => "阈值分割", "Morphology" => "形态学处理", "FindCircles" or "FindLines" => "自动检测", _ => "算法参数" }
        };
        string description = key switch
        {
            "channel" => "默认按亮度转换；也可提取 B、G、R 单通道。灰度输入保持原值。",
            "input" => "此节点读取的配方公开图像输入名称；与 WPF 传入的输入契约对应。",
            "positionReference" => "输入图 ROI 使用手动位置；匹配中心模式跟随上游定位；位置修正模式还可跟随旋转和等比缩放。",
            "roiReference" => "输入图基准使用当前输入局部坐标；跟随位置修正时，ROI 保存在教学参考图坐标。",
            "matchOffsetX" or "matchOffsetY" => "模板中心到待测圆中心的偏移，按原图像素填写；匹配中心模式使用原图 X／Y 轴。",
            "matchSearchRadius" => "未绘制圆 ROI 时，在本次匹配中心周围使用的搜索半径。",
            "kernel" => "核宽度使用奇数。增大会增强平滑或形态学作用，也可能损失细节。",
            "kernelHeight" => "0 表示与核宽度相同；指定奇数可单独调整纵向作用范围。",
            "sigma" when component == "GaussianBlur" => "X 方向高斯标准差；0 由核尺寸自动计算。",
            "sigmaY" => "Y 方向高斯标准差；0 使用 X 方向 Sigma。",
            "borderMode" => "控制滤波核超出图像或 ROI 裁剪边界时的像素补全方式。",
            "borderValue" => "仅用于常量边界：-1 自动选择，0 黑色，255 白色。二值掩膜不接受其他灰度。",
            "method" when component == "Threshold" => "固定阈值适合光照稳定；Otsu／Triangle 自动选阈值；局部阈值适合光照不均。",
            "method" => "自动模式搜索多个候选；卡尺模式沿指定边缘采点并拟合单个几何目标。",
            "operation" when component == "Morphology" => "开运算去除小亮点，闭运算填小暗隙；梯度提取边界，顶帽／黑帽增强亮／暗细节。",
            "operation" => "选择本次几何测量的定义；输入对象类型、有效性和公差随定义检查。",
            "value" when component == "Threshold" => "只有固定阈值方式使用此值；正常方向下，大于阈值的像素输出白色。",
            "invert" => "交换前景和背景，输出仍为 0／255 二值掩膜。",
            "blockSize" => "局部阈值的邻域宽高；使用 3～255 的奇数，窗口越大越偏向整体亮度。",
            "offset" => "局部阈值 = 邻域均值 − C；C 越大，正常方向下越容易输出白色。",
            "kernelShape" => "矩形覆盖完整窗口；椭圆适合圆滑结构；十字只沿水平和竖直方向作用。",
            "iterations" => "形态学重复次数；宽核和多次迭代会同时增强尺寸变化。",
            "caliperCount" => "沿目标边界均匀布置的采样窗口数量；最终可用点数还受极性、阈值和剔除影响。",
            "sampleSpacing" => "沿搜索带长度方向的采样站间距。间距越小点集越密；每站独立搜索边缘，缺失站不会补出虚构点。",
            "maximumPoints" => "允许的最大采样站数量。减小间距或加长搜索带超过此上限时，请调整范围或步距；不会静默截断点集。",
            "caliperPlacement" => "跟随边界沿指定 ROI 边排列；搜索带中心沿中线排列；手动指引用角度、长度或半径定义。",
            "roiEdge" => "指定矩形的一条边布置卡尺，其他三条边不参与本次直线拟合。",
            "roiEdgeIndex" => "选择多边形相邻顶点组成的参考边；修改顶点后应核对该边是否仍存在。",
            "radius" => "手动指引模式的参考半径；0 根据输入尺寸估计。它不是最终测得的半径。",
            "guideLength" => "手动指引线的采样范围；0 根据输入图尺寸自动选择。",
            "angleDegrees" => "手动指引线相对原图 +X 的方向，图像 Y 轴向下。",
            "startAngle" or "endAngle" => "限定圆卡尺覆盖的弧段，角度跨度为 10～360°；圆环 ROI 模式使用 ROI 自身的角度。",
            "reverseSearch" => "反转卡尺搜索法向；第一／最后边缘和极性均按反转后的方向解释。",
            "projectionWidth" => "沿边缘切向汇总的像素数，必须为奇数；增大能抑制噪声，但可能混入相邻结构。",
            "searchLength" => "沿边缘法向搜索的长度。搜索带模式由 ROI 高度控制；圆环模式由 ROI 环宽控制。",
            "projectionStatistic" => "均值投影速度快；中位数投影能抑制窗口内较窄的划痕和亮暗离群像素。",
            "subpixelRefinement" => "对梯度峰值进行抛物线插值；关闭后使用离散采样位置，不改变 ROI，也不会把最终拟合坐标强制取整。",
            "sigma" => "对一维投影平滑；越大越能抑制噪声，但需更高的搜索窗口。高度至少为 6 × Sigma + 6 px。",
            "polarity" or "firstPolarity" or "secondPolarity" => "按卡尺箭头方向判断暗→亮或亮→暗；方向反转时应一并核对极性。",
            "edgeSelection" => "同一个窗口存在多条边时，选择搜索方向上的第一条、最后一条或最强边缘。",
            "caliperThreshold" => "一维投影梯度的最低强度；过高会丢点，过低可能选到纹理。",
            "edgeThreshold" when component is "LineCaliper" or "CircleCaliper" or "MeasureEdgePair" or "FindEdgePoints" => "一维投影梯度的最低强度；过高会丢点，过低可能选到纹理。",
            "edgeThreshold" => "自动找圆所用的边缘检测高阈值；与卡尺梯度强度阈值分别设置。",
            "dp" => "霍夫累加器与工作图的分辨率比例；1 使用相同分辨率，增大可能加速但降低中心分辨率。",
            "minimumDistance" => "两个候选圆心之间的最小间距，按原图像素设置。",
            "minimumRadius" or "maximumRadius" => "自动找圆的半径范围，按原图像素设置；不是直径，且下限不能超过上限。",
            "voteThreshold" when component == "FindCircles" => "圆候选的累加投票阈值；降低会增加候选及误检。",
            "voteThreshold" => "直线候选的累加投票阈值；降低会增加候选及纹理线。",
            "edgeLow" or "edgeHigh" => "Canny 的低／高阈值；低阈值必须小于高阈值，改变梯度核或幅值方式后应重新确认。",
            "minimumLength" => "自动检测直线段的最小长度，按原图像素设置。",
            "maximumGap" => "自动检测时允许连接的线段断点间距，按原图像素设置。",
            "removePointCount" => "先剔除偏差最大的指定点数，再按最大拟合偏差继续筛选；实际剔除数可能更多。",
            "maximumDeviation" => "有效点到最终拟合几何的最大允许偏差，按原图像素计算。",
            "minimumCoverage" when component == "FindEdgePoints" => "有效边缘点占采样站数量的最低比例；不足时保留本次诊断点，但不作为有效点集继续测量。",
            "minimumCoverage" => "最终保留点数占请求卡尺数的最低比例；不足时返回无有效拟合结果。",
            "outputAnnulusWidth" => "拟合圆两侧的结果环带宽度。设为 0 只显示拟合圆周；正值会额外显示内、外边界。此项不控制画笔粗细。",
            "lineWidth" or "overlayLineWidth" => "结果画笔粗细，范围 0.5～10，不能设为 0。圆查找要去掉内外环边界，请将“结果圆环带宽”设为 0。",
            "overlayColor" or "color" => "使用 #RRGGBB 格式；默认结果为绿色 #00FF00。",
            "minimumCount" or "maximumCount" or "expected" => "用于数量合格判定，不能强制算法检测到指定个数。",
            "resultIndex" => "从全部目标中选择一个供测量和后续定位使用；从 0 开始。",
            "scoreThreshold" => "归一化匹配分数下限；降低会增加候选，也可能增加误匹配。",
            "overlapThreshold" => "重叠候选的抑制阈值；越小越严格，可能合并相邻目标。",
            "searchDownsample" => "粗搜索降采样倍数；0 自动选择。粗搜后仍在原图邻域细化位置。",
            "candidateLimit" => "限制粗搜后进入精搜的候选数；增加可能更完整，也会增加耗时。",
            "maximumMatches" => "最多保留的匹配目标数量；数量合格条件请另外设置。",
            "angleStep" => "角度搜索间隔；越小搜索越密，耗时也越高。",
            "angleMinimum" or "angleMaximum" => "模板旋转搜索范围；上下限相同仅搜索该角度，下限不能超过上限。",
            "scaleMinimum" or "scaleMaximum" => "模板尺度搜索范围；1 表示教学尺寸，上下限相同仅搜索该尺度。",
            "scaleStep" => "尺度搜索间隔；越小搜索越密，耗时也越高。",
            "preBlurKernel" => "自动找圆前的高斯核；1 不进行空间平滑，默认 5 保持原有行为。单位为检测工作图像素。",
            "preBlurSigma" => "自动找圆前的高斯标准差；0 由核尺寸自动计算。",
            "edgeAperture" => "Canny 梯度核可取 3、5、7；变化后应重新确认高低阈值。",
            "edgeL2Gradient" => "启用欧氏梯度幅值；关闭使用绝对值和，与原有行为相同。",
            "parallelAngleToleranceDegrees" => "两条线的最小夹角超过此值时，平行间距返回无效，不使用 0 代替。",
            "distanceMode" => "圆心距离或圆周最短间距；两种数值均输出，公差判定使用当前选择的方式。",
            "firstIndex" or "secondIndex" => "从对应输入的实际目标列表选择测量对象，从 0 开始；无目标或越界时返回无效。",
            "lineIndex" => "从参考线来源的实际直线列表选择一条无限直线，从 0 开始；输入无直线或索引越界时不生成距离。",
            "statistic" => "选择用于主测量值和公差判定的统计量。所有逐点垂距及最小、最大、平均、均方根、标准差、极差仍会一并输出。",
            "displayEveryNth" => "每隔多少个点显示一条垂足连线，减少密集标记遮挡；不影响计算和导出的完整距离列表。",
            "showPoints" => "显示每个真实采到的边缘点；标记单独显示，不写入图像像素。",
            "showCurve" => "将连续采样站的边缘点连接成曲线；缺失站两侧不跨越连线，也不拟合为直线或圆。",
            "firstPoint" or "secondPoint" => "使用拟合线段的起点、终点或中点参与点类测量。",
            "expectedWidth" => "双边卡尺优先寻找接近期望间距的两条边；它不能替代实际测量。",
            "minimumWidth" or "maximumWidth" => "双边卡尺允许的候选间距范围，单位 px；与最终尺寸公差分别设置。",
            "pairSelection" => "在符合方向、极性和间距的候选边缘对中，选择最接近期望宽度或对比度最高的一对。",
            "minimum" or "maximum" => "轮廓多边形面积的筛选范围，包含上下限，单位为原图像素平方。",
            "toleranceEnabled" => "启用后对有效测量值进行尺寸判定；没有目标或无效几何仍返回 Unknown。",
            "toleranceMode" => "上下限直接指定允许范围；名义值模式使用名义值加上下偏差。",
            "lowerLimit" or "upperLimit" => "尺寸合格范围，包含两个边界；与测量结果使用相同单位，长度为 px、角度为 °。",
            "nominalValue" => "名义尺寸；最终允许范围为名义值加下偏差、上偏差。",
            "lowerDeviation" or "upperDeviation" => "相对于名义值的允许偏差，可为负数；下偏差不能超过上偏差。",
            "downstreamImageMode" => "选择把原图和定位中心传给后续检测，或使用选中目标的裁剪图。",
            "downstreamPadding" => "为选中模板目标的后续检测图像增加留边，单位为原图像素。",
            "largeImageMode" => "自动缩小只影响检测工作图，结果换算回原图坐标；精细测量优先使用小 ROI 和卡尺。",
            _ => ""
        };
        double step = p["step"] is { } configured ? Number(configured) : p["type"]!.GetValue<string>() == "integer" ? 1 :
            key is "scoreThreshold" or "overlapThreshold" or "minimumCoverage" or "dp" or "scaleMinimum" or "scaleMaximum" or "scaleStep" ? .01 : .1;
        string unit = label.Contains("px²", StringComparison.Ordinal) ? "px²" : label.Contains("px", StringComparison.Ordinal) ? "px" : label.Contains('°') ? "°" : "";
        return new(p["group"]?.GetValue<string>() ?? group, p["description"]?.GetValue<string>() ?? description, step, p["unit"]?.GetValue<string>() ?? unit);
    }

    public static bool Visible(string component, string key, JsonObject values)
    {
        if (component == "FindEdgePoints")
        {
            string mode = values["curveMode"]?.GetValue<string>() ?? "raw";
            if (key is "filterWindow" or "outlierTolerance" or "smoothCurve") return mode != "raw";
            if (key == "maximumGapLength") return mode == "reconstruct";
        }
        if (component == "BlobAnalysis" && key == "imageThreshold") return (values["imageThresholdMethod"]?.GetValue<string>() ?? "fixed") == "fixed";
        if (component is "FindCircles" or "CircleCaliper" && values["roi"]?["shape"]?.GetValue<string>() == "annulus" && key is "searchLength" or "radius" or "startAngle" or "endAngle") return false;
        if (component == "Threshold")
        {
            string method = values["method"]?.GetValue<string>() ?? "fixed";
            if (key == "value") return method == "fixed";
            if (key is "blockSize" or "offset") return method is "adaptiveMean" or "adaptiveGaussian";
        }
        if (component == "Morphology" && key == "borderValue") return (values["borderMode"]?.GetValue<string>() ?? "constant") == "constant";
        return true;
    }

    public static string Limits(string component, JsonObject p)
    {
        if (p["type"]!.GetValue<string>() is not ("integer" or "number")) return "";
        var help = Describe(component, p);
        string range = p["min"] is null ? "" : $"范围 {p["min"]}～{p["max"]}";
        string odd = p["odd"]?.GetValue<bool>() == true ? p["allowZero"]?.GetValue<bool>() == true ? " · 0 或奇数" : " · 奇数" : "";
        return $"{range}{odd} · 步长 {help.Step.ToString("0.########", CultureInfo.InvariantCulture)} {help.Unit}".Trim();
    }

    public static JsonNode Parse(JsonObject p, string text)
    {
        string type = p["type"]!.GetValue<string>(), label = p["label"]!.GetValue<string>();
        if (type is not ("integer" or "number")) return JsonValue.Create(text)!;
        JsonNode value;
        if (type == "integer")
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) throw new ArgumentException(label + "需要整数。");
            value = JsonValue.Create(integer)!;
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new ArgumentException(label + "需要有限数字。");
            value = JsonValue.Create(number)!;
        }
        double n = Number(value);
        if (p["min"] is { } min && n < Number(min) || p["max"] is { } max && n > Number(max))
            throw new ArgumentException($"{label}允许范围为 {p["min"]}～{p["max"]}。");
        if (p["odd"]?.GetValue<bool>() == true && n % 2 == 0 && !(n == 0 && p["allowZero"]?.GetValue<bool>() == true))
            throw new ArgumentException(label + (p["allowZero"]?.GetValue<bool>() == true ? "必须为 0 或奇数。" : "必须为奇数。"));
        return value;
    }

    public static JsonNode Next(string component, JsonObject p, string text, int direction)
    {
        double current = Number(Parse(p, text)), step = Describe(component, p).Step;
        double next = (double)((decimal)current + Math.Sign(direction) * (decimal)step);
        if (p["odd"]?.GetValue<bool>() == true && p["allowZero"]?.GetValue<bool>() == true && (current == 0 || current == 1 && direction < 0)) next = direction > 0 ? 1 : 0;
        if (p["min"] is { } min) next = Math.Max(next, Number(min));
        if (p["max"] is { } max) next = Math.Min(next, Number(max));
        return Parse(p, next.ToString(p["type"]!.GetValue<string>() == "integer" ? "0" : "G", CultureInfo.InvariantCulture));
    }

    private static double Number(JsonNode value) => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
}

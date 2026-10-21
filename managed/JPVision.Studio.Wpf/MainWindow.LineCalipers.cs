using JPVision.Editor.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private void BuildLineSamplingSummary(string id, RoiRegion roi, JsonObject parameters, string tagPrefix = "line")
    {
        double Number(string key, double fallback) => parameters[key] is JsonNode value ? double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) : fallback;
        int count = (int)Number("caliperCount", 24), removal = (int)Number("removePointCount", 0);
        double coverage = Number("minimumCoverage", .5), deviation = Number("maximumDeviation", 2);
        int required = Math.Max(2, (int)Math.Ceiling(count * coverage)), allowedRemoval = Math.Max(0, count - required);
        bool strip = parameters["caliperPlacement"]?.GetValue<string>() == "roiCenter";
        double height = strip && roi.Shape is "rectangle" or "rotatedRectangle" ? roi.Height : Number("searchLength", 60);
        double width = Number("projectionWidth", 5), samplesPerCaliper = (2 * Math.Ceiling(height / 2) + 1) * width;
        int budgetMaximum = double.IsFinite(samplesPerCaliper) && samplesPerCaliper > 0 ? (int)Math.Min(32768, Math.Floor(CaliperLayout.EdgePointSamplingBudget / samplesPerCaliper)) : 0;
        double? spacing = null, length = null;
        bool geometryKnown = roi.Shape != "none" || inputNode == id && inputFrame is not null;
        if (geometryKnown && count is >= 4 and <= 32768)
        {
            var info = inputNode == id ? inputFrame?.Info : null;
            var windows = CaliperLayout.Create(roi, parameters, false, info?.Width ?? 32768, info?.Height ?? 32768, true);
            if (windows.Count > 1)
            {
                double dx = windows[^1].X - windows[0].X, dy = windows[^1].Y - windows[0].Y;
                spacing = Math.Sqrt(dx * dx + dy * dy) / (windows.Count - 1);
                length = spacing * (strip || parameters["caliperPlacement"]?.GetValue<string>() == "roiBoundary" ? count : count - 1);
            }
        }
        string range = spacing is double step && length is double span ? $"采样长度约 {span:0.##} px，沿长度方向每 {step:0.###} px 一把。" : "绘制搜索带后可查看沿长度方向的采样间隔。";
        var summary = Note($"{count} 把卡尺。{range}拟合至少保留 {required} 点（{coverage:P0}）；当前指定剔除 {removal} 点，最多可指定 {allowedRemoval} 点。当前卡尺宽高下最多支持 {budgetMaximum} 把。", false);
        summary.Tag = tagPrefix + ":sampling-summary"; Parameters.Children.Add(summary);
        string state = count is < 4 or > 32768 || budgetMaximum < 1 ? "当前卡尺数量或宽高无效，请检查采样设置。" : removal > allowedRemoval
            ? $"指定剔除 {removal} 点后无法满足最低 {required} 点，请将剔除点数调至 {allowedRemoval} 以内，或重新设置卡尺数量／最低有效比例。"
            : count > budgetMaximum
                ? $"当前宽高最多允许 {budgetMaximum} 把，已设 {count} 把；组合搜索量过大，请减少卡尺数量、投影宽度或搜索高度。"
                : "当前采样设置可执行。预览最多显示 256 个窗口，实际按设定数量采样。";
        var stateNote = Note(state, false); stateNote.Tag = tagPrefix + ":sampling-state"; Parameters.Children.Add(stateNote);
        Parameters.Children.Add(Note($"最大拟合偏差 {deviation:0.######} px，使用当前输入图的实际像素，不随窗口缩放改变。采样点仅表示找到边缘；直线度、剔点和有效比例均通过后才输出拟合直线。"));
    }

    private void AddCaliperDiagnostics(StackPanel target, JsonElement packet, JsonElement caliper, bool circleFit)
    {
        bool fitted = caliper.TryGetProperty("fitSucceeded", out var succeeded) ? succeeded.GetBoolean() : packet.GetProperty("found").GetBoolean();
        string Text(string field) => caliper.TryGetProperty(field, out var value) ? ScalarText(value) : "未提供";
        var counts = Note($"{(fitted ? "卡尺拟合（剔除后）" : "卡尺采样")}：布置 {Text("requestedCount")} 把 · 有效采样 {Text("validSamplingCalipers")} 把 · 找到边缘 {Text("sampledCount")} 点\n未找到边缘 {Text("missingCount")} 把 · 剔除 {Text("rejectedCount")} 点 · 保留 {Text("retainedCount")} 点 · 最低需 {Text("minimumRequiredPoints")} 点", false);
        counts.Tag = circleFit ? "circle:fit-counts" : "line:fit-counts"; target.Children.Add(counts);
        target.Children.Add(Note($"指定剔除 {Text("requestedRemovalCount")} 点；最大拟合偏差 {Text("maximumDeviation")} px（当前输入图像素，{ResultCoordinates(packet)}，不是屏幕像素）。"));
        if (!circleFit)
        {
            foreach (var (field, label) in new[] { ("effectiveSamplingSpacing", "实际卡尺间隔"), ("initialMaximumDeviation", "首次拟合最大偏差"), ("initialRmsError", "首次拟合 RMS"), ("maximumRetainedDeviation", fitted ? "最终最大偏差" : "保留点候选线最大偏差"), ("finalRmsError", fitted ? "最终 RMS" : "保留点候选线 RMS") })
                if (caliper.TryGetProperty(field, out var value))
                {
                    var note = Note(label + "：" + (value.ValueKind == JsonValueKind.Number ? ScalarText(value) + " px" : "无有效候选"), false);
                    note.Tag = "line:fit:" + field; target.Children.Add(note);
                }
            if (caliper.TryGetProperty("fitIterations", out var iterations) && iterations.ValueKind == JsonValueKind.Number)
            {
                string limit = caliper.TryGetProperty("fitIterationLimit", out var maximum) && maximum.ValueKind == JsonValueKind.Number ? " / " + ScalarText(maximum) : "";
                var note = Note("拟合次数：" + ScalarText(iterations) + limit, false); note.Tag = "line:fit:iterations"; target.Children.Add(note);
            }
        }
        if (fitted) return;
        string failure = caliper.TryGetProperty("fitFailureReason", out var reason) ? reason.GetString() ?? "" : "";
        string explanation = failure switch
        {
            "InsufficientEdgePoints" => $"找到的边缘点不足最低 {Text("minimumRequiredPoints")} 点。请核对搜索带位置、极性、卡尺高度及边缘阈值；沿长边可增加卡尺数量以加密采样。",
            "InsufficientInliers" => circleFit ? "剔点或按最大偏差筛选后，剩余圆周点不足最低有效比例。请检查圆弧、剔除数量和允许偏差。" : "采到了边缘，但剔点或最大偏差筛选后，保留点不足。请检查边缘是否有弯曲、台阶或毛刺，缩小到待测直线段，并核对剔点数、最低有效比例及允许偏差。",
            "DegenerateGeometry" => circleFit ? "点集接近共线或过于集中，无法确定稳定的圆；请扩大有效圆弧。" : "保留点重合或分布不足，无法确定稳定直线；请覆盖有足够长度的真实边缘。",
            "UnavailableSamplingArea" => "有效图像采样不足。请核对当前输入图的尺寸、搜索窗口是否过多落在图外，以及上游裁剪或定位是否有效。",
            "FitIterationLimit" => $"异常点筛选达到 {Text("fitIterationLimit")} 次仍未收敛，本次不输出直线。请缩小到单一直边，检查纹理、极性及异常点分布。",
            "NumericalFailure" or "InvalidCircle" => circleFit ? "点集未能得到有效圆，请检查采样分布和选边。" : "数值拟合未得到有效直线，请检查采样分布，尝试局部有效区域。",
            _ => circleFit ? "点集未通过圆拟合。" : "点集未通过直线拟合。"
        };
        Header(target, circleFit ? "未输出圆环的原因" : "未输出拟合直线的原因");
        var failureNote = Note(explanation, false); failureNote.Tag = circleFit ? "circle:fit-failure" : "line:fit-failure"; target.Children.Add(failureNote);
        target.Children.Add(Note("画面上的采样点和剔除点标记仅用于诊断，不代表拟合成功。偏差按当前输入图像素计算，缩小显示不会降低实际偏差。"));
    }
}

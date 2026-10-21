using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private VisionFixture? inputFixture;
    private bool FixtureActive(string id) => inputFrame is not null && inputNode == id && flow is not null &&
        FixturePositioning.UsesFixture(EffectiveParameters(id), inputFixture, flow.Spec(id).Type == "FindCircles");
    private void BuildFixturePanel(string id)
    {
        Header(Parameters, "工件定位基准");
        Parameters.Children.Add(Note("连接匹配模块的图像输出，图像会携带当前定位结果。使用当前匹配建立教学基准，后续 ROI 随平移、旋转及等比缩放跟随；旧配方的独立定位输入仍可使用。"));
        var reference = flow!.Node(id)["parameters"]?["reference"]?.AsObject();
        if (reference?.Count > 0)
            Parameters.Children.Add(Note($"已教学：({reference["centerX"]}, {reference["centerY"]}) · 角度 {reference["angleDegrees"]}° · 尺度 {reference["scale"]}"));
        else Parameters.Children.Add(Note("尚未教学定位基准。"));
        var teach = new Button { Content = "使用当前匹配建立基准", Margin = new Thickness(0, 0, 0, 8) };
        teach.Click += async (_, _) => await BusyAsync(() => TeachFixtureAsync(id));
        Parameters.Children.Add(teach);
        Parameters.Children.Add(Note("更换模板后请重新教学；原有测量 ROI 的参考坐标需要重新确认。"));
    }
    private async Task TeachFixtureAsync(string id)
    {
        if (flow is null || source is null) throw new InvalidOperationException("请先打开参考图像并连接定位来源。");
        bool separatePose = flow.Connections.Any(e => e!["toNode"]!.GetValue<string>() == id && e["toPort"]!.GetValue<string>() == "position");
        using var preview = await ExecuteAsync(flow.BindingRecipe(id, separatePose ? "position" : "input"), "", false);
        if (preview.Status != ExecutionStatus.Succeeded) throw new InvalidOperationException("定位来源执行失败，请检查模板和搜索参数。");
        var pose = separatePose ? preview.GetOutput<VisionPose>("InputPreview") : preview.GetImageAttachedOutput<VisionPose>("InputPreview", "Pose");
        if (!pose.Available || pose.Position is null) throw new InvalidOperationException("当前没有选中有效的匹配目标，无法教学。");
        var p = pose.Position;
        flow.SetParameter(id, "reference", new JsonObject { ["centerX"] = p.CenterX, ["centerY"] = p.CenterY,
            ["angleDegrees"] = p.AngleDegrees, ["scale"] = p.Scale, ["modelHash"] = pose.ModelHash });
        BuildInspector(); Log("位置修正已建立当前模板的参考基准。");
    }
    private void ReadInputFixture(JsonElement metadata) => inputFixture = metadata.TryGetProperty("fixture", out var fixture)
        ? fixture.Deserialize<VisionFixture>() : null;
}

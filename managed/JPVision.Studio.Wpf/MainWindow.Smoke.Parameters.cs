using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JPVision.Studio;

public partial class MainWindow
{
    private T ParameterControl<T>(string tag) where T : FrameworkElement => InspectorControls<T>().Single(c => c.Tag as string == tag);
    private void ParameterChoice(string key, string label) => ParameterControl<ComboBox>(key).SelectedItem = label;
    private void ParameterButton(string tag) => ParameterControl<Button>(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private void ParameterNumber(string key, string text)
    {
        var edit = ParameterControl<TextBox>(key); edit.Text = text;
        edit.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private async Task SmokeParameterDetailsAsync(string scenario)
    {
        const int width = 256, height = 192;
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            pixels[y * width + x] = (byte)((x - 128) * (x - 128) + (y - 96) * (y - 96) < 45 * 45 ? 230 : 20);
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8); ShowSource();
        flow!.Add("ExternalImage", 30, 80, "source");
        string type = scenario switch { "parameters-threshold" => "Threshold", "parameters-morphology" => "Morphology", "parameters-caliper" => "FindCircles", _ => "GaussianBlur" };
        if (type == "Morphology")
        {
            flow.Add("Threshold", 260, 80, "binary"); flow.Connect("source", "image", "binary", "input");
        }
        flow.Add(type, type == "Morphology" ? 500 : 300, 80, "tool");
        flow.Connect(type == "Morphology" ? "binary" : "source", type == "Morphology" ? "mask" : "image", "tool", "input");
        if (type == "FindCircles")
        {
            flow.SetParameter("tool", "method", JsonValue.Create("caliper"));
            flow.SetParameter("tool", "roi", RoiRegion.Circle(128, 96, 45).ToJson());
            flow.SetParameter("tool", "searchLength", JsonValue.Create(24.0));
        }
        Graph.Select("tool");
        Require(InspectorControls<Expander>().Any(e => e.Tag is string tag && tag.StartsWith("group:", StringComparison.Ordinal)), "parameter groups are missing");
        if (type == "Threshold")
        {
            ParameterChoice("method", "局部高斯阈值");
            Require(!InspectorControls<TextBox>().Any(c => c.Tag as string == "value"), "inactive fixed threshold is still editable");
            ParameterNumber("blockSize", "17"); ParameterNumber("offset", "-3.25"); ParameterButton("plus:offset");
            Require(flow.Node("tool")["parameters"]!["offset"]!.GetValue<double>() == -3.15, "decimal fine step did not reach the recipe");
            ParameterButton("reset:offset");
        }
        else if (type == "Morphology")
        {
            ParameterChoice("kernelShape", "十字"); ParameterChoice("operation", "膨胀");
            ParameterChoice("borderValue", "黑色（0）"); ParameterButton("plus:kernelHeight");
            Require(flow.Node("tool")["parameters"]!["kernelHeight"]!.GetValue<long>() == 1, "automatic height did not step to a valid odd size");
            ParameterNumber("kernelHeight", "5");
        }
        else if (type == "FindCircles")
        {
            ParameterChoice("projectionStatistic", "中位数投影"); ParameterControl<CheckBox>("subpixelRefinement").IsChecked = false;
            ParameterButton("plus:caliperCount"); ParameterButton("reset:caliperCount");
            flow.Undo(); Require(flow.Node("tool")["parameters"]!["caliperCount"]!.GetValue<long>() == 25, "parameter reset was not undoable"); flow.Redo();
            Require(InspectorControls<TextBlock>().Any(t => t.Text.Contains("不能设为 0", StringComparison.Ordinal)), "pen width explanation is missing");
        }
        else
        {
            ParameterNumber("sigma", "0.55"); ParameterButton("plus:sigma"); ParameterButton("plus:kernelHeight");
            ParameterChoice("borderMode", "复制边界像素");
        }
        flow.Publish("Output", "tool", type == "FindCircles" ? "result" : type is "Threshold" or "Morphology" ? "mask" : "image", false);
        await BusyAsync(RunOnceAsync); VerifyParameterDetails(scenario);
    }

    private void VerifyParameterDetails(string scenario)
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "refined parameters did not execute");
        var p = flow!.Node("tool")["parameters"]!;
        if (scenario == "parameters-caliper")
        {
            Require(p["projectionStatistic"]!.GetValue<string>() == "median" && !p["subpixelRefinement"]!.GetValue<bool>(), "saved caliper precision options changed");
            Require(result!.GetResult<VisionCircle>("Output").Count == 1, "median calipers did not output a fitted circle");
        }
        else
        {
            using var image = result!.GetOutputImage("Output"); Require(image.Info.Width == 256 && image.Info.Height == 192, "refined processing changed image dimensions");
            if (scenario == "parameters-threshold") Require(p["blockSize"]!.GetValue<long>() == 17 && p["offset"]!.GetValue<double>() == 2, "threshold window or reset did not roundtrip");
            if (scenario == "parameters-morphology") Require(p["kernelShape"]!.GetValue<string>() == "cross" && p["kernelHeight"]!.GetValue<long>() == 5, "rectangular morphology did not roundtrip");
            if (scenario == "parameters-gaussian") Require(Math.Abs(p["sigma"]!.GetValue<double>() - .65) < 1e-10 && p["kernelHeight"]!.GetValue<long>() == 1, "Gaussian fine step did not roundtrip");
        }
    }
}

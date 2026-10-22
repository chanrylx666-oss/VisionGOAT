using JPVision.Sdk;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeDeepLearningAsync()
    {
        string manifest = Path.GetFullPath(Argument("--model-test") ?? throw new InvalidOperationException("--model-test manifest required"));
        var repository = new DeepLearningModelRepository(CapturePath + ".models");
        var installed = await repository.ImportAsync(manifest);
        const int width = 320, height = 240;
        byte[] pixels = new byte[width * height];
        for (int y = 70; y < 100; y++) Array.Fill(pixels, (byte)255, y * width + 110, 45);
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 600, -50); ShowSource();
        flow!.Add("ExternalImage", 30, 120, "source"); flow.Add("DeepLearningDefect", 320, 120, "defect");
        flow.Connect("source", "image", "defect", "input");
        flow.Node("defect")["parameters"]!["modelRoot"] = repository.Root;
        flow.Node("defect")["parameters"]!["modelKey"] = installed.ModelKey;
        flow.Publish("Defects", "defect", "result", false); flow.Publish("Image", "defect", "image", false);
        Graph.Select("defect");
        Require(InspectorControls<Button>().Any(b => Equals(b.Tag, "deep-learning:import")), "model import action missing");
        Require(InspectorControls<ComboBox>().Any(b => Equals(b.Tag, "deep-learning:models")), "installed model selector missing");
        await BusyAsync(RunOnceAsync);
        Require(result?.Status == ExecutionStatus.Succeeded, result?.Data.GetRawText() ?? "no run");
        var defects = result!.GetDeepLearningDefects("Defects");
        Require(defects.Count == 1 && !defects.Passed && defects.TileCount > 1 && defects.TotalArea == 1350, "segmentation inference or tiled merge incorrect");
        var feature = defects.Features[0];
        Require(feature.X == 710 && feature.Y == 20 && feature.CentroidX == 732 && feature.CentroidY == 34.5, "defect coordinates lost input origin");
        Require(Results.Children.OfType<DataGrid>().Any(g => Equals(g.Tag, "deep-learning:results") && g.Items.Count == 1), "defect result table missing");
        using var output = result.GetOutputImage("Image");
        Require(output.CopyPixels().SequenceEqual(source.CopyPixels()), "inference changed clean image pixels");
        Require(ImageView.Overlays?.EnumerateArray().Any(o => o.GetProperty("color").GetString() == "#00FF00") == true, "green independent overlay missing");
        ParameterNumber("minimumArea", "2000"); await BusyAsync(RunOnceAsync);
        Require(result!.GetDeepLearningDefects("Defects").Count == 0 && result.GetDeepLearningDefects("Defects").Passed, "minimum area filter not applied");
        ParameterNumber("minimumArea", "10"); await BusyAsync(RunOnceAsync);
        _ = result!.GetOutput<JsonElement>("Defects");
    }
}

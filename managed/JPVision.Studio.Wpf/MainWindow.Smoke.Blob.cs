using JPVision.Editor.Core;
using JPVision.Sdk;
using System.Text.Json;
using System.Windows.Controls;

namespace JPVision.Studio;

public partial class MainWindow
{
    private async Task SmokeBlobAnalysisAsync()
    {
        const int width = 320, height = 240;
        byte[] pixels = Enumerable.Repeat((byte)20, width * height).ToArray();
        void Fill(int left, int top, int w, int h, byte value)
        {
            for (int y = top; y < top + h; y++) Array.Fill(pixels, value, y * width + left, w);
        }
        Fill(40, 40, 40, 30, 230); Fill(50, 50, 10, 10, 20);
        Fill(150, 60, 20, 50, 230); Fill(0, 140, 15, 30, 230); Fill(250, 200, 2, 2, 230);
        source!.Dispose(); source = VisionFrame.CopyFrom(pixels, width, height, width, VisionPixelFormat.Gray8, 600, -50); ShowSource();
        flow!.Add("ExternalImage", 30, 120, "source"); flow.Add("BlobAnalysis", 320, 120, "blob");
        flow.Connect("source", "image", "blob", "input"); flow.Publish("Blob", "blob", "result", false); flow.Publish("BlobImage", "blob", "image", false);
        Require(flow.Spec("blob").ToolboxVisible && flow.Spec("blob").VisibleOutputs.Count == 1 && flow.Spec("blob").VisibleOutputs[0].Type == "Image", "Blob is not available as an image-processing toolbox operator");
        Graph.Select("blob");
        Require(ParameterControl<ComboBox>("connectivity").Items.Count == 2 && !InspectorControls<TextBox>().Any(t => Equals(t.Tag, "connectivity")), "Blob connectivity allows unsupported values between 4 and 8");
        ParameterChoice("connectivity", "4 邻域（上下左右）"); ParameterChoice("connectivity", "8 邻域（含对角）");
        ParameterChoice("imageThresholdMethod", "Otsu 自动阈值");
        Require(!InspectorControls<TextBox>().Any(t => Equals(t.Tag, "imageThreshold")), "automatic Blob threshold still exposes an ignored manual threshold");
        ParameterChoice("imageThresholdMethod", "固定阈值"); ParameterNumber("imageThreshold", "128");
        await DrawRegionAsync("blob", "rectangle", false);
        ImageView.CommitRegion(RoiRegion.Rectangle(0, 0, 300, 220), false); ImageView.EndEdit();
        await EditRoiAsync();
        ImageView.CommitRegion(RoiRegion.Rectangle(0, 0, 300, 220), false); ImageView.EndEdit();
        await BusyAsync(RunOnceAsync);
        Require(result!.GetOutput<JsonElement>("Blob").GetProperty("count").GetInt32() == 4, "Blob segmentation missed a region before filtering");
        ParameterControl<CheckBox>("excludeBorder").IsChecked = true;
        await BusyAsync(RunOnceAsync);
        var borderFiltered = result!.GetOutput<JsonElement>("Blob");
        Require(borderFiltered.GetProperty("count").GetInt32() == 3 && borderFiltered.GetProperty("features").EnumerateArray().All(f => !f.GetProperty("touchesBoundary").GetBoolean()), "Blob border filter did not reject the actual ROI-edge region");
        ParameterNumber("minimumArea", "500"); ParameterChoice("sortOrder", "面积从大到小");
        await BusyAsync(RunOnceAsync); VerifyBlobAnalysis();
        ParameterNumber("minimumWidth", "30"); await BusyAsync(RunOnceAsync);
        Require(result!.GetOutput<JsonElement>("Blob").GetProperty("count").GetInt32() == 1 &&
            result.GetOutput<JsonElement>("Blob").GetProperty("features")[0].GetProperty("area").GetDouble() == 1100, "Blob width filtering did not operate independently from area");
        ParameterNumber("minimumWidth", "1"); ParameterNumber("minimumArea", "2000"); await BusyAsync(RunOnceAsync);
        var empty = result!.GetOutput<JsonElement>("Blob");
        Require(empty.GetProperty("dataAvailable").GetBoolean() && empty.GetProperty("count").GetInt32() == 0 && empty.GetProperty("features").GetArrayLength() == 0 &&
            empty.GetProperty("totalArea").GetDouble() == 0 && Results.Children.OfType<DataGrid>().Single(g => Equals(g.Tag, "blob:table")).Items.Count == 0, "empty Blob filtering reused previous regions or treated valid zero area as unavailable");
        ParameterNumber("minimumArea", "500"); await BusyAsync(RunOnceAsync); VerifyBlobAnalysis();
        Log("已验证 Blob：带孔区域实际面积、两个目标、贴边与尺寸筛选、空结果恢复、ROI编辑、独立绿色标记与原图坐标。");
    }

    private void VerifyBlobAnalysis()
    {
        Require(result?.Status == ExecutionStatus.Succeeded, "Blob analysis flow did not execute");
        var packet = result!.GetOutput<JsonElement>("Blob");
        Require(packet.GetProperty("resultKind").GetString() == "blobAnalysis" && packet.GetProperty("featureKind").GetString() == "region" &&
            packet.GetProperty("dataAvailable").GetBoolean() && packet.GetProperty("candidateCount").GetInt32() == 4 && packet.GetProperty("rejectedCount").GetInt32() == 2 &&
            packet.GetProperty("count").GetInt32() == 2 && packet.GetProperty("totalArea").GetDouble() == 2100, "Blob connected-component/filter summary is incorrect");
        var first = packet.GetProperty("features")[0]; var second = packet.GetProperty("features")[1];
        Require(first.GetProperty("area").GetDouble() == 1100 && second.GetProperty("area").GetDouble() == 1000 && first.GetProperty("perimeter").GetDouble() == 180 &&
            packet.GetProperty("perimeterMethod").GetString() == "pixelEdges", "Blob area or pixel-edge perimeter incorrectly filled the hole or used contour polygon area");
        Require(Math.Abs(first.GetProperty("centroidX").GetDouble() - (600 + 65950.0 / 1100)) < 1e-8 && Math.Abs(first.GetProperty("centroidY").GetDouble() - 4.5) < 1e-8 &&
            first.GetProperty("x").GetInt32() == 640 && first.GetProperty("y").GetInt32() == -10 && first.GetProperty("width").GetInt32() == 40 && first.GetProperty("height").GetInt32() == 30,
            "Blob centroid or bounding box did not retain source image origin");
        Require(Math.Abs(first.GetProperty("extent").GetDouble() - 1100.0 / 1200) < 1e-9 && first.GetProperty("aspectRatio").GetDouble() == 40.0 / 30 &&
            !first.GetProperty("touchesBoundary").GetBoolean(), "Blob shape descriptors or boundary status are missing");
        Require(Results.Children.OfType<DataGrid>().Single(g => Equals(g.Tag, "blob:table")).Items.Count == 2 &&
            Results.Children.OfType<TextBlock>().Single(t => Equals(t.Tag, "blob:total-area")).Text.Contains("2100", StringComparison.Ordinal), "Blob areas and coordinates are not displayed in the results panel");
        using var image = result.GetOutputImage("BlobImage");
        Require(image.Info.Width == 320 && image.Info.Height == 240 && image.Info.OriginX == 600 && image.Info.OriginY == -50 && image.CopyPixels().SequenceEqual(source!.CopyPixels()),
            "Blob annotations or its detection ROI changed the clean input image");
        Require(ImageView.Overlays?.EnumerateArray().Any(o => o.TryGetProperty("color", out var color) && color.GetString() == "#00FF00") == true, "Blob green annotations are missing");
    }
}

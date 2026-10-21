using JPVision.Editor.Core;
using Xunit;

namespace JPVision.Tests;

public sealed class ErrorMessageTests
{
    [Fact]
    public void NativeJsonIsShownAsAModuleSpecificActionableMessage()
    {
        string raw = "{\"code\":5,\"message\":\"Hough original-resolution input 5120 x 5120 exceeds 4,194,304 pixels. Select a smaller module ROI or use adaptive large-image mode.\",\"nodeId\":\"detect\"}";
        string display = VisionErrorMessage.Describe(raw, id => id == "detect" ? "圆查找" : null);
        Assert.Contains("圆查找", display);
        Assert.Contains("5120", display);
        Assert.Contains("ROI", display);
        Assert.Contains("自动", display);
        Assert.DoesNotContain("\"code\"", display);
        Assert.DoesNotContain("{", display);
    }
    [Fact]
    public void OldHoughErrorAlsoExplainsTheModuleRoi()
    {
        string display = VisionErrorMessage.Describe("Hough detection accepts up to 4 megapixels. Use CropRect to select a smaller ROI.");
        Assert.Contains("检测 ROI", display);
        Assert.Contains("自动", display);
    }
    [Fact]
    public void UnknownJsonKeepsItsReasonAndStableNodeId()
    {
        string display = VisionErrorMessage.Describe("{\"code\":2,\"message\":\"Unexpected port\",\"nodeId\":\"n42\"}");
        Assert.Contains("Unexpected port", display);
        Assert.Contains("n42", display);
        Assert.DoesNotContain("{", display);
    }
    [Fact]
    public void EvenCaliperWidthExplainsTheAllowedValues()
    {
        string display = VisionErrorMessage.Describe("Caliper projection width must be odd.");
        Assert.Contains("1～63", display);
        Assert.Contains("奇数", display);
        Assert.Contains("5、7、9", display);
    }
    [Theory]
    [InlineData("Circular boundary calipers require a circle ROI.", "圆形 ROI")]
    [InlineData("Line boundary calipers require a rectangle or polygon ROI.", "矩形或多边形")]
    [InlineData("Line strip calipers require a rectangle or rotated rectangle ROI.", "绘制直线卡尺搜索带")]
    [InlineData("Line strip search height must not exceed 512 pixels.", "搜索高度不能超过")]
    [InlineData("Polygon caliper edge index exceeds ROI vertex count.", "重新选择")]
    [InlineData("Caliper search length is too short for the smoothing sigma.", "卡尺高度")]
    public void CaliperSetupErrorsExplainTheNextAction(string native, string expected)
    {
        Assert.Contains(expected, VisionErrorMessage.Describe(native));
    }
    [Theory]
    [InlineData("A")] [InlineData("B")]
    public void LineMeasurementInputErrorsIdentifyTheInputAndRequiredTool(string input)
    {
        string display = VisionErrorMessage.Describe($"This operation requires a line at input {input}.");
        Assert.Contains("输入 " + input, display); Assert.Contains("直线查找", display); Assert.Contains("索引", display);
    }
    [Theory]
    [InlineData("Line-circle distance requires a circle at input B.", "输入 B")]
    [InlineData("Circle distance requires a circle at both selected indices.", "两个输入")]
    public void CircularDistanceErrorsExplainTheRequiredGeometry(string message, string input)
    {
        string display = VisionErrorMessage.Describe(message);
        Assert.Contains("圆查找", display); Assert.Contains(input, display); Assert.Contains("索引", display);
    }
    [Fact]
    public void ExistingPlainTextErrorsRemainReadable()
    {
        Assert.Equal("file not found", VisionErrorMessage.Describe("file not found"));
        Assert.Contains("模板尚未学习", VisionErrorMessage.Describe("Template has not been taught or imported."));
        Assert.Equal("{not json}", VisionErrorMessage.Describe("{not json}"));
    }
}

using JPVision.Editor.Core;
using System.Text.Json.Nodes;
using Xunit;

namespace JPVision.Tests;

public sealed class ParameterEditorTests
{
    private static JsonObject Numeric(string name, double min, double max, bool integer = false) => new()
    { ["name"] = name, ["label"] = name == "lineWidth" ? "线宽" : name, ["type"] = integer ? "integer" : "number", ["min"] = min, ["max"] = max };

    [Fact]
    public void ZeroCircleBandIsValidButZeroPenWidthReportsItsActualBounds()
    {
        Assert.Equal(0, ParameterEditor.Parse(Numeric("outputAnnulusWidth", 0, 512), "0").GetValue<double>());
        var error = Assert.Throws<ArgumentException>(() => ParameterEditor.Parse(Numeric("lineWidth", .5, 10), "0"));
        Assert.Contains("0.5～10", error.Message);
        Assert.Contains("结果圆环带宽", ParameterEditor.Describe("BuildFeatureOverlay", Numeric("lineWidth", .5, 10)).Description);
    }

    [Fact]
    public void SmallStepsDoNotAccumulateBinaryFloatingPointErrorsAndStopAtLimits()
    {
        var p = Numeric("sigma", 0, 5);
        Assert.Equal(.3, ParameterEditor.Next("LineCaliper", p, "0.2", 1).GetValue<double>());
        Assert.Equal(5, ParameterEditor.Next("LineCaliper", p, "5", 1).GetValue<double>());
        Assert.Equal(0, ParameterEditor.Next("LineCaliper", p, "0", -1).GetValue<double>());
    }

    [Fact]
    public void OddWindowSteppingRespectsAutomaticZeroSentinel()
    {
        var p = Numeric("kernelHeight", 0, 101, true); p["odd"] = true; p["allowZero"] = true; p["step"] = 2;
        Assert.Equal(1, ParameterEditor.Next("GaussianBlur", p, "0", 1).GetValue<long>());
        Assert.Equal(3, ParameterEditor.Next("GaussianBlur", p, "1", 1).GetValue<long>());
        Assert.Equal(0, ParameterEditor.Next("GaussianBlur", p, "1", -1).GetValue<long>());
        Assert.Throws<ArgumentException>(() => ParameterEditor.Parse(p, "2"));
    }

    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("1e1000")] [InlineData("abc")]
    public void InvalidNumbersNeverBecomeAnExecutableParameter(string text) =>
        Assert.Throws<ArgumentException>(() => ParameterEditor.Parse(Numeric("radius", 0, 512), text));

    [Fact]
    public void ThresholdShowsOnlyTheControlsUsedByTheSelectedMethod()
    {
        var p = new JsonObject { ["method"] = "fixed" };
        Assert.True(ParameterEditor.Visible("Threshold", "value", p)); Assert.False(ParameterEditor.Visible("Threshold", "blockSize", p));
        p["method"] = "otsu";
        Assert.False(ParameterEditor.Visible("Threshold", "value", p)); Assert.False(ParameterEditor.Visible("Threshold", "offset", p));
        p["method"] = "adaptiveGaussian";
        Assert.False(ParameterEditor.Visible("Threshold", "value", p)); Assert.True(ParameterEditor.Visible("Threshold", "blockSize", p)); Assert.True(ParameterEditor.Visible("Threshold", "offset", p));
    }

    [Fact]
    public void CustomComponentsCanSupplyTheirOwnGroupHelpAndStep()
    {
        var p = Numeric("custom", 0, 10); p["group"] = "自定义"; p["description"] = "算法专用提示"; p["step"] = .025; p["unit"] = "px";
        var help = ParameterEditor.Describe("MyComponent", p);
        Assert.Equal("自定义", help.Group); Assert.Equal("算法专用提示", help.Description); Assert.Equal(.025, help.Step); Assert.Equal("px", help.Unit);
    }

    [Fact]
    public void CustomGrayBorderParameterDoesNotInheritBinaryMorphologyRestrictions()
    {
        var p = Numeric("borderValue", 0, 255, true);
        Assert.Equal(128, ParameterEditor.Parse(p, "128").GetValue<long>());
    }

    [Fact]
    public void CircularAnnulusUsesItsRoiWidthAndAnglesInsteadOfInactiveGuideFields()
    {
        var p = new JsonObject { ["roi"] = new JsonObject { ["shape"] = "annulus" } };
        foreach (string key in new[] { "searchLength", "radius", "startAngle", "endAngle" }) Assert.False(ParameterEditor.Visible("FindCircles", key, p));
        Assert.True(ParameterEditor.Visible("FindCircles", "caliperCount", p)); Assert.True(ParameterEditor.Visible("FindCircles", "projectionWidth", p));
    }

    [Theory]
    [InlineData("Adaptive threshold block size must be odd.", "局部阈值窗口")]
    [InlineData("Kernel height must be zero or odd.", "核高度")]
    [InlineData("Circle pre-blur kernel size must be odd.", "检测前平滑核")]
    [InlineData("Canny aperture size must be odd (3, 5 or 7).", "3、5 或 7")]
    [InlineData("Binary morphology border value must be -1 (automatic), 0 or 255.", "边界灰度")]
    [InlineData("Edge low threshold must be below high threshold.", "低阈值")]
    [InlineData("Kernel size must be odd.", "核尺寸")]
    [InlineData("Caliper removal count leaves too few points for minimum coverage.", "剔除点数过多")]
    public void RefinedParameterValidationHasActionableChineseMessages(string message, string expected) =>
        Assert.Contains(expected, VisionErrorMessage.Describe(message));
}

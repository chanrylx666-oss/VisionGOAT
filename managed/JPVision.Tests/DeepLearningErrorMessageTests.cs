using JPVision.Editor.Core;
using Xunit;

namespace JPVision.Tests;

public sealed class DeepLearningErrorMessageTests
{
    [Theory]
    [InlineData("RuntimeUnavailable", "运行环境不可用")]
    [InlineData("ModelMissing", "尚未选择模型")]
    [InlineData("ModelInvalid", "模型校验失败")]
    [InlineData("TileInvalid", "重叠宽度")]
    [InlineData("ThresholdInvalid", "0～1")]
    [InlineData("OutputInvalid", "不能判为合格")]
    [InlineData("InferenceFailed", "不能判为合格")]
    [InlineData("ResultLimit", "没有被截断为合格")]
    public void RuntimeErrorsExplainRecoveryAndKeepDiagnostic(string kind, string expected)
    {
        string raw = "DeepLearning" + kind + ": test detail";
        string wrapped = System.Text.Json.JsonSerializer.Serialize(new { nodeId = "detect", message = raw });
        string text = VisionErrorMessage.Describe(wrapped, _ => "深度学习缺陷检测");
        Assert.Contains(expected, text); Assert.Contains(raw, text); Assert.StartsWith("深度学习缺陷检测（detect）", text);
    }
}

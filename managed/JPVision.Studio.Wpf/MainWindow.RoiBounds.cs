namespace JPVision.Studio;

public partial class MainWindow
{
    private void BuildRoiBoundsNote(string id)
    {
        if (inputNode == id && inputFrame is not null)
        {
            var info = inputFrame.Info;
            var size = Note($"当前输入：{info.Width} × {info.Height} px；原点 ({info.OriginX}, {info.OriginY})。", false);
            size.Tag = "roi:input-size";
            Parameters.Children.Add(size);
        }
        var note = Note(IsAnnularUnwrap(id)
            ? "圆环可以贴边或部分超界，采样保持所绘圆环，仅使用输入图中的有效像素。完全没有有效像素时输出无效，不替换为全图。"
            : "ROI 可以贴边或部分超界：普通图像处理使用有效交集；卡尺保持绘制位置，仅有效采样参与拟合。完全没有有效像素时结果无效，不替换为全图。");
        note.Tag = "roi:bounds";
        Parameters.Children.Add(note);
        Parameters.Children.Add(Note("ROI 坐标以当前算子输入图为准；圆环展开后使用展开图坐标，输出保留该图的坐标平面与原点。"));
    }
}

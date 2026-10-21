using JPVision.Sdk;
using System.Text.Json.Nodes;

namespace JPVision.Editor.Core;

public static class TemplateLearning
{
    public static JsonObject Learn(byte[] pixels, int width, int height, int stride, VisionPixelFormat format, RoiRegion region)
    {
        int channels = format == VisionPixelFormat.Gray8 ? 1 : format == VisionPixelFormat.Bgr24 ? 3 : throw new ArgumentException("不支持的图像格式。");
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || stride < (long)width * channels || pixels.Length < (long)stride * height)
            throw new ArgumentException("图像尺寸或步长无效。");
        if (region.Shape == "none")
            throw new ArgumentException("请先绘制模板区域。");
        var bounds = region.Bounds;
        int left = Math.Max(0, (int)Math.Floor(bounds.Left)), top = Math.Max(0, (int)Math.Floor(bounds.Top));
        int w = Math.Min(width, (int)Math.Ceiling(bounds.Right)) - left, h = Math.Min(height, (int)Math.Ceiling(bounds.Bottom)) - top;
        if (w < 4 || h < 4 || w > 512 || h > 512)
            throw new ArgumentException("模板宽高须为 4～512 像素，请缩小绘制区域。");
        byte[] gray = new byte[w * h], mask = new byte[w * h];
        int count = 0;
        double sum = 0, squares = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int src = (top + y) * stride + (left + x) * channels, dst = y * w + x;
            byte value = channels == 1 ? pixels[src] : (byte)((1868 * pixels[src] + 9617 * pixels[src + 1] + 4899 * pixels[src + 2] + 8192) >> 14);
            gray[dst] = value;
            bool valid = region.Contains(left + x + .5, top + y + .5);
            mask[dst] = valid ? (byte)255 : (byte)0;
            if (valid)
            {
                count++;
                sum += value;
                squares += value * value;
            }
        }
        if (count < 16 || squares / count - Math.Pow(sum / count, 2) < 1)
            throw new ArgumentException("模板至少需要 16 个有效像素和灰度对比；请包含物品边缘及少量背景。");
        var model = new JsonObject { ["width"] = w, ["height"] = h, ["grayBase64"] = Convert.ToBase64String(gray) };
        if (mask.Any(p => p == 0))
            model["maskBase64"] = Convert.ToBase64String(mask);
        return model;
    }
}

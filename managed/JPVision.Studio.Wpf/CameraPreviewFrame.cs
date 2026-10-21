using JPVision.Acquisition;
using JPVision.Sdk;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JPVision.Studio;

/// <summary>One prepared bitmap shared by preview views; camera pixels remain immutable and at acquisition resolution.</summary>
internal sealed class CameraPreviewFrame
{
    internal CameraFrame Frame { get; }
    internal BitmapSource Bitmap { get; }
    internal FrameInfo Info { get; }
    private CameraPreviewFrame(CameraFrame frame, BitmapSource bitmap)
    {
        Frame = frame; Bitmap = bitmap;
        Info = new(frame.Width, frame.Height, frame.Stride, frame.PixelFormat, 0, 0);
    }
    internal static CameraPreviewFrame Create(CameraFrame frame)
    {
        // CameraFrame owns an exact array. Passing it to WPF avoids intermediate managed/native frame copies.
        if (!MemoryMarshal.TryGetArray(frame.Pixels, out ArraySegment<byte> pixels) || pixels.Array is null || pixels.Offset != 0)
            throw new InvalidOperationException("相机预览需要连续的自有像素缓冲区。");
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            frame.PixelFormat == VisionPixelFormat.Gray8 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, pixels.Array, frame.Stride);
        bitmap.Freeze();
        return new(frame, bitmap);
    }
}

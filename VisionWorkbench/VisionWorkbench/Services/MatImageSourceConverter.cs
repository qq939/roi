using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace VisionWorkbench.Services;

public static class MatImageSourceConverter
{
    public static Mat CreateMat(ImageSource imageSource)
    {
        if (imageSource is not BitmapSource bitmapSource)
        {
            throw new InvalidOperationException("Only bitmap image sources can be converted to Mat.");
        }

        BitmapSource source = bitmapSource;
        if (source.Format != PixelFormats.Bgr24)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        return BitmapSourceConverter.ToMat(source);
    }

    public static ImageSource CreateImageSource(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
        {
            throw new ArgumentException("Camera frame is empty.", nameof(source));
        }

        var bitmap = BitmapSourceConverter.ToBitmapSource(source);
        bitmap.Freeze();
        return bitmap;
    }
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace VisionWorkbench.Services;

public static class ImageSourceFileStorage
{
    public static void SavePng(ImageSource imageSource, string filePath)
    {
        if (imageSource is not BitmapSource bitmapSource)
        {
            throw new InvalidOperationException("Only bitmap image sources can be saved.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    public static ImageSource LoadImage(string filePath)
    {
        using var image = Cv2.ImRead(Path.GetFullPath(filePath), ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidOperationException($"图片读取失败：{filePath}");
        }

        return MatImageSourceConverter.CreateImageSource(image);
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using OpenCvSharp;
using VideoInferenceDemo;
using Drawing = System.Drawing;

namespace VisionWorkbench.Services;

internal static class FileNameWatermarkRenderer
{
    private const int Margin = 16;

    public static Mat Render(Mat source, string text, CameraImageWatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(options);

        if (source.Empty())
        {
            throw new ArgumentException("Watermark source image is empty.", nameof(source));
        }

        var normalized = options.Normalize();
        Cv2.ImEncode(".bmp", source, out var encodedSource);
        using var sourceStream = new MemoryStream(encodedSource, writable: false);
        using var decoded = new Drawing.Bitmap(sourceStream);
        using var bitmap = new Drawing.Bitmap(decoded.Width, decoded.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(decoded, 0, 0);
            graphics.PageUnit = Drawing.GraphicsUnit.Pixel;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var format = new Drawing.StringFormat(Drawing.StringFormat.GenericTypographic)
            {
                FormatFlags = Drawing.StringFormatFlags.NoWrap | Drawing.StringFormatFlags.MeasureTrailingSpaces
            };
            var requireChinese = ContainsChineseCharacters(text);
            var fontSize = ResolveFontSize(graphics, text, normalized.FontSize, bitmap.Size, format, requireChinese);
            using var font = CreateFont(fontSize, requireChinese);
            var textSize = graphics.MeasureString(text, font, int.MaxValue, format);
            var location = ResolveLocation(bitmap.Size, textSize, normalized.Position);
            var outlineOffset = Math.Max(1, (int)Math.Round(fontSize / 18.0));
            using var outlineBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(220, 0, 0, 0));
            using var textBrush = new Drawing.SolidBrush(ParseColor(normalized.Color));

            foreach (var (offsetX, offsetY) in OutlineOffsets)
            {
                graphics.DrawString(
                    text,
                    font,
                    outlineBrush,
                    location.X + offsetX * outlineOffset,
                    location.Y + offsetY * outlineOffset,
                    format);
            }

            graphics.DrawString(text, font, textBrush, location.X, location.Y, format);
        }

        using var renderedStream = new MemoryStream();
        bitmap.Save(renderedStream, ImageFormat.Bmp);
        return Cv2.ImDecode(renderedStream.ToArray(), ImreadModes.Color);
    }

    private static readonly (int X, int Y)[] OutlineOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),            (1, 0),
        (-1, 1),  (0, 1),  (1, 1)
    ];

    private static Drawing.Font CreateFont(float fontSize, bool requireChinese = false)
    {
        var fallbackFamilies = requireChinese
            ? new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial Unicode MS", "Segoe UI" }
            : new[] { "Microsoft YaHei UI", "Arial", "Segoe UI" };

        foreach (var family in fallbackFamilies)
        {
            try
            {
                var font = new Drawing.Font(family, fontSize, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel);
                return font;
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        return new Drawing.Font(Drawing.FontFamily.GenericSansSerif, fontSize, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel);
    }

    private static bool ContainsChineseCharacters(string text)
    {
        return text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff');
    }

    private static float ResolveFontSize(
        Drawing.Graphics graphics,
        string text,
        int configuredFontSize,
        Drawing.Size imageSize,
        Drawing.StringFormat format,
        bool requireChinese = false)
    {
        var fontSize = (float)configuredFontSize;
        using var configuredFont = CreateFont(fontSize, requireChinese);
        var measuredSize = graphics.MeasureString(text, configuredFont, int.MaxValue, format);

        var availableWidth = Math.Max(1, imageSize.Width - Margin * 2);
        var availableHeight = Math.Max(1, imageSize.Height - Margin * 2);

        if (measuredSize.Width > availableWidth || measuredSize.Height > availableHeight)
        {
            var widthScale = availableWidth / measuredSize.Width;
            var heightScale = availableHeight / measuredSize.Height;
            fontSize = Math.Max(1, fontSize * Math.Min(widthScale, heightScale));
        }

        return fontSize;
    }

    private static Drawing.PointF ResolveLocation(
        Drawing.Size imageSize,
        Drawing.SizeF textSize,
        CameraImageWatermarkPosition position)
    {
        var left = Margin;
        var right = imageSize.Width - Margin - textSize.Width;
        var top = Margin;
        var bottom = imageSize.Height - Margin - textSize.Height;
        var x = position is CameraImageWatermarkPosition.TopRight or CameraImageWatermarkPosition.BottomRight
            ? right
            : left;
        var y = position is CameraImageWatermarkPosition.BottomLeft or CameraImageWatermarkPosition.BottomRight
            ? bottom
            : top;

        return new Drawing.PointF(
            Math.Clamp(x, 0, Math.Max(0, imageSize.Width - textSize.Width)),
            Math.Clamp(y, 0, Math.Max(0, imageSize.Height - textSize.Height)));
    }

    private static Drawing.Color ParseColor(string value)
    {
        var normalized = CameraImageWatermarkOptions.TryNormalizeColor(value, out var color)
            ? color
            : CameraImageWatermarkOptions.DefaultColor;
        return Drawing.Color.FromArgb(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }
}

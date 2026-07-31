using System;

namespace VideoInferenceDemo;

public enum CameraImageWatermarkPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public sealed class CameraImageWatermarkOptions
{
    public const int DefaultFontSize = 28;
    public const string DefaultColor = "#FFFFFF";

    public bool Enabled { get; set; } = true;
    public int FontSize { get; set; } = DefaultFontSize;
    public string Color { get; set; } = DefaultColor;
    public CameraImageWatermarkPosition Position { get; set; } = CameraImageWatermarkPosition.BottomRight;

    public CameraImageWatermarkOptions Normalize()
    {
        return new CameraImageWatermarkOptions
        {
            Enabled = Enabled,
            FontSize = Math.Clamp(FontSize, 12, 128),
            Color = TryNormalizeColor(Color, out var normalizedColor) ? normalizedColor : DefaultColor,
            Position = Enum.IsDefined(Position) ? Position : CameraImageWatermarkPosition.BottomRight
        };
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = DefaultColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (!color.StartsWith('#'))
        {
            color = $"#{color}";
        }

        if (color.Length != 7)
        {
            return false;
        }

        for (var index = 1; index < color.Length; index++)
        {
            if (!Uri.IsHexDigit(color[index]))
            {
                return false;
            }
        }

        normalized = color.ToUpperInvariant();
        return true;
    }
}

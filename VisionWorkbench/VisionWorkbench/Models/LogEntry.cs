using System.Windows.Media;

namespace VisionWorkbench.Models;

public sealed class LogEntry
{
    public LogEntry(string level, string message)
    {
        Time = DateTime.Now;
        Level = level;
        Message = message;
    }

    public DateTime Time { get; }

    public string Level { get; }

    public string Message { get; }

    public string Text => $"{Time:HH:mm:ss} [{Level}] {Message}";

    public Brush LevelBrush => Level switch
    {
        "NG" => UiBrushes.Danger,
        "WARN" => UiBrushes.Warning,
        _ => UiBrushes.TextMuted
    };
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoInferenceDemo;

public sealed record HikLineModeProbe(string LineName, string? LineMode);

public static class HikHardwareTriggerSourceResolver
{
    public static IReadOnlyList<string> DefaultLineCandidates { get; } =
    [
        "Line0",
        "Line1",
        "Line2",
        "Line3",
        "Line4",
        "Line5",
        "Line6",
        "Line7"
    ];

    public static IReadOnlyList<string> BuildCandidateOrder(
        IEnumerable<HikLineModeProbe> lineModes,
        IEnumerable<string>? triggerSourceEntries = null)
    {
        var probes = lineModes
            .Where(item => !string.IsNullOrWhiteSpace(item.LineName))
            .GroupBy(item => item.LineName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var supportedTriggerSources = NormalizeSymbols(triggerSourceEntries)
            .Where(IsLineName)
            .ToList();
        var supportedSet = supportedTriggerSources.Count > 0
            ? new HashSet<string>(supportedTriggerSources, StringComparer.OrdinalIgnoreCase)
            : null;

        if (probes.Any(item => !string.IsNullOrWhiteSpace(item.LineMode)))
        {
            return probes
                .Where(item => IsInputLineMode(item.LineMode))
                .Select(item => item.LineName.Trim())
                .Where(item => supportedSet == null || supportedSet.Contains(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (supportedTriggerSources.Count > 0)
        {
            return DefaultLineCandidates
                .Where(item => supportedSet!.Contains(item))
                .Concat(supportedTriggerSources.Where(item => !DefaultLineCandidates.Contains(item, StringComparer.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return DefaultLineCandidates;
    }

    public static bool IsInputLineMode(string? lineMode)
    {
        return string.Equals(lineMode?.Trim(), "Input", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatLineModes(IEnumerable<HikLineModeProbe> lineModes)
    {
        var items = lineModes
            .Where(item => !string.IsNullOrWhiteSpace(item.LineName))
            .Select(item => $"{item.LineName.Trim()}={FormatValue(item.LineMode)}")
            .ToList();
        return items.Count > 0 ? string.Join(", ", items) : "n/a";
    }

    public static string FormatSymbols(IEnumerable<string>? symbols)
    {
        var items = NormalizeSymbols(symbols).ToList();
        return items.Count > 0 ? string.Join("|", items) : "n/a";
    }

    private static IEnumerable<string> NormalizeSymbols(IEnumerable<string>? symbols)
    {
        return symbols?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private static bool IsLineName(string value)
    {
        return value.StartsWith("Line", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }
}

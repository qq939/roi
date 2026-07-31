using System.IO;
using System.Runtime.InteropServices;

namespace VisionWorkbench.Services;

public static class NativeCameraRuntimeInitializer
{
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            AppDiagnostics.Info("native-runtime", "Skip native camera runtime initialization on non-Windows OS.");
            return;
        }

        var paths = ResolveCandidatePaths()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AppDiagnostics.Info("native-runtime", $"Initialize native camera runtime paths. Count={paths.Length}, Paths={string.Join("|", paths)}");
        foreach (var path in paths)
        {
            _ = AddDllDirectory(path);
        }

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var existingParts = existingPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var merged = paths
            .Concat(existingParts)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, merged));
    }

    private static IEnumerable<string> ResolveCandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "ThirdParty", "CameraHIK");
        yield return ResolveMvsRuntimeX64();
    }

    private static string ResolveMvsRuntimeX64()
    {
        try
        {
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return string.IsNullOrWhiteSpace(programFilesX86)
                ? string.Empty
                : Path.Combine(programFilesX86, "Common Files", "MVS", "Runtime", "Win64_x64");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("native-runtime", "Failed to resolve MVS runtime path.", ex);
            return string.Empty;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}

using System.IO;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.Services;

public sealed class VisionRuntimePaths
{
    public const string DefaultRootDirectory = "RuntimeData";

    public VisionRuntimePaths(string? rootDirectory = null, string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var root = string.IsNullOrWhiteSpace(rootDirectory) ? DefaultRootDirectory : rootDirectory.Trim();
        RootDirectory = Path.GetFullPath(Path.IsPathRooted(root)
            ? root
            : Path.Combine(BaseDirectory, root));
    }

    public string BaseDirectory { get; }

    public string RootDirectory { get; }

    public string ConfigDirectory => Path.Combine(RootDirectory, "Config");

    public string ModelsDirectory => Path.Combine(RootDirectory, "Models");

    public string CacheDirectory => Path.Combine(RootDirectory, "Cache");

    public string DatabaseDirectory => Path.Combine(RootDirectory, "Database");

    public string ProductsDirectory => Path.Combine(RootDirectory, "Products");

    public string InspectionImagesDirectory => Path.Combine(RootDirectory, "InspectionImages");

    public string InspectionConfigurationPath => Path.Combine(ConfigDirectory, "inspection_config.json");

    public string CameraConfigurationPath => Path.Combine(ConfigDirectory, "camera_config.json");

    public string IoModuleConfigurationPath => Path.Combine(ConfigDirectory, "io_module_config.json");

    public string ClipModelPath => Path.Combine(ModelsDirectory, ClipRuntimeOptions.ModelFileName);

    public string ClipVectorDatabasePath => Path.Combine(CacheDirectory, "clip_vectors.db");

    public string AlignmentTemplateDatabasePath => Path.Combine(DatabaseDirectory, "alignment_templates.db");

    public string InspectionResultDatabasePath => Path.Combine(DatabaseDirectory, "inspection_results.db");

    public string ClipTrainingQueriesDirectory => Path.Combine(CacheDirectory, "ClipTrainingQueries");

    public string ClipQueriesDirectory => Path.Combine(CacheDirectory, "ClipQueries");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(ProductsDirectory);
        Directory.CreateDirectory(InspectionImagesDirectory);
        Directory.CreateDirectory(ClipTrainingQueriesDirectory);
        Directory.CreateDirectory(ClipQueriesDirectory);
    }

    public void MigrateLegacyFiles()
    {
        EnsureDirectories();
        MoveLegacyFile(Path.Combine(BaseDirectory, "inspection_config.json"), InspectionConfigurationPath);
        MoveLegacyFile(Path.Combine(BaseDirectory, "camera_config.json"), CameraConfigurationPath);
        MoveLegacyFile(Path.Combine(BaseDirectory, "Cache", "clip_vectors.db"), ClipVectorDatabasePath);
        MoveLegacyFile(Path.Combine(RootDirectory, "alignment_templates.db"), AlignmentTemplateDatabasePath);
        MoveLegacyFile(Path.Combine(RootDirectory, "inspection_results.db"), InspectionResultDatabasePath);
        MoveLegacyClipModel();
        MoveLegacyDirectory(Path.Combine(BaseDirectory, "Runtime", "ClipTrainingQueries"), ClipTrainingQueriesDirectory);
        MoveLegacyDirectory(Path.Combine(BaseDirectory, "Runtime", "ClipQueries"), ClipQueriesDirectory);
    }

    public IReadOnlyList<string> GetClipModelPathCandidates()
    {
        return [ClipModelPath];
    }

    private void MoveLegacyClipModel()
    {
        if (File.Exists(ClipModelPath))
        {
            return;
        }

        foreach (var candidate in GetLegacyModelPathCandidates())
        {
            if (File.Exists(candidate))
            {
                MoveLegacyFile(candidate, ClipModelPath);
                return;
            }
        }
    }

    private IReadOnlyList<string> GetLegacyModelPathCandidates()
    {
        var candidates = new List<string>();
        var current = BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            candidates.Add(Path.Combine(current, "Models", ClipRuntimeOptions.ModelFileName));
            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return candidates
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(ClipModelPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void MoveLegacyFile(string sourcePath, string targetPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
        {
            return;
        }

        if (File.Exists(targetPath))
        {
            AppDiagnostics.Info("paths", $"Legacy file migration skipped because target exists. Source={sourcePath}, Target={targetPath}");
            return;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(sourcePath, targetPath);
        AppDiagnostics.Info("paths", $"Legacy file migrated. Source={sourcePath}, Target={targetPath}");
    }

    private void MoveLegacyDirectory(string sourcePath, string targetPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sourcePath))
        {
            return;
        }

        if (Directory.Exists(targetPath))
        {
            AppDiagnostics.Info("paths", $"Legacy directory migration skipped because target exists. Source={sourcePath}, Target={targetPath}");
            return;
        }

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(sourcePath, targetPath);
        AppDiagnostics.Info("paths", $"Legacy directory migrated. Source={sourcePath}, Target={targetPath}");
    }
}

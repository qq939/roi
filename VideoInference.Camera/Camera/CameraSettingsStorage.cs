using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoInferenceDemo;

public static class CameraSettingsStorage
{
    private const string SopRulesStrategy = "sop_rules";
    private const string Sop1Strategy = "sop1";
    private const string Sop2Strategy = "sop2";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static CameraSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return CameraSettings.CreateDefault();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<CameraSettings>(json, Options);
            return Normalize(settings);
        }
        catch (Exception ex)
        {
            CameraDiagnostics.Error("camera-storage", $"Failed to load camera config '{path}'.", ex);
            throw new InvalidOperationException(
                $"Failed to load camera config '{path}'. Fix the JSON file before saving camera settings.",
                ex);
        }
    }

    public static void Save(string path, CameraSettings settings)
    {
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, Options);
        WriteAtomic(path, json);
    }

    private static CameraSettings Normalize(CameraSettings? settings)
    {
        settings ??= new CameraSettings();
        settings.Cameras ??= new System.Collections.Generic.List<CameraProfile>();
        settings.SopProfiles ??= new System.Collections.Generic.List<CameraSopProfile>();

        var normalized = settings.Cameras
            .Select((camera, index) => (camera ?? CameraProfile.CreateDefault(index + 1)).Normalize(index + 1))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(CameraProfile.CreateDefault(1));
        }

        var selectedId = settings.SelectedCameraId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedId) ||
            normalized.All(camera => !string.Equals(camera.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            selectedId = normalized[0].Id;
        }

        return new CameraSettings
        {
            Cameras = normalized,
            SopProfiles = settings.SopProfiles
                .Where(profile => profile != null)
                .Select(NormalizeSopProfile)
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && profile.Steps.Count > 0)
                .ToList(),
            SelectedCameraId = selectedId
        };
    }

    public static void SaveSopProfiles(string path, IReadOnlyList<CameraSopProfile> profiles)
    {
        var settings = Load(path);
        settings.SopProfiles = (profiles ?? Array.Empty<CameraSopProfile>())
            .Where(profile => profile != null)
            .Select(NormalizeSopProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && profile.Steps.Count > 0)
            .ToList();
        Save(path, settings);
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            var backupPath = $"{path}.bak";
            File.Copy(path, backupPath, overwrite: true);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private static CameraSopProfile NormalizeSopProfile(CameraSopProfile profile)
    {
        var id = string.IsNullOrWhiteSpace(profile.Id)
            ? Guid.NewGuid().ToString("N")
            : profile.Id.Trim();
        var name = string.IsNullOrWhiteSpace(profile.Name) ? id : profile.Name.Trim();
        var isSop1 = IsSop1Profile(name);
        var isSop2 = IsSop2Profile(name);
        var stepLimit = isSop1
            ? 5
            : isSop2
                ? 6
                : int.MaxValue;
        var steps = (profile.Steps ?? new System.Collections.Generic.List<CameraSopStep>())
            .Where(step => step != null)
            .OrderBy(step => step.Step)
            .Take(stepLimit)
            .Select((step, index) => new CameraSopStep
            {
                Step = index + 1,
                Name = step.Name?.Trim() ?? string.Empty,
                ActionCode = string.IsNullOrWhiteSpace(step.ActionCode) ? null : step.ActionCode.Trim(),
                TcnLabel = string.IsNullOrWhiteSpace(step.TcnLabel) ? null : step.TcnLabel.Trim(),
                ExpectedStateCode = string.IsNullOrWhiteSpace(step.ExpectedStateCode) ? null : step.ExpectedStateCode.Trim()
            })
            .Where(step => !string.IsNullOrWhiteSpace(step.Name))
            .ToList();

        return new CameraSopProfile
        {
            Id = id,
            Name = name,
            Strategy = ResolveSopStrategy(name, profile.Strategy),
            FingerprintModuleId = string.IsNullOrWhiteSpace(profile.FingerprintModuleId) ? string.Empty : profile.FingerprintModuleId.Trim(),
            Steps = steps
        };
    }

    private static bool IsSop1Profile(string name)
    {
        return string.Equals(name?.Trim(), "sop1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSop2Profile(string name)
    {
        return string.Equals(name?.Trim(), "sop2", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSopStrategy(string name, string? configuredStrategy)
    {
        if (IsSop1Profile(name))
        {
            return Sop1Strategy;
        }

        if (IsSop2Profile(name))
        {
            return Sop2Strategy;
        }

        return string.IsNullOrWhiteSpace(configuredStrategy)
            ? SopRulesStrategy
            : configuredStrategy.Trim();
    }
}

namespace VisionWorkbench.Services;

public enum ConfigurationCommitReason
{
    Manual,
    Navigation,
    ApplicationClosing
}

public sealed record ConfigurationCommitResult(bool Success, string Message)
{
    public static ConfigurationCommitResult Ok(string message) => new(true, message);

    public static ConfigurationCommitResult Fail(string message) => new(false, message);
}

public interface IConfigurationWorkspace
{
    string ConfigurationWorkspaceName { get; }

    bool IsDirty { get; }

    Task<ConfigurationCommitResult> TryCommitAsync(
        ConfigurationCommitReason reason,
        CancellationToken cancellationToken = default);
}

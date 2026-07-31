using System.IO;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public sealed class S3UploadConfiguration
{
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = "cn-north-1";
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string DefaultPrefix { get; set; } = "AMSPhotos/";
    public bool Enabled { get; set; } = true;

    public static S3UploadConfiguration LoadFromEnvFile(string envFilePath)
    {
        var config = new S3UploadConfiguration();
        if (!File.Exists(envFilePath))
        {
            config.Enabled = false;
            AppDiagnostics.Warn("s3", $"S3 config file not found: {envFilePath}. S3 upload disabled.");
            return config;
        }

        foreach (var line in File.ReadAllLines(envFilePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "BUCKET_NAME":
                    config.BucketName = value;
                    break;
                case "REGION":
                    config.Region = value;
                    break;
                case "AWS_ACCESS_KEY_ID":
                    config.AccessKeyId = value;
                    break;
                case "AWS_SECRET_ACCESS_KEY":
                    config.SecretAccessKey = value;
                    break;
                case "DEFAULT_PREFIX":
                    config.DefaultPrefix = value;
                    break;
            }
        }

        config.Enabled = !string.IsNullOrEmpty(config.BucketName)
                        && !string.IsNullOrEmpty(config.AccessKeyId)
                        && !string.IsNullOrEmpty(config.SecretAccessKey);

        if (!config.Enabled)
        {
            AppDiagnostics.Warn("s3", "S3 configuration incomplete. S3 upload disabled.");
        }

        return config;
    }
}

public sealed class S3UploadService : IDisposable
{
    private readonly S3UploadConfiguration _configuration;
    private readonly IAmazonS3 _s3Client;
    private readonly SemaphoreSlim _uploadSemaphore;
    private bool _disposed;

    public S3UploadService(S3UploadConfiguration configuration)
    {
        _configuration = configuration;
        _uploadSemaphore = new SemaphoreSlim(2, 2);

        if (!configuration.Enabled)
        {
            _s3Client = null!;
            return;
        }

        try
        {
            var credentials = new BasicAWSCredentials(configuration.AccessKeyId, configuration.SecretAccessKey);
            _s3Client = new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(configuration.Region));
            AppDiagnostics.Info("s3", $"S3UploadService initialized. Bucket={configuration.BucketName}, Region={configuration.Region}");
        }
        catch (Exception ex)
        {
            _s3Client = null!;
            _configuration.Enabled = false;
            AppDiagnostics.Error("s3", "Failed to initialize S3 client. S3 upload disabled.", ex);
        }
    }

    public bool IsEnabled => _configuration.Enabled && _s3Client != null;

    public async Task UploadImagePairAsync(
        string? rawImagePath,
        string? renderedImagePath,
        string serialNumber,
        string cameraName,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            AppDiagnostics.Info("s3", "S3 upload skipped: service disabled.");
            return;
        }

        var uploadTasks = new List<Task>();

        if (!string.IsNullOrWhiteSpace(rawImagePath) && File.Exists(rawImagePath))
        {
            var fileName = Path.GetFileName(rawImagePath);
            var s3Key = $"{_configuration.DefaultPrefix.TrimEnd('/')}/{SanitizePathSegment(serialNumber)}/{SanitizePathSegment(cameraName)}/raw/{fileName}";
            uploadTasks.Add(UploadFileAsync(rawImagePath, s3Key, cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(renderedImagePath) && File.Exists(renderedImagePath))
        {
            var fileName = Path.GetFileName(renderedImagePath);
            var s3Key = $"{_configuration.DefaultPrefix.TrimEnd('/')}/{SanitizePathSegment(serialNumber)}/{SanitizePathSegment(cameraName)}/result/{fileName}";
            uploadTasks.Add(UploadFileAsync(renderedImagePath, s3Key, cancellationToken));
        }

        if (uploadTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(uploadTasks);
                AppDiagnostics.Info("s3", $"Image pair uploaded to S3. Serial={serialNumber}, Camera={cameraName}, Raw={rawImagePath != null}, Rendered={renderedImagePath != null}");
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error("s3", $"S3 image pair upload failed. Serial={serialNumber}, Camera={cameraName}, Error={ex.Message}", ex);
            }
        }
        else
        {
            AppDiagnostics.Warn("s3", $"S3 upload skipped: no valid images. Raw={rawImagePath}, Rendered={renderedImagePath}");
        }
    }

    public async Task UploadRawImageFolderAsync(
        string localImagePath,
        string serialNumber,
        string cameraName,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            AppDiagnostics.Info("s3", "S3 upload skipped: service disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(localImagePath) || !File.Exists(localImagePath))
        {
            AppDiagnostics.Warn("s3", $"S3 upload skipped: local file not found. Path={localImagePath}");
            return;
        }

        try
        {
            await _uploadSemaphore.WaitAsync(cancellationToken);

            var fileName = Path.GetFileName(localImagePath);
            var s3Key = BuildS3Key(serialNumber, cameraName, fileName);

            using var fileStream = File.OpenRead(localImagePath);
            var fileInfo = new FileInfo(localImagePath);

            var putRequest = new PutObjectRequest
            {
                BucketName = _configuration.BucketName,
                Key = s3Key,
                InputStream = fileStream,
                ContentType = GetContentType(fileName)
            };

            await _s3Client.PutObjectAsync(putRequest, cancellationToken);

            AppDiagnostics.Info("s3", $"Raw image uploaded to S3. Key=s3://{_configuration.BucketName}/{s3Key}, Size={fileInfo.Length}B");
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Warn("s3", "S3 upload canceled.");
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("s3", $"S3 upload failed. LocalPath={localImagePath}, Serial={serialNumber}, Error={ex.Message}", ex);
        }
        finally
        {
            _uploadSemaphore.Release();
        }
    }

    public async Task UploadDirectoryAsync(
        string localDirectory,
        string serialNumber,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            AppDiagnostics.Info("s3", "S3 upload skipped: service disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(localDirectory) || !Directory.Exists(localDirectory))
        {
            AppDiagnostics.Warn("s3", $"S3 upload skipped: directory not found. Path={localDirectory}");
            return;
        }

        try
        {
            var files = Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories);
            var uploadTasks = new List<Task>();

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(localDirectory, file);
                var s3Key = BuildDirectoryS3Key(serialNumber, relativePath);

                uploadTasks.Add(UploadFileAsync(file, s3Key, cancellationToken));
            }

            await Task.WhenAll(uploadTasks);
            AppDiagnostics.Info("s3", $"Directory uploaded to S3. Serial={serialNumber}, Files={files.Length}");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("s3", $"S3 directory upload failed. Dir={localDirectory}, Serial={serialNumber}, Error={ex.Message}", ex);
        }
    }

    private async Task UploadFileAsync(
        string localPath,
        string s3Key,
        CancellationToken cancellationToken)
    {
        try
        {
            await _uploadSemaphore.WaitAsync(cancellationToken);

            using var fileStream = File.OpenRead(localPath);
            var putRequest = new PutObjectRequest
            {
                BucketName = _configuration.BucketName,
                Key = s3Key,
                InputStream = fileStream,
                ContentType = GetContentType(s3Key)
            };

            await _s3Client.PutObjectAsync(putRequest, cancellationToken);

            AppDiagnostics.Info("s3", $"File uploaded: s3://{_configuration.BucketName}/{s3Key}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error("s3", $"File upload failed. Key={s3Key}, Error={ex.Message}", ex);
        }
        finally
        {
            _uploadSemaphore.Release();
        }
    }

    private string BuildS3Key(string serialNumber, string cameraName, string fileName)
    {
        var prefix = _configuration.DefaultPrefix.TrimEnd('/');
        var sanitizedSerial = SanitizePathSegment(serialNumber);
        var sanitizedCamera = SanitizePathSegment(cameraName);
        return $"{prefix}/{sanitizedSerial}/{sanitizedCamera}/{fileName}";
    }

    private string BuildDirectoryS3Key(string serialNumber, string relativePath)
    {
        var prefix = _configuration.DefaultPrefix.TrimEnd('/');
        var sanitizedSerial = SanitizePathSegment(serialNumber);
        var normalizedPath = relativePath.Replace('\\', '/');
        return $"{prefix}/{sanitizedSerial}/{normalizedPath}";
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".tiff" or ".tif" => "image/tiff",
            ".webp" => "image/webp",
            ".json" => "application/json",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _uploadSemaphore.Dispose();
            if (_s3Client != null)
            {
                _s3Client.Dispose();
            }
        }
        catch { }
    }
}

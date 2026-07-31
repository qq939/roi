using System.Globalization;
using System.IO;
using System.Text;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public enum ExportFormat
{
    Html,
    Pdf
}

public sealed class InspectionResultExportService
{
    public async Task ExportAsync(
        string serialNumber,
        IEnumerable<InspectionResultRecord> records,
        string outputPath,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var recordList = records.ToList();
        if (recordList.Count == 0)
        {
            throw new ArgumentException("No records to export.", nameof(records));
        }

        var grouped = recordList.GroupBy(r => r.CameraName).ToList();

        switch (format)
        {
            case ExportFormat.Html:
                await ExportToHtmlAsync(serialNumber, grouped, outputPath, cancellationToken);
                break;
            case ExportFormat.Pdf:
                await ExportToPdfAsync(serialNumber, grouped, outputPath, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static async Task ExportToHtmlAsync(
        string serialNumber,
        List<IGrouping<string, InspectionResultRecord>> groupedRecords,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var html = GenerateHtmlReport(serialNumber, groupedRecords);
        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8, cancellationToken);
    }

    private static async Task ExportToPdfAsync(
        string serialNumber,
        List<IGrouping<string, InspectionResultRecord>> groupedRecords,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var html = GenerateHtmlReport(serialNumber, groupedRecords);
        await PdfSharpHtmlConverter.ConvertAsync(html, outputPath, cancellationToken);
    }

    private static string GenerateHtmlReport(
        string serialNumber,
        List<IGrouping<string, InspectionResultRecord>> groupedRecords)
    {
        var sb = new StringBuilder();
        var exportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var firstRecord = groupedRecords.First().First();
        var productCode = firstRecord.ProductCode;

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>检测报告 - {EscapeHtml(serialNumber)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { font-family: 'Microsoft YaHei', 'Segoe UI', Arial, sans-serif; font-size: 14px; color: #333; background: #f5f5f5; padding: 20px; }
            .container { max-width: 1200px; margin: 0 auto; background: #fff; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
            .header { background: linear-gradient(135deg, #2196F3, #1976D2); color: white; padding: 30px; border-radius: 8px 8px 0 0; }
            .header h1 { font-size: 24px; margin-bottom: 10px; }
            .header .info { display: flex; flex-wrap: wrap; gap: 20px; font-size: 13px; opacity: 0.9; }
            .header .info span { display: flex; align-items: center; gap: 6px; }
            .summary { display: flex; gap: 15px; padding: 20px 30px; background: #f8f9fa; border-bottom: 1px solid #e0e0e0; }
            .summary-item { flex: 1; text-align: center; padding: 12px; background: white; border-radius: 6px; }
            .summary-item .value { font-size: 24px; font-weight: bold; color: #2196F3; }
            .summary-item .label { font-size: 12px; color: #666; margin-top: 4px; }
            .summary-item.ok .value { color: #4CAF50; }
            .summary-item.ng .value { color: #f44336; }
            .camera-section { padding: 25px 30px; border-bottom: 1px solid #e0e0e0; page-break-inside: avoid; }
            .camera-section:last-child { border-bottom: none; }
            .camera-title { font-size: 18px; font-weight: 600; color: #1976D2; margin-bottom: 15px; display: flex; align-items: center; gap: 8px; }
            .camera-title::before { content: ''; width: 4px; height: 20px; background: #2196F3; border-radius: 2px; }
            .image-gallery { display: flex; flex-wrap: wrap; gap: 15px; margin-bottom: 20px; }
            .image-item { flex: 1; min-width: 280px; max-width: 350px; background: #f8f9fa; border-radius: 6px; overflow: hidden; }
            .image-item img { width: 100%; height: auto; display: block; }
            .image-item .caption { padding: 8px 12px; font-size: 12px; color: #666; text-align: center; background: #f0f0f0; }
            .data-table { width: 100%; border-collapse: collapse; font-size: 13px; }
            .data-table th { background: #e3f2fd; color: #1565C0; text-align: left; padding: 10px 12px; font-weight: 600; border-bottom: 2px solid #bbdefb; }
            .data-table td { padding: 8px 12px; border-bottom: 1px solid #eee; }
            .data-table tr:hover { background: #f5f5f5; }
            .result-ok { color: #4CAF50; font-weight: 600; }
            .result-ng { color: #f44336; font-weight: 600; }
            .footer { padding: 15px 30px; background: #f8f9fa; border-radius: 0 0 8px 8px; font-size: 12px; color: #888; text-align: center; }
            @media print { body { background: white; padding: 0; } .container { box-shadow: none; } .camera-section { page-break-inside: avoid; } }
        ");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("<div class=\"container\">");
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<h1>检测报告 - 序列号：{EscapeHtml(serialNumber)}</h1>");
        sb.AppendLine("<div class=\"info\">");
        sb.AppendLine($"<span><strong>成品号：</strong>{EscapeHtml(productCode)}</span>");
        sb.AppendLine($"<span><strong>导出时间：</strong>{exportTime}</span>");
        sb.AppendLine($"<span><strong>相机数量：</strong>{groupedRecords.Count}</span>");
        sb.AppendLine($"<span><strong>记录数量：</strong>{groupedRecords.Sum(g => g.Count())}</span>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Summary
        var totalCount = groupedRecords.Sum(g => g.Count());
        var okCount = groupedRecords.Sum(g => g.Count(r => r.Result == "OK"));
        var ngCount = totalCount - okCount;
        sb.AppendLine("<div class=\"summary\">");
        sb.AppendLine("<div class=\"summary-item\">");
        sb.AppendLine($"<div class=\"value\">{totalCount}</div>");
        sb.AppendLine("<div class=\"label\">总记录数</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"summary-item ok\">");
        sb.AppendLine($"<div class=\"value\">{okCount}</div>");
        sb.AppendLine("<div class=\"label\">合格 (OK)</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class=\"summary-item ng\">");
        sb.AppendLine($"<div class=\"value\">{ngCount}</div>");
        sb.AppendLine("<div class=\"label\">不合格 (NG)</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Camera sections
        foreach (var cameraGroup in groupedRecords)
        {
            var cameraName = cameraGroup.Key;
            var cameraRecords = cameraGroup.OrderBy(r => r.OccurredAt).ToList();
            var firstCameraRecord = cameraGroup.First();

            sb.AppendLine("<div class=\"camera-section\">");
            sb.AppendLine($"<div class=\"camera-title\">{EscapeHtml(cameraName)}</div>");

            // Image gallery - find raw image and rendered image
            var rawImagePath = firstCameraRecord.RawImagePath;
            var cropImagePath = firstCameraRecord.CropImagePath;

            if (!string.IsNullOrWhiteSpace(rawImagePath) || !string.IsNullOrWhiteSpace(cropImagePath))
            {
                sb.AppendLine("<div class=\"image-gallery\">");

                if (!string.IsNullOrWhiteSpace(rawImagePath) && File.Exists(rawImagePath))
                {
                    var rawImageHtml = ConvertImageToBase64(rawImagePath);
                    if (!string.IsNullOrEmpty(rawImageHtml))
                    {
                        sb.AppendLine("<div class=\"image-item\">");
                        sb.AppendLine(rawImageHtml);
                        sb.AppendLine("<div class=\"caption\">原图</div>");
                        sb.AppendLine("</div>");
                    }
                }

                if (!string.IsNullOrWhiteSpace(cropImagePath) && File.Exists(cropImagePath))
                {
                    var cropImageHtml = ConvertImageToBase64(cropImagePath);
                    if (!string.IsNullOrEmpty(cropImageHtml))
                    {
                        sb.AppendLine("<div class=\"image-item\">");
                        sb.AppendLine(cropImageHtml);
                        sb.AppendLine("<div class=\"caption\">渲染图</div>");
                        sb.AppendLine("</div>");
                    }
                }

                sb.AppendLine("</div>");
            }

            // Data table
            sb.AppendLine("<table class=\"data-table\">");
            sb.AppendLine("<thead>");
            sb.AppendLine("<tr>");
            sb.AppendLine("<th>时间</th>");
            sb.AppendLine("<th>序列号</th>");
            sb.AppendLine("<th>任务</th>");
            sb.AppendLine("<th>结果</th>");
            sb.AppendLine("<th>OK分数</th>");
            sb.AppendLine("<th>NG分数</th>");
            sb.AppendLine("<th>Margin</th>");
            sb.AppendLine("<th>耗时(ms)</th>");
            sb.AppendLine("</tr>");
            sb.AppendLine("</thead>");
            sb.AppendLine("<tbody>");

            foreach (var record in cameraRecords)
            {
                var resultClass = record.Result == "OK" ? "result-ok" : "result-ng";
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{record.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}</td>");
                sb.AppendLine($"<td>{EscapeHtml(record.SerialNumber ?? "--")}</td>");
                sb.AppendLine($"<td>{EscapeHtml(record.TaskName ?? "--")}</td>");
                sb.AppendLine($"<td class=\"{resultClass}\">{record.Result}</td>");
                sb.AppendLine($"<td>{FormatScore(record.OkScore)}</td>");
                sb.AppendLine($"<td>{FormatScore(record.NgScore)}</td>");
                sb.AppendLine($"<td>{FormatScore(record.Margin)}</td>");
                sb.AppendLine($"<td>{record.ElapsedMs?.ToString("0", CultureInfo.InvariantCulture) ?? "--"}</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<div class=\"footer\">");
        sb.AppendLine($"由 VisionWorkbench 生成 | 导出时间：{exportTime}");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static string ConvertImageToBase64(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return string.Empty;
            }

            var bytes = File.ReadAllBytes(imagePath);
            var base64 = Convert.ToBase64String(bytes);
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            var mimeType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            return $"<img src=\"data:{mimeType};base64,{base64}\" alt=\"图片\" />";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static string FormatScore(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "--";
    }
}

internal static class PdfSharpHtmlConverter
{
    public static Task ConvertAsync(string html, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var tempHtml = Path.Combine(Path.GetTempPath(), $"export_{Guid.NewGuid()}.html");
            File.WriteAllText(tempHtml, html, Encoding.UTF8);

            try
            {
                // Try to use Chrome/Edge headless to convert
                var chromePath = FindChromePath();
                if (!string.IsNullOrEmpty(chromePath))
                {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = chromePath,
                        Arguments = $"--headless --disable-gpu --print-to-pdf=\"{outputPath}\" \"file:///{tempHtml.Replace("\\", "/")}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    });
                    process?.WaitForExit(30000);
                }
                else
                {
                    // Fallback: save as HTML file (user can convert manually)
                    File.Copy(tempHtml, outputPath.Replace(".pdf", ".html"), true);
                }
            }
            finally
            {
                if (File.Exists(tempHtml))
                {
                    File.Delete(tempHtml);
                }
            }
        }, cancellationToken);
    }

    private static string? FindChromePath()
    {
        var paths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Google\Chrome\Application\chrome.exe"
        };

        return paths.FirstOrDefault(File.Exists);
    }
}

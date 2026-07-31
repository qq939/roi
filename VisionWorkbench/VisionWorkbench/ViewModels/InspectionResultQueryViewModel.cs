using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionWorkbench.Models;
using VisionWorkbench.Services;

namespace VisionWorkbench.ViewModels;

public sealed partial class InspectionResultQueryViewModel : ObservableObject
{
    private const string ResultAllText = "不限";
    private readonly InspectionResultStore _resultStore;
    private readonly RuntimeInspectionContext _inspectionContext;

    public InspectionResultQueryViewModel(InspectionResultStore resultStore, RuntimeInspectionContext inspectionContext)
    {
        _resultStore = resultStore;
        _inspectionContext = inspectionContext;
        ResultOptions = [ResultAllText, "OK", "NG"];
        TaskOptions = [ResultAllText];
        Records = [];
        selectedResult = ResultAllText;
        selectedTaskFilter = ResultAllText;
        statusText = "默认查询最近100条检测记录。";
        _ = QueryAsync();

        _inspectionContext.ProductCodeChanged += OnInspectionContextProductCodeChanged;
        _inspectionContext.SerialNumberChanged += OnInspectionContextSerialNumberChanged;
        _inspectionContext.SelectedCameraChanged += OnInspectionContextSelectedCameraChanged;

        // 初始化时同步当前值
        ProductCode = _inspectionContext.ProductCode;
        SerialNumber = _inspectionContext.SerialNumber;
        CameraName = _inspectionContext.SelectedCamera?.Name ?? string.Empty;
    }

    private void OnInspectionContextProductCodeChanged(object? sender, EventArgs e)
    {
        ProductCode = _inspectionContext.ProductCode;
    }

    private void OnInspectionContextSerialNumberChanged(object? sender, EventArgs e)
    {
        SerialNumber = _inspectionContext.SerialNumber;
    }

    private void OnInspectionContextSelectedCameraChanged(object? sender, EventArgs e)
    {
        CameraName = _inspectionContext.SelectedCamera?.Name ?? string.Empty;
    }

    public void RefreshPublicParams()
    {
        ProductCode = _inspectionContext.ProductCode;
        SerialNumber = _inspectionContext.SerialNumber;
        CameraName = _inspectionContext.SelectedCamera?.Name ?? string.Empty;
    }

    public ObservableCollection<string> ResultOptions { get; }

    public ObservableCollection<string> TaskOptions { get; }

    public ObservableCollection<InspectionResultQueryRowViewModel> Records { get; }

    public bool CanQuery => !IsBusy;

    [ObservableProperty]
    private bool last100Only = true;

    [ObservableProperty]
    private DateTime? startDate;

    [ObservableProperty]
    private DateTime? endDate;

    [ObservableProperty]
    private string productCode = string.Empty;

    [ObservableProperty]
    private string serialNumber = string.Empty;

    [ObservableProperty]
    private string cameraName = string.Empty;

    [ObservableProperty]
    private string selectedTaskFilter;

    [ObservableProperty]
    private string selectedResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueryCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText;

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private async Task QueryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (StartDate.HasValue &&
            EndDate.HasValue &&
            StartDate.Value.Date > EndDate.Value.Date)
        {
            StatusText = "开始时间不能晚于结束时间。";
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshTaskOptionsAsync();
            var query = new InspectionResultQuery
            {
                StartTime = StartDate.HasValue ? new DateTimeOffset(StartDate.Value.Date) : null,
                EndTime = EndDate.HasValue ? new DateTimeOffset(EndDate.Value.Date.AddDays(1).AddTicks(-1)) : null,
                ProductCode = EmptyToNull(ProductCode),
                SerialNumber = EmptyToNull(SerialNumber),
                CameraName = EmptyToNull(CameraName),
                TaskName = string.Equals(SelectedTaskFilter, ResultAllText, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : EmptyToNull(SelectedTaskFilter),
                Result = string.Equals(SelectedResult, ResultAllText, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : EmptyToNull(SelectedResult),
                Limit = Last100Only ? 100 : null
            };

            var records = await _resultStore.QueryAsync(query);
            Records.Clear();
            foreach (var record in records)
            {
                Records.Add(new InspectionResultQueryRowViewModel(record));
            }

            StatusText = $"已加载 {Records.Count} 条记录。{FormatQuerySummary(query)}";
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败：{ex.Message}";
            AppDiagnostics.Error("inspection-result-query", "Inspection result query failed.", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanExport));
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        Last100Only = true;
        StartDate = null;
        EndDate = null;
        ProductCode = string.Empty;
        SerialNumber = string.Empty;
        CameraName = string.Empty;
        SelectedTaskFilter = ResultAllText;
        SelectedResult = ResultAllText;
        await QueryAsync();
    }

    public bool CanExport => !IsBusy && !string.IsNullOrWhiteSpace(SerialNumber) && Records.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(string? format)
    {
        if (string.IsNullOrWhiteSpace(SerialNumber) || Records.Count == 0)
        {
            StatusText = "请先输入序列号并查询数据。";
            return;
        }

        try
        {
            var formatLower = (format ?? "html").ToLowerInvariant();
            var extension = formatLower == "pdf" ? ".pdf" : ".html";
            var filter = formatLower == "pdf"
                ? "PDF Files (*.pdf)|*.pdf"
                : "HTML Files (*.html)|*.html";

            var dialog = new SaveFileDialog
            {
                Title = "导出检测报告",
                Filter = filter,
                FileName = $"检测报告_{SerialNumber}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}",
                DefaultExt = extension
            };

            if (dialog.ShowDialog() == true)
            {
                IsBusy = true;
                StatusText = "正在导出，请稍候...";

                var exportFormat = formatLower == "pdf" ? ExportFormat.Pdf : ExportFormat.Html;
                var records = Records.Select(r => r.ToInspectionResultRecord()).ToList();

                var exportService = new InspectionResultExportService();
                await exportService.ExportAsync(SerialNumber, records, dialog.FileName, exportFormat);

                StatusText = $"导出成功：{Path.GetFileName(dialog.FileName)}";

                if (MessageBox.Show($"报告已导出到：\n{dialog.FileName}\n\n是否打开文件？",
                    "导出成功", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dialog.FileName,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
            AppDiagnostics.Error("inspection-result-export", "Export failed.", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanExport));
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowRawImage))]
    private void ShowRawImage(InspectionResultQueryRowViewModel? row)
    {
        ShowImage(row?.ResolveRawImagePath(), "原图");
    }

    [RelayCommand(CanExecute = nameof(CanShowRoiImage))]
    private void ShowRoiImage(InspectionResultQueryRowViewModel? row)
    {
        ShowImage(row?.ResolveRoiImagePath(), "ROI图");
    }

    private void ShowImage(string? imagePath, string imageLabel)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            StatusText = $"当前记录没有{imageLabel}路径。";
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(imagePath);
        }
        catch (Exception ex)
        {
            StatusText = $"图片路径无效：{ex.Message}";
            return;
        }

        if (!File.Exists(fullPath))
        {
            StatusText = $"{imageLabel}不存在：{fullPath}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
            StatusText = $"已定位{imageLabel}：{Path.GetFileName(fullPath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开{imageLabel}位置失败：{ex.Message}";
            AppDiagnostics.Error("inspection-result-query", $"Open image location failed. Path={fullPath}", ex);
        }
    }

    private static bool CanShowRawImage(InspectionResultQueryRowViewModel? row)
    {
        return row?.HasRawImagePath == true;
    }

    private static bool CanShowRoiImage(InspectionResultQueryRowViewModel? row)
    {
        return row?.HasRoiImagePath == true;
    }

    private async Task RefreshTaskOptionsAsync()
    {
        var selected = SelectedTaskFilter;
        var taskNames = await _resultStore.ListTaskNamesAsync();
        TaskOptions.Clear();
        TaskOptions.Add(ResultAllText);
        foreach (var taskName in taskNames)
        {
            TaskOptions.Add(taskName);
        }

        SelectedTaskFilter = TaskOptions.FirstOrDefault(task =>
            string.Equals(task, selected, StringComparison.OrdinalIgnoreCase)) ?? ResultAllText;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatQuerySummary(InspectionResultQuery query)
    {
        var parts = new List<string>();
        if (query.Limit.HasValue)
        {
            parts.Add($"最近{query.Limit.Value}条");
        }

        if (query.StartTime.HasValue || query.EndTime.HasValue)
        {
            var start = query.StartTime?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "不限";
            var end = query.EndTime?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "不限";
            parts.Add($"时间 {start} 至 {end}");
        }

        AddSummary(parts, "成品号", query.ProductCode);
        AddSummary(parts, "序列号", query.SerialNumber);
        AddSummary(parts, "相机", query.CameraName);
        AddSummary(parts, "Task", query.TaskName);
        AddSummary(parts, "结果", query.Result);
        return parts.Count == 0 ? "条件：不限。" : $"条件：{string.Join("，", parts)}。";
    }

    private static void AddSummary(ICollection<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label} {value}");
        }
    }
}

public sealed class InspectionResultQueryRowViewModel
{
    private readonly InspectionResultRecord _record;

    public InspectionResultQueryRowViewModel(InspectionResultRecord record)
    {
        _record = record;
    }

    public long Id => _record.Id;

    public string OccurredAtText => _record.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string ProductCode => _record.ProductCode;

    public string SerialNumber => EmptyText(_record.SerialNumber);

    public string CameraName => _record.CameraName;

    public string TaskName => _record.TaskName;

    public string Result => _record.Result;

    public Brush ResultBrush => string.Equals(Result, "OK", StringComparison.OrdinalIgnoreCase)
        ? UiBrushes.Success
        : UiBrushes.Danger;

    public string OkScoreText => FormatScore(_record.OkScore);

    public string NgScoreText => FormatScore(_record.NgScore);

    public string ElapsedMsText => _record.ElapsedMs.HasValue
        ? _record.ElapsedMs.Value.ToString("0", CultureInfo.InvariantCulture)
        : "--";

    public string ErrorText => EmptyText(_record.ErrorMessage);

    public bool HasRawImagePath => !string.IsNullOrWhiteSpace(_record.RawImagePath);

    public bool HasRoiImagePath => !string.IsNullOrWhiteSpace(_record.CropImagePath);

    public string RawImageButtonToolTip => HasRawImagePath
        ? $"在资源管理器中定位原图：{_record.RawImagePath}"
        : "当前记录没有原图路径";

    public string RoiImageButtonToolTip => HasRoiImagePath
        ? $"在资源管理器中定位ROI图：{_record.CropImagePath}"
        : "当前记录没有ROI图路径";

    public string DetailText
    {
        get
        {
            var builder = new StringBuilder();
            AppendLine(builder, "ID", _record.Id.ToString(CultureInfo.InvariantCulture));
            AppendLine(builder, "时间", OccurredAtText);
            AppendLine(builder, "Cycle", _record.CycleId.ToString(CultureInfo.InvariantCulture));
            AppendLine(builder, "成品号", _record.ProductCode);
            AppendLine(builder, "序列号", _record.SerialNumber);
            AppendLine(builder, "相机", _record.CameraName);
            AppendLine(builder, "任务", _record.TaskName);
            AppendLine(builder, "结果", _record.Result);
            AppendLine(builder, "OK分数", FormatScore(_record.OkScore));
            AppendLine(builder, "NG分数", FormatScore(_record.NgScore));
            AppendLine(builder, "Margin", FormatScore(_record.Margin));
            AppendLine(builder, "Threshold", FormatScore(_record.Threshold));
            AppendLine(builder, "TopK", _record.TopK?.ToString(CultureInfo.InvariantCulture));
            AppendLine(builder, "耗时(ms)", ElapsedMsText);
            AppendLine(builder, "原图", _record.RawImagePath);
            AppendLine(builder, "Crop", _record.CropImagePath);
            AppendLine(builder, "TopOK", _record.TopOkImagePath);
            AppendLine(builder, "TopNG", _record.TopNgImagePath);
            AppendLine(builder, "错误", _record.ErrorMessage);
            return builder.ToString().TrimEnd();
        }
    }

    public string ResolveRawImagePath()
    {
        return _record.RawImagePath ?? string.Empty;
    }

    public string ResolveRoiImagePath()
    {
        return _record.CropImagePath ?? string.Empty;
    }

    private static string EmptyText(string? value) => string.IsNullOrWhiteSpace(value) ? "--" : value;

    private static string FormatScore(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "--";
    }

    private static void AppendLine(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(label).Append("：").AppendLine(value);
        }
    }

    public InspectionResultRecord ToInspectionResultRecord() => _record;
}

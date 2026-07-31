using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;
using VisionWorkbench.Services.Clip;

namespace VisionWorkbench.ViewModels;

public sealed partial class TaskSampleLibraryViewModel : ObservableObject
{
    private readonly ClipTrainingLibraryService _libraryService;
    private readonly IUserDialogService _dialogService;

    public TaskSampleLibraryViewModel(
        InspectionTaskDefinition task,
        ClipTrainingLibraryService libraryService,
        IUserDialogService dialogService)
    {
        Task = task;
        _libraryService = libraryService;
        _dialogService = dialogService;
        Samples = [];
    }

    public InspectionTaskDefinition Task { get; }

    public ObservableCollection<ClipTrainingSampleViewModel> Samples { get; }

    public string WindowTitle => $"任务样本库 - {Task.Name}";

    public bool CanOperate => !IsBusy;

    public bool CanOperateSelected => !IsBusy && SelectedSample != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanOperateSelected))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperateSelected))]
    private ClipTrainingSampleViewModel? selectedSample;

    [ObservableProperty]
    private ImageSource? previewImage;

    [ObservableProperty]
    private string previewTitle = "未选择样本";

    [ObservableProperty]
    private string previewDetail = "选择左侧样本后显示预览。";

    [ObservableProperty]
    private string statusText = "正在加载样本库。";

    [ObservableProperty]
    private string summaryText = "未加载";

    partial void OnSelectedSampleChanged(ClipTrainingSampleViewModel? value)
    {
        if (value != null)
        {
            _ = ShowSampleAsync(value);
        }
        else
        {
            PreviewImage = null;
            PreviewTitle = "未选择样本";
            PreviewDetail = "选择左侧样本后显示预览。";
        }

        RefreshCommandStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    public async Task RefreshAsync()
    {
        await RunOperationAsync("正在加载任务样本库...", async () =>
        {
            await LoadSamplesCoreAsync(SelectedSample?.Info.Id);
            StatusText = Samples.Count == 0
                ? "当前任务样本库为空。"
                : "任务样本库已加载。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private async Task MarkSelectedOkAsync()
    {
        await RelabelSelectedAsync(InspectionJudgment.OK);
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private async Task MarkSelectedNgAsync()
    {
        await RelabelSelectedAsync(InspectionJudgment.NG);
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private async Task IgnoreSelectedAsync()
    {
        if (SelectedSample == null)
        {
            return;
        }

        await RunOperationAsync("正在忽略样本...", async () =>
        {
            var selectedId = SelectedSample.Info.Id;
            await _libraryService.SetSampleIgnoredAsync(SelectedSample.Info);
            await LoadSamplesCoreAsync(selectedId);
            StatusText = "样本已设置为忽略，不参与推理和训练。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedSample == null)
        {
            return;
        }

        var confirm = _dialogService.Confirm(
            $"确定删除样本？\n{SelectedSample.ImagePath}",
            "删除任务样本");
        if (!confirm)
        {
            return;
        }

        await RunOperationAsync("正在删除样本...", async () =>
        {
            await _libraryService.DeleteSampleAsync(SelectedSample.Info);
            await LoadSamplesCoreAsync(null);
            StatusText = "样本已删除。";
        });
    }

    private async Task RelabelSelectedAsync(InspectionJudgment label)
    {
        if (SelectedSample == null)
        {
            return;
        }

        await RunOperationAsync($"正在改为 {label} 样本...", async () =>
        {
            var selectedId = SelectedSample.Info.Id;
            await _libraryService.RelabelSampleAsync(SelectedSample.Info, label);
            await LoadSamplesCoreAsync(selectedId);
            StatusText = $"样本已改为 {label}，并重新启用。";
        });
    }

    private async Task LoadSamplesCoreAsync(long? selectedId)
    {
        var samples = await _libraryService.LoadAllSamplesAsync(Task);
        Samples.Clear();
        foreach (var sample in samples)
        {
            Samples.Add(new ClipTrainingSampleViewModel(sample));
        }

        SelectedSample = selectedId.HasValue
            ? Samples.FirstOrDefault(sample => sample.Info.Id == selectedId.Value)
              ?? Samples.FirstOrDefault()
            : Samples.FirstOrDefault();
        SummaryText = BuildSummaryText();
    }

    private async Task ShowSampleAsync(ClipTrainingSampleViewModel sample)
    {
        try
        {
            PreviewImage = await System.Threading.Tasks.Task.Run(() => ImageSourceFileStorage.LoadImage(sample.ImagePath));
            PreviewTitle = $"{sample.StateText}  {sample.FileName}";
            PreviewDetail = $"{sample.DetailText}\n{sample.ImagePath}";
        }
        catch (Exception ex)
        {
            StatusText = $"样本预览失败：{ex.Message}";
        }
    }

    private async Task RunOperationAsync(string busyText, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = busyText;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildSummaryText()
    {
        var ok = Samples.Count(sample => sample.Enabled && sample.Info.Label == InspectionJudgment.OK);
        var ng = Samples.Count(sample => sample.Enabled && sample.Info.Label == InspectionJudgment.NG);
        var ignored = Samples.Count(sample => !sample.Enabled);
        var vectorSet = Task.EnsureClipVectorSet();
        return string.Join(Environment.NewLine,
            $"任务：{Task.Name}",
            $"向量集：{vectorSet.VectorSetId}",
            $"OK：{ok}",
            $"NG：{ng}",
            $"忽略：{ignored}");
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanOperateSelected));
        RefreshCommand.NotifyCanExecuteChanged();
        MarkSelectedOkCommand.NotifyCanExecuteChanged();
        MarkSelectedNgCommand.NotifyCanExecuteChanged();
        IgnoreSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
}

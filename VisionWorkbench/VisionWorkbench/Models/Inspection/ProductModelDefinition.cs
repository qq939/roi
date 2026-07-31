using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionWorkbench.Models.Inspection;

public sealed partial class ProductModelDefinition : ObservableObject
{
    [ObservableProperty]
    private string id = "default-product";

    [ObservableProperty]
    private string name = "默认型号";

    [ObservableProperty]
    private bool enabled = true;
}

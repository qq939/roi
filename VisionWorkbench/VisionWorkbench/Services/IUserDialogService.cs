using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Services;

public interface IUserDialogService
{
    IReadOnlyList<string> OpenImageFiles(bool multiselect);

    bool Confirm(string message, string title);

    ProductModelCreateDialogResult? ShowCreateProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        BarcodeScannerSerialPortService barcodeScanner);

    ProductModelCopyDialogResult? ShowCopyProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition? selectedProduct,
        BarcodeScannerSerialPortService barcodeScanner);

    string? ShowRenameProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition product);

    bool ShowDeleteProductModelDialog(
        ProductModelDefinition product,
        BarcodeScannerSerialPortService barcodeScanner);
}

public sealed record ProductModelCreateDialogResult(
    string ProductCode,
    string ProductName);

public sealed record ProductModelCopyDialogResult(
    ProductModelDefinition SourceProduct,
    string ProductCode,
    string ProductName);

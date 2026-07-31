using System.Windows;
using Microsoft.Win32;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Views;

namespace VisionWorkbench.Services;

public sealed class UserDialogService : IUserDialogService
{
    private const string ImageFilter = "Image Files|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All Files|*.*";

    public IReadOnlyList<string> OpenImageFiles(bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            Filter = ImageFilter,
            Multiselect = multiselect
        };

        if (dialog.ShowDialog() != true)
        {
            return Array.Empty<string>();
        }

        return multiselect
            ? dialog.FileNames
            : string.IsNullOrWhiteSpace(dialog.FileName)
                ? Array.Empty<string>()
                : [dialog.FileName];
    }

    public bool Confirm(string message, string title)
    {
        return MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public ProductModelCreateDialogResult? ShowCreateProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        BarcodeScannerSerialPortService barcodeScanner)
    {
        var dialog = new ProductModelCreateDialog(products, barcodeScanner)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true
            ? new ProductModelCreateDialogResult(dialog.ProductCode, dialog.ProductName)
            : null;
    }

    public ProductModelCopyDialogResult? ShowCopyProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition? selectedProduct,
        BarcodeScannerSerialPortService barcodeScanner)
    {
        var dialog = new ProductModelCopyDialog(products, selectedProduct, barcodeScanner)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true && dialog.SourceProduct != null
            ? new ProductModelCopyDialogResult(dialog.SourceProduct, dialog.ProductCode, dialog.ProductName)
            : null;
    }

    public string? ShowRenameProductModelDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition product)
    {
        var dialog = new ProductModelRenameDialog(products, product)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.ProductName : null;
    }

    public bool ShowDeleteProductModelDialog(
        ProductModelDefinition product,
        BarcodeScannerSerialPortService barcodeScanner)
    {
        var dialog = new ProductModelDeleteDialog(product, barcodeScanner)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true;
    }
}

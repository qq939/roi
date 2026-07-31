using System.Collections.ObjectModel;
using System.Windows;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public partial class ProductModelCreateDialog : Window
{
    private readonly IReadOnlyCollection<ProductModelDefinition> _products;
    private readonly BarcodeScannerSerialPortService _barcodeScanner;
    private bool _isNameManuallyEdited;

    public ProductModelCreateDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        BarcodeScannerSerialPortService barcodeScanner)
    {
        InitializeComponent();
        _products = products;
        _barcodeScanner = barcodeScanner;
        ScannerBarcodeBox.Text = _barcodeScanner.LastProductCode;
        _barcodeScanner.BarcodeScanned += OnBarcodeScanned;
        Closed += OnClosed;
        ProductCodeBox.Focus();
    }

    public string ProductCode => ProductCodeBox.Text.Trim();

    public string ProductName => ProductNameBox.Text.Trim();

    private void OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        if (!BarcodeScannerSerialPortService.IsProductCodeBarcode(e.Barcode))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            ScannerBarcodeBox.Text = e.Barcode;
            ProductCodeBox.Text = e.Barcode;
            ProductCodeBox.CaretIndex = ProductCodeBox.Text.Length;
            SyncNameFromProductCode();
        });
    }

    private void ProductCodeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SyncNameFromProductCode();
    }

    private void SyncNameFromProductCode()
    {
        if (!_isNameManuallyEdited && string.IsNullOrWhiteSpace(ProductNameBox.Text))
        {
            ProductNameBox.Text = ProductCodeBox.Text;
        }
    }

    private void OnNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _isNameManuallyEdited = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _barcodeScanner.BarcodeScanned -= OnBarcodeScanned;
        Closed -= OnClosed;
    }

    private void FillProductCodeFromScanner_Click(object sender, RoutedEventArgs e)
    {
        ProductCodeBox.Text = _barcodeScanner.LastProductCode;
        ProductCodeBox.CaretIndex = ProductCodeBox.Text.Length;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInput())
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(ProductCode))
        {
            MessageText.Text = "成品号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProductName))
        {
            MessageText.Text = "名称不能为空";
            return false;
        }

        if (_products.Any(product => string.Equals(product.Id.Trim(), ProductCode, StringComparison.OrdinalIgnoreCase)))
        {
            MessageText.Text = "成品号已存在";
            return false;
        }

        if (_products.Any(product => string.Equals(product.Name.Trim(), ProductName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageText.Text = "名称已存在";
            return false;
        }

        MessageText.Text = string.Empty;
        return true;
    }
}

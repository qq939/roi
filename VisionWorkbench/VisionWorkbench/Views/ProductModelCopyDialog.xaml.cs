using System.Windows;
using System.Windows.Controls;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public partial class ProductModelCopyDialog : Window
{
    private readonly IReadOnlyList<ProductModelDefinition> _products;
    private readonly BarcodeScannerSerialPortService _barcodeScanner;
    private bool _syncingSource;

    public ProductModelCopyDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition? selectedProduct,
        BarcodeScannerSerialPortService barcodeScanner)
    {
        InitializeComponent();
        _products = products.ToArray();
        _barcodeScanner = barcodeScanner;
        SourceCodeCombo.ItemsSource = _products;
        SourceNameCombo.ItemsSource = _products;
        ScannerBarcodeBox.Text = _barcodeScanner.LastProductCode;
        _barcodeScanner.BarcodeScanned += OnBarcodeScanned;
        Closed += OnClosed;

        SetSourceProduct(selectedProduct ?? _products.FirstOrDefault());
        TargetProductCodeBox.Focus();
    }

    public ProductModelDefinition? SourceProduct => SourceCodeCombo.SelectedItem as ProductModelDefinition;

    public string ProductCode => TargetProductCodeBox.Text.Trim();

    public string ProductName => TargetProductNameBox.Text.Trim();

    private void OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        if (!BarcodeScannerSerialPortService.IsProductCodeBarcode(e.Barcode))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            ScannerBarcodeBox.Text = e.Barcode;
            TargetProductCodeBox.Text = e.Barcode;
            TargetProductCodeBox.CaretIndex = TargetProductCodeBox.Text.Length;
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _barcodeScanner.BarcodeScanned -= OnBarcodeScanned;
        Closed -= OnClosed;
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSource)
        {
            return;
        }

        var selected = (sender as ComboBox)?.SelectedItem as ProductModelDefinition;
        SetSourceProduct(selected);
    }

    private void SetSourceProduct(ProductModelDefinition? product)
    {
        if (product == null)
        {
            return;
        }

        _syncingSource = true;
        SourceCodeCombo.SelectedItem = product;
        SourceNameCombo.SelectedItem = product;
        _syncingSource = false;
        TargetProductNameBox.Text = BuildUniqueCopyName(product.Name);
    }

    private string BuildUniqueCopyName(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "新型号" : sourceName.Trim();
        var index = 1;
        while (true)
        {
            var candidate = $"{baseName} 副本{index}";
            if (_products.All(product => !string.Equals(product.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            index++;
        }
    }

    private void FillProductCodeFromScanner_Click(object sender, RoutedEventArgs e)
    {
        TargetProductCodeBox.Text = _barcodeScanner.LastProductCode;
        TargetProductCodeBox.CaretIndex = TargetProductCodeBox.Text.Length;
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
        if (SourceProduct == null)
        {
            MessageText.Text = "请选择源型号";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProductCode))
        {
            MessageText.Text = "新成品号不能为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ProductName))
        {
            MessageText.Text = "新名称不能为空";
            return false;
        }

        if (_products.Any(product => string.Equals(product.Id.Trim(), ProductCode, StringComparison.OrdinalIgnoreCase)))
        {
            MessageText.Text = "新成品号已存在";
            return false;
        }

        if (_products.Any(product => string.Equals(product.Name.Trim(), ProductName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageText.Text = "新名称已存在";
            return false;
        }

        MessageText.Text = string.Empty;
        return true;
    }
}

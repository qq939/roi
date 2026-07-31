using System.Windows;
using VisionWorkbench.Models.Inspection;
using VisionWorkbench.Services;

namespace VisionWorkbench.Views;

public partial class ProductModelDeleteDialog : Window
{
    private readonly ProductModelDefinition _product;
    private readonly BarcodeScannerSerialPortService _barcodeScanner;

    public ProductModelDeleteDialog(ProductModelDefinition product, BarcodeScannerSerialPortService barcodeScanner)
    {
        InitializeComponent();
        _product = product;
        _barcodeScanner = barcodeScanner;
        ProductCodeText.Text = product.Id;
        ProductNameText.Text = product.Name;
        ScannerBarcodeBox.Text = _barcodeScanner.LastProductCode;
        _barcodeScanner.BarcodeScanned += OnBarcodeScanned;
        Closed += OnClosed;
        ConfirmationCodeBox.Focus();
    }

    private void OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        if (!BarcodeScannerSerialPortService.IsProductCodeBarcode(e.Barcode))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            ScannerBarcodeBox.Text = e.Barcode;
            ConfirmationCodeBox.Text = e.Barcode;
            ConfirmationCodeBox.CaretIndex = ConfirmationCodeBox.Text.Length;
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _barcodeScanner.BarcodeScanned -= OnBarcodeScanned;
        Closed -= OnClosed;
    }

    private void FillProductCodeFromScanner_Click(object sender, RoutedEventArgs e)
    {
        ConfirmationCodeBox.Text = _barcodeScanner.LastProductCode;
        ConfirmationCodeBox.CaretIndex = ConfirmationCodeBox.Text.Length;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ConfirmationCodeBox.Text.Trim(), _product.Id, StringComparison.OrdinalIgnoreCase))
        {
            MessageText.Text = "确认成品号与当前成品号不一致";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

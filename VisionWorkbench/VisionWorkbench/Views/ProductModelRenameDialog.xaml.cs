using System.Windows;
using VisionWorkbench.Models.Inspection;

namespace VisionWorkbench.Views;

public partial class ProductModelRenameDialog : Window
{
    private readonly IReadOnlyCollection<ProductModelDefinition> _products;
    private readonly ProductModelDefinition _product;

    public ProductModelRenameDialog(
        IReadOnlyCollection<ProductModelDefinition> products,
        ProductModelDefinition product)
    {
        InitializeComponent();
        _products = products;
        _product = product;
        ProductCodeText.Text = product.Id;
        ProductNameBox.Text = product.Name;
        ProductNameBox.Focus();
        ProductNameBox.SelectAll();
    }

    public string ProductName => ProductNameBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProductName))
        {
            MessageText.Text = "名称不能为空";
            return;
        }

        if (_products.Any(product =>
                !ReferenceEquals(product, _product) &&
                string.Equals(product.Name.Trim(), ProductName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageText.Text = "名称已存在";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

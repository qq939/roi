using System.Windows.Controls;

namespace VisionWorkbench.Views;

public partial class HomeWorkspaceView : UserControl
{
    public HomeWorkspaceView()
    {
        InitializeComponent();
    }

    private void EventTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        EventTextBox.ScrollToEnd();
    }
}

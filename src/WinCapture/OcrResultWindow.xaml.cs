using System.Windows;

namespace WinCapture;

public partial class OcrResultWindow : Window
{
    public OcrResultWindow(string text)
    {
        InitializeComponent();
        ResultTextBox.Text = text;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ResultTextBox.Text);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

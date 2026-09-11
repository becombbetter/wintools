using System.Windows;
using System.Windows.Input;

namespace WinCapture;

public partial class TextInputWindow : Window
{
    public string Value => ValueTextBox.Text;

    public TextInputWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ValueTextBox.Focus();
        ValueTextBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                DialogResult = true;
            }
        };
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

using System.Drawing;
using System.Windows;
using System.Windows.Input;
using WinCapture.Services;

namespace WinCapture;

public partial class PinWindow : Window
{
    private readonly Bitmap _bitmap;

    public PinWindow(Bitmap bitmap)
    {
        InitializeComponent();
        _bitmap = bitmap;
        PinnedImage.Source = ScreenCaptureService.ToBitmapSource(bitmap);
        var ratio = bitmap.Width / (double)bitmap.Height;
        Width = Math.Min(720, Math.Max(240, bitmap.Width * 0.55));
        Height = Width / ratio;
        Closed += (_, _) => _bitmap.Dispose();
    }

    private void PinnedImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpacityButton_Click(object sender, RoutedEventArgs e)
    {
        Opacity = Opacity > 0.8 ? 0.62 : 1;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

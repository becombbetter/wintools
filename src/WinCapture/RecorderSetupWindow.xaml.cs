using System.Windows;
using System.Windows.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace WinCapture;

public partial class RecorderSetupWindow : Window
{
    public RecordingRequest? Request { get; private set; }

    public RecorderSetupWindow(AppSettings settings)
    {
        InitializeComponent();
        SystemAudioCheckBox.IsChecked = settings.RecordSystemAudio;
        MicrophoneCheckBox.IsChecked = settings.RecordMicrophone;
        CursorCheckBox.IsChecked = settings.IncludeCursorInRecordings;
        ClickCheckBox.IsChecked = settings.ShowMouseClicks;
        SelectByTag(FrameRateComboBox, settings.FrameRate.ToString(), 1);
        SelectByTag(BitrateComboBox, settings.VideoBitrateMbps.ToString(), 1);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var targetTag = ((ComboBoxItem)TargetComboBox.SelectedItem).Tag?.ToString() ?? "PrimaryScreen";
        var frameRate = int.Parse(((ComboBoxItem)FrameRateComboBox.SelectedItem).Tag!.ToString()!);
        var bitrate = int.Parse(((ComboBoxItem)BitrateComboBox.SelectedItem).Tag!.ToString()!);
        Request = new RecordingRequest(
            Enum.Parse<RecordingTarget>(targetTag),
            SystemAudioCheckBox.IsChecked == true,
            MicrophoneCheckBox.IsChecked == true,
            CursorCheckBox.IsChecked == true,
            ClickCheckBox.IsChecked == true,
            frameRate,
            bitrate);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectByTag(WpfComboBox comboBox, string tag, int fallback)
    {
        comboBox.SelectedIndex = fallback;
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == tag)
            {
                comboBox.SelectedItem = item;
                break;
            }
        }
    }
}

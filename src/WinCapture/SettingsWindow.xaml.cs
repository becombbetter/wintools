using System.Windows;
using System.Windows.Controls;
using WinCapture.Services;
using Forms = System.Windows.Forms;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WinCapture;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ImageDirectoryTextBox.Text = settings.ImageDirectory;
        VideoDirectoryTextBox.Text = settings.VideoDirectory;
        CaptureCursorCheckBox.IsChecked = settings.IncludeCursorInScreenshots;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        LongShotAutoScrollCheckBox.IsChecked = settings.LongShotAutoScroll;
        RecordSystemAudioCheckBox.IsChecked = settings.RecordSystemAudio;
        RecordMicrophoneCheckBox.IsChecked = settings.RecordMicrophone;
        RecordCursorCheckBox.IsChecked = settings.IncludeCursorInRecordings;
        ShowClicksCheckBox.IsChecked = settings.ShowMouseClicks;
        SelectByTag(DelayComboBox, settings.CaptureDelaySeconds.ToString());
    }

    private void BrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(ImageDirectoryTextBox);
    }

    private void BrowseVideoButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(VideoDirectoryTextBox);
    }

    private static void BrowseFolder(WpfTextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            InitialDirectory = target.Text,
            UseDescriptionForTitle = true,
            Description = "选择保存目录"
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        _settings.ImageDirectory = NormalizeDirectory(
            ImageDirectoryTextBox.Text, _settings.ImageDirectory, defaults.ImageDirectory);
        _settings.VideoDirectory = NormalizeDirectory(
            VideoDirectoryTextBox.Text, _settings.VideoDirectory, defaults.VideoDirectory);
        ImageDirectoryTextBox.Text = _settings.ImageDirectory;
        VideoDirectoryTextBox.Text = _settings.VideoDirectory;
        _settings.IncludeCursorInScreenshots = CaptureCursorCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _settings.LongShotAutoScroll = LongShotAutoScrollCheckBox.IsChecked == true;
        _settings.RecordSystemAudio = RecordSystemAudioCheckBox.IsChecked == true;
        _settings.RecordMicrophone = RecordMicrophoneCheckBox.IsChecked == true;
        _settings.IncludeCursorInRecordings = RecordCursorCheckBox.IsChecked == true;
        _settings.ShowMouseClicks = ShowClicksCheckBox.IsChecked == true;
        _settings.CaptureDelaySeconds = int.Parse(((ComboBoxItem)DelayComboBox.SelectedItem).Tag!.ToString()!);
        SettingsService.Save(_settings);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 把用户输入的目录规整成合法路径：留空回退到原值，仍为空则用默认目录，
    /// 路径非法时同样回退，避免把空串写进设置后引发崩溃。
    /// </summary>
    private static string NormalizeDirectory(string? input, string current, string fallback)
    {
        var text = (input ?? string.Empty).Trim().Trim('"');
        var candidate = string.IsNullOrWhiteSpace(text) ? current : text;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return fallback;
        }
    }

    private static void SelectByTag(WpfComboBox comboBox, string value)
    {
        comboBox.SelectedIndex = 0;
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == value)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }
}

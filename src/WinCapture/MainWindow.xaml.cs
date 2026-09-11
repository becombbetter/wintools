using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinCapture.Services;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace WinCapture;

public partial class MainWindow : Window
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkPrintScreen = 0x2C;
    private const uint VkR = 0x52;

    private GlobalHotKeyService? _hotKeys;
    private readonly Forms.NotifyIcon _trayIcon;
    private RecorderService? _recorder;
    private RecordingControlWindow? _recordingControl;
    private bool _allowClose;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = CreateTrayIcon();
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Loaded += (_, _) =>
        {
            RefreshHistory();
            if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase)))
            {
                Hide();
            }
        };
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("区域截图", null, (_, _) => Dispatcher.Invoke(() => _ = CaptureAsync(CaptureKind.Region)));
        menu.Items.Add("窗口截图", null, (_, _) => Dispatcher.Invoke(() => _ = CaptureAsync(CaptureKind.Window)));
        menu.Items.Add("全屏截图", null, (_, _) => Dispatcher.Invoke(() => _ = CaptureAsync(CaptureKind.FullScreen)));
        menu.Items.Add("长截图", null, (_, _) => Dispatcher.Invoke(() => _ = StartLongCaptureAsync()));
        menu.Items.Add("开始/停止录屏", null, (_, _) => Dispatcher.Invoke(() => _ = ToggleRecordingAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("显示主界面", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WinCapture",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        return icon;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hotKeys = new GlobalHotKeyService(handle);
        var registered = true;
        registered &= _hotKeys.Register(1, 0, VkPrintScreen, () => _ = CaptureAsync(CaptureKind.Region));
        registered &= _hotKeys.Register(2, ModAlt, VkPrintScreen, () => _ = CaptureAsync(CaptureKind.Window));
        registered &= _hotKeys.Register(3, ModControl, VkPrintScreen, () => _ = StartLongCaptureAsync());
        registered &= _hotKeys.Register(4, ModControl | ModShift, VkR, () => _ = ToggleRecordingAsync());
        registered &= _hotKeys.Register(5, ModShift, VkPrintScreen, () => _ = CaptureAsync(CaptureKind.FullScreen));
        if (!registered)
        {
            StatusText.Text = "部分全局快捷键被其他程序占用，可通过主界面继续使用";
        }
    }

    private async Task CaptureAsync(CaptureKind kind)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            Hide();
            if (App.Settings.CaptureDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(App.Settings.CaptureDelaySeconds));
            }
            else
            {
                await Task.Delay(220);
            }

            Rectangle bounds;
            if (kind == CaptureKind.FullScreen)
            {
                bounds = Forms.SystemInformation.VirtualScreen;
            }
            else
            {
                var selection = RegionSelector.Select(kind);
                if (selection is null)
                {
                    ShowMainWindow();
                    return;
                }

                bounds = selection.Bounds;
            }

            await Task.Delay(120);
            var bitmap = ScreenCaptureService.Capture(bounds, App.Settings.IncludeCursorInScreenshots);
            var capturedWidth = bitmap.Width;
            var capturedHeight = bitmap.Height;
            ShowMainWindow();
            var editor = new EditorWindow(bitmap);
            editor.Closed += (_, _) => RefreshHistory();
            editor.Show();
            StatusText.Text = $"已捕获 {capturedWidth} × {capturedHeight} px";
        }
        catch (Exception exception)
        {
            ShowMainWindow();
            WpfMessageBox.Show(this, exception.Message, "截图失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StartLongCaptureAsync()
    {
        if (_busy)
        {
            return;
        }

        var confirmation = WpfMessageBox.Show(
            this,
            "选择可滚动内容区域后，WinCapture 会把鼠标移入区域并自动向下滚动。请先把页面停在长截图起点。",
            "开始长截图",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        _busy = true;
        try
        {
            Hide();
            await Task.Delay(250);
            var selection = RegionSelector.Select(CaptureKind.Region);
            if (selection is null)
            {
                ShowMainWindow();
                return;
            }

            await Task.Delay(350);
            var progress = new Progress<LongShotProgress>(value =>
            {
                StatusText.Text = $"{value.Message}，当前高度 {value.AppendedHeight:N0} px";
            });
            var bitmap = await LongShotService.CaptureAsync(
                selection.Bounds,
                App.Settings.LongShotMaxFrames,
                progress);
            var capturedWidth = bitmap.Width;
            var capturedHeight = bitmap.Height;

            ShowMainWindow();
            var editor = new EditorWindow(bitmap);
            editor.Closed += (_, _) => RefreshHistory();
            editor.Show();
            StatusText.Text = $"长截图完成：{capturedWidth} × {capturedHeight} px";
        }
        catch (Exception exception)
        {
            ShowMainWindow();
            WpfMessageBox.Show(this, exception.Message, "长截图失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (_recorder?.IsRecording == true)
        {
            _recorder.Stop();
            return;
        }

        if (_busy)
        {
            return;
        }

        var setup = new RecorderSetupWindow(App.Settings) { Owner = IsVisible ? this : null };
        if (setup.ShowDialog() != true || setup.Request is null)
        {
            return;
        }

        _busy = true;
        try
        {
            Hide();
            SelectionResult? selection = null;
            if (setup.Request.Target == RecordingTarget.Region)
            {
                await Task.Delay(220);
                selection = RegionSelector.Select(CaptureKind.Region);
            }
            else if (setup.Request.Target == RecordingTarget.Window)
            {
                await Task.Delay(220);
                selection = RegionSelector.Select(CaptureKind.Window);
            }

            if (setup.Request.Target != RecordingTarget.PrimaryScreen && selection is null)
            {
                ShowMainWindow();
                return;
            }

            await Task.Delay(250);
            var outputPath = App.History.NewVideoPath();
            _recorder = new RecorderService();
            _recorder.Start(setup.Request, selection, outputPath);
            _recordingControl = new RecordingControlWindow(_recorder);
            _recordingControl.RecordingCompleted += path =>
            {
                StatusText.Text = $"录屏已保存：{path}";
                RefreshHistory();
                ShowMainWindow();
                _recorder?.Dispose();
                _recorder = null;
                _recordingControl = null;
            };
            _recordingControl.RecordingFailed += error =>
            {
                ShowMainWindow();
                WpfMessageBox.Show(this, error, "录屏失败", MessageBoxButton.OK, MessageBoxImage.Error);
                _recorder?.Dispose();
                _recorder = null;
                _recordingControl = null;
            };
            _recordingControl.Show();
        }
        catch (Exception exception)
        {
            ShowMainWindow();
            _recorder?.Dispose();
            _recorder = null;
            WpfMessageBox.Show(this, exception.Message, "录屏启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RefreshHistory()
    {
        HistoryList.ItemsSource = App.History.GetRecent();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RefreshHistory();
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void RegionCaptureButton_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureKind.Region);
    private void WindowCaptureButton_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureKind.Window);
    private void FullCaptureButton_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureKind.FullScreen);
    private void LongCaptureButton_Click(object sender, RoutedEventArgs e) => _ = StartLongCaptureAsync();
    private void RecordButton_Click(object sender, RoutedEventArgs e) => _ = ToggleRecordingAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(App.Settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            App.History.EnsureDirectories();
            RefreshHistory();
            StatusText.Text = "设置已保存";
        }
    }

    private void ManualButton_Click(object sender, RoutedEventArgs e)
    {
        var manualPath = Path.Combine(AppContext.BaseDirectory, "使用手册.html");
        if (File.Exists(manualPath))
        {
            OpenPath(manualPath);
        }
        else
        {
            WpfMessageBox.Show(this, "未找到使用手册.html。", "使用手册", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenImageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.Settings.ImageDirectory);
        OpenPath(App.Settings.ImageDirectory);
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem item && File.Exists(item.FilePath))
        {
            OpenPath(item.FilePath);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(1200, "WinCapture", "程序仍在托盘运行。", Forms.ToolTipIcon.Info);
        }
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _recordingControl?.Close();
        _recorder?.Dispose();
        _hotKeys?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}

using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenRecorderLib;
using WinCapture.Services;

namespace WinCapture;

public partial class RecordingControlWindow : Window
{
    private readonly RecorderService _recorder;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startedAt = DateTime.Now;
    private TimeSpan _pausedDuration;
    private DateTime? _pausedAt;
    private bool _isPaused;
    private bool _stopping;

    public event Action<string>? RecordingCompleted;
    public event Action<string>? RecordingFailed;

    public RecordingControlWindow(RecorderService recorder)
    {
        InitializeComponent();
        _recorder = recorder;
        _recorder.Completed += Recorder_Completed;
        _recorder.Failed += Recorder_Failed;
        _recorder.StatusChanged += Recorder_StatusChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
            Top = SystemParameters.WorkArea.Top + 18;
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
        Closed += RecordingControlWindow_Closed;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        if (_isPaused)
        {
            _recorder.Resume();
            _pausedDuration += DateTime.Now - _pausedAt!.Value;
            _pausedAt = null;
            _isPaused = false;
            PauseButton.Content = "Ⅱ";
            StateText.Text = "正在录制";
        }
        else
        {
            _recorder.Pause();
            _pausedAt = DateTime.Now;
            _isPaused = true;
            PauseButton.Content = "▶";
            StateText.Text = "已暂停";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        PauseButton.IsEnabled = false;
        StateText.Text = "正在保存…";
        _recorder.Stop();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = _pausedAt ?? DateTime.Now;
        TimeText.Text = (now - _startedAt - _pausedDuration).ToString(@"hh\:mm\:ss");
    }

    private void Recorder_StatusChanged(RecorderStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            if (status == RecorderStatus.Paused)
            {
                StateText.Text = "已暂停";
            }
        });
    }

    private void Recorder_Completed(string path)
    {
        Dispatcher.Invoke(() =>
        {
            RecordingCompleted?.Invoke(path);
            Close();
        });
    }

    private void Recorder_Failed(string error)
    {
        Dispatcher.Invoke(() =>
        {
            RecordingFailed?.Invoke(error);
            Close();
        });
    }

    private void RecordingControlWindow_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _recorder.Completed -= Recorder_Completed;
        _recorder.Failed -= Recorder_Failed;
        _recorder.StatusChanged -= Recorder_StatusChanged;
        if (!_stopping && _recorder.IsRecording)
        {
            _recorder.Stop();
        }
    }
}

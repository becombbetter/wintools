using System.Drawing;
using ScreenRecorderLib;
using FormsScreen = System.Windows.Forms.Screen;

namespace WinCapture.Services;

public sealed class RecorderService : IDisposable
{
    private Recorder? _recorder;

    public event Action<string>? Completed;
    public event Action<string>? Failed;
    public event Action<RecorderStatus>? StatusChanged;

    public bool IsRecording => _recorder?.Status is RecorderStatus.Recording or RecorderStatus.Paused;

    public void Start(RecordingRequest request, SelectionResult? selection, string outputPath)
    {
        if (_recorder is not null)
        {
            throw new InvalidOperationException("已有录屏任务正在运行。");
        }

        var source = CreateSource(request, selection);
        var audioSources = new List<AudioSourceBase>();
        if (request.SystemAudio && LoopbackAudioSource.Default is { } playback)
        {
            audioSources.Add(playback);
        }

        if (request.Microphone && CaptureAudioSource.Default is { } microphone)
        {
            audioSources.Add(microphone);
        }

        var options = RecorderOptions.Default;
        options.SourceOptions = new SourceOptions
        {
            RecordingSources = new List<RecordingSourceBase> { source }
        };
        options.OutputOptions = new OutputOptions
        {
            RecorderMode = RecorderMode.Video
        };
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Framerate = request.FrameRate,
            Bitrate = request.BitrateMbps * 1_000_000,
            IsHardwareEncodingEnabled = true,
            IsMp4FastStartEnabled = true,
            IsFixedFramerate = false
        };
        options.AudioOptions = new AudioOptions
        {
            IsAudioEnabled = audioSources.Count > 0,
            AudioSources = audioSources,
            Bitrate = AudioBitrate.bitrate_128kbps,
            Channels = AudioChannels.Stereo
        };
        options.MouseOptions = new MouseOptions
        {
            IsMousePointerEnabled = request.IncludeCursor,
            IsMouseClicksDetected = request.ShowClicks,
            MouseLeftClickDetectionColor = "#16CFC2",
            MouseRightClickDetectionColor = "#E95645",
            MouseClickDetectionRadius = 18,
            MouseClickDetectionDuration = 170
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += (_, eventArgs) =>
        {
            var filePath = eventArgs.FilePath;
            ReleaseRecorder();
            Completed?.Invoke(filePath);
        };
        _recorder.OnRecordingFailed += (_, eventArgs) =>
        {
            var error = eventArgs.Error;
            ReleaseRecorder();
            Failed?.Invoke(error);
        };
        _recorder.OnStatusChanged += (_, eventArgs) => StatusChanged?.Invoke(eventArgs.Status);
        _recorder.Record(outputPath);
    }

    public void Pause() => _recorder?.Pause();
    public void Resume() => _recorder?.Resume();
    public void Stop() => _recorder?.Stop();

    private static RecordingSourceBase CreateSource(RecordingRequest request, SelectionResult? selection)
    {
        if (request.Target == RecordingTarget.Window)
        {
            if (selection is null || selection.WindowHandle == nint.Zero)
            {
                throw new InvalidOperationException("未选择有效窗口。");
            }

            return new WindowRecordingSource(selection.WindowHandle)
            {
                IsBorderRequired = false,
                IsCursorCaptureEnabled = request.IncludeCursor
            };
        }

        if (request.Target == RecordingTarget.Region)
        {
            if (selection is null)
            {
                throw new InvalidOperationException("未选择录制区域。");
            }

            var screen = FormsScreen.FromPoint(new Point(
                selection.Bounds.Left + selection.Bounds.Width / 2,
                selection.Bounds.Top + selection.Bounds.Height / 2));
            var relative = new Rectangle(
                selection.Bounds.Left - screen.Bounds.Left,
                selection.Bounds.Top - screen.Bounds.Top,
                selection.Bounds.Width,
                selection.Bounds.Height);

            return new DisplayRecordingSource(screen.DeviceName)
            {
                RecorderApi = RecorderApi.DesktopDuplication,
                SourceRect = new ScreenRect(relative.Left, relative.Top, relative.Width, relative.Height),
                IsCursorCaptureEnabled = request.IncludeCursor
            };
        }

        var primary = DisplayRecordingSource.MainMonitor
            ?? throw new InvalidOperationException("没有检测到主显示器。");
        primary.RecorderApi = RecorderApi.DesktopDuplication;
        primary.IsCursorCaptureEnabled = request.IncludeCursor;
        return primary;
    }

    private void ReleaseRecorder()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        recorder?.Dispose();
    }

    public void Dispose()
    {
        if (_recorder is not null)
        {
            _recorder.Stop();
            ReleaseRecorder();
        }
    }
}

namespace WinCapture;

public sealed class AppSettings
{
    public string ImageDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinCapture");

    public string VideoDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "WinCapture");

    public int CaptureDelaySeconds { get; set; }
    public bool IncludeCursorInScreenshots { get; set; }
    public bool IncludeCursorInRecordings { get; set; } = true;
    public bool ShowMouseClicks { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public int FrameRate { get; set; } = 30;
    public int VideoBitrateMbps { get; set; } = 8;
    public int LongShotMaxFrames { get; set; } = 30;
    public bool StartWithWindows { get; set; }
}

public sealed record HistoryItem(string FilePath, string FileName, DateTime CreatedAt, string Kind)
{
    public string DisplayTime => CreatedAt.ToString("MM-dd HH:mm");
}

public enum CaptureKind
{
    Region,
    Window,
    FullScreen
}

public enum EditorTool
{
    None,
    Pen,
    Highlighter,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Number,
    Mosaic,
    Blur,
    Redact,
    Crop
}

public enum RecordingTarget
{
    PrimaryScreen,
    Region,
    Window
}

public sealed record RecordingRequest(
    RecordingTarget Target,
    bool SystemAudio,
    bool Microphone,
    bool IncludeCursor,
    bool ShowClicks,
    int FrameRate,
    int BitrateMbps);

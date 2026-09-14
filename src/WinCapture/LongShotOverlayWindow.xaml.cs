using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinCapture.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace WinCapture;

/// <summary>
/// 长截图过程中显示的悬浮面板：实时预览拼接结果，并提供完成 / 撤销 / 取消入口。
/// 面板本身不会被截进长图，也不会抢走目标窗口的焦点。
/// </summary>
public partial class LongShotOverlayWindow : Window
{
    private const int HotKeyFinish = 0x5101;
    private const int HotKeyCancel = 0x5102;
    private const uint VkReturn = 0x0D;
    private const uint VkEscape = 0x1B;
    private const int PreviewWidth = 240;
    private const int PreviewRefreshStep = 120;
    private const int EdgeGap = 12;

    private readonly LongShotSession _session;
    private readonly DrawingRectangle _region;
    private GlobalHotKeyService? _hotKeys;
    private Bitmap? _preview;
    private int _previewHeight;

    public LongShotOverlayWindow(LongShotSession session, DrawingRectangle region)
    {
        InitializeComponent();
        _session = session;
        _region = region;
        HintText.Text = session.Mode == LongShotMode.Auto
            ? "程序会自动向下滚动，请保持目标窗口可见。"
            : "把鼠标移到目标内容上滚动，程序会实时拼接。到位后点『完成』。";
        Loaded += Overlay_Loaded;
        Closed += Overlay_Closed;
        _session.Updated += Session_Updated;
        Refresh(force: true);
    }

    private void Overlay_Loaded(object? sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        PositionWindow(handle);

        _hotKeys = new GlobalHotKeyService(handle);
        if (!_hotKeys.Register(HotKeyFinish, 0, VkReturn, () => _session.RequestFinish()))
        {
            HintText.Text += "（Enter 被其他程序占用，请点击按钮结束）";
        }

        _hotKeys.Register(HotKeyCancel, 0, VkEscape, () => _session.RequestCancel());
    }

    private void Overlay_Closed(object? sender, EventArgs e)
    {
        _session.Updated -= Session_Updated;
        _hotKeys?.Dispose();
        _hotKeys = null;
        _preview?.Dispose();
        _preview = null;
    }

    private void Session_Updated() => Refresh();

    private void FinishButton_Click(object sender, RoutedEventArgs e) => _session.RequestFinish();

    private void UndoButton_Click(object sender, RoutedEventArgs e) => _session.RequestUndo();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _session.RequestCancel();

    private void Refresh(bool force = false)
    {
        var stitcher = _session.Stitcher;
        if (stitcher is null)
        {
            CounterText.Text = "准备中";
            StatusText.Text = _session.Status;
            return;
        }

        CounterText.Text = $"{stitcher.Segments} 段 · {stitcher.TotalHeight:N0} px";
        StatusText.Text = _session.Status;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(_session.HasWarning ? "SignalBrush" : "InkBrush");

        if (!force && _preview is not null &&
            Math.Abs(stitcher.TotalHeight - _previewHeight) < PreviewRefreshStep)
        {
            return;
        }

        var next = stitcher.BuildPreview(PreviewWidth);
        PreviewImage.Source = ScreenCaptureService.ToBitmapSource(next);
        _preview?.Dispose();
        _preview = next;
        _previewHeight = stitcher.TotalHeight;
    }

    /// <summary>优先把面板放在选区外面；实在放不下再压到选区右上角（不会被截入画面）。</summary>
    private void PositionWindow(nint handle)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var screen = Forms.Screen.FromRectangle(_region).Bounds;

        var x = _region.Right + EdgeGap;
        var y = _region.Top;
        if (x + width > screen.Right)
        {
            x = _region.Left - EdgeGap - width;
        }

        if (x < screen.Left)
        {
            x = _region.Right - width - EdgeGap;
            y = _region.Top + EdgeGap;
        }

        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width));
        if (y + height > screen.Bottom)
        {
            y = screen.Bottom - height - EdgeGap;
        }

        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        NativeMethods.MoveWindow(handle, x, y);
    }
}

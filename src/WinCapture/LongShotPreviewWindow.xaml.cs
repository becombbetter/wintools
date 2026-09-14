using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WinCapture.Services;
using WpfMessageBox = System.Windows.MessageBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace WinCapture;

/// <summary>
/// 长截图完成后的预览窗口。
///
/// 长截图动辄上万像素高，直接塞进编辑器不便于整体确认，所以先在这里看：
/// 可以滚动、拖拽平移，并用缩放百分比控制大小。
/// 缩放支持：加减按钮（按档位跳）、预设档位（适应宽度 / 50% / 100% / 200%）、
/// Ctrl + 滚轮（以鼠标位置为锚点）、Ctrl + 0/1 快捷键、双击在「适应宽度 / 100%」间切换。
/// </summary>
public partial class LongShotPreviewWindow : Window
{
    /// <summary>加减与滚轮缩放走的档位，避免出现 137% 这种没有意义的数值。</summary>
    private static readonly double[] ZoomLadder =
    {
        0.05, 0.1, 0.15, 0.25, 0.33, 0.5, 0.67, 0.75, 1.0,
        1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0
    };

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    private readonly Bitmap _bitmap;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private double _zoom = 1.0;

    private bool _panning;
    private WpfPoint _panStart;
    private double _panOriginX;
    private double _panOriginY;

    public LongShotPreviewWindow(Bitmap bitmap, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        InitializeComponent();
        _bitmap = bitmap;
        _pixelWidth = bitmap.Width;
        _pixelHeight = bitmap.Height;

        PreviewImage.Source = ScreenCaptureService.ToBitmapSource(bitmap);
        SizeText.Text = $"{_pixelWidth:N0} × {_pixelHeight:N0} px";
        StatusText.Text = string.IsNullOrWhiteSpace(detail) ? "长截图预览" : detail;

        Loaded += Preview_Loaded;
        Closed += (_, _) => _bitmap.Dispose();
    }

    /// <summary>实际屏幕缩放（150% 缩放时是 1.5）。「100%」= 一个图像像素对一个物理像素。</summary>
    private double DeviceScaleX => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private double DeviceScaleY => VisualTreeHelper.GetDpi(this).DpiScaleY;

    private void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        // 默认「适应宽度」：长图先整幅横向看全
        ZoomToFitWidth();
        PreviewScroll.Focus();
    }

    // ---------------- 缩放 ----------------

    /// <summary>
    /// 改变缩放。anchor 为 ScrollViewer 坐标系里的锚点（一般是鼠标位置），
    /// 缩放后该点下方的图像内容保持不动；传 null 则以视口中心为锚点。
    /// </summary>
    private void SetZoom(double zoom, WpfPoint? anchor = null)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var scaleX = DeviceScaleX;
        var scaleY = DeviceScaleY;

        var point = anchor ?? new WpfPoint(
            PreviewScroll.ViewportWidth / 2,
            PreviewScroll.ViewportHeight / 2);

        // 锚点下方对应的图像像素坐标，缩放前后应当一致
        var imageX = (PreviewScroll.HorizontalOffset + point.X) * scaleX / _zoom;
        var imageY = (PreviewScroll.VerticalOffset + point.Y) * scaleY / _zoom;

        _zoom = zoom;
        ApplyImageSize();
        PreviewScroll.UpdateLayout();
        PreviewScroll.ScrollToHorizontalOffset(imageX * _zoom / scaleX - point.X);
        PreviewScroll.ScrollToVerticalOffset(imageY * _zoom / scaleY - point.Y);
        UpdateZoomText();
    }

    private void ApplyImageSize()
    {
        var scaleX = DeviceScaleX;
        var scaleY = DeviceScaleY;
        // 「100%」= 图像 1 像素对应 1 个物理像素：DIP 尺寸 = 像素数 × 缩放 ÷ 屏幕缩放
        PreviewImage.Width = Math.Max(1, _pixelWidth * _zoom / scaleX);
        PreviewImage.Height = Math.Max(1, _pixelHeight * _zoom / scaleY);

        // 放得很大时改用最近邻，方便逐像素看清楚；缩小时用双线性，平滑且够快
        RenderOptions.SetBitmapScalingMode(
            PreviewImage,
            _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Fant);
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        SizeText.Text = $"{_pixelWidth:N0} × {_pixelHeight:N0} px";
    }

    /// <summary>按档位放大 / 缩小一级。</summary>
    private void StepZoom(int direction, WpfPoint? anchor = null)
    {
        if (direction > 0)
        {
            foreach (var level in ZoomLadder)
            {
                if (level > _zoom + 1e-6)
                {
                    SetZoom(level, anchor);
                    return;
                }
            }

            SetZoom(MaxZoom, anchor);
            return;
        }

        for (var index = ZoomLadder.Length - 1; index >= 0; index--)
        {
            if (ZoomLadder[index] < _zoom - 1e-6)
            {
                SetZoom(ZoomLadder[index], anchor);
                return;
            }
        }

        SetZoom(MinZoom, anchor);
    }

    /// <summary>适应宽度：整幅长图横向铺满视口（纵向保持当前位置附近的锚点）。</summary>
    private void ZoomToFitWidth()
    {
        var viewport = PreviewScroll.ViewportWidth;
        if (viewport <= 0)
        {
            viewport = Math.Max(1, PreviewScroll.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        }

        if (viewport <= 0 || _pixelWidth <= 0)
        {
            return;
        }

        SetZoom(viewport * DeviceScaleX / _pixelWidth);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => StepZoom(1);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void ZoomFitWidthButton_Click(object sender, RoutedEventArgs e) => ZoomToFitWidth();

    private void ZoomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string raw } &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var zoom))
        {
            SetZoom(zoom);
        }
    }

    private void PreviewScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl + 滚轮：以鼠标位置为锚点缩放
            e.Handled = true;
            StepZoom(e.Delta > 0 ? 1 : -1, e.GetPosition(PreviewScroll));
            return;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift + 滚轮：横向滚动
            e.Handled = true;
            PreviewScroll.ScrollToHorizontalOffset(PreviewScroll.HorizontalOffset - e.Delta);
        }
    }

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        base.OnKeyDown(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:
                    StepZoom(1);
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    StepZoom(-1);
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    ZoomToFitWidth();
                    e.Handled = true;
                    return;
                case Key.D1:
                case Key.NumPad1:
                    SetZoom(1.0);
                    e.Handled = true;
                    return;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // ---------------- 拖拽平移 ----------------

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击：在「适应宽度」与「100%」之间切换
            var fit = PreviewScroll.ViewportWidth * DeviceScaleX / Math.Max(1, _pixelWidth);
            SetZoom(Math.Abs(_zoom - 1.0) < 0.01 ? fit : 1.0, e.GetPosition(PreviewScroll));
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(PreviewScroll);
        _panOriginX = PreviewScroll.HorizontalOffset;
        _panOriginY = PreviewScroll.VerticalOffset;
        PreviewImage.CaptureMouse();
        PreviewImage.Cursor = System.Windows.Input.Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewImage_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var point = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_panOriginX - (point.X - _panStart.X));
        PreviewScroll.ScrollToVerticalOffset(_panOriginY - (point.Y - _panStart.Y));
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();

    private void EndPan()
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        PreviewImage.ReleaseMouseCapture();
        PreviewImage.Cursor = null;
    }

    // ---------------- 动作 ----------------

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetImage(ScreenCaptureService.ToBitmapSource(_bitmap));
            StatusText.Text = "已复制到剪贴板";
        }
        catch (Exception exception)
        {
            // 剪贴板可能被其他程序占用，失败时提示而不是崩掉
            WpfMessageBox.Show(this, $"复制到剪贴板失败：{exception.Message}", "复制",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "保存长截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|PDF 文档|*.pdf",
            FileName = Path.GetFileName(App.History.NewImagePath("长截图")),
            InitialDirectory = App.Settings.ImageDirectory,
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var extension = Path.GetExtension(dialog.FileName);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                PdfExportService.Save(_bitmap, dialog.FileName);
            }
            else
            {
                var format = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Jpeg
                    : ImageFormat.Png;
                _bitmap.Save(dialog.FileName, format);
            }

            StatusText.Text = $"已保存：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, $"保存失败：{exception.Message}", "保存长截图",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在识别文字…";
        try
        {
            var text = await OcrService.RecognizeAsync(_bitmap);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "未识别到文字";
                return;
            }

            var copied = true;
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch
            {
                // 剪贴板被占用时不影响查看识别结果
                copied = false;
            }

            var resultWindow = new OcrResultWindow(text) { Owner = this };
            resultWindow.ShowDialog();
            StatusText.Text = copied ? "识别结果已复制到剪贴板" : "识别完成，但复制到剪贴板失败";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "OCR 识别失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "OCR 识别失败";
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var pinned = new PinWindow(ScreenCaptureService.CloneBitmap(_bitmap));
        pinned.Show();
        StatusText.Text = "长截图已置顶";
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        // 编辑器会接管传进去的位图，这里给它一份拷贝，预览窗口可以继续开着
        var editor = new EditorWindow(ScreenCaptureService.CloneBitmap(_bitmap));
        editor.Show();
        StatusText.Text = "已在编辑器中打开（本窗口可以继续对照）";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

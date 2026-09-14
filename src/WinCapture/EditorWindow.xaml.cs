using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using Microsoft.Win32;
using WinCapture.Controls;
using WinCapture.Services;
using WpfMessageBox = System.Windows.MessageBox;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace WinCapture;

public partial class EditorWindow : Window
{
    private readonly ImageEditorSurface _surface;

    public EditorWindow(Bitmap bitmap)
    {
        InitializeComponent();
        _surface = new ImageEditorSurface(bitmap);
        _surface.Tool = EditorTool.Arrow;
        _surface.TextRequested += Surface_TextRequested;
        _surface.Changed += RefreshState;
        EditorHost.Children.Add(_surface);
        bitmap.Dispose();
        RefreshState();
        Closed += (_, _) => _surface.Dispose();
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string toolName } && Enum.TryParse<EditorTool>(toolName, out var tool))
        {
            _surface.Tool = tool;
            StatusText.Text = $"当前工具：{GetToolName(tool)}";
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => _surface.Undo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => _surface.Redo();

    private void Surface_TextRequested(System.Drawing.Point point)
    {
        var dialog = new TextInputWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _surface.ApplyText(point, dialog.Value);
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetImage(ScreenCaptureService.ToBitmapSource(_surface.Bitmap));
            StatusText.Text = "已复制到剪贴板";
        }
        catch (Exception exception)
        {
            // 剪贴板可能被其他程序占用，失败时提示而不是让程序崩掉
            WpfMessageBox.Show(this, $"复制到剪贴板失败：{exception.Message}", "复制", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfSaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|PDF 文档|*.pdf",
            FileName = Path.GetFileName(App.History.NewImagePath()),
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
                PdfExportService.Save(_surface.Bitmap, dialog.FileName);
            }
            else
            {
                var format = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Jpeg
                    : ImageFormat.Png;
                _surface.Bitmap.Save(dialog.FileName, format);
            }

            StatusText.Text = $"已保存：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, $"保存失败：{exception.Message}", "保存截图", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在识别文字…";
        try
        {
            var text = await OcrService.RecognizeAsync(_surface.Bitmap);
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
        var pinned = new PinWindow(ScreenCaptureService.CloneBitmap(_surface.Bitmap));
        pinned.Show();
        StatusText.Text = "截图已置顶";
    }

    private void RefreshState()
    {
        UndoButton.IsEnabled = _surface.CanUndo;
        RedoButton.IsEnabled = _surface.CanRedo;
        StatusText.Text = $"{_surface.Bitmap.Width} × {_surface.Bitmap.Height} px";
    }

    private static string GetToolName(EditorTool tool) => tool switch
    {
        EditorTool.Pen => "画笔",
        EditorTool.Highlighter => "高亮",
        EditorTool.Arrow => "箭头",
        EditorTool.Rectangle => "矩形",
        EditorTool.Ellipse => "椭圆",
        EditorTool.Text => "文字",
        EditorTool.Number => "编号",
        EditorTool.Mosaic => "马赛克",
        EditorTool.Blur => "模糊",
        EditorTool.Redact => "遮挡",
        EditorTool.Crop => "裁剪",
        _ => "选择"
    };
}

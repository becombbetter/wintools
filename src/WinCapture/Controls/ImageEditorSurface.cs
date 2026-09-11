using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinCapture.Services;
using DrawingColor = System.Drawing.Color;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingFontStyle = System.Drawing.FontStyle;
using DrawingPen = System.Drawing.Pen;
using DrawingPoint = System.Drawing.Point;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;

namespace WinCapture.Controls;

public sealed class ImageEditorSurface : FrameworkElement, IDisposable
{
    private readonly Stack<Bitmap> _undo = new();
    private readonly Stack<Bitmap> _redo = new();
    private Bitmap _bitmap;
    private Bitmap? _operationStart;
    private DrawingPoint _start;
    private DrawingPoint _last;
    private bool _drawing;
    private int _sequenceNumber;

    public EditorTool Tool { get; set; }
    public DrawingColor StrokeColor { get; set; } = DrawingColor.FromArgb(233, 86, 69);
    public float StrokeWidth { get; set; } = 4;
    public event Action<DrawingPoint>? TextRequested;
    public event Action? Changed;

    public Bitmap Bitmap => _bitmap;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public ImageEditorSurface(Bitmap bitmap)
    {
        _bitmap = ScreenCaptureService.CloneBitmap(bitmap);
        Focusable = true;
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Cross;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var imageRect = GetImageRect();
        drawingContext.DrawImage(ScreenCaptureService.ToBitmapSource(_bitmap), imageRect);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = ToBitmapPoint(e.GetPosition(this));
        if (point is null)
        {
            return;
        }

        if (Tool == EditorTool.Text)
        {
            TextRequested?.Invoke(point.Value);
            return;
        }

        if (Tool == EditorTool.Number)
        {
            PushUndo();
            _sequenceNumber++;
            using var graphics = Graphics.FromImage(_bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(StrokeColor);
            graphics.FillEllipse(brush, point.Value.X - 15, point.Value.Y - 15, 30, 30);
            using var font = new Font("Segoe UI", 13, DrawingFontStyle.Bold, GraphicsUnit.Point);
            var text = _sequenceNumber.ToString();
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, DrawingBrushes.White, point.Value.X - size.Width / 2, point.Value.Y - size.Height / 2);
            NotifyChanged();
            return;
        }

        if (Tool == EditorTool.None)
        {
            return;
        }

        _drawing = true;
        _start = point.Value;
        _last = point.Value;
        _operationStart = ScreenCaptureService.CloneBitmap(_bitmap);
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drawing || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = ToBitmapPoint(e.GetPosition(this));
        if (point is null)
        {
            return;
        }

        if (Tool is EditorTool.Pen or EditorTool.Highlighter)
        {
            using var graphics = Graphics.FromImage(_bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Tool == EditorTool.Highlighter
                ? DrawingColor.FromArgb(92, 255, 211, 66)
                : StrokeColor;
            using var pen = new DrawingPen(color, Tool == EditorTool.Highlighter ? StrokeWidth * 3 : StrokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLine(pen, _last, point.Value);
            _last = point.Value;
            InvalidateVisual();
        }
        else if (Tool is EditorTool.Line or EditorTool.Arrow or EditorTool.Rectangle or EditorTool.Ellipse)
        {
            RestoreOperationStart();
            DrawShape(_start, point.Value);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_drawing)
        {
            return;
        }

        ReleaseMouseCapture();
        _drawing = false;
        var point = ToBitmapPoint(e.GetPosition(this)) ?? _last;

        if (Tool is EditorTool.Line or EditorTool.Arrow or EditorTool.Rectangle or EditorTool.Ellipse)
        {
            RestoreOperationStart();
            DrawShape(_start, point);
        }
        else if (Tool is EditorTool.Mosaic or EditorTool.Blur or EditorTool.Redact or EditorTool.Crop)
        {
            RestoreOperationStart();
            ApplyAreaTool(Normalize(_start, point));
        }

        if (_operationStart is not null)
        {
            _undo.Push(_operationStart);
            _operationStart = null;
            ClearStack(_redo);
        }

        NotifyChanged();
    }

    public void ApplyText(DrawingPoint point, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PushUndo();
        using var graphics = Graphics.FromImage(_bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Microsoft YaHei UI", 18, DrawingFontStyle.Bold, GraphicsUnit.Point);
        using var outline = new SolidBrush(DrawingColor.FromArgb(165, 255, 255, 255));
        using var brush = new SolidBrush(StrokeColor);
        graphics.DrawString(text, font, outline, point.X + 1, point.Y + 1);
        graphics.DrawString(text, font, brush, point.X, point.Y);
        NotifyChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(_bitmap);
        _bitmap = _undo.Pop();
        NotifyChanged();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(_bitmap);
        _bitmap = _redo.Pop();
        NotifyChanged();
    }

    private void DrawShape(DrawingPoint start, DrawingPoint end)
    {
        using var graphics = Graphics.FromImage(_bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new DrawingPen(StrokeColor, StrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = Tool == EditorTool.Arrow ? LineCap.ArrowAnchor : LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var rectangle = Normalize(start, end);

        switch (Tool)
        {
            case EditorTool.Line:
            case EditorTool.Arrow:
                graphics.DrawLine(pen, start, end);
                break;
            case EditorTool.Rectangle:
                graphics.DrawRectangle(pen, rectangle);
                break;
            case EditorTool.Ellipse:
                graphics.DrawEllipse(pen, rectangle);
                break;
        }
    }

    private void ApplyAreaTool(DrawingRectangle rectangle)
    {
        rectangle = DrawingRectangle.Intersect(rectangle, new DrawingRectangle(0, 0, _bitmap.Width, _bitmap.Height));
        if (rectangle.Width < 2 || rectangle.Height < 2)
        {
            return;
        }

        if (Tool == EditorTool.Crop)
        {
            var cropped = _bitmap.Clone(rectangle, DrawingPixelFormat.Format32bppPArgb);
            _bitmap.Dispose();
            _bitmap = cropped;
            return;
        }

        using var graphics = Graphics.FromImage(_bitmap);
        if (Tool == EditorTool.Redact)
        {
            using var brush = new SolidBrush(DrawingColor.FromArgb(28, 34, 37));
            graphics.FillRectangle(brush, rectangle);
            return;
        }

        var blockSize = Tool == EditorTool.Mosaic ? 14 : 5;
        var sampleWidth = Math.Max(1, rectangle.Width / blockSize);
        var sampleHeight = Math.Max(1, rectangle.Height / blockSize);
        using var sample = new Bitmap(sampleWidth, sampleHeight, DrawingPixelFormat.Format32bppPArgb);
        using (var sampleGraphics = Graphics.FromImage(sample))
        {
            sampleGraphics.InterpolationMode = Tool == EditorTool.Mosaic
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBilinear;
            sampleGraphics.DrawImage(_bitmap, new DrawingRectangle(0, 0, sample.Width, sample.Height), rectangle, GraphicsUnit.Pixel);
        }

        graphics.InterpolationMode = Tool == EditorTool.Mosaic
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(sample, rectangle);
    }

    private void PushUndo()
    {
        _undo.Push(ScreenCaptureService.CloneBitmap(_bitmap));
        ClearStack(_redo);
    }

    private void RestoreOperationStart()
    {
        if (_operationStart is null)
        {
            return;
        }

        _bitmap.Dispose();
        _bitmap = ScreenCaptureService.CloneBitmap(_operationStart);
    }

    private Rect GetImageRect()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(ActualWidth / _bitmap.Width, ActualHeight / _bitmap.Height);
        var width = _bitmap.Width * scale;
        var height = _bitmap.Height * scale;
        return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private DrawingPoint? ToBitmapPoint(WpfPoint point)
    {
        var imageRect = GetImageRect();
        if (imageRect.IsEmpty || !imageRect.Contains(point))
        {
            return null;
        }

        return new DrawingPoint(
            Math.Clamp((int)((point.X - imageRect.X) * _bitmap.Width / imageRect.Width), 0, _bitmap.Width - 1),
            Math.Clamp((int)((point.Y - imageRect.Y) * _bitmap.Height / imageRect.Height), 0, _bitmap.Height - 1));
    }

    private static DrawingRectangle Normalize(DrawingPoint first, DrawingPoint second)
    {
        return DrawingRectangle.FromLTRB(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Max(first.X, second.X),
            Math.Max(first.Y, second.Y));
    }

    private void NotifyChanged()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    private static void ClearStack(Stack<Bitmap> stack)
    {
        while (stack.TryPop(out var bitmap))
        {
            bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        _bitmap.Dispose();
        _operationStart?.Dispose();
        ClearStack(_undo);
        ClearStack(_redo);
    }
}

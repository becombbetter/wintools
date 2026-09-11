using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WinCapture.Services;

public sealed record SelectionResult(DrawingRectangle Bounds, nint WindowHandle);

public static class RegionSelector
{
    public static SelectionResult? Select(CaptureKind kind)
    {
        using var form = new SelectionForm(kind);
        return form.ShowDialog() == DialogResult.OK ? form.Result : null;
    }

    private sealed class SelectionForm : Form
    {
        private readonly CaptureKind _kind;
        private readonly Bitmap _desktop;
        private readonly DrawingRectangle _virtualBounds;
        private DrawingPoint _start;
        private DrawingPoint _current;
        private DrawingRectangle _selection;
        private bool _dragging;
        private nint _hoverWindow;

        public SelectionResult? Result { get; private set; }

        public SelectionForm(CaptureKind kind)
        {
            _kind = kind;
            _virtualBounds = SystemInformation.VirtualScreen;
            _desktop = ScreenCaptureService.Capture(_virtualBounds);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = _virtualBounds;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_kind == CaptureKind.Window)
            {
                if (_hoverWindow != nint.Zero && !_selection.IsEmpty)
                {
                    Result = new SelectionResult(ToGlobal(_selection), _hoverWindow);
                    DialogResult = DialogResult.OK;
                    Close();
                }

                return;
            }

            _dragging = true;
            _start = e.Location;
            _current = e.Location;
            _selection = DrawingRectangle.Empty;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_kind == CaptureKind.Window && !_dragging)
            {
                var globalPoint = new DrawingPoint(e.X + _virtualBounds.Left, e.Y + _virtualBounds.Top);
                var hit = NativeMethods.FindWindowAtPoint(globalPoint, Handle);
                _hoverWindow = hit.Handle;
                _selection = ToLocal(hit.Bounds);
                Invalidate();
                return;
            }

            if (!_dragging)
            {
                return;
            }

            _current = e.Location;
            _selection = Normalize(_start, _current);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging || e.Button != MouseButtons.Left)
            {
                return;
            }

            _dragging = false;
            _selection = Normalize(_start, e.Location);
            if (_selection.Width >= 4 && _selection.Height >= 4)
            {
                Result = new SelectionResult(ToGlobal(_selection), nint.Zero);
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else if (e.KeyCode == Keys.Enter && !_selection.IsEmpty)
            {
                Result = new SelectionResult(ToGlobal(_selection), _hoverWindow);
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.CompositingMode = CompositingMode.SourceOver;
            e.Graphics.DrawImageUnscaled(_desktop, 0, 0);
            using var dimBrush = new SolidBrush(Color.FromArgb(142, 10, 18, 21));
            e.Graphics.FillRectangle(dimBrush, ClientRectangle);

            if (!_selection.IsEmpty)
            {
                e.Graphics.DrawImage(_desktop, _selection, _selection, GraphicsUnit.Pixel);
                using var borderPen = new Pen(Color.FromArgb(255, 22, 207, 194), 2);
                e.Graphics.DrawRectangle(borderPen, _selection);

                var label = $"{_selection.Width} x {_selection.Height}";
                using var font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
                var size = e.Graphics.MeasureString(label, font);
                var labelRect = new DrawingRectangle(
                    _selection.Left,
                    Math.Max(0, _selection.Top - (int)size.Height - 10),
                    (int)size.Width + 14,
                    (int)size.Height + 6);
                using var labelBrush = new SolidBrush(Color.FromArgb(235, 19, 31, 36));
                e.Graphics.FillRectangle(labelBrush, labelRect);
                e.Graphics.DrawString(label, font, Brushes.White, labelRect.Left + 7, labelRect.Top + 3);
            }

            using var hintFont = new Font("Microsoft YaHei UI", 11, FontStyle.Regular, GraphicsUnit.Point);
            var hint = _kind == CaptureKind.Window ? "单击选择窗口  ·  Esc 取消" : "拖动选择区域  ·  右键或 Esc 取消";
            var hintSize = e.Graphics.MeasureString(hint, hintFont);
            var hintRect = new DrawingRectangle(
                (ClientSize.Width - (int)hintSize.Width) / 2 - 15,
                24,
                (int)hintSize.Width + 30,
                (int)hintSize.Height + 14);
            using var hintBrush = new SolidBrush(Color.FromArgb(225, 19, 31, 36));
            e.Graphics.FillRectangle(hintBrush, hintRect);
            e.Graphics.DrawString(hint, hintFont, Brushes.White, hintRect.Left + 15, hintRect.Top + 7);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _desktop.Dispose();
            }

            base.Dispose(disposing);
        }

        private DrawingRectangle ToGlobal(DrawingRectangle local)
        {
            local.Offset(_virtualBounds.Left, _virtualBounds.Top);
            return local;
        }

        private DrawingRectangle ToLocal(DrawingRectangle global)
        {
            global.Offset(-_virtualBounds.Left, -_virtualBounds.Top);
            return DrawingRectangle.Intersect(ClientRectangle, global);
        }

        private static DrawingRectangle Normalize(DrawingPoint first, DrawingPoint second)
        {
            return DrawingRectangle.FromLTRB(
                Math.Min(first.X, second.X),
                Math.Min(first.Y, second.Y),
                Math.Max(first.X, second.X),
                Math.Max(first.Y, second.Y));
        }
    }
}

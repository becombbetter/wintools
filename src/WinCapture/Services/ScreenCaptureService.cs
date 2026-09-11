using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace WinCapture.Services;

public static class ScreenCaptureService
{
    public static Bitmap CaptureVirtualScreen(bool includeCursor = false)
    {
        return Capture(System.Windows.Forms.SystemInformation.VirtualScreen, includeCursor);
    }

    public static Bitmap Capture(DrawingRectangle region, bool includeCursor = false)
    {
        if (region.Width < 1 || region.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);

        if (includeCursor)
        {
            var cursorPosition = System.Windows.Forms.Cursor.Position;
            if (region.Contains(cursorPosition))
            {
                var cursorBounds = new DrawingRectangle(
                    cursorPosition.X - region.Left,
                    cursorPosition.Y - region.Top,
                    32,
                    32);
                System.Windows.Forms.Cursors.Default.Draw(graphics, cursorBounds);
            }
        }

        return bitmap;
    }

    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    public static Bitmap CloneBitmap(Bitmap bitmap)
    {
        return bitmap.Clone(new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppPArgb);
    }

    public static byte[] ToPngBytes(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

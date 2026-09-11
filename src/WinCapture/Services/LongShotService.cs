using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WinCapture.Services;

public sealed record LongShotProgress(int FrameCount, int AppendedHeight, string Message);

public static class LongShotService
{
    public static async Task<Bitmap> CaptureAsync(
        DrawingRectangle region,
        int maximumFrames,
        IProgress<LongShotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<Bitmap>();
        Bitmap? previous = null;
        var unchangedCount = 0;
        var totalHeight = 0;

        try
        {
            NativeMethods.SetCursorPos(region.Left + region.Width / 2, region.Top + region.Height / 2);
            await Task.Delay(350, cancellationToken);

            previous = ScreenCaptureService.Capture(region);
            parts.Add(ScreenCaptureService.CloneBitmap(previous));
            totalHeight = previous.Height;
            progress?.Report(new LongShotProgress(1, totalHeight, "已捕获首屏"));

            for (var index = 1; index < maximumFrames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMethods.SendMouseWheel(-640);
                await Task.Delay(620, cancellationToken);

                var current = ScreenCaptureService.Capture(region);
                var match = FindOverlap(previous, current);
                if (match.Score > 45)
                {
                    current.Dispose();
                    progress?.Report(new LongShotProgress(index, totalHeight, "内容匹配结束"));
                    break;
                }

                if (match.Overlap >= current.Height * 0.9 && match.Score < 8)
                {
                    unchangedCount++;
                    current.Dispose();
                    if (unchangedCount >= 2)
                    {
                        progress?.Report(new LongShotProgress(index, totalHeight, "已到达页面底部"));
                        break;
                    }

                    continue;
                }

                unchangedCount = 0;
                var newHeight = current.Height - match.Overlap;
                if (newHeight < 8)
                {
                    current.Dispose();
                    continue;
                }

                var strip = current.Clone(
                    new DrawingRectangle(0, match.Overlap, current.Width, newHeight),
                    PixelFormat.Format32bppPArgb);
                parts.Add(strip);
                totalHeight += strip.Height;
                progress?.Report(new LongShotProgress(index + 1, totalHeight, $"已拼接 {index + 1} 屏"));

                previous.Dispose();
                previous = current;

                if (totalHeight >= 50000)
                {
                    progress?.Report(new LongShotProgress(index + 1, totalHeight, "已达到 50,000 像素高度上限"));
                    break;
                }
            }

            return Combine(parts, region.Width, totalHeight);
        }
        finally
        {
            previous?.Dispose();
            foreach (var part in parts)
            {
                part.Dispose();
            }
        }
    }

    private static (int Overlap, double Score) FindOverlap(Bitmap previous, Bitmap current)
    {
        var previousBuffer = ReadPixels(previous);
        var currentBuffer = ReadPixels(current);
        var height = Math.Min(previous.Height, current.Height);
        var width = Math.Min(previous.Width, current.Width);
        var minimumOverlap = Math.Max(30, (int)(height * 0.16));
        var maximumOverlap = Math.Max(minimumOverlap, (int)(height * 0.94));
        var xStart = width / 10;
        var xEnd = width - xStart;
        var xStep = Math.Max(8, width / 70);
        var bestOverlap = minimumOverlap;
        var bestScore = double.MaxValue;

        for (var overlap = minimumOverlap; overlap <= maximumOverlap; overlap += 3)
        {
            var yStep = Math.Max(5, overlap / 34);
            long difference = 0;
            long samples = 0;

            for (var y = 4; y < overlap - 4; y += yStep)
            {
                var previousY = previous.Height - overlap + y;
                var currentY = y;
                for (var x = xStart; x < xEnd; x += xStep)
                {
                    var previousIndex = previousY * previousBuffer.Stride + x * 4;
                    var currentIndex = currentY * currentBuffer.Stride + x * 4;
                    difference += Math.Abs(previousBuffer.Bytes[previousIndex] - currentBuffer.Bytes[currentIndex]);
                    difference += Math.Abs(previousBuffer.Bytes[previousIndex + 1] - currentBuffer.Bytes[currentIndex + 1]);
                    difference += Math.Abs(previousBuffer.Bytes[previousIndex + 2] - currentBuffer.Bytes[currentIndex + 2]);
                    samples += 3;
                }
            }

            if (samples == 0)
            {
                continue;
            }

            var score = difference / (double)samples;
            if (score < bestScore)
            {
                bestScore = score;
                bestOverlap = overlap;
            }
        }

        return (bestOverlap, bestScore);
    }

    private static PixelBuffer ReadPixels(Bitmap bitmap)
    {
        var rectangle = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new PixelBuffer(bytes, Math.Abs(data.Stride));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap Combine(IReadOnlyList<Bitmap> parts, int width, int totalHeight)
    {
        var result = new Bitmap(width, totalHeight, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        var y = 0;
        foreach (var part in parts)
        {
            graphics.DrawImageUnscaled(part, 0, y);
            y += part.Height;
        }

        return result;
    }

    private sealed record PixelBuffer(byte[] Bytes, int Stride);
}

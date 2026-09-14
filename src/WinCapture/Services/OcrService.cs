using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WinCapture.Services;

/// <summary>
/// 截图文字识别。
///
/// Windows 自带的 OCR 引擎对「文字偏小、背景偏暗、对比度低」的截图识别很差，
/// 直接把手里的位图原样丢进去会大量出错。这里在送进引擎前统一做四件事：
/// 1. 分辨率不足时按比例放大，让文字落到引擎擅长的尺寸；
/// 2. 拉伸对比度，救回发灰的截图；
/// 3. 深色背景（深色主题截图）自动反色，避免白字被当成背景；
/// 4. 超长截图分块识别，按纵坐标去掉重叠区域重复识别的行，既不漏字也不重字。
/// </summary>
public static class OcrService
{
    /// <summary>小图放大到大约这个宽度，保证文字足够大。</summary>
    private const int ComfortableWidth = 1600;

    /// <summary>放大上限，以及「本身已经够清晰、无需放大」的宽度。</summary>
    private const int MaxUpscale = 4;

    private const int WideEnoughWidth = 2400;

    /// <summary>放大后总像素的上限，避免超长图把内存吃光。</summary>
    private const long PixelBudget = 40_000_000;

    /// <summary>分块高度与相邻块重叠的高度，重叠用于避免正好切断一行文字。</summary>
    private const int TileHeight = 2200;

    private const int TileOverlap = 140;

    /// <summary>低于这个平均亮度就认为整图偏暗，做反色处理。</summary>
    private const int DarkBackgroundThreshold = 105;

    public static async Task<string> RecognizeAsync(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var engine = CreateEngine();

        // 先按原图分块再逐块增强：超长图不会产生一张巨大的中间位图。
        var tiles = SplitTiles(source);
        try
        {
            var lines = new List<string>();
            var coveredTop = double.NegativeInfinity;

            foreach (var (tile, offset) in tiles)
            {
                using var enhanced = Enhance(tile, out var scale);
                var recognized = await RecognizeTileAsync(engine, enhanced, scale, offset);

                // 相邻块有 TileOverlap 像素的重叠，重叠区里的行上一块已经输出过。
                // 这里只在「进入新块、还没输出本块第一行」时按纵坐标做去重；
                // 一旦输出过本块的行，就全部保留，避免同一块内并排的两列文字
                // 因为纵坐标相近而被误删。
                var accepting = false;
                foreach (var line in recognized)
                {
                    if (!accepting)
                    {
                        if (line.Top <= coveredTop + 0.5)
                        {
                            continue;
                        }

                        accepting = true;
                    }

                    lines.Add(line.Text);
                    if (line.Top > coveredTop)
                    {
                        coveredTop = line.Top;
                    }
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        finally
        {
            foreach (var (tile, _) in tiles)
            {
                tile.Dispose();
            }
        }
    }

    /// <summary>优先用用户配置的语言；没有就退到系统里任意一个可用语言包。</summary>
    private static OcrEngine CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            return engine;
        }

        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is not null)
            {
                return engine;
            }
        }

        throw new InvalidOperationException(
            "系统没有安装任何 OCR 语言包。请在「设置 › 时间和语言 › 语言和区域」中添加语言（勾选光学字符识别）后重试。");
    }

    private static async Task<List<(double Top, string Text)>> RecognizeTileAsync(
        OcrEngine engine,
        Bitmap tile,
        int scale,
        int offset)
    {
        using var stream = new MemoryStream();
        tile.Save(stream, ImageFormat.Bmp);
        var bytes = stream.ToArray();

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(softwareBitmap);

        var lines = new List<(double Top, string Text)>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            // 取该行所有单词里最靠上的纵坐标，再换算回原图坐标系
            var top = line.Words.Min(word => word.BoundingRect.Y);
            lines.Add((offset + top / scale, line.Text));
        }

        return lines;
    }

    /// <summary>放大 + 对比度拉伸 + 深色反色，得到更利于识别的位图。</summary>
    private static Bitmap Enhance(Bitmap source, out int scale)
    {
        // 常见的 1000~2400 宽截图文字偏小，统一放大 2 倍；
        // 更小的图放大到约 ComfortableWidth；已经很宽的图本身够清晰，不放大。
        var desired = 1;
        if (source.Width < ComfortableWidth)
        {
            desired = (int)Math.Ceiling(ComfortableWidth / (double)source.Width);
        }
        else if (source.Width < WideEnoughWidth)
        {
            desired = 2;
        }

        desired = Math.Clamp(desired, 1, MaxUpscale);

        // 同时受「像素预算」和「引擎单边上限」约束
        var pixels = (long)source.Width * source.Height;
        var byBudget = (int)Math.Sqrt(PixelBudget / (double)Math.Max(1, pixels));
        var byLimit = Math.Max(1, (int)OcrEngine.MaxImageDimension / Math.Max(1, source.Width));
        scale = Math.Clamp(Math.Min(desired, Math.Min(Math.Max(1, byBudget), byLimit)), 1, MaxUpscale);

        var width = Math.Max(1, source.Width * scale);
        var height = Math.Max(1, source.Height * scale);
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = scale > 1
                ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        NormalizeContrast(result);
        return result;
    }

    /// <summary>
    /// 按亮度直方图做彩色保持的对比度拉伸；整图偏暗时顺带反色。
    /// 三个通道套同一条查找表，颜色关系不会错乱。
    /// </summary>
    private static void NormalizeContrast(Bitmap bitmap)
    {
        var rectangle = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var length = data.Stride * data.Height;
            var buffer = new byte[length];
            Marshal.Copy(data.Scan0, buffer, 0, length);

            var histogram = new int[256];
            long total = 0;
            long lumaSum = 0;
            for (var index = 0; index + 3 < length; index += 4)
            {
                // 32bppPArgb 的内存顺序是 B、G、R
                var luma = (buffer[index] * 29 + buffer[index + 1] * 150 + buffer[index + 2] * 77) >> 8;
                histogram[luma]++;
                lumaSum += luma;
                total++;
            }

            if (total == 0)
            {
                return;
            }

            var low = Percentile(histogram, total, 0.02);
            var high = Percentile(histogram, total, 0.98);
            if (high - low < 16)
            {
                // 本来就接近纯色，拉伸只会放大噪声
                low = 0;
                high = 255;
            }

            var invert = lumaSum / total < DarkBackgroundThreshold;
            var lut = new byte[256];
            var span = high - low;
            for (var value = 0; value < 256; value++)
            {
                var mapped = span <= 0 ? value : (value - low) * 255 / span;
                mapped = Math.Clamp(mapped, 0, 255);
                lut[value] = (byte)(invert ? 255 - mapped : mapped);
            }

            for (var index = 0; index + 3 < length; index += 4)
            {
                buffer[index] = lut[buffer[index]];
                buffer[index + 1] = lut[buffer[index + 1]];
                buffer[index + 2] = lut[buffer[index + 2]];
            }

            Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static int Percentile(int[] histogram, long total, double fraction)
    {
        var target = (long)(total * fraction);
        long sum = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            sum += histogram[value];
            if (sum >= target)
            {
                return value;
            }
        }

        return 255;
    }

    /// <summary>超长图切成有重叠的横条，并记录每块在原图中的纵坐标偏移。</summary>
    private static List<(Bitmap Image, int Offset)> SplitTiles(Bitmap image)
    {
        var tiles = new List<(Bitmap Image, int Offset)>();
        if (image.Height <= TileHeight)
        {
            tiles.Add((image.Clone(
                new DrawingRectangle(0, 0, image.Width, image.Height),
                PixelFormat.Format32bppPArgb), 0));
            return tiles;
        }

        var y = 0;
        while (y < image.Height)
        {
            var height = Math.Min(TileHeight, image.Height - y);
            tiles.Add((image.Clone(
                new DrawingRectangle(0, y, image.Width, height),
                PixelFormat.Format32bppPArgb), y));

            if (y + height >= image.Height)
            {
                break;
            }

            y += TileHeight - TileOverlap;
        }

        return tiles;
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WinCapture.Services;

public enum StitchOutcome
{
    /// <summary>成功追加了新内容。</summary>
    Appended,

    /// <summary>画面没有实质变化，不需要拼接。</summary>
    Unchanged,

    /// <summary>用户往回滚动，画面内容已经在长图里了。</summary>
    ScrolledBack,

    /// <summary>找不到可靠的重叠位置，本帧不参与拼接。</summary>
    CannotAlign,

    /// <summary>达到高度上限。</summary>
    LimitReached
}

public sealed record StitchResult(
    StitchOutcome Outcome,
    int ScrollDelta,
    double Score,
    int Segments,
    int TotalHeight,
    string Message);

/// <summary>一帧画面的像素副本，附带逐行均值前缀和，用于快速粗筛对齐位置。</summary>
public readonly record struct FrameBuffer(byte[] Pixels, int Stride, int Width, int Height, double[] RowSums);

/// <summary>
/// 长截图拼接核心：把逐屏截图按重叠区域对齐后拼成一张长图。
/// 只负责几何匹配与像素拼接，不涉及滚动、按键和界面。
/// </summary>
public sealed class LongShotStitcher : IDisposable
{
    public const int DefaultMaxHeight = 50000;

    /// <summary>保留的参考帧数量。参考帧越多，能容忍的滚动跨度越大。</summary>
    private const int MaxAnchors = 10;

    /// <summary>参与匹配的参考帧数量，与保留数量一致，保证往回滚动也能被识别。</summary>
    private const int RecentAnchorCount = MaxAnchors;

    /// <summary>粗筛时按多少行取一次均值，下限与上限。</summary>
    private const int MinSignatureBandRows = 4;

    private const int MaxSignatureBandRows = 24;

    /// <summary>参与逐像素精修的候选区间数量。</summary>
    private const int BasinCount = 3;

    /// <summary>两个候选相差多少像素才视为"不同位置"。</summary>
    private const int DistinctTopWindow = 12;

    /// <summary>小于这个高度的增量不值得单独拼一段。</summary>
    private const int MinSegmentHeight = 6;

    /// <summary>可以接受的平均通道误差。</summary>
    private const double AcceptScore = 11.0;

    /// <summary>最优解必须比次优解好这么多，否则视为重复内容、不可靠。</summary>
    private const double AmbiguityRatio = 0.72;

    /// <summary>判定固定元素时允许的通道误差。</summary>
    private const double BandDifferenceLimit = 2.5;

    /// <summary>画面中段至少要变化这么多，才说明这一屏滚动过。</summary>
    private const double BandScrollDifference = 6.0;

    /// <summary>固定元素必须有纹理，纯色区域不算（否则白底会被误当成吸底栏）。</summary>
    private const double BandStructureDeviation = 8.0;

    /// <summary>固定元素最多占视口高度的比例。</summary>
    private const double MaxBandRatio = 0.12;

    private readonly List<Bitmap> _segments = new();
    private readonly List<Anchor> _anchors = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _maxHeight;
    private readonly int _minimumSampleRows;
    private readonly int _sampleLeft;
    private readonly int _sampleRight;
    private readonly int _signatureBandRows;

    private FrameBuffer? _previous;
    private int _topBand;
    private int _bottomBand;
    private int _bandMisses;
    private bool _disposed;

    public LongShotStitcher(Bitmap firstFrame, int maxHeight = DefaultMaxHeight)
    {
        ArgumentNullException.ThrowIfNull(firstFrame);
        if (firstFrame.Width < 24 || firstFrame.Height < 24)
        {
            throw new ArgumentException("长截图区域太小，请选择更大的范围。", nameof(firstFrame));
        }

        _width = firstFrame.Width;
        _height = firstFrame.Height;
        _maxHeight = Math.Max(_height, maxHeight);
        _minimumSampleRows = Math.Max(24, _height / 12);
        _signatureBandRows = Math.Clamp(_height / 50, MinSignatureBandRows, MaxSignatureBandRows);

        _sampleLeft = Math.Max(2, _width / 12);
        _sampleRight = _width - Math.Max(4, _width / 10); // 右边多留一点，避开滚动条
        if (_sampleRight - _sampleLeft < 32)
        {
            _sampleLeft = 0;
            _sampleRight = _width;
        }

        var buffer = ReadFrame(firstFrame);
        _segments.Add(ScreenCaptureService.CloneBitmap(firstFrame));
        TotalHeight = firstFrame.Height;
        AddAnchor(buffer, 0);
        _previous = buffer;
    }

    /// <summary>已拼接的段数（含首屏）。</summary>
    public int Segments => _segments.Count;

    /// <summary>当前长图总高度。</summary>
    public int TotalHeight { get; private set; }

    public int Width => _width;

    public int Height => _height;

    /// <summary>检测到的吸顶元素高度。</summary>
    public int TopBand => _topBand;

    /// <summary>检测到的吸底固定栏高度。</summary>
    public int BottomBand => _bottomBand;

    public IReadOnlyList<Bitmap> SegmentsView => _segments;

    /// <summary>提交一帧画面，成功时把新增部分追加到长图末尾。</summary>
    public StitchResult Submit(Bitmap frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame.Width != _width || frame.Height != _height)
        {
            return Failure(StitchOutcome.CannotAlign, "捕获区域尺寸发生变化，无法继续拼接");
        }

        if (TotalHeight >= _maxHeight)
        {
            return new StitchResult(
                StitchOutcome.LimitReached, 0, 0, _segments.Count, TotalHeight,
                $"已达到 {_maxHeight:N0} px 高度上限，长截图到此结束");
        }

        var buffer = ReadFrame(frame);

        // 先识别吸顶/吸底固定元素，让它们从一开始就不干扰对齐
        UpdateBands(buffer, _previous);

        var alignment = FindBestAlignment(buffer);
        if (alignment is null)
        {
            _previous = buffer;
            return Failure(StitchOutcome.CannotAlign, "没有找到与已拼接内容重叠的部分，请往回滚一点再继续");
        }

        var match = alignment.Value;
        var delta = match.Top - _anchors[^1].Top;

        if (match.Top < 0)
        {
            _previous = buffer;
            return Failure(StitchOutcome.CannotAlign, "已经滚到起点之上，请往回滚到起始位置");
        }

        if (!IsReliable(match))
        {
            _previous = buffer;
            return new StitchResult(
                StitchOutcome.CannotAlign, delta, match.Score, _segments.Count, TotalHeight,
                match.Score <= AcceptScore
                    ? "画面重复内容较多，无法可靠对齐，请继续向下滚动"
                    : "滚动幅度过大或画面变化太快，请慢一点、幅度小一点");
        }

        // 先判断吸顶/吸底固定元素，再决定这一屏能拼多少像素
        UpdateBands(buffer, _previous);

        // 首屏还没有拼第二段时，先把固定底栏裁掉，避免它出现在长图中间
        if (_segments.Count == 1 && _bottomBand > 0)
        {
            TrimFirstSegment(_bottomBand);
        }

        var usableEnd = _height - _bottomBand;
        var rowStart = TotalHeight - match.Top;
        var added = match.Top + usableEnd - TotalHeight;

        if (delta == 0 && _anchors.Count == 1)
        {
            _previous = buffer;
            return new StitchResult(
                StitchOutcome.Unchanged, 0, match.Score, _segments.Count, TotalHeight,
                "画面尚未滚动，请向下滚动目标内容");
        }

        if (added <= 0)
        {
            _previous = buffer;
            return new StitchResult(
                StitchOutcome.ScrolledBack, delta, match.Score, _segments.Count, TotalHeight,
                "已回退到已拼接的内容，未新增像素");
        }

        if (added < MinSegmentHeight || rowStart < _topBand)
        {
            _previous = buffer;
            return new StitchResult(
                StitchOutcome.Unchanged, delta, match.Score, _segments.Count, TotalHeight,
                "变化幅度太小，请继续向下滚动");
        }

        var strip = frame.Clone(
            new DrawingRectangle(0, rowStart, _width, added),
            PixelFormat.Format32bppPArgb);
        _segments.Add(strip);
        TotalHeight = match.Top + usableEnd;
        AddAnchor(buffer, match.Top);
        _previous = buffer;

        return new StitchResult(
            StitchOutcome.Appended, delta, match.Score, _segments.Count, TotalHeight,
            $"已拼接 {_segments.Count} 段");
    }

    /// <summary>撤销最后一段（首屏不可撤销）。</summary>
    public bool Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_segments.Count <= 1)
        {
            return false;
        }

        var last = _segments[^1];
        _segments.RemoveAt(_segments.Count - 1);
        TotalHeight -= last.Height;
        last.Dispose();

        // 落在裁剪范围之外的参考帧不再有意义
        _anchors.RemoveAll(anchor => anchor.Top >= TotalHeight);
        return true;
    }

    /// <summary>合成最终长图，调用方负责释放。</summary>
    public Bitmap Build()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Bitmap(_width, TotalHeight, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        var y = 0;
        foreach (var segment in _segments)
        {
            graphics.DrawImageUnscaled(segment, 0, y);
            y += segment.Height;
        }

        return result;
    }

    /// <summary>生成按比例缩小的预览图，用于悬浮面板实时显示。</summary>
    public Bitmap BuildPreview(int targetWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        targetWidth = Math.Clamp(targetWidth, 24, Math.Max(24, _width));
        var scale = targetWidth / (double)_width;
        var rows = new List<int>();
        var previewHeight = 0;
        foreach (var segment in _segments)
        {
            var height = Math.Max(1, (int)Math.Round(segment.Height * scale));
            rows.Add(height);
            previewHeight += height;
        }

        var preview = new Bitmap(targetWidth, Math.Max(1, previewHeight), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(preview);
        graphics.InterpolationMode = InterpolationMode.Low;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(Color.White);
        var y = 0;
        for (var index = 0; index < _segments.Count; index++)
        {
            graphics.DrawImage(_segments[index], new DrawingRectangle(0, y, targetWidth, rows[index]));
            y += rows[index];
        }

        return preview;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var segment in _segments)
        {
            segment.Dispose();
        }

        _segments.Clear();
        _anchors.Clear();
        _previous = null;
    }

    /// <summary>读取一帧的像素副本，并计算逐行均值前缀和用于粗筛。</summary>
    public static FrameBuffer ReadFrame(Bitmap bitmap)
    {
        var rectangle = new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return new FrameBuffer(
                pixels,
                stride,
                bitmap.Width,
                bitmap.Height,
                BuildRowSums(pixels, stride, bitmap.Width, bitmap.Height));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>两帧的采样平均差，用来判断画面是否已经停止滚动。</summary>
    public static double Difference(FrameBuffer first, FrameBuffer second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return double.MaxValue;
        }

        var xStep = Math.Max(3, first.Width / 70);
        long difference = 0;
        long samples = 0;
        for (var y = 0; y < first.Height; y += 3)
        {
            var firstOffset = y * first.Stride;
            var secondOffset = y * second.Stride;
            for (var x = 0; x < first.Width; x += xStep)
            {
                var firstIndex = firstOffset + x * 4;
                var secondIndex = secondOffset + x * 4;
                difference += Math.Abs(first.Pixels[firstIndex] - second.Pixels[secondIndex]);
                difference += Math.Abs(first.Pixels[firstIndex + 1] - second.Pixels[secondIndex + 1]);
                difference += Math.Abs(first.Pixels[firstIndex + 2] - second.Pixels[secondIndex + 2]);
                samples += 3;
            }
        }

        return samples == 0 ? double.MaxValue : difference / (double)samples;
    }

    /// <summary>
    /// 每行的采样均值按通道累加成前缀和，便于 O(1) 求任意行区间的均值。
    /// </summary>
    private static double[] BuildRowSums(byte[] pixels, int stride, int width, int height)
    {
        var sums = new double[3 * (height + 1)];
        var xStart = Math.Max(1, width / 16);
        var xEnd = width - Math.Max(1, width / 12);
        if (xEnd - xStart < 8)
        {
            xStart = 0;
            xEnd = width;
        }

        var xStep = Math.Max(1, (xEnd - xStart) / 120);
        var block = height + 1;
        for (var y = 0; y < height; y++)
        {
            var offset = y * stride;
            double blue = 0;
            double green = 0;
            double red = 0;
            long count = 0;
            for (var x = xStart; x < xEnd; x += xStep)
            {
                var index = offset + x * 4;
                blue += pixels[index];
                green += pixels[index + 1];
                red += pixels[index + 2];
                count++;
            }

            if (count == 0)
            {
                count = 1;
            }

            sums[y + 1] = sums[y] + blue / count;
            sums[block + y + 1] = sums[block + y] + green / count;
            sums[2 * block + y + 1] = sums[2 * block + y] + red / count;
        }

        return sums;
    }

    private StitchResult Failure(StitchOutcome outcome, string message)
    {
        return new StitchResult(outcome, 0, double.MaxValue, _segments.Count, TotalHeight, message);
    }

    private void AddAnchor(FrameBuffer buffer, int top)
    {
        _anchors.Add(new Anchor(buffer.Pixels, buffer.Stride, buffer.RowSums, top));
        while (_anchors.Count > MaxAnchors)
        {
            _anchors.RemoveAt(0);
        }
    }

    /// <summary>在新帧与历史参考帧之间寻找最佳垂直对齐位置。</summary>
    private Alignment? FindBestAlignment(FrameBuffer frame)
    {
        var limit = _height - _minimumSampleRows;
        if (limit <= 0 || _anchors.Count == 0)
        {
            return null;
        }

        // 第一阶段：逐行均值签名按 1 像素步长全量搜索。
        // 签名按若干行取均值，对 ±1~2 像素抖动不敏感，不会漏掉正确位置。
        var candidates = new List<Candidate>();
        for (var index = Math.Max(0, _anchors.Count - RecentAnchorCount); index < _anchors.Count; index++)
        {
            var anchor = _anchors[index];
            for (var delta = -limit; delta <= limit; delta++)
            {
                var score = SignatureDistance(frame, anchor, delta);
                if (!double.IsNaN(score))
                {
                    candidates.Add(new Candidate(anchor.Top + delta, score));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort((first, second) => first.Score.CompareTo(second.Score));

        // 第二阶段：取若干互不相邻的最优区间，用逐像素比较确认精确位置
        var basinGap = _signatureBandRows * 2;
        var basins = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (basins.All(item => Math.Abs(item.Top - candidate.Top) > basinGap))
            {
                basins.Add(candidate);
            }

            if (basins.Count >= BasinCount)
            {
                break;
            }
        }

        var window = _signatureBandRows + 2;
        var refined = new List<Candidate>();
        foreach (var basin in basins)
        {
            var exact = Refine(frame, basin.Top, window);
            if (exact is not null)
            {
                refined.Add(exact.Value);
            }
        }

        if (refined.Count == 0)
        {
            return null;
        }

        refined.Sort((first, second) => first.Score.CompareTo(second.Score));
        var winner = refined[0];
        var rival = double.MaxValue;
        foreach (var candidate in refined)
        {
            if (Math.Abs(candidate.Top - winner.Top) > DistinctTopWindow && candidate.Score < rival)
            {
                rival = candidate.Score;
            }
        }

        return new Alignment(winner.Top, winner.Score, rival);
    }

    /// <summary>
    /// 计算可比对的行区间：新帧第 r 行对应参考帧第 delta + r 行。
    /// 两帧的顶栏/底栏都是固定在屏幕上不随滚动移动的元素，落在这些行上的对应关系无效。
    /// </summary>
    private (int Low, int High) ComparisonRange(int delta)
    {
        var low = Math.Max(0, -delta);
        var high = Math.Min(_height, _height - delta);

        low = Math.Max(low, _topBand);
        low = Math.Max(low, _topBand - delta);
        high = Math.Min(high, _height - _bottomBand);
        high = Math.Min(high, _height - _bottomBand - delta);
        return (low, high);
    }

    /// <summary>
    /// 用逐行均值签名估算某个位移下的差异：按 _signatureBandRows 行取均值，
    /// 抵消亚像素抖动带来的噪声，用于全步长粗筛。
    /// </summary>
    private double SignatureDistance(FrameBuffer frame, Anchor anchor, int delta)
    {
        var (low, high) = ComparisonRange(delta);
        if (high - low < _minimumSampleRows)
        {
            return double.NaN;
        }

        var band = _signatureBandRows;
        var block = _height + 1;
        double difference = 0;
        long samples = 0;
        for (var y = low; y + band <= high; y += band)
        {
            var anchorY = y + delta;
            for (var channel = 0; channel < 3; channel++)
            {
                var offset = channel * block;
                var first = frame.RowSums[offset + y + band] - frame.RowSums[offset + y];
                var second = anchor.RowSums[offset + anchorY + band] - anchor.RowSums[offset + anchorY];
                difference += Math.Abs(first - second) / band;
                samples++;
            }
        }

        return samples == 0 ? double.NaN : difference / samples;
    }

    /// <summary>在候选区间内做逐像素比较，得到精确到 1 像素的对齐位置。</summary>
    private Candidate? Refine(FrameBuffer frame, int top, int window)
    {
        var xStep = Math.Max(4, _width / 80);
        var yStep = Math.Max(3, _minimumSampleRows / 40);
        Candidate? best = null;
        foreach (var anchor in _anchors)
        {
            var baseDelta = top - anchor.Top;
            for (var delta = baseDelta - window; delta <= baseDelta + window; delta++)
            {
                if (Math.Abs(delta) > _height - _minimumSampleRows)
                {
                    continue;
                }

                var score = Compare(frame, anchor, delta, xStep, yStep);
                if (double.IsNaN(score))
                {
                    continue;
                }

                if (best is null || score < best.Value.Score)
                {
                    best = new Candidate(anchor.Top + delta, score);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 比较新帧与参考帧在位移 delta 下的重合部分。
    /// 新帧第 r 行对应参考帧第 delta + r 行，返回平均通道误差，NaN 表示样本不足。
    /// </summary>
    private double Compare(FrameBuffer frame, Anchor anchor, int delta, int xStep, int yStep)
    {
        // 吸顶/吸底元素在两帧里都固定在屏幕位置上，不随滚动移动，
        // 必须同时排除新帧与参考帧各自的顶栏/底栏，否则固定元素会污染匹配分数。
        var (low, high) = ComparisonRange(delta);
        if (high - low < _minimumSampleRows)
        {
            return double.NaN;
        }

        long difference = 0;
        long samples = 0;
        for (var y = low; y < high; y += yStep)
        {
            var frameOffset = y * frame.Stride;
            var anchorOffset = (delta + y) * anchor.Stride;
            for (var x = _sampleLeft; x < _sampleRight; x += xStep)
            {
                var frameIndex = frameOffset + x * 4;
                var anchorIndex = anchorOffset + x * 4;
                difference += Math.Abs(frame.Pixels[frameIndex] - anchor.Pixels[anchorIndex]);
                difference += Math.Abs(frame.Pixels[frameIndex + 1] - anchor.Pixels[anchorIndex + 1]);
                difference += Math.Abs(frame.Pixels[frameIndex + 2] - anchor.Pixels[anchorIndex + 2]);
                samples += 3;
            }
        }

        return samples == 0 ? double.NaN : difference / (double)samples;
    }

    private static bool IsReliable(Alignment alignment)
    {
        if (alignment.Score > AcceptScore)
        {
            return false;
        }

        // 若另一个位置也匹配得很好，说明这段内容有重复，宁可不拼也不能拼错
        if (alignment.RivalScore <= AcceptScore && alignment.Score >= alignment.RivalScore * AmbiguityRatio)
        {
            return false;
        }

        return true;
    }

    /// <summary>识别吸顶导航、吸底工具栏这类固定在屏幕上不随滚动移动的元素。</summary>
    private void UpdateBands(FrameBuffer current, FrameBuffer? reference)
    {
        if (reference is not { } baseline)
        {
            return;
        }

        var maxBand = Math.Max(24, (int)(_height * MaxBandRatio));
        var step = Math.Max(2, maxBand / 20);
        var xStep = Math.Max(6, _width / 60);

        // 画面中段没变化说明这一屏没滚动，此时无从判断固定元素
        if (RegionDifference(current, baseline, _height / 4, _height * 3 / 4, xStep) < BandScrollDifference)
        {
            return;
        }

        var top = 0;
        while (top + step <= maxBand &&
               RegionDifference(current, baseline, top, top + step, xStep) < BandDifferenceLimit)
        {
            top += step;
        }

        if (top > 0)
        {
            _topBand = top;
        }

        var bottom = 0;
        while (bottom + step <= maxBand &&
               RegionDifference(current, baseline, _height - bottom - step, _height - bottom, xStep) < BandDifferenceLimit)
        {
            bottom += step;
        }

        if (bottom > 0 && HasStructure(current, _height - bottom, _height, xStep))
        {
            _bottomBand = bottom;
            _bandMisses = 0;
        }
        else if (_bottomBand > 0 && ++_bandMisses >= 3)
        {
            _bottomBand = 0;
            _bandMisses = 0;
        }
    }

    private void TrimFirstSegment(int band)
    {
        var first = _segments[0];
        var removed = Math.Min(band, first.Height - MinSegmentHeight);
        if (removed < MinSegmentHeight)
        {
            return;
        }

        var trimmed = first.Clone(
            new DrawingRectangle(0, 0, _width, first.Height - removed),
            PixelFormat.Format32bppPArgb);
        _segments[0] = trimmed;
        first.Dispose();
        TotalHeight -= removed;
    }

    /// <summary>两个同尺寸画面在指定行区间内的采样平均差。</summary>
    private double RegionDifference(FrameBuffer current, FrameBuffer reference, int yStart, int yEnd, int xStep)
    {
        yStart = Math.Max(0, yStart);
        yEnd = Math.Min(_height, yEnd);
        if (yEnd - yStart < 2)
        {
            return double.MaxValue;
        }

        long difference = 0;
        long samples = 0;
        for (var y = yStart; y < yEnd; y += 2)
        {
            var currentOffset = y * current.Stride;
            var referenceOffset = y * reference.Stride;
            for (var x = _sampleLeft; x < _sampleRight; x += xStep)
            {
                var currentIndex = currentOffset + x * 4;
                var referenceIndex = referenceOffset + x * 4;
                difference += Math.Abs(current.Pixels[currentIndex] - reference.Pixels[referenceIndex]);
                difference += Math.Abs(current.Pixels[currentIndex + 1] - reference.Pixels[referenceIndex + 1]);
                difference += Math.Abs(current.Pixels[currentIndex + 2] - reference.Pixels[referenceIndex + 2]);
                samples += 3;
            }
        }

        return samples == 0 ? double.MaxValue : difference / (double)samples;
    }

    /// <summary>判断一段画面是否有纹理，用来排除纯色背景造成的误判。</summary>
    private bool HasStructure(FrameBuffer frame, int yStart, int yEnd, int xStep)
    {
        yStart = Math.Max(0, yStart);
        yEnd = Math.Min(_height, yEnd);
        if (yEnd - yStart < 4)
        {
            return false;
        }

        long sum = 0;
        long sumSquares = 0;
        long count = 0;
        for (var y = yStart; y < yEnd; y += 2)
        {
            var offset = y * frame.Stride;
            for (var x = _sampleLeft; x < _sampleRight; x += xStep)
            {
                int value = frame.Pixels[offset + x * 4 + 1];
                sum += value;
                sumSquares += value * value;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        var mean = sum / (double)count;
        var variance = sumSquares / (double)count - mean * mean;
        return Math.Sqrt(Math.Max(0, variance)) >= BandStructureDeviation;
    }

    private sealed record Anchor(byte[] Pixels, int Stride, double[] RowSums, int Top);

    private readonly record struct Candidate(int Top, double Score);

    private readonly record struct Alignment(int Top, double Score, double RivalScore);
}

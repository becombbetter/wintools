using System.Drawing;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WinCapture.Services;

public enum LongShotMode
{
    /// <summary>用户自己滚动，程序只负责实时拼接。</summary>
    Manual,

    /// <summary>程序代替用户发滚轮，逐屏滚动并拼接。</summary>
    Auto
}

public sealed record LongShotOutcome(
    Bitmap? Image,
    int Segments,
    int TotalHeight,
    bool Canceled,
    string Message);

/// <summary>
/// 长截图会话：界面通过它下发"完成 / 撤销 / 取消"，服务通过它汇报状态。
/// </summary>
public sealed class LongShotSession : IDisposable
{
    private bool _finishRequested;
    private bool _cancelRequested;
    private bool _undoRequested;

    public LongShotSession(LongShotMode mode)
    {
        Mode = mode;
    }

    public LongShotMode Mode { get; }

    public LongShotStitcher? Stitcher { get; private set; }

    public string Status { get; private set; } = "准备中…";

    public bool HasWarning { get; private set; }

    public int Segments => Stitcher?.Segments ?? 0;

    public int TotalHeight => Stitcher?.TotalHeight ?? 0;

    /// <summary>状态或拼接进度变化时触发，供界面刷新。</summary>
    public event Action? Updated;

    public void RequestFinish() => _finishRequested = true;

    public void RequestCancel() => _cancelRequested = true;

    public void RequestUndo() => _undoRequested = true;

    internal bool FinishRequested => _finishRequested;

    internal bool CancelRequested => _cancelRequested;

    internal bool ConsumeUndo()
    {
        if (!_undoRequested)
        {
            return false;
        }

        _undoRequested = false;
        return true;
    }

    internal void Attach(LongShotStitcher stitcher) => Stitcher = stitcher;

    internal void Report(string status, bool warning = false)
    {
        Status = status;
        HasWarning = warning;
        Updated?.Invoke();
    }

    public void Dispose()
    {
        Stitcher?.Dispose();
        Stitcher = null;
    }
}

/// <summary>
/// 长截图流程：手动模式下用户滚动、程序实时拼接，随时可以结束或撤销；
/// 自动模式下由程序发送滚轮，并等待画面真正停止后再拼接。
/// </summary>
public static class LongShotService
{
    /// <summary>轮询间隔：越小越跟手，CPU 占用也越高。</summary>
    private const int PollIntervalMs = 120;

    /// <summary>等待画面静止的最长时间，超时后按当前画面尝试拼接。</summary>
    private const int SettleTimeoutMs = 1400;

    /// <summary>两次采样差异小于该值即视为画面已静止。</summary>
    private const double SettleDifference = 1.4;

    /// <summary>自动模式单次滚动的最大格数。</summary>
    private const int MaxAutoNotches = 10;

    private const int WheelNotch = 120;

    public static async Task<LongShotOutcome> CaptureAsync(
        DrawingRectangle region,
        LongShotSession session,
        int maxSegments,
        CancellationToken cancellationToken = default)
    {
        var cursorMoved = false;
        var cursorOrigin = default(NativeMethods.NativePoint);

        try
        {
            if (session.Mode == LongShotMode.Auto)
            {
                // 滚轮事件会发给鼠标位置下的窗口，先把指针移进区域并激活目标窗口
                cursorMoved = NativeMethods.TryGetCursorPosition(out cursorOrigin);
                var centerX = region.Left + region.Width / 2;
                var centerY = region.Top + region.Height / 2;
                NativeMethods.SetCursorPos(centerX, centerY);
                NativeMethods.ActivateWindowAt(centerX, centerY);
                await Task.Delay(320, cancellationToken);
            }
            else
            {
                await Task.Delay(160, cancellationToken);
            }

            using var firstFrame = ScreenCaptureService.Capture(region);
            var stitcher = new LongShotStitcher(firstFrame);
            session.Attach(stitcher);
            session.Report(session.Mode == LongShotMode.Auto
                ? "正在自动滚动拼接，随时可以点『完成』或按 Esc 结束"
                : "请滚动目标内容，程序会实时拼接");

            var wheelDelta = -480;
            var needScroll = session.Mode == LongShotMode.Auto;
            var unchangedStreak = 0;
            var failedStreak = 0;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested || session.CancelRequested)
                {
                    return Canceled(session);
                }

                if (session.FinishRequested)
                {
                    break;
                }

                if (stitcher.Segments >= maxSegments)
                {
                    session.Report($"已达到 {maxSegments} 段上限，自动结束");
                    break;
                }

                if (session.ConsumeUndo())
                {
                    session.Report(stitcher.Undo() ? "已撤销最后一段" : "只剩首屏，无法再撤销");
                }

                if (needScroll && session.Mode == LongShotMode.Auto)
                {
                    NativeMethods.SendMouseWheel(wheelDelta);
                    needScroll = false;
                }

                var frame = await WaitForSettledFrameAsync(region, session, cancellationToken);
                if (frame is null)
                {
                    if (cancellationToken.IsCancellationRequested || session.CancelRequested)
                    {
                        return Canceled(session);
                    }

                    // 用户按下了完成或撤销，交给下一轮循环处理
                    continue;
                }

                using (frame)
                {
                    var result = stitcher.Submit(frame);
                    switch (result.Outcome)
                    {
                        case StitchOutcome.Appended:
                            unchangedStreak = 0;
                            failedStreak = 0;
                            session.Report($"已拼接 {result.Segments} 段 · {result.TotalHeight:N0} px");
                            if (session.Mode == LongShotMode.Auto)
                            {
                                wheelDelta = AdaptWheelDelta(wheelDelta, result.ScrollDelta, region.Height);
                                needScroll = true;
                            }

                            break;

                        case StitchOutcome.LimitReached:
                            session.Report(result.Message);
                            return Finish(session);

                        case StitchOutcome.ScrolledBack:
                            unchangedStreak = 0;
                            session.Report(result.Message, true);
                            break;

                        case StitchOutcome.Unchanged:
                            session.Report(result.Message, session.Mode == LongShotMode.Auto);
                            if (session.Mode == LongShotMode.Auto)
                            {
                                unchangedStreak++;
                                if (unchangedStreak >= 2)
                                {
                                    session.Report("已到达页面底部，自动结束");
                                    return Finish(session);
                                }

                                needScroll = true;
                            }

                            break;

                        case StitchOutcome.CannotAlign:
                            session.Report(result.Message, true);
                            failedStreak++;
                            if (session.Mode == LongShotMode.Auto)
                            {
                                if (failedStreak >= 2)
                                {
                                    session.Report("连续两屏无法对齐，自动结束");
                                    return Finish(session);
                                }

                                needScroll = true;
                            }

                            break;
                    }
                }
            }

            return Finish(session);
        }
        finally
        {
            if (cursorMoved)
            {
                NativeMethods.SetCursorPos(cursorOrigin.X, cursorOrigin.Y);
            }
        }
    }

    /// <summary>等待画面停止滚动后返回该帧；返回 null 表示被取消或用户要求结束。</summary>
    private static async Task<Bitmap?> WaitForSettledFrameAsync(
        DrawingRectangle region,
        LongShotSession session,
        CancellationToken cancellationToken)
    {
        FrameBuffer? previous = null;
        var settled = false;
        var waited = 0;

        while (true)
        {
            var frame = ScreenCaptureService.Capture(region);
            var buffer = LongShotStitcher.ReadFrame(frame);
            if (previous is not null && LongShotStitcher.Difference(previous.Value, buffer) < SettleDifference)
            {
                settled = true;
            }

            previous = buffer;

            if (settled || waited >= SettleTimeoutMs)
            {
                // 页面有视频、闪烁光标这类持续动画时画面永远不会静止，超时后照常尝试拼接
                return frame;
            }

            frame.Dispose();
            waited += PollIntervalMs;

            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (cancellationToken.IsCancellationRequested || session.CancelRequested ||
                session.FinishRequested)
            {
                return null;
            }
        }
    }

    /// <summary>根据实际滚动距离调整滚轮幅度，让每屏保留约一半重叠。</summary>
    private static int AdaptWheelDelta(int current, int scrollDelta, int viewportHeight)
    {
        if (scrollDelta <= 0)
        {
            return Math.Clamp(current * 2, -MaxAutoNotches * WheelNotch, -WheelNotch);
        }

        var target = viewportHeight * 0.5;
        var scaled = current * (target / scrollDelta);
        var notches = Math.Clamp(
            (int)Math.Round(Math.Abs(scaled) / WheelNotch),
            1,
            MaxAutoNotches);
        return -notches * WheelNotch;
    }

    private static LongShotOutcome Finish(LongShotSession session)
    {
        var stitcher = session.Stitcher;
        if (stitcher is null)
        {
            return new LongShotOutcome(null, 0, 0, true, "长截图未开始");
        }

        var image = stitcher.Build();
        return new LongShotOutcome(
            image,
            stitcher.Segments,
            stitcher.TotalHeight,
            false,
            $"长截图完成：{stitcher.Segments} 段，{stitcher.Width} × {stitcher.TotalHeight} px");
    }

    private static LongShotOutcome Canceled(LongShotSession session)
    {
        session.Report("已取消长截图");
        return new LongShotOutcome(null, session.Segments, session.TotalHeight, true, "已取消长截图");
    }
}

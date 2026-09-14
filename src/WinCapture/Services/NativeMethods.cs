using System.Runtime.InteropServices;

namespace WinCapture.Services;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExToolWindow = 0x00000080;
    public const uint GaRoot = 2;
    public const uint DwmwaExtendedFrameBounds = 9;
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint InputMouse = 0;
    public const uint MouseEventWheel = 0x0800;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoActivate = 0x0010;

    public static readonly nint HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect rect, int size);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint window, char[] text, int count);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint handle);

    public static System.Drawing.Rectangle GetVisibleWindowBounds(nint window)
    {
        window = GetAncestor(window, GaRoot);
        if (window == nint.Zero || !IsWindowVisible(window))
        {
            return System.Drawing.Rectangle.Empty;
        }

        var result = DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeRect>());
        if (result != 0 && !GetWindowRect(window, out rect))
        {
            return System.Drawing.Rectangle.Empty;
        }

        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static string GetWindowTitle(nint window)
    {
        var buffer = new char[512];
        var length = GetWindowText(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static (nint Handle, System.Drawing.Rectangle Bounds) FindWindowAtPoint(
        System.Drawing.Point point,
        nint excludedWindow)
    {
        nint foundHandle = nint.Zero;
        var foundBounds = System.Drawing.Rectangle.Empty;

        EnumWindows((window, _) =>
        {
            if (window == excludedWindow || !IsWindowVisible(window))
            {
                return true;
            }

            var style = GetWindowLong(window, GwlExStyle);
            if ((style & WsExToolWindow) != 0)
            {
                return true;
            }

            var bounds = GetVisibleWindowBounds(window);
            if (!bounds.IsEmpty && bounds.Width > 30 && bounds.Height > 30 && bounds.Contains(point))
            {
                foundHandle = window;
                foundBounds = bounds;
                return false;
            }

            return true;
        }, nint.Zero);

        return (foundHandle, foundBounds);
    }

    /// <summary>激活指定坐标下最上层的窗口，保证滚轮事件发给正确的目标。</summary>
    public static void ActivateWindowAt(int x, int y)
    {
        var hit = FindWindowAtPoint(new System.Drawing.Point(x, y), nint.Zero);
        if (hit.Handle != nint.Zero)
        {
            SetForegroundWindow(hit.Handle);
        }
    }

    public static bool TryGetCursorPosition(out NativePoint point) => GetCursorPos(out point);

    /// <summary>用物理像素摆放窗口，避免多显示器缩放下 WPF 设备无关单位的换算误差。</summary>
    public static void MoveWindow(nint window, int x, int y)
    {
        SetWindowPos(window, HwndTopMost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    public static void SendMouseWheel(int delta)
    {
        var inputs = new[]
        {
            new Input
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        MouseData = unchecked((uint)delta),
                        Flags = MouseEventWheel
                    }
                }
            }
        };

        SendInput(1, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public delegate bool EnumWindowsProc(nint window, nint parameter);
}

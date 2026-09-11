using System.Windows.Interop;

namespace WinCapture.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();

    public GlobalHotKeyService(nint windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle);
        _source.AddHook(WndProc);
    }

    public bool Register(int id, uint modifiers, uint virtualKey, Action action)
    {
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _source.RemoveHook(WndProc);
        _actions.Clear();
    }
}

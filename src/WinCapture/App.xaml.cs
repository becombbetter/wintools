using System.Windows;
using WinCapture.Services;

namespace WinCapture;

public partial class App : System.Windows.Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static HistoryService History { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
        Settings = SettingsService.Load();
        History = new HistoryService(Settings);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}

using System.Text.Json;
using Microsoft.Win32;

namespace WinCapture.Services;

public static class SettingsService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinCapture";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinCapture",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
        if (settings.StartWithWindows)
        {
            key?.SetValue(StartupValueName, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }
}

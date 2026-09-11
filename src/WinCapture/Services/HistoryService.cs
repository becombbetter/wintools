namespace WinCapture.Services;

public sealed class HistoryService
{
    private readonly AppSettings _settings;

    public HistoryService(AppSettings settings)
    {
        _settings = settings;
        EnsureDirectories();
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_settings.ImageDirectory);
        Directory.CreateDirectory(_settings.VideoDirectory);
    }

    public IReadOnlyList<HistoryItem> GetRecent(int count = 30)
    {
        EnsureDirectories();
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".mp4", ".pdf"
        };

        return new[] { _settings.ImageDirectory, _settings.VideoDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory))
            .Where(path => supported.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTime)
            .Take(count)
            .Select(file => new HistoryItem(
                file.FullName,
                file.Name,
                file.CreationTime,
                file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "录屏" : "截图"))
            .ToList();
    }

    public string NewImagePath(string suffix = "截图", string extension = ".png")
    {
        EnsureDirectories();
        return Path.Combine(_settings.ImageDirectory, $"WinCapture_{suffix}_{DateTime.Now:yyyyMMdd_HHmmssfff}{extension}");
    }

    public string NewVideoPath()
    {
        EnsureDirectories();
        return Path.Combine(_settings.VideoDirectory, $"WinCapture_录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }
}

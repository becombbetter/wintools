namespace WinCapture.Services;

public sealed class HistoryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".mp4", ".pdf"
    };

    private readonly AppSettings _settings;

    public HistoryService(AppSettings settings)
    {
        _settings = settings;
        EnsureDirectories();
    }

    /// <summary>
    /// 创建保存目录。目录被清空或指向非法路径时不能抛异常，
    /// 否则设置里留空会让整个程序在保存后崩溃。
    /// </summary>
    public void EnsureDirectories()
    {
        TryCreateDirectory(_settings.ImageDirectory);
        TryCreateDirectory(_settings.VideoDirectory);
    }

    public IReadOnlyList<HistoryItem> GetRecent(int count = 30)
    {
        EnsureDirectories();

        var items = new List<HistoryItem>();
        foreach (var directory in ExistingDirectories())
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
            }
            catch
            {
                // 目录暂时不可访问（权限、网络盘掉线等）时跳过，不影响其他目录
                continue;
            }

            foreach (var path in files)
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                try
                {
                    var file = new FileInfo(path);
                    items.Add(new HistoryItem(
                        file.FullName,
                        file.Name,
                        file.CreationTime,
                        file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "录屏" : "截图"));
                }
                catch
                {
                    // 单个文件元数据异常时忽略该文件
                }
            }
        }

        return items
            .OrderByDescending(item => item.CreatedAt)
            .Take(count)
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

    private IEnumerable<string> ExistingDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { _settings.ImageDirectory, _settings.VideoDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !seen.Add(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static void TryCreateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
        }
    }
}

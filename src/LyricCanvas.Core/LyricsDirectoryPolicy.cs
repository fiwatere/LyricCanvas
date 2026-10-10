namespace LyricCanvas.Core;

/// <summary>LyricCanvas 的本地歌词搜索顺序。</summary>
public static class LyricsDirectoryPolicy
{
    /// <summary>
    /// 返回“自定义目录 → 程序目录/lyrics → 当前工作目录/lyrics”的去重绝对路径。
    /// 缓存仍以首选目录为根目录，单独存放在其 cache 子目录中。
    /// </summary>
    public static IReadOnlyList<string> GetSearchDirectories(
        string? configuredDirectory,
        string applicationBaseDirectory,
        string currentWorkingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentWorkingDirectory);

        var candidates = new[]
        {
            configuredDirectory,
            Path.Combine(applicationBaseDirectory, "lyrics"),
            Path.Combine(currentWorkingDirectory, "lyrics"),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(candidates.Length);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            if (seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }
}

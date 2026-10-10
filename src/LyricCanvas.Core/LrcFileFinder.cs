using System.Text;

namespace LyricCanvas.Core;

/// <summary>
/// 在目录里按曲名歌手找歌词文件。
///
/// 查找顺序与命名规则：
///   1. "曲名 - 歌手.lrc"（最精确，能区分同名不同歌手）
///   2. "曲名.lrc"
///   3. 忽略大小写再各试一次（Windows 文件系统本来不区分大小写，
///      但网络盘、WSL 挂载盘可能是区分的）
/// </summary>
public sealed class LrcFileFinder
{
    private readonly IReadOnlyList<string> _searchDirectories;

    public LrcFileFinder(params string?[] searchDirectories)
    {
        _searchDirectories = (searchDirectories ?? Array.Empty<string?>())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>实际会搜索哪些目录。</summary>
    public IReadOnlyList<string> SearchDirectories => _searchDirectories;

    /// <summary>按曲名和歌手找歌词文件。找不到返回 null。</summary>
    public string? Find(string title, string artist)
    {
        var safeTitle = SanitizeFileName(title);
        if (safeTitle.Length == 0)
        {
            return null;
        }

        var safeArtist = SanitizeFileName(artist);

        // 候选文件名，按优先级排列
        var names = new List<string>(4);
        if (safeArtist.Length > 0)
        {
            names.Add($"{safeTitle} - {safeArtist}");
        }

        names.Add(safeTitle);

        foreach (var directory in _searchDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in names)
            {
                var exact = Path.Combine(directory, name + ".lrc");
                if (File.Exists(exact))
                {
                    return exact;
                }
            }

            // 忽略大小写再扫一遍
            var match = FindIgnoringCase(directory, names);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindIgnoringCase(string directory, List<string> names)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*.lrc").ToList();

            foreach (var name in names)
            {
                var match = files.FirstOrDefault(f => string.Equals(
                    Path.GetFileNameWithoutExtension(f),
                    name,
                    StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    return match;
                }
            }
        }
        catch (IOException)
        {
            // 目录读不了就跳过
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// 把曲名变成安全的文件名。
    /// 曲名里出现 / \ : * ? " &lt; &gt; | 时替换掉，否则拼出来的路径是非法的。
    /// </summary>
    internal static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        // Windows 上文件名不能以点或空格结尾
        return builder.ToString().Trim().TrimEnd('.', ' ');
    }
}

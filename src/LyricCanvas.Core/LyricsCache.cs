using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricCanvas.Core;

/// <summary>
/// 自动获取歌词的独立缓存。
/// 用户歌词保存在歌词目录根部；缓存放在 cache 子目录。缓存文件名含稳定身份哈希，
/// 元数据再次核对曲名和歌手，避免清理非法字符或截断文件名造成不同曲目串用。
/// </summary>
public sealed class LyricsCache
{
    private const int CurrentFormatVersion = 2;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _cacheDirectory;

    private sealed record CacheMetadata(
        int FormatVersion,
        string TitleIdentity,
        string ArtistIdentity,
        string CacheKey,
        string LyricsSha256,
        string SourceName,
        bool NeedsConfirmation,
        bool IsConfirmed,
        string Message,
        string MatchedCandidateKey,
        string MatchedTitle,
        string MatchedArtist);

    public LyricsCache(string lyricsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lyricsDirectory);
        _cacheDirectory = Path.Combine(lyricsDirectory, "cache");
    }

    /// <summary>缓存目录的实际路径。</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>按曲名和歌手算出唯一的缓存文件路径。</summary>
    public string GetCachePath(string title, string artist)
        => Path.Combine(
            _cacheDirectory,
            BuildBaseName(NormalizeIdentityField(title), NormalizeIdentityField(artist)) + ".lrc");

    /// <summary>读取身份和内容都通过校验的缓存；旧版无身份元数据的文件会被忽略。</summary>
    public LyricsCacheEntry? Read(string title, string artist)
    {
        var path = GetCachePath(title, artist);
        if (!TryReadVerified(path, out var metadata, out var content)
            || !string.Equals(metadata.TitleIdentity, NormalizeIdentityField(title), StringComparison.Ordinal)
            || !string.Equals(metadata.ArtistIdentity, NormalizeIdentityField(artist), StringComparison.Ordinal))
        {
            return null;
        }

        return new LyricsCacheEntry(
            content,
            metadata.SourceName,
            metadata.NeedsConfirmation && !metadata.IsConfirmed,
            metadata.IsConfirmed,
            metadata.Message,
            metadata.MatchedCandidateKey,
            metadata.MatchedTitle,
            metadata.MatchedArtist,
            path);
    }

    /// <summary>缓存里是否有一份身份、内容都能验证的歌词。</summary>
    public bool Exists(string title, string artist) => Read(title, artist) is not null;

    /// <summary>
    /// 把歌词写入缓存。写入失败返回 null，不影响歌词显示。
    /// 歌词正文与元数据分别原子替换，元数据保存曲目身份、来源和匹配确认状态。
    /// </summary>
    public string? Write(
        string title,
        string artist,
        string lrcContent,
        string? sourceName = null,
        bool needsConfirmation = false,
        string? message = null,
        string? matchedCandidateKey = null,
        string? matchedTitle = null,
        string? matchedArtist = null)
    {
        if (string.IsNullOrWhiteSpace(lrcContent))
        {
            return null;
        }

        var path = GetCachePath(title, artist);
        var metadataPath = GetMetadataPath(path);
        var token = Guid.NewGuid().ToString("N");
        var lyricsTempPath = path + "." + token + ".tmp";
        var metadataTempPath = metadataPath + "." + token + ".tmp";

        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            var content = new StringBuilder();
            content.Append("# LyricCanvas cache v2").AppendLine();
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                content.Append("# 来源: ").Append(sourceName).AppendLine();
            }

            content.Append("# 抓取时间: ")
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine();
            content.Append(lrcContent);
            var savedLyrics = content.ToString();

            var titleIdentity = NormalizeIdentityField(title);
            var artistIdentity = NormalizeIdentityField(artist);
            var metadata = new CacheMetadata(
                CurrentFormatVersion,
                titleIdentity,
                artistIdentity,
                BuildCacheKey(titleIdentity, artistIdentity),
                ComputeSha256(savedLyrics),
                sourceName ?? string.Empty,
                needsConfirmation,
                IsConfirmed: false,
                message ?? string.Empty,
                matchedCandidateKey ?? string.Empty,
                matchedTitle ?? string.Empty,
                matchedArtist ?? string.Empty);

            File.WriteAllText(lyricsTempPath, savedLyrics, new UTF8Encoding(false));
            File.WriteAllText(
                metadataTempPath,
                JsonSerializer.Serialize(metadata, MetadataJsonOptions),
                new UTF8Encoding(false));

            File.Move(lyricsTempPath, path, overwrite: true);
            File.Move(metadataTempPath, metadataPath, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            TryDeleteFile(lyricsTempPath);
            TryDeleteFile(metadataTempPath);
        }
    }

    /// <summary>把当前缓存匹配标记为用户已确认。</summary>
    public bool MarkConfirmed(string title, string artist)
    {
        var path = GetCachePath(title, artist);
        if (!TryReadVerified(path, out var metadata, out _)
            || !string.Equals(metadata.TitleIdentity, NormalizeIdentityField(title), StringComparison.Ordinal)
            || !string.Equals(metadata.ArtistIdentity, NormalizeIdentityField(artist), StringComparison.Ordinal))
        {
            return false;
        }

        return TryWriteMetadata(
            GetMetadataPath(path),
            metadata with { NeedsConfirmation = false, IsConfirmed = true, Message = string.Empty });
    }

    /// <summary>只删除指定曲目对应的新版缓存及其身份元数据。</summary>
    public bool Delete(string title, string artist)
    {
        var path = GetCachePath(title, artist);
        var deletedLyrics = TryDeleteFile(path);
        var deletedMetadata = TryDeleteFile(GetMetadataPath(path));
        return deletedLyrics || deletedMetadata;
    }

    /// <summary>
    /// 清空 cache 根目录里可确认由词绘管理的歌词文件。不会递归处理子目录，
    /// 也会保留没有缓存元数据或旧缓存标记、身份无法核验的文件。
    /// </summary>
    public int Clear()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return 0;
        }

        if (IsReparsePoint(_cacheDirectory))
        {
            return 0;
        }

        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.lrc", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                var metadataPath = GetMetadataPath(file);
                var managed = TryReadVerified(file, out _, out _)
                    || IsLegacyManagedFile(file);
                if (!managed || !TryDeleteFile(file))
                {
                    continue;
                }

                removed++;
                TryDeleteFile(metadataPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 无法访问的目录或文件保持原样，继续使用已有统计。
        }

        return removed;
    }

    /// <summary>统计 cache 根目录里身份可确认的新版和带旧版抓取标记的缓存。</summary>
    public LyricsCacheInfo GetInfo()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return new LyricsCacheInfo(0, 0, _cacheDirectory);
        }

        if (IsReparsePoint(_cacheDirectory))
        {
            return new LyricsCacheInfo(0, 0, _cacheDirectory);
        }

        var count = 0;
        var ignoredLegacyCount = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.lrc", SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                if (!TryReadVerified(file, out _, out _))
                {
                    if (IsLegacyManagedFile(file))
                    {
                        ignoredLegacyCount++;
                    }

                    continue;
                }

                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return new LyricsCacheInfo(count, bytes, _cacheDirectory)
        {
            IgnoredLegacyFileCount = ignoredLegacyCount,
        };
    }

    private bool TryReadVerified(string path, out CacheMetadata metadata, out string content)
    {
        metadata = null!;
        content = string.Empty;
        var metadataPath = GetMetadataPath(path);
        if (!File.Exists(path) || !File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            content = File.ReadAllText(path);
            metadata = JsonSerializer.Deserialize<CacheMetadata>(
                File.ReadAllText(metadataPath), MetadataJsonOptions)!;
            if (metadata is null
                || metadata.FormatVersion != CurrentFormatVersion
                || !string.Equals(metadata.CacheKey, BuildCacheKey(metadata.TitleIdentity, metadata.ArtistIdentity), StringComparison.Ordinal)
                || !string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    Path.GetFileNameWithoutExtension(GetCachePath(metadata.TitleIdentity, metadata.ArtistIdentity)),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(metadata.LyricsSha256, ComputeSha256(content), StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            return false;
        }
    }

    private bool TryWriteMetadata(string path, CacheMetadata metadata)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(metadata, MetadataJsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static bool IsLegacyManagedFile(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            for (var i = 0; i < 8; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    return false;
                }

                if (line.StartsWith("# 抓取时间:", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return false;
    }

    private static string GetMetadataPath(string lyricsPath) => lyricsPath + ".meta.json";

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 无法判明文件所有权时，清理和统计都按未知文件处理。
            return true;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string BuildBaseName(string title, string artist)
    {
        var titlePrefix = LrcFileFinder.SanitizeFileName(title);
        var artistPrefix = LrcFileFinder.SanitizeFileName(artist);
        if (titlePrefix.Length > 34)
        {
            titlePrefix = titlePrefix[..34].TrimEnd('.', ' ');
        }

        if (artistPrefix.Length > 12)
        {
            artistPrefix = artistPrefix[..12].TrimEnd('.', ' ');
        }

        var readable = titlePrefix.Length == 0 ? "unknown" : titlePrefix;
        if (artistPrefix.Length > 0)
        {
            readable += "-" + artistPrefix;
        }

        return readable + "-" + BuildCacheKey(title, artist);
    }

    private static string BuildCacheKey(string title, string artist)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            NormalizeIdentityField(title),
            NormalizeIdentityField(artist),
        });
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static string NormalizeIdentityField(string? value)
        => (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>经过身份和正文校验的缓存条目。</summary>
public sealed record LyricsCacheEntry(
    string LyricsText,
    string SourceName,
    bool NeedsConfirmation,
    bool IsConfirmed,
    string Message,
    string MatchedCandidateKey,
    string MatchedTitle,
    string MatchedArtist,
    string FilePath);

/// <summary>缓存统计信息。</summary>
public sealed record LyricsCacheInfo(int FileCount, long TotalBytes, string Directory)
{
    /// <summary>不含身份元数据的旧版缓存数量；不会命中，但清理时可删除。</summary>
    public int IgnoredLegacyFileCount { get; init; }

    /// <summary>人类可读的大小描述。</summary>
    public string SizeText => TotalBytes switch
    {
        < 1024 => $"{TotalBytes} 字节",
        < 1024 * 1024 => $"{TotalBytes / 1024.0:0.#} KB",
        _ => $"{TotalBytes / 1024.0 / 1024.0:0.#} MB",
    };
}

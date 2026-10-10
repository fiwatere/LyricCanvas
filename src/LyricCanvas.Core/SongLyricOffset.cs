using System.Text;

namespace LyricCanvas.Core;

/// <summary>某首歌曲单独保存的歌词时间偏移。</summary>
public sealed class SongLyricOffset
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;

    /// <summary>正值表示歌词提前，负值表示歌词延后。</summary>
    public int OffsetMs { get; set; }

    public static string NormalizePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string CreateKey(string? title, string? artist)
        => NormalizePart(title) + "\u001f" + NormalizePart(artist);

    public SongLyricOffset Clone() => new()
    {
        Title = Title,
        Artist = Artist,
        OffsetMs = OffsetMs,
    };
}

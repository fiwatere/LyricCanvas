using System.Globalization;

namespace LyricCanvas.Media;

internal static class AudioLyricModelRecovery
{
    private const string ManagedModelFileName = "ggml-tiny-q5_1.bin";

    /// <summary>
    /// 将词绘管理目录里的疑似损坏模型移动到带时间戳的隔离文件，保留原内容以便恢复。
    /// 不处理用户指定目录或 LyricDesktop 旧目录中的文件。
    /// </summary>
    public static string? TryQuarantine(
        string modelPath,
        IReadOnlyCollection<string> managedDirectories,
        DateTimeOffset now)
    {
        try
        {
            var fullPath = Path.GetFullPath(modelPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null
                || !string.Equals(Path.GetFileName(fullPath), ManagedModelFileName, StringComparison.OrdinalIgnoreCase)
                || !managedDirectories.Any(candidate => PathsEqual(candidate, directory))
                || IsReparsePoint(directory)
                || !File.Exists(fullPath))
            {
                return null;
            }

            var quarantinePath = fullPath + ".invalid-" + now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            if (File.Exists(quarantinePath))
            {
                quarantinePath += "-" + Guid.NewGuid().ToString("N");
            }

            File.Move(fullPath, quarantinePath);
            return quarantinePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

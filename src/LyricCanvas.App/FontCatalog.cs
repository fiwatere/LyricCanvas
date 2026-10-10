using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace LyricCanvas.App;

/// <summary>管理系统字体和仅供词绘使用的导入字体。</summary>
public static class FontCatalog
{
    private const string ImportedScheme = "lyricfont://";
    private const string DefaultFont = "Microsoft YaHei UI";

    private static readonly string[] BuiltInFonts =
    {
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "SimHei",
        "SimSun",
        "KaiTi",
        "Segoe UI",
        "Arial",
    };

    private static string FontsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LyricCanvas",
        "Fonts");

    public sealed record Choice(string Name, string Value)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<Choice> GetChoices()
    {
        var result = BuiltInFonts.Select(name => new Choice(name, name)).ToList();
        var values = new HashSet<string>(BuiltInFonts, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in GetDirectoriesSafely())
        {
            var id = Path.GetFileName(directory);
            foreach (var file in GetFilesSafely(directory).Where(IsSupportedFont))
            {
                try
                {
                    foreach (var familyName in ReadFamilyNames(file))
                    {
                        var value = $"{ImportedScheme}{id}/{Uri.EscapeDataString(familyName)}";
                        if (values.Add(value))
                        {
                            result.Add(new Choice($"{familyName}（导入）", value));
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException
                                           or UnauthorizedAccessException)
                {
                    // 忽略损坏或当前系统无法解析的字体文件，仍保留其他可用字体。
                }
            }
        }

        return result;
    }

    /// <summary>复制字体到词绘专用目录并返回可在设置列表中选择的字体。</summary>
    public static IReadOnlyList<Choice> Import(string sourcePath)
    {
        if (!IsSupportedFont(sourcePath))
        {
            throw new ArgumentException("目前支持 .ttf 和 .otf 字体文件。", nameof(sourcePath));
        }

        var names = ReadFamilyNames(sourcePath);
        if (names.Length == 0)
        {
            throw new InvalidDataException("没有从该文件中读取到字体名称。");
        }

        var id = Guid.NewGuid().ToString("N");
        var targetDirectory = Path.Combine(FontsDirectory, id);
        var targetPath = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
        Directory.CreateDirectory(targetDirectory);
        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch
        {
            try
            {
                // Only remove the unique file/directory created by this import attempt.
                File.Delete(targetPath);
                Directory.Delete(targetDirectory, recursive: false);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // Keep the original import error when cleanup is also blocked.
            }

            throw;
        }

        return names.Select(name => new Choice(
            $"{name}（导入）",
            $"{ImportedScheme}{id}/{Uri.EscapeDataString(name)}")).ToArray();
    }

    /// <summary>把设置中保存的字体标识转换成 WPF 可用字体。</summary>
    public static FontFamily CreateFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(ImportedScheme, StringComparison.OrdinalIgnoreCase))
        {
            return new FontFamily(string.IsNullOrWhiteSpace(value) ? DefaultFont : value);
        }

        try
        {
            var uri = new Uri(value, UriKind.Absolute);
            var id = uri.Host;
            var familyName = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(familyName))
            {
                return new FontFamily(DefaultFont);
            }

            var directory = Path.Combine(FontsDirectory, id) + Path.DirectorySeparatorChar;
            if (!Directory.Exists(directory))
            {
                return new FontFamily(DefaultFont);
            }

            var baseUri = new Uri(directory, UriKind.Absolute);
            return new FontFamily(baseUri, $"./#{familyName}");
        }
        catch (UriFormatException)
        {
            return new FontFamily(DefaultFont);
        }
        catch (ArgumentException)
        {
            return new FontFamily(DefaultFont);
        }
    }

    private static bool IsSupportedFont(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ReadFamilyNames(string file)
    {
        using var collection = new PrivateFontCollection();
        collection.AddFontFile(file);
        var families = collection.Families;
        try
        {
            return families.Select(family => family.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            foreach (var family in families)
            {
                family.Dispose();
            }
        }
    }

    private static string[] GetDirectoriesSafely()
    {
        try
        {
            return Directory.GetDirectories(FontsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string[] GetFilesSafely(string directory)
    {
        try
        {
            return Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}

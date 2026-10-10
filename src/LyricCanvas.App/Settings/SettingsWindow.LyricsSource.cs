using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LyricCanvas.Core;
using MessageBox = System.Windows.MessageBox;

namespace LyricCanvas.App.Settings;

/// <summary>歌词目录、缓存、播放器诊断和逐曲偏移。</summary>
public partial class SettingsWindow
{
    private void FetchNetworkCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var enabled = FetchNetworkCheck.IsChecked == true;
        if (_config.Current.FetchLyricsFromNetwork != enabled)
        {
            _config.Update(c => c.FetchLyricsFromNetwork = enabled);
            _windowManager.RefreshCurrentLyrics();
        }
        UpdateCacheInfo();
    }

    private void AudioLyricSyncCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var enabled = AudioLyricSyncCheck.IsChecked == true;
        if (!_config.UpdateAndSave(c => c.AudioLyricSyncEnabled = enabled))
        {
            _suppressEvents = true;
            AudioLyricSyncCheck.IsChecked = _config.Current.AudioLyricSyncEnabled;
            _suppressEvents = false;
            AudioLyricSyncStatusText.Text =
                $"设置保存失败：{_config.LastSaveError ?? "未知错误"}";
            return;
        }

        AudioLyricSyncStatusText.Text = enabled
            ? _windowManager.AudioLyricSyncStatus
            : "未启用";
    }

    private void OnAudioLyricSyncStatusChanged(string status)
    {
        if (AudioLyricSyncStatusText is not null)
        {
            AudioLyricSyncStatusText.Text = _config.Current.AudioLyricSyncEnabled
                ? status
                : "未启用";
        }
    }

    private void MediaAppAllowListBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _mediaAppAllowListDebounce ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _mediaAppAllowListDebounce!.Stop();
                SaveMediaAppAllowList();
            },
            Dispatcher);

        _mediaAppAllowListDebounce.Stop();
        _mediaAppAllowListDebounce.Start();
    }

    private void SaveMediaAppAllowList()
    {
        if (MediaAppAllowListBox is null)
        {
            return;
        }

        var ids = MediaAppAllowListBox.Text
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var value = string.Join(Environment.NewLine, ids);
        if (!string.Equals(value, _config.Current.MediaAppAllowList, StringComparison.Ordinal))
        {
            _config.Update(c => c.MediaAppAllowList = value);
        }
    }

    private void UpdateMediaSourceDiagnostics()
    {
        if (MediaSourceDiagnosticsText is null)
        {
            return;
        }

        var diagnostics = _windowManager.MediaDiagnostics;
        var appId = string.IsNullOrWhiteSpace(diagnostics.AppId) ? "无" : diagnostics.AppId;
        var title = string.IsNullOrWhiteSpace(diagnostics.Title) ? "（无媒体标题）" : diagnostics.Title;
        var state = diagnostics.Playback switch
        {
            PlaybackState.Playing => "播放中",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Stopped => "已停止",
            PlaybackState.Closed => "已关闭",
            PlaybackState.Changing => "切换中",
            PlaybackState.Opened => "已打开",
            _ => "未知",
        };
        var sourceUpdate = diagnostics.TimelineUpdatedAtUtc is { } timestamp
            ? timestamp.ToLocalTime().ToString("HH:mm:ss.fff")
            : "尚未提供";
        var sampledAt = diagnostics.SampledAtUtc == DateTimeOffset.MinValue
            ? "尚未取样"
            : diagnostics.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss.fff");

        MediaSourceDiagnosticsText.Text =
            $"媒体会话：{diagnostics.SessionCount} 个 · {diagnostics.SelectionStatus}\n" +
            $"应用 ID：{appId}\n" +
            $"媒体标题：{title}\n" +
            $"状态：{state} · 播放器报告位置：{FormatMediaPosition(diagnostics.ReportedPosition)}\n" +
            $"播放器时间轴更新时间：{sourceUpdate} · 本机取样：{sampledAt}\n" +
            $"LyricCanvas 估算位置：{FormatMediaPosition(diagnostics.EstimatedPosition)}";
        CopyObservedMediaAppIdButton.IsEnabled = !string.IsNullOrWhiteSpace(diagnostics.AppId);
    }

    private static string FormatMediaPosition(TimeSpan position)
    {
        var milliseconds = Math.Max(0, (long)position.TotalMilliseconds);
        var minutes = milliseconds / 60_000;
        var seconds = milliseconds / 1_000 % 60;
        var fraction = milliseconds % 1_000;
        return $"{minutes:00}:{seconds:00}.{fraction:000}";
    }

    private void CopyObservedMediaAppIdButton_Click(object sender, RoutedEventArgs e)
    {
        var appId = _windowManager.MediaDiagnostics.AppId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(appId);
            StatusText.Text = "媒体应用 ID 已复制。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = "无法访问剪贴板，请从诊断信息中复制应用 ID。";
        }
    }

    private void EarlierLyricButton_Click(object sender, RoutedEventArgs e)
        => AdjustCurrentLyricOffset(100);

    private void LaterLyricButton_Click(object sender, RoutedEventArgs e)
        => AdjustCurrentLyricOffset(-100);

    private void ClearLyricOffsetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_windowManager.ClearCurrentSongLyricOffset())
        {
            CurrentLyricOffsetText.Text = $"保存失败：{_config.LastSaveError ?? "未检测到当前歌曲"}";
            return;
        }

        RefreshCurrentLyricOffsetControls();
    }

    private void AdjustCurrentLyricOffset(int deltaMs)
    {
        if (!_windowManager.AdjustCurrentSongLyricOffset(deltaMs))
        {
            CurrentLyricOffsetText.Text = $"保存失败：{_config.LastSaveError ?? "未检测到当前歌曲"}";
            return;
        }

        RefreshCurrentLyricOffsetControls();
    }

    private void OnCurrentTrackChanged(NowPlaying _) => RefreshCurrentLyricOffsetControls();

    private void RefreshCurrentLyricOffsetControls()
    {
        if (CurrentLyricTrackText is null || CurrentLyricOffsetText is null)
        {
            return;
        }

        var track = _windowManager.CurrentNowPlaying;
        var hasTrack = track.HasTrack;
        var offset = hasTrack
            ? _config.Current.GetSongLyricOffsetMs(track.Title, track.Artist)
            : 0;

        CurrentLyricTrackText.Text = hasTrack ? track.DisplayText : "尚未读取到播放器歌曲信息";
        CurrentLyricOffsetText.Text = $"当前偏移：{offset:+#;-#;0} 毫秒";
        EarlierLyricButton.IsEnabled = hasTrack && offset < 10_000;
        LaterLyricButton.IsEnabled = hasTrack && offset > -10_000;
        ClearLyricOffsetButton.IsEnabled = hasTrack && offset != 0;
    }

    /// <summary>当前歌词目录。没指定时用程序目录下的 lyrics，和主窗口保持一致。</summary>
    private string GetEffectiveLyricsDirectory()
    {
        var custom = _config.Current.LyricsDirectory;
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(AppContext.BaseDirectory, "lyrics")
            : custom.Trim();
    }

    private void UpdateCacheInfo()
    {
        if (CacheInfoText is null)
        {
            return;
        }

        var directory = GetEffectiveLyricsDirectory();
        var info = new LyricsCache(directory).GetInfo();

        var networkText = _config.Current.FetchLyricsFromNetwork
            ? "已开启：本地找不到时会联网抓取。"
            : "已关闭：只使用本地文件和已有缓存。";

        CacheInfoText.Text = info.FileCount == 0
            ? $"{networkText}\n可用缓存为空，还没有新版缓存。"
            : $"{networkText}\n可用缓存里现有 {info.FileCount} 首歌词，占用 {info.SizeText}。";
        if (info.IgnoredLegacyFileCount > 0)
        {
            CacheInfoText.Text += $"\n另有 {info.IgnoredLegacyFileCount} 首旧版缓存缺少曲目身份记录，不会被使用；需要时会重新抓取。";
        }

        ClearCacheButton.IsEnabled = info.FileCount > 0 || info.IgnoredLegacyFileCount > 0;
    }

    private void RefreshCacheInfo_Click(object sender, RoutedEventArgs e) => UpdateCacheInfo();


    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var directory = GetEffectiveLyricsDirectory();

        var confirm = MessageBox.Show(
            "确定要清空自动抓取的歌词缓存吗？\n\n" +
            "只处理 cache 目录顶层中可确认由词绘管理的缓存歌词。未知文件、子目录和重解析点会保留；歌词目录根部的个人 LRC 不受影响。",
            "清空歌词缓存",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var removed = new LyricsCache(directory).Clear();
        UpdateCacheInfo();

        MessageBox.Show(
            $"已清除 {removed} 首可确认由词绘管理的缓存歌词。\n\n需要重新获取当前歌曲时，请使用“重新查找当前歌曲”。",
            "词绘",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenCacheDirectory_Click(object sender, RoutedEventArgs e)
    {
        OpenInExplorer(new LyricsCache(GetEffectiveLyricsDirectory()).CacheDirectory);
    }


    private void LyricsDirectoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        // 打字过程中每个字符都保存会频繁写盘，延迟一下
        _lyricsDirDebounce ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _lyricsDirDebounce!.Stop();
                SaveLyricsDirectory();
            },
            Dispatcher);

        _lyricsDirDebounce.Stop();
        _lyricsDirDebounce.Start();
    }

    private void SaveLyricsDirectory()
    {
        var directory = LyricsDirectoryBox.Text.Trim();
        if (!string.Equals(_config.Current.LyricsDirectory, directory, StringComparison.Ordinal))
        {
            _config.Update(c => c.LyricsDirectory = directory);
            _windowManager.RefreshCurrentLyrics();
        }

        UpdateLyricsDirectoryHint();
    }

    private void UpdateLyricsDirectoryHint()
    {
        if (LyricsDirectoryHint is null || SearchOrderText is null)
        {
            return;
        }

        var custom = LyricsDirectoryBox.Text.Trim();

        if (custom.Length == 0)
        {
            LyricsDirectoryHint.Text = "未设置自定义目录：将依次搜索程序目录和当前工作目录下的 lyrics 文件夹。";
        }
        else if (Directory.Exists(custom))
        {
            var count = 0;
            try
            {
                count = Directory.GetFiles(custom, "*.lrc").Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            LyricsDirectoryHint.Text = $"目录存在，里面有 {count} 个 .lrc 文件。";
        }
        else
        {
            LyricsDirectoryHint.Text = "目录不存在，请检查路径。";
        }

        SearchOrderText.Text = string.Join(
            Environment.NewLine,
            GetSearchOrderPreview().Select((p, i) => $"{i + 1}. {p}"));
    }

    private IEnumerable<string> GetSearchOrderPreview()
    {
        var custom = LyricsDirectoryBox.Text.Trim();
        return LyricsDirectoryPolicy.GetSearchDirectories(
            custom,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());
    }

    private void RefreshCurrentLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (!_windowManager.RefreshCurrentLyrics(forceSearch: true))
        {
            MessageBox.Show(this, "当前没有可重新查找的歌曲。", "词绘", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCurrentLyricsResolutionChanged(LyricsResolution? resolution)
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateCurrentLyricsResolutionDisplay(resolution);
        }
        else
        {
            Dispatcher.BeginInvoke(() => UpdateCurrentLyricsResolutionDisplay(resolution));
        }
    }

    private void UpdateCurrentLyricsResolutionDisplay(LyricsResolution? resolution)
    {
        if (CurrentLyricsResolutionText is null)
        {
            return;
        }

        var needsConfirmation = resolution?.NeedsConfirmation == true;
        ConfirmLyricsMatchButton.IsEnabled = needsConfirmation;
        RejectLyricsMatchButton.IsEnabled = needsConfirmation;

        if (resolution is null)
        {
            CurrentLyricsResolutionText.Text = "当前歌曲歌词尚未就绪。";
            return;
        }

        if (!resolution.HasLyrics)
        {
            CurrentLyricsResolutionText.Text = string.IsNullOrWhiteSpace(resolution.Message)
                ? "当前歌曲未找到歌词。"
                : $"当前歌曲未找到歌词：{resolution.Message}";
            return;
        }

        var origin = resolution.Origin switch
        {
            LyricsOrigin.UserFile => "本地文件",
            LyricsOrigin.Cache => "缓存",
            LyricsOrigin.Network => "网络获取",
            _ => "未知来源",
        };
        var source = string.IsNullOrWhiteSpace(resolution.SourceName)
            ? string.Empty
            : $" · {resolution.SourceName}";
        var candidate = string.IsNullOrWhiteSpace(resolution.MatchedTitle)
            ? string.Empty
            : $"\n候选：{resolution.MatchedTitle}" +
              (string.IsNullOrWhiteSpace(resolution.MatchedArtist) ? string.Empty : $" — {resolution.MatchedArtist}");

        if (needsConfirmation)
        {
            CurrentLyricsResolutionText.Text =
                $"歌词来源：{origin}{source}\n匹配可能不准确，请确认或尝试其他结果。{candidate}\n{resolution.Message}";
        }
        else if (resolution.IsConfirmed)
        {
            CurrentLyricsResolutionText.Text = $"歌词来源：{origin}{source} · 已确认{candidate}";
        }
        else
        {
            CurrentLyricsResolutionText.Text = $"歌词来源：{origin}{source}{candidate}";
        }
    }

    private void ConfirmLyricsMatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_windowManager.ConfirmCurrentLyricsMatch())
        {
            MessageBox.Show(this, "当前没有可确认的歌词匹配。", "词绘", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RejectLyricsMatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_windowManager.RejectCurrentLyricsMatch())
        {
            MessageBox.Show(this, "当前没有可尝试替换的低置信匹配。", "词绘", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenLyricsDirectory_Click(object sender, RoutedEventArgs e)
    {
        var custom = LyricsDirectoryBox.Text.Trim();
        var target = custom.Length > 0 && Directory.Exists(custom)
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "lyrics");

        OpenInExplorer(target);
    }

}

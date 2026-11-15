using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Dlss5Demo.Core;
using FluentAvalonia.UI.Controls;

namespace Dlss5Demo.App.Views;

/// <summary>
/// 设置页：外观、DLSS NR 运行库目录（含迁移）与关于信息。目录切换只在这里改，
/// 工作台通过 <see cref="AppSession.NrDirectoryChanged"/> 重新拉取版本列表。
/// </summary>
public partial class SettingsView : UserControl
{
    private AppSession? _session;
    private bool _loading;
    private bool _busy;
    private CancellationTokenSource? _migrationCts;

    public SettingsView() => InitializeComponent();

    internal void Attach(AppSession session)
    {
        _session = session;
        _loading = true;
        try
        {
            (session.Settings.ThemeIndex switch
            {
                1 => ThemeLightRadio,
                2 => ThemeDarkRadio,
                _ => ThemeSystemRadio,
            }).IsChecked = true;

            var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            AboutVersionText.Text = $"DLSS5 Video Enc · v{version}";
            AboutPathText.Text =
                $"宿主：{DemoPaths.HostDllPath}\n" +
                $"运行库目录：{NrRuntimeLocations.EffectiveDirectory}\n" +
                $"设置文件：{DemoPaths.SettingsPath}\n" +
                $"日志目录：{DemoPaths.LogsDir}";
            RefreshDirectoryView();
        }
        finally
        {
            _loading = false;
        }

        if (session.StartupWarning is { Length: > 0 } warning)
            ShowError($"设置页指定的目录不可用，已回退到内置 runtime：{warning}");
    }

    // ============ 外观 ============

    private void ThemeRadio_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loading || _session is null) return;
        if ((sender as RadioButton)?.Tag is not { } tag ||
            !int.TryParse(tag.ToString(), out int index)) return;
        _session.Settings.ThemeIndex = Math.Clamp(index, 0, 2);
        _session.ApplyTheme();
        _session.Save();
    }

    // ============ NR 运行库目录 ============

    /// <summary>按钮/状态刷新：所有可用性判断集中在这里，避免迁移中重复点击。</summary>
    private void RefreshDirectoryView()
    {
        if (_session is null) return;
        string directory = _session.NrDirectory;
        NrDirectoryBox.Text = directory;
        NrDirectoryPill.Text = _session.UsesCustomNrDirectory ? "自定义目录" : "内置 runtime";
        NrDirectoryPill.Tone = _session.UsesCustomNrDirectory ? "ready" : "off";
        ChangeNrDirectoryButton.IsEnabled = !_busy;
        OpenNrDirectoryButton.IsEnabled = !_busy;
        ResetNrDirectoryButton.IsEnabled = !_busy && _session.UsesCustomNrDirectory;
        MigrateNrButton.IsEnabled = !_busy && _session.UsesCustomNrDirectory;

        // 只统计当前生效目录内的条目：设置页讲的是「这个目录里有什么」，
        // 其它来源（mods、external）留给工作台的版本列表。
        var entries = NrRuntimeCatalog.Discover()
            .Where(item => IsInDirectory(item.DllPath, directory))
            .ToList();
        long bytes = 0;
        foreach (var entry in entries)
        {
            try { bytes += new FileInfo(entry.DllPath).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        NrSummaryText.Text = entries.Count == 0
            ? "该目录暂未发现 nvngx_dlssnr*.dll。把一个或多个版本放进该目录（可带子目录），再在工作台点「刷新」。"
            : $"当前目录已发现 {entries.Count} 个 NR 运行库 · {bytes / 1048576.0:0.0} MiB" +
              (_session.UsesCustomNrDirectory ? "" : "（内置目录随程序发布，仅作后备）");
    }

    private static bool IsInDirectory(string path, string directory)
    {
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenNrDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        OpenDirectory(_session.NrDirectory);
    }

    private async void ChangeNrDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        IStorageFolder? start = null;
        try { start = await top.StorageProvider.TryGetFolderFromPathAsync(_session.NrDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 DLSS NR 运行库目录",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        string? target = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(target)) return;
        if (NrRuntimeLocations.IsSameDirectory(target, _session.NrDirectory)) return;

        NrMigrationPlan plan;
        try { plan = await Task.Run(() => NrMigrationPlan.Create(_session.NrDirectory, target)); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowError("无法读取当前目录：" + error.Message);
            return;
        }

        var content = new StackPanel { Spacing = 8, MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = $"当前：{_session.NrDirectory}",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"新目录：{Path.GetFullPath(target)}",
            TextWrapping = TextWrapping.Wrap,
        });
        var migrateBox = new CheckBox
        {
            Content = "同时迁移现有运行库（校验通过后删除原文件）",
            IsChecked = plan.Items.Count > 0,
            IsVisible = plan.Items.Count > 0,
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.Children.Add(new TextBlock
        {
            Text = plan.Items.Count == 0
                ? "当前目录没有运行库文件；切换后把 nvngx_dlssnr*.dll 放进新目录即可。"
                : $"当前目录有 {plan.Items.Count} 个运行库（{plan.TotalBytes / 1048576.0:0.0} MiB）。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(migrateBox);
        if (_session.IsProcessing)
            content.Children.Add(new TextBlock
            {
                Text = "队列正在处理：NR DLL 可能被占用，迁移失败时源文件会保留，建议空闲时再试。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = this.TryFindResource("SystemFillColorCautionBrush", out object? caution) && caution is IBrush brush
                    ? brush : null,
            });

        var dialog = new FAContentDialog
        {
            Title = "更改 NR 运行库目录",
            Content = content,
            PrimaryButtonText = "切换目录",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync(top) != FAContentDialogResult.Primary) return;

        await ApplyDirectoryAsync(target, migrateBox.IsChecked == true, plan);
    }

    private async void MigrateNr_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        if (!_session.UsesCustomNrDirectory)
        {
            ShowError("当前使用内置 runtime 目录；请先「更改…」指定一个可写目录再迁移。");
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        NrMigrationPlan plan;
        try { plan = await Task.Run(() => NrMigrationPlan.Create(DemoPaths.RuntimeDir, _session.NrDirectory)); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowError("无法读取内置运行库目录：" + error.Message);
            return;
        }

        if (plan.Items.Count == 0)
        {
            NrStatusText.Text = "内置 runtime 目录里没有可迁移的运行库文件。";
            NrStatusText.IsVisible = true;
            return;
        }

        var dialog = new FAContentDialog
        {
            Title = "迁移内置运行库",
            Content = new TextBlock
            {
                Text = $"把内置 runtime 中的 {plan.Items.Count} 个运行库（{plan.TotalBytes / 1048576.0:0.0} MiB）" +
                       $"复制到 {_session.NrDirectory}，逐文件校验 SHA-256 后删除源文件。" +
                       "同名但内容不同的文件不会被覆盖，会另存为带序号的副本。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
            },
            PrimaryButtonText = "开始迁移",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync(top) != FAContentDialogResult.Primary) return;

        await ApplyDirectoryAsync(_session.NrDirectory, migrate: true, plan);
    }

    private async void ResetNrDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        if (!_session.UsesCustomNrDirectory) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var dialog = new FAContentDialog
        {
            Title = "恢复默认运行库目录",
            Content = new TextBlock
            {
                Text = "恢复后使用随程序发布的 runtime 目录。当前目录里的运行库文件不会被移动或删除，" +
                       "之后仍可用「更改…」重新指回该目录。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync(top) != FAContentDialogResult.Primary) return;

        if (!_session.TrySetNrDirectory(string.Empty, out string error))
        {
            ShowError(error);
            return;
        }
        RefreshDirectoryView();
        _session.NotifyNrDirectoryChanged(null);
        NrStatusText.Text = "已恢复使用内置 runtime 目录。";
        NrStatusText.IsVisible = true;
        NrErrorText.IsVisible = false;
    }

    /// <summary>放入新 DLL 后不必切页：这里重扫目录并把结果推到工作台的版本列表。</summary>
    private void RefreshNrList_Click(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        RefreshDirectoryView();
        _session.NotifyNrDirectoryChanged(null);
        NrStatusText.Text = "已重新扫描目录，工作台的 NR 版本列表同步更新。";
        NrStatusText.IsVisible = true;
    }

    private void CancelNrMigration_Click(object? sender, RoutedEventArgs e)
    {
        _migrationCts?.Cancel();
        NrStatusText.Text = "正在取消迁移…";
    }

    /// <summary>
    /// 先切换目录（校验可写并落盘）再迁移：切换失败时一个文件都不动，不会留下
    /// 「文件搬走了但设置还指向旧目录」的半成品状态；迁移失败则设置已生效，源文件保留在原处。
    /// </summary>
    private async Task ApplyDirectoryAsync(string target, bool migrate, NrMigrationPlan plan)
    {
        if (_session is null) return;
        if (!_session.TrySetNrDirectory(target, out string directoryError))
        {
            ShowError(directoryError);
            return;
        }

        SetBusy(true);
        NrErrorText.IsVisible = false;
        NrStatusText.IsVisible = false;
        string? remappedId = null;
        try
        {
            if (migrate && plan.Items.Count > 0)
            {
                _migrationCts = new CancellationTokenSource();
                MigrationProgress.Value = 0;
                var progress = new Progress<NrMigrationProgress>(update =>
                {
                    MigrationProgress.Value = update.Fraction;
                    NrStatusText.Text = $"正在迁移 {update.FilesDone}/{update.TotalFiles} · " +
                                        $"{update.BytesDone / 1048576.0:0.0}/{update.TotalBytes / 1048576.0:0.0} MiB · " +
                                        $"{Path.GetFileName(update.CurrentFile)}";
                    NrStatusText.IsVisible = true;
                });
                var result = await NrRuntimeMigration.RunAsync(plan, progress, _migrationCts.Token);
                remappedId = RemapSavedSelection(plan, result);
                NrStatusText.Text = "迁移完成 · " + result.Summary();
                NrStatusText.IsVisible = true;
                if (result.Messages.Count > 0)
                    ShowError(string.Join(Environment.NewLine, result.Messages.Take(5)) +
                              (result.Messages.Count > 5 ? $"{Environment.NewLine}…共 {result.Messages.Count} 条" : ""));
            }

            RefreshDirectoryView();
            _session.NotifyNrDirectoryChanged(remappedId);
        }
        catch (OperationCanceledException)
        {
            NrStatusText.Text = "已取消迁移：目录已切换，未完成的文件留在原目录，可再次点击「迁移内置运行库到此目录」。";
            NrStatusText.IsVisible = true;
            RefreshDirectoryView();
            _session.NotifyNrDirectoryChanged(null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            ShowError("迁移失败：" + error.Message);
            RefreshDirectoryView();
            _session.NotifyNrDirectoryChanged(null);
        }
        finally
        {
            _migrationCts?.Dispose();
            _migrationCts = null;
            SetBusy(false);
        }
    }

    /// <summary>迁移会改变条目 ID（runtime/ ↔ custom/），把保存的选择同步到新 ID。</summary>
    private string? RemapSavedSelection(NrMigrationPlan plan, NrMigrationResult result)
    {
        if (_session is null || string.IsNullOrWhiteSpace(_session.Settings.NrRuntimeId)) return null;
        string saved = _session.Settings.NrRuntimeId;
        foreach (var pair in result.PathMap)
        {
            string oldId = IdFor(plan.SourceRoot, Path.GetRelativePath(plan.SourceRoot, pair.Key));
            if (!string.Equals(saved, oldId, StringComparison.OrdinalIgnoreCase)) continue;
            string newId = IdFor(plan.TargetRoot, Path.GetRelativePath(plan.TargetRoot, pair.Value));
            _session.Settings.NrRuntimeId = newId;
            _session.Save();
            return newId;
        }
        return null;
    }

    /// <summary>与 <see cref="NrRuntimeCatalog"/> 一致的 ID 规则：内置 runtime / 用户目录两种前缀。</summary>
    private static string IdFor(string root, string relative)
    {
        string prefix = NrRuntimeLocations.IsSameDirectory(root, DemoPaths.RuntimeDir) ? "runtime/" : "custom/";
        return prefix + relative.Replace('\\', '/');
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        MigrationProgress.IsVisible = busy;
        CancelMigrationButton.IsVisible = busy;
        if (!busy) MigrationProgress.Value = 0;
        RefreshDirectoryView();
    }

    private void ShowError(string message)
    {
        NrErrorText.Text = message;
        NrErrorText.IsVisible = true;
    }

    // ============ 关于 ============

    private void OpenLogs_Click(object? sender, RoutedEventArgs e) => OpenDirectory(DemoPaths.LogsDir);

    private void OpenRuntimeDir_Click(object? sender, RoutedEventArgs e) => OpenDirectory(DemoPaths.RuntimeDir);

    private void OpenDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError("无法打开目录：" + error.Message);
        }
    }
}

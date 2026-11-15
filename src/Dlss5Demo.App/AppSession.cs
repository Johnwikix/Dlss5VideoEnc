using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Dlss5Demo.Core;

namespace Dlss5Demo.App;

/// <summary>
/// 工作台与设置页共享的会话状态：设置只有一份实例，主题切换、NR 运行库目录变更
/// 都从这里广播，避免两个页面各存一份配置互相覆盖。
/// </summary>
internal sealed class AppSession
{
    public AppSession(AppSettings settings)
    {
        Settings = settings;
        ApplyTheme();
        // PreferSystemTheme 关闭后由这里接管「跟随系统」：Windows 主题变化时重算一次，
        // 显式浅色/深色不受影响。
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) =>
            {
                if (Settings.ThemeIndex == 0) Dispatcher.UIThread.Post(ApplyTheme);
            };
        if (!string.IsNullOrWhiteSpace(Settings.NrRuntimeDirectory) &&
            !NrRuntimeLocations.TrySetUserDirectory(Settings.NrRuntimeDirectory, out string error))
        {
            // 自定义目录所在磁盘未连接时回退到内置 runtime，界面会同时给出提示，
            // 保存的路径保留，用户接回磁盘后无需重新选择。
            StartupWarning = error;
            Serilog.Log.Warning("NR 运行库目录不可用，回退到 runtime：{Directory}（{Error}）",
                Settings.NrRuntimeDirectory, error);
        }
    }

    public AppSettings Settings { get; }

    /// <summary>启动时自定义 NR 目录不可用的原因；null 表示一切正常。</summary>
    public string? StartupWarning { get; }

    /// <summary>当前生效的 NR 运行库根目录。</summary>
    public string NrDirectory => NrRuntimeLocations.EffectiveDirectory;

    public bool UsesCustomNrDirectory => NrRuntimeLocations.IsCustom;

    /// <summary>转码队列是否正在处理；设置页据此提示迁移可能失败。</summary>
    public bool IsProcessing { get; set; }

    /// <summary>NR 目录变化；参数为迁移后建议重新选中的版本 ID（null 表示沿用当前选择）。</summary>
    public event Action<string?>? NrDirectoryChanged;

    public void Save() => AppSettingsStore.Save(Settings);

    public void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = Settings.ThemeIndex switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => SystemThemeVariant(app),
        };
    }

    /// <summary>当前系统主题；平台不提供时回退 Default（仍由 Avalonia 解析）。</summary>
    private static ThemeVariant SystemThemeVariant(Application app)
    {
        try
        {
            // PlatformColorValues.ThemeVariant 是 PlatformThemeVariant?，需要映射到 ThemeVariant。
            return app.PlatformSettings?.GetColorValues().ThemeVariant switch
            {
                PlatformThemeVariant.Light => ThemeVariant.Light,
                PlatformThemeVariant.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            return ThemeVariant.Default;
        }
    }

    /// <summary>
    /// 保存并生效新的 NR 目录。目录不可写或与内置目录相同时返回 false，
    /// 此时设置与运行时状态都保持原样（迁移失败不应留下半切换状态）。
    /// </summary>
    public bool TrySetNrDirectory(string? directory, out string error)
    {
        string previous = Settings.NrRuntimeDirectory;
        if (!NrRuntimeLocations.TrySetUserDirectory(directory, out error))
        {
            // TrySetUserDirectory 只在失败时保持原值，这里恢复设置文本以保持一致。
            Settings.NrRuntimeDirectory = previous;
            return false;
        }

        Settings.NrRuntimeDirectory = NrRuntimeLocations.IsCustom ? NrRuntimeLocations.UserDirectory! : string.Empty;
        Save();
        return true;
    }

    public void NotifyNrDirectoryChanged(string? preferredRuntimeId = null) =>
        NrDirectoryChanged?.Invoke(preferredRuntimeId);
}

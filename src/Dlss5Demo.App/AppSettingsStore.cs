using System.Text.Json;
using Dlss5Demo.Core;

namespace Dlss5Demo.App;

/// <summary>
/// settings.json 持久化：默认在程序根目录；MSIX 打包运行时由 DemoPaths
/// 重定向到可写数据目录。损坏或不可写时回退默认值而不是崩溃。
/// </summary>
internal static class AppSettingsStore
{
    private static readonly string SettingsPath = DemoPaths.SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(SettingsPath), AppSettingsContext.Default.AppSettings);
            settings ??= new AppSettings();
            settings.UpgradeNrPresetDefault();
            return settings;
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "读取设置失败（{Path}），使用默认设置。", SettingsPath);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings));
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "保存设置失败（{Path}）。", SettingsPath);
        }
    }
}

using System.Text.Json;

namespace Dlss5Demo.App;

/// <summary>settings.json 持久化：文件在程序根目录，损坏时回退默认值而不是崩溃。</summary>
internal static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(SettingsPath), AppSettingsContext.Default.AppSettings);
            return settings ?? new AppSettings();
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

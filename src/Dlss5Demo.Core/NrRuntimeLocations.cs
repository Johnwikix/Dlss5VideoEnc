namespace Dlss5Demo.Core;

/// <summary>
/// NR 运行库目录的用户覆盖。设置页可把模型根目录改到任意可写位置（例如 MSIX 只读
/// 安装目录之外），进程内只保留一个值：目录发现（<see cref="NrRuntimeCatalog"/>）与
/// 「自动」解析（<see cref="DemoPaths.RuntimeDllPath"/>）都读这里，避免两处各存一份。
/// </summary>
public static class NrRuntimeLocations
{
    private static string? _userDirectory;

    /// <summary>自定义目录（绝对路径）；null 表示使用随程序发布的 runtime 目录。</summary>
    public static string? UserDirectory => _userDirectory;

    /// <summary>实际生效的 NR 运行库根目录。</summary>
    public static string EffectiveDirectory => _userDirectory ?? DemoPaths.RuntimeDir;

    /// <summary>是否使用用户自定义目录。</summary>
    public static bool IsCustom => _userDirectory != null;

    /// <summary>
    /// 设置自定义目录；空值或与 runtime 目录相同都按「使用默认」处理，避免同一批 DLL
    /// 在版本列表里出现两份、迁移时自己复制自己。目录不存在时尝试创建。
    /// </summary>
    /// <returns>目录可用返回 true；路径非法或目录无法创建时返回 false 并保持原值。</returns>
    public static bool TrySetUserDirectory(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _userDirectory = null;
            return true;
        }

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            error = "路径无效：" + exception.Message;
            return false;
        }

        if (IsSameDirectory(full, DemoPaths.RuntimeDir))
        {
            _userDirectory = null;
            return true;
        }

        try { Directory.CreateDirectory(full); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "目录不可用：" + exception.Message;
            return false;
        }

        _userDirectory = full;
        return true;
    }

    /// <summary>恢复默认 root=runtime。</summary>
    public static void Reset() => _userDirectory = null;

    public static bool IsSameDirectory(string left, string right) =>
        string.Equals(TrimSeparators(left), TrimSeparators(right), StringComparison.OrdinalIgnoreCase);

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

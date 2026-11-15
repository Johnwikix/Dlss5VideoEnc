using System.Diagnostics;

namespace Dlss5Demo.Core;

/// <summary>A discovered DLL, identified by its location rather than a list index or version number.</summary>
public sealed record NrRuntimeInfo(string Id, string DllPath, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Reads the installed NR DLLs without loading them. Folder names describe vendor/GPU variants.</summary>
public static class NrRuntimeCatalog
{
    public static IReadOnlyList<NrRuntimeInfo> Discover() => Discover(DemoPaths.RuntimeDir,
        DemoPaths.EnumerateOverrideDirectories(), DemoPaths.RuntimeDllPath, NrRuntimeLocations.UserDirectory);

    /// <summary>
    /// 扫描内置 runtime 目录、设置页指定的用户目录与 mods 覆盖目录。用户目录的条目以
    /// <c>custom/</c> 前缀区分，因此同一布局搬到别处后旧 ID 会变化，迁移流程负责重映射。
    /// </summary>
    internal static IReadOnlyList<NrRuntimeInfo> Discover(string runtimeDirectory,
        IEnumerable<string> overrideDirectories, string? automaticPath = null, string? userDirectory = null)
    {
        var results = new List<NrRuntimeInfo>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Scan(runtimeDirectory, "runtime/");
        if (!string.IsNullOrWhiteSpace(userDirectory) &&
            !NrRuntimeLocations.IsSameDirectory(userDirectory, runtimeDirectory))
            Scan(userDirectory, "custom/");
        foreach (string directory in overrideDirectories) Scan(directory, "mods/");
        if (!string.IsNullOrWhiteSpace(automaticPath) && File.Exists(automaticPath))
            Add(automaticPath, "external/" + Path.GetFullPath(automaticPath));
        results.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        return results;

        void Scan(string directory, string prefix)
        {
            if (!Directory.Exists(directory)) return;
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, MatchCasing = MatchCasing.CaseInsensitive };
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "nvngx_dlssnr*.dll", enumeration))
                {
                    string name = Path.GetFileName(file);
                    if (!name.StartsWith("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    Add(file, prefix + Path.GetRelativePath(directory, file).Replace('\\', '/'));
                }
            }
            catch (IOException) { /* A directory can disappear during refresh. */ }
            catch (UnauthorizedAccessException) { }
        }

        void Add(string file, string id)
        {
            string fullPath = Path.GetFullPath(file);
            if (!paths.Add(fullPath) || !ids.Add(id)) return;
            string? version = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(fullPath);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string label = id.EndsWith("/nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)
                ? id[..^"/nvngx_dlssnr.dll".Length] : id;
            label = label switch
            {
                "runtime" => "runtime（内置激活版）",
                "custom" => "用户目录（激活版）",
                _ when label.StartsWith("custom/", StringComparison.OrdinalIgnoreCase) => "用户目录/" + label["custom/".Length..],
                _ => label,
            };
            results.Add(new NrRuntimeInfo(id, fullPath,
                string.IsNullOrWhiteSpace(version) ? label : $"{version} · {label}"));
        }
    }

    /// <summary>Empty means the existing environment/mods/default policy. An explicit missing choice must fail.</summary>
    public static string Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return DemoPaths.RuntimeDllPath;
        return Discover().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.DllPath
            ?? throw new FileNotFoundException($"所选 NR 版本已不存在：{id}。请刷新版本列表并重新选择，或将 DLL 放回原目录。");
    }

    /// <summary>CLI accepts a catalog ID, an absolute path, or a path relative to the working/runtime directory.</summary>
    public static string ResolveArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DemoPaths.RuntimeDllPath;
        var installed = Discover().FirstOrDefault(item => string.Equals(item.Id, value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (installed != null) return installed.DllPath;
        string candidate = Path.GetFullPath(value);
        if (File.Exists(candidate)) return candidate;
        candidate = Path.GetFullPath(Path.Combine(DemoPaths.RuntimeDir, value));
        if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("找不到指定的 NR DLL，请检查 --nr-runtime 路径。", value);
    }
}

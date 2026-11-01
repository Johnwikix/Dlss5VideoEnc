namespace Dlss5Demo.Core;

/// <summary>定位 demo 的 runtime 目录（宿主 DLL 与 NVIDIA 运行库所在处）。</summary>
public static class DemoPaths
{
    public static string RuntimeDir { get; } = FindRuntimeDir();

    public static string HostDllPath => Path.Combine(RuntimeDir, "dlssnr_host_v2.dll");
    /// <summary>
    /// 解析当前使用的 DLSS NR 运行库。
    ///
    /// 运行库按显卡代际分发，不能把 RTX 20/30/40/50 的 DLL 混用。优先级为：
    /// 显式路径、mods 覆盖、DLSS5_GPU_SERIES 对应子目录、runtime 根目录激活版、
    /// 按显卡代际自动匹配 nr\ 变体（版本低者优先）、nr\ 最低版本。
    /// 完整转码必须从根目录加载（驱动 617.14 下 nr\ 深层路径在会话 teardown 会挂起），
    /// 根目录应放置本机适配版本；nr\ 仅作版本库与显式手动选择。
    /// </summary>
    public static string RuntimeDllPath => FindDlssRuntimeDll();
    public static string? RuntimeProfile => ReadOptionalEnvironment("DLSS5_GPU_SERIES");
    public static string VsrHostDllPath => Path.Combine(RuntimeDir, "vsr_host.dll");
    public static string VsrRuntimeDllPath => Path.Combine(RuntimeDir, "nvngx_vsr.dll");
    public static string LogPath => Path.Combine(RuntimeDir, "dlss_run.log");
    public static string VsrLogPath => Path.Combine(RuntimeDir, "vsr_run.log");

    private static string FindRuntimeDir()
    {
        var candidates = new List<string>();
        var environment = Environment.GetEnvironmentVariable("DLSS5_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(environment))
            candidates.Add(environment);

        // Either host identifies a runtime directory; VSR-only installs need no NR binaries.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current != null; current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "runtime"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "runtime"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "dlssnr_host_v2.dll"))
                || File.Exists(Path.Combine(candidate, "vsr_host.dll"))
                || File.Exists(Path.Combine(candidate, "driver_hdr_host.dll"))
                || File.Exists(Path.Combine(candidate, "rife_v4.25_lite_v2.onnx"))
                || File.Exists(Path.Combine(candidate, "rife_v4.25_v2.onnx")))
                return Path.GetFullPath(candidate);
        }
        // 未找到时返回首选候选，让调用方收到明确的 FileNotFoundException。
        return Path.GetFullPath(candidates[^1]);
    }

    private static string FindDlssRuntimeDll()
    {
        var configured = ReadOptionalEnvironment("DLSS5_NR_RUNTIME_DLL");
        if (configured != null)
            return Path.GetFullPath(configured);

        // 与 DLSS5Tool 的 mods 覆盖方式保持一致：可在不覆盖内置 DLL 的情况下
        // 放入 RTX 30/40/50 对应版本。优先应用目录，再查 runtime 的父目录。
        foreach (var directory in EnumerateOverrideDirectories())
        {
            var candidate = Path.Combine(directory, "nvngx_dlssnr.dll");
            if (File.Exists(candidate))
                return candidate;
        }

        var profile = RuntimeProfile;
        if (profile != null)
        {
            // nr\ 下按系列命名的版本目录（当前布局），以及 runtime 根的同名目录（旧布局兼容）。
            foreach (var directory in new[] { Path.Combine(RuntimeDir, "nr"), RuntimeDir })
            {
                var profileCandidate = Path.Combine(directory, profile, "nvngx_dlssnr.dll");
                if (File.Exists(profileCandidate))
                    return Path.GetFullPath(profileCandidate);
            }
            var suffixedCandidate = Path.Combine(RuntimeDir, $"nvngx_dlssnr_{profile}.dll");
            if (File.Exists(suffixedCandidate))
                return Path.GetFullPath(suffixedCandidate);
        }

        // 根目录是激活位：放本机显卡适配的版本，优先于代际匹配（nr\ 深层路径
        // 在完整转码的会话 teardown 会挂起，见类注释）。
        var rootDefault = Path.Combine(RuntimeDir, "nvngx_dlssnr.dll");
        if (File.Exists(rootDefault))
            return Path.GetFullPath(rootDefault);

        // 根目录未放置时的兜底：按本机 RTX 代际在 nr\ 下挑选能跑的变体（版本
        // 低者优先），再退回 nr\ 全场最低版本；都没有则交由加载方报缺文件。
        if (NrGpuAutoSelect.DetectSeries() is { } series &&
            NrGpuAutoSelect.SelectForSeries(series, RuntimeDir) is { } matched)
        {
            PipelineLog.Info($"NR 自动匹配 RTX {series}：{Path.GetFullPath(matched)}");
            return Path.GetFullPath(matched);
        }
        return NrGpuAutoSelect.SelectLowestVariant(RuntimeDir) ?? rootDefault;
    }

    internal static IEnumerable<string> EnumerateOverrideDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current != null; current = current.Parent)
        {
            var mods = Path.Combine(current.FullName, "mods");
            if (seen.Add(mods))
                yield return mods;
        }

        var runtimeParent = Directory.GetParent(RuntimeDir)?.FullName;
        if (runtimeParent != null)
        {
            var mods = Path.Combine(runtimeParent, "mods");
            if (seen.Add(mods))
                yield return mods;
        }

        var runtimeMods = Path.Combine(RuntimeDir, "mods");
        if (seen.Add(runtimeMods))
            yield return runtimeMods;
    }

    private static string? ReadOptionalEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

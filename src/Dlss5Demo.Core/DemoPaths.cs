namespace Dlss5Demo.Core;

/// <summary>定位 demo 的 runtime 目录（宿主 DLL 与 NVIDIA 运行库所在处）。</summary>
public static class DemoPaths
{
    public static string RuntimeDir { get; } = FindRuntimeDir();

    public static string HostDllPath => Path.Combine(RuntimeDir, "dlssnr_host_v2.dll");
    public static string RuntimeDllPath => Path.Combine(RuntimeDir, "nvngx_dlssnr.dll");
    public static string VsrHostDllPath => Path.Combine(RuntimeDir, "vsr_host.dll");
    public static string VsrRuntimeDllPath => Path.Combine(RuntimeDir, "nvngx_vsr.dll");
    public static string LogPath => Path.Combine(RuntimeDir, "dlss_run.log");
    public static string VsrLogPath => Path.Combine(RuntimeDir, "vsr_run.log");
    public static string FfmpegPath => Environment.GetEnvironmentVariable("DLSS5_FFMPEG") ?? "ffmpeg";
    public static string FfprobePath
    {
        get
        {
            var ffmpeg = FfmpegPath;
            if (string.Equals(Path.GetFileNameWithoutExtension(ffmpeg), "ffmpeg", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe" + Path.GetExtension(ffmpeg));
            return "ffprobe";
        }
    }

    private static string FindRuntimeDir()
    {
        var candidates = new List<string>();
        var environment = Environment.GetEnvironmentVariable("DLSS5_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(environment))
            candidates.Add(environment);

        // 从应用目录逐级向上找 runtime\dlssnr_host_v2.dll（bin\Debug\net10.0 → ... → 仓库根）。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current != null; current = current.Parent)
            candidates.Add(Path.Combine(current.FullName, "runtime"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "runtime"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "dlssnr_host_v2.dll")))
                return Path.GetFullPath(candidate);
        }
        // 未找到时返回首选候选，让调用方收到明确的 FileNotFoundException。
        return Path.GetFullPath(candidates[^1]);
    }
}

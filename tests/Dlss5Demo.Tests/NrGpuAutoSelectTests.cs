using Dlss5Demo.Core;

internal static class NrGpuAutoSelectTests
{
    internal static void SeriesMapping()
    {
        // 消费级 GeForce 各代代表型号（含本机实测 5070 Ti = 0x2C05）。
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x1E02) == 20, "RTX 2080 Ti must map to series 20.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x1F07) == 20, "RTX 2060 must map to series 20.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2204) == 30, "RTX 3090 must map to series 30.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2484) == 30, "RTX 3070 must map to series 30.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2503) == 30, "RTX 3060 must map to series 30.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2684) == 40, "RTX 4090 must map to series 40.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2882) == 40, "RTX 4060 must map to series 40.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2B85) == 50, "RTX 5090 must map to series 50.");
        Require(NrGpuAutoSelect.SeriesFromDeviceId(0x2C05) == 50, "RTX 5070 Ti must map to series 50.");
        // 段边界归属明确，段间空档与未知设备返回 null（回退默认运行库）。
        foreach (var (device, series) in new[] { (0x1FFFu, 20), (0x2200u, 30), (0x25FFu, 30),
                     (0x2600u, 40), (0x28FFu, 40), (0x2B00u, 50), (0x2FFFu, 50) })
            Require(NrGpuAutoSelect.SeriesFromDeviceId(device) == series, $"Device {device:X4} must map to series {series}.");
        foreach (var unknown in new[] { 0x2100u, 0x2900u, 0x3000u, 0x0FFFu })
            Require(NrGpuAutoSelect.SeriesFromDeviceId(unknown) is null, $"Unknown device {unknown:X4} must not map to a series.");
    }

    internal static void SeriesTokens()
    {
        var community = "Community-Patched-310.9.16-RTX20-RTX30-RTX40-Series";
        foreach (var series in new[] { 20, 30, 40 })
            Require(NrGpuAutoSelect.SupportsSeries(community, series), $"{community} must declare RTX {series}.");
        Require(!NrGpuAutoSelect.SupportsSeries(community, 50), "RTX 50 must not be claimed by the 20-40 variant.");
        var original = "NVIDIA-Original-310.8.0-RTX50";
        Require(NrGpuAutoSelect.SupportsSeries(original, 50) && !NrGpuAutoSelect.SupportsSeries(original, 40),
            "Series tokens must match exactly.");
        Require(!NrGpuAutoSelect.SupportsSeries("Quadro-RTX5000", 50), "RTX5000 must not be mistaken for RTX 50.");
        Require(NrGpuAutoSelect.SupportsSeries("rtx 40", 40), "Tokens must ignore case and spacing.");
        Require(!NrGpuAutoSelect.SupportsSeries("Community-SF-v2-310.8", 20), "Folders without tokens must not match.");
    }

    internal static void AutoSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "nr-auto-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime");
        var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 镜像真实 runtime 布局：根目录是激活版（DemoPaths 层优先，SelectForSeries 不读它），
            // nr\ 下变体含社区/原版混排（保留原版 310.8.0 覆盖同版本平局按路径升序的分支）。
            Put("runtime/nvngx_dlssnr.dll", new Version(310, 8, 0, 0));
            Put("runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll", new Version(310, 9, 16, 0));
            Put("runtime/nr/Community-Patched-310.9.16-RTX20-RTX30-RTX40-Series/nvngx_dlssnr.dll", new Version(310, 9, 16, 0));
            Put("runtime/nr/NVIDIA-Original-310.8.0-RTX50/nvngx_dlssnr.dll", new Version(310, 8, 0, 0));
            Put("runtime/nr/Community-Patched-310.8.0-RTX40-RTX50-Series/nvngx_dlssnr.dll", new Version(310, 8, 0, 0));
            Put("runtime/nr/Community-SF-v2-310.8-RTX20-RTX30-Series/nvngx_dlssnr.dll", new Version(310, 8, 2, 0));

            string? Selected(int series) => NrGpuAutoSelect.SelectForSeries(series, runtime, Read);
            string? Lowest() => NrGpuAutoSelect.SelectLowestVariant(runtime, Read);
            string Folder(int series) => Path.GetFileName(Path.GetDirectoryName(Selected(series))!)!;
            string LowestFolder() => Path.GetFileName(Path.GetDirectoryName(Lowest())!)!;
            // 版本低者优先：310.8 族覆盖的代际全部选 310.8，不被 310.9.16 抢走；
            // 50 系同版本平局（社区 vs 原版 310.8.0.0）按路径升序取社区版。
            Require(Folder(50) == "Community-Patched-310.8.0-RTX40-RTX50-Series",
                "RTX 50 must select the lowest version (310.8 community) over 310.9.16.");
            Require(Folder(40) == "Community-Patched-310.8.0-RTX40-RTX50-Series", "RTX 40 must select the 310.8 community build.");
            foreach (var series in new[] { 20, 30 })
                Require(Folder(series) == "Community-SF-v2-310.8-RTX20-RTX30-Series",
                    $"RTX {series} must select the 310.8 SF build.");
            Require(Selected(60) is null, "Series without a runnable variant must fall back (null).");
            Require(NrGpuAutoSelect.SelectForSeries(null, runtime, Read) is null, "Undetectable series must fall back.");
            // 回退选择不限代际，取 nr\ 全场最低版本（根目录文件不参与）。
            Require(LowestFolder() == "Community-Patched-310.8.0-RTX40-RTX50-Series",
                "Fallback must pick the lowest variant regardless of GPU series.");
            // 新增更低版本立即胜出，确认比较方向是低版本优先。
            Put("runtime/nr/Old-310.7.0-RTX50/nvngx_dlssnr.dll", new Version(310, 7, 0, 0));
            Require(Folder(50) == "Old-310.7.0-RTX50", "A newly added lower version must win.");
            Require(LowestFolder() == "Old-310.7.0-RTX50", "Fallback must follow the newly added lowest version.");
            // 选择与根目录默认无关：换版本、删根目录都不改变自动匹配结果。
            versions[Path.GetFullPath(Path.Combine(runtime, "nvngx_dlssnr.dll"))] = new Version(310, 9, 16, 0);
            Require(Folder(40) == "Community-Patched-310.8.0-RTX40-RTX50-Series", "Root default version must not affect selection.");
            File.Delete(Path.Combine(runtime, "nvngx_dlssnr.dll"));
            versions.Remove(Path.GetFullPath(Path.Combine(runtime, "nvngx_dlssnr.dll")));
            Require(Folder(30) == "Community-SF-v2-310.8-RTX20-RTX30-Series", "A missing root default must not affect selection.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        void Put(string relative, Version version)
        {
            // GetFullPath 归一化分隔符，与目录枚举返回的键一致。
            var path = Path.GetFullPath(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "stub");
            versions[path] = version;
        }

        Version? Read(string path) => versions.TryGetValue(path, out var version) ? version : null;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

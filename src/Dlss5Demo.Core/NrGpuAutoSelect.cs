using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dlss5Demo.Core;

/// <summary>
/// 「自动」运行库的显卡匹配：检测本机 RTX 代际（20/30/40/50），在 runtime\nr\ 下
/// 挑选目录名声明支持该系列的变体，版本低者优先（保守的已验证版本优先）；
/// 找不到或无法检测代际时由调用方回退根目录默认。
/// </summary>
public static partial class NrGpuAutoSelect
{
    private static int? _detectedSeries;
    private static bool _detectionAttempted;

    /// <summary>检测本机 RTX 代际（显存最大的 NVIDIA 适配器）；枚举失败或非已知代际返回 null。结果缓存。</summary>
    public static int? DetectSeries()
    {
        if (_detectionAttempted)
            return _detectedSeries;
        _detectionAttempted = true;
        try
        {
            var primary = DlssNrHost.EnumerateAdapters(DemoPaths.HostDllPath)
                .OrderByDescending(adapter => adapter.DedicatedVideoMemory)
                .FirstOrDefault();
            _detectedSeries = primary is null ? null : SeriesFromDeviceId(primary.DeviceId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            PipelineLog.Info($"NR 显卡代际检测失败，按默认运行库处理：{error.Message}");
            _detectedSeries = null;
        }
        return _detectedSeries;
    }

    /// <summary>消费级 GeForce PCI 设备号段 → RTX 代际；未知段返回 null（调用方回退默认）。</summary>
    internal static int? SeriesFromDeviceId(uint deviceId) => deviceId switch
    {
        >= 0x1E00 and <= 0x1FFF => 20, // Turing（RTX 20；GTX 16 同段，不适用时由 NGX 初始化报错）
        >= 0x2200 and <= 0x25FF => 30, // Ampere GA10x（RTX 30）
        >= 0x2600 and <= 0x28FF => 40, // Ada AD10x（RTX 40）
        >= 0x2B00 and <= 0x2FFF => 50, // Blackwell GB20x（RTX 50）
        _ => null,
    };

    [GeneratedRegex(@"RTX\s*0*(\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuSeriesPattern();

    /// <summary>目录名里的 RTX 代际标记（如 RTX20-RTX30-RTX40 / RTX50）须完整匹配；RTX5000 ≠ RTX50。</summary>
    internal static bool SupportsSeries(string folderName, int series)
    {
        foreach (Match match in GpuSeriesPattern().Matches(folderName))
            if (int.TryParse(match.Groups[1].Value, out var token) && token == series)
                return true;
        return false;
    }

    /// <summary>
    /// 在 runtimeDirectory\nr\ 下挑选适配 series 的 nvngx_dlssnr.dll（递归，目录名须含
    /// RTX&lt;系列&gt; 标记）。版本低者优先，版本未知排最后；平局保留路径升序先出现者。
    /// 找不到或无法检测代际时返回 null。
    /// </summary>
    public static string? SelectForSeries(int? series, string runtimeDirectory) =>
        SelectForSeries(series, runtimeDirectory, ReadFileVersion);

    /// <summary>回退选择：nr\ 下版本最低的变体（不限代际，与自动档低版本口径一致）；无变体返回 null。</summary>
    public static string? SelectLowestVariant(string runtimeDirectory) =>
        SelectLowestVariant(runtimeDirectory, ReadFileVersion);

    internal static string? SelectForSeries(int? series, string runtimeDirectory, Func<string, Version?> readVersion)
    {
        if (series is not { } detected)
            return null;
        return FoldByVersion(EnumerateVariants(runtimeDirectory)
            .Where(variant => SupportsSeries(variant.Folder, detected)), readVersion);
    }

    internal static string? SelectLowestVariant(string runtimeDirectory, Func<string, Version?> readVersion) =>
        FoldByVersion(EnumerateVariants(runtimeDirectory), readVersion);

    // nr\ 下的全部 nvngx_dlssnr.dll 变体，按路径升序排序（与文件系统枚举顺序解耦）。
    // Folder 为相对 nr\ 的目录名（支持嵌套），用于代际标记匹配。
    private static List<(string File, string Folder)> EnumerateVariants(string runtimeDirectory)
    {
        var variantsRoot = Path.Combine(runtimeDirectory, "nr");
        var candidates = new List<(string File, string Folder)>();
        if (!Directory.Exists(variantsRoot))
            return candidates;
        try
        {
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive };
            foreach (string file in Directory.EnumerateFiles(variantsRoot, "nvngx_dlssnr.dll", enumeration))
            {
                var folder = Path.GetDirectoryName(Path.GetRelativePath(variantsRoot, file));
                if (!string.IsNullOrEmpty(folder))
                    candidates.Add((file, folder));
            }
        }
        catch (IOException) { /* 刷新期间目录可能被移走。 */ }
        catch (UnauthorizedAccessException) { }
        candidates.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File));
        return candidates;
    }

    private static string? FoldByVersion(IEnumerable<(string File, string Folder)> candidates,
        Func<string, Version?> readVersion)
    {
        string? bestPath = null;
        Version? bestVersion = null;
        foreach (var (file, _) in candidates)
        {
            var version = readVersion(file);
            if (bestPath is null || Prefer(version, bestVersion))
            {
                bestPath = file;
                bestVersion = version;
            }
        }
        return bestPath;
    }

    // 版本低者优先（保守的已验证版本胜出）；版本未知排最后，已知胜未知。
    internal static bool Prefer(Version? version, Version? incumbentVersion)
    {
        if (version is null || incumbentVersion is null)
            return incumbentVersion is null && version is not null;
        return version < incumbentVersion;
    }

    private static Version? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart is 0 && info.FileMinorPart is 0 ? null
                : new Version(info.FileMajorPart, info.FileMinorPart,
                    Math.Max(0, info.FileBuildPart), Math.Max(0, info.FilePrivatePart));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

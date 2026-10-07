using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>DLSS NR 外观参数（对应上游 dlss5tool 的公开设置）。</summary>
public sealed record DlssNrOptions
{
    /// <summary>风格索引（0 起，由运行库定义）。</summary>
    public int Style { get; init; }

    /// <summary>降噪/增强强度 0..20，1.0 为默认。</summary>
    public float Intensity { get; init; } = 1.0f;

    /// <summary>局部色调强度 0..20。</summary>
    public float LocalTone { get; init; } = 1.0f;

    /// <summary>局部结构强度 0..20。</summary>
    public float LocalStructure { get; init; } = 1.0f;

    /// <summary>皮肤结构强度 0..20（配合自动掩码生效）。</summary>
    public float SkinStructure { get; init; } = 0.5f;

    /// <summary>启用自动掩码（保护人脸/皮肤区域）。</summary>
    public bool UseAutoMask { get; init; }
}

public sealed record GpuAdapter
{
    public required string Description { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public ulong DedicatedVideoMemory { get; init; }
    public bool IsNvidia => VendorId == 0x10DE;
}

/// <summary>
/// dlssnr_host_v2.dll（源自 https://github.com/banbanzhige/DLSS5Tool，MIT）的托管封装。
/// 一次 Init/CreateFeature，随后逐帧 Process；帧契约为 RGBA8、宽高与输入一致。
/// 该类非线程安全：所有调用须串行化。
/// </summary>
public sealed class DlssNrHost : IDisposable
{
    private const string DllName = "dlssnr_host_v2.dll";
    private const int NvidiaVendorId = 0x10DE;
    private const int DefaultRuntimeHint = 0;
    private const uint AdapterSoftware = 1u;
    private const uint AdapterD3D12Level11 = 2u;

    private IntPtr _library;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAdapterInfo
    {
        public uint StructSize;
        public uint PreferenceIndex;
        public uint VendorId;
        public uint DeviceId;
        public uint SubsysId;
        public uint Revision;
        public uint Flags;
        public ulong DedicatedVideoMemory;
        public int LuidHigh;
        public uint LuidLow;
        public unsafe fixed byte Description[256]; // wchar_t[128]
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dlssnr_init(int width, int height, int preset,
        [MarshalAs(UnmanagedType.LPWStr)] string runtimePath,
        [MarshalAs(UnmanagedType.LPWStr)] string logPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dlssnr_create_feature(int width, int height, int preset);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dlssnr_set_options(int preset, int style, float intensity,
        float localTone, float localStruct, float skinStruct, int useAutoMask,
        int uiCorrection, int guidanceMode, int depthConvention,
        float motionScaleX, float motionScaleY);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dlssnr_configure_format(int formatId, int profileId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int dlssnr_process(void* color, void* motion, void* depth,
        void* output, int reset);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dlssnr_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int dlssnr_enumerate_adapters(NativeAdapterInfo* items, int capacity);

    private DlssNrHost(IntPtr library, int width, int height)
    {
        _library = library;
        _width = width;
        _height = height;
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>列出可用图形适配器（无需初始化 NGX）。</summary>
    public static IReadOnlyList<GpuAdapter> EnumerateAdapters(string hostDllPath)
    {
        var library = LoadHost(hostDllPath);
        try
        {
            var count = 0;
            var records = new List<GpuAdapter>();
            unsafe
            {
                count = dlssnr_enumerate_adapters(null, 0);
                if (count is < 0 or > 64)
                    throw new InvalidOperationException("DLSS 宿主返回了无效的适配器数量。");
                if (count == 0)
                    return Array.Empty<GpuAdapter>();
                var items = new NativeAdapterInfo[count];
                for (var index = 0; index < count; index++)
                    items[index].StructSize = (uint)sizeof(NativeAdapterInfo);
                fixed (NativeAdapterInfo* pointer = items)
                {
                    var returned = dlssnr_enumerate_adapters(pointer, count);
                    if (returned < 0)
                        throw new InvalidOperationException("DLSS 宿主无法枚举图形适配器。");
                    for (var index = 0; index < Math.Min(returned, count); index++)
                    {
                        var item = &pointer[index];
                        if (item->VendorId != NvidiaVendorId || (item->Flags & AdapterSoftware) != 0 ||
                            (item->Flags & AdapterD3D12Level11) == 0)
                            continue;
                        records.Add(new GpuAdapter
                        {
                            Description = Marshal.PtrToStringUni((IntPtr)item->Description) ?? "",
                            VendorId = item->VendorId,
                            DeviceId = item->DeviceId,
                            DedicatedVideoMemory = item->DedicatedVideoMemory,
                        });
                    }
                }
            }
            return records;
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>
    /// 初始化 NGX 会话。runtimeDllPath 为 nvngx_dlssnr.dll 的完整路径（须与本机显卡系列匹配）。
    /// </summary>
    public static DlssNrHost Create(string hostDllPath, string runtimeDllPath, string logPath,
        int width, int height, DlssNrOptions options, bool hdr = false, string colorTransfer = "")
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "帧尺寸无效。");
        if (!File.Exists(hostDllPath))
            throw new FileNotFoundException("未找到 dlssnr_host_v2.dll。", hostDllPath);
        if (!File.Exists(runtimeDllPath))
            throw new FileNotFoundException(
                "未找到 nvngx_dlssnr.dll。请按显卡系列（RTX 30/40/50）下载对应版本并放入 runtime、mods，" +
                "或设置 DLSS5_NR_RUNTIME_DLL 指向匹配的 DLL。",
                runtimeDllPath);

        options = NormalizeOptions(options);
        var library = LoadHost(hostDllPath);
        try
        {
            if (hdr)
                dlssnr_configure_format(1, colorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase) ? 3 : 2);
            // 运行库要求在 create 之前推送默认 hint；NR 不暴露 SR preset 选择。
            dlssnr_set_options(DefaultRuntimeHint, options.Style, options.Intensity,
                options.LocalTone, options.LocalStructure, options.SkinStructure,
                options.UseAutoMask ? 1 : 0, 0, 0, 2, 1.0f, 1.0f);
            if (dlssnr_init(width, height, DefaultRuntimeHint, runtimeDllPath, logPath) == 0)
            {
                var tail = ReadLogTail(logPath, 1200);
                throw new InvalidOperationException(
                    "dlssnr_init 失败（D3D12 / NGX 初始化被拒绝）。dlss_run.log 末尾：\n" + tail);
            }
            if (dlssnr_create_feature(width, height, DefaultRuntimeHint) == 0)
            {
                var tail = ReadLogTail(logPath, 1200);
                throw new InvalidOperationException("创建 Feature 18 失败。dlss_run.log 末尾：\n" + tail);
            }
            return new DlssNrHost(library, width, height) { _options = options };
        }
        catch
        {
            try { dlssnr_shutdown(); } catch { /* 尽力清理 */ }
            NativeLibrary.Free(library);
            throw;
        }
    }

    private DlssNrOptions _options = new();

    /// <summary>更新外观参数；对下一次 ProcessFrame 生效（preset 变更除外，需重建会话）。</summary>
    public void UpdateOptions(DlssNrOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options = NormalizeOptions(options);
        _options = options;
        dlssnr_set_options(DefaultRuntimeHint, options.Style, options.Intensity,
            options.LocalTone, options.LocalStructure, options.SkinStructure,
            options.UseAutoMask ? 1 : 0, 0, 0, 2, 1.0f, 1.0f);
    }

    private static DlssNrOptions NormalizeOptions(DlssNrOptions options) => options with
    {
        Intensity = Math.Clamp(options.Intensity, 0f, 20f),
        LocalTone = Math.Clamp(options.LocalTone, 0f, 20f),
        LocalStructure = Math.Clamp(options.LocalStructure, 0f, 20f),
        SkinStructure = Math.Clamp(options.SkinStructure, 0f, 20f),
    };

    /// <summary>
    /// 处理一帧（RGBA8，行长 width*4）。零引导路径：motion/depth 传空。
    /// reset 置位时清除 DLSS 时序历史（首帧或切镜后使用）。
    /// </summary>
    public unsafe bool ProcessFrame(byte* colorRgba8, byte* outputRgba8, bool reset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return dlssnr_process(colorRgba8, null, null, outputRgba8, reset ? 1 : 0) != 0;
    }

    /// <summary>处理一帧 RGBA16F，缓冲区按 Half 位模式排列。</summary>
    public unsafe bool ProcessFrame(ushort* colorRgba16F, ushort* outputRgba16F, bool reset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return dlssnr_process(colorRgba16F, null, null, outputRgba16F, reset ? 1 : 0) != 0;
    }

    private static IntPtr LoadHost(string hostDllPath)
    {
        var resolved = Path.GetFullPath(hostDllPath);
        // 显式按路径加载一次；后续 DllImport 按模块名复用已加载实例。
        return NativeLibrary.Load(resolved);
    }

    private static string ReadLogTail(string path, int limit)
    {
        try
        {
            if (!File.Exists(path))
                return "(无日志)";
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            if (length > limit)
                stream.Seek(-limit, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception error)
        {
            return $"(读取日志失败: {error.Message})";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { dlssnr_shutdown(); } catch { /* 关闭阶段尽力而为 */ }
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }
}

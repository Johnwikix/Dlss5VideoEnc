using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>托管包装 RTX Video Super Resolution 原生宿主。</summary>
public sealed class RtxVideoSuperResolutionHost : IDisposable
{
    private const string DllName = "vsr_host.dll";
    private IntPtr _library;
    private bool _disposed;
    public int QueueCapacity { get; }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vsr_init(int inputWidth, int inputHeight, int scale, int quality, int hdr,
        [MarshalAs(UnmanagedType.LPWStr)] string runtimeDirectory,
        [MarshalAs(UnmanagedType.LPWStr)] string logPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int vsr_process(void* input, void* output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vsr_queue_capacity();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int vsr_enqueue(void* input);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int vsr_dequeue(void* output, int preview);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int vsr_encoder_init(string libraryPath, string outputPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string codec, [MarshalAs(UnmanagedType.LPUTF8Str)] string options,
        int fpsNumerator, int fpsDenominator, int previewWidth, int previewHeight);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vsr_encoder_finish();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vsr_shutdown();

    private RtxVideoSuperResolutionHost(IntPtr library, int width, int height, int scale, bool hdr)
    {
        _library = library;
        QueueCapacity = NativeLibrary.TryGetExport(library, "vsr_queue_capacity", out _)
            && NativeLibrary.TryGetExport(library, "vsr_enqueue", out _)
            && NativeLibrary.TryGetExport(library, "vsr_dequeue", out _) ? Math.Clamp(vsr_queue_capacity(), 1, 3) : 1;
        InputWidth = width;
        InputHeight = height;
        Scale = scale;
        IsHdr = hdr;
        OutputWidth = checked(width * scale);
        OutputHeight = checked(height * scale);
    }

    public int InputWidth { get; }
    public int InputHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int Scale { get; }
    public bool IsHdr { get; }

    public static RtxVideoSuperResolutionHost Create(
        string hostDllPath, string runtimeDllPath, string logPath,
        int width, int height, int scale, bool hdr, int quality = 4)
    {
        scale = NormalizeScale(scale);
        if (scale == 1)
            throw new ArgumentOutOfRangeException(nameof(scale), "RTX Video 超分会话只接受 2× 或 4×。");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "视频尺寸无效。");
        if (!File.Exists(hostDllPath))
            throw new FileNotFoundException("未找到 vsr_host.dll。请按 native/vsr_host/build.bat 构建 RTX Video 宿主。", hostDllPath);
        if (!File.Exists(runtimeDllPath))
            throw new FileNotFoundException("未找到 nvngx_vsr.dll。请安装 RTX Video SDK 运行库并放入 runtime 目录。", runtimeDllPath);

        var library = NativeLibrary.Load(Path.GetFullPath(hostDllPath));
        try
        {
            var runtimeDirectory = Path.GetDirectoryName(Path.GetFullPath(runtimeDllPath)) ?? ".";
            if (vsr_init(width, height, scale, Math.Clamp(quality, 1, 4), hdr ? 1 : 0,
                    runtimeDirectory, logPath) == 0)
            {
                var tail = ReadLogTail(logPath, 1600);
                throw new InvalidOperationException(
                    "RTX Video Super Resolution 初始化失败。" +
                    (string.IsNullOrWhiteSpace(tail) ? "" : "\nVSR 日志末尾：\n" + tail));
            }
            PipelineLog.Info($"RTX VSR 会话已创建: {width}×{height} → {width * scale}×{height * scale} hdr={hdr}");
            return new RtxVideoSuperResolutionHost(library, width, height, scale, hdr);
        }
        catch
        {
            try { vsr_shutdown(); } catch { }
            NativeLibrary.Free(library);
            throw;
        }
    }

    public unsafe bool ProcessFrame(byte* input, byte* output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return vsr_process(input, output) != 0;
    }

    public unsafe bool ProcessFrame(ushort* input, ushort* output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return vsr_process(input, output) != 0;
    }

    internal unsafe bool EnqueueFrame(byte* input) => vsr_enqueue(input) != 0;
    internal unsafe bool DequeueFrame(byte* output, bool preview) => vsr_dequeue(output, preview ? 1 : 0) != 0;
    internal bool EnableEncoder(string libraryPath, VsrEncoderConfiguration configuration)
        => NativeLibrary.TryGetExport(_library, "vsr_encoder_init", out _)
           && vsr_encoder_init(libraryPath, configuration.OutputPath, configuration.Encoder, configuration.Options,
               configuration.FpsNumerator, configuration.FpsDenominator,
               configuration.PreviewWidth, configuration.PreviewHeight) != 0;
    internal bool FinishEncoding() => vsr_encoder_finish() != 0;

    public static int NormalizeScale(int value) => value is 2 or 4 ? value : 1;

    private static string ReadLogTail(string path, int limit)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > limit) stream.Seek(-limit, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { vsr_shutdown(); } catch { }
        PipelineLog.Info("RTX VSR 会话已释放。");
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }
}

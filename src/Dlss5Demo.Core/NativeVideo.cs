using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>只加载 runtime 中一套 ABI 匹配的共享库，句柄保持到进程结束。</summary>
internal static unsafe class NativeVideo
{
    private static readonly Lazy<nint[]> Libraries = new(() =>
    {
        string[] names = ["avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll", "avfilter-12.dll"];
        var handles = new nint[names.Length];
        for (int i = 0; i < names.Length; i++) handles[i] = NativeLibrary.Load(File.Exists(Path.Combine(DemoPaths.RuntimeDir, names[i]))
            ? Path.Combine(DemoPaths.RuntimeDir, names[i]) : Path.Combine(AppContext.BaseDirectory, names[i]));
        ffmpeg.RootPath = File.Exists(Path.Combine(DemoPaths.RuntimeDir, "avutil-61.dll")) ? DemoPaths.RuntimeDir : AppContext.BaseDirectory;
        return handles;
    });
    internal static void Initialize() { _ = Libraries.Value; _ = ffmpeg.av_version_info(); }
    internal static void Check(int result, string action)
    {
        if (result >= 0) return;
        byte* error = stackalloc byte[512];
        ffmpeg.av_strerror(result, error, 512);
        throw new InvalidOperationException($"{action}: {Marshal.PtrToStringUTF8((nint)error)} ({result})");
    }
    internal const int Again = -11;
}

internal abstract class InterpolationFrameStream : Stream
{
    internal abstract void Complete();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

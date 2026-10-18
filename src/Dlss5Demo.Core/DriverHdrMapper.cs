using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>Owns one installed-driver D3D11 TrueHDR session; callers serialize frame processing.</summary>
internal sealed unsafe class DriverHdrMapper : IDisposable
{
    private nint _library, _session;
    private readonly delegate* unmanaged[Cdecl]<nint, void*, void*, int> _process;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly byte[] _output;
    internal int FrameBytes => _output.Length;
    internal long Frames { get; private set; }

    internal DriverHdrMapper(int width, int height)
    {
        _output = new byte[checked(width * height * 4)];
        var path = Path.Combine(DemoPaths.RuntimeDir, "driver_hdr_host.dll");
        if (!File.Exists(path)) throw new FileNotFoundException(
            "强制 HDR 需要 driver_hdr_host.dll，请运行 native/vsr_host/build-hdr.bat。", path);
        _library = NativeLibrary.Load(path);
        try
        {
            var create = (delegate* unmanaged[Cdecl]<uint, uint, nint*, int>)NativeLibrary.GetExport(_library, "driver_hdr_create");
            _process = (delegate* unmanaged[Cdecl]<nint, void*, void*, int>)NativeLibrary.GetExport(_library, "driver_hdr_process");
            _destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "driver_hdr_destroy");
            nint session = 0;
            int hr = create((uint)width, (uint)height, &session);
            if (hr < 0) throw new NotSupportedException($"驱动 RTX Video HDR 初始化失败 (0x{hr:X8})。需要支持 TrueHDR 的 NVIDIA RTX GPU 与驱动。");
            _session = session;
            PipelineLog.Info($"驱动 RTX Video HDR 已启用：{width}×{height}，补帧后 RGBA8/SDR → RGB10/BT.2020/PQ；离屏 D3D11。");
        }
        catch { Dispose(); throw; }
    }

    internal void Write(ReadOnlySpan<byte> frame, Stream destination)
    {
        ObjectDisposedException.ThrowIf(_session == 0, this);
        if (frame.Length != FrameBytes) throw new ArgumentException("TrueHDR 必须处理整帧 RGBA8。");
        fixed (byte* input = frame, output = _output)
        {
            int hr = _process(_session, input, output);
            if (hr < 0) throw new InvalidOperationException($"驱动 RTX Video HDR 第 {Frames + 1} 帧映射失败 (0x{hr:X8})。");
        }
        destination.Write(_output);
        Frames++;
    }

    public void Dispose()
    {
        if (_session != 0) { _destroy(_session); _session = 0; }
        if (_library != 0) { NativeLibrary.Free(_library); _library = 0; }
    }
}

/// <summary>Placed downstream of interpolation, including its delayed tail frames. Leaves the destination open.</summary>
internal sealed class DriverHdrFrameStream(DriverHdrMapper mapper, Stream destination, CancellationToken ct) : Stream
{
    private readonly byte[] _partial = new byte[mapper.FrameBytes];
    private int _pending;
    internal void Complete()
    {
        if (_pending != 0) throw new InvalidOperationException("TrueHDR 输入在半帧处结束。");
        PipelineLog.Info($"驱动 RTX Video HDR 映射完成：{mapper.Frames} 输出帧（包含补帧与尾帧）。");
    }
    public override void Write(ReadOnlySpan<byte> pixels)
    {
        while (!pixels.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            if (_pending == 0 && pixels.Length >= mapper.FrameBytes)
            {
                mapper.Write(pixels[..mapper.FrameBytes], destination);
                pixels = pixels[mapper.FrameBytes..];
            }
            else
            {
                int count = Math.Min(pixels.Length, _partial.Length - _pending);
                pixels[..count].CopyTo(_partial.AsSpan(_pending));
                _pending += count; pixels = pixels[count..];
                if (_pending == _partial.Length) { mapper.Write(_partial, destination); _pending = 0; }
            }
        }
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() => destination.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

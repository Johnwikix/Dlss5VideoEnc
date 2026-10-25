namespace Dlss5Demo.Core;

/// <summary>NGX stays isolated in the VSR worker; only handles/status cross the pipe.</summary>
internal sealed class GpuVsrProcessor : IDisposable
{
    private readonly CudaVideoContext _cuda;
    private readonly RtxVideoWorker _worker;
    private readonly CudaVideoContext.SharedBuffer[] _shared;
    private readonly int _width, _height, _scale;
    private bool _disposed;

    internal GpuVsrProcessor(CudaVideoContext cuda, RtxVideoWorker worker, int width, int height, int scale)
    {
        _cuda = cuda; _worker = worker; _width = width; _height = height; _scale = scale;
        _shared = worker.ImportGpuFrames(cuda);
    }

    internal void Process(CudaVideoFrame input, CudaVideoFrame output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Width != _width || input.Height != _height || output.Width != _width * _scale || output.Height != _height * _scale)
            throw new ArgumentException("VSR 设备帧尺寸不符。");
        _cuda.SwapRedBlue(input.Data, input.Pitch, _shared[0].Data, _shared[0].Pitch, input.Width, input.Height);
        // CUDA stream completion above and worker's D3D12 fence below protect
        // both directions, including slot reuse. No CPU mapping/copy of pixels.
        _worker.ProcessGpuFrame();
        _cuda.SwapRedBlue(_shared[1].Data, _shared[1].Pitch, output.Data, output.Pitch, output.Width, output.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shared[1].Dispose(); }
        finally { _shared[0].Dispose(); }
    }
}

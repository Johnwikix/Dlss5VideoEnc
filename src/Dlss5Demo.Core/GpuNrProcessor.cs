using System.Runtime.Intrinsics.X86;

namespace Dlss5Demo.Core;

/// <summary>
/// One stage owns NVOFA, CUDA histories and the NR host. CUDA and D3D12 exchange
/// shared device buffers; only the reset flag and final host-path frame return
/// to the CPU. All GPU work completes before a borrowed frame slot is released.
/// </summary>
internal sealed unsafe class GpuNrProcessor : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly DlssNrHost _host;
    private readonly int _width, _height;
    private readonly bool _history;
    private readonly int _fused = Fma.IsSupported ? 1 : 0;
    private readonly float _weight;
    private CudaVideoContext _cuda = null!;
    private NvidiaOpticalFlow _flow = null!;
    private CudaVideoContext.SharedBuffer[,] _shared = null!;
    private nint _motion, _rejected, _errors, _reset, _previousInput, _currentInput, _previousResidual, _nextResidual;
    private nint _staging, _resetHost;
    private bool _nativeCreated, _disposed;
    private long _frames;
    private readonly PipelineStageMetrics _metrics = new("GPU NR", "upload", "nvofa", "prepare", "nr", "history_readback");

    internal static GpuNrProcessor? TryCreate(DlssNrHost host, int width, int height, bool hdr, double fps)
    {
        if (hdr || !host.MotionGuidance || !host.UseGpuTemporalProcessing) return null;
        var processor = new GpuNrProcessor(host, width, height, fps);
        try
        {
            processor.Initialize();
            PipelineLog.Info("光流/历史显存管线：CUDA 灰度 → NVOFA → CUDA 运动验证 → 共享显存 NR → CUDA 历史累积；仅回读切镜标志与最终帧。");
            return processor;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            processor.Dispose();
            PipelineLog.Info($"光流/历史显存管线不可用，沿用兼容路径：{error.Message}");
            return null;
        }
        catch
        {
            processor.Dispose();
            throw;
        }
    }

    private GpuNrProcessor(DlssNrHost host, int width, int height, double fps)
    {
        _host = host; _width = width; _height = height; _history = host.TemporalStabilization;
        double seconds = double.IsFinite(fps) && fps > 0 ? 1 / fps : 1 / 60.0;
        _weight = seconds <= .25 ? (float)Math.Exp(-seconds / .12) : 0;
    }

    private T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    private nint Device(long bytes) => Own(new CudaVideoContext.DeviceAllocation(_cuda, checked((nuint)bytes))).Data;

    private void Initialize()
    {
        _cuda = Own(new CudaVideoContext(0)); _cuda.InitializeTemporal();
        _host.ConfigureMotionGuidance(true);
        int count = DlssNrHost.dlssnr_gpu_frames_create();
        if (count is < 1 or > 16) throw new InvalidOperationException("NR 宿主未提供共享显存帧。");
        _nativeCreated = true;
        _shared = new CudaVideoContext.SharedBuffer[count, 3];
        for (int slot = 0; slot < count; slot++) for (int kind = 0; kind < 3; kind++)
        {
            if (DlssNrHost.dlssnr_gpu_frames_info(slot, kind, out var handle, out var allocation, out var bytes, out var pitch, out var luid) == 0)
                throw new InvalidOperationException("NR 共享显存布局不可用。");
            _shared[slot, kind] = Own(new CudaVideoContext.SharedBuffer(_cuda, handle, allocation, bytes, pitch, luid));
        }
        _flow = Own(NvidiaOpticalFlow.TryCreate(_width, _height, false, _host.FineMotion ? 1 : 4, deviceOnly: true)
            ?? throw new InvalidOperationException("NVOFA 设备路径不可用。"));
        long pixels = checked((long)_width * _height);
        _motion = Device(pixels * 8); _rejected = Device(pixels); _reset = Device(4);
        _staging = Own(new CudaVideoContext.PinnedAllocation(_cuda, checked((nuint)(pixels * 4)))).Data;
        _resetHost = Own(new CudaVideoContext.PinnedAllocation(_cuda, 4)).Data;
        if (_history)
        {
            _errors = Device(pixels * 4);
            _previousInput = Device(pixels * 16); _currentInput = Device(pixels * 16);
            _previousResidual = Device(pixels * 16); _nextResidual = Device(pixels * 16);
        }
    }

    internal void Process(byte* input, byte* output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int slot = DlssNrHost.dlssnr_gpu_frames_next();
        if (slot < 0 || slot >= _shared.GetLength(0)) throw new InvalidOperationException("NR 共享显存槽不可用。");
        var color = _shared[slot, 0]; var halfMotion = _shared[slot, 1]; var result = _shared[slot, 2];
        long stamp = _metrics.Start();
        _cuda.UploadRgbaRows(input, _staging, color.Data, color.Pitch, _width, _height);
        _metrics.Record(0, stamp); stamp = _metrics.Start();
        var flow = _flow.CalculateDevice(_cuda, color.Data, color.Pitch, _frames == 0);
        _metrics.Record(1, stamp); stamp = _metrics.Start();
        _cuda.TemporalExpand(flow, _motion, _rejected, _width, _height);
        bool reset;
        if (_history)
        {
            reset = _cuda.TemporalPrepare(color.Data, color.Pitch, _previousInput, _currentInput, _motion, _errors,
                _reset, _resetHost, _width, _height, _frames != 0, _fused);
            (_previousInput, _currentInput) = (_currentInput, _previousInput);
        }
        else { reset = _frames == 0; _cuda.SetDeviceFlag(_reset, reset ? 1u : 0u); }
        if (reset) _flow.ResetHistory();
        _cuda.TemporalReliability(_motion, _rejected, _errors, halfMotion.Data, halfMotion.Pitch, _reset, _width, _height, _history);
        _metrics.Record(2, stamp); stamp = _metrics.Start();
        if (!_host.ProcessDeviceFrame(slot, reset)) throw new InvalidOperationException("NR 显存帧处理失败。");
        _metrics.Record(3, stamp); stamp = _metrics.Start();
        if (_history)
        {
            _cuda.TemporalAccumulate(color.Data, color.Pitch, result.Data, result.Pitch, _previousResidual, _nextResidual,
                _motion, _errors, _reset, _width, _height, _weight, _fused);
            (_previousResidual, _nextResidual) = (_nextResidual, _previousResidual);
        }
        _cuda.DownloadRgbaRows(result.Data, result.Pitch, _staging, output, _width, _height);
        _metrics.Record(4, stamp); _frames++;
    }

    internal void ReportMetrics(long frames) => _metrics.Report(frames);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        if (_cuda != null)
            try { _cuda.FinishTemporal(); } catch (Exception error) { failure = error; }
        // Unmap CUDA imports before closing their D3D12 exporters. Continue
        // releasing other owners even if a device failure affects one of them.
        for (int i = _owned.Count - 1; i >= 0; i--)
            try { _owned[i].Dispose(); } catch (Exception error) { failure ??= error; }
        if (_nativeCreated)
            try { DlssNrHost.dlssnr_gpu_frames_close(); } catch (Exception error) { failure ??= error; }
        if (failure != null) throw new InvalidOperationException("GPU NR 清理失败。", failure);
    }
}

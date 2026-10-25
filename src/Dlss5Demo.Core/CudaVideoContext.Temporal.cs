using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

internal sealed unsafe partial class CudaVideoContext
{
    private nint _temporalModule;
    private nint _grayKernel, _expandKernel, _prepareKernel, _sceneKernel, _reliabilityKernel, _accumulateKernel;

    internal void InitializeTemporal()
    {
        using var scope = Enter();
        if (_temporalModule != 0) return;
        _temporalModule = LoadModule("temporal");
        Check(cuModuleGetFunction(out _grayKernel, _temporalModule, "gray"));
        Check(cuModuleGetFunction(out _expandKernel, _temporalModule, "expand"));
        Check(cuModuleGetFunction(out _prepareKernel, _temporalModule, "prepare"));
        Check(cuModuleGetFunction(out _sceneKernel, _temporalModule, "scene"));
        Check(cuModuleGetFunction(out _reliabilityKernel, _temporalModule, "reliability"));
        Check(cuModuleGetFunction(out _accumulateKernel, _temporalModule, "accumulate"));
    }

    private void SubmitTemporal(nint kernel, int width, int height, void** args)
        => Check(cuLaunchKernel(kernel, (uint)((width + 31) / 32), (uint)((height + 7) / 8), 1, 32, 8, 1, 0, _stream, args, null));

    internal void TemporalGray(nint input, int pitch, nint output, int outputPitch, int width, int height, int alignedWidth, int alignedHeight)
    {
        using var scope = Enter();
        void** args = stackalloc void*[8] { &input, &pitch, &output, &outputPitch, &width, &height, &alignedWidth, &alignedHeight };
        SubmitTemporal(_grayKernel, alignedWidth, alignedHeight, args);
        Check(cuStreamSynchronize(_stream)); // NVOFA owns a different stream.
    }

    internal void TemporalExpand(NvidiaOpticalFlow.DeviceFlow flow, nint motion, nint rejected, int width, int height)
    {
        using var scope = Enter();
        nint packed = flow.Packed, cost = flow.Cost;
        int pitch = flow.Pitch, cp = flow.CostPitch, gw = flow.GridWidth, gh = flow.GridHeight, grid = flow.Grid;
        int confidence = flow.Confidence ? 1 : 0, clear = flow.Clear ? 1 : 0;
        void** args = stackalloc void*[13] { &packed, &pitch, &cost, &cp, &motion, &rejected, &width, &height, &gw, &gh, &grid, &confidence, &clear };
        SubmitTemporal(_expandKernel, width, height, args);
    }

    internal bool TemporalPrepare(nint input, int pitch, nint previous, nint current, nint motion, nint errors,
        nint reset, nint resetHost, int width, int height, bool hasPrevious, int fused)
    {
        using var scope = Enter();
        int has = hasPrevious ? 1 : 0, first = hasPrevious ? 0 : 1;
        void** prepare = stackalloc void*[10] { &input, &pitch, &previous, &current, &motion, &errors, &width, &height, &has, &fused };
        SubmitTemporal(_prepareKernel, width, height, prepare);
        void** scene = stackalloc void*[5] { &errors, &reset, &width, &height, &first };
        SubmitTemporal(_sceneKernel, 1, 1, scene);
        Check(cuMemcpyDtoHAsync((void*)resetHost, (ulong)reset, 4, _stream));
        Check(cuStreamSynchronize(_stream));
        return *(uint*)resetHost != 0;
    }

    internal void TemporalReliability(nint motion, nint rejected, nint errors, nint halfMotion, int pitch, nint reset, int width, int height, bool history)
    {
        using var scope = Enter();
        int hasHistory = history ? 1 : 0;
        void** args = stackalloc void*[9] { &motion, &rejected, &errors, &halfMotion, &pitch, &reset, &width, &height, &hasHistory };
        SubmitTemporal(_reliabilityKernel, width, height, args);
        Check(cuStreamSynchronize(_stream)); // D3D12 may now read shared color/motion.
    }

    internal void TemporalAccumulate(nint input, int inputPitch, nint output, int outputPitch, nint previous, nint next,
        nint motion, nint errors, nint reset, int width, int height, float weight, int fused)
    {
        using var scope = Enter();
        void** args = stackalloc void*[13] { &input, &inputPitch, &output, &outputPitch, &previous, &next, &motion, &errors, &reset, &width, &height, &weight, &fused };
        SubmitTemporal(_accumulateKernel, width, height, args);
    }

    internal void SetDeviceFlag(nint pointer, uint value)
    {
        using var scope = Enter();
        Check(cuMemsetD32Async((ulong)pointer, value, 1, _stream));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCopy2D
    {
        public nuint SourceX, SourceY;
        public int SourceType; public nint SourceHost; public ulong SourceDevice; public nint SourceArray;
        public nuint SourcePitch, DestinationX, DestinationY;
        public int DestinationType; public nint DestinationHost; public ulong DestinationDevice; public nint DestinationArray;
        public nuint DestinationPitch, Width, Height;
    }

    internal void UploadRgbaRows(byte* source, nint staging, nint destination, int pitch, int width, int height)
    {
        using var scope = Enter();
        long bytes = checked((long)width * height * 4);
        Buffer.MemoryCopy(source, (void*)staging, bytes, bytes);
        var copy = new MemoryCopy2D { SourceType = 1, SourceHost = staging, SourcePitch = (nuint)(width * 4),
            DestinationType = 2, DestinationDevice = (ulong)destination, DestinationPitch = (nuint)pitch,
            Width = (nuint)(width * 4), Height = (nuint)height };
        Check(cuMemcpy2DAsync(&copy, _stream));
        // The next kernel runs on this same stream. The caller owns staging
        // until DownloadRgbaRows or FinishTemporal has joined the stream.
    }

    internal void DownloadRgbaRows(nint source, int pitch, nint staging, byte* destination, int width, int height)
    {
        using var scope = Enter();
        var copy = new MemoryCopy2D { SourceType = 2, SourceDevice = (ulong)source, SourcePitch = (nuint)pitch,
            DestinationType = 1, DestinationHost = staging, DestinationPitch = (nuint)(width * 4),
            Width = (nuint)(width * 4), Height = (nuint)height };
        Check(cuMemcpy2DAsync(&copy, _stream));
        Check(cuStreamSynchronize(_stream));
        long bytes = checked((long)width * height * 4);
        Buffer.MemoryCopy((void*)staging, destination, bytes, bytes);
    }

    internal void FinishTemporal() { using var scope = Enter(); Check(cuStreamSynchronize(_stream)); }

    internal void WriteDevice(nint destination, ReadOnlySpan<byte> source)
    {
        using var scope = Enter(); Check(cuStreamSynchronize(_stream));
        fixed (byte* data = source)
        {
            Check(cuMemcpyHtoD((ulong)destination, data, (nuint)source.Length));
            // Pageable HtoD can return after staging but before its default-
            // stream DMA completes. Our nonblocking stream does not wait for
            // that stream implicitly. Join before unpinning/launching kernels.
            Check(cuStreamSynchronize(0));
        }
    }
    internal void ReadDevice(nint source, Span<byte> destination)
    {
        using var scope = Enter(); Check(cuStreamSynchronize(_stream));
        fixed (byte* data = destination) Check(cuMemcpyDtoH(data, (ulong)source, (nuint)destination.Length));
    }

    internal sealed class DeviceAllocation : IDisposable
    {
        private readonly CudaVideoContext _cuda;
        private ulong _data;
        internal nint Data => (nint)_data;
        internal DeviceAllocation(CudaVideoContext cuda, nuint bytes)
        {
            _cuda = cuda;
            using var scope = cuda.Enter();
            Check(cuMemAlloc(out _data, bytes));
            try { Check(cuMemsetD8Async(_data, 0, bytes, cuda._stream)); Check(cuStreamSynchronize(cuda._stream)); }
            catch { cuMemFree(_data); _data = 0; throw; }
        }
        public void Dispose()
        {
            if (_data == 0) return;
            using var scope = _cuda.Enter();
            Check(cuStreamSynchronize(_cuda._stream)); Check(cuMemFree(_data)); _data = 0;
        }
    }

    internal sealed class PinnedAllocation : IDisposable
    {
        internal nint Data { get; private set; }
        internal PinnedAllocation(CudaVideoContext cuda, nuint bytes)
        {
            using var scope = cuda.Enter(); Check(cuMemHostAlloc(out var pointer, bytes, 1)); Data = pointer;
        }
        public void Dispose() { if (Data != 0) { Check(cuMemFreeHost(Data)); Data = 0; } }
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct ExternalMemoryDescription
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(8)] internal nint Handle;
        [FieldOffset(24)] internal ulong Size;
        [FieldOffset(32)] internal uint Flags;
    }
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct ExternalBufferDescription { [FieldOffset(8)] internal ulong Size; }

    internal sealed class SharedBuffer : IDisposable
    {
        private readonly CudaVideoContext _cuda;
        private nint _memory;
        private ulong _pointer;
        internal nint Data => (nint)_pointer;
        internal int Pitch { get; }
        internal SharedBuffer(CudaVideoContext cuda, nint handle, ulong allocation, ulong bytes, uint pitch, ulong adapterLuid)
        {
            _cuda = cuda; Pitch = checked((int)pitch);
            using var scope = cuda.Enter();
            Check(cuDeviceGetLuid(out ulong cudaLuid, out _, cuda.DeviceId));
            if (cudaLuid != adapterLuid) throw new InvalidOperationException("D3D12 与 CUDA 适配器不同，无法共享显存。");
            var description = new ExternalMemoryDescription { Type = 5, Handle = handle, Size = allocation, Flags = 1 };
            Check(cuImportExternalMemory(out _memory, ref description));
            try
            {
                var buffer = new ExternalBufferDescription { Size = bytes };
                Check(cuExternalMemoryGetMappedBuffer(out _pointer, _memory, ref buffer));
            }
            catch { cuDestroyExternalMemory(_memory); _memory = 0; throw; }
        }
        public void Dispose()
        {
            if (_memory == 0) return;
            using var scope = _cuda.Enter(); Check(cuStreamSynchronize(_cuda._stream));
            if (_pointer != 0) { Check(cuMemFree(_pointer)); _pointer = 0; }
            Check(cuDestroyExternalMemory(_memory)); _memory = 0;
        }
    }

    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpy2DAsync_v2")] private static extern int cuMemcpy2DAsync(MemoryCopy2D* copy, nint stream);
    [DllImport("nvcuda.dll")] private static extern int cuMemsetD8Async(ulong pointer, byte value, nuint count, nint stream);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetLuid(out ulong luid, out uint nodeMask, int device);
    [DllImport("nvcuda.dll")] private static extern int cuImportExternalMemory(out nint memory, ref ExternalMemoryDescription description);
    [DllImport("nvcuda.dll")] private static extern int cuExternalMemoryGetMappedBuffer(out ulong pointer, nint memory, ref ExternalBufferDescription description);
    [DllImport("nvcuda.dll")] private static extern int cuDestroyExternalMemory(nint memory);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyHtoD_v2")] private static extern int cuMemcpyHtoD(ulong destination, void* source, nuint bytes);
}

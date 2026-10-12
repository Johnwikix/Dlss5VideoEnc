using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>
/// NVIDIA Optical Flow API 2.0, loaded exclusively from the installed driver.
/// Estimates current-to-previous displacement in input pixels. No CUDA toolkit,
/// Python runtime or downloaded SDK is needed by the application.
/// ABI: https://github.com/NVIDIA/NVIDIAOpticalFlowSDK (see bundled license).
/// API declarations: Copyright (c) 2020 NVIDIA CORPORATION.
/// </summary>
internal sealed unsafe class NvidiaOpticalFlow : IDisposable
{
    private readonly int _width, _height, _alignedWidth, _alignedHeight, _gridWidth, _gridHeight;
    private readonly bool _hdr;
    private readonly byte[] _gray;
    private readonly short[] _packed;
    private readonly IntPtr[] _api = new IntPtr[12];
    private readonly IntPtr[] _buffers = new IntPtr[3];
    private readonly ulong[] _pointers = new ulong[3];
    private readonly uint[] _pitches = new uint[3];
    private IntPtr _cuda, _driver, _context, _handle;
    private int _device, _nextInput;
    private bool _retained, _hasPrevious, _disposed;
    private bool _resetHints = true;
    private delegate* unmanaged[Stdcall]<IntPtr, int> _push;
    private delegate* unmanaged[Stdcall]<IntPtr*, int> _pop;
    private delegate* unmanaged[Stdcall]<Copy2D*, int> _copy;
    private delegate* unmanaged[Stdcall]<int> _synchronize;
    internal float[] Motion { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Init
    {
        public uint Width, Height;
        public int Grid, HintGrid, Mode, Performance, ExternalHints, OutputCost;
        public IntPtr Private;
        public int Disparity, Roi;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer
    {
        public uint Width, Height;
        public int Usage, Format;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExecuteInput
    {
        public IntPtr Input, Reference, Hints;
        public int DisableTemporal;
        public uint Padding;
        public IntPtr Private;
        public uint Padding2, RoiCount;
        public IntPtr Rois;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExecuteOutput { public IntPtr Output, Cost, Private; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Stride { public fixed uint Values[6]; public uint Planes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Copy2D
    {
        public nuint SourceX, SourceY;
        public int SourceType;
        public IntPtr SourceHost;
        public ulong SourceDevice;
        public IntPtr SourceArray;
        public nuint SourcePitch, DestinationX, DestinationY;
        public int DestinationType;
        public IntPtr DestinationHost;
        public ulong DestinationDevice;
        public IntPtr DestinationArray;
        public nuint DestinationPitch, Width, Height;
    }

    internal static NvidiaOpticalFlow? TryCreate(int width, int height, bool hdr)
    {
        var flow = new NvidiaOpticalFlow(width, height, hdr);
        try
        {
            flow.Initialize();
            PipelineLog.Info($"NVIDIA 光流已启用: {width}×{height}, current→previous, grid=4");
            return flow;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            flow.Dispose();
            PipelineLog.Info($"NVIDIA 光流不可用，使用静态区域时序累积: {error.Message}");
            return null;
        }
    }

    private NvidiaOpticalFlow(int width, int height, bool hdr)
    {
        _width = width; _height = height; _hdr = hdr;
        _alignedWidth = checked((width + 15) / 16 * 16);
        _alignedHeight = checked((height + 15) / 16 * 16);
        _gridWidth = _alignedWidth / 4; _gridHeight = _alignedHeight / 4;
        _gray = new byte[checked(_alignedWidth * _alignedHeight)];
        _packed = new short[checked(_gridWidth * _gridHeight * 2)];
        Motion = new float[checked(width * height * 2)];
    }

    private IntPtr Cuda(string name) => NativeLibrary.GetExport(_cuda, name);
    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"{operation} failed (status {status}).");
    }

    private void Initialize()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        _cuda = NativeLibrary.Load(Path.Combine(system, "nvcuda.dll"));
        _driver = NativeLibrary.Load(Path.Combine(system, "nvofapi64.dll"));
        _push = (delegate* unmanaged[Stdcall]<IntPtr, int>)Cuda("cuCtxPushCurrent_v2");
        _pop = (delegate* unmanaged[Stdcall]<IntPtr*, int>)Cuda("cuCtxPopCurrent_v2");
        _copy = (delegate* unmanaged[Stdcall]<Copy2D*, int>)Cuda("cuMemcpy2D_v2");
        _synchronize = (delegate* unmanaged[Stdcall]<int>)Cuda("cuCtxSynchronize");
        Check(((delegate* unmanaged[Stdcall]<uint, int>)Cuda("cuInit"))(0), "cuInit");
        int device;
        Check(((delegate* unmanaged[Stdcall]<int*, int, int>)Cuda("cuDeviceGet"))(&device, 0), "cuDeviceGet");
        _device = device;
        IntPtr context;
        Check(((delegate* unmanaged[Stdcall]<IntPtr*, int, int>)Cuda("cuDevicePrimaryCtxRetain"))(&context, _device), "cuDevicePrimaryCtxRetain");
        _context = context; _retained = true;
        fixed (IntPtr* table = _api)
            Check(((delegate* unmanaged[Stdcall]<uint, IntPtr*, int>)NativeLibrary.GetExport(_driver, "NvOFAPICreateInstanceCuda"))(0x20, table), "NvOFAPICreateInstanceCuda");
        if (_api.Any(pointer => pointer == IntPtr.Zero))
            throw new InvalidOperationException("Incomplete NVIDIA optical flow API table.");
        Check(_push(_context), "cuCtxPushCurrent");
        try
        {
            IntPtr handle;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)_api[0])(_context, &handle), "nvCreateOpticalFlowCuda");
            _handle = handle;
            var init = new Init { Width = (uint)_alignedWidth, Height = (uint)_alignedHeight, Grid = 4, Mode = 1, Performance = 5 };
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Init*, int>)_api[1])(_handle, &init), "nvOFInit");
            for (var index = 0; index < 3; index++)
            {
                var description = new Buffer
                {
                    Width = (uint)(index < 2 ? _alignedWidth : _gridWidth),
                    Height = (uint)(index < 2 ? _alignedHeight : _gridHeight),
                    Usage = index < 2 ? 1 : 2, Format = index < 2 ? 1 : 5,
                };
                IntPtr buffer;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Buffer*, int, IntPtr*, int>)_api[2])(_handle, &description, 2, &buffer), "nvOFCreateGPUBufferCuda");
                _buffers[index] = buffer;
                _pointers[index] = ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)_api[4])(buffer);
                var stride = new Stride();
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Stride*, int>)_api[5])(buffer, &stride), "nvOFGPUBufferGetStrideInfo");
                _pitches[index] = stride.Values[0];
                if (_pointers[index] == 0) throw new InvalidOperationException("Null NVIDIA optical flow buffer.");
            }
        }
        finally { Pop(); }
    }

    private void Pop()
    {
        IntPtr previous;
        Check(_pop(&previous), "cuCtxPopCurrent");
    }

    internal void Calculate(byte* rgba, bool reset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Convert actual NR input, including VSR scaling, rather than an earlier
        // decoder frame in a different coordinate system. Replicate padded edges.
        Parallel.For(0, _alignedHeight, row =>
        {
            var sourceRow = Math.Min(row, _height - 1) * _width;
            for (var x = 0; x < _alignedWidth; x++)
            {
                var pixel = (sourceRow + Math.Min(x, _width - 1)) * 4;
                float luma;
                if (_hdr)
                {
                    var halves = (Half*)rgba;
                    luma = 255 * (.299f * (float)halves[pixel] + .587f * (float)halves[pixel + 1] + .114f * (float)halves[pixel + 2]);
                }
                else luma = (77 * rgba[pixel] + 150 * rgba[pixel + 1] + 29 * rgba[pixel + 2]) / 256f;
                _gray[row * _alignedWidth + x] = float.IsFinite(luma) ? (byte)Math.Clamp(MathF.Round(luma), 0, 255) : (byte)0;
            }
        });
        Check(_push(_context), "cuCtxPushCurrent");
        try
        {
            fixed (byte* gray = _gray)
            {
                var upload = new Copy2D { SourceType = 1, SourceHost = (IntPtr)gray, SourcePitch = (nuint)_alignedWidth,
                    DestinationType = 2, DestinationDevice = _pointers[_nextInput], DestinationPitch = _pitches[_nextInput],
                    Width = (nuint)_alignedWidth, Height = (nuint)_alignedHeight };
                Check(_copy(&upload), "upload optical flow input");
            }
            if (_hasPrevious && !reset)
            {
                // API output maps Input to Reference: current frame to previous.
                var input = new ExecuteInput { Input = _buffers[_nextInput], Reference = _buffers[_nextInput ^ 1], DisableTemporal = _resetHints ? 1 : 0 };
                var output = new ExecuteOutput { Output = _buffers[2] };
                Check(((delegate* unmanaged[Stdcall]<IntPtr, ExecuteInput*, ExecuteOutput*, int>)_api[7])(_handle, &input, &output), "nvOFExecute");
                Check(_synchronize(), "cuCtxSynchronize");
                fixed (short* packed = _packed)
                {
                    var download = new Copy2D { SourceType = 2, SourceDevice = _pointers[2], SourcePitch = _pitches[2],
                        DestinationType = 1, DestinationHost = (IntPtr)packed, DestinationPitch = (nuint)(_gridWidth * 4),
                        Width = (nuint)(_gridWidth * 4), Height = (nuint)_gridHeight };
                    Check(_copy(&download), "download optical flow output");
                }
                Parallel.For(0, _height, ExpandRow);
                _resetHints = false;
            }
            else ResetHistory();
            _hasPrevious = true;
            _nextInput ^= 1;
        }
        finally { Pop(); }
    }

    internal void ResetHistory()
    {
        _resetHints = true;
        Array.Clear(Motion);
    }

    private void ExpandRow(int row)
    {
        var fy = Math.Clamp((row + .5f) / 4 - .5f, 0, _gridHeight - 1);
        var y0 = (int)fy; var y1 = Math.Min(y0 + 1, _gridHeight - 1); var ay = fy - y0;
        for (var x = 0; x < _width; x++)
        {
            var fx = Math.Clamp((x + .5f) / 4 - .5f, 0, _gridWidth - 1);
            var x0 = (int)fx; var x1 = Math.Min(x0 + 1, _gridWidth - 1); var ax = fx - x0;
            for (var component = 0; component < 2; component++)
            {
                var a = _packed[(y0 * _gridWidth + x0) * 2 + component] * (1 - ax) + _packed[(y0 * _gridWidth + x1) * 2 + component] * ax;
                var b = _packed[(y1 * _gridWidth + x0) * 2 + component] * (1 - ax) + _packed[(y1 * _gridWidth + x1) * 2 + component] * ax;
                Motion[(row * _width + x) * 2 + component] = (a * (1 - ay) + b * ay) / 32;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero && _push(_context) == 0)
        {
            for (var index = 2; index >= 0; index--)
                if (_buffers[index] != IntPtr.Zero)
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)_api[8])(_buffers[index]);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)_api[9])(_handle);
            IntPtr previous;
            _pop(&previous);
        }
        if (_retained)
            ((delegate* unmanaged[Stdcall]<int, int>)Cuda("cuDevicePrimaryCtxRelease_v2"))(_device);
        if (_driver != IntPtr.Zero) NativeLibrary.Free(_driver);
        if (_cuda != IntPtr.Zero) NativeLibrary.Free(_cuda);
    }
}

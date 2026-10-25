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
    private readonly int _width, _height, _alignedWidth, _alignedHeight, _preferredGrid;
    private int _gridSize = 4, _gridWidth, _gridHeight;
    private readonly bool _hdr;
    private readonly bool _confidence;
    private readonly bool _deviceOnly;
    private IntPtr _grayHost;
    private short[] _packed = [];
    private byte[] _cost = [];
    private byte[] _costMax = [];
    private readonly int[] _sampleX0, _sampleX1;
    private readonly float[] _sampleAx;
    private readonly IntPtr[] _api = new IntPtr[12];
    private readonly IntPtr[] _buffers = new IntPtr[4];
    private readonly ulong[] _pointers = new ulong[4];
    private readonly uint[] _pitches = new uint[4];
    private IntPtr _cuda, _driver, _context, _handle, _inputStream, _outputStream;
    private int _device, _nextInput;
    private bool _retained, _hasPrevious, _disposed;
    private bool _resetHints = true;
    private delegate* unmanaged[Stdcall]<IntPtr, int> _push;
    private delegate* unmanaged[Stdcall]<IntPtr*, int> _pop;
    private delegate* unmanaged[Stdcall]<Copy2D*, int> _copy;
    private delegate* unmanaged[Stdcall]<Copy2D*, IntPtr, int> _copyAsync;
    private delegate* unmanaged[Stdcall]<IntPtr, int> _streamSynchronize;
    private delegate* unmanaged[Stdcall]<IntPtr, int> _streamDestroy;
    internal float[] Motion { get; }
    internal bool[] RejectedMotion { get; }
    internal int GridSize => _gridSize;
    private float[] _motionOutput;
    private bool[] _rejectedOutput;
    private readonly PipelineStageMetrics _metrics = new("光流", "gray", "upload", "execute_wait", "download", "expand");
    internal void ReportMetrics(long frames) => _metrics.Report(frames);

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

    internal static NvidiaOpticalFlow? TryCreate(int width, int height, bool hdr, int preferredGrid = 1, bool confidence = true, bool deviceOnly = false)
    {
        var flow = new NvidiaOpticalFlow(width, height, hdr, preferredGrid, confidence, deviceOnly);
        try
        {
            flow.Initialize();
            PipelineLog.Info($"NVIDIA 光流已启用: {width}×{height}, current→previous, grid={flow.GridSize}, SLOW; 置信度过滤={confidence}");
            return flow;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            flow.Dispose();
            if (error is InvalidOperationException && (preferredGrid != 4 || confidence))
            {
                PipelineLog.Info($"精细光流不可用，回退 grid=4: {error.Message}");
                return TryCreate(width, height, hdr, 4, false, deviceOnly);
            }
            PipelineLog.Info($"NVIDIA 光流不可用，使用静态区域时序累积: {error.Message}");
            return null;
        }
    }

    private NvidiaOpticalFlow(int width, int height, bool hdr, int preferredGrid, bool confidence, bool deviceOnly)
    {
        _width = width; _height = height; _hdr = hdr; _preferredGrid = preferredGrid; _confidence = confidence;
        _deviceOnly = deviceOnly;
        _alignedWidth = checked((width + 15) / 16 * 16);
        _alignedHeight = checked((height + 15) / 16 * 16);
        Motion = deviceOnly ? [] : new float[checked(width * height * 2)];
        RejectedMotion = deviceOnly ? [] : new bool[checked(width * height)];
        _motionOutput = Motion;
        _rejectedOutput = RejectedMotion;
        _sampleX0 = new int[width]; _sampleX1 = new int[width]; _sampleAx = new float[width];
    }

    private void ConfigureGrid()
    {
        uint count = 0;
        var getCaps = (delegate* unmanaged[Stdcall]<IntPtr, int, uint*, uint*, int>)_api[11];
        Check(getCaps(_handle, 0, null, &count), "query optical flow grid count");
        if (count is 0 or > 16) throw new InvalidOperationException("Invalid optical flow grid capabilities.");
        var grids = stackalloc uint[16];
        Check(getCaps(_handle, 0, grids, &count), "query optical flow grids");
        _gridSize = 4;
        for (var index = 0; index < count; index++)
            if (grids[index] is 1 or 2 or 4 && grids[index] >= _preferredGrid)
                _gridSize = Math.Min(_gridSize, (int)grids[index]);
        _gridWidth = _alignedWidth / _gridSize; _gridHeight = _alignedHeight / _gridSize;
        if (_deviceOnly) return;
        _packed = new short[checked(_gridWidth * _gridHeight * 2)];
        _cost = _confidence ? new byte[checked(_gridWidth * _gridHeight)] : [];
        _costMax = _confidence && _gridSize > 1 ? new byte[_cost.Length] : [];
        for (var x = 0; x < _width; x++)
        {
            var fx = Math.Clamp((x + .5f) / _gridSize - .5f, 0, _gridWidth - 1);
            var x0 = (int)fx;
            _sampleX0[x] = x0 * 2;
            _sampleX1[x] = Math.Min(x0 + 1, _gridWidth - 1) * 2;
            _sampleAx[x] = fx - x0;
        }
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
        _copyAsync = (delegate* unmanaged[Stdcall]<Copy2D*, IntPtr, int>)Cuda("cuMemcpy2DAsync_v2");
        _streamSynchronize = (delegate* unmanaged[Stdcall]<IntPtr, int>)Cuda("cuStreamSynchronize");
        _streamDestroy = (delegate* unmanaged[Stdcall]<IntPtr, int>)Cuda("cuStreamDestroy_v2");
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
            ConfigureGrid();
            if (!_deviceOnly)
            {
                IntPtr grayHost;
                Check(((delegate* unmanaged[Stdcall]<IntPtr*, nuint, uint, int>)Cuda("cuMemHostAlloc"))(
                    &grayHost, checked((nuint)(_alignedWidth * _alignedHeight)), 0), "allocate pinned optical flow input");
                _grayHost = grayHost;
            }
            IntPtr inputStream, outputStream;
            var createStream = (delegate* unmanaged[Stdcall]<IntPtr*, uint, int>)Cuda("cuStreamCreate");
            Check(createStream(&inputStream, 1), "create nonblocking optical flow input stream");
            _inputStream = inputStream;
            Check(createStream(&outputStream, 1), "create nonblocking optical flow output stream");
            _outputStream = outputStream;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)_api[6])(_handle, _inputStream, _outputStream), "nvOFSetIOCudaStreams");
            var init = new Init { Width = (uint)_alignedWidth, Height = (uint)_alignedHeight,
                Grid = _gridSize, Mode = 1, Performance = 5, OutputCost = _confidence ? 1 : 0 };
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Init*, int>)_api[1])(_handle, &init), "nvOFInit");
            for (var index = 0; index < (_confidence ? 4 : 3); index++)
            {
                var description = new Buffer
                {
                    Width = (uint)(index < 2 ? _alignedWidth : _gridWidth),
                    Height = (uint)(index < 2 ? _alignedHeight : _gridHeight),
                    Usage = index < 2 ? 1 : index == 2 ? 2 : 4,
                    Format = index < 2 ? 1 : index == 2 ? 5 : 7,
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

    internal void Calculate(byte* rgba, bool reset, float[]? destination = null, bool[]? rejected = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deviceOnly) throw new InvalidOperationException("Device optical flow requires CalculateDevice.");
        _motionOutput = destination ?? Motion;
        _rejectedOutput = rejected ?? RejectedMotion;
        if (_motionOutput.Length != Motion.Length) throw new ArgumentException("Invalid motion buffer.", nameof(destination));
        if (_rejectedOutput.Length != RejectedMotion.Length) throw new ArgumentException("Invalid confidence buffer.", nameof(rejected));
        var stageStart = _metrics.Start();
        var gray = (byte*)_grayHost;
        // Convert actual NR input, including VSR scaling, rather than an earlier
        // decoder frame in a different coordinate system. Replicate padded edges.
        Parallel.For(0, _alignedHeight, FrameCpuBudget.Options, row =>
        {
            var sourceRow = Math.Min(row, _height - 1) * _width;
            if (!_hdr)
            {
                var grayOffset = row * _alignedWidth;
                for (var x = 0; x < _width; x++)
                {
                    var pixel = (sourceRow + x) * 4;
                    var sum = 77 * rgba[pixel] + 150 * rgba[pixel + 1] + 29 * rgba[pixel + 2];
                    // Exact integer equivalent of Round(sum / 256f, ToEven).
                    gray[grayOffset + x] = ToGray(sum);
                }
                new Span<byte>(gray + grayOffset + _width, _alignedWidth - _width).Fill(gray[grayOffset + _width - 1]);
                return;
            }
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
                gray[row * _alignedWidth + x] = float.IsFinite(luma) ? (byte)Math.Clamp(MathF.Round(luma), 0, 255) : (byte)0;
            }
        });
        _metrics.Record(0, stageStart);
        Check(_push(_context), "cuCtxPushCurrent");
        try
        {
            stageStart = _metrics.Start();
            {
                var upload = new Copy2D { SourceType = 1, SourceHost = _grayHost, SourcePitch = (nuint)_alignedWidth,
                    DestinationType = 2, DestinationDevice = _pointers[_nextInput], DestinationPitch = _pitches[_nextInput],
                    Width = (nuint)_alignedWidth, Height = (nuint)_alignedHeight };
                Check(_copyAsync(&upload, _inputStream), "upload optical flow input");
            }
            _metrics.Record(1, stageStart);
            if (_hasPrevious && !reset)
            {
                // API output maps Input to Reference: current frame to previous.
                var input = new ExecuteInput { Input = _buffers[_nextInput], Reference = _buffers[_nextInput ^ 1], DisableTemporal = _resetHints ? 1 : 0 };
                var output = new ExecuteOutput { Output = _buffers[2], Cost = _confidence ? _buffers[3] : IntPtr.Zero };
                stageStart = _metrics.Start();
                Check(((delegate* unmanaged[Stdcall]<IntPtr, ExecuteInput*, ExecuteOutput*, int>)_api[7])(_handle, &input, &output), "nvOFExecute");
                // Wait only for OF output, not NR kernels running on other CUDA streams.
                Check(_streamSynchronize(_outputStream), "cuStreamSynchronize optical flow output");
                _metrics.Record(2, stageStart);
                stageStart = _metrics.Start();
                fixed (short* packed = _packed)
                {
                    var download = new Copy2D { SourceType = 2, SourceDevice = _pointers[2], SourcePitch = _pitches[2],
                        DestinationType = 1, DestinationHost = (IntPtr)packed, DestinationPitch = (nuint)(_gridWidth * 4),
                        Width = (nuint)(_gridWidth * 4), Height = (nuint)_gridHeight };
                    Check(_copy(&download), "download optical flow output");
                }
                if (_confidence)
                    fixed (byte* cost = _cost)
                    {
                        var download = new Copy2D { SourceType = 2, SourceDevice = _pointers[3], SourcePitch = _pitches[3],
                            DestinationType = 1, DestinationHost = (IntPtr)cost, DestinationPitch = (nuint)_gridWidth,
                            Width = (nuint)_gridWidth, Height = (nuint)_gridHeight };
                        Check(_copy(&download), "download optical flow confidence");
                    }
                _metrics.Record(3, stageStart);
                stageStart = _metrics.Start();
                if (_costMax.Length > 0) PrepareCostMax();
                Parallel.For(0, _height, FrameCpuBudget.Options, ExpandRow);
                _metrics.Record(4, stageStart);
                _resetHints = false;
            }
            else
            {
                // No OF execution links the output stream on the first/reset frame.
                // Finish its upload before the next CPU grayscale pass reuses pinned memory.
                Check(_streamSynchronize(_inputStream), "finish reset-frame optical flow upload");
                ResetHistory();
            }
            _hasPrevious = true;
            _nextInput ^= 1;
        }
        finally { Pop(); }
    }

    internal void ResetHistory()
    {
        _resetHints = true;
        Array.Clear(_motionOutput);
        Array.Clear(_rejectedOutput);
    }

    internal readonly record struct DeviceFlow(nint Packed, int Pitch, nint Cost, int CostPitch,
        int GridWidth, int GridHeight, int Grid, bool Confidence, bool Clear);

    internal DeviceFlow CalculateDevice(CudaVideoContext cuda, nint rgba, int pitch, bool reset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_deviceOnly || _hdr || cuda.DeviceId != 0) throw new InvalidOperationException("Unsupported device optical flow input.");
        cuda.TemporalGray(rgba, pitch, (nint)_pointers[_nextInput], (int)_pitches[_nextInput], _width, _height, _alignedWidth, _alignedHeight);
        bool clear = !_hasPrevious || reset;
        Check(_push(_context), "cuCtxPushCurrent");
        try
        {
            if (!clear)
            {
                var input = new ExecuteInput { Input = _buffers[_nextInput], Reference = _buffers[_nextInput ^ 1], DisableTemporal = _resetHints ? 1 : 0 };
                var output = new ExecuteOutput { Output = _buffers[2], Cost = _confidence ? _buffers[3] : IntPtr.Zero };
                Check(((delegate* unmanaged[Stdcall]<IntPtr, ExecuteInput*, ExecuteOutput*, int>)_api[7])(_handle, &input, &output), "nvOFExecute device");
                Check(_streamSynchronize(_outputStream), "finish device optical flow");
                _resetHints = false;
            }
            else _resetHints = true;
            _hasPrevious = true; _nextInput ^= 1;
            return new((nint)_pointers[2], (int)_pitches[2], (nint)_pointers[3], (int)_pitches[3], _gridWidth, _gridHeight, _gridSize, _confidence, clear);
        }
        finally { Pop(); }
    }

    internal static byte ToGray(int weightedSum)
        => (byte)((weightedSum + 127 + ((weightedSum >> 8) & 1)) >> 8);

    private void PrepareCostMax()
    {
        // Every group of source pixels samples the same four grid costs. Do
        // the neighborhood reduction on that small plane instead of per pixel.
        for (var y = 0; y < _gridHeight; y++)
        {
            var row = y * _gridWidth;
            var below = Math.Min(y + 1, _gridHeight - 1) * _gridWidth;
            for (var x = 0; x < _gridWidth; x++)
            {
                var right = Math.Min(x + 1, _gridWidth - 1);
                _costMax[row + x] = Math.Max(Math.Max(_cost[row + x], _cost[row + right]),
                    Math.Max(_cost[below + x], _cost[below + right]));
            }
        }
    }

    private void ExpandRow(int row)
    {
        if (_gridSize == 1)
        {
            var packedRow = row * _gridWidth;
            var outputRow = row * _width;
            for (var x = 0; x < _width; x++)
            {
                var packed = packedRow + x;
                var pixel = outputRow + x;
                var mx = _packed[packed * 2] / 32f;
                var my = _packed[packed * 2 + 1] / 32f;
                var invalid = _confidence && (_cost[packed] >= 128 || x + mx < 0 || x + mx > _width - 1 || row + my < 0 || row + my > _height - 1);
                _rejectedOutput[pixel] = invalid;
                _motionOutput[pixel * 2] = invalid ? 0 : mx;
                _motionOutput[pixel * 2 + 1] = invalid ? 0 : my;
            }
            return;
        }
        var fy = Math.Clamp((row + .5f) / _gridSize - .5f, 0, _gridHeight - 1);
        var y0 = (int)fy; var y1 = Math.Min(y0 + 1, _gridHeight - 1); var ay = fy - y0;
        var row0 = y0 * _gridWidth * 2;
        var row1 = y1 * _gridWidth * 2;
        for (var x = 0; x < _width; x++)
        {
            var x0 = _sampleX0[x]; var x1 = _sampleX1[x]; var ax = _sampleAx[x];
            for (var component = 0; component < 2; component++)
            {
                var a = _packed[row0 + x0 + component] * (1 - ax) + _packed[row0 + x1 + component] * ax;
                var b = _packed[row1 + x0 + component] * (1 - ax) + _packed[row1 + x1 + component] * ax;
                _motionOutput[(row * _width + x) * 2 + component] = (a * (1 - ay) + b * ay) / 32;
            }
            var pixel = row * _width + x;
            var mx = _motionOutput[pixel * 2]; var my = _motionOutput[pixel * 2 + 1];
            var cost = _confidence ? _costMax[(row0 + x0) / 2] : 0;
            var invalid = _confidence && (cost >= 128 || x + mx < 0 || x + mx > _width - 1 || row + my < 0 || row + my > _height - 1);
            _rejectedOutput[pixel] = invalid;
            if (invalid) _motionOutput[pixel * 2] = _motionOutput[pixel * 2 + 1] = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero && _push(_context) == 0)
        {
            if (_outputStream != IntPtr.Zero) _streamSynchronize(_outputStream);
            if (_inputStream != IntPtr.Zero) _streamSynchronize(_inputStream);
            for (var index = _buffers.Length - 1; index >= 0; index--)
                if (_buffers[index] != IntPtr.Zero)
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)_api[8])(_buffers[index]);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)_api[9])(_handle);
            if (_outputStream != IntPtr.Zero) _streamDestroy(_outputStream);
            if (_inputStream != IntPtr.Zero) _streamDestroy(_inputStream);
            if (_grayHost != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Cuda("cuMemFreeHost"))(_grayHost);
            IntPtr previous;
            _pop(&previous);
        }
        if (_retained)
            ((delegate* unmanaged[Stdcall]<int, int>)Cuda("cuDevicePrimaryCtxRelease_v2"))(_device);
        if (_driver != IntPtr.Zero) NativeLibrary.Free(_driver);
        if (_cuda != IntPtr.Zero) NativeLibrary.Free(_cuda);
    }
}

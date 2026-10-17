using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;

namespace Dlss5Demo.Core;

public enum FrameInterpolationBackend { NvidiaOpticalFlow, Rife }
public enum RifeModel { Lite, Full }

public static class FrameInterpolationBackends
{
    public static FrameInterpolationBackend Parse(string value) => value.ToLowerInvariant() switch
    {
        "nvidia" or "vulkan" => FrameInterpolationBackend.NvidiaOpticalFlow,
        "rife" => FrameInterpolationBackend.Rife,
        _ => throw new ArgumentException("补帧后端只能是 nvidia 或 rife。", nameof(value))
    };
}

/// <summary>RIFE GPU 会话：FP32 张量保留显存，支持 CUDA 显存帧及 RGBA 上传/回读两种像素接口。</summary>
internal sealed class RifeGpuSession : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly InferenceSession _network;
    private InferenceSession? _prepare, _pack;
    private readonly Func<string, CancellationToken, InferenceSession> _createSession;
    private readonly (string Before, string After) _hostStages;
    private readonly OrtMemoryAllocation _preparedStorage, _inferredStorage;
    private readonly OrtIoBinding _networkBinding;
    private OrtIoBinding? _prepareBinding, _packBinding;
    private readonly OrtMemoryAllocation _first, _second, _amount, _output;
    private readonly int _width, _height, _inputWidth, _inputHeight;
    private readonly long[] _shape;
    private bool _disposed;
    private readonly nint _preparedPointer, _inferredPointer;
    internal CudaVideoContext Cuda { get; private set; } = null!;
    internal int FrameBytes => checked(_width * _height * 4);
    internal int RowBytes => _width * 4;
    internal int InputRowBytes => _inputWidth * 4;
    private static readonly SemaphoreSlim CatalogGate = new(1);
    private static bool _catalogRegistered;

    internal static async Task<RifeGpuSession> CreateAsync(TranscodeOptions options, int width, int height,
        bool hdr, CancellationToken ct, bool preferCudaPixels = false)
    {
        var artifact = ValidateRequest(options, width, height, hdr);
        string path = artifact.Path, expected = artifact.Hash;
        using (var file = File.OpenRead(path))
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != expected)
                throw new InvalidDataException($"RIFE 模型校验失败：{path}");
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_catalogRegistered)
            {
                _ = OrtEnv.Instance();
                var catalog = ExecutionProviderCatalog.GetDefault();
                await catalog.RegisterCertifiedAsync();
                if (!OrtEnv.Instance().GetEpDevices().Any(d => d.EpName.Equals("NvTensorRTRTXExecutionProvider", StringComparison.OrdinalIgnoreCase)))
                {
                    var provider = catalog.FindAllProviders().FirstOrDefault(p => p.Name.Equals("NvTensorRTRTXExecutionProvider", StringComparison.OrdinalIgnoreCase));
                    if (provider != null)
                    {
                        PipelineLog.Info("正在准备 Windows ML NVIDIA GPU 提供程序。");
                        var result = await provider.EnsureReadyAsync().AsTask(ct).ConfigureAwait(false);
                        if (result.Status != ExecutionProviderReadyResultState.Success || !provider.TryRegister())
                            throw new InvalidOperationException($"Windows ML NVIDIA GPU 提供程序不可用：{result.Status} {result.DiagnosticText}");
                    }
                }
                _catalogRegistered = OrtEnv.Instance().GetEpDevices().Any(d => d.EpName.Equals("NvTensorRTRTXExecutionProvider", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally { CatalogGate.Release(); }
        return await Task.Run(() => new RifeGpuSession(path, width, height, ct, preferCudaPixels), ct).ConfigureAwait(false);
    }

    internal static (string Path, string Hash) ValidateRequest(TranscodeOptions options, int width, int height, bool hdr)
    {
        if (options.InterpolationMultiplier is not (2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(options.InterpolationMultiplier));
        if (!Enum.IsDefined(options.InterpolationModel)) throw new ArgumentOutOfRangeException(nameof(options.InterpolationModel));
        if (hdr) throw new NotSupportedException("RIFE 当前仅支持 SDR 输入和输出。HDR 请改用 NVIDIA Vulkan 光流，或先将输入正确色调映射为 SDR。");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("RIFE GPU 后端需要 Windows 11 24H2 或更高版本的 Windows ML。");
        if (width < 1 || height < 1 || (long)width * height > 3840L * 2160)
            throw new NotSupportedException("RIFE 当前支持最高 3840×2160 像素；请降低 RTX Video 超分倍率。");
        string name = options.InterpolationModel == RifeModel.Lite ? "rife_v4.25_lite_v2.onnx" : "rife_v4.25_v2.onnx";
        string expected = options.InterpolationModel == RifeModel.Lite
            ? "610B5DE57CDCFBCCE9914C23E60A1CD357779A6F9582A1BCFCB035F8EB38509B"
            : "65C57A5E4ABB17AD67FAF35054291AC53AFFAB0506D509D2E66698F6ECD75584";
        return (Path.Combine(DemoPaths.RuntimeDir, name), expected);
    }

    private RifeGpuSession(string path, int width, int height, CancellationToken ct, bool preferCudaPixels)
    {
        _width = width; _height = height;
        _inputWidth = width <= 64 ? 128 : width;
        _inputHeight = height <= 64 ? 128 : height;
        try
        {
            var env = OrtEnv.Instance();
            var device = env.GetEpDevices().FirstOrDefault(d => d.HardwareDevice.Type == OrtHardwareDeviceType.GPU
                && d.EpName.Equals("NvTensorRTRTXExecutionProvider", StringComparison.OrdinalIgnoreCase))
                ?? throw new NotSupportedException("未找到 Windows ML NVIDIA TensorRT RTX GPU 提供程序。请先安装该硬件提供程序；不会回退 CPU 推理。");
            _hostStages = VideoPixelGraph.CreateStages(path, "rife", ct);
            var cache = Path.Combine(DemoPaths.RuntimeDir, "Cache", "TensorRT-RTX");
            Directory.CreateDirectory(cache);
            InferenceSession Session(string model, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                using var settings = new SessionOptions { EnableMemoryPattern = false, EnableCpuMemArena = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                foreach (var key in new[] { "height", "h", "in_height" }) settings.AddFreeDimensionOverrideByName(key, _inputHeight);
                foreach (var key in new[] { "width", "w", "in_width" }) settings.AddFreeDimensionOverrideByName(key, _inputWidth);
                foreach (var key in new[] { "batch", "batch_size" }) settings.AddFreeDimensionOverrideByName(key, 1);
                settings.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
                // NR/VSR/NVENC 与其他 GPU 会话可并发，沿用组合任务禁用 CUDA Graph 捕获的已验证策略。
                settings.AppendExecutionProvider(env, [device], new Dictionary<string, string>
                { ["nv_runtime_cache_path"] = cache, ["enable_cuda_graph"] = "0" });
                return Own(new InferenceSession(model, settings));
            }
            _createSession = Session;
            _network = Session(path, ct);
            var input = _network.InputMetadata.Single();
            if (input.Value.ElementDataType != TensorElementType.Float || input.Value.Dimensions.Length != 4 || input.Value.Dimensions[1] != 7)
                throw new InvalidDataException("RIFE 模型输入必须是 [1,7,H,W] FP32。");
            using var info = device.GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
            var gpu = Own(new OrtAllocator(_network, info));
            uint plane = checked((uint)(_inputWidth * _inputHeight));
            _first = Own(OrtAllocator.DefaultInstance.Allocate(plane * 4));
            _second = Own(OrtAllocator.DefaultInstance.Allocate(plane * 4));
            _amount = Own(OrtAllocator.DefaultInstance.Allocate(4));
            _output = Own(OrtAllocator.DefaultInstance.Allocate(plane * 4));
            var prepared = Own(gpu.Allocate(plane * 28));
            var inferred = Own(gpu.Allocate(plane * 12));
            _preparedStorage = prepared; _inferredStorage = inferred;
            _preparedPointer = prepared.DangerousGetHandle(); _inferredPointer = inferred.DangerousGetHandle();
            int deviceId = device.EpOptions.Entries.TryGetValue("device_id", out var id) && int.TryParse(id, out var ordinal) ? ordinal : 0;
            Cuda = Own(new CudaVideoContext(deviceId));
            _shape = [1, _inputHeight, _inputWidth, 4];
            _networkBinding = Own(_network.CreateIoBinding());
            long[] networkInput = [1, 7, _inputHeight, _inputWidth], networkOutput = [1, 3, _inputHeight, _inputWidth];
            _networkBinding.BindInput(input.Key, TensorElementType.Float, networkInput, prepared);
            _networkBinding.BindOutput(_network.OutputNames.Single(), TensorElementType.Float, networkOutput, inferred);
            First.Clear(); Second.Clear();
            if (preferCudaPixels) { Cuda.ClearTensor(_preparedPointer, checked(plane * 7)); RunNetwork(ct); }
            else Run(.5f, ct);
            PipelineLog.Info($"RIFE GPU 已验证：{Path.GetFileName(path)}，{width}×{height}，Windows ML / {device.EpName}；{(preferCudaPixels ? "CUDA 显存帧" : "RGBA 上传/回读")}，中间张量保留显存，CPU 推理回退已禁用。");
        }
        catch
        {
            try { Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
            throw;
        }
    }

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
    private static unsafe Span<byte> Bytes(OrtMemoryAllocation resource) => new((void*)resource.DangerousGetHandle(), checked((int)resource.Size));
    internal Span<byte> First => Bytes(_first);
    internal Span<byte> Second => Bytes(_second);
    internal ReadOnlySpan<byte> Output => Bytes(_output);
    internal void SetFrame(ReadOnlySpan<byte> source, bool first)
    {
        if (source.Length != FrameBytes) throw new ArgumentException("RIFE 输入帧尺寸不符。");
        var target = first ? First : Second;
        for (int y = 0; y < _inputHeight; y++)
        {
            var row = source.Slice(Math.Min(y, _height - 1) * RowBytes, RowBytes);
            var destination = target.Slice(y * InputRowBytes, InputRowBytes);
            row.CopyTo(destination);
            for (int x = _width; x < _inputWidth; x++) row[^4..].CopyTo(destination.Slice(x * 4, 4));
        }
    }
    internal void Run(float amount, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        EnsureHostStages(ct);
        MemoryMarshal.Write(Bytes(_amount), in amount);
        _prepareBinding!.BindInput(VideoPixelGraph.Prefix + "first", TensorElementType.UInt8, _shape, _first);
        _prepareBinding!.BindInput(VideoPixelGraph.Prefix + "second", TensorElementType.UInt8, _shape, _second);
        _prepareBinding!.BindInput(VideoPixelGraph.Prefix + "amount", TensorElementType.Float, [1], _amount);
        using var run = new RunOptions();
        using var stop = ct.Register(static state => ((RunOptions)state!).Terminate = true, run);
        try
        {
            Execute(_prepare!, _prepareBinding!); Execute(_network, _networkBinding); Execute(_pack!, _packBinding!);
            ct.ThrowIfCancellationRequested();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        void Execute(InferenceSession session, OrtIoBinding binding)
        {
            ExceptionDispatchInfo? failure = null;
            try { binding.SynchronizeBoundInputs(); session.RunWithBinding(run, binding); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            // 取消也要等待在途 GPU 完成，才可复用或释放帧/张量。
            try { binding.SynchronizeBoundOutputs(); }
            catch (Exception ex) { failure ??= ExceptionDispatchInfo.Capture(ex); }
            failure?.Throw();
        }
    }
    private void EnsureHostStages(CancellationToken ct)
    {
        if (_prepare != null) return;
        _prepare = _createSession(_hostStages.Before, ct); _pack = _createSession(_hostStages.After, ct);
        _prepareBinding = Own(_prepare.CreateIoBinding()); _packBinding = Own(_pack.CreateIoBinding());
        _prepareBinding.BindOutput(_prepare.OutputNames.Single(), TensorElementType.Float, [1, 7, _inputHeight, _inputWidth], _preparedStorage);
        _packBinding.BindInput(_pack.InputNames.Single(), TensorElementType.Float, [1, 3, _inputHeight, _inputWidth], _inferredStorage);
        _packBinding.BindOutput(_pack.OutputNames.Single(), TensorElementType.Int32, [1, _inputHeight, _inputWidth], _output);
    }
    internal void RunCuda(CudaVideoFrame first, CudaVideoFrame second, CudaVideoFrame output, float amount, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        Cuda.PrepareRife(first, second, _preparedPointer, _inputWidth, _inputHeight, amount);
        RunNetwork(ct);
        Cuda.PackRife(_inferredPointer, _inputWidth, _inputHeight, output);
    }
    private void RunNetwork(CancellationToken ct)
    {
        using var run = new RunOptions();
        using var stop = ct.Register(static state => ((RunOptions)state!).Terminate = true, run);
        ExceptionDispatchInfo? failure = null;
        try { _networkBinding.SynchronizeBoundInputs(); _network.RunWithBinding(run, _networkBinding); }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        try { _networkBinding.SynchronizeBoundOutputs(); }
        catch (Exception ex) { failure ??= ExceptionDispatchInfo.Capture(ex); }
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        failure?.Throw();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        for (int i = _owned.Count - 1; i >= 0; i--)
            try { _owned[i].Dispose(); } catch (Exception ex) { failure ??= ExceptionDispatchInfo.Capture(ex); }
        _owned.Clear();
        failure?.Throw();
    }
}

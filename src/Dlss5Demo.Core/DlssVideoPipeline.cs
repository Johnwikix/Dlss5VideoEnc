using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

public sealed record TranscodeOptions
{
    public string HostDllPath { get; init; } = "";
    public string RuntimeDllPath { get; init; } = "";
    public string LogPath { get; init; } = "";
    /// <summary>Auto prefers NVDEC; a first-frame initialization failure retries software decoding.</summary>
    public VideoDecodeMode DecodeMode { get; init; } = VideoDecodeMode.Auto;
    /// <summary>NVIDIA CUDA device index used by NVDEC.</summary>
    public int DecodeDeviceIndex { get; init; }
    /// <summary>Enable DLSS NR, optical flow and temporal correction. False needs no NR runtime.</summary>
    public bool EnableNr { get; init; } = true;
    /// <summary>独立 GPU 补帧，支持 NVIDIA Vulkan 与 RIFE，默认关闭。</summary>
    public bool EnableInterpolation { get; init; }
    public bool UseGpuFramePipeline { get; init; } = true;
    /// <summary>Keep compatible SDR NR/SR frames on the GPU between NVDEC and RIFE.</summary>
    public bool UseGpuEnhancementPipeline { get; init; } = true;
    internal int GpuFrameCapacity { get; init; } = 5;
    public int InterpolationMultiplier { get; init; } = 2;
    internal bool UseLinearVulkanImages { get; init; } = true;
    public FrameInterpolationBackend InterpolationBackend { get; init; } = FrameInterpolationBackend.NvidiaOpticalFlow;
    public RifeModel InterpolationModel { get; init; } = RifeModel.Lite;
    public string VsrHostDllPath { get; init; } = "";
    public string VsrRuntimeDllPath { get; init; } = "";
    public string VsrLogPath { get; init; } = "";
    /// <summary>Optional worker host for applications embedding Core; must handle RtxVideoWorker.TryServe.</summary>
    public string VsrWorkerExecutablePath { get; init; } = "";
    /// <summary>RTX Video Super Resolution scale: 1=off, 2=2×, 4=4×.</summary>
    public int SuperResolutionScale { get; init; } = 1;
    /// <summary>RTX Video Super Resolution quality level (1..4).</summary>
    public int SuperResolutionQuality { get; init; } = 4;
    /// <summary>Run NR and its temporal guidance at source resolution before RTX Video SR.</summary>
    public bool NrBeforeSuperResolution { get; init; } = true;
    /// <summary>Overlap NR input uploads with prior GPU work using the host's FIFO queue.</summary>
    public bool UseAsyncNrQueue { get; init; } = true;
    /// <summary>Keep VSR output on the GPU for compatible NVENC exports.</summary>
    public bool UseGpuVsrEncoding { get; init; } = true;
    public DlssNrOptions Dlss { get; init; } = new();
    /// <summary>hevc_nvenc / h264_nvenc / libx264 / libx265。</summary>
    public string VideoEncoder { get; init; } = "hevc_nvenc";
    /// <summary>NVENC 为 -cq 值，libx264/5 为 -crf 值。</summary>
    public int Quality { get; init; } = 19;
    /// <summary>限制处理帧数（冒烟测试用）；null 表示全片。</summary>
    public int? MaxFrames { get; init; }
    public bool CopyAudio { get; init; } = true;
    public VideoTranscodeOptions Video { get; init; } = new();
    public bool EnablePreviewFrames { get; init; } = true;
    /// <summary>预览帧率与统计进度解耦，避免预览被半秒统计周期限制。</summary>
    public int PreviewFramesPerSecond { get; init; } = 60;
    public ITranscodePreviewSink? PreviewSink { get; init; }
}

public sealed record TranscodeProgress(
    long FramesDone, long TotalFrames, double Fps, TimeSpan Elapsed);

public sealed record TranscodeResult
{
    public required string OutputPath { get; init; }
    /// <summary>Processed source frames; interpolation multiplies encoded output frames separately.</summary>
    public required long FramesProcessed { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required double AverageFps { get; init; }
    public required long OutputBytes { get; init; }
}

/// <summary>
/// 进程内解码（软解/NVDEC → RGBA 帧）→ DLSS NR 逐帧处理 → 进程内编码、音频与封装。无 ffmpeg 子进程。
/// </summary>
public static partial class DlssVideoPipeline
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// NGX 宿主是进程级单例状态（重复 init 会崩溃、调用须串行化）：
    /// 上一会话 shutdown 完成前不允许下一次 init，整条管线在此门上串行。
    /// </summary>
    private static readonly SemaphoreSlim PipelineGate = new(1, 1);

    public static async Task<TranscodeResult> RunAsync(
        string inputPath, string outputPath, TranscodeOptions options,
        IProgress<TranscodeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await PipelineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TranscodeResult result;
            try { result = await RunCoreAsync(inputPath, outputPath, options, progress, cancellationToken).ConfigureAwait(false); }
            catch { ResetRifeSession(); throw; }
            PipelineLog.Info($"管线完成: {result.FramesProcessed} 帧 · {result.AverageFps:0.0} fps → {result.OutputPath}");
            return result;
        }
        catch (OperationCanceledException)
        {
            PipelineLog.Info("管线已取消。");
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // 取消会让解码中断回调生效：随后的管道中断与失败都是取消的连带产物，
            // 统一归并为取消，不能当成转码失败报告给 UI。
            PipelineLog.Info($"取消时归并 {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            throw new OperationCanceledException("转码已取消。", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            PipelineLog.Info($"管线失败: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            throw;
        }
        finally
        {
            PipelineGate.Release();
        }
    }

    private static async Task<TranscodeResult> RunCoreAsync(
        string inputPath, string outputPath, TranscodeOptions options,
        IProgress<TranscodeProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("输入视频不存在。", inputPath);

        if (options.MaxFrames is <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxFrames));
        options = NormalizeHdrOptions(options);
        var video = MediaProbe.Probe(inputPath);
        ValidateDecodeOptions(options, video);
        ValidateDriverHdrOutput(options, video);
        if (options.EnableInterpolation && video.FrameCount == 0
            && await VulkanFrameInterpolation.IsSingleFrameAsync(inputPath, cancellationToken).ConfigureAwait(false))
            video = video with { FrameCount = 1 };
        var scale = RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale);
        var plan = CreateProcessingPlan(video, scale, options.Video.PreserveHdr, !options.EnableNr || options.NrBeforeSuperResolution);
        options = NormalizeOutputScale(options, plan.ProcessWidth, plan.ProcessHeight);
        if ((plan.SourceWidth & 1) != 0 || (plan.SourceHeight & 1) != 0)
            throw new NotSupportedException($"暂不支持奇数尺寸视频（当前 {plan.SourceWidth}x{plan.SourceHeight}）。");

        if (options.EnableInterpolation)
        {
            if (!Enum.IsDefined(options.InterpolationBackend)) throw new ArgumentOutOfRangeException(nameof(options.InterpolationBackend));
            if (options.InterpolationMultiplier is not (2 or 4 or 6)) throw new ArgumentOutOfRangeException(nameof(options.InterpolationMultiplier));
            if (!double.IsFinite(video.Fps) || video.Fps <= 0) throw new NotSupportedException("补帧需要有效的源视频帧率。");
        }
        using var hdrMapper = NeedsDriverHdr(options, video)
            ? new DriverHdrMapper(plan.ProcessWidth, plan.ProcessHeight) : null;
        var rife = options.EnableInterpolation && options.InterpolationBackend == FrameInterpolationBackend.Rife
            ? await AcquireRifeSessionAsync(options, video, plan.ProcessWidth, plan.ProcessHeight, cancellationToken).ConfigureAwait(false) : null;
        if (rife == null) ResetRifeSession();
        if (options.EnableInterpolation)
        {
            if (rife == null)
                options = options with { UseLinearVulkanImages = await VulkanFrameInterpolation.ValidateAsync(options, video, plan.Hdr, cancellationToken, options.UseLinearVulkanImages).ConfigureAwait(false) };
        }

        using var vulkan = options.EnableInterpolation && options.InterpolationBackend == FrameInterpolationBackend.NvidiaOpticalFlow
            ? new VulkanInterpolationSession(plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, video, options.InterpolationMultiplier, options.UseLinearVulkanImages) : null;
        using var outputOwnership = OutputFileNames.Reserve(outputPath);
        outputPath = outputOwnership.Path;
        PipelineLog.Info($"管线开始: {Path.GetFileName(inputPath)} → {Path.GetFileName(outputPath)} · {options.VideoEncoder}");

        var totalFrames = options.MaxFrames is { } limit
            ? Math.Min(limit, video.FrameCount > 0 ? video.FrameCount : limit)
            : video.FrameCount;

        if (options.Video.TwoPassEnabled && IsTwoPassSupported(options.Video))
            return await RunTwoPassAsync(inputPath, outputPath, options, video, totalFrames, progress, cancellationToken, rife, vulkan, hdrMapper).ConfigureAwait(false);

        if (rife != null && CanUseCudaRife(options, video) && rife.Cuda.DeviceId == options.DecodeDeviceIndex)
        {
            try { return await RunCudaRifeAsync(inputPath, outputPath, options, video, totalFrames, rife, progress, cancellationToken).ConfigureAwait(false); }
            catch (CudaDecodeUnavailableException ex) when (options.DecodeMode == VideoDecodeMode.Auto)
            { PipelineLog.Info(ex.Message + " 自动模式从头重试主机帧路径。"); }
            catch (CudaEnhancementUnavailableException ex)
            { PipelineLog.Info(ex.Message + " 从头使用 NR/SR 主机传输路径。"); }
        }

        using var vsr = await Task.Run(() => CreateVsr(options, plan, video, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var host = options.EnableNr ? DlssNrHost.Create(
            options.HostDllPath, options.RuntimeDllPath, options.LogPath,
            plan.NrWidth, plan.NrHeight, options.Dlss, plan.Hdr, video.ColorTransfer) : null;

        await using var decoder = new FrameDecoder(inputPath, options, video, plan.Hdr, cancellationToken);
        (long Frames, TimeSpan Elapsed) outcome;
        if (vsr?.GpuEncoding == true)
        {
            using var cancellation = cancellationToken.Register(() => { decoder.Abort(); vsr.Abort(); });
            outcome = await Task.Run(() => RunFrameStream(
                decoder, Stream.Null, host, vsr, plan, totalFrames, options.LogPath, progress,
                options.EnablePreviewFrames ? options.PreviewSink : null, options.PreviewFramesPerSecond,
                video.Fps, options.UseAsyncNrQueue, cancellationToken)).ConfigureAwait(false);
            if (totalFrames > 0 && outcome.Frames < totalFrames)
                throw new InvalidOperationException($"解码提前结束：期望 {totalFrames} 帧，只读到 {outcome.Frames} 帧。");
            var finalize = Stopwatch.StartNew();
            vsr.FinishEncoding();
            await Task.Run(() => RemuxEncoded(inputPath, outputPath, options, video,
                vsr.Encoder!.OutputPath, outcome.Frames), cancellationToken).ConfigureAwait(false);
            outcome.Elapsed += finalize.Elapsed;
            PipelineLog.Info($"VSR 编码排空与封装: {finalize.Elapsed.TotalMilliseconds:0} ms（计入最终处理耗时）");
        }
        else if (rife != null && CanEncodeRifeOnGpu(options))
        {
            var cache = Path.Combine(Path.GetTempPath(), $"dlss5-rife-{Guid.NewGuid():N}.nut");
            try
            {
                using var encoder = new CudaRifePipeline.HostInputEncoder(cache, options,
                    video with { Width = plan.ProcessWidth, Height = plan.ProcessHeight }, rife, cancellationToken);
                using var cancellation = cancellationToken.Register(decoder.Abort);
                outcome = await Task.Run(() => RunFrameStream(decoder, encoder, host, vsr, plan, totalFrames,
                    options.LogPath, progress, options.EnablePreviewFrames ? options.PreviewSink : null,
                    options.PreviewFramesPerSecond, video.Fps, options.UseAsyncNrQueue, cancellationToken)).ConfigureAwait(false);
                var finalize = Stopwatch.StartNew();
                encoder.Complete();
                if (totalFrames > 0 && outcome.Frames < totalFrames)
                    throw new InvalidOperationException($"解码提前结束：期望 {totalFrames} 帧，只读到 {outcome.Frames} 帧。");
                await Task.Run(() => RemuxEncoded(inputPath, outputPath, options, video, cache, encoder.OutputFrames), cancellationToken).ConfigureAwait(false);
                outcome.Elapsed += finalize.Elapsed;
            }
            finally { try { File.Delete(cache); } catch (IOException) { } }
        }
        else
        {
            await using var encoder = new RawVideoEncoder(inputPath, outputPath, options, video,
                plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, cancellationToken);
            using var cancellation = cancellationToken.Register(() => { decoder.Abort(); encoder.Close(); });
            outcome = await Task.Run(() => RunFrameLoop(
                decoder, encoder, host, vsr, plan, totalFrames, options.LogPath, progress,
                options.EnablePreviewFrames ? options.PreviewSink : null,
                options.PreviewFramesPerSecond, video.Fps, options.UseAsyncNrQueue, cancellationToken, rife, vulkan, options.InterpolationMultiplier, hdrMapper)).ConfigureAwait(false);
        }

        var outputInfo = new FileInfo(outputPath);
        return new TranscodeResult
        {
            OutputPath = outputPath,
            FramesProcessed = outcome.Frames,
            Elapsed = outcome.Elapsed,
            AverageFps = outcome.Frames / outcome.Elapsed.TotalSeconds,
            OutputBytes = outputInfo.Exists ? outputInfo.Length : 0,
        };
    }

    private static bool IsTwoPassSupported(VideoTranscodeOptions profile)
        => profile.Container != VideoContainer.Gif
           && profile.HardwareBackend == HardwareBackend.Software
           && profile.RateControl == RateControlMode.Bitrate
           && profile.VideoCodec is VideoCodec.H264 or VideoCodec.H265;

    internal static TranscodeOptions NormalizeOutputScale(TranscodeOptions options, int width, int height)
    {
        var video = options.Video;
        if (video.CropEnabled) return options; // Scaling applies after cropping, in a different coordinate space.
        bool sameSize = video.ScaleMode switch
        {
            ScaleMode.Exact or ScaleMode.FitWithin => Math.Max(16, video.ScaleWidth) == width && Math.Max(16, video.ScaleHeight) == height,
            ScaleMode.Width => Math.Max(16, video.ScaleWidth) == width,
            ScaleMode.Height => Math.Max(16, video.ScaleHeight) == height,
            _ => false,
        };
        if (!sameSize) return options;
        PipelineLog.Info($"输出尺寸已是 {width}×{height}：省略同尺寸缩放，保留兼容的 GPU 编码路径。");
        return options with { Video = video with { ScaleMode = ScaleMode.None } };
    }

    private readonly record struct ProcessingPlan(
        int SourceWidth, int SourceHeight, int ProcessWidth, int ProcessHeight, bool Hdr, bool NrFirst)
    {
        public int NrWidth => NrFirst ? SourceWidth : ProcessWidth;
        public int NrHeight => NrFirst ? SourceHeight : ProcessHeight;
        public long NrBytes => checked((long)NrWidth * NrHeight * (Hdr ? 8 : 4));
        public long SourceBytes => checked((long)SourceWidth * SourceHeight * (Hdr ? 8 : 4));
        public long ProcessBytes => checked((long)ProcessWidth * ProcessHeight * (Hdr ? 8 : 4));
    }

    private static ProcessingPlan CreateProcessingPlan(VideoInfo video, int scale, bool preserveHdr, bool nrFirst)
    {
        var hdr = video.IsHdr && preserveHdr;
        var processWidth = checked(video.Width * scale);
        var processHeight = checked(video.Height * scale);
        if (processWidth > 16384 || processHeight > 16384)
            throw new NotSupportedException($"RTX Video 超分后的尺寸超过 16384 限制（{processWidth}×{processHeight}）。");
        return new ProcessingPlan(video.Width, video.Height, processWidth, processHeight, hdr, nrFirst);
    }

    private static RtxVideoWorker? CreateVsr(
        TranscodeOptions options, ProcessingPlan plan, VideoInfo video, CancellationToken cancellationToken, bool deviceFrames = false)
    {
        if (plan.ProcessWidth == plan.SourceWidth && plan.ProcessHeight == plan.SourceHeight)
            return null;
        var host = string.IsNullOrWhiteSpace(options.VsrHostDllPath)
            ? DemoPaths.VsrHostDllPath : options.VsrHostDllPath;
        var runtime = string.IsNullOrWhiteSpace(options.VsrRuntimeDllPath)
            ? DemoPaths.VsrRuntimeDllPath : options.VsrRuntimeDllPath;
        var log = string.IsNullOrWhiteSpace(options.VsrLogPath) ? DemoPaths.VsrLogPath : options.VsrLogPath;
        return RtxVideoWorker.Create(
            host, runtime, log, plan.SourceWidth, plan.SourceHeight,
            plan.ProcessWidth / plan.SourceWidth, options.SuperResolutionQuality, plan.Hdr,
            deviceFrames ? 1 : FrameSlotCount(plan, options.EnableNr && options.Dlss.MotionGuidance, options.EnableNr && options.Dlss.TemporalStabilization),
            options.VsrWorkerExecutablePath, cancellationToken, deviceFrames ? null : CreateGpuEncoderConfiguration(options, plan, video), deviceFrames);
    }

    private static VsrEncoderConfiguration? CreateGpuEncoderConfiguration(TranscodeOptions options, ProcessingPlan plan, VideoInfo video)
    {
        var profile = options.Video;
        if (!options.UseGpuVsrEncoding || options.EnableInterpolation || !plan.NrFirst || profile.TwoPassEnabled || plan.Hdr
            || options.VideoEncoder is not ("hevc_nvenc" or "h264_nvenc" or "av1_nvenc")
            || profile.ForceHdr
            || profile.CropEnabled || profile.ScaleMode != ScaleMode.None || profile.Deinterlace != DeinterlaceMode.None
            || profile.Denoise != DenoiseMode.None || profile.FpsMode != FpsMode.SameAsSource || profile.Container == VideoContainer.Gif)
            return null;
        var rate = video.FrameRate.Split('/');
        if (!int.TryParse(rate[0], out var numerator) || numerator <= 0) return null;
        var denominator = rate.Length > 1 && int.TryParse(rate[1], out var parsed) ? parsed : 1;
        if (denominator <= 0) return null;
        var quality = Math.Clamp(profile.Quality > 0 ? profile.Quality : options.Quality, 0, 51);
        var preset = string.IsNullOrWhiteSpace(profile.EncoderPreset) ? "p5" : profile.EncoderPreset;
        // Do not pass arbitrary strings through av_dict_parse_string's separators.
        if (preset is not ("p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7")) return null;
        var settings = $"preset={preset}:tune=hq:rc=" + (profile.RateControl == RateControlMode.Bitrate
            ? $"vbr:b={Math.Max(100, profile.VideoBitrateKbps)}k" : $"constqp:cq={quality}:b=0");
        var preview = options.EnablePreviewFrames ? options.PreviewSink : null;
        var previewScale = preview == null ? 0 : Math.Min(1, Math.Min(
            preview.MaxFrameWidth > 0 ? (double)preview.MaxFrameWidth / plan.ProcessWidth : 1,
            preview.MaxFrameHeight > 0 ? (double)preview.MaxFrameHeight / plan.ProcessHeight : 1));
        return new VsrEncoderConfiguration
        {
            OutputPath = Path.Combine(Path.GetTempPath(), "dlss5-vsr-" + Guid.NewGuid().ToString("N") + ".nut"),
            Encoder = options.VideoEncoder, Options = settings, FpsNumerator = numerator, FpsDenominator = denominator,
            PreviewWidth = preview == null ? 0 : Math.Max(1, (int)(plan.ProcessWidth * previewScale)),
            PreviewHeight = preview == null ? 0 : Math.Max(1, (int)(plan.ProcessHeight * previewScale)),
        };
    }

    private static async Task<TranscodeResult> RunTwoPassAsync(
        string inputPath, string outputPath, TranscodeOptions options, VideoInfo video,
        long totalFrames, IProgress<TranscodeProgress>? progress, CancellationToken cancellationToken, RifeGpuSession? rife, VulkanInterpolationSession? vulkan, DriverHdrMapper? hdrMapper)
    {
        var plan = CreateProcessingPlan(video,
            RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale),
            options.Video.PreserveHdr, !options.EnableNr || options.NrBeforeSuperResolution);
        var rawPath = Path.Combine(Path.GetTempPath(), $"dlss5-{Guid.NewGuid():N}.rgba");
        var passLog = Path.Combine(Path.GetTempPath(), $"dlss5-{Guid.NewGuid():N}-pass");
        var started = Stopwatch.StartNew();
        try
        {
            (long Frames, TimeSpan Elapsed) outcome;
            {
                using var vsr = await Task.Run(() => CreateVsr(options, plan, video, cancellationToken), cancellationToken).ConfigureAwait(false);
                using var host = options.EnableNr ? DlssNrHost.Create(
                    options.HostDllPath, options.RuntimeDllPath, options.LogPath,
                    plan.NrWidth, plan.NrHeight, options.Dlss, plan.Hdr, video.ColorTransfer) : null;
                await using (var decoder = new FrameDecoder(inputPath, options, video, plan.Hdr, cancellationToken))
                await using (var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    using var cancellation = cancellationToken.Register(decoder.Abort);
                    using var hdr = hdrMapper == null ? null : new DriverHdrFrameStream(hdrMapper, raw, cancellationToken);
                    Stream destination = hdr ?? (Stream)raw;
                    using InterpolationFrameStream? interpolation = rife != null ? new RifeFrameStream(destination, rife, options.InterpolationMultiplier, cancellationToken)
                        : vulkan != null ? new VulkanFrameStream(destination, vulkan, cancellationToken) : null;
                    outcome = await Task.Run(() => RunFrameStream(
                        decoder, interpolation ?? destination, host, vsr, plan, totalFrames, options.LogPath,
                        progress, options.EnablePreviewFrames ? options.PreviewSink : null,
                        options.PreviewFramesPerSecond, video.Fps, options.UseAsyncNrQueue, cancellationToken)).ConfigureAwait(false);
                    interpolation?.Complete();
                    hdr?.Complete();
                }
            }

            await Task.Run(() =>
            {
                using var first = new RawVideoEncoder(inputPath, outputPath, options, video,
                    plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, cancellationToken, rawPath, pass: 1, passLog);
                first.RunFromRawFile();
            }, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                using var second = new RawVideoEncoder(inputPath, outputPath, options, video,
                    plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, cancellationToken, rawPath, pass: 2, passLog);
                second.RunFromRawFile();
            }, cancellationToken).ConfigureAwait(false);

            started.Stop();
            var outputInfo = new FileInfo(outputPath);
            return new TranscodeResult
            {
                OutputPath = outputPath,
                FramesProcessed = outcome.Frames,
                Elapsed = started.Elapsed,
                AverageFps = outcome.Frames / Math.Max(started.Elapsed.TotalSeconds, 0.001),
                OutputBytes = outputInfo.Exists ? outputInfo.Length : 0,
            };
        }
        finally
        {
            try { File.Delete(rawPath); } catch { }
            try { File.Delete(passLog); } catch { }
            try { File.Delete(passLog + "-0.log"); } catch { }
            try { File.Delete(passLog + "-0.log.mbtree"); } catch { }
            try { File.Delete(passLog + ".cutree"); } catch { }
            try { File.Delete(passLog + ".temp"); } catch { }
            try { File.Delete(passLog + ".cutree.temp"); } catch { }
        }
    }

    private static (long Frames, TimeSpan Elapsed) RunFrameStream(
        FrameDecoder decoder, Stream destination, DlssNrHost? host, RtxVideoWorker? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps, bool useAsyncNrQueue,
        CancellationToken cancellationToken)
        => RunFrameCore(decoder, destination, host, vsr, plan, totalFrames, logPath, progress,
            preview, previewFramesPerSecond, sourceFps, useAsyncNrQueue, decoder.Abort, cancellationToken);

    private static (long Frames, TimeSpan Elapsed) RunFrameLoop(
        FrameDecoder decoder, RawVideoEncoder encoder, DlssNrHost? host, RtxVideoWorker? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps, bool useAsyncNrQueue,
        CancellationToken cancellationToken, RifeGpuSession? rife, VulkanInterpolationSession? vulkan, int multiplier, DriverHdrMapper? hdrMapper)
    {
        var stopwatch = Stopwatch.StartNew();
        using var hdr = hdrMapper == null ? null : new DriverHdrFrameStream(hdrMapper, encoder, cancellationToken);
        Stream destination = hdr ?? (Stream)encoder;
        using InterpolationFrameStream? interpolation = rife != null ? new RifeFrameStream(destination, rife, multiplier, cancellationToken)
            : vulkan != null ? new VulkanFrameStream(destination, vulkan, cancellationToken) : null;
        var result = RunFrameCore(decoder, interpolation ?? destination, host, vsr, plan,
            totalFrames, logPath, progress, preview, previewFramesPerSecond, sourceFps, useAsyncNrQueue,
            decoder.Abort, cancellationToken);
        interpolation?.Complete();
        hdr?.Complete();
        encoder.Complete();
        if (totalFrames > 0 && result.Frames < totalFrames)
            throw new InvalidOperationException(
                $"解码提前结束：期望 {totalFrames} 帧，只读到 {result.Frames} 帧。");
        // Interpolation/encoding may still be draining on the GPU after the raw
        // producer reaches EOF. Include that work in final elapsed time and FPS.
        stopwatch.Stop();
        progress?.Report(new TranscodeProgress(result.Frames, totalFrames,
            result.Frames / Math.Max(stopwatch.Elapsed.TotalSeconds, .001), stopwatch.Elapsed));
        return (result.Frames, stopwatch.Elapsed);
    }

    private static int FrameSlotCount(ProcessingPlan plan, bool motion, bool history)
    {
        var bytes = plan.SourceBytes + plan.NrBytes;
        if (plan.SourceWidth != plan.ProcessWidth) bytes += plan.ProcessBytes;
        if (plan.Hdr) bytes += plan.ProcessBytes;
        var pixels = (long)plan.NrWidth * plan.NrHeight;
        if (motion) bytes += pixels * 9;
        if (history) bytes += pixels * 4;
        // The lower bound permits SR, NR and CPU output history to overlap. Large
        // frames use fewer slots; histories/driver allocations are separate.
        // Leave room for confidence metadata without dropping a whole in-flight
        // slot at common 1440p → 5K sizes. Histories/driver memory are separate.
        return (int)Math.Clamp(576L * 1024 * 1024 / Math.Max(bytes, 1), 3, 5);
    }

    private static unsafe (long Frames, TimeSpan Elapsed) RunFrameCore(
        FrameDecoder decoder, Stream destination, DlssNrHost? host, RtxVideoWorker? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps, bool useAsyncNrQueue, Action abort,
        CancellationToken cancellationToken)
    {
        using var gpuNr = host != null ? GpuNrProcessor.TryCreate(host, plan.NrWidth, plan.NrHeight, plan.Hdr, sourceFps) : null;
        using var flow = gpuNr == null && host?.MotionGuidance == true
            ? NvidiaOpticalFlow.TryCreate(plan.NrWidth, plan.NrHeight, plan.Hdr, host.FineMotion ? 1 : 4) : null;
        if (gpuNr == null) host?.ConfigureMotionGuidance(flow != null);
        var temporal = gpuNr == null && host?.TemporalStabilization == true
            ? new DlssNrTemporalStabilizer(plan.NrWidth, plan.NrHeight, plan.Hdr, sourceFps, flow != null) : null;
        var slots = new VideoFrameSlot[vsr?.FrameCapacity ?? FrameSlotCount(plan, flow != null, temporal != null)];
        try
        {
            for (var index = 0; index < slots.Length; index++)
                slots[index] = new(index, plan.SourceBytes, plan.ProcessBytes, plan.Hdr,
                    plan.NrBytes, checked(plan.NrWidth * plan.NrHeight), flow != null, temporal != null, vsr, plan.NrFirst, host != null);
            var order = host == null ? vsr == null ? "直通" : "RTX Video SR"
                : plan.NrFirst ? "光流/输入验证 → NR → 历史 → SR" : "SR → 光流/输入验证 → NR → 历史";
            PipelineLog.Info($"有界帧管线: {slots.Length} 槽; 解码 → {order} → 编码/预览");
            if (host == null) PipelineLog.Info("DLSS NR 已关闭：跳过 NR、光流和历史累积，不加载 NR 运行库。");
            var stopwatch = Stopwatch.StartNew();
            var lastReport = -ProgressInterval;
            var previewInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(previewFramesPerSecond, 1, 60));
            var lastPreview = -previewInterval;
            long lastPreviewFrame = 0;
            VideoFrameSlot? lastOutput = null;
            var capacity = gpuNr == null && host != null && useAsyncNrQueue ? Math.Min(host.QueueCapacity, slots.Length - 1) : 1;
            if (host != null) PipelineLog.Info($"NR 提交队列: {capacity} 帧（单线程按序提交/回读）");
            if (vsr != null) PipelineLog.Info($"VSR 逐帧处理；GPU 编码={vsr.GpuEncoding}");
            var gpuEncoding = vsr?.GpuEncoding == true;
            var previewWidth = gpuEncoding ? vsr!.Encoder!.PreviewWidth : plan.ProcessWidth;
            var previewHeight = gpuEncoding ? vsr!.Encoder!.PreviewHeight : plan.ProcessHeight;
            var metrics = new PipelineStageMetrics("管线", "read", "vsr", "flow", "prepare", "nr", "history", "write", "preview", "nr_submit", "nr_readback");
            bool Read(VideoFrameSlot frame, long index)
            {
                if (totalFrames > 0 && index >= totalFrames) return false;
                var started = metrics.Start();
                if (!decoder.ReadFrame(new Span<byte>(frame.Source, checked((int)plan.SourceBytes)))) return false;
                if (plan.Hdr) NormalizeHdrFrame((ushort*)frame.Source, plan.SourceWidth * plan.SourceHeight * 4);
                metrics.Record(0, started);
                return true;
            }
            var lastCapture = -previewInterval;
            void SuperResolution(VideoFrameSlot frame, long index)
            {
                var capture = preview != null && (index + 1 == totalFrames
                    || totalFrames <= 0 || stopwatch.Elapsed - lastCapture >= previewInterval);
                var started = metrics.Start();
                vsr!.ProcessFrame(frame.Slot, capture);
                metrics.Record(1, started);
                frame.HasPreview = !gpuEncoding || capture;
                if (capture) lastCapture = stopwatch.Elapsed;
            }
            void Prepare(VideoFrameSlot frame, long index)
            {
                var started = metrics.Start();
                flow?.Calculate(frame.Input, index == 0, frame.Motion, frame.RejectedMotion);
                metrics.Record(2, started);
                started = metrics.Start();
                frame.Reset = temporal?.PrepareInput(frame.Input, frame.Motion, frame.Errors!) ?? index == 0;
                if (frame.Reset) flow?.ResetHistory();
                if (frame.Motion != null)
                    MotionReliability.Apply(frame.Motion, frame.RejectedMotion!, frame.Errors, !frame.Reset);
                metrics.Record(3, started);
            }
            void NeuralRendering(VideoFrameSlot frame, long index)
            {
                var started = metrics.Start();
                bool ok;
                fixed (float* motion = frame.Motion)
                    ok = host!.ProcessVideoFrame(frame.Input, motion, frame.Output, frame.Reset);
                metrics.Record(4, started);
                if (!ok) throw new InvalidOperationException($"第 {index + 1} 帧 DLSS NR 处理失败，详见 {logPath}");
            }
            void Accumulate(VideoFrameSlot frame, long index)
            {
                var started = metrics.Start();
                if (temporal != null)
                {
                    temporal.SetPreparedFrame(frame.Motion, frame.Errors!, frame.Reset);
                    temporal.Apply(frame.Input, frame.Output);
                }
                metrics.Record(5, started);
            }
            void DeviceNeuralRendering(VideoFrameSlot frame, long index)
            {
                var started = metrics.Start();
                gpuNr!.Process(frame.Input, frame.Output);
                metrics.Record(4, started);
            }
            void QueuedNeuralRendering(IEnumerable<BoundedFramePipeline.Frame<VideoFrameSlot>> input,
                Action<BoundedFramePipeline.Frame<VideoFrameSlot>> emit, CancellationToken token)
            {
                var pending = new Queue<BoundedFramePipeline.Frame<VideoFrameSlot>>(capacity);
                void Collect()
                {
                    token.ThrowIfCancellationRequested();
                    var frame = pending.Dequeue();
                    var started = metrics.Start();
                    var ok = host!.DequeueVideoFrame(frame.Buffer.Output);
                    metrics.Record(4, started);
                    metrics.Record(9, started);
                    if (!ok) throw new InvalidOperationException($"第 {frame.Index + 1} 帧 NR 回读失败，详见 {logPath}");
                    emit(frame);
                }
                foreach (var frame in input)
                {
                    token.ThrowIfCancellationRequested();
                    var started = metrics.Start();
                    bool ok;
                    fixed (float* motion = frame.Buffer.Motion)
                        ok = host!.EnqueueVideoFrame(frame.Buffer.Input, motion, frame.Buffer.Reset);
                    metrics.Record(4, started);
                    metrics.Record(8, started);
                    if (!ok) throw new InvalidOperationException($"第 {frame.Index + 1} 帧 NR 提交失败，详见 {logPath}");
                    pending.Enqueue(frame);
                    if (pending.Count >= capacity) Collect();
                }
                while (pending.Count > 0) Collect();
            }
            void Write(VideoFrameSlot frame, long index)
            {
                var started = metrics.Start();
                if (!gpuEncoding)
                {
                    if (plan.Hdr)
                        ConvertHdrToRgba64((ushort*)frame.FinalOutput, (ushort*)frame.Encoded, plan.ProcessWidth * plan.ProcessHeight * 4);
                    destination.Write(new ReadOnlySpan<byte>(frame.Encoded, checked((int)plan.ProcessBytes)));
                }
                metrics.Record(6, started);
                var frames = index + 1;
                lastOutput = frame;
                started = metrics.Start();
                if (preview != null && (!gpuEncoding || frame.HasPreview) && stopwatch.Elapsed - lastPreview >= previewInterval)
                {
                    PublishPreview(preview, frame.Source, plan.SourceWidth, plan.SourceHeight,
                        frame.FinalOutput, previewWidth, previewHeight, plan.Hdr, frames, totalFrames, sourceFps);
                    lastPreview = stopwatch.Elapsed;
                    lastPreviewFrame = frames;
                }
                metrics.Record(7, started);
                if (stopwatch.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = stopwatch.Elapsed;
                    progress?.Report(new TranscodeProgress(frames, totalFrames,
                        frames / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));
                }
            }
            var stages = new List<BoundedFramePipeline.Stage<VideoFrameSlot>>();
            if (vsr != null && !plan.NrFirst) stages.Add(BoundedFramePipeline.PerFrame<VideoFrameSlot>(SuperResolution));
            if (host != null)
            {
                if (gpuNr != null) stages.Add(BoundedFramePipeline.PerFrame<VideoFrameSlot>(DeviceNeuralRendering));
                else
                {
                    stages.Add(BoundedFramePipeline.PerFrame<VideoFrameSlot>(Prepare));
                    stages.Add(capacity > 1 ? QueuedNeuralRendering : BoundedFramePipeline.PerFrame<VideoFrameSlot>(NeuralRendering));
                    stages.Add(BoundedFramePipeline.PerFrame<VideoFrameSlot>(Accumulate));
                }
            }
            if (vsr != null && plan.NrFirst) stages.Add(BoundedFramePipeline.PerFrame<VideoFrameSlot>(SuperResolution));
            var processed = BoundedFramePipeline.RunStages(slots, Read, stages.ToArray(), Write,
                () => { vsr?.Abort(); abort(); }, cancellationToken);
            // EOF/limit reads never overwrite a slot. The final written frame is
            // still valid here after all workers have joined.
            if (preview != null && processed > 0 && lastPreviewFrame != processed && lastOutput != null)
                PublishPreview(preview, lastOutput.Source, plan.SourceWidth, plan.SourceHeight,
                    lastOutput.FinalOutput, previewWidth, previewHeight, plan.Hdr, processed, totalFrames, sourceFps);
            destination.Flush();
            stopwatch.Stop();
            metrics.Report(processed);
            flow?.ReportMetrics(processed);
            gpuNr?.ReportMetrics(processed);
            progress?.Report(new TranscodeProgress(processed, totalFrames,
                processed / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));
            return (processed, stopwatch.Elapsed);
        }
        finally
        {
            foreach (var slot in slots) slot?.Dispose();
        }
    }

    private static unsafe void NormalizeHdrFrame(ushort* buffer, int values)
    {
        for (var index = 0; index < values; index++)
            buffer[index] = (ushort)BitConverter.HalfToInt16Bits((Half)(buffer[index] / 65535f));
    }

    private static unsafe void ConvertHdrToRgba64(ushort* sourceHalf, ushort* destination, int values)
    {
        for (var index = 0; index < values; index++)
        {
            var value = (float)BitConverter.Int16BitsToHalf((short)sourceHalf[index]);
            destination[index] = (ushort)Math.Clamp(MathF.Round(value * 65535f), 0, 65535);
        }
    }

    private static unsafe void PublishPreview(ITranscodePreviewSink preview,
        byte* source, int sourceWidth, int sourceHeight, byte* processed,
        int processedWidth, int processedHeight, bool hdr,
        long frames, long totalFrames, double sourceFps)
    {
        if (!hdr)
        {
            preview.Publish(new ReadOnlySpan<byte>(source, checked(sourceWidth * sourceHeight * 4)),
                sourceWidth, sourceHeight,
                new ReadOnlySpan<byte>(processed, checked(processedWidth * processedHeight * 4)),
                processedWidth, processedHeight, frames, totalFrames, sourceFps);
            return;
        }
        var targetWidth = Math.Max(sourceWidth, processedWidth);
        var targetHeight = Math.Max(sourceHeight, processedHeight);
        if (preview.MaxFrameWidth > 0 && preview.MaxFrameHeight > 0)
        {
            var scale = Math.Min(1, Math.Min((double)preview.MaxFrameWidth / targetWidth, (double)preview.MaxFrameHeight / targetHeight));
            targetWidth = Math.Max(1, (int)(targetWidth * scale));
            targetHeight = Math.Max(1, (int)(targetHeight * scale));
        }
        else { targetWidth = 0; targetHeight = 0; }
        var sourcePreviewWidth = targetWidth > 0 ? targetWidth : sourceWidth;
        var sourcePreviewHeight = targetHeight > 0 ? targetHeight : sourceHeight;
        var processedPreviewWidth = targetWidth > 0 ? targetWidth : processedWidth;
        var processedPreviewHeight = targetHeight > 0 ? targetHeight : processedHeight;
        var sourceLength = checked(sourcePreviewWidth * sourcePreviewHeight * 4);
        var processedLength = checked(processedPreviewWidth * processedPreviewHeight * 4);
        var sourcePreview = ArrayPool<byte>.Shared.Rent(sourceLength);
        try
        {
            var processedPreview = ArrayPool<byte>.Shared.Rent(processedLength);
            try
            {
                FillHdrPreview((ushort*)source, sourceWidth, sourceHeight, sourcePreview.AsSpan(0, sourceLength), sourcePreviewWidth, sourcePreviewHeight);
                FillHdrPreview((ushort*)processed, processedWidth, processedHeight, processedPreview.AsSpan(0, processedLength), processedPreviewWidth, processedPreviewHeight);
                preview.Publish(sourcePreview.AsSpan(0, sourceLength), sourcePreviewWidth, sourcePreviewHeight,
                    processedPreview.AsSpan(0, processedLength), processedPreviewWidth, processedPreviewHeight, frames, totalFrames, sourceFps);
            }
            finally { ArrayPool<byte>.Shared.Return(processedPreview); }
        }
        finally { ArrayPool<byte>.Shared.Return(sourcePreview); }
    }

    internal static unsafe void FillHdrPreview(ushort* source, int width, int height,
        Span<byte> result, int targetWidth, int targetHeight)
    {
        for (var y = 0; y < targetHeight; y++)
        {
            var row = (int)((long)y * height / targetHeight) * width;
            for (var x = 0; x < targetWidth; x++)
            {
                var pixel = row + (int)((long)x * width / targetWidth);
                var index = y * targetWidth + x;
                var r = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[pixel * 4]), 0, 1);
                var g = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[pixel * 4 + 1]), 0, 1);
                var b = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[pixel * 4 + 2]), 0, 1);
                result[index * 4] = ToSrgbByte(r);
                result[index * 4 + 1] = ToSrgbByte(g);
                result[index * 4 + 2] = ToSrgbByte(b);
                result[index * 4 + 3] = 255;
            }
        }
    }

    private static byte ToSrgbByte(float value)
    {
        var srgb = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(srgb * 255f), 0, 255);
    }
    private static string SoftwarePreset(string preset) => preset.ToLowerInvariant() switch
    {
        "p1" => "ultrafast",
        "p3" => "fast",
        "p5" or "" => "medium",
        "p7" => "slow",
        _ => preset,
    };

    private static string AudioEncoder(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "aac", AudioCodec.Mp3 => "libmp3lame", AudioCodec.Opus => "libopus",
        AudioCodec.Vorbis => "libvorbis", AudioCodec.Flac => "flac", AudioCodec.Ac3 => "ac3", _ => "aac"
    };

    private static string BuildFilterChain(VideoTranscodeOptions o, VideoInfo source, string encoder,
        bool interpolate = false, bool mappedHdr = false)
    {
        var filters = new List<string>();
        if (mappedHdr)
            filters.Add("format=gbrp16le,zscale=matrixin=gbr:matrix=bt2020nc:transferin=smpte2084:transfer=smpte2084:primariesin=bt2020:primaries=bt2020:rangein=full:range=limited,format=yuv420p10le");
        if (o.CropEnabled)
            filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None)
            filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? interpolate ? "bwdif=mode=send_frame" : "bwdif" : "yadif");
        if (o.Denoise != DenoiseMode.None)
            filters.Add(o.Denoise switch { DenoiseMode.Light => "hqdn3d=4:3:6:4.5", DenoiseMode.Medium => "hqdn3d=8:6:8:6", _ => "hqdn3d=12:8:12:8" });
        if (o.ScaleMode != ScaleMode.None)
        {
            var w = Math.Max(16, o.ScaleWidth); var h = Math.Max(16, o.ScaleHeight);
            filters.Add(o.ScaleMode switch
            {
                ScaleMode.FitWithin => $"scale=w={w}:h={h}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos",
                ScaleMode.Width => $"scale={w}:-2:flags=lanczos",
                ScaleMode.Height => $"scale=-2:{h}:flags=lanczos",
                _ => $"scale={w}:{h}:flags=lanczos",
            });
        }
        if (!interpolate && o.FpsMode != FpsMode.SameAsSource && o.FpsValue > 0) filters.Add($"fps={o.FpsValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (o.Container == VideoContainer.Gif)
        {
            if (!interpolate) filters.Add($"fps={Math.Clamp(o.GifFps, 1, 30)}");
            filters.Add($"scale={Math.Clamp(o.GifWidth, 64, 800)}:-2:flags=lanczos");
            var dither = string.IsNullOrWhiteSpace(o.GifDither) ? "bayer" : o.GifDither;
            var stats = o.GifStatsMode is "single" ? "single" : "diff";
            filters.Add($"split[s0][s1];[s0]palettegen=max_colors={Math.Clamp(o.GifMaxColors, 32, 256)}:stats_mode={stats}[p];[s1][p]paletteuse=dither={dither}");
        }
        if (!mappedHdr && o.Container != VideoContainer.Gif && o.PreserveHdr && (source.IsHdr || o.ForceHdr) && !encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            filters.Add("format=p010le");
        if (o.Container != VideoContainer.Gif && o.PreserveHdr && (source.IsHdr || o.ForceHdr))
        {
            // Rawvideo has no per-frame color tags. NVENC reads AVFrame tags,
            // so output codec options alone do not preserve PQ/HLG primaries.
            var primaries = string.IsNullOrWhiteSpace(source.ColorPrimaries) ? "bt2020" : source.ColorPrimaries;
            var transfer = string.IsNullOrWhiteSpace(source.ColorTransfer) ? "smpte2084" : source.ColorTransfer;
            var matrix = string.IsNullOrWhiteSpace(source.ColorSpace) ? "bt2020nc" : source.ColorSpace;
            filters.Add($"setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={matrix}");
        }
        return string.Join(',', filters);
    }
}

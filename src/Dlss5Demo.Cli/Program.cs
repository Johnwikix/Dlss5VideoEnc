using Dlss5Demo.Core;

if (RtxVideoWorker.TryServe(args)) return;

var input = args.Length > 0 ? args[0] : @"F:\Captures\2021.02.17-19.27.mp4";
var output = args.Length > 1 ? args[1] : "output_dlss.mp4";
int? maxFrames = null;
var options = new DlssNrOptions();
var encoder = "hevc_nvenc";
var quality = 19;
var superResolutionScale = 1;
var superResolutionQuality = 4;
var previewEnabled = true;
var nrFirst = true;
var enableNr = true;
var enableInterpolation = false;
var gpuFrames = true;
var interpolationMultiplier = 2;
var interpolationBackend = FrameInterpolationBackend.NvidiaOpticalFlow;
var rifeModel = RifeModel.Lite;
var nrQueue = true;
var gpuVsrEncoding = true;
var decodeMode = VideoDecodeMode.Auto;
var decodeDevice = 0;
var hdrMode = "auto";
string? nrRuntime = null;

for (var index = 2; index < args.Length - 1; index++)
{
    switch (args[index].ToLowerInvariant())
    {
        case "--frames": maxFrames = int.Parse(args[++index]); break;
        case "--style": options = options with { Style = int.Parse(args[++index]) }; break;
        case "--intensity": options = options with { Intensity = float.Parse(args[++index]) }; break;
        case "--local-tone": options = options with { LocalTone = float.Parse(args[++index]) }; break;
        case "--local-struct": options = options with { LocalStructure = float.Parse(args[++index]) }; break;
        case "--skin": options = options with { SkinStructure = float.Parse(args[++index]) }; break;
        case "--auto-mask": options = options with { UseAutoMask = args[++index] == "1" }; break;
        case "--temporal": options = options with { TemporalStabilization = args[++index] != "0" }; break;
        case "--gpu-temporal": options = options with { UseGpuTemporalProcessing = args[++index] != "0" }; break;
        case "--continuous-history": options = options with { ContinuousHistory = args[++index] != "0" }; break;
        case "--motion-guidance": options = options with { MotionGuidance = args[++index] != "0" }; break;
        case "--fine-motion": options = options with { FineMotion = args[++index] != "0" }; break;
        case "--encoder": encoder = args[++index]; break;
        case "--quality": quality = int.Parse(args[++index]); break;
        case "--sr": superResolutionScale = RtxVideoSuperResolutionHost.NormalizeScale(int.Parse(args[++index])); break;
        case "--sr-quality": superResolutionQuality = Math.Clamp(int.Parse(args[++index]), 1, 4); break;
        case "--preview": previewEnabled = args[++index] != "0"; break;
        case "--nr-before-sr": nrFirst = args[++index] != "0"; break;
        case "--nr": enableNr = args[++index] != "0"; break;
        case "--nr-runtime": nrRuntime = args[++index]; break;
        case "--interpolation-backend": interpolationBackend = FrameInterpolationBackends.Parse(args[++index]); break;
        case "--rife-model": rifeModel = Enum.Parse<RifeModel>(args[++index], true); break;
        case "--gpu-frames": gpuFrames = args[++index] != "0"; break;
        case "--interpolate": enableInterpolation = args[++index] != "0"; break;
        case "--interpolation-multiplier": interpolationMultiplier = int.Parse(args[++index]); break;
        case "--nr-queue": nrQueue = args[++index] != "0"; break;
        case "--sr-gpu-encode": gpuVsrEncoding = args[++index] != "0"; break;
        case "--decoder": decodeMode = VideoDecodeModes.Parse(args[++index]); break;
        case "--decode-device": decodeDevice = int.Parse(args[++index]); break;
        case "--hdr": hdrMode = args[++index].ToLowerInvariant(); break;
    }
}

if (hdrMode is not ("auto" or "force" or "sdr"))
    throw new ArgumentException("--hdr 只能是 auto、force 或 sdr。");

Console.OutputEncoding = System.Text.Encoding.UTF8;
PipelineLog.Sink = message => Console.WriteLine($"[pipeline] {message}");
Console.WriteLine($"输入: {input}");
Console.WriteLine($"输出: {output}");
Console.WriteLine($"宿主: {DemoPaths.HostDllPath}");
var runtimeDllPath = enableNr ? NrRuntimeCatalog.ResolveArgument(nrRuntime) : "";
Console.WriteLine($"运行库: {runtimeDllPath}");
Console.WriteLine($"运行库配置: {DemoPaths.RuntimeProfile ?? "default"}");

if (enableNr)
{
    var adapters = DlssNrHost.EnumerateAdapters(DemoPaths.HostDllPath);
    foreach (var adapter in adapters)
        Console.WriteLine($"适配器: {adapter.Description} ({adapter.DedicatedVideoMemory / (1024 * 1024)} MiB)");
    if (adapters.Count == 0)
        throw new InvalidOperationException("未检测到可用于 DLSS 的 NVIDIA D3D12 GPU。");
}

var video = MediaProbe.Probe(input);
Console.WriteLine(
    $"视频: {video.Codec} {video.Width}x{video.Height} {video.PixelFormat} @{video.FrameRate} " +
    $"{video.FrameCount} 帧 / {video.DurationSeconds:F2}s，音频 {(video.HasAudio ? video.AudioCodec : "无")}");

var previewSink = new CountingPreviewSink();
var progress = new Progress<TranscodeProgress>(update =>
{
    Console.WriteLine(
        $"  进度 {update.FramesDone}/{update.TotalFrames} " +
        $"({update.FramesDone * 100 / Math.Max(update.TotalFrames, 1):F1}%) " +
        $"{update.Fps:F1} fps，已用 {update.Elapsed.TotalSeconds:F1}s");
});

var result = await DlssVideoPipeline.RunAsync(input, output, new TranscodeOptions
{
    FfmpegPath = DemoPaths.FfmpegPath,
    HostDllPath = DemoPaths.HostDllPath,
    RuntimeDllPath = runtimeDllPath,
    LogPath = DemoPaths.LogPath,
    Dlss = options,
    EnableNr = enableNr,
    DecodeMode = decodeMode,
    DecodeDeviceIndex = decodeDevice,
    EnableInterpolation = enableInterpolation,
                UseGpuFramePipeline = gpuFrames,
    InterpolationMultiplier = interpolationMultiplier,
                InterpolationBackend = interpolationBackend,
                InterpolationModel = rifeModel,
    VideoEncoder = encoder,
    Quality = quality,
    MaxFrames = maxFrames,
    SuperResolutionScale = superResolutionScale,
    SuperResolutionQuality = superResolutionQuality,
    NrBeforeSuperResolution = nrFirst,
    UseAsyncNrQueue = nrQueue,
    UseGpuVsrEncoding = gpuVsrEncoding,
    EnablePreviewFrames = previewEnabled,
    PreviewSink = previewSink,
    Video = new VideoTranscodeOptions { ForceHdr = hdrMode == "force", PreserveHdr = hdrMode != "sdr" },
}, progress);

Console.WriteLine(
    $"完成: {result.FramesProcessed} 帧，平均 {result.AverageFps:F1} fps，" +
    $"耗时 {result.Elapsed.TotalSeconds:F1}s，输出 {result.OutputBytes / (1024.0 * 1024.0):F1} MiB → {result.OutputPath}");
if (enableInterpolation) Console.WriteLine($"补帧输出: {result.FramesProcessed * interpolationMultiplier} 帧（{interpolationMultiplier}×；处理速度按源帧统计）");
Console.WriteLine($"同步预览: {previewSink.Pairs} 对帧，尺寸不一致 {previewSink.MismatchedSizes} 次");

DlssVideoPipeline.ReleaseGpuResources();

sealed class CountingPreviewSink : ITranscodePreviewSink
{
    public int MaxFrameWidth => 960;
    public int MaxFrameHeight => 540;
    public long Pairs { get; private set; }
    public long MismatchedSizes { get; private set; }

    public void Publish(ReadOnlySpan<byte> sourceRgba, int sourceWidth, int sourceHeight,
        ReadOnlySpan<byte> processedRgba, int processedWidth, int processedHeight,
        long framesDone, long totalFrames, double sourceFps)
    {
        Pairs++;
        if (sourceRgba.Length != sourceWidth * sourceHeight * 4 ||
            processedRgba.Length != processedWidth * processedHeight * 4)
            MismatchedSizes++;
    }

    public void Publish(ReadOnlySpan<byte> sourceRgba, ReadOnlySpan<byte> processedRgba,
        int width, int height, long framesDone, long totalFrames, double sourceFps)
        => Publish(sourceRgba, width, height, processedRgba, width, height,
            framesDone, totalFrames, sourceFps);
}

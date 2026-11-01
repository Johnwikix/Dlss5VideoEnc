using System.Diagnostics;
using Dlss5Demo.Core;

if (RtxVideoWorker.TryServe(args)) return 0;

// 位置参数全部视为输入（文件或目录；目录展开受支持扩展名的视频）。
// 输出统一用 --output（仅单输入）或 --output-dir（多输入，默认源文件同目录）自动命名。
// 每个 flag 都带一个值：预扫描时成对跳过，避免 flag 值被误认成输入路径。
var positional = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index].StartsWith("--", StringComparison.Ordinal)) { index++; continue; }
    positional.Add(args[index]);
}
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
var gpuEnhancement = true;
var interpolationMultiplier = 2;
var interpolationBackend = FrameInterpolationBackend.NvidiaOpticalFlow;
var rifeModel = RifeModel.Lite;
var nrQueue = true;
var gpuVsrEncoding = true;
var decodeMode = VideoDecodeMode.Auto;
var decodeDevice = 0;
var hdrMode = "auto";
string? nrRuntime = null;
string? outputOverride = null;
string? outputDir = null;

for (var index = 0; index < args.Length; index++)
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
        case "--gpu-enhancement": gpuEnhancement = args[++index] != "0"; break;
        case "--interpolate": enableInterpolation = args[++index] != "0"; break;
        case "--interpolation-multiplier": interpolationMultiplier = int.Parse(args[++index]); break;
        case "--nr-queue": nrQueue = args[++index] != "0"; break;
        case "--sr-gpu-encode": gpuVsrEncoding = args[++index] != "0"; break;
        case "--decoder": decodeMode = VideoDecodeModes.Parse(args[++index]); break;
        case "--decode-device": decodeDevice = int.Parse(args[++index]); break;
        case "--hdr": hdrMode = args[++index].ToLowerInvariant(); break;
        case "--output": outputOverride = args[++index]; break;
        case "--output-dir": outputDir = args[++index]; break;
    }
}

if (hdrMode is not ("auto" or "force" or "sdr"))
    throw new ArgumentException("--hdr 只能是 auto、force 或 sdr。");

var inputs = ExpandInputs(positional);
if (inputs.Count == 0)
{
    Console.WriteLine("用法: Dlss5TestCli <输入文件|目录>... [--output 输出(单输入)] [--output-dir 输出目录] [flags]");
    Console.WriteLine("示例: Dlss5TestCli a.mp4 b.mkv --frames 60 · Dlss5TestCli F:\\Captures --output-dir F:\\Out");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
PipelineLog.Sink = message => Console.WriteLine($"[pipeline] {message}");
Console.WriteLine($"输入: {inputs.Count} 个文件（批量模式，顺序处理）");
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

// Ctrl+C：取消当前文件并跳过剩余项（NGX 管线全局串行，取消后仍逐项收尾清理）。
using var batchCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Console.WriteLine("\n收到 Ctrl+C，正在取消当前文件并跳过剩余项…");
    batchCancellation.Cancel();
};

var extension = encoder.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif" : ".mp4";
var previewSink = new CountingPreviewSink();
var transcodeOptions = new TranscodeOptions
{
    HostDllPath = DemoPaths.HostDllPath,
    RuntimeDllPath = runtimeDllPath,
    LogPath = DemoPaths.LogPath,
    Dlss = options,
    EnableNr = enableNr,
    DecodeMode = decodeMode,
    DecodeDeviceIndex = decodeDevice,
    EnableInterpolation = enableInterpolation,
    UseGpuFramePipeline = gpuFrames,
    UseGpuEnhancementPipeline = gpuEnhancement,
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
};

var batchStopwatch = Stopwatch.StartNew();
int done = 0, failed = 0, skipped = 0;
for (var index = 0; index < inputs.Count; index++)
{
    var input = inputs[index];
    var name = Path.GetFileName(input);
    if (batchCancellation.IsCancellationRequested)
    {
        Console.WriteLine($"[{index + 1}/{inputs.Count}] 跳过: {name}");
        skipped++;
        continue;
    }
    Console.WriteLine($"\n[{index + 1}/{inputs.Count}] 输入: {input}");
    var output = ResolveOutput(input);
    Console.WriteLine($"输出: {output}");
    try
    {
        var video = MediaProbe.Probe(input);
        Console.WriteLine(
            $"视频: {video.Codec} {video.Width}x{video.Height} {video.PixelFormat} @{video.FrameRate} " +
            $"{video.FrameCount} 帧 / {video.DurationSeconds:F2}s，音频 {(video.HasAudio ? video.AudioCodec : "无")}");
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"[失败] {name}: 探测失败 - {error.Message.Split('\n')[0]}");
        continue;
    }

    try
    {
        var progress = new Progress<TranscodeProgress>(update =>
        {
            Console.WriteLine(
                $"  [{name}] 进度 {update.FramesDone}/{update.TotalFrames} " +
                $"({update.FramesDone * 100 / Math.Max(update.TotalFrames, 1):F1}%) " +
                $"{update.Fps:F1} fps，已用 {update.Elapsed.TotalSeconds:F1}s");
        });
        var result = await DlssVideoPipeline.RunAsync(input, output, transcodeOptions, progress, batchCancellation.Token);
        done++;
        Console.WriteLine(
            $"完成: {result.FramesProcessed} 帧，平均 {result.AverageFps:F1} fps，" +
            $"耗时 {result.Elapsed.TotalSeconds:F1}s，输出 {result.OutputBytes / (1024.0 * 1024.0):F1} MiB → {result.OutputPath}");
        if (enableInterpolation)
            Console.WriteLine($"补帧输出: {result.FramesProcessed * interpolationMultiplier} 帧（{interpolationMultiplier}×；处理速度按源帧统计）");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"[取消] {name} · 队列停止");
        break;
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"[失败] {name}: {error.Message.Split('\n')[0]}");
    }
}

batchStopwatch.Stop();
Console.WriteLine($"\n批量结束: 成功 {done} / 失败 {failed} / 跳过 {skipped} · 总耗时 {batchStopwatch.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"同步预览: {previewSink.Pairs} 对帧，尺寸不一致 {previewSink.MismatchedSizes} 次");
DlssVideoPipeline.ReleaseGpuResources();
return failed > 0 ? 1 : 0;

static List<string> ExpandInputs(List<string> positional)
{
    var inputs = new List<string>();
    foreach (var candidate in positional)
    {
        var path = candidate.Trim().Trim('"');
        if (Directory.Exists(path))
        {
            inputs.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Where(file => VideoCatalog.VideoExtensions.Contains(Path.GetExtension(file)))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            inputs.Add(path);
        }
    }
    return inputs;
}

string ResolveOutput(string input)
{
    if (inputs.Count == 1 && !string.IsNullOrWhiteSpace(outputOverride))
        return OutputFileNames.Available(outputOverride);
    var output = inputs.Count == 1 && outputOverride is null
        ? "output_dlss" + extension
        : OutputFileNames.ForInput(input, extension);
    if (!string.IsNullOrWhiteSpace(outputDir))
    {
        Directory.CreateDirectory(outputDir!);
        output = Path.Combine(outputDir!, Path.GetFileName(output));
    }
    return OutputFileNames.Available(output);
}

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

file static class VideoCatalog
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".ts", ".webm",
    };
}

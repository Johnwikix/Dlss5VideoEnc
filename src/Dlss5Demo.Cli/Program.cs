using Dlss5Demo.Core;

var input = args.Length > 0 ? args[0] : @"F:\Captures\2021.02.17-19.27.mp4";
var output = args.Length > 1 ? args[1] : "output_dlss.mp4";
int? maxFrames = null;
var options = new DlssNrOptions();
var encoder = "hevc_nvenc";
var quality = 19;

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
        case "--encoder": encoder = args[++index]; break;
        case "--quality": quality = int.Parse(args[++index]); break;
    }
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"输入: {input}");
Console.WriteLine($"输出: {output}");
Console.WriteLine($"宿主: {DemoPaths.HostDllPath}");
Console.WriteLine($"运行库: {DemoPaths.RuntimeDllPath}");
Console.WriteLine($"运行库配置: {DemoPaths.RuntimeProfile ?? "default"}");

var adapters = DlssNrHost.EnumerateAdapters(DemoPaths.HostDllPath);
foreach (var adapter in adapters)
    Console.WriteLine($"适配器: {adapter.Description} ({adapter.DedicatedVideoMemory / (1024 * 1024)} MiB)");
if (adapters.Count == 0)
    throw new InvalidOperationException("未检测到可用于 DLSS 的 NVIDIA D3D12 GPU。");

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
    HostDllPath = DemoPaths.HostDllPath,
    RuntimeDllPath = DemoPaths.RuntimeDllPath,
    LogPath = DemoPaths.LogPath,
    Dlss = options,
    VideoEncoder = encoder,
    Quality = quality,
    MaxFrames = maxFrames,
    PreviewSink = previewSink,
}, progress);

Console.WriteLine(
    $"完成: {result.FramesProcessed} 帧，平均 {result.AverageFps:F1} fps，" +
    $"耗时 {result.Elapsed.TotalSeconds:F1}s，输出 {result.OutputBytes / (1024.0 * 1024.0):F1} MiB → {result.OutputPath}");
Console.WriteLine($"同步预览: {previewSink.Pairs} 对帧，尺寸不一致 {previewSink.MismatchedSizes} 次");

sealed class CountingPreviewSink : ITranscodePreviewSink
{
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

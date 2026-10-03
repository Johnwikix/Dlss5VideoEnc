using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dlss5Demo.Core;

public sealed record TranscodeOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string HostDllPath { get; init; } = "";
    public string RuntimeDllPath { get; init; } = "";
    public string LogPath { get; init; } = "";
    public string VsrHostDllPath { get; init; } = "";
    public string VsrRuntimeDllPath { get; init; } = "";
    public string VsrLogPath { get; init; } = "";
    /// <summary>RTX Video Super Resolution scale: 1=off, 2=2×, 4=4×.</summary>
    public int SuperResolutionScale { get; init; } = 1;
    /// <summary>RTX Video Super Resolution quality level (1..4).</summary>
    public int SuperResolutionQuality { get; init; } = 4;
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
    public required long FramesProcessed { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required double AverageFps { get; init; }
    public required long OutputBytes { get; init; }
}

/// <summary>
/// ffmpeg 解码（rawvideo RGBA 管道）→ DLSS NR 逐帧处理 → ffmpeg 编码、音频和封装。
/// </summary>
public static class DlssVideoPipeline
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    public static async Task<TranscodeResult> RunAsync(
        string inputPath, string outputPath, TranscodeOptions options,
        IProgress<TranscodeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("输入视频不存在。", inputPath);

        var video = MediaProbe.Probe(inputPath, DemoPaths.FfprobePath);
        var scale = RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale);
        var plan = CreateProcessingPlan(video, scale, options.Video.PreserveHdr);
        if ((plan.SourceWidth & 1) != 0 || (plan.SourceHeight & 1) != 0)
            throw new NotSupportedException($"暂不支持奇数尺寸视频（当前 {plan.SourceWidth}x{plan.SourceHeight}）。");

        var totalFrames = options.MaxFrames is { } limit
            ? Math.Min(limit, video.FrameCount > 0 ? video.FrameCount : limit)
            : video.FrameCount;

        if (options.Video.TwoPassEnabled && IsTwoPassSupported(options.Video))
            return await RunTwoPassAsync(inputPath, outputPath, options, video, totalFrames, progress, cancellationToken);

        using var vsr = CreateVsr(options, plan, video);
        using var host = DlssNrHost.Create(
            options.HostDllPath, options.RuntimeDllPath, options.LogPath,
            plan.ProcessWidth, plan.ProcessHeight, options.Dlss, plan.Hdr, video.ColorTransfer);

        await using var decoder = new CommandDrain("ffmpeg-decode", options.FfmpegPath,
            DecoderArguments(inputPath, options.MaxFrames, video, plan.Hdr), redirectStdout: true);
        await using var encoder = new CommandDrain("ffmpeg-encode", options.FfmpegPath,
            EncoderArguments(inputPath, outputPath, options, video,
                plan.ProcessWidth, plan.ProcessHeight, plan.Hdr), redirectStdin: true);

        // 取消时杀掉两个子进程，解除管道上的阻塞读。
        using var cancellation = cancellationToken.Register(() =>
        {
            TryKill(decoder.Process);
            TryKill(encoder.Process);
        });

        // 帧循环天然串行（读一帧→处理→写一帧），同步阻塞 IO 最直接且无死锁。
        var outcome = await Task.Run(() => RunFrameLoop(
            decoder, encoder, host, vsr, plan, totalFrames, options.LogPath, progress,
            options.EnablePreviewFrames ? options.PreviewSink : null,
            options.PreviewFramesPerSecond, video.Fps, cancellationToken));

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

    private readonly record struct ProcessingPlan(
        int SourceWidth, int SourceHeight, int ProcessWidth, int ProcessHeight, bool Hdr)
    {
        public long SourceBytes => checked((long)SourceWidth * SourceHeight * (Hdr ? 8 : 4));
        public long ProcessBytes => checked((long)ProcessWidth * ProcessHeight * (Hdr ? 8 : 4));
    }

    private static ProcessingPlan CreateProcessingPlan(VideoInfo video, int scale, bool preserveHdr)
    {
        var hdr = video.IsHdr && preserveHdr;
        var processWidth = checked(video.Width * scale);
        var processHeight = checked(video.Height * scale);
        if (processWidth > 16384 || processHeight > 16384)
            throw new NotSupportedException($"RTX Video 超分后的尺寸超过 16384 限制（{processWidth}×{processHeight}）。");
        return new ProcessingPlan(video.Width, video.Height, processWidth, processHeight, hdr);
    }

    private static RtxVideoSuperResolutionHost? CreateVsr(
        TranscodeOptions options, ProcessingPlan plan, VideoInfo video)
    {
        if (plan.ProcessWidth == plan.SourceWidth && plan.ProcessHeight == plan.SourceHeight)
            return null;
        var host = string.IsNullOrWhiteSpace(options.VsrHostDllPath)
            ? DemoPaths.VsrHostDllPath : options.VsrHostDllPath;
        var runtime = string.IsNullOrWhiteSpace(options.VsrRuntimeDllPath)
            ? DemoPaths.VsrRuntimeDllPath : options.VsrRuntimeDllPath;
        var log = string.IsNullOrWhiteSpace(options.VsrLogPath) ? DemoPaths.VsrLogPath : options.VsrLogPath;
        return RtxVideoSuperResolutionHost.Create(
            host, runtime, log, plan.SourceWidth, plan.SourceHeight,
            plan.ProcessWidth / plan.SourceWidth, plan.Hdr, options.SuperResolutionQuality);
    }

    private static async Task<TranscodeResult> RunTwoPassAsync(
        string inputPath, string outputPath, TranscodeOptions options, VideoInfo video,
        long totalFrames, IProgress<TranscodeProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = CreateProcessingPlan(video,
            RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale),
            options.Video.PreserveHdr);
        var rawPath = Path.Combine(Path.GetTempPath(), $"dlss5-{Guid.NewGuid():N}.rgba");
        var passLog = Path.Combine(Path.GetTempPath(), $"dlss5-{Guid.NewGuid():N}-pass");
        var started = Stopwatch.StartNew();
        try
        {
            using var vsr = CreateVsr(options, plan, video);
            using var host = DlssNrHost.Create(
                options.HostDllPath, options.RuntimeDllPath, options.LogPath,
                plan.ProcessWidth, plan.ProcessHeight, options.Dlss, plan.Hdr, video.ColorTransfer);
            (long Frames, TimeSpan Elapsed) outcome;
            await using (var decoder = new CommandDrain("ffmpeg-decode", options.FfmpegPath,
                DecoderArguments(inputPath, options.MaxFrames, video, plan.Hdr), redirectStdout: true))
            await using (var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                using var cancellation = cancellationToken.Register(() => TryKill(decoder.Process));
                outcome = await Task.Run(() => RunFrameStream(
                    decoder, raw, host, vsr, plan, totalFrames, options.LogPath,
                    progress, options.EnablePreviewFrames ? options.PreviewSink : null,
                    options.PreviewFramesPerSecond, video.Fps, cancellationToken));
                decoder.Process.WaitForExit(30_000);
                if (decoder.Process.ExitCode != 0)
                    throw new InvalidOperationException("ffmpeg 解码失败：\n" + decoder.ErrorTail);
            }

            await RunPassAsync(options.FfmpegPath,
                EncoderArguments(inputPath, outputPath, options, video,
                    plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, rawPath, 1, passLog), cancellationToken);
            await RunPassAsync(options.FfmpegPath,
                EncoderArguments(inputPath, outputPath, options, video,
                    plan.ProcessWidth, plan.ProcessHeight, plan.Hdr, rawPath, 2, passLog), cancellationToken);

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
        }
    }

    private static async Task RunPassAsync(string ffmpegPath, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        await using var process = new CommandDrain("ffmpeg-pass", ffmpegPath, arguments);
        await process.WaitForExitAsync(cancellationToken);
        if (process.Process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg 两遍编码失败：\n" + process.ErrorTail);
    }

    private static (long Frames, TimeSpan Elapsed) RunFrameStream(
        CommandDrain decoder, Stream destination, DlssNrHost host, RtxVideoSuperResolutionHost? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps,
        CancellationToken cancellationToken)
        => RunFrameCore(decoder, destination, host, vsr, plan, totalFrames, logPath, progress,
            preview, previewFramesPerSecond, sourceFps, cancellationToken);

    private static (long Frames, TimeSpan Elapsed) RunFrameLoop(
        CommandDrain decoder, CommandDrain encoder, DlssNrHost host, RtxVideoSuperResolutionHost? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps,
        CancellationToken cancellationToken)
    {
        var result = RunFrameCore(decoder, encoder.Process.StandardInput.BaseStream, host, vsr, plan,
            totalFrames, logPath, progress, preview, previewFramesPerSecond, sourceFps, cancellationToken);
        encoder.Process.StandardInput.Close();
        encoder.Process.WaitForExit(120_000);
        decoder.Process.WaitForExit(30_000);
        if (encoder.Process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg 编码失败：\n" + encoder.ErrorTail);
        if (decoder.Process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg 解码失败：\n" + decoder.ErrorTail);
        if (totalFrames > 0 && result.Frames < totalFrames)
            throw new InvalidOperationException(
                $"解码提前结束：期望 {totalFrames} 帧，只读到 {result.Frames} 帧。\n{decoder.ErrorTail}");
        return result;
    }

    private static (long Frames, TimeSpan Elapsed) RunFrameCore(
        CommandDrain decoder, Stream destination, DlssNrHost host, RtxVideoSuperResolutionHost? vsr,
        ProcessingPlan plan, long totalFrames, string logPath, IProgress<TranscodeProgress>? progress,
        ITranscodePreviewSink? preview, int previewFramesPerSecond, double sourceFps,
        CancellationToken cancellationToken)
    {
        unsafe
        {
            var sourceBuffer = (byte*)NativeMemory.Alloc((nuint)plan.SourceBytes);
            var processBuffer = (byte*)NativeMemory.Alloc((nuint)plan.ProcessBytes);
            var outputBuffer = (byte*)NativeMemory.Alloc((nuint)plan.ProcessBytes);
            var encodedBuffer = plan.Hdr
                ? (byte*)NativeMemory.Alloc((nuint)(plan.ProcessWidth * plan.ProcessHeight * 8L))
                : outputBuffer;
            try
            {
                var decoderOutput = decoder.Process.StandardOutput.BaseStream;
                var stopwatch = Stopwatch.StartNew();
                var lastReport = stopwatch.Elapsed - ProgressInterval;
                var previewInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(previewFramesPerSecond, 1, 60));
                var lastPreview = -previewInterval;
                long lastPreviewFrame = 0;
                long frames = 0;
                while (totalFrames <= 0 || frames < totalFrames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Fill(decoderOutput, new Span<byte>(sourceBuffer, checked((int)plan.SourceBytes))))
                        break;
                    if (plan.Hdr)
                        NormalizeHdrFrame((ushort*)sourceBuffer, plan.SourceWidth * plan.SourceHeight * 4);

                    var nrInput = sourceBuffer;
                    if (vsr != null)
                    {
                        if (!vsr.ProcessFrame(sourceBuffer, processBuffer))
                            throw new InvalidOperationException($"第 {frames + 1} 帧 RTX Video 超分失败。");
                        nrInput = processBuffer;
                    }

                    var ok = plan.Hdr
                        ? host.ProcessFrame((ushort*)nrInput, (ushort*)outputBuffer, frames == 0)
                        : host.ProcessFrame(nrInput, outputBuffer, frames == 0);
                    if (!ok)
                        throw new InvalidOperationException($"第 {frames + 1} 帧 DLSS NR 处理失败，详见 {logPath}");

                    if (plan.Hdr)
                    {
                        ConvertHdrToRgba64((ushort*)outputBuffer, (ushort*)encodedBuffer,
                            plan.ProcessWidth * plan.ProcessHeight * 4);
                        destination.Write(new ReadOnlySpan<byte>(encodedBuffer, checked((int)plan.ProcessBytes)));
                    }
                    else
                        destination.Write(new ReadOnlySpan<byte>(outputBuffer, checked((int)plan.ProcessBytes)));
                    frames++;

                    if (preview != null && stopwatch.Elapsed - lastPreview >= previewInterval)
                    {
                        PublishPreview(preview, sourceBuffer, plan.SourceWidth, plan.SourceHeight,
                            outputBuffer, plan.ProcessWidth, plan.ProcessHeight, plan.Hdr,
                            frames, totalFrames, sourceFps);
                        lastPreview = stopwatch.Elapsed;
                        lastPreviewFrame = frames;
                    }
                    if (stopwatch.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = stopwatch.Elapsed;
                        progress?.Report(new TranscodeProgress(
                            frames, totalFrames, frames / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));
                    }
                }
                if (preview != null && frames > 0 && lastPreviewFrame != frames)
                    PublishPreview(preview, sourceBuffer, plan.SourceWidth, plan.SourceHeight,
                        outputBuffer, plan.ProcessWidth, plan.ProcessHeight, plan.Hdr,
                        frames, totalFrames, sourceFps);
                destination.Flush();
                stopwatch.Stop();
                progress?.Report(new TranscodeProgress(
                    frames, totalFrames, frames / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001), stopwatch.Elapsed));
                return (frames, stopwatch.Elapsed);
            }
            finally
            {
                NativeMemory.Free(sourceBuffer);
                NativeMemory.Free(processBuffer);
                NativeMemory.Free(outputBuffer);
                if (encodedBuffer != outputBuffer)
                    NativeMemory.Free(encodedBuffer);
            }
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
        var sourcePreview = HdrPreview((ushort*)source, sourceWidth, sourceHeight);
        var processedPreview = HdrPreview((ushort*)processed, processedWidth, processedHeight);
        preview.Publish(sourcePreview, sourceWidth, sourceHeight, processedPreview,
            processedWidth, processedHeight, frames, totalFrames, sourceFps);
    }

    private static unsafe byte[] HdrPreview(ushort* source, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (var index = 0; index < width * height; index++)
        {
            var r = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[index * 4]), 0, 1);
            var g = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[index * 4 + 1]), 0, 1);
            var b = Math.Clamp((float)BitConverter.Int16BitsToHalf((short)source[index * 4 + 2]), 0, 1);
            result[index * 4] = ToSrgbByte(r);
            result[index * 4 + 1] = ToSrgbByte(g);
            result[index * 4 + 2] = ToSrgbByte(b);
            result[index * 4 + 3] = 255;
        }
        return result;
    }

    private static byte ToSrgbByte(float value)
    {
        var srgb = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(srgb * 255f), 0, 255);
    }
    private static IReadOnlyList<string> DecoderArguments(
        string input, int? maxFrames, VideoInfo video, bool hdr)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", input, "-map", "0:v:0" };
        if (hdr)
        {
            var transfer = string.IsNullOrWhiteSpace(video.ColorTransfer) ? "smpte2084" : video.ColorTransfer;
            var primaries = string.IsNullOrWhiteSpace(video.ColorPrimaries) ? "bt2020" : video.ColorPrimaries;
            var matrix = string.IsNullOrWhiteSpace(video.ColorSpace) ? "bt2020nc" : video.ColorSpace;
            var range = video.ColorRange is "pc" or "jpeg" or "full" ? "full" : "limited";
            arguments.AddRange(["-vf",
                $"zscale=matrixin={matrix}:matrix={matrix}:transferin={transfer}:transfer={transfer}:primariesin={primaries}:primaries={primaries}:rangein={range}:range=full,format=gbrp16le,format=rgba64le",
                "-f", "rawvideo", "-pix_fmt", "rgba64le"]);
        }
        else
            arguments.AddRange(["-f", "rawvideo", "-pix_fmt", "rgba"]);
        if (maxFrames is { } limit)
        {
            arguments.Add("-frames:v");
            arguments.Add(limit.ToString());
        }
        arguments.Add("pipe:1");
        return arguments;
    }

    private static IReadOnlyList<string> EncoderArguments(
        string input, string output, TranscodeOptions options, VideoInfo video,
        int rawWidth, int rawHeight, bool hdr,
        string? rawInputPath = null, int pass = 0, string? passLog = null)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (rawInputPath is null)
            arguments.AddRange(["-f", "rawvideo", "-framerate", video.FrameRate,
                "-pix_fmt", hdr ? "rgba64le" : "rgba", "-s", $"{rawWidth}x{rawHeight}", "-i", "pipe:0"]);
        else
            arguments.AddRange(["-f", "rawvideo", "-framerate", video.FrameRate,
                "-pix_fmt", hdr ? "rgba64le" : "rgba", "-s", $"{rawWidth}x{rawHeight}", "-i", rawInputPath]);

        var profile = options.Video;
        var includeAudio = options.CopyAudio && video.HasAudio && profile.AudioCodec != AudioCodec.None
            && profile.Container != VideoContainer.Gif && pass != 1;
        var copyAudio = includeAudio && profile.AudioCodec == AudioCodec.Copy;
        if (includeAudio)
        {
            arguments.Add("-i");
            arguments.Add(input);
        }
        arguments.Add("-map");
        arguments.Add("0:v:0");
        if (includeAudio)
        {
            arguments.Add("-map");
            arguments.Add("1:a:0?");
        }

        var filters = BuildFilterChain(profile, video, options.VideoEncoder);
        if (!string.IsNullOrWhiteSpace(filters))
        {
            arguments.Add("-vf");
            arguments.Add(filters);
        }
        arguments.AddRange(VideoEncoderArguments(options, video, hdr));
        if (pass is 1 or 2)
            arguments.AddRange(["-pass", pass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-passlogfile", passLog ?? Path.Combine(Path.GetTempPath(), "dlss5-pass")]);
        if (copyAudio)
        {
            arguments.Add("-c:a");
            arguments.Add("copy");
        }
        else if (includeAudio)
        {
            arguments.Add("-c:a");
            arguments.Add(AudioEncoder(profile.AudioCodec));
            if (profile.AudioCodec != AudioCodec.Flac)
            {
                arguments.Add("-b:a");
                arguments.Add($"{Math.Clamp(profile.AudioBitrateKbps, 32, 512)}k");
            }
            if (Math.Abs(profile.AudioGainDb) > 0.01)
            {
                arguments.Add("-af");
                arguments.Add($"volume={profile.AudioGainDb.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}dB");
            }
        }
        if (profile.PreserveHdr && (video.IsHdr || profile.ForceHdr))
        {
            arguments.Add("-color_primaries"); arguments.Add(string.IsNullOrWhiteSpace(video.ColorPrimaries) ? "bt2020" : video.ColorPrimaries);
            arguments.Add("-color_trc"); arguments.Add(string.IsNullOrWhiteSpace(video.ColorTransfer) ? "smpte2084" : video.ColorTransfer);
            arguments.Add("-colorspace"); arguments.Add(string.IsNullOrWhiteSpace(video.ColorSpace) ? "bt2020nc" : video.ColorSpace);
            if (!string.IsNullOrWhiteSpace(video.ColorRange)) { arguments.Add("-color_range"); arguments.Add(video.ColorRange); }
            arguments.Add("-metadata:s:v:0"); arguments.Add("master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)");
            arguments.Add("-metadata:s:v:0"); arguments.Add("max-cll=1000,400");
        }
        if (profile.Container == VideoContainer.Gif)
            arguments.AddRange(["-loop", Math.Max(0, profile.GifLoop).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.MaxFrames is { } maxFrames)
            arguments.AddRange(["-frames:v", Math.Max(1, maxFrames).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (includeAudio)
            arguments.Add("-shortest");
        if (pass == 1)
            arguments.AddRange(["-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"]);
        else
            arguments.Add(output);
        return arguments;
    }

    private static IReadOnlyList<string> VideoEncoderArguments(TranscodeOptions options, VideoInfo video, bool hdr)
    {
        var profile = options.Video;
        var encoder = string.IsNullOrWhiteSpace(options.VideoEncoder) ? VideoCodecNames.Encoder(profile) : options.VideoEncoder;
        var quality = Math.Clamp(profile.Quality > 0 ? profile.Quality : options.Quality, 0, 51);
        var list = new List<string> { "-c:v", encoder };
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            list.AddRange(["-preset", string.IsNullOrWhiteSpace(profile.EncoderPreset) ? "p5" : profile.EncoderPreset,
                "-tune", "hq", "-rc", profile.RateControl == RateControlMode.Bitrate ? "vbr" : "constqp"]);
            if (profile.RateControl == RateControlMode.Bitrate)
            {
                list.AddRange(["-b:v", $"{Math.Max(100, profile.VideoBitrateKbps)}k"]);
            }
            else list.AddRange(["-cq", quality.ToString(), "-b:v", "0"]);
        }
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal) || encoder.EndsWith("_amf", StringComparison.Ordinal))
        {
            list.AddRange(["-global_quality", quality.ToString()]);
        }
        else if (encoder is "gif")
        {
            // GIF is configured entirely through the filter chain.
        }
        else
        {
            list.AddRange(["-preset", SoftwarePreset(profile.EncoderPreset),
                profile.RateControl == RateControlMode.Bitrate ? "-b:v" : "-crf",
                profile.RateControl == RateControlMode.Bitrate ? $"{Math.Max(100, profile.VideoBitrateKbps)}k" : quality.ToString()]);
        }
        if (encoder == "gif")
            list.AddRange(["-pix_fmt", "pal8"]);
        else
        {
            list.AddRange(["-pix_fmt", hdr && profile.VideoCodec != VideoCodec.H264 ? "p010le" : "yuv420p"]);
        }
        return list;
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

    private static string BuildFilterChain(VideoTranscodeOptions o, VideoInfo source, string encoder)
    {
        var filters = new List<string>();
        if (o.CropEnabled)
            filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None) filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
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
        if (o.FpsMode != FpsMode.SameAsSource && o.FpsValue > 0) filters.Add($"fps={o.FpsValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (o.Container == VideoContainer.Gif)
        {
            filters.Add($"fps={Math.Clamp(o.GifFps, 1, 30)}");
            filters.Add($"scale={Math.Clamp(o.GifWidth, 64, 800)}:-2:flags=lanczos");
            var dither = string.IsNullOrWhiteSpace(o.GifDither) ? "bayer" : o.GifDither;
            var stats = o.GifStatsMode is "single" ? "single" : "diff";
            filters.Add($"split[s0][s1];[s0]palettegen=max_colors={Math.Clamp(o.GifMaxColors, 32, 256)}:stats_mode={stats}[p];[s1][p]paletteuse=dither={dither}");
        }
        if (o.Container != VideoContainer.Gif && o.PreserveHdr && (source.IsHdr || o.ForceHdr) && !encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            filters.Add("format=p010le");
        return string.Join(',', filters);
    }

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer[filled..]);
            if (read <= 0)
            {
                if (filled > 0) throw new EndOfStreamException("解码器返回了不完整的视频帧。");
                return false;
            }
            filled += read;
        }
        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* 取消路径上的清理失败不再传播 */ }
    }

    /// <summary>包装 ffmpeg 子进程：异步排空 stderr（保留尾部），退出时确保回收。</summary>
    private sealed class CommandDrain : IAsyncDisposable
    {
        private readonly StringBuilder _stderr = new();
        private readonly Task _drainTask;

        public CommandDrain(string name, string executable, IReadOnlyList<string> arguments,
            bool redirectStdout = false, bool redirectStdin = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = redirectStdout,
                RedirectStandardInput = redirectStdin,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (redirectStdout)
                startInfo.StandardOutputEncoding = Encoding.UTF8;
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            Process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {name}（{executable}）。");
            _drainTask = Task.Run(async () =>
            {
                var buffer = new char[4096];
                var reader = Process.StandardError;
                while (await reader.ReadAsync(buffer) > 0)
                {
                    const int cap = 16 * 1024;
                    if (_stderr.Length > cap)
                        _stderr.Remove(0, _stderr.Length - cap);
                    _stderr.Append(buffer);
                }
            });
        }

        public Process Process { get; }
        public string ErrorTail => _stderr.ToString().TrimEnd();

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Process.WaitForExitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync(CancellationToken.None).WaitAsync(
                        TimeSpan.FromSeconds(10));
                }
            }
            catch { /* 清理阶段忽略 */ }
            // 某些 FFmpeg 构建在进程退出后仍会保持 stderr 管道句柄；不能让 UI 永久卡在清理阶段。
            try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            Process.Dispose();
        }
    }
}

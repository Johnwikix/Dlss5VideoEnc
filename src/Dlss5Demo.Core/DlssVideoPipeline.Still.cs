using System.Diagnostics;

namespace Dlss5Demo.Core;

public static partial class DlssVideoPipeline
{
    private const int MaxImageDimension = 16384;

    /// <summary>
    /// 静态图路径：解码 RGBA8 → 单帧 NR（reset=1、分块评估）→ 编码 PNG/JPEG。
    /// 单帧即模型稳态（静止重复帧不改变结果），无需任何时域管线。
    /// </summary>
    private static async Task<TranscodeResult> RunStillImageAsync(
        string inputPath, string outputPath, TranscodeOptions options,
        IProgress<TranscodeProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.EnableNr)
            throw new NotSupportedException("图片模式当前仅支持 DLSS NR 处理。");
        if (!StillImage.IsWritableOutput(outputPath))
            throw new NotSupportedException($"图片输出只支持 PNG/JPEG：{outputPath}");
        if (options.EnableInterpolation || options.SuperResolutionScale > 1)
            PipelineLog.Info("图片输入：插帧/超分不适用于静态图，本次已忽略。");

        var watch = Stopwatch.StartNew();
        // 解码/NR/编码全为同步原生调用，放线程池避免占用调用方线程（GUI）。
        return await Task.Run(
            () => ProcessStillImage(inputPath, outputPath, options, progress, watch, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static TranscodeResult ProcessStillImage(
        string inputPath, string outputPath, TranscodeOptions options,
        IProgress<TranscodeProgress>? progress, Stopwatch watch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = StillImageCodec.Decode(inputPath);
        var width = source.Width;
        var height = source.Height;
        if (width > MaxImageDimension || height > MaxImageDimension)
            throw new NotSupportedException($"图片尺寸 {width}×{height} 超出可处理范围（边长 1..{MaxImageDimension}）。");
        PipelineLog.Info($"图片管线开始: {inputPath} → {outputPath} · {width}×{height}");

        var finalPath = outputPath;
        if (Path.GetExtension(finalPath).ToLowerInvariant() is ".jpg" or ".jpeg"
            && (width % 2 != 0 || height % 2 != 0))
        {
            // yuvj444p 的奇数 JPEG 兼容性参差；奇数尺寸统一回退无损 PNG。
            finalPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(finalPath))!,
                Path.GetFileNameWithoutExtension(finalPath) + ".png");
            PipelineLog.Info($"奇数尺寸不输出 JPEG，回退 PNG：{finalPath}");
        }

        var pixels = (long)width * height;
        var rgba = source.Rgba;
        // NR 契约为不透明 RGBA8（模型对 A 通道行为未定义）：抽出 alpha、按不透明送入、处理后写回。
        var alpha = ExtractAlpha(rgba, pixels);
        var processed = new byte[checked(pixels * 4)];

        using (var reservation = OutputFileNames.Reserve(finalPath))
        {
            var hostOptions = options.Dlss with
            {
                ContinuousHistory = false,
                TemporalStabilization = false,
                MotionGuidance = false,
                UseGpuTemporalProcessing = false,
                FineMotion = false,
            };
            using var host = DlssNrHost.Create(options.HostDllPath, options.RuntimeDllPath, options.LogPath,
                width, height, hostOptions, hdr: false, tiling: true);
            unsafe
            {
                fixed (byte* input = rgba)
                fixed (byte* output = processed)
                {
                    if (!host.ProcessFrame(input, output, true))
                        throw new InvalidOperationException("DLSS NR 处理图片失败。");
                }
            }

            if (alpha != null) RestoreAlpha(processed, alpha, pixels);
            if (options.EnablePreviewFrames)
            {
                options.PreviewSink?.Publish(rgba, width, height, processed, width, height, 1, 1, 0);
            }

            var keepAlpha = alpha != null && Path.GetExtension(finalPath).ToLowerInvariant() == ".png";
            StillImageCodec.Encode(finalPath, processed, width, height, keepAlpha, options.Quality);
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed = watch.Elapsed;
            progress?.Report(new TranscodeProgress(1, 1, 0, elapsed));
            return new TranscodeResult
            {
                OutputPath = finalPath,
                FramesProcessed = 1,
                Elapsed = elapsed,
                AverageFps = elapsed.TotalSeconds > 0 ? 1 / elapsed.TotalSeconds : 0,
                OutputBytes = new FileInfo(finalPath).Length,
            };
        }
    }

    /// <summary>检测任意非不透明像素即返回完整 alpha 平面，否则 null（全不透明无需处理）。</summary>
    private static byte[]? ExtractAlpha(byte[] rgba, long pixels)
    {
        var hasAlpha = false;
        for (var i = 3; i < pixels * 4; i += 4)
        {
            if (rgba[i] == 255) continue;
            hasAlpha = true;
            break;
        }
        if (!hasAlpha) return null;

        var alpha = new byte[pixels];
        for (long p = 0, i = 3; p < pixels; p++, i += 4)
        {
            alpha[p] = rgba[i];
            rgba[i] = 255;
        }
        return alpha;
    }

    private static void RestoreAlpha(byte[] processed, byte[] alpha, long pixels)
    {
        for (long p = 0, i = 3; p < pixels; p++, i += 4)
            processed[i] = alpha[p];
    }
}

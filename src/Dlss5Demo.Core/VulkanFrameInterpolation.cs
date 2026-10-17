using System.Diagnostics;

namespace Dlss5Demo.Core;

/// <summary>
/// little-fancy-tool-ava 的 NVIDIA 补帧后端：VK_NV_optical_flow + Vulkan 合成。
/// 作为编码前的独立滤镜运行，NR/SR 只处理源帧，不重复执行在生成帧上。
/// </summary>
internal static class VulkanFrameInterpolation
{
    internal static async Task<bool> IsSingleFrameAsync(string input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(DemoPaths.FfprobePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Some containers omit nb_frames, including one-frame Matroska files.
        // Inspect at most two packets rather than decoding the entire clip.
        foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#2",
            "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", input })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var stop = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("检查补帧输入失败：" + error.Trim());
        return int.TryParse(output.Trim(), out var frames) && frames == 1;
    }

    internal static string OutputRate(VideoInfo video, int multiplier)
    {
        var parts = video.FrameRate.Split('/');
        if (parts.Length == 2 && long.TryParse(parts[0], out var numerator) && long.TryParse(parts[1], out var denominator)
            && numerator > 0 && denominator > 0)
            return $"{checked(numerator * multiplier)}/{denominator}";
        return (video.Fps * multiplier).ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static string Filter(int multiplier, bool hdr, bool singleFrame)
    {
        if (multiplier is not (2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(multiplier), "补帧倍率只能是 2、4 或 6。");
        // HDR uses the full 16-bit color planes for synthesis. Only the optical
        // flow estimator's luma is 8-bit; color pictures never pass through BGRA8.
        var format = hdr ? "rgba64le" : "rgba";
        var pad = singleFrame ? "tpad=stop_mode=clone:stop=1," : "";
        // Rawvideo already has an exact rational source clock. Subdivide it so
        // fractional rates cannot accumulate the microsecond rounding of AVTB.
        return $"{pad}format={format},settb=expr=intb/{multiplier},hwupload," +
            $"fruc_vulkan=fps=source_fps*{multiplier}:perf=medium,hwdownload,format={format},setpts=PTS:strip_fps=1";
    }

    internal static async Task<bool> ValidateAsync(TranscodeOptions options, VideoInfo video, bool hdr,
        CancellationToken cancellationToken, bool linear = true)
    {
        if (options.InterpolationMultiplier is not (2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(options.InterpolationMultiplier), "补帧倍率只能是 2、4 或 6。");
        if (!double.IsFinite(video.Fps) || video.Fps <= 0)
            throw new NotSupportedException("NVIDIA 补帧需要有效的源视频帧率。");
        if (hdr && (options.Video.VideoCodec == VideoCodec.H264 || options.VideoEncoder.Contains("264", StringComparison.Ordinal)
            || options.Video.Container == VideoContainer.Gif))
            throw new NotSupportedException("保留 HDR 的 NVIDIA 补帧请使用 HEVC 或 AV1 10-bit 输出。");

        try
        {
            await Task.Run(() => Probe(options.InterpolationMultiplier, hdr, linear, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (linear && error is not OperationCanceledException)
        {
            PipelineLog.Info("NVIDIA Vulkan 线性图像探测失败，重试最优平铺布局：" + error.Message);
            return await ValidateAsync(options, video, hdr, cancellationToken, false).ConfigureAwait(false);
        }
        PipelineLog.Info($"NVIDIA Vulkan 补帧已验证：{options.InterpolationMultiplier}×，{(hdr ? "RGBA16 / HDR" : "RGBA8 / SDR")}；{(linear ? "线性图像" : "最优平铺")}；进程内 DLL；NR/SR → 补帧 → 编码。预览与处理计数采样源帧。");
        return linear;
    }
    private static void Probe(int multiplier, bool hdr, bool linear, CancellationToken ct)
    {
        var video = new VideoInfo { Width = 320, Height = 192, FrameRate = "24/1", Codec = "rawvideo", PixelFormat = hdr ? "rgba64le" : "rgba" };
        using var session = new VulkanInterpolationSession(320, 192, hdr, video, multiplier, linear);
        byte[] pixels = new byte[session.FrameBytes];
        for (int i = 0; i < 4; i++)
        {
            pixels.AsSpan().Fill((byte)(i * 32));
            session.Write(pixels, Stream.Null, ct);
        }
        session.Complete(Stream.Null, ct);
        if (session.Outputs != 4 * multiplier) throw new NotSupportedException("Vulkan 未生成实际补帧。");
    }

}

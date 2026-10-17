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
    internal static void AddDeviceArguments(List<string> arguments)
        => arguments.AddRange(["-init_hw_device", "vulkan=fruc:,vendor_id=0x10de", "-filter_hw_device", "fruc"]);

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

    internal static async Task ValidateAsync(TranscodeOptions options, VideoInfo video, bool hdr,
        CancellationToken cancellationToken)
    {
        if (options.InterpolationMultiplier is not (2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(options.InterpolationMultiplier), "补帧倍率只能是 2、4 或 6。");
        if (!double.IsFinite(video.Fps) || video.Fps <= 0)
            throw new NotSupportedException("NVIDIA 补帧需要有效的源视频帧率。");
        if (hdr && (options.Video.VideoCodec == VideoCodec.H264 || options.VideoEncoder.Contains("264", StringComparison.Ordinal)
            || options.Video.Container == VideoContainer.Gif))
            throw new NotSupportedException("保留 HDR 的 NVIDIA 补帧请使用 HEVC 或 AV1 10-bit 输出。");

        // Execute a real graph on a matching NVIDIA adapter. A filter listing or
        // vendor name alone cannot establish that the installed driver works.
        var start = new ProcessStartInfo(options.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        AddDeviceArguments(arguments);
        arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=320x192:rate=24:duration=0.125",
            "-vf", Filter(options.InterpolationMultiplier, hdr, false), "-an", "-fps_mode", "passthrough",
            "-progress", "pipe:1", "-nostats", "-f", "null", "-"]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 NVIDIA 补帧探针。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var stop = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException("NVIDIA Vulkan 补帧初始化超时。");
        var frames = output.Split('\n').Where(line => line.StartsWith("frame="))
            .Select(line => int.TryParse(line.AsSpan(6).Trim(), out var count) ? count : 0).LastOrDefault();
        if (process.ExitCode != 0 || frames < 3 * options.InterpolationMultiplier)
            throw new NotSupportedException("NVIDIA Vulkan 补帧不可用：需要包含 fruc_vulkan 的 FFmpeg 和支持 VK_NV_optical_flow 的驱动。\n" + error.Trim());
        PipelineLog.Info($"NVIDIA Vulkan 补帧已验证：{options.InterpolationMultiplier}×，{(hdr ? "RGBA16 / HDR" : "RGBA8 / SDR")}；NR/SR → 补帧 → 编码。预览与处理计数采样源帧。");
    }
}

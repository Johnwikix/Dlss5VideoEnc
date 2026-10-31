using System.Diagnostics;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

/// <summary>
/// little-fancy-tool-ava 的 NVIDIA 补帧后端：VK_NV_optical_flow + Vulkan 合成。
/// 作为编码前的独立滤镜运行，NR/SR 只处理源帧，不重复执行在生成帧上。
/// </summary>
internal static class VulkanFrameInterpolation
{
    internal static Task<bool> IsSingleFrameAsync(string input, CancellationToken cancellationToken)
        => Task.Run(() => IsSingleFrame(input, cancellationToken), cancellationToken);

    /// <summary>有些容器不带 nb_frames（包括单帧 Matroska）：只读前两个视频包判断。</summary>
    private static unsafe bool IsSingleFrame(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeVideo.Initialize();
        AVFormatContext* format = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            if (packet == null) throw new OutOfMemoryException();
            NativeVideo.Check(ffmpeg.avformat_open_input(&format, input, null, null), "检查补帧输入");
            var index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (index < 0) return false;
            var frames = 0;
            int result;
            while (frames < 2 && (result = ffmpeg.av_read_frame(format, packet)) >= 0)
            {
                if (packet->stream_index == index) frames++;
                ffmpeg.av_packet_unref(packet);
            }
            return frames == 1;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
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

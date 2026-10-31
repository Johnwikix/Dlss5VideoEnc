using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

public sealed record VideoInfo
{
    public required string Codec { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string PixelFormat { get; init; }
    /// <summary>有理数帧率，例如 "60/1"。</summary>
    public required string FrameRate { get; init; }
    public double Fps => ParseFraction(FrameRate);
    public long FrameCount { get; init; }
    public double DurationSeconds { get; init; }
    public bool HasAudio { get; init; }
    public string AudioCodec { get; init; } = "";
    public string ColorPrimaries { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string ColorSpace { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public bool IsHdr => ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
        || ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase)
        || (ColorPrimaries.Equals("bt2020", StringComparison.OrdinalIgnoreCase)
            && PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase));

    public static double ParseFraction(string text)
    {
        var parts = (text ?? "").Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
            return numerator / denominator;
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}

/// <summary>
/// 进程内探测（avformat_open_input + avformat_find_stream_info），不再依赖 ffprobe.exe。
/// </summary>
public static unsafe class MediaProbe
{
    private const int ProbeTimeoutSec = 15;

    // Root the native callback; its opaque deadline lives until avformat_close_input returns.
    private static readonly AVIOInterruptCB_callback ProbeInterrupt = static opaque =>
        System.Diagnostics.Stopwatch.GetTimestamp() >= *(long*)opaque ? 1 : 0;

    public static VideoInfo Probe(string mediaPath)
    {
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("输入视频不存在。", mediaPath);
        NativeVideo.Initialize();
        AVFormatContext* format = null;
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + ProbeTimeoutSec * System.Diagnostics.Stopwatch.Frequency;
        try
        {
            format = ffmpeg.avformat_alloc_context();
            if (format == null) throw new OutOfMemoryException();
            format->interrupt_callback = new AVIOInterruptCB { callback = ProbeInterrupt, opaque = &deadline };
            var opened = ffmpeg.avformat_open_input(&format, mediaPath, null, null);
            if (opened < 0 || format == null)
                NativeVideo.Check(opened, $"打开输入（{Path.GetFileName(mediaPath)}）");
            NativeVideo.Check(ffmpeg.avformat_find_stream_info(format, null), "读取流信息");

            AVStream* videoStream = null;
            var audioCodec = "";
            var hasAudio = false;
            for (uint i = 0; i < format->nb_streams; i++)
            {
                var stream = format->streams[i];
                var type = stream->codecpar->codec_type;
                if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && videoStream == null) videoStream = stream;
                else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO && !hasAudio)
                {
                    hasAudio = true;
                    audioCodec = ffmpeg.avcodec_get_name(stream->codecpar->codec_id) ?? "";
                }
            }
            if (videoStream == null) throw new InvalidOperationException("输入文件中没有视频流。");

            var par = videoStream->codecpar;
            var rate = videoStream->avg_frame_rate;
            if (rate.num <= 0 || rate.den <= 0) rate = videoStream->r_frame_rate;
            if (rate.num <= 0 || rate.den <= 0) rate = new() { num = 0, den = 1 };
            var duration = videoStream->duration > 0
                ? videoStream->duration * ffmpeg.av_q2d(videoStream->time_base)
                : format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;

            return new VideoInfo
            {
                Codec = ffmpeg.avcodec_get_name(par->codec_id) ?? "",
                Width = par->width,
                Height = par->height,
                PixelFormat = par->format >= 0
                    ? ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format) ?? "" : "",
                FrameRate = $"{rate.num}/{rate.den}",
                FrameCount = videoStream->nb_frames > 0 ? videoStream->nb_frames : 0,
                DurationSeconds = duration,
                HasAudio = hasAudio,
                AudioCodec = audioCodec,
                ColorPrimaries = ffmpeg.av_color_primaries_name(par->color_primaries) ?? "",
                ColorTransfer = ffmpeg.av_color_transfer_name(par->color_trc) ?? "",
                ColorSpace = ffmpeg.av_color_space_name(par->color_space) ?? "",
                ColorRange = ffmpeg.av_color_range_name(par->color_range) ?? "",
            };
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static string? PtrToString(byte* pointer)
        => pointer == null ? null : Marshal.PtrToStringUTF8((nint)pointer);
}

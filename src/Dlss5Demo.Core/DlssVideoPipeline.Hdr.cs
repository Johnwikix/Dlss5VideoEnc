namespace Dlss5Demo.Core;

public static partial class DlssVideoPipeline
{
    internal static TranscodeOptions NormalizeHdrOptions(TranscodeOptions options)
    {
        if (options.EnableInterpolation && options.InterpolationBackend == FrameInterpolationBackend.Rife
            && options.Video.ForceHdr)
        {
            PipelineLog.Info("RIFE 仅支持 SDR 输出：跳过强制 HDR 映射，使用 SDR 编码。");
            return options with { Video = options.Video with { ForceHdr = false } };
        }
        return options;
    }

    internal static bool NeedsDriverHdr(TranscodeOptions options, VideoInfo source)
        => !source.IsHdr && options.Video.PreserveHdr && options.Video.ForceHdr
           && !(options.EnableInterpolation && options.InterpolationBackend == FrameInterpolationBackend.Rife);

    private static void ValidateDriverHdrOutput(TranscodeOptions options, VideoInfo source)
    {
        if (!NeedsDriverHdr(options, source)) return;
        var encoder = string.IsNullOrWhiteSpace(options.VideoEncoder) ? VideoCodecNames.Encoder(options.Video) : options.VideoEncoder;
        if (options.Video.Container == VideoContainer.Gif || options.Video.VideoCodec == VideoCodec.H264
            || encoder is not ("hevc_nvenc" or "av1_nvenc" or "libx265" or "libsvtav1"
                or "hevc_qsv" or "av1_qsv" or "hevc_amf" or "av1_amf" or "libvpx-vp9"))
            throw new NotSupportedException("驱动 RTX Video HDR 需要 HEVC、AV1 或 VP9 10-bit 编码，不能输出 H.264/GIF。");
    }

    private static VideoInfo DriverHdrOutputVideo(VideoInfo source) => source with
    {
        PixelFormat = "x2bgr10le", ColorPrimaries = "bt2020", ColorTransfer = "smpte2084",
        ColorSpace = "bt2020nc", ColorRange = "tv",
    };
}

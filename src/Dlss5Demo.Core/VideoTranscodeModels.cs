namespace Dlss5Demo.Core;

public enum VideoContainer { Mp4, Mkv, Mov, Avi, Gif }
public enum VideoCodec { H264, H265, Av1, Vp9, Mpeg4, Gif }
public enum HardwareBackend { Software, Nvidia, Intel, Amd }
public enum AudioCodec { Copy, Aac, Mp3, Opus, Vorbis, Flac, Ac3, None }
public enum RateControlMode { Quality, Bitrate }
public enum ScaleMode { None, FitWithin, Exact, Width, Height }
public enum FpsMode { SameAsSource, Fixed, Peak }
public enum DeinterlaceMode { None, Yadif, Bwdif }
public enum DenoiseMode { None, Light, Medium, Strong }

/// <summary>完整的视频封装、编码、滤镜和 HDR 输出设置。</summary>
public sealed record VideoTranscodeOptions
{
    public VideoContainer Container { get; init; } = VideoContainer.Mp4;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.H265;
    public HardwareBackend HardwareBackend { get; init; } = HardwareBackend.Nvidia;
    public AudioCodec AudioCodec { get; init; } = AudioCodec.Copy;
    public RateControlMode RateControl { get; init; } = RateControlMode.Quality;
    public int Quality { get; init; } = 19;
    public int VideoBitrateKbps { get; init; } = 8000;
    public int AudioBitrateKbps { get; init; } = 192;
    public string EncoderPreset { get; init; } = "p5";
    public bool TwoPassEnabled { get; init; }
    public double AudioGainDb { get; init; }
    public ScaleMode ScaleMode { get; init; }
    public int ScaleWidth { get; init; } = 1920;
    public int ScaleHeight { get; init; } = 1080;
    public bool KeepAspect { get; init; } = true;
    public bool CropEnabled { get; init; }
    public int CropTop { get; init; }
    public int CropBottom { get; init; }
    public int CropLeft { get; init; }
    public int CropRight { get; init; }
    public FpsMode FpsMode { get; init; } = FpsMode.SameAsSource;
    public double FpsValue { get; init; } = 30;
    public DeinterlaceMode Deinterlace { get; init; }
    public DenoiseMode Denoise { get; init; }
    public int GifWidth { get; init; } = 480;
    public int GifFps { get; init; } = 15;
    public int GifMaxColors { get; init; } = 256;
    public string GifDither { get; init; } = "bayer";
    public int GifLoop { get; init; }
    public string GifStatsMode { get; init; } = "diff";
    public bool PreserveHdr { get; init; } = true;
    public bool ForceHdr { get; init; }
}

/// <summary>ffprobe 结果，包含 HDR 传递所需的色彩元数据。</summary>
public sealed record VideoProbeInfo(
    string FilePath,
    long DurationMs,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string AudioCodec,
    string Container,
    long BitRate,
    bool HasAudio,
    bool HasVideo,
    string PixelFormat = "",
    string ColorPrimaries = "",
    string ColorTransfer = "",
    string ColorSpace = "",
    string ColorRange = "")
{
    public bool IsHdr => ForceHdrMetadata(ColorTransfer)
        || (ColorPrimaries.Equals("bt2020", StringComparison.OrdinalIgnoreCase)
            && PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase));
    public string Display => HasVideo
        ? $"{Width}×{Height} · {Fps:0.##} fps · {VideoCodec} · {PixelFormat} · {DurationMs / 1000.0:0.0}s"
        : "没有视频流";

    private static bool ForceHdrMetadata(string transfer)
        => transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
           || transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
}

public static class VideoCodecNames
{
    public static string Encoder(VideoTranscodeOptions options)
    {
        if (options.Container == VideoContainer.Gif || options.VideoCodec == VideoCodec.Gif)
            return "gif";
        return options.HardwareBackend switch
    {
        HardwareBackend.Nvidia when options.VideoCodec == VideoCodec.H264 => "h264_nvenc",
        HardwareBackend.Nvidia when options.VideoCodec == VideoCodec.H265 => "hevc_nvenc",
        HardwareBackend.Nvidia when options.VideoCodec == VideoCodec.Av1 => "av1_nvenc",
        HardwareBackend.Intel when options.VideoCodec == VideoCodec.H264 => "h264_qsv",
        HardwareBackend.Intel when options.VideoCodec == VideoCodec.H265 => "hevc_qsv",
        HardwareBackend.Intel when options.VideoCodec == VideoCodec.Av1 => "av1_qsv",
        HardwareBackend.Amd when options.VideoCodec == VideoCodec.H264 => "h264_amf",
        HardwareBackend.Amd when options.VideoCodec == VideoCodec.H265 => "hevc_amf",
        HardwareBackend.Amd when options.VideoCodec == VideoCodec.Av1 => "av1_amf",
        _ when options.VideoCodec == VideoCodec.H264 => "libx264",
        _ when options.VideoCodec == VideoCodec.H265 => "libx265",
        _ when options.VideoCodec == VideoCodec.Av1 => "libsvtav1",
        _ when options.VideoCodec == VideoCodec.Vp9 => "libvpx-vp9",
        _ => "mpeg4",
    };
    }
}

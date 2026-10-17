namespace Dlss5Demo.Core;

public enum VideoDecodeMode
{
    Auto,
    Nvdec,
    Software,
}

public static class VideoDecodeModes
{
    public static VideoDecodeMode Parse(string value) => value.ToLowerInvariant() switch
    {
        "auto" => VideoDecodeMode.Auto,
        "nvdec" => VideoDecodeMode.Nvdec,
        "cpu" or "software" => VideoDecodeMode.Software,
        _ => throw new ArgumentException("解码方式只能是 auto、nvdec 或 cpu。", nameof(value)),
    };
}

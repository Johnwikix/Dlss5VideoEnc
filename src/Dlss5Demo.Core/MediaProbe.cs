using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
            return numerator / denominator;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}

public static class MediaProbe
{
    public static VideoInfo Probe(string mediaPath, string ffprobePath = "ffprobe")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(mediaPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffprobe，请确认已安装并在 PATH 中。");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 失败：{stderr.Trim()}");

        using var document = JsonDocument.Parse(stdout);
        JsonElement video = default;
        var hasAudio = false;
        var audioCodec = "";
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() : null;
            if (type == "video" && video.ValueKind == JsonValueKind.Undefined)
                video = stream;
            else if (type == "audio" && !hasAudio)
            {
                hasAudio = true;
                audioCodec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "";
            }
        }
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("输入文件中没有视频流。");

        long frames = 0;
        if (video.TryGetProperty("nb_frames", out var nbFrames) &&
            long.TryParse(nbFrames.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            frames = parsed;
        double duration = 0;
        if (video.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
            duration = parsedDuration;

        return new VideoInfo
        {
            Codec = video.GetProperty("codec_name").GetString() ?? "",
            Width = video.GetProperty("width").GetInt32(),
            Height = video.GetProperty("height").GetInt32(),
            PixelFormat = video.TryGetProperty("pix_fmt", out var fmt) ? fmt.GetString() ?? "" : "",
            FrameRate = video.TryGetProperty("avg_frame_rate", out var rate)
                ? rate.GetString() ?? "0/1"
                : video.TryGetProperty("r_frame_rate", out var realRate) ? realRate.GetString() ?? "0/1" : "0/1",
            FrameCount = frames,
            DurationSeconds = duration,
            HasAudio = hasAudio,
            AudioCodec = audioCodec,
            ColorPrimaries = video.TryGetProperty("color_primaries", out var primaries) ? primaries.GetString() ?? "" : "",
            ColorTransfer = video.TryGetProperty("color_transfer", out var transfer) ? transfer.GetString() ?? "" : "",
            ColorSpace = video.TryGetProperty("color_space", out var space) ? space.GetString() ?? "" : "",
            ColorRange = video.TryGetProperty("color_range", out var range) ? range.GetString() ?? "" : "",
        };
    }
}

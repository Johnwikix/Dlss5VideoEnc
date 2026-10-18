using System.Diagnostics;
using Dlss5Demo.Core;

internal static class DriverHdrIntegration
{
    internal static void Policy()
    {
        var sdr = new VideoInfo { Width = 320, Height = 192, Codec = "h264", PixelFormat = "yuv420p", FrameRate = "24/1",
            ColorPrimaries = "bt709", ColorTransfer = "bt709", ColorSpace = "bt709" };
        var force = new TranscodeOptions { Video = new VideoTranscodeOptions { ForceHdr = true } };
        Require(DlssVideoPipeline.NeedsDriverHdr(force, sdr), "SDR force HDR must map pixels");
        Require(!DlssVideoPipeline.NeedsDriverHdr(force, sdr with { ColorTransfer = "smpte2084" }), "PQ must bypass mapping");
        Require(!DlssVideoPipeline.NeedsDriverHdr(force, sdr with { ColorTransfer = "arib-std-b67" }), "HLG must bypass mapping");
        Require(!DlssVideoPipeline.NeedsDriverHdr(force with { Video = force.Video with { PreserveHdr = false } }, sdr), "SDR output must bypass mapping");
        var rife = DlssVideoPipeline.NormalizeHdrOptions(force with { EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife });
        Require(!rife.Video.ForceHdr && !DlssVideoPipeline.NeedsDriverHdr(rife, sdr), "RIFE must stay SDR");
        Require(DlssVideoPipeline.NormalizeHdrOptions(force with { InterpolationBackend = FrameInterpolationBackend.Rife }).Video.ForceHdr,
            "Inactive RIFE selection must not disable forced HDR");
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "driver-hdr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new List<string>();
        var previous = PipelineLog.Sink;
        PipelineLog.Sink = line => { lock (log) log.Add(line); Console.WriteLine(line); };
        string fixture = Path.Combine(root, "source-bt709.mp4");
        await Command(DemoPaths.FfmpegPath, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x192:rate=24000/1001",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-frames:v", "8", "-t", "0.333667", "-c:v", "libx264",
            "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv", "-c:a", "aac", fixture]);
        var source = MediaProbe.Probe(fixture, DemoPaths.FfprobePath);
        TranscodeOptions Options() => new() { FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false, EnablePreviewFrames = false,
            HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
            LogPath = Path.Combine(root, "nr.log"), VsrLogPath = Path.Combine(root, "vsr.log"),
            MaxFrames = 6, Video = new VideoTranscodeOptions { ForceHdr = true } };
        async Task<string> Run(string name, string input, TranscodeOptions options, bool mapped, bool expectHdr = true)
        {
            lock (log) log.Clear();
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, name + ".mp4"), options).WaitAsync(TimeSpan.FromMinutes(3));
            var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
            int multiplier = options.EnableInterpolation ? options.InterpolationMultiplier : 1;
            Require(result.FramesProcessed == options.MaxFrames && output.FrameCount == result.FramesProcessed * multiplier, name + ": lost frames");
            Require(Math.Abs(output.Fps - source.Fps * multiplier) < .00001, name + ": wrong frame rate");
            Require(output.HasAudio && Math.Abs(output.DurationSeconds - result.FramesProcessed / source.Fps) < .003, name + ": audio/duration changed");
            Require(output.Width == source.Width * options.SuperResolutionScale && output.Height == source.Height * options.SuperResolutionScale, name + ": wrong size");
            Require(output.IsHdr == expectHdr, name + ": unexpected HDR state");
            if (expectHdr)
            {
                var inputVideo = MediaProbe.Probe(input, DemoPaths.FfprobePath);
                Require(output.PixelFormat == "yuv420p10le", name + ": encoder did not produce 10-bit YUV");
                Require(output.ColorPrimaries == "bt2020" && output.ColorSpace == "bt2020nc" && output.ColorRange == "tv"
                    && output.ColorTransfer == (mapped ? "smpte2084" : inputVideo.ColorTransfer), name + ": wrong HDR VUI");
            }
            lock (log)
            {
                Require(log.Any(l => l.StartsWith("驱动 RTX Video HDR 已启用")) == mapped, name + ": wrong mapping path");
                if (mapped) Require(log.Any(l => l.StartsWith($"驱动 RTX Video HDR 映射完成：{output.FrameCount} 输出帧")), name + ": tail frames bypassed mapper");
                File.WriteAllLines(Path.Combine(root, name + ".log"), log);
            }
            await Command(DemoPaths.FfprobePath, ["-v", "error", "-show_streams", "-of", "json", result.OutputPath], Path.Combine(root, name + "-probe.json"));
            Console.WriteLine($"PASS HDR {name}: {output.FrameCount} frames @{output.FrameRate}, {output.PixelFormat}, {output.ColorPrimaries}/{output.ColorTransfer}/{output.ColorSpace}/{output.ColorRange}");
            return result.OutputPath;
        }
        async Task<byte[]> Decode(string input, string name, string format = "gbrp16le")
        {
            string path = Path.Combine(root, name + ".raw");
            await Command(DemoPaths.FfmpegPath, ["-v", "error", "-y", "-i", input, "-map", "0:v:0", "-an", "-pix_fmt", format, "-f", "rawvideo", path]);
            return await File.ReadAllBytesAsync(path);
        }
        try
        {
            var hdrSource = await Run("hevc-direct", fixture, Options(), true);
            await Run("av1-direct", fixture, Options() with { VideoEncoder = "av1_nvenc", Video = Options().Video with { VideoCodec = VideoCodec.Av1 } }, true);
            foreach (int multiplier in new[] { 2, 4, 6 })
                await Run("vulkan-" + multiplier, fixture, Options() with { EnableInterpolation = true, InterpolationMultiplier = multiplier }, true);
            await Run("one-frame-vulkan", fixture, Options() with { EnableInterpolation = true, MaxFrames = 1 }, true);
            await Run("nr-sr-vulkan", fixture, Options() with { EnableNr = true, SuperResolutionScale = 2, EnableInterpolation = true, MaxFrames = 3 }, true);
            await Run("software-two-pass", fixture, Options() with { VideoEncoder = "libx265", Video = Options().Video with {
                HardwareBackend = HardwareBackend.Software, RateControl = RateControlMode.Bitrate, TwoPassEnabled = true, VideoBitrateKbps = 1000 } }, true);

            // A driver-mapped HDR fixture makes this an actual PQ preservation test.
            foreach (string transfer in new[] { "smpte2084", "arib-std-b67" })
            {
                string input = hdrSource;
                if (transfer == "arib-std-b67")
                {
                    input = Path.Combine(root, "source-hlg.mp4");
                    await Command(DemoPaths.FfmpegPath, ["-v", "error", "-y", "-i", hdrSource, "-vf",
                        "zscale=transferin=smpte2084:transfer=arib-std-b67,format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc",
                        "-c:v", "hevc_nvenc", "-pix_fmt", "p010le", "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc", "-c:a", "copy", input]);
                }
                var settings = Options() with { EnableInterpolation = true };
                string forced = await Run(transfer + "-forced", input, settings, false);
                string automatic = await Run(transfer + "-auto", input, settings with { Video = settings.Video with { ForceHdr = false } }, false);
                var forcedPixels = await Decode(forced, transfer + "-forced");
                var automaticPixels = await Decode(automatic, transfer + "-auto");
                Require(forcedPixels.SequenceEqual(automaticPixels), transfer + ": ForceHdr changed native HDR pixels");
                Console.WriteLine("PASS native HDR forced/auto decoded pixels identical: " + transfer);
            }

            // Compare encoded mapped pixels with the mapper's direct RGB10 output after
            // the same explicit matrix conversion; this catches channel order/range errors.
            byte[] rgba = await Decode(fixture, "source-rgba", "rgba");
            string raw = Path.Combine(root, "driver-reference.rgb10");
            using (var mapper = new DriverHdrMapper(source.Width, source.Height))
            using (var file = File.Create(raw))
                for (int i = 0; i < 6; i++) mapper.Write(rgba.AsSpan(i * mapper.FrameBytes, mapper.FrameBytes), file);
            string reference = Path.Combine(root, "driver-reference.yuv");
            await Command(DemoPaths.FfmpegPath, ["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "x2bgr10le", "-video_size", "320x192", "-i", raw,
                "-vf", "format=gbrp16le,zscale=matrixin=gbr:matrix=bt2020nc:transferin=smpte2084:transfer=smpte2084:primariesin=bt2020:primaries=bt2020:rangein=full:range=limited,format=yuv420p10le",
                "-f", "rawvideo", reference]);
            var expected = await File.ReadAllBytesAsync(reference);
            var actual = await Decode(hdrSource, "encoded-yuv", "yuv420p10le");
            var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(actual.AsSpan());
            var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(expected.AsSpan());
            Require(a.Length == b.Length, "Reference size changed");
            double squared = 0;
            for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; squared += d * d; }
            double psnr = 10 * Math.Log10(1023.0 * 1023 * a.Length / Math.Max(squared, .0001));
            Require(psnr > 35, $"Driver→encode pixel comparison failed: {psnr:F2} dB");
            Console.WriteLine($"PASS driver RGB10 reference vs HEVC decoded YUV: {psnr:F2} dB");

            foreach (var bad in new[] { Options() with { VideoEncoder = "h264_nvenc" }, Options() with { Video = Options().Video with { Container = VideoContainer.Gif } } })
            {
                string invalid = Path.Combine(root, "invalid-" + Guid.NewGuid().ToString("N") + ".mp4");
                try { await DlssVideoPipeline.RunAsync(fixture, invalid, bad); throw new Exception("Invalid HDR encoder accepted"); }
                catch (NotSupportedException) { Require(!File.Exists(invalid), "Invalid settings reserved output"); }
            }
            using (var stop = new CancellationTokenSource())
            {
                try { await DlssVideoPipeline.RunAsync(fixture, Path.Combine(root, "cancel.mp4"), Options() with { EnableInterpolation = true }, new Cancel(stop), stop.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Console.WriteLine("PASS HDR mapping cancellation joins workers"); }
            }
            await Run("restart", fixture, Options(), true);
            Console.WriteLine("HDR integration artifacts: " + root);
        }
        finally { PipelineLog.Sink = previous; DlssVideoPipeline.ReleaseGpuResources(); }
    }

    private sealed class Cancel(CancellationTokenSource stop) : IProgress<TranscodeProgress>
    { public void Report(TranscodeProgress value) => stop.Cancel(); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Command(string executable, string[] arguments, string? stdoutPath = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        if (process.ExitCode != 0) throw new Exception(await error);
        if (stdoutPath != null) await File.WriteAllTextAsync(stdoutPath, await output);
    }
}

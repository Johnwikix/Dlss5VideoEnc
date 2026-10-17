using System.Diagnostics;
using System.Runtime.InteropServices;
using Dlss5Demo.Core;

internal static class InterpolationIntegration
{
    internal static async Task PixelsAsync()
    {
        const int width = 512, height = 96, bytes = width * height * 8;
        var root = Path.Combine(Environment.CurrentDirectory, ".qa", "interpolation-port-2026-10-17", "pixels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = new byte[bytes * 3];
        var pixels = MemoryMarshal.Cast<byte, ushort>(input.AsSpan());
        for (var frame = 0; frame < 3; frame++)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (frame * width * height + y * width + x) * 4;
            var value = (ushort)(1000 + ((x + frame * 3) % width) * 97 + (y % 7) * 11);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
            pixels[index + 3] = ushort.MaxValue;
        }
        var inputPath = Path.Combine(root, "input.rgba64");
        var outputPath = Path.Combine(root, "output.rgba64");
        await File.WriteAllBytesAsync(inputPath, input);
        var video = new VideoInfo { Codec = "rawvideo", PixelFormat = "rgba64le", Width = width, Height = height, FrameRate = "24000/1001" };
        using var session = new VulkanInterpolationSession(width, height, true, video, 2, true);
        using var destination = new MemoryStream();
        for (int i = 0; i < 3; i++) session.Write(input.AsSpan(i * bytes, bytes), destination, default);
        session.Complete(destination, default);
        var output = destination.ToArray();
        await File.WriteAllBytesAsync(outputPath, output);
        Require(output.Length == bytes * 6, "Pixel probe did not double frames");
        Require(input.AsSpan(0, bytes).SequenceEqual(output.AsSpan(0, bytes)), "Original HDR frame changed inside FRUC");
        for (var frame = 0; frame < 3; frame++)
            Require(input.AsSpan(frame * bytes, bytes).SequenceEqual(output.AsSpan(frame * bytes * 2, bytes)),
                $"Original frame {frame} shifted away from its presentation timestamp");
        Require(!output.AsSpan(bytes, bytes).SequenceEqual(input.AsSpan(0, bytes))
            && !output.AsSpan(bytes, bytes).SequenceEqual(input.AsSpan(bytes, bytes)), "Generated frame repeats a source frame");
        var middle = MemoryMarshal.Cast<byte, ushort>(output.AsSpan(bytes, bytes));
        var values = new HashSet<ushort>();
        for (var index = 0; index < middle.Length; index += 4) values.Add(middle[index]);
        Require(values.Count > 256, $"HDR synthesis quantized to only {values.Count} gray values");
        Console.WriteLine($"PASS Vulkan HDR pixels: original exact; generated frame differs from both inputs; {values.Count} distinct 16-bit gray values");
        Console.WriteLine("Pixel artifacts: " + root);
    }

    internal static async Task RunAsync(string input, FrameInterpolationBackend backend = FrameInterpolationBackend.NvidiaOpticalFlow)
    {
        var source = MediaProbe.Probe(input, DemoPaths.FfprobePath);
        var root = Path.Combine(Environment.CurrentDirectory, ".qa", "interpolation-port-2026-10-17", "integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logs = new List<string>();
        var previous = PipelineLog.Sink;
        PipelineLog.Sink = line => { lock (logs) logs.Add(line); };
        TranscodeOptions Options() => new()
        {
            FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false, EnableInterpolation = true,
            InterpolationBackend = backend,
            HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
            LogPath = Path.Combine(root, "nr.log"), VsrLogPath = Path.Combine(root, "vsr.log"),
            MaxFrames = 6, EnablePreviewFrames = false,
            Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.Copy },
        };
        async Task Run(string name, TranscodeOptions options)
        {
            lock (logs) logs.Clear();
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, name + ".mp4"), options)
                .WaitAsync(TimeSpan.FromMinutes(5));
            var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
            var multiplier = options.EnableInterpolation ? options.InterpolationMultiplier : 1;
            Require(output.FrameCount == result.FramesProcessed * multiplier, $"{name}: source={result.FramesProcessed}, output={output.FrameCount}");
            Require(Math.Abs(output.Fps - source.Fps * multiplier) < .00001, name + ": incorrect rational rate");
            Require(output.Width == source.Width * options.SuperResolutionScale && output.Height == source.Height * options.SuperResolutionScale, name + ": wrong dimensions");
            Require(output.HasAudio == source.HasAudio, name + ": audio lost");
            Require(Math.Abs(output.DurationSeconds - result.FramesProcessed / source.Fps) < .003, name + ": changed clip duration");
            Require(output.IsHdr == source.IsHdr, name + ": HDR lost");
            if (source.IsHdr)
                Require(output.PixelFormat.Contains("10") && output.ColorTransfer == source.ColorTransfer && output.ColorPrimaries == source.ColorPrimaries,
                    name + ": HDR depth or color tags lost");
            if (options.EnableInterpolation)
                Require(logs.Any(line => line.StartsWith(backend == FrameInterpolationBackend.Rife ? "RIFE GPU 已验证" : "NVIDIA Vulkan 补帧已验证")), name + ": did not validate actual graph");
            Console.WriteLine($"PASS interpolation {name}: {output.Width}x{output.Height}, {output.FrameCount} frames @{output.FrameRate}, {output.DurationSeconds:F6}s, {output.PixelFormat}, {output.ColorTransfer}");
        }
        try
        {
            await Run("only-2x", Options());
            await Run("only-4x", Options() with { InterpolationMultiplier = 4 });
            await Run("only-6x", Options() with { InterpolationMultiplier = 6 });
            await Run("one-frame", Options() with { MaxFrames = 1 });
            await Run("disabled", Options() with { EnableInterpolation = false });
            await Run("saved-fps-ignored", Options() with
            { Video = new VideoTranscodeOptions { FpsMode = FpsMode.Fixed, FpsValue = 30 } });
            if (source.Width <= 1280 && source.Height <= 720)
            {
                await Run("disabled-nr", Options() with { EnableInterpolation = false, EnableNr = true });
                await Run("disabled-sr2", Options() with { EnableInterpolation = false, SuperResolutionScale = 2 });
                await Run("disabled-nr-sr2", Options() with { EnableInterpolation = false, EnableNr = true, SuperResolutionScale = 2 });
                await Run("sr2", Options() with { SuperResolutionScale = 2 });
                await Run("nr", Options() with { EnableNr = true });
                await Run("nr-sr2", Options() with { EnableNr = true, SuperResolutionScale = 2 });
                await Run("sr2-nr", Options() with { EnableNr = true, SuperResolutionScale = 2, NrBeforeSuperResolution = false });
                if (!source.IsHdr)
                    await Run("two-pass", Options() with
                    {
                        VideoEncoder = "libx264",
                        Video = new VideoTranscodeOptions { VideoCodec = VideoCodec.H264, HardwareBackend = HardwareBackend.Software,
                            RateControl = RateControlMode.Bitrate, TwoPassEnabled = true, VideoBitrateKbps = 1500 },
                    });
                foreach (var count in new[] { 1, 6 })
                {
                    var fixture = Path.Combine(root, $"unknown-{count}.mkv");
                    var start = new ProcessStartInfo(DemoPaths.FfmpegPath)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-v", "error", "-y", "-i", input, "-map", "0:v:0", "-c", "copy",
                        "-frames:v", count.ToString(), fixture }) start.ArgumentList.Add(argument);
                    using var process = Process.Start(start)!;
                    var error = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Require(process.ExitCode == 0, await error);
                    Require(MediaProbe.Probe(fixture, DemoPaths.FfprobePath).FrameCount == 0, "Matroska fixture unexpectedly has frame count");
                    var result = await DlssVideoPipeline.RunAsync(fixture, Path.Combine(root, $"unknown-{count}.mp4"), Options() with { MaxFrames = null });
                    var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
                    Require(result.FramesProcessed == count && output.FrameCount == count * 2, "Unknown source count caused duplicated/missing frames");
                    Require(Math.Abs(output.DurationSeconds - count / source.Fps) < .003, "Unknown source count changed duration");
                    Console.WriteLine($"PASS interpolation Matroska without nb_frames: {count} source → {output.FrameCount} output");
                }
            }
            if (source.FrameCount > 6) await Run("full-length", Options() with { MaxFrames = null });
            using (var stop = new CancellationTokenSource())
            {
                try
                {
                    await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options(),
                        new CancelProgress(stop), stop.Token).WaitAsync(TimeSpan.FromMinutes(1));
                    throw new Exception("Interpolation ignored cancellation");
                }
                catch (OperationCanceledException) { Console.WriteLine("PASS interpolation cancellation joins pipeline"); }
            }
            await Run("restart", Options());
            try
            {
                await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "invalid.mp4"), Options() with { InterpolationMultiplier = 3 });
                throw new Exception("Invalid multiplier was accepted");
            }
            catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS interpolation invalid multiplier rejected before output reservation"); }
            Require(!File.Exists(Path.Combine(root, "invalid.mp4")), "Invalid interpolation reserved an output");
            Console.WriteLine("Interpolation artifacts: " + root);
        }
        finally { PipelineLog.Sink = previous; }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private sealed class CancelProgress(CancellationTokenSource stop) : IProgress<TranscodeProgress>
    { public void Report(TranscodeProgress value) => stop.Cancel(); }
}

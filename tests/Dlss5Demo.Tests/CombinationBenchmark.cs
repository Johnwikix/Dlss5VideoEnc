using System.Diagnostics;
using System.Text.Json;
using Dlss5Demo.Core;

internal static class CombinationBenchmark
{
    // Keep both pipeline time and wall time: model initialization is significant for short clips.
    internal static async Task RunAsync(string input, string root)
    {
        Directory.CreateDirectory(root);
        var cases = new[]
        {
            new Case("540-sr2", 960, 2, 1), new Case("540-sr4", 960, 4, 1),
            new Case("1080-sr4", 1920, 4, 1),
            new Case("540-rife2", 960, 1, 2), new Case("1080-rife4", 1920, 1, 4),
            new Case("2160-rife2", 3840, 1, 2),
            new Case("540-sr2-rife4", 960, 2, 4), new Case("540-sr4-rife2", 960, 4, 2),
            new Case("1080-sr2-rife2", 1920, 2, 2),
            new Case("540-nr-sr2-rife4", 960, 2, 4, Nr: true),
            new Case("540-sr2-rife6-scale", 960, 2, 6, Scale: true),
            new Case("1080-rife4-scale", 1920, 1, 4, Scale: true),
            new Case("540-rife6-host", 960, 1, 6, Host: true),
            new Case("540-full4-scale", 960, 1, 4, Scale: true, Model: RifeModel.Full),
        };
        var measurements = new List<object>();
        foreach (var group in cases.GroupBy(c => c.Width))
        {
            var fixture = Path.Combine(root, $"input-{group.Key}.mp4");
            if (!File.Exists(fixture))
            {
                var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in new[] { "-v", "error", "-i", input, "-an", "-frames:v", "24", "-vf", $"scale={group.Key}:{group.Key * 9 / 16}:flags=lanczos", "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", fixture })
                    start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new Exception("Fixture generation failed");
            }
            foreach (var test in group)
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var options = new TranscodeOptions
                {
                    HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
                    LogPath = Path.Combine(root, "nr.log"), VsrLogPath = Path.Combine(root, "vsr.log"),
                    EnableNr = test.Nr, SuperResolutionScale = test.Sr,
                    EnableInterpolation = test.Multiplier > 1, InterpolationBackend = FrameInterpolationBackend.Rife,
                    InterpolationMultiplier = Math.Max(2, test.Multiplier), InterpolationModel = test.Model,
                    UseGpuFramePipeline = !test.Host, MaxFrames = 24, EnablePreviewFrames = false,
                    Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None,
                        ScaleMode = test.Scale ? ScaleMode.Exact : ScaleMode.None, ScaleWidth = 1280, ScaleHeight = 720 },
                };
                using var log = File.CreateText(Path.Combine(root, $"{test.Name}-{iteration}.log"));
                var previous = PipelineLog.Sink;
                PipelineLog.Sink = message => { lock (log) { log.WriteLine(message); log.Flush(); } };
                try
                {
                    var wall = Stopwatch.StartNew();
                    var result = await DlssVideoPipeline.RunAsync(fixture, Path.Combine(root, $"{test.Name}-{iteration}.mp4"), options);
                    wall.Stop();
                    var video = MediaProbe.Probe(result.OutputPath);
                    int width = test.Scale ? 1280 : test.Width * test.Sr, height = test.Scale ? 720 : test.Width * 9 / 16 * test.Sr;
                    if (video.Width != width || video.Height != height || video.FrameCount != 24 * test.Multiplier)
                        throw new Exception($"Invalid output {test.Name}: {video.Width}x{video.Height}, {video.FrameCount} frames");
                    measurements.Add(new { test.Name, iteration, width, height, seconds = result.Elapsed.TotalSeconds,
                        wallSeconds = wall.Elapsed.TotalSeconds, sourceFps = result.AverageFps, outputFrames = video.FrameCount });
                    await File.WriteAllTextAsync(Path.Combine(root, "measurements.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"BENCH {test.Name} {iteration}: {result.AverageFps:F2} source fps; wall {wall.Elapsed.TotalSeconds:F2}s");
                }
                finally { PipelineLog.Sink = previous; }
            }
        }
    }

    private sealed record Case(string Name, int Width, int Sr, int Multiplier, bool Nr = false,
        bool Scale = false, bool Host = false, RifeModel Model = RifeModel.Lite);
}

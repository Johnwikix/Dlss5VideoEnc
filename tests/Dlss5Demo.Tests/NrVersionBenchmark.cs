using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dlss5Demo.Core;

internal static class NrVersionBenchmark
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Runtime(string Label, string Path, string Version, string Sha256);
    private sealed record Scenario(string Name, int Width, int Height, int Frames,
        int Sr = 1, int Rife = 1, bool NrFirst = true);
    private sealed record Measurement(string Kind, string Scenario, string Runtime, int Trial, bool Warmup,
        int Frames, double SourceFps, double ProcessSeconds, double WallSeconds, double InitSeconds,
        string? Output, Dictionary<string, double>? StageMs);

    // Both DLLs run through the same compiled host/pipeline. Trial 0 primes each configuration.
    internal static async Task RunAsync(string input, string root, string oldDll, string newDll)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "measurements.json"))) throw new IOException("Use a new benchmark output directory.");
        var runtimes = new[] { Describe("old", oldDll), Describe("new", newDll) };
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
        {
            input = Path.GetFullPath(input), runtimes, measuredTrials = 3, warmupTrials = 1,
            runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount,
            nrHostSha256 = Hash(DemoPaths.HostDllPath), coreSha256 = Hash(typeof(DlssNrHost).Assembly.Location),
            settings = "HEVC NVENC P5/CQ19; SR quality 4; RIFE Lite; preview/audio off; optical flow and NR history on; NR FIFO on",
        }, Json));

        string hd = await Fixture(input, root, 1920, 1080);
        string uhd = await Fixture(input, root, 3840, 2160);
        var measurements = new List<Measurement>();
        var previous = PipelineLog.Sink;
        try
        {
            foreach (var hostCase in new[] { (Path: hd, Width: 1920, Height: 1080, Motion: false),
                (Path: hd, Width: 1920, Height: 1080, Motion: true), (Path: uhd, Width: 3840, Height: 2160, Motion: true) })
            {
                byte[] frame = await DecodeFrame(hostCase.Path, hostCase.Width, hostCase.Height);
                string name = $"host-{hostCase.Height}p-motion-{hostCase.Motion}";
                for (int trial = 0; trial <= 3; trial++)
                foreach (var runtime in trial % 2 == 0 ? runtimes : runtimes.Reverse())
                {
                    using var log = File.CreateText(Path.Combine(root, $"{name}-{runtime.Label}-{trial}.log"));
                    PipelineLog.Sink = message => { lock (log) { log.WriteLine(message); log.Flush(); } };
                    var row = MeasureHost(runtime, name, root, trial, frame, hostCase.Width, hostCase.Height, hostCase.Motion);
                    await Save(row);
                }
            }

            var cases = new[]
            {
                new Scenario("1080p-NR", 1920, 1080, 240),
                new Scenario("4K-NR", 3840, 2160, 180),
                new Scenario("1080p-NR-SR2", 1920, 1080, 180, Sr: 2),
                new Scenario("1080p-NR-SR4", 1920, 1080, 90, Sr: 4),
                new Scenario("1080p-SR2-NR", 1920, 1080, 120, Sr: 2, NrFirst: false),
                new Scenario("1080p-NR-RIFE4", 1920, 1080, 180, Rife: 4),
                new Scenario("1080p-NR-SR2-RIFE2", 1920, 1080, 120, Sr: 2, Rife: 2),
                new Scenario("1080p-NR-SR2-RIFE4", 1920, 1080, 90, Sr: 2, Rife: 4),
            };
            foreach (var test in cases)
            for (int trial = 0; trial <= 3; trial++)
            foreach (var runtime in trial % 2 == 0 ? runtimes : runtimes.Reverse())
            {
                string label = $"{test.Name}-{runtime.Label}-{trial}";
                using var log = File.CreateText(Path.Combine(root, label + ".log"));
                long initStart = 0;
                double initSeconds = 0;
                var stages = new Dictionary<string, double>();
                PipelineLog.Sink = message =>
                {
                    lock (log)
                    {
                        log.WriteLine(message); log.Flush();
                        if (message.StartsWith("NR 运行库:")) initStart = Stopwatch.GetTimestamp();
                        if (message.StartsWith("NGX NR 会话已创建:") && initStart != 0)
                            initSeconds = Stopwatch.GetElapsedTime(initStart).TotalSeconds;
                        if (message.StartsWith("管线 每帧阶段耗时"))
                            foreach (string entry in message[(message.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(','))
                            {
                                string[] parts = entry.Trim().Split('=');
                                if (parts.Length == 2 && double.TryParse(parts[1].Replace("ms", ""), CultureInfo.InvariantCulture, out double value))
                                    stages[parts[0]] = value;
                            }
                    }
                };
                var options = new TranscodeOptions
                {
                    FfmpegPath = DemoPaths.FfmpegPath, HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = runtime.Path,
                    LogPath = Path.Combine(root, label + "-native.log"), VsrLogPath = Path.Combine(root, label + "-vsr.log"),
                    EnableNr = true, MaxFrames = test.Frames, EnablePreviewFrames = false,
                    SuperResolutionScale = test.Sr, SuperResolutionQuality = 4, NrBeforeSuperResolution = test.NrFirst,
                    EnableInterpolation = test.Rife > 1, InterpolationBackend = FrameInterpolationBackend.Rife,
                    InterpolationModel = RifeModel.Lite, InterpolationMultiplier = Math.Max(2, test.Rife),
                    Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None, EncoderPreset = "p5", Quality = 19 },
                };
                var wall = Stopwatch.StartNew();
                var result = await DlssVideoPipeline.RunAsync(test.Width == 1920 ? hd : uhd, Path.Combine(root, label + ".mp4"), options);
                wall.Stop();
                var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
                if (result.FramesProcessed != test.Frames || output.Width != test.Width * test.Sr || output.Height != test.Height * test.Sr
                    || output.FrameCount != test.Frames * test.Rife || Math.Abs(output.Fps - 60 * test.Rife) > .001)
                    throw new InvalidDataException("Benchmark output shape/frame clock changed: " + label);
                await Save(new("pipeline", test.Name, runtime.Label, trial, trial == 0, test.Frames, result.AverageFps,
                    result.Elapsed.TotalSeconds, wall.Elapsed.TotalSeconds, initSeconds, result.OutputPath, stages));
            }
        }
        finally { PipelineLog.Sink = previous; DlssVideoPipeline.ReleaseGpuResources(); }

        var summary = measurements.Where(x => !x.Warmup).GroupBy(x => new { x.Kind, x.Scenario }).Select(group =>
        {
            var oldRows = group.Where(x => x.Runtime == "old").ToArray();
            var newRows = group.Where(x => x.Runtime == "new").ToArray();
            double before = Median(oldRows.Select(x => x.SourceFps)), after = Median(newRows.Select(x => x.SourceFps));
            return new { group.Key.Kind, group.Key.Scenario, oldFps = before, newFps = after, gainPercent = (after / before - 1) * 100,
                oldMin = oldRows.Min(x => x.SourceFps), oldMax = oldRows.Max(x => x.SourceFps), newMin = newRows.Min(x => x.SourceFps), newMax = newRows.Max(x => x.SourceFps),
                oldInitSeconds = Median(oldRows.Select(x => x.InitSeconds)), newInitSeconds = Median(newRows.Select(x => x.InitSeconds)),
                oldWallSeconds = Median(oldRows.Select(x => x.WallSeconds)), newWallSeconds = Median(newRows.Select(x => x.WallSeconds)) };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(summary, Json));
        Console.WriteLine("SUMMARY " + Path.Combine(root, "summary.json"));

        async Task Save(Measurement measurement)
        {
            measurements.Add(measurement);
            await File.WriteAllTextAsync(Path.Combine(root, "measurements.json"), JsonSerializer.Serialize(measurements, Json));
            Console.WriteLine($"BENCH {measurement.Kind} {measurement.Scenario} {measurement.Runtime} trial={measurement.Trial}: {measurement.SourceFps:F2} source fps, process={measurement.ProcessSeconds:F3}s, wall={measurement.WallSeconds:F3}s, NR-init={measurement.InitSeconds:F3}s");
        }
    }

    private static unsafe Measurement MeasureHost(Runtime runtime, string name, string root, int trial, byte[] input, int width, int height, bool motion)
    {
        byte[] output = new byte[input.Length];
        float[]? vectors = motion ? new float[checked(width * height * 2)] : null;
        var wall = Stopwatch.StartNew();
        using var host = DlssNrHost.Create(DemoPaths.HostDllPath, runtime.Path, Path.Combine(root, $"{name}-{runtime.Label}-{trial}-native.log"), width, height, new DlssNrOptions());
        host.ConfigureMotionGuidance(motion);
        double init = wall.Elapsed.TotalSeconds;
        const int count = 240;
        var timer = new Stopwatch();
        fixed (byte* source = input, destination = output)
        fixed (float* guidance = vectors)
        {
            for (int i = 0; i < 30; i++) Require(host.ProcessVideoFrame(source, guidance, destination, i == 0), "NR host warmup failed");
            timer.Start();
            int pending = 0, capacity = host.QueueCapacity;
            for (int i = 0; i < count; i++)
            {
                Require(host.EnqueueVideoFrame(source, guidance, false), "NR enqueue failed");
                if (++pending >= capacity) { Require(host.DequeueVideoFrame(destination), "NR dequeue failed"); pending--; }
            }
            while (pending-- > 0) Require(host.DequeueVideoFrame(destination), "NR drain failed");
            timer.Stop();
        }
        Require(output.Any(value => value != 0), "NR output is empty");
        return new("host", name, runtime.Label, trial, trial == 0, count, count / timer.Elapsed.TotalSeconds,
            timer.Elapsed.TotalSeconds, wall.Elapsed.TotalSeconds, init, null, null);
    }

    private static Runtime Describe(string label, string path)
    {
        path = NrRuntimeCatalog.ResolveArgument(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(label, path, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}", Hash(path));
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<string> Fixture(string input, string root, int width, int height)
    {
        string path = Path.Combine(root, $"input-{width}x{height}.mp4");
        if (!File.Exists(path)) await Ffmpeg(["-v", "error", "-i", input, "-an", "-frames:v", "240", "-vf", $"scale={width}:{height}:flags=lanczos,fps=60",
            "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", path]);
        var video = MediaProbe.Probe(path, DemoPaths.FfprobePath);
        Require(video.FrameCount == 240 && video.Width == width && video.Height == height && video.Fps == 60, "Fixture must provide 240 frames at 60 fps");
        return path;
    }
    private static async Task<byte[]> DecodeFrame(string path, int width, int height)
    {
        var data = await Ffmpeg(["-v", "error", "-ss", "1", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"]);
        Require(data.Length == checked(width * height * 4), "NR source frame length changed");
        return data;
    }
    private static async Task<byte[]> Ffmpeg(string[] arguments)
    {
        var start = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, await error);
        return output.ToArray();
    }
}

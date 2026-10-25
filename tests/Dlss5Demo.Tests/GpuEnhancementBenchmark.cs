using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dlss5Demo.Core;

internal static class GpuEnhancementBenchmark
{
    internal static async Task SlotsAsync(string input, string root, string dll)
    {
        Directory.CreateDirectory(root);
        var previous = PipelineLog.Sink;
        var results = new List<object>();
        try
        {
            for (int trial = 0; trial < 4; trial++)
            foreach (int slots in trial % 2 == 0 ? new[] { 3, 4, 5 } : new[] { 5, 4, 3 })
            {
                string label = $"slots-{slots}-{trial}";
                using var log = new StreamWriter(Path.Combine(root, label + ".log")) { AutoFlush = true };
                PipelineLog.Sink = message => { lock (log) log.WriteLine(message); };
                var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, label + ".mp4"), new TranscodeOptions
                {
                    FfmpegPath = DemoPaths.FfmpegPath, HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = NrRuntimeCatalog.ResolveArgument(dll), LogPath = Path.Combine(root, label + "-nr.log"),
                    VsrLogPath = Path.Combine(root, label + "-vsr.log"), GpuFrameCapacity = slots, MaxFrames = 90, SuperResolutionScale = 2,
                    EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife, InterpolationMultiplier = 4, EnablePreviewFrames = false,
                    Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None, EncoderPreset = "p5", Quality = 19 },
                });
                results.Add(new { slots, trial, result.AverageFps, result.OutputPath });
                await File.WriteAllTextAsync(Path.Combine(root, "slots.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"SLOTS {slots} trial {trial}: {result.AverageFps:F4}");
            }
        }
        finally { PipelineLog.Sink = previous; DlssVideoPipeline.ReleaseGpuResources(); }
    }

    private sealed record Case(string Name, int Width, int Height, int Frames, int Sr, int Rife, bool Nr = true, bool NrFirst = true);
    private sealed record Row(string Scenario, bool Gpu, int Trial, int Frames, double Fps, double Seconds, double WallSeconds, string Output);

    internal static async Task RunAsync(string fixtures, string root, string dll)
    {
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "measurements.json"))) throw new IOException("Use a fresh output directory.");
        dll = NrRuntimeCatalog.ResolveArgument(dll);
        var json = new JsonSerializerOptions { WriteIndented = true };
        string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
        {
            fixtures = Path.GetFullPath(fixtures), dll, nrVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion,
            dllSha256 = Hash(dll), nrHostSha256 = Hash(DemoPaths.HostDllPath), vsrHostSha256 = Hash(DemoPaths.VsrHostDllPath),
            coreSha256 = Hash(typeof(DlssNrHost).Assembly.Location), measuredTrials = 3, warmupTrials = 1,
            settings = "Same GPU NR/history + SR Q4 + RIFE Lite + HEVC NVENC P5 CQ19. Only UseGpuEnhancementPipeline toggled; no audio/preview. Processing FPS excludes setup for both paths; wall time includes all setup/teardown. CUDA NVDEC color conversion differs from host swscale: measure quality separately.",
        }, json));
        var cases = new[]
        {
            new Case("1080p-NR-RIFE2", 1920, 1080, 180, 1, 2),
            new Case("1080p-NR-RIFE4", 1920, 1080, 180, 1, 4),
            new Case("1080p-NR-SR2-RIFE2", 1920, 1080, 120, 2, 2),
            new Case("1080p-NR-SR2-RIFE4", 1920, 1080, 90, 2, 4),
            new Case("1080p-SR2-NR-RIFE2", 1920, 1080, 90, 2, 2, NrFirst: false),
            new Case("1080p-SR2-RIFE2", 1920, 1080, 120, 2, 2, Nr: false),
            new Case("4K-NR-RIFE2", 3840, 2160, 90, 1, 2),
        };
        var rows = new List<Row>(); var previousLog = PipelineLog.Sink;
        try
        {
            foreach (var test in cases)
            {
                string input = Path.Combine(fixtures, $"input-{test.Width}x{test.Height}.mp4");
                var source = MediaProbe.Probe(input);
                if (source.Width != test.Width || source.Height != test.Height || source.FrameCount < test.Frames || source.Fps != 60)
                    throw new InvalidDataException("Unexpected real-video fixture: " + input);
                for (int trial = 0; trial < 4; trial++)
                foreach (bool gpu in trial % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    string label = $"{test.Name}-{(gpu ? "gpu" : "host")}-{trial}";
                    using var log = new StreamWriter(Path.Combine(root, label + ".log")) { AutoFlush = true };
                    bool usedGpu = false;
                    PipelineLog.Sink = line => { lock (log) { log.WriteLine(line); if (line.StartsWith("显存帧管线：")) usedGpu = true; } };
                    var options = new TranscodeOptions
                    {
                        FfmpegPath = DemoPaths.FfmpegPath, HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = dll,
                        LogPath = Path.Combine(root, label + "-nr.log"), VsrLogPath = Path.Combine(root, label + "-vsr.log"),
                        MaxFrames = test.Frames, EnableNr = test.Nr, UseGpuEnhancementPipeline = gpu, EnablePreviewFrames = false,
                        SuperResolutionScale = test.Sr, SuperResolutionQuality = 4, NrBeforeSuperResolution = test.NrFirst,
                        EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife,
                        InterpolationModel = RifeModel.Lite, InterpolationMultiplier = test.Rife,
                        Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None, EncoderPreset = "p5", Quality = 19 },
                    };
                    var wall = Stopwatch.StartNew();
                    var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, label + ".mp4"), options);
                    wall.Stop();
                    var output = MediaProbe.Probe(result.OutputPath);
                    if (usedGpu != gpu || result.FramesProcessed != test.Frames || output.Width != test.Width * test.Sr || output.Height != test.Height * test.Sr
                        || output.FrameCount != test.Frames * test.Rife || output.Fps != 60 * test.Rife)
                        throw new InvalidDataException("Unexpected benchmark transport/frame clock: " + label);
                    rows.Add(new(test.Name, gpu, trial, test.Frames, result.AverageFps, result.Elapsed.TotalSeconds, wall.Elapsed.TotalSeconds, result.OutputPath));
                    await File.WriteAllTextAsync(Path.Combine(root, "measurements.json"), JsonSerializer.Serialize(rows, json));
                    Console.WriteLine($"BENCH {label}: {result.AverageFps:F2} source fps; process={result.Elapsed.TotalSeconds:F3}s; wall={wall.Elapsed.TotalSeconds:F3}s");
                }
            }
        }
        finally { PipelineLog.Sink = previousLog; DlssVideoPipeline.ReleaseGpuResources(); }
        static double Median(IEnumerable<double> values) => values.Order().ElementAt(1);
        var summary = rows.Where(x => x.Trial != 0).GroupBy(x => x.Scenario).Select(group =>
        {
            var host = group.Where(x => !x.Gpu).ToArray(); var gpu = group.Where(x => x.Gpu).ToArray();
            double before = Median(host.Select(x => x.Fps)), after = Median(gpu.Select(x => x.Fps));
            return new { Scenario = group.Key, hostFps = before, gpuFps = after, gainPercent = (after / before - 1) * 100,
                hostMin = host.Min(x => x.Fps), hostMax = host.Max(x => x.Fps), gpuMin = gpu.Min(x => x.Fps), gpuMax = gpu.Max(x => x.Fps),
                hostWallSeconds = Median(host.Select(x => x.WallSeconds)), gpuWallSeconds = Median(gpu.Select(x => x.WallSeconds)) };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(summary, json));
        Console.WriteLine("SUMMARY " + Path.Combine(root, "summary.json"));
    }
}

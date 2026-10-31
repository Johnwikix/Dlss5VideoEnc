using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dlss5Demo.Core;

internal static class GpuTemporalBenchmark
{
    private sealed record Case(string Name, int Width, int Height, int Frames, int Sr = 1, int Rife = 1, bool NrFirst = true);
    private sealed record Row(string Scenario, bool Gpu, int Trial, int Frames, double Fps, double Seconds, double WallSeconds, string Output);

    // Reuse the exact real-video fixtures from NrVersionBenchmark. The only
    // variable is the optical-flow/history transport; NR DLL is fixed.
    internal static async Task RunAsync(string fixtures, string root, string dll)
    {
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, "measurements.json"))) throw new IOException("Use a fresh output directory.");
        dll = NrRuntimeCatalog.ResolveArgument(dll);
        var json = new JsonSerializerOptions { WriteIndented = true };
        string Hash(string path) { using var data = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(data)); }
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(new
        {
            fixtures = Path.GetFullPath(fixtures), dll, nrVersion = FileVersionInfo.GetVersionInfo(dll).FileVersion,
            dllSha256 = Hash(dll), hostSha256 = Hash(DemoPaths.HostDllPath), coreSha256 = Hash(typeof(DlssNrHost).Assembly.Location),
            measuredTrials = 3, warmupTrials = 1, settings = "CPU reference vs CUDA/NVOFA/shared D3D12; same NR, flow quality and history; HEVC NVENC P5 CQ19; SR Q4; RIFE Lite; no audio/preview",
        }, json));
        var cases = new[]
        {
            new Case("1080p-NR", 1920, 1080, 240), new Case("4K-NR", 3840, 2160, 180),
            new Case("1080p-NR-SR2", 1920, 1080, 180, Sr: 2), new Case("1080p-NR-SR4", 1920, 1080, 90, Sr: 4),
            new Case("1080p-SR2-NR", 1920, 1080, 120, Sr: 2, NrFirst: false),
            new Case("1080p-NR-RIFE4", 1920, 1080, 180, Rife: 4),
            new Case("1080p-NR-SR2-RIFE2", 1920, 1080, 120, Sr: 2, Rife: 2),
            new Case("1080p-NR-SR2-RIFE4", 1920, 1080, 90, Sr: 2, Rife: 4),
        };
        var rows = new List<Row>(); var previousLog = PipelineLog.Sink;
        try
        {
            foreach (var test in cases)
            {
                string input = Path.Combine(fixtures, $"input-{test.Width}x{test.Height}.mp4");
                var source = MediaProbe.Probe(input);
                if (source.Width != test.Width || source.Height != test.Height || source.FrameCount < test.Frames || source.Fps != 60)
                    throw new InvalidDataException("Unexpected benchmark fixture");
                for (int trial = 0; trial <= 3; trial++)
                foreach (bool gpu in trial % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    string label = $"{test.Name}-{(gpu ? "gpu" : "cpu")}-{trial}";
                    using var log = File.CreateText(Path.Combine(root, label + ".log"));
                    bool usedGpu = false;
                    PipelineLog.Sink = message =>
                    {
                        lock (log) { log.WriteLine(message); log.Flush(); if (message.StartsWith("光流/历史显存管线：")) usedGpu = true; }
                    };
                    var options = new TranscodeOptions
                    {
                        HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = dll, 
                        LogPath = Path.Combine(root, label + "-native.log"), VsrLogPath = Path.Combine(root, label + "-vsr.log"),
                        Dlss = new DlssNrOptions { UseGpuTemporalProcessing = gpu }, UseGpuEnhancementPipeline = false,
                        MaxFrames = test.Frames, EnablePreviewFrames = false,
                        SuperResolutionScale = test.Sr, SuperResolutionQuality = 4, NrBeforeSuperResolution = test.NrFirst,
                        EnableInterpolation = test.Rife > 1, InterpolationBackend = FrameInterpolationBackend.Rife,
                        InterpolationModel = RifeModel.Lite, InterpolationMultiplier = Math.Max(2, test.Rife),
                        Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None, EncoderPreset = "p5", Quality = 19 },
                    };
                    var wall = Stopwatch.StartNew();
                    var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, label + ".mp4"), options);
                    wall.Stop();
                    var output = MediaProbe.Probe(result.OutputPath);
                    if (usedGpu != gpu || result.FramesProcessed != test.Frames || output.Width != test.Width * test.Sr || output.Height != test.Height * test.Sr
                        || output.FrameCount != test.Frames * test.Rife || output.Fps != 60 * test.Rife)
                        throw new InvalidDataException("Unexpected benchmark path/frame clock: " + label);
                    rows.Add(new(test.Name, gpu, trial, test.Frames, result.AverageFps, result.Elapsed.TotalSeconds, wall.Elapsed.TotalSeconds, result.OutputPath));
                    await File.WriteAllTextAsync(Path.Combine(root, "measurements.json"), JsonSerializer.Serialize(rows, json));
                    Console.WriteLine($"BENCH {label}: {result.AverageFps:F2} source fps; process={result.Elapsed.TotalSeconds:F3}s; wall={wall.Elapsed.TotalSeconds:F3}s");
                }
            }
        }
        finally { PipelineLog.Sink = previousLog; DlssVideoPipeline.ReleaseGpuResources(); }
        double Median(IEnumerable<double> values) => values.Order().ElementAt(1);
        var summary = rows.Where(x => x.Trial != 0).GroupBy(x => x.Scenario).Select(group =>
        {
            var cpu = group.Where(x => !x.Gpu).ToArray(); var gpu = group.Where(x => x.Gpu).ToArray();
            double before = Median(cpu.Select(x => x.Fps)), after = Median(gpu.Select(x => x.Fps));
            return new { Scenario = group.Key, cpuFps = before, gpuFps = after, gainPercent = (after / before - 1) * 100,
                cpuMin = cpu.Min(x => x.Fps), cpuMax = cpu.Max(x => x.Fps), gpuMin = gpu.Min(x => x.Fps), gpuMax = gpu.Max(x => x.Fps),
                cpuWallSeconds = Median(cpu.Select(x => x.WallSeconds)), gpuWallSeconds = Median(gpu.Select(x => x.WallSeconds)) };
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(summary, json));
        Console.WriteLine("SUMMARY " + Path.Combine(root, "summary.json"));
    }
}

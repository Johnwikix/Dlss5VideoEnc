using Dlss5Demo.Core;

internal static class MixedBenchmark
{
    internal static async Task RunAsync(string input, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var rows = new List<object>();
        foreach (string name in new[] { "nr-host", "nr-cuda", "sr-host", "sr-cuda", "nr-sr-host", "nr-sr-cuda" })
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var options = new TranscodeOptions
            {
                HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
                LogPath = Path.Combine(outputRoot, "nr.log"), VsrLogPath = Path.Combine(outputRoot, "vsr.log"),
                EnableNr = name.StartsWith("nr"), SuperResolutionScale = name.Contains("sr") ? 2 : 1,
                EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife, InterpolationMultiplier = 4,
                UseGpuFramePipeline = name.EndsWith("cuda"), MaxFrames = 24, EnablePreviewFrames = false,
                Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None },
            };
            var wall = System.Diagnostics.Stopwatch.StartNew();
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(outputRoot, $"{name}-{iteration}.mp4"), options);
            wall.Stop();
            var video = MediaProbe.Probe(result.OutputPath);
            if (video.FrameCount != 96 || video.Width != 1920 * options.SuperResolutionScale || video.Height != 1080 * options.SuperResolutionScale)
                throw new Exception("Mixed benchmark output dimensions/count changed");
            rows.Add(new { name, iteration, seconds = result.Elapsed.TotalSeconds, wallSeconds = wall.Elapsed.TotalSeconds,
                outputFps = video.FrameCount / result.Elapsed.TotalSeconds, outputFrames = video.FrameCount, path = result.OutputPath });
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "measurements.json"), System.Text.Json.JsonSerializer.Serialize(rows,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"BENCH {name} {iteration}: {video.FrameCount / result.Elapsed.TotalSeconds:F3} output fps, wall={wall.Elapsed.TotalSeconds:F3}s");
        }
    }
}

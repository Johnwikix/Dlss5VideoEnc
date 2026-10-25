using Dlss5Demo.Core;
using System.Diagnostics;

internal static class GpuEnhancementTests
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    internal static async Task LegacyVsrAsync(string input, string oldVsrHost, string nrDll)
    {
        var previous = PipelineLog.Sink; var messages = new List<string>();
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "gpu-vsr-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PipelineLog.Sink = message => { lock (messages) messages.Add(message); };
        try
        {
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "fallback.mp4"), new TranscodeOptions
            {
                FfmpegPath = DemoPaths.FfmpegPath, HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = NrRuntimeCatalog.ResolveArgument(nrDll),
                LogPath = Path.Combine(root, "nr.log"), VsrHostDllPath = Path.GetFullPath(oldVsrHost), VsrLogPath = Path.Combine(root, "vsr.log"),
                SuperResolutionScale = 2, EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife,
                MaxFrames = 6, EnablePreviewFrames = false,
            }).WaitAsync(TimeSpan.FromMinutes(2));
            var output = MediaProbe.Probe(result.OutputPath);
            Require(result.FramesProcessed == 6 && output.FrameCount == 12, "Old VSR fallback lost frames.");
            Require(messages.Any(message => message.Contains("NR/SR 主机传输路径")) &&
                messages.Any(message => message.StartsWith("NR/SR 主机帧")), "Old VSR did not retry the host transport.");
            File.WriteAllLines(Path.Combine(root, "pipeline.log"), messages);
            Console.WriteLine("PASS VSR without GPU exports retries complete host transport; " + root);
        }
        finally { PipelineLog.Sink = previous; DlssVideoPipeline.ReleaseGpuResources(); }
    }

    internal static unsafe void Transport(string dll)
    {
        // 336*4 is not a D3D12 row-pitch multiple: catches padding/stride mistakes.
        const int width = 336, height = 180;
        byte[] input = new byte[width * height * 4];
        new Random(139).NextBytes(input);
        for (int i = 3; i < input.Length; i += 4) input[i] = 255;
        using var cuda = new CudaVideoContext(0);
        using var source = cuda.Allocate(width, height);
        cuda.UploadRgba(input, source.Frame);
        foreach (int scale in new[] { 2, 4 })
        {
            using var worker = RtxVideoWorker.Create(DemoPaths.VsrHostDllPath, DemoPaths.VsrRuntimeDllPath,
                DemoPaths.VsrLogPath, width, height, scale, 4, false, 1);
            using var output = cuda.Allocate(width * scale, height * scale);
            var expected = new byte[input.Length * scale * scale];
            fixed (byte* data = input) Buffer.MemoryCopy(data, worker.Source(0), input.Length, input.Length);
            worker.ProcessFrame(0, false);
            new ReadOnlySpan<byte>(worker.Output(0), expected.Length).CopyTo(expected);
            using var gpu = new GpuVsrProcessor(cuda, worker, width, height, scale);
            for (int trial = 0; trial < 4; trial++)
            {
                gpu.Process(source.Frame, output.Frame);
                byte[] actual = new byte[expected.Length]; cuda.Download(output.Frame, actual);
                for (int i = 0; i < actual.Length; i += 4) (actual[i], actual[i + 2]) = (actual[i + 2], actual[i]);
                Require(expected.AsSpan().SequenceEqual(actual), "VSR shared GPU pixels/order/pitch differ from host transport.");
            }
            Console.WriteLine($"PASS VSR {scale}x cross-process GPU transport matches every RGBA byte, including repeated slot reuse");
        }

        foreach (bool history in new[] { false, true })
        {
            var frames = new List<byte[]>();
            for (int pass = 0; pass < 2; pass++)
            {
                using var host = DlssNrHost.Create(DemoPaths.HostDllPath, dll, DemoPaths.LogPath, width, height,
                    new DlssNrOptions { TemporalStabilization = history }, false);
                using var nr = GpuNrProcessor.TryCreate(host, width, height, false, 24)
                    ?? throw new Exception("GPU NR unavailable");
                using var destination = cuda.Allocate(width, height);
                for (int frame = 0; frame < 8; frame++)
                {
                    var shifted = new byte[input.Length];
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                        input.AsSpan((y * width + (x + frame) % width) * 4, 4).CopyTo(shifted.AsSpan((y * width + x) * 4, 4));
                    var actual = new byte[input.Length];
                    if (pass == 0)
                    {
                        fixed (byte* a = shifted, b = actual) nr.Process(a, b);
                        frames.Add(actual);
                    }
                    else
                    {
                        cuda.UploadRgba(shifted, source.Frame);
                        nr.ProcessDevice(source.Frame, destination.Frame); cuda.Download(destination.Frame, actual);
                        for (int i = 0; i < actual.Length; i += 4) (actual[i], actual[i + 2]) = (actual[i + 2], actual[i]);
                        Require(frames[frame].AsSpan().SequenceEqual(actual), $"NR GPU handoff changed pixels/history at {frame}.");
                    }
                }
            }
            Console.WriteLine($"PASS NR GPU input/output matches host transport byte-for-byte, history={history}");
        }
    }

    internal static async Task RunAsync(string input, string dll)
    {
        dll = NrRuntimeCatalog.ResolveArgument(dll);
        Transport(dll);
        var root = Path.Combine(Environment.CurrentDirectory, ".qa", "gpu-enhancement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = MediaProbe.Probe(input);
        var previous = PipelineLog.Sink;
        var lines = new List<string>();
        PipelineLog.Sink = line => { lock (lines) lines.Add(line); };
        TranscodeOptions Options() => new()
        {
            FfmpegPath = DemoPaths.FfmpegPath, HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = dll, LogPath = DemoPaths.LogPath,
            EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife, MaxFrames = 6,
            EnablePreviewFrames = false, Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.Copy },
        };
        try
        {
            await Run("NR-RIFE2", Options());
            await Run("NR-SR2-RIFE4", Options() with { SuperResolutionScale = 2, InterpolationMultiplier = 4 });
            await Run("SR2-NR-RIFE2", Options() with { SuperResolutionScale = 2, NrBeforeSuperResolution = false });
            await Run("SR4-RIFE6", Options() with { EnableNr = false, SuperResolutionScale = 4, InterpolationMultiplier = 6 });
            await Run("NR-no-history", Options() with { Dlss = new DlssNrOptions { TemporalStabilization = false, FineMotion = true } });
            await Run("NR-SR2-RIFE-Full", Options() with { SuperResolutionScale = 2, InterpolationModel = RifeModel.Full });
            await Run("disabled-transport", Options() with { UseGpuEnhancementPipeline = false }, false);
            await Run("CPU-temporal", Options() with { Dlss = new DlssNrOptions { UseGpuTemporalProcessing = false } }, false);
            await Run("software-decode", Options() with { DecodeMode = VideoDecodeMode.Software, SuperResolutionScale = 2 }, false);
            string unsupported = Path.Combine(root, "ffv1.mkv");
            var start = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-i", input, "-an", "-frames:v", "6", "-c:v", "ffv1", unsupported }) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
                Require(process.ExitCode == 0, await error);
            }
            await Run("NVDEC-retry-with-enhancement", Options() with { SuperResolutionScale = 2 }, false, unsupported);
            using (var preview = new TranscodePreviewBuffer(80, 60))
            {
                await Run("preview", Options() with { SuperResolutionScale = 2, EnablePreviewFrames = true, PreviewSink = preview });
                using var frame = preview.TakeLatest();
                Require(frame != null && frame.Width <= 80 && frame.Height <= 60, "GPU preview ignored requested size.");
                Require(!frame!.Source.SequenceEqual(frame.Processed), "Enhanced GPU preview lost source/result comparison.");
                Console.WriteLine("PASS reduced preview retains separate source and enhanced pixels");
            }
            using var stop = new CancellationTokenSource();
            try
            {
                await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options() with { SuperResolutionScale = 2 },
                    new CancelProgress(stop), stop.Token).WaitAsync(TimeSpan.FromSeconds(45));
                throw new Exception("GPU enhancement ignored cancellation.");
            }
            catch (OperationCanceledException) { Console.WriteLine("PASS GPU enhancement cancellation releases CUDA imports and VSR worker"); }
            await Run("restart", Options() with { SuperResolutionScale = 2 });
        }
        finally { PipelineLog.Sink = previous; DlssVideoPipeline.ReleaseGpuResources(); }
        Console.WriteLine("GPU enhancement artifacts: " + root);

        async Task Run(string name, TranscodeOptions options, bool gpu = true, string? runInput = null)
        {
            lock (lines) lines.Clear();
            var runSource = runInput == null ? source : MediaProbe.Probe(runInput);
            var result = await DlssVideoPipeline.RunAsync(runInput ?? input, Path.Combine(root, name + ".mp4"), options).WaitAsync(TimeSpan.FromMinutes(2));
            var output = MediaProbe.Probe(result.OutputPath);
            Require(result.FramesProcessed == 6 && output.FrameCount == 6 * options.InterpolationMultiplier, name + " frame count");
            Require(output.Width == source.Width * options.SuperResolutionScale && output.Height == source.Height * options.SuperResolutionScale, name + " dimensions");
            Require(Math.Abs(output.Fps - source.Fps * options.InterpolationMultiplier) < .001, name + " fps");
            Require(output.HasAudio == runSource.HasAudio, name + " audio");
            lock (lines)
            {
                Require(lines.Any(line => line.StartsWith("NVDEC 显存帧已确认")) == gpu, name + " silently selected wrong transport");
                File.WriteAllLines(Path.Combine(root, name + ".log"), lines);
            }
            Console.WriteLine("PASS GPU enhancement pipeline: " + name);
        }
    }

    private sealed class CancelProgress(CancellationTokenSource stop) : IProgress<TranscodeProgress>
    {
        public void Report(TranscodeProgress value) { if (value.FramesDone > 0) stop.Cancel(); }
    }
}

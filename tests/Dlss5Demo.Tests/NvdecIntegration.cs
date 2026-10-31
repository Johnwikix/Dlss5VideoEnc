using System.Diagnostics;
using Dlss5Demo.Core;

internal static class NvdecIntegration
{
    internal static async Task BenchmarkAsync(string input)
    {
        var video = MediaProbe.Probe(input);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(video.Width * video.Height * (video.IsHdr ? 8 : 4)));
        var results = new List<object>();
        async Task Measure(VideoDecodeMode mode, int limit, bool warmup)
        {
            var options = new TranscodeOptions
            {
                EnableNr = false, DecodeMode = mode, MaxFrames = limit,
            };
            var watch = Stopwatch.StartNew();
            // 进程内解码不再有子进程；采样当前进程 CPU 前后差作为解码开销。
            var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            await using var decoder = new DlssVideoPipeline.FrameDecoder(input, options, video, video.IsHdr, default);
            var frames = 0;
            while (decoder.ReadFrame(pixels)) frames++;
            watch.Stop();
            var cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds;
            if (frames != Math.Min(limit, video.FrameCount > 0 ? video.FrameCount : limit))
                throw new Exception("Decode benchmark did not read the expected source frames");
            if (!warmup)
            {
                var result = new { mode = mode.ToString(), frames, seconds = watch.Elapsed.TotalSeconds,
                    decoderCpuSeconds = cpuSeconds, fps = frames / watch.Elapsed.TotalSeconds };
                results.Add(result);
                Console.WriteLine("BENCH " + System.Text.Json.JsonSerializer.Serialize(result));
            }
        }
        await Measure(VideoDecodeMode.Software, 30, true);
        await Measure(VideoDecodeMode.Nvdec, 30, true);
        for (var trial = 0; trial < 3; trial++)
        {
            await Measure(trial == 1 ? VideoDecodeMode.Nvdec : VideoDecodeMode.Software, 600, false);
            await Measure(trial == 1 ? VideoDecodeMode.Software : VideoDecodeMode.Nvdec, 600, false);
        }
        Console.WriteLine("BENCH_RESULTS " + System.Text.Json.JsonSerializer.Serialize(results));
    }

    internal static async Task RunAsync(string input)
    {
        var video = MediaProbe.Probe(input);
        var hdr = video.IsHdr;
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var saved = PipelineLog.Sink;
        PipelineLog.Sink = logs.Enqueue;
        var settings = new TranscodeOptions { EnableNr = false, MaxFrames = 3 };
        var frameBytes = checked(video.Width * video.Height * (hdr ? 8 : 4));
        var softwarePixels = GC.AllocateUninitializedArray<byte>(frameBytes);
        var hardwarePixels = GC.AllocateUninitializedArray<byte>(frameBytes);
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            double error = 0;
            long samples = 0, frames = 0;
            await using (var software = new DlssVideoPipeline.FrameDecoder(input, settings with { DecodeMode = VideoDecodeMode.Software }, video, hdr, default))
            await using (var hardware = new DlssVideoPipeline.FrameDecoder(input, settings with { DecodeMode = VideoDecodeMode.Nvdec }, video, hdr, default))
            {
                while (true)
                {
                    var cpu = software.ReadFrame(softwarePixels);
                    var gpu = hardware.ReadFrame(hardwarePixels);
                    Require(cpu == gpu, "NVDEC changed decoded source frame count");
                    if (!cpu) break;
                    frames++;
                    if (hdr)
                    {
                        for (var index = 0; index < frameBytes; index += 2)
                        {
                            var difference = BitConverter.ToUInt16(softwarePixels, index) - BitConverter.ToUInt16(hardwarePixels, index);
                            error += (double)difference * difference;
                        }
                        samples += frameBytes / 2;
                    }
                    else
                    {
                        for (var index = 0; index < frameBytes; index++)
                        {
                            var difference = softwarePixels[index] - hardwarePixels[index];
                            error += difference * difference;
                        }
                        samples += frameBytes;
                    }
                }
                Require(logs.Any(line => line.StartsWith("NVDEC 已确认")) && !logs.Any(line => line.StartsWith("NVDEC 首帧初始化失败")), "Did not exercise actual mandatory CUDA download");
            }
            var peak = hdr ? 65535d : 255d;
            var psnr = error == 0 ? double.PositiveInfinity : 10 * Math.Log10(peak * peak * samples / error);
            Require(frames > 0 && psnr >= 50, $"NVDEC pixel parity too low: {psnr:F3} dB");
            Console.WriteLine($"PASS actual NVDEC pixel parity: {video.Codec} {video.Width}x{video.Height} {video.PixelFormat}, hdr={hdr}, {frames} frames, PSNR={psnr:F3} dB");

            // A deliberately missing CUDA device verifies real initialization
            // fallback without changing the working system/driver installation.
            logs.Clear();
            await using (var fallback = new DlssVideoPipeline.FrameDecoder(input,
                settings with { DecodeDeviceIndex = 9999, MaxFrames = 1 }, video, hdr, default))
            {
                Require(fallback.ReadFrame(hardwarePixels) && !fallback.ReadFrame(hardwarePixels), "Real fallback lost the single source frame");
                Require(logs.Any(line => line.StartsWith("NVDEC 首帧初始化失败")) && !logs.Any(line => line.StartsWith("NVDEC 已确认")), "Real initialization fallback was not reported");
            }
            Console.WriteLine("PASS actual NVDEC unavailable-device initialization falls back to software");

            var root = Path.Combine(Environment.CurrentDirectory, ".qa", "nvdec", "integration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            async Task Run(string name, TranscodeOptions options)
            {
                var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, name + ".mp4"), options);
                var output = MediaProbe.Probe(result.OutputPath);
                Require(result.FramesProcessed == 3, name + ": lost source frames");
                Require(output.FrameCount == 3 * (options.EnableInterpolation ? options.InterpolationMultiplier : 1), name + ": wrong output count");
                Require(output.IsHdr == hdr && (!hdr || output.ColorTransfer == video.ColorTransfer && output.PixelFormat.Contains("10")), name + ": HDR changed");
                Require(output.HasAudio == video.HasAudio, name + ": audio lost");
                Require(output.Width == video.Width * options.SuperResolutionScale && output.Height == video.Height * options.SuperResolutionScale, name + ": wrong size");
                Console.WriteLine($"PASS NVDEC pipeline {name}: {output.Width}x{output.Height}, {output.FrameCount} frames, {output.PixelFormat}, {output.ColorTransfer}");
            }
            var options = settings with
            {
                DecodeMode = VideoDecodeMode.Nvdec, MaxFrames = 3,
                HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
                LogPath = Path.Combine(root, "nr.log"), VsrLogPath = Path.Combine(root, "vsr.log"), EnablePreviewFrames = false,
            };
            await Run("only-decode", options);
            await Run("interpolation", options with { EnableInterpolation = true });
            if (video.Width <= 640 && video.Height <= 360)
            {
                await Run("nr-sr-interpolation", options with { EnableNr = true, SuperResolutionScale = 2, EnableInterpolation = true });
                await Run("sr-gpu-encode", options with { SuperResolutionScale = 2 });
            }
            Console.WriteLine("NVDEC artifacts: " + root);
        }
        finally { PipelineLog.Sink = saved; }
    }
}

using Dlss5Demo.Core;
using Microsoft.ML.OnnxRuntime;

internal static class RifeIntegration
{
    internal static async Task PixelsAsync()
    {
        foreach (var size in new[] { (Width: 80, Height: 72), (Width: 32, Height: 48) })
        foreach (var choice in new[] { RifeModel.Lite, RifeModel.Full })
        {
            int width = size.Width, height = size.Height, bytes = width * height * 4;
            var options = new TranscodeOptions { InterpolationMultiplier = 4, InterpolationModel = choice };
            using var model = await RifeGpuSession.CreateAsync(options, width, height, false, default);
            byte[] first = new byte[bytes], second = new byte[bytes];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                first[i] = (byte)(60 + x % 80); first[i + 1] = (byte)(120 + y % 80); first[i + 2] = (byte)(20 + (x + y) % 40); first[i + 3] = 255;
                int shift = (x + 3) % width;
                second[i] = (byte)(60 + shift % 80); second[i + 1] = first[i + 1]; second[i + 2] = (byte)(20 + (shift + y) % 40); second[i + 3] = 255;
            }
            model.SetFrame(first, true); model.SetFrame(second, false);
            int iw = width <= 64 ? 128 : width, ih = height <= 64 ? 128 : height, plane = iw * ih;
            float[] input = new float[7 * plane], output = new float[3 * plane];
            Pack(first, 0); Pack(second, 3 * plane);
            var path = Path.Combine(DemoPaths.RuntimeDir, choice == RifeModel.Lite ? "rife_v4.25_lite_v2.onnx" : "rife_v4.25_v2.onnx");
            // 独立运行原模型作为像素适配器的参照，测试才使用 CPU，生产路径禁止 CPU 回退。
            using var reference = new InferenceSession(path);
            using var inputTensor = OrtValue.CreateTensorValueFromMemory(input, [1, 7, ih, iw]);
            using var outputTensor = OrtValue.CreateTensorValueFromMemory(output, [1, 3, ih, iw]);
            using var firstGpu = model.Cuda.Allocate(width, height);
            using var secondGpu = model.Cuda.Allocate(width, height);
            using var outputGpu = model.Cuda.Allocate(width, height);
            CudaPixelTests.Upload(model.Cuda, firstGpu.Frame, CudaPixelTests.Bgra(first));
            CudaPixelTests.Upload(model.Cuda, secondGpu.Frame, CudaPixelTests.Bgra(second));
            using var run = new RunOptions();
            foreach (float amount in new[] { .25f, .5f, .75f })
            {
                input.AsSpan(6 * plane).Fill(amount);
                reference.Run(run, [reference.InputNames.Single()], [inputTensor], [reference.OutputNames.Single()], [outputTensor]);
                model.Run(amount, default);
                double error = 0; int maxError = 0;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                {
                    var value = output[c * plane + y * iw + x];
                    int expected = !float.IsFinite(value) ? 0 : (int)(Math.Clamp(value, 0, 1) * 255 + .5f);
                    int difference = model.Output[y * iw * 4 + x * 4 + c] - expected;
                    error += difference * difference;
                    maxError = Math.Max(maxError, Math.Abs(difference));
                }
                model.RunCuda(firstGpu.Frame, secondGpu.Frame, outputGpu.Frame, amount, default);
                byte[] gpu = CudaPixelTests.Bgra(CudaPixelTests.Download(model.Cuda, outputGpu.Frame));
                double cudaError = 0;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int difference = gpu[(y * width + x) * 4 + c] - model.Output[(y * iw + x) * 4 + c];
                    cudaError += difference * difference;
                }
                double cudaPsnr = cudaError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255d * 255 * width * height * 3 / cudaError);
                Require(cudaPsnr >= 40, "CUDA RIFE pixel adapter changed: " + cudaPsnr);
                Console.WriteLine($"PASS CUDA RIFE vs GPU host adapter: {choice} {width}x{height} t={amount}, PSNR={cudaPsnr:F2}");
                double psnr = error == 0 ? double.PositiveInfinity : 10 * Math.Log10(255d * 255 * width * height * 3 / error);
                Require(psnr >= 40, $"RGBA/timestep/padding changed: {choice} {width}x{height} t={amount}, PSNR={psnr}, max={maxError}");
                Console.WriteLine($"PASS RIFE GPU pixels vs original CPU graph: {choice} {width}x{height} t={amount}, PSNR={psnr:F2}, max={maxError}");
            }
            using var memory = new MemoryStream();
            using var stream = new RifeFrameStream(memory, model, 4, default);
            stream.Write(first); stream.Write(second); stream.Complete(); stream.Complete();
            var result = memory.ToArray();
            Require(result.Length == bytes * 8, "RIFE frame-pair/tail count changed");
            Require(first.AsSpan().SequenceEqual(result.AsSpan(0, bytes)), "RIFE changed first original");
            Require(second.AsSpan().SequenceEqual(result.AsSpan(bytes * 4, bytes)), "RIFE changed second original or its timestamp");
            for (int i = 5; i < 8; i++) Require(second.AsSpan().SequenceEqual(result.AsSpan(bytes * i, bytes)), "RIFE tail changed");
            Require(!first.AsSpan().SequenceEqual(result.AsSpan(bytes, bytes)) && !second.AsSpan().SequenceEqual(result.AsSpan(bytes, bytes)), "RIFE repeated instead of synthesizing");
            Console.WriteLine($"PASS RIFE original identity, generated pixels, tail and idempotent EOF: {choice}, {width}x{height}");
            void Pack(byte[] pixels, int offset)
            {
                for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                for (int c = 0; c < 3; c++)
                    input[offset + c * plane + y * iw + x] = pixels[(Math.Min(y, height - 1) * width + Math.Min(x, width - 1)) * 4 + c] / 255f;
            }
        }
    }
    internal static async Task BenchmarkAsync(string input, string outputRoot, bool rifeOnly = false)
    {
        Directory.CreateDirectory(outputRoot);
        var measurements = new List<object>();
        var video = MediaProbe.Probe(input, DemoPaths.FfprobePath);
        foreach (var name in (rifeOnly ? new[] { "rife-lite-host", "rife-lite-cuda", "rife-full-host", "rife-full-cuda" } : new[] { "native-optimal", "native-linear", "rife-lite-host", "rife-lite-cuda", "rife-full-host", "rife-full-cuda" }))
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var options = new TranscodeOptions
            {
                FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false, EnableInterpolation = true,
                UseLinearVulkanImages = name != "native-optimal", UseGpuFramePipeline = name.EndsWith("cuda"), InterpolationMultiplier = 4,
                InterpolationBackend = name.StartsWith("rife") ? FrameInterpolationBackend.Rife : FrameInterpolationBackend.NvidiaOpticalFlow,
                InterpolationModel = name.Contains("full") ? RifeModel.Full : RifeModel.Lite,
                EnablePreviewFrames = false, MaxFrames = 72,
                VideoEncoder = "hevc_nvenc", Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None }
            };
            var wall = System.Diagnostics.Stopwatch.StartNew();
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(outputRoot, $"{name}-{iteration}.mp4"), options);
            wall.Stop();
            var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
            Require(output.FrameCount == 288 && Math.Abs(output.Fps - video.Fps * 4) < .00001, "Benchmark output frame clock failed");
            measurements.Add(new { name, iteration, sourceFrames = result.FramesProcessed,
                outputFrames = output.FrameCount, seconds = result.Elapsed.TotalSeconds, wallSeconds = wall.Elapsed.TotalSeconds,
                outputFps = output.FrameCount / result.Elapsed.TotalSeconds, path = result.OutputPath });
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "measurements.json"), System.Text.Json.JsonSerializer.Serialize(measurements,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"BENCH {name} {iteration}: {output.FrameCount / result.Elapsed.TotalSeconds:F3} output fps, wall={wall.Elapsed.TotalSeconds:F3}s");
        }
    }
    private static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
}

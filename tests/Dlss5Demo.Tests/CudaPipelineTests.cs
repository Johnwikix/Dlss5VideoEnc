using System.Diagnostics;
using Dlss5Demo.Core;

internal static class CudaPipelineTests
{
    internal static async Task StaticFrameReuseAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "cuda-repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string input = Path.Combine(root, "black-white.mp4");
        await Command(["-v", "error", "-f", "lavfi", "-i", "color=black:s=320x180:r=24:d=0.25",
            "-f", "lavfi", "-i", "color=white:s=320x180:r=24:d=0.25", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", input]);
        foreach (var decoder in new[] { VideoDecodeMode.Auto, VideoDecodeMode.Software })
        {
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, decoder + ".mp4"), new TranscodeOptions
            {
                FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false, EnableInterpolation = true,
                InterpolationBackend = FrameInterpolationBackend.Rife, InterpolationMultiplier = 6,
                DecodeMode = decoder, EnablePreviewFrames = false, Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None },
            });
            var pixels = await Command(["-v", "error", "-i", result.OutputPath, "-vf", "scale=1:1:flags=area,format=gray", "-f", "rawvideo", "pipe:1"]);
            Require(pixels.Length == 72, "NV12 reuse changed static/cut/tail frame count.");
            for (int i = 0; i < pixels.Length; i++)
                Require(i < 36 ? pixels[i] < 10 : pixels[i] > 245, "NV12 reuse changed a frame or scene-cut timestamp.");
            Console.WriteLine($"PASS immutable NV12 reuse, scene-cut timestamp and tail pixels: {decoder}");
        }

        static async Task<byte[]> Command(string[] args)
        {
            var start = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var output = new MemoryStream();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.BaseStream.CopyToAsync(output);
            await process.WaitForExitAsync();
            Require(process.ExitCode == 0, await error);
            return output.ToArray();
        }
    }

    internal static async Task RunAsync(string input)
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "interpolation-port-2026-10-17", "cuda-cases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = MediaProbe.Probe(input, DemoPaths.FfprobePath);
        var lines = new List<string>(); var previous = PipelineLog.Sink;
        PipelineLog.Sink = line => { lock (lines) lines.Add(line); };
        TranscodeOptions Options() => new() { FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false,
            EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife, InterpolationMultiplier = 4,
            MaxFrames = 6, EnablePreviewFrames = false, Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.Copy } };
        try
        {
            foreach (var codec in new[] { (Name: "h264_nvenc", Type: VideoCodec.H264), (Name: "av1_nvenc", Type: VideoCodec.Av1) })
                await Run(codec.Name, input, Options() with { VideoEncoder = codec.Name, Video = new VideoTranscodeOptions { VideoCodec = codec.Type, AudioCodec = AudioCodec.Copy } }, true);
            await Run("same-size-output", input, Options() with { Video = new VideoTranscodeOptions
                { ScaleMode = ScaleMode.Exact, ScaleWidth = original.Width, ScaleHeight = original.Height } }, true);
            await Run("resized-output", input, Options() with { Video = new VideoTranscodeOptions
                { ScaleMode = ScaleMode.Exact, ScaleWidth = 160, ScaleHeight = 90 } }, false);
            lock (lines) Require(lines.Any(l => l.Contains("复用已校验")), "Changing output filters rebuilt an identical RIFE network.");
            Console.WriteLine("PASS same-size output retains CUDA; real resize reuses the same RIFE network");
            using (var preview = new TranscodePreviewBuffer(64, 64))
            {
                await Run("preview", input, Options() with { PreviewSink = preview, EnablePreviewFrames = true }, true);
                using var frame = preview.TakeLatest();
                Require(frame != null && frame.Width <= 64 && frame.Height <= 64, "CUDA preview did not use size hint");
                Require(frame!.Source.SequenceEqual(frame.Processed), "RIFE source previews disagree");
                Console.WriteLine("PASS CUDA reduced GPU preview and source/result identity");
            }
            string unknownAudio = Path.Combine(root, "unknown-audio.mkv");
            var copy = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-i", input, "-map", "0", "-c", "copy", unknownAudio }) copy.ArgumentList.Add(argument);
            using (var process = Process.Start(copy)!) { var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Require(process.ExitCode == 0, await error); }
            Require(MediaProbe.Probe(unknownAudio, DemoPaths.FfprobePath).FrameCount == 0, "Fixture unexpectedly exposes frame count");
            var unknownResult = await DlssVideoPipeline.RunAsync(unknownAudio, Path.Combine(root, "unknown-audio.mp4"), Options() with { MaxFrames = null });
            var unknownOutput = MediaProbe.Probe(unknownResult.OutputPath, DemoPaths.FfprobePath);
            Require(unknownOutput.FrameCount == unknownResult.FramesProcessed * 4 && unknownOutput.HasAudio, "Unknown source count lost video/audio");
            Require(Math.Abs(unknownOutput.DurationSeconds - unknownResult.FramesProcessed / original.Fps) < .003, "Unknown source count changed duration");
            Console.WriteLine("PASS CUDA unknown source frame count retains bounded audio and complete video");
            await Run("software-decoder", input, Options() with { DecodeMode = VideoDecodeMode.Software }, false);
            string fixture = Path.Combine(root, "ffv1.mkv");
            var start = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-i", input, "-map", "0:v:0", "-frames:v", "6", "-vf", "format=yuv420p", "-c:v", "ffv1", fixture }) start.ArgumentList.Add(argument);
            using (var process = Process.Start(start)!)
            {
                var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Require(process.ExitCode == 0, await error);
            }
            await Run("first-frame-fallback", fixture, Options(), false);
            lock (lines) Require(lines.Any(l => l.Contains("主机帧路径")) && !lines.Any(l => l.StartsWith("NVDEC 显存帧已确认")), "Failed NVDEC claimed GPU pixels or did not retry");
            Console.WriteLine("PASS native NVDEC failure retries complete host path with lazy GPU adapters");
            using var stop = new CancellationTokenSource();
            try
            {
                await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options(), new CancelProgress(stop), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
                throw new Exception("CUDA pipeline ignored cancellation");
            }
            catch (OperationCanceledException) { Console.WriteLine("PASS native CUDA cancellation joins workers and releases retained frames"); }
            await Run("restart", input, Options(), true);
            Console.WriteLine("CUDA artifacts: " + root);
        }
        finally { PipelineLog.Sink = previous; }

        async Task Run(string name, string source, TranscodeOptions options, bool cuda)
        {
            lock (lines) lines.Clear();
            var result = await DlssVideoPipeline.RunAsync(source, Path.Combine(root, name + ".mp4"), options).WaitAsync(TimeSpan.FromMinutes(2));
            var output = MediaProbe.Probe(result.OutputPath, DemoPaths.FfprobePath);
            Require(output.FrameCount == 24 && Math.Abs(output.Fps - original.Fps * 4) < .00001, name + ": frame count/rate changed");
            Require(Math.Abs(output.DurationSeconds - 6 / original.Fps) < .003, name + ": duration changed");
            Require(output.HasAudio == MediaProbe.Probe(source, DemoPaths.FfprobePath).HasAudio, name + ": audio changed");
            if (cuda) lock (lines) Require(lines.Any(l => l.StartsWith("NVDEC 显存帧已确认")), name + ": did not use GPU frames");
            Console.WriteLine("PASS CUDA real pipeline: " + name);
        }
    }
    private sealed class CancelProgress(CancellationTokenSource stop) : IProgress<TranscodeProgress>
    { public void Report(TranscodeProgress value) => stop.Cancel(); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

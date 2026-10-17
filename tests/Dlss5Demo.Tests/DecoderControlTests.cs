using System.Diagnostics;
using Dlss5Demo.Core;

internal static class DecoderControlTests
{
    // Actual child processes exercise EOF, stderr drains and process replacement,
    // but deliberately never initialize a GPU while another chat benchmarks it.
    internal static bool TryServe(string[] args)
    {
        var behavior = Environment.GetEnvironmentVariable("DLSS5_TEST_DECODER");
        if (behavior == null || args.FirstOrDefault() != "-hide_banner") return false;
        var hardware = args.Contains("-hwaccel");
        if (hardware && behavior == "block")
        {
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        using var output = Console.OpenStandardOutput();
        if (hardware)
        {
            if (behavior == "partial") output.Write(new byte[7]);
            if (behavior == "late") output.Write(Enumerable.Repeat((byte)7, 32).ToArray());
            output.Flush();
            Console.Error.WriteLine("CONTROL_TEST: hardware negotiation failed");
            Environment.ExitCode = 65;
            return true;
        }
        output.Write(Enumerable.Repeat((byte)1, 32).ToArray());
        output.Write(Enumerable.Repeat((byte)2, 32).ToArray());
        return true;
    }

    internal static async Task RunAsync()
    {
        var savedBehavior = Environment.GetEnvironmentVariable("DLSS5_TEST_DECODER");
        var savedLog = PipelineLog.Sink;
        var logs = new List<string>();
        PipelineLog.Sink = logs.Add;
        var video = new VideoInfo { Codec = "h264", Width = 4, Height = 2,
            PixelFormat = "yuv420p", FrameRate = "24/1", FrameCount = 2 };
        var options = new TranscodeOptions
        {
            FfmpegPath = Path.Combine(AppContext.BaseDirectory, "Dlss5Demo.Tests.exe"),
            EnableNr = false,
        };
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            foreach (var behavior in new[] { "fail", "partial" })
            {
                Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", behavior);
                logs.Clear();
                await using var decoder = new DlssVideoPipeline.FrameDecoder("fixture", options, video, false, default);
                var originalProcess = decoder.Process;
                var originalId = originalProcess.Id;
                var pixels = new byte[32];
                Require(decoder.ReadFrame(pixels) && pixels.All(value => value == 1), "Fallback leaked partial GPU pixels");
                Require(decoder.Process.Id != originalId, "Fallback did not replace the failed process");
                Require(decoder.ReadFrame(pixels) && pixels.All(value => value == 2) && !decoder.ReadFrame(pixels), "Fallback lost/duplicated source frames");
                Require(logs.Count(line => line.StartsWith("NVDEC 首帧初始化失败")) == 1
                    && !logs.Any(line => line.StartsWith("NVDEC 已确认")), "Wrong fallback backend reporting");
                Console.WriteLine($"PASS decoder process fallback before first frame: {behavior}");
            }
            Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", "fail");
            await using (var decoder = new DlssVideoPipeline.FrameDecoder("fixture", options with { DecodeMode = VideoDecodeMode.Nvdec }, video, false, default))
            {
                try { decoder.ReadFrame(new byte[32]); throw new Exception("Forced NVDEC silently fell back"); }
                catch (InvalidOperationException error) { Require(error.Message.Contains("CONTROL_TEST"), "Lost hardware decoder error"); }
            }
            Console.WriteLine("PASS decoder forced NVDEC preserves failure");

            Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", "late");
            logs.Clear();
            await using (var decoder = new DlssVideoPipeline.FrameDecoder("fixture", options, video, false, default))
            {
                Require(decoder.ReadFrame(new byte[32]), "Missing hardware first frame");
                try { decoder.ReadFrame(new byte[32]); throw new Exception("Midstream failure silently restarted history"); }
                catch (InvalidOperationException error) { Require(error.Message.Contains("已输出 1 帧"), "Lost midstream frame position"); }
                Require(!logs.Any(line => line.StartsWith("NVDEC 首帧初始化失败")), "Midstream history was retried");
            }
            Console.WriteLine("PASS decoder never switches backends after publishing a frame");

            Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", "block");
            using (var stop = new CancellationTokenSource())
            await using (var decoder = new DlssVideoPipeline.FrameDecoder("fixture", options, video, false, stop.Token))
            {
                var reading = Task.Run(() => decoder.ReadFrame(new byte[32]));
                stop.CancelAfter(150);
                try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Blocked decoder ignored cancellation"); }
                catch (OperationCanceledException) { }
                Require(decoder.Process.HasExited, "Cancelled child process survived");
            }
            Console.WriteLine("PASS decoder cancellation terminates a blocked frame read");

            Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", "fail");
            using (var stop = new CancellationTokenSource())
            {
                PipelineLog.Sink = line => { if (line.StartsWith("NVDEC 首帧初始化失败")) stop.Cancel(); };
                await using var decoder = new DlssVideoPipeline.FrameDecoder("fixture", options, video, false, stop.Token);
                try { decoder.ReadFrame(new byte[32]); throw new Exception("Cancelled fallback started another decoder"); }
                catch (OperationCanceledException) { }
            }
            Console.WriteLine("PASS cancellation at fallback boundary starts no replacement process");
        }
        finally
        {
            PipelineLog.Sink = savedLog;
            Environment.SetEnvironmentVariable("DLSS5_TEST_DECODER", savedBehavior);
        }
    }
}

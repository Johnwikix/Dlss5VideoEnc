using System.Diagnostics;
using Dlss5Demo.Core;

internal static class DecoderControlTests
{
    // 进程内解码器没有子进程替身可注入失败；用真实夹具 + 不存在的 CUDA 设备号
    // 触发 NVDEC 打开失败，验证回退与取消语义。夹具由 PATH 上的 ffmpeg 生成。
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dlss5-decoder-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, "fixture.mp4");
        if (Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x192:rate=24",
                "-frames:v", "10", "-pix_fmt", "yuv420p", "-c:v", "libx264", fixture]) != 0)
            throw new Exception("无法用 PATH 上的 ffmpeg 生成夹具");
        var savedLog = PipelineLog.Sink;
        var logs = new List<string>();
        PipelineLog.Sink = logs.Add;
        var video = MediaProbe.Probe(fixture);
        var pixels = new byte[video.Width * video.Height * 4];
        const int missingDevice = 99;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            // Auto 模式：NVDEC 打开失败必须从头回退软件解码，不丢帧、不泄漏半帧。
            logs.Clear();
            await using (var decoder = new DlssVideoPipeline.FrameDecoder(fixture,
                new TranscodeOptions { EnableNr = false, DecodeDeviceIndex = missingDevice }, video, false, default))
            {
                var frames = 0;
                while (decoder.ReadFrame(pixels)) frames++;
                Require(frames == 10, $"回退软解丢帧：{frames}");
            }
            Require(logs.Any(line => line.StartsWith("NVDEC 初始化失败")), "回退没有记录原因");
            Require(logs.Any(line => line.StartsWith("软件解码已确认")), "回退后没有软件解码确认日志");
            Console.WriteLine("PASS decoder auto fallback replaces hardware with software");

            // 强制 NVDEC：失败必须以 InvalidOperationException 上抛，不能静默回退。
            // 进程内实现在构造时即创建 CUDA 设备，失败可能发生在构造或首次读取。
            DlssVideoPipeline.FrameDecoder? forced = null;
            try
            {
                forced = new DlssVideoPipeline.FrameDecoder(fixture,
                    new TranscodeOptions { EnableNr = false, DecodeMode = VideoDecodeMode.Nvdec, DecodeDeviceIndex = missingDevice },
                    video, false, default);
                forced.ReadFrame(pixels);
                throw new Exception("Forced NVDEC silently fell back");
            }
            catch (InvalidOperationException) { }
            finally { if (forced != null) await forced.DisposeAsync(); }
            Console.WriteLine("PASS decoder forced NVDEC preserves failure");

            // 软件解码：正常路径逐帧到 EOF。
            await using (var decoder = new DlssVideoPipeline.FrameDecoder(fixture,
                new TranscodeOptions { EnableNr = false, DecodeMode = VideoDecodeMode.Software }, video, false, default))
            {
                var frames = 0;
                while (decoder.ReadFrame(pixels)) frames++;
                Require(frames == 10, $"软件解码帧数不符：{frames}");
            }
            Console.WriteLine("PASS decoder software reads the whole fixture");

            // 帧数上限：读到限制即停，不触碰其余输入。
            await using (var decoder = new DlssVideoPipeline.FrameDecoder(fixture,
                new TranscodeOptions { EnableNr = false, MaxFrames = 3 }, video, false, default))
            {
                var frames = 0;
                while (decoder.ReadFrame(pixels)) frames++;
                Require(frames == 3, $"帧数上限失效：{frames}");
            }
            Console.WriteLine("PASS decoder stops at the frame limit");

            // 已取消的令牌：构造或首次读取立即中止，不再解码。
            using (var stopped = new CancellationTokenSource())
            {
                stopped.Cancel();
                DlssVideoPipeline.FrameDecoder? cancelled = null;
                try
                {
                    cancelled = new DlssVideoPipeline.FrameDecoder(fixture,
                        new TranscodeOptions { EnableNr = false }, video, false, stopped.Token);
                    cancelled.ReadFrame(pixels);
                    throw new Exception("Cancelled decoder kept reading");
                }
                catch (OperationCanceledException) { }
                finally { if (cancelled != null) await cancelled.DisposeAsync(); }
            }
            Console.WriteLine("PASS decoder honours a cancelled token");

            await Task.CompletedTask;
        }
        finally
        {
            PipelineLog.Sink = savedLog;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static int Run(string name, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = name,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new Exception($"无法启动 {name}");
        process.WaitForExit(15000);
        return process.HasExited ? process.ExitCode : -1;
    }
}

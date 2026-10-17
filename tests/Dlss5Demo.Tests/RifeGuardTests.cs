using Dlss5Demo.Core;

internal static class RifeGuardTests
{
    internal static async Task RunAsync(string input, string hdrInput)
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "interpolation-port-2026-10-17", "guards");
        Directory.CreateDirectory(root);
        TranscodeOptions Options() => new() { FfmpegPath = DemoPaths.FfmpegPath, EnableNr = false,
            EnableInterpolation = true, InterpolationBackend = FrameInterpolationBackend.Rife, EnablePreviewFrames = false, MaxFrames = 6 };
        await Reject<ArgumentOutOfRangeException>("invalid-backend", input, Options() with { InterpolationBackend = (FrameInterpolationBackend)999 });
        await Reject<ArgumentOutOfRangeException>("invalid-model", input, Options() with { InterpolationModel = (RifeModel)999 });
        await Reject<ArgumentOutOfRangeException>("invalid-multiplier", input, Options() with { InterpolationMultiplier = 3 });
        await Reject<NotSupportedException>("hdr", hdrInput, Options());
        await Reject<NotSupportedException>("hdr-no-bypass", hdrInput, Options() with { Video = new VideoTranscodeOptions { PreserveHdr = false } });
        await Reject<NotSupportedException>("forced-hdr", input, Options() with { Video = new VideoTranscodeOptions { ForceHdr = true, PreserveHdr = true } });
        var options = Options();
        using var model = await RifeGpuSession.CreateAsync(options, 1920, 1080, false, default);
        byte[] first = new byte[1920 * 1080 * 4], second = new byte[first.Length];
        Array.Fill(first, (byte)80); Array.Fill(second, (byte)120);
        model.SetFrame(first, true); model.SetFrame(second, false);
        using (var stop = new CancellationTokenSource())
        {
            stop.CancelAfter(TimeSpan.FromMilliseconds(1));
            try { await Task.Run(() => { while (true) model.Run(.5f, stop.Token); }).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { Console.WriteLine("PASS RIFE timed GPU cancellation drains bindings"); }
        }
        model.Run(.5f, default);
        Console.WriteLine("PASS RIFE GPU inference restarts after cancellation");
        async Task Reject<T>(string name, string source, TranscodeOptions settings) where T : Exception
        {
            string output = Path.Combine(root, name + ".mp4");
            try { await DlssVideoPipeline.RunAsync(source, output, settings); throw new Exception("Guard accepted " + name); }
            catch (T) { if (File.Exists(output)) throw new Exception("Invalid settings reserved output: " + name); }
            Console.WriteLine("PASS RIFE guard rejects before output reservation: " + name);
        }
    }
}

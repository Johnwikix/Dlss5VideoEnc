using Dlss5Demo.Core;
using System.Numerics;
using System.Diagnostics;

if (RtxVideoWorker.TryServe(args)) return;
if (DecoderControlTests.TryServe(args)) return;

var tests = new (string Name, Action Run)[]
{
    ("NR versions are discovered recursively and keep stable IDs on refresh", NrRuntimeCatalogTests.Discovery),
    ("Identity output scaling keeps GPU paths without changing real resize/crop requests", OutputScaleTests.Identity),
    ("Driver HDR maps SDR only; native HDR and RIFE bypass mapping", DriverHdrIntegration.Policy),
    ("NR controls reject model-extrapolating and nonfinite strengths", StrengthBoundaries),
    ("Output names skip existing files and directories", OutputNames),
    ("Rejected motion cannot poison NR or correction history", ReliableMotion),
    ("Stable input suppresses alternating NR correction", StableCorrection),
    ("Changing source details remain immediate", SourceDetails),
    ("Local movement rejects mismatched history", LocalMovement),
    ("Scene cut resets history before NR", SceneCut),
    ("Temporal duration follows source FPS", SourceFrameRate),
    ("HDR retains signed correction and alpha", HdrCorrection),
    ("Nonfinite HDR history cannot contaminate valid pixels", NonfiniteHdr),
    ("Parallel row path matches sequential behavior", ParallelRows),
    ("Translated correction follows matching input motion", MotionCorrection),
    ("Invalid motion and disocclusion reject history", InvalidMotion),
    ("Bounded stages preserve frame order and buffer ownership", PipelineOrder),
    ("Stage failures preserve the original error and stop peers", PipelineFailure),
    ("Cancellation joins all frame stages", PipelineCancellation),
    ("Input preparation can run ahead without changing temporal output", PreparedHistory),
    ("Integer optical-flow luma retains exact ties-to-even rounding", GrayRounding),
    ("Small HDR previews match full conversion then nearest resizing", HdrPreviewSampling),
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} regression checks passed.");
var nrVersions = Array.IndexOf(args, "--nr-versions");
if (nrVersions >= 0) NrRuntimeCatalogTests.Switch(args[nrVersions + 1]);
var nrVersionBenchmark = Array.IndexOf(args, "--nr-version-benchmark");
if (nrVersionBenchmark >= 0) await NrVersionBenchmark.RunAsync(args[nrVersionBenchmark + 1], args[nrVersionBenchmark + 2], args[nrVersionBenchmark + 3], args[nrVersionBenchmark + 4]);
if (args.Contains("--driver-hdr")) DriverHdrIntegration.RunAsync().GetAwaiter().GetResult();
if (args.Contains("--gpu"))
{
    NativeOpticalFlow();
    NativeFineMotion();
    NativeFormatSwitch();
    Console.WriteLine("PASS Native HDR to SDR session format switch");
    NativeHistorySwitch();
    NativeQueueParity();
}
var benchmarkArgument = Array.IndexOf(args, "--nr-benchmark");
if (benchmarkArgument >= 0) NativeNrBenchmark(args[benchmarkArgument + 1]);
var flickerArgument = Array.IndexOf(args, "--gpu-flicker");
if (flickerArgument >= 0)
    NativeFlicker(args[flickerArgument + 1]);
var comparisonArgument = Array.IndexOf(args, "--compare-video");
if (comparisonArgument >= 0)
    CompareVideo(args[comparisonArgument + 1], args[comparisonArgument + 2], args[comparisonArgument + 3]);
var pipelineArgument = Array.IndexOf(args, "--pipeline");
if (pipelineArgument >= 0)
    PipelineIntegration(args[pipelineArgument + 1]).GetAwaiter().GetResult();
var transportArgument = Array.IndexOf(args, "--vsr-transport");
if (transportArgument >= 0) VsrTransportIntegration(args[transportArgument + 1]).GetAwaiter().GetResult();
var nrSwitchArgument = Array.IndexOf(args, "--nr-switch");
if (nrSwitchArgument >= 0) NrSwitchIntegration(args[nrSwitchArgument + 1]).GetAwaiter().GetResult();
var interpolationArgument = Array.IndexOf(args, "--interpolation");
if (interpolationArgument >= 0) InterpolationIntegration.RunAsync(args[interpolationArgument + 1]).GetAwaiter().GetResult();
var rifeGuards = Array.IndexOf(args, "--rife-guards");
if (rifeGuards >= 0) RifeGuardTests.RunAsync(args[rifeGuards + 1], args[rifeGuards + 2]).GetAwaiter().GetResult();
var cudaPipeline = Array.IndexOf(args, "--cuda-pipeline");
if (cudaPipeline >= 0) CudaPipelineTests.RunAsync(args[cudaPipeline + 1]).GetAwaiter().GetResult();
var mixedBenchmark = Array.IndexOf(args, "--mixed-benchmark");
if (mixedBenchmark >= 0) MixedBenchmark.RunAsync(args[mixedBenchmark + 1], args[mixedBenchmark + 2]).GetAwaiter().GetResult();
var combinationBenchmark = Array.IndexOf(args, "--combination-benchmark");
if (combinationBenchmark >= 0) CombinationBenchmark.RunAsync(args[combinationBenchmark + 1], args[combinationBenchmark + 2]).GetAwaiter().GetResult();
var rifeArgument = Array.IndexOf(args, "--rife");
if (rifeArgument >= 0) InterpolationIntegration.RunAsync(args[rifeArgument + 1], FrameInterpolationBackend.Rife).GetAwaiter().GetResult();
if (args.Contains("--cuda-pixels")) CudaPixelTests.Scenes();
if (args.Contains("--gpu-temporal")) GpuTemporalTests.Kernels();
var gpuTemporalBenchmark = Array.IndexOf(args, "--gpu-temporal-benchmark");
if (gpuTemporalBenchmark >= 0) await GpuTemporalBenchmark.RunAsync(args[gpuTemporalBenchmark + 1], args[gpuTemporalBenchmark + 2], args[gpuTemporalBenchmark + 3]);
if (args.Contains("--cuda-repeats")) CudaPipelineTests.StaticFrameReuseAsync().GetAwaiter().GetResult();
if (args.Contains("--rife-pixels")) RifeIntegration.PixelsAsync().GetAwaiter().GetResult();
var rifeBenchmark = Array.IndexOf(args, "--rife-benchmark");
if (rifeBenchmark >= 0) RifeIntegration.BenchmarkAsync(args[rifeBenchmark + 1], args[rifeBenchmark + 2], args.Contains("--benchmark-rife-only")).GetAwaiter().GetResult();
if (args.Contains("--interpolation-pixels")) InterpolationIntegration.PixelsAsync().GetAwaiter().GetResult();
if (args.Contains("--decoder-control")) DecoderControlTests.RunAsync().GetAwaiter().GetResult();
var nvdecArgument = Array.IndexOf(args, "--nvdec");
if (nvdecArgument >= 0) NvdecIntegration.RunAsync(args[nvdecArgument + 1]).GetAwaiter().GetResult();
var nvdecBenchmarkArgument = Array.IndexOf(args, "--nvdec-benchmark");
if (nvdecBenchmarkArgument >= 0) NvdecIntegration.BenchmarkAsync(args[nvdecBenchmarkArgument + 1]).GetAwaiter().GetResult();

DlssVideoPipeline.ReleaseGpuResources();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task NrSwitchIntegration(string input)
{
    var root = Path.Combine(Environment.CurrentDirectory, ".qa", "nr-switch", "integration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var source = MediaProbe.Probe(input);
    var sourceFrames = source.FrameCount;
    if (sourceFrames == 0)
    {
        var count = new ProcessStartInfo(DemoPaths.FfprobePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", input })
            count.ArgumentList.Add(argument);
        using var counter = Process.Start(count)!;
        var value = counter.StandardOutput.ReadToEnd().Trim();
        counter.WaitForExit();
        Check(counter.ExitCode == 0 && long.TryParse(value, out sourceFrames), "Cannot count source fixture frames.");
    }
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previous = PipelineLog.Sink;
    PipelineLog.Sink = logs.Enqueue;
    TranscodeOptions Options(string name, int scale = 2) => new()
    {
        EnableNr = false,
        // Deliberately absent: disabling NR must remove its native dependencies.
        HostDllPath = Path.Combine(root, "missing-nr-host.dll"),
        RuntimeDllPath = Path.Combine(root, "missing-nr-runtime.dll"),
        LogPath = Path.Combine(root, name + "-nr.log"), VsrLogPath = Path.Combine(root, name + "-sr.log"),
        SuperResolutionScale = scale, MaxFrames = 12,
        Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.Copy },
    };
    async Task Run(string name, TranscodeOptions options, bool gpu)
    {
        logs.Clear();
        using var preview = new TranscodePreviewBuffer(160, 90);
        var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, name + ".mp4"), options with
            { PreviewSink = preview, PreviewFramesPerSecond = 1 }).WaitAsync(TimeSpan.FromSeconds(60));
        var encoded = MediaProbe.Probe(result.OutputPath);
        var expectedFrames = options.MaxFrames ?? sourceFrames;
        Check(encoded.Width == source.Width * options.SuperResolutionScale && encoded.Height == source.Height * options.SuperResolutionScale,
            name + ": wrong dimensions with NR disabled.");
        Check(encoded.FrameCount == expectedFrames && result.FramesProcessed == expectedFrames, name + ": lost tail frames.");
        Check(encoded.IsHdr == source.IsHdr && Math.Abs(encoded.Fps - source.Fps) < .0001, name + ": HDR/FPS changed.");
        Check(encoded.HasAudio == source.HasAudio, name + ": audio was lost.");
        Check(!File.Exists(options.LogPath), name + ": NR still initialized.");
        Check(logs.Any(line => line.StartsWith("DLSS NR 已关闭")) && !logs.Any(line => line.StartsWith("NGX NR 会话已创建")),
            name + ": NR bypass was not honored.");
        Check(logs.Any(line => line.StartsWith("VSR GPU 编码已启用")) == gpu, name + ": wrong GPU encoding path.");
        using var last = preview.TakeLatest();
        Check(last?.FramesDone == expectedFrames, name + ": final preview was lost.");
        var pixels = last!.Processed.ToArray();
        Check(pixels.Where((_, i) => i % 4 != 3).Any(value => value > 16), name + ": black/uninitialized output.");
        if (options.SuperResolutionScale == 1)
            Check(last.Source.SequenceEqual(last.Processed), "NR and SR off must preserve the decoded preview pixels.");
        Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(module =>
            module.ModuleName is "dlssnr_host_v2.dll" or "nvngx_dlssnr.dll"), name + ": NR DLL was loaded despite missing NR paths.");
        Console.WriteLine($"PASS NR off {name}: {encoded.Width}×{encoded.Height}, {encoded.FrameCount} frames, hdr={encoded.IsHdr}, GPU encode={gpu}");
    }
    try
    {
        await Run("both-off", Options("both-off", 1), false);
        await Run("sr2-gpu", Options("sr2-gpu"), !source.IsHdr);
        await Run("sr2-cpu", Options("sr2-cpu") with { UseGpuVsrEncoding = false }, false);
        await Run("sr4", Options("sr4", 4), !source.IsHdr);
        await Run("nr-order-ignored", Options("nr-order-ignored") with { NrBeforeSuperResolution = false }, !source.IsHdr);
        await Run("one-frame", Options("one-frame") with { MaxFrames = 1 }, !source.IsHdr);
        await Run("full-length", Options("full-length") with { MaxFrames = null }, !source.IsHdr);
        if (!source.IsHdr)
            await Run("two-pass", Options("two-pass") with
            {
                VideoEncoder = "libx264",
                Video = new VideoTranscodeOptions { VideoCodec = VideoCodec.H264, HardwareBackend = HardwareBackend.Software,
                    RateControl = RateControlMode.Bitrate, TwoPassEnabled = true, VideoBitrateKbps = 1800 },
            }, false);
        using (var stop = new CancellationTokenSource())
        {
            try
            {
                await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options("cancel"),
                    new InlineProgress(_ => stop.Cancel()), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
                throw new Exception("NR-off pipeline ignored cancellation.");
            }
            catch (OperationCanceledException) { }
        }
        await Run("restart", Options("restart"), !source.IsHdr);
        try
        {
            await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "nr-on-missing.mp4"), Options("nr-on-missing", 1) with { EnableNr = true });
            throw new Exception("NR enabled ignored its absent runtime.");
        }
        catch (FileNotFoundException) { }
        Console.WriteLine("PASS NR switch: absent NR dependencies, cancellation/restart, enabled dependency check");
    }
    finally { PipelineLog.Sink = previous; }
}

static void StrengthBoundaries()
{
    var high = DlssNrHost.NormalizeOptions(new DlssNrOptions { Intensity = 20, LocalTone = 20, LocalStructure = 20, SkinStructure = 20 });
    Check(high.Intensity == 5 && high.LocalTone == 5 && high.LocalStructure == 5 && high.SkinStructure == 5,
        "Legacy 20× strengths can still reach the NR model.");
    var invalid = DlssNrHost.NormalizeOptions(new DlssNrOptions { Intensity = float.NaN, LocalTone = float.PositiveInfinity,
        LocalStructure = float.NegativeInfinity, SkinStructure = float.NaN });
    Check(invalid.Intensity == 1 && invalid.LocalTone == 1 && invalid.LocalStructure == 1 && invalid.SkinStructure == .5f,
        "Nonfinite strengths were not replaced by defaults.");
    var low = DlssNrHost.NormalizeOptions(new DlssNrOptions { Intensity = -1, LocalTone = -1, LocalStructure = -1, SkinStructure = -1 });
    Check(low.Intensity == 0 && low.LocalTone == 0 && low.LocalStructure == 0 && low.SkinStructure == 0, "Negative strengths escaped normalization.");
    var normal = new DlssNrOptions { Intensity = .35f, LocalTone = 1.25f, LocalStructure = 2, SkinStructure = .5f };
    Check(DlssNrHost.NormalizeOptions(normal) == normal, "Valid strengths changed.");
}

static void OutputNames()
{
    var root = Path.Combine(Path.GetTempPath(), "dlss5-names-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var input = Path.Combine(root, "input.mp4");
        var output = Path.Combine(root, "input_dlss5.mp4");
        File.WriteAllText(input, "source");
        File.WriteAllText(output, "do not overwrite");
        File.WriteAllText(Path.Combine(root, "input_dlss5_1.mp4"), "existing result");
        Directory.CreateDirectory(Path.Combine(root, "input_dlss5_2.mp4"));
        Check(OutputFileNames.ForInput(input, ".mp4") == Path.Combine(root, "input_dlss5_3.mp4"), "Duplicate suffix did not advance.");
        Check(OutputFileNames.Available(input) == Path.Combine(root, "input_1.mp4"), "Input video could become its own output.");
        Check(File.ReadAllText(output) == "do not overwrite" && File.ReadAllText(input) == "source", "Name generation modified a file.");
        Check(OutputFileNames.ForInput(Path.Combine(root, "other.mkv"), ".gif") == Path.Combine(root, "other_dlss5.gif"), "Input/container changes did not change the suggestion.");
        var reservations = Enumerable.Range(0, 8).Select(_ => Task.Run(() => OutputFileNames.Reserve(output))).ToArray();
        Task.WaitAll(reservations);
        try
        {
            Check(reservations.Select(task => task.Result.Path).Distinct().Count() == 8, "Concurrent writers claimed the same output name.");
            Check(File.ReadAllText(output) == "do not overwrite", "A reservation truncated an existing file.");
        }
        finally { foreach (var task in reservations) task.Result.Dispose(); }
    }
    finally
    {
        // This fixture owns only these flat files and one empty child directory.
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(Path.Combine(root, "input_dlss5_2.mp4"));
        Directory.Delete(root);
    }
}

static void ReliableMotion()
{
    var motion = new float[] { 2, 1, 100, 100, float.NaN, float.PositiveInfinity, 3, 0 };
    var rejected = new[] { false, true, false, false };
    var errors = new[] { .01f, .01f, .01f, .2f };
    MotionReliability.Apply(motion, rejected, errors, true);
    Check(motion[0] == 2 && motion[1] == 1 && errors[0] == .01f, "Reliable displacement changed.");
    for (var pixel = 1; pixel < 4; pixel++)
        Check(rejected[pixel] && motion[pixel * 2] == 0 && motion[pixel * 2 + 1] == 0 && float.IsPositiveInfinity(errors[pixel]),
            "An unreliable vector reached NR or remained eligible for history.");
    var initial = new[] { 1f, 0f };
    MotionReliability.Apply(initial, [false], [1f], false);
    Check(initial[0] == 1, "Reset frame was tested against an absent color history.");
    var withoutHistory = new[] { 5f, 6f };
    MotionReliability.Apply(withoutHistory, [true], null, false);
    Check(withoutHistory.All(value => value == 0), "Confidence rejection depended on the extra temporal filter.");
}

static void GrayRounding()
{
    for (var sum = 0; sum <= 255 * 256; sum++)
        Check(NvidiaOpticalFlow.ToGray(sum) == (byte)MathF.Round(sum / 256f), $"Luma rounding changed for weighted sum {sum}.");
}

static unsafe void HdrPreviewSampling()
{
    const int width = 32, height = 18, targetWidth = 13, targetHeight = 7;
    var source = new Half[width * height * 4];
    for (var index = 0; index < source.Length; index++) source[index] = (Half)((index % 173) / 172f);
    var full = new byte[width * height * 4];
    var small = new byte[targetWidth * targetHeight * 4];
    fixed (Half* input = source)
    {
        DlssVideoPipeline.FillHdrPreview((ushort*)input, width, height, full, width, height);
        DlssVideoPipeline.FillHdrPreview((ushort*)input, width, height, small, targetWidth, targetHeight);
    }
    for (var y = 0; y < targetHeight; y++)
        for (var x = 0; x < targetWidth; x++)
        {
            var pixel = (y * height / targetHeight * width + x * width / targetWidth) * 4;
            Check(small.AsSpan((y * targetWidth + x) * 4, 4).SequenceEqual(full.AsSpan(pixel, 4)), "HDR preview sampling changed the displayed colors.");
        }
}

static async Task PipelineIntegration(string input)
{
    var source = MediaProbe.Probe(input);
    var root = Path.Combine(Environment.CurrentDirectory, ".qa", "performance", "integration");
    Directory.CreateDirectory(root);
    var cases = new[]
    {
        (Name: "both", Scale: 1, Motion: true, History: true, NrFirst: true),
        (Name: "history", Scale: 1, Motion: false, History: true, NrFirst: true),
        (Name: "motion", Scale: 1, Motion: true, History: false, NrFirst: true),
        (Name: "none", Scale: 1, Motion: false, History: false, NrFirst: true),
        (Name: "sr2", Scale: 2, Motion: true, History: true, NrFirst: true),
        (Name: "sr4", Scale: 4, Motion: true, History: true, NrFirst: true),
        (Name: "sr2-first", Scale: 2, Motion: true, History: true, NrFirst: false),
        (Name: "sr4-first", Scale: 4, Motion: true, History: true, NrFirst: false),
    };
    TranscodeOptions Options(string name, int scale, ITranscodePreviewSink? preview = null) => new()
    {
        HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
        LogPath = Path.Combine(root, name + "-nr.log"), VsrLogPath = Path.Combine(root, name + "-sr.log"),
        SuperResolutionScale = scale, MaxFrames = 12, CopyAudio = false, PreviewSink = preview,
        Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None },
    };
    foreach (var item in cases)
    {
        using var preview = new TranscodePreviewBuffer(160, 90);
        var output = Path.Combine(root, item.Name + (source.IsHdr ? "-hdr" : "") + ".mp4");
        var options = Options(item.Name, item.Scale, preview) with
        {
            NrBeforeSuperResolution = item.NrFirst,
            Dlss = new DlssNrOptions { MotionGuidance = item.Motion, ContinuousHistory = item.History, TemporalStabilization = item.History },
        };
        var result = await DlssVideoPipeline.RunAsync(input, output, options).WaitAsync(TimeSpan.FromSeconds(60));
        var encoded = MediaProbe.Probe(result.OutputPath);
        Check(result.FramesProcessed == 12 && encoded.FrameCount == 12, $"Wrong output frame count: {item.Name}.");
        Check(encoded.Width == source.Width * item.Scale && encoded.Height == source.Height * item.Scale, "SR output dimensions changed.");
        Check(encoded.IsHdr == source.IsHdr, "HDR metadata was not preserved.");
        using var last = preview.TakeLatest();
        Check(last != null && last.FramesDone == 12 && last.Width <= 160 && last.Height <= 90, "Final source/output preview pair was lost.");
        Console.WriteLine($"PASS Pipeline {item.Name}: {encoded.Width}×{encoded.Height}, 12 frames, hdr={encoded.IsHdr}");
    }
    using (var preview = new TranscodePreviewBuffer(160, 90))
    {
        var output = Path.Combine(root, "one-frame.mp4");
        var result = await DlssVideoPipeline.RunAsync(input, output, Options("one-frame", 2, preview) with { MaxFrames = 1 })
            .WaitAsync(TimeSpan.FromSeconds(30));
        using var last = preview.TakeLatest();
        Check(result.FramesProcessed == 1 && MediaProbe.Probe(result.OutputPath).FrameCount == 1 && last?.FramesDone == 1,
            "A partial NR submission queue did not flush its final frame.");
        Console.WriteLine("PASS Pipeline flushes a single frame from the NR queue");
    }
    using (var cancel = new CancellationTokenSource())
    {
        var progress = new InlineProgress(_ => cancel.Cancel());
        try
        {
            await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options("cancel", 2), progress, cancel.Token)
                .WaitAsync(TimeSpan.FromSeconds(30));
            throw new Exception("SR pipeline ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }
    // A new SR session must start after all canceled workers and native state have been reclaimed.
    var restarted = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "restart.mp4"), Options("restart", 2))
        .WaitAsync(TimeSpan.FromSeconds(30));
    Check(restarted.FramesProcessed == 12, "SR session failed after cancellation.");
    Console.WriteLine("PASS Pipeline cancellation and SR restart");
    if (!source.IsHdr)
    {
        var options = Options("two-pass", 2) with
        {
            VideoEncoder = "libx264",
            Video = new VideoTranscodeOptions
            {
                VideoCodec = VideoCodec.H264, HardwareBackend = HardwareBackend.Software,
                RateControl = RateControlMode.Bitrate, VideoBitrateKbps = 1500, TwoPassEnabled = true, AudioCodec = AudioCodec.None,
            },
        };
        var output = Path.Combine(root, "two-pass.mp4");
        var result = await DlssVideoPipeline.RunAsync(input, output, options).WaitAsync(TimeSpan.FromSeconds(60));
        Check(result.FramesProcessed == 12 && MediaProbe.Probe(result.OutputPath).FrameCount == 12, "Two-pass frame cache lost frames.");
        Console.WriteLine("PASS Pipeline SR + two-pass encoding");
    }
    var protectedOutput = Path.Combine(root, "protected.mp4");
    File.WriteAllText(protectedOutput, "never overwrite this file");
    var renamed = await DlssVideoPipeline.RunAsync(input, protectedOutput, Options("no-overwrite", 1) with { MaxFrames = 1 })
        .WaitAsync(TimeSpan.FromSeconds(30));
    Check(File.ReadAllText(protectedOutput) == "never overwrite this file" && renamed.OutputPath != protectedOutput
        && MediaProbe.Probe(renamed.OutputPath).FrameCount == 1, "Existing output was overwritten or the renamed video was lost.");
    Console.WriteLine("PASS Pipeline suffixes existing outputs without modifying them");
    var racedOutput = Path.Combine(root, "raced-" + Guid.NewGuid().ToString("N") + ".mp4");
    // RunAsync reserves the destination before asynchronous SR initialization.
    // A second process must be unable to claim that name or replace its file.
    var racing = DlssVideoPipeline.RunAsync(input, racedOutput, Options("race", 2) with { MaxFrames = 1 });
    var claimed = false;
    try
    {
        using var competitor = File.Open(racedOutput, FileMode.CreateNew, FileAccess.Write);
        claimed = true;
    }
    catch (IOException) { }
    var racedResult = await racing.WaitAsync(TimeSpan.FromSeconds(30));
    Check(!claimed && racedResult.OutputPath == racedOutput && MediaProbe.Probe(racedResult.OutputPath).FrameCount == 1,
        "Output reservation did not protect the name through encoder startup.");
    Console.WriteLine("PASS Pipeline atomically reserves output names through encoder startup");
    var invalidOutput = Path.Combine(root, "encoder-failure.mp4");
    try
    {
        await DlssVideoPipeline.RunAsync(input, invalidOutput, Options("encoder-failure", 2) with { VideoEncoder = "missing_encoder_for_regression" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        throw new Exception("Failed encoder was not reported.");
    }
    catch (Exception error) when (error is IOException or InvalidOperationException) { }
    Console.WriteLine("PASS Pipeline encoder failure cleans up all stages");
}

static void PipelineOrder()
{
    var buffers = Enumerable.Range(0, 3).Select(_ => new TestFrame()).ToArray();
    var output = new List<long>();
    var aborted = 0;
    var frames = BoundedFramePipeline.Run(buffers,
        (frame, index) => { frame.Value = index; return index < 120; },
        [ (frame, index) => { Thread.SpinWait(10000); Check(frame.Value == index, "Input slot reused during processing."); frame.Value += 1000; },
          (frame, index) => { Thread.Yield(); Check(frame.Value == index + 1000, "Stage metadata/frame pairing changed."); } ],
        (frame, index) => { if (index % 7 == 0) Thread.Sleep(1); output.Add(frame.Value); },
        () => Interlocked.Increment(ref aborted), CancellationToken.None);
    Check(frames == 120 && output.SequenceEqual(Enumerable.Range(0, 120).Select(index => index + 1000L)), "Dropped/reordered output frames.");
    Check(aborted == 0, "Normal EOF aborted workers.");
}

static void PipelineFailure()
{
    var buffers = Enumerable.Range(0, 3).Select(_ => new TestFrame()).ToArray();
    var original = new IOException("injected stage failure");
    var aborted = 0;
    var task = Task.Run(() => BoundedFramePipeline.Run(buffers, (_, index) => index < 10000,
        [ (_, index) => { if (index == 5) throw original; } ], (_, _) => Thread.Sleep(1),
        () => Interlocked.Increment(ref aborted), CancellationToken.None));
    try { task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); throw new Exception("Stage error was swallowed."); }
    catch (IOException error) { Check(ReferenceEquals(error, original), "Cleanup masked the original failure."); }
    Check(aborted == 1, "Failure did not unblock the other stages exactly once.");
}

static void PipelineCancellation()
{
    using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    var buffers = Enumerable.Range(0, 3).Select(_ => new TestFrame()).ToArray();
    var aborted = 0;
    var task = Task.Run(() => BoundedFramePipeline.Run(buffers, (_, _) => true,
        [ (_, _) => Thread.Sleep(2) ], (_, _) => Thread.Sleep(2),
        () => Interlocked.Increment(ref aborted), stop.Token));
    try { task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored."); }
    catch (OperationCanceledException) { }
    Check(aborted == 1, "Cancellation did not unblock stage IO.");
}

static unsafe void PreparedHistory()
{
    const int width = 32, height = 24, count = 30;
    var reference = new DlssNrTemporalStabilizer(width, height, false, 60, true);
    var staged = new DlssNrTemporalStabilizer(width, height, false, 60, true);
    var inputs = new byte[count][];
    var outputs = new byte[count][];
    var expected = new byte[count][];
    var motions = new float[count][];
    var errors = new float[count][];
    var resets = new bool[count];
    for (var frame = 0; frame < count; frame++)
    {
        inputs[frame] = Rgba(width, height, frame < 12 ? (byte)60 : (byte)180);
        for (var pixel = 0; pixel < width * height; pixel++)
            inputs[frame][pixel * 4] += (byte)((pixel % width - frame + 32) % 16);
        outputs[frame] = inputs[frame].ToArray();
        for (var pixel = 0; pixel < width * height; pixel++) outputs[frame][pixel * 4] += frame % 2 == 0 ? (byte)10 : (byte)0;
        expected[frame] = outputs[frame].ToArray();
        motions[frame] = new float[width * height * 2];
        for (var pixel = 0; pixel < width * height; pixel++) motions[frame][pixel * 2] = -1;
        errors[frame] = new float[width * height];
        fixed (byte* input = inputs[frame], result = expected[frame])
        {
            reference.PrepareFrame(input, motions[frame]);
            reference.Apply(input, result);
            // Prepare the entire input sequence before applying any staged output.
            resets[frame] = staged.PrepareInput(input, motions[frame], errors[frame]);
        }
    }
    for (var frame = 0; frame < count; frame++)
    {
        staged.SetPreparedFrame(motions[frame], errors[frame], resets[frame]);
        fixed (byte* input = inputs[frame], result = outputs[frame]) staged.Apply(input, result);
        Check(outputs[frame].SequenceEqual(expected[frame]), $"Prepared input changed frame {frame} history/cut behavior.");
    }
}

static byte[] Rgba(int width, int height, byte value, byte alpha = 255)
{
    var buffer = new byte[width * height * 4];
    for (var i = 0; i < buffer.Length; i += 4)
    {
        buffer[i] = buffer[i + 1] = buffer[i + 2] = value;
        buffer[i + 3] = alpha;
    }
    return buffer;
}

static unsafe bool Frame(DlssNrTemporalStabilizer filter, byte[] input, byte[] output)
{
    fixed (byte* source = input, destination = output)
    {
        var reset = filter.PrepareFrame(source);
        filter.Apply(source, destination);
        return reset;
    }
}

static void StableCorrection()
{
    var filter = new DlssNrTemporalStabilizer(16, 12, false, 60);
    var input = Rgba(16, 12, 100);
    var changes = new List<int>();
    var previous = 0;
    for (var frame = 0; frame < 120; frame++)
    {
        var output = Rgba(16, 12, frame % 2 == 0 ? (byte)132 : (byte)100, 173);
        Check(Frame(filter, input, output) == (frame == 0), "Static frames must not reset history.");
        if (frame > 20) changes.Add(Math.Abs(output[0] - previous));
        Check(output[3] == 173, "NR alpha changed.");
        previous = output[0];
    }
    Check(changes.Average() < 12, $"Alternating correction still flickers: {changes.Average()}.");
    Check(input[0] == 100, "The stabilized result must never modify NR input.");
}

static void SourceDetails()
{
    var filter = new DlssNrTemporalStabilizer(16, 12, false, 60);
    var input = Rgba(16, 12, 100);
    Frame(filter, input, Rgba(16, 12, 100));
    for (var frame = 1; frame < 20; frame++)
    {
        // Small real source changes are added immediately, not temporally blurred.
        input = Rgba(16, 12, (byte)(100 + frame));
        var output = input.ToArray();
        Frame(filter, input, output);
        Check(output.SequenceEqual(input), "Zero NR correction blurred source details.");
    }
}

static void LocalMovement()
{
    var filter = new DlssNrTemporalStabilizer(32, 24, false, 60);
    var input = Rgba(32, 24, 60);
    Frame(filter, input, Rgba(32, 24, 100));
    var pixel = (12 * 32 + 16) * 4;
    input[pixel] = input[pixel + 1] = input[pixel + 2] = 230;
    var output = Rgba(32, 24, 100);
    output[pixel] = output[pixel + 1] = output[pixel + 2] = 210;
    var raw = output.ToArray();
    Check(!Frame(filter, input, output), "A local moving object reset the whole scene.");
    Check(output.AsSpan(pixel, 4).SequenceEqual(raw.AsSpan(pixel, 4)), "Moving object retained stale correction.");
    var neighbor = pixel - 4;
    Check(output.AsSpan(neighbor, 4).SequenceEqual(raw.AsSpan(neighbor, 4)), "Changed edge leaked into its neighbor.");
}

static void SceneCut()
{
    var filter = new DlssNrTemporalStabilizer(32, 24, false, 60);
    Frame(filter, Rgba(32, 24, 40), Rgba(32, 24, 90));
    var input = Rgba(32, 24, 210);
    var output = Rgba(32, 24, 180, 91);
    Check(Frame(filter, input, output), "A cut was not reported before NR evaluation.");
    Check(output.SequenceEqual(Rgba(32, 24, 180, 91)), "Previous scene polluted the cut frame.");
    Check(!Frame(filter, input, Rgba(32, 24, 180)), "The following continuous frame reset again.");
}

static int Decay(double fps, int frames)
{
    var filter = new DlssNrTemporalStabilizer(8, 8, false, fps);
    var input = Rgba(8, 8, 100);
    Frame(filter, input, Rgba(8, 8, 180));
    byte[] output = [];
    for (var frame = 0; frame < frames; frame++)
    {
        output = input.ToArray();
        Frame(filter, input, output);
    }
    return output[0];
}

static void SourceFrameRate()
{
    Check(Math.Abs(Decay(30, 3) - Decay(60, 6)) <= 1, "Equal source durations produced different decay.");
    Check(Decay(2, 1) == 100, "A source interval longer than 250 ms retained stale history.");
}

static unsafe bool HalfFrame(DlssNrTemporalStabilizer filter, Half[] input, Half[] output)
{
    fixed (Half* source = input, destination = output)
    {
        var reset = filter.PrepareFrame((byte*)source);
        filter.Apply((byte*)source, (byte*)destination);
        return reset;
    }
}

static Half[] HalfRgba(float value, float alpha = 1) =>
    Enumerable.Range(0, 8 * 8 * 4).Select(i => (Half)(i % 4 == 3 ? alpha : value)).ToArray();

static void HdrCorrection()
{
    var filter = new DlssNrTemporalStabilizer(8, 8, true, 60);
    var input = HalfRgba(.6f);
    var darkened = HalfRgba(.3f, .75f);
    HalfFrame(filter, input, darkened);
    var raw = HalfRgba(.6f, .75f);
    HalfFrame(filter, input, raw);
    Check((float)raw[0] > .3f && (float)raw[0] < .5f, "Signed negative HDR correction was clipped/lost.");
    Check(raw[3] == (Half).75f, "HDR alpha changed.");
    Check(input[0] == (Half).6f, "HDR NR input was modified.");
}

static void NonfiniteHdr()
{
    var filter = new DlssNrTemporalStabilizer(8, 8, true, 60);
    var input = HalfRgba(.4f);
    var output = HalfRgba(.5f);
    output[0] = Half.NaN;
    HalfFrame(filter, input, output);
    Check(output[0] == input[0], "Invalid NR pixel did not fall back to the original.");
    output = HalfRgba(.55f);
    HalfFrame(filter, input, output);
    Check(output.All(Half.IsFinite), "Invalid HDR history contaminated another frame.");
    Check(output[0] == (Half).55f, "An invalid history observation was reused.");
}

static void ParallelRows()
{
    var filter = new DlssNrTemporalStabilizer(16, 256, false, 60);
    var input = Rgba(16, 256, 100);
    Frame(filter, input, Rgba(16, 256, 132));
    var output = Rgba(16, 256, 100);
    Frame(filter, input, output);
    Check(output.Where((_, i) => i % 4 != 3).Distinct().Count() == 1, "Parallel rows diverged.");
    Check(output[0] > 100 && output[0] < 132, "Parallel path did not accumulate history.");
}

static unsafe void MotionCorrection()
{
    const int width = 32, height = 24, shift = 4;
    var filter = new DlssNrTemporalStabilizer(width, height, false, 60, true);
    var previous = Rgba(width, height, 0);
    var previousOutput = previous.ToArray();
    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                previous[offset + channel] = (byte)(x * 4 + 20);
                previousOutput[offset + channel] = (byte)(x * 4 + 20 + (x < 16 ? 30 : 10));
            }
        }
    Frame(filter, previous, previousOutput);
    var current = Rgba(width, height, 0);
    for (var y = 0; y < height; y++)
        for (var x = shift; x < width; x++)
            previous.AsSpan((y * width + x - shift) * 4, 4).CopyTo(current.AsSpan((y * width + x) * 4, 4));
    var motion = new float[width * height * 2];
    for (var pixel = 0; pixel < width * height; pixel++) motion[pixel * 2] = -shift;
    var output = current.ToArray();
    fixed (byte* source = current, destination = output)
    {
        Check(!filter.PrepareFrame(source, motion), "A matching translation reset the scene.");
        filter.Apply(source, destination);
    }
    var sample = (12 * width + 18) * 4; // Previous x=14, with a 30/255 correction.
    Check(output[sample] - current[sample] >= 23, "The correction did not follow current-to-previous motion.");
    Check(output[0] == current[0], "Disoccluded edge retained out-of-bounds history.");
}

static unsafe void InvalidMotion()
{
    const int width = 16, height = 12;
    var filter = new DlssNrTemporalStabilizer(width, height, false, 60, true);
    var input = Rgba(width, height, 100);
    Frame(filter, input, Rgba(width, height, 150));
    var motion = new float[width * height * 2];
    motion[(6 * width + 8) * 2] = float.NaN;
    var output = Rgba(width, height, 120);
    fixed (byte* source = input, destination = output)
    {
        filter.PrepareFrame(source, motion);
        filter.Apply(source, destination);
    }
    Check(output[(6 * width + 8) * 4] == 120, "Nonfinite motion retained stale correction.");
    Check(output[(6 * width + 7) * 4] == 120, "Motion discontinuity leaked to a neighbor.");
}

static unsafe void NativeFormatSwitch()
{
    const int width = 512, height = 288;
    var logDirectory = Path.Combine(Environment.CurrentDirectory, ".qa", "temporal-tests");
    Directory.CreateDirectory(logDirectory);
    var hdrLog = Path.Combine(logDirectory, "format-hdr.log");
    using (var hdr = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
        hdrLog, width, height, new DlssNrOptions(), true))
    {
        var input = Enumerable.Repeat((Half).5f, width * height * 4).ToArray();
        var output = new Half[input.Length];
        fixed (Half* source = input, destination = output)
            Check(hdr.ProcessFrame((ushort*)source, (ushort*)destination, true), "Native HDR evaluation failed.");
        Check(output.All(Half.IsFinite), "Native HDR evaluation produced invalid output.");
    }
    Check(File.ReadAllText(hdrLog).Contains("format=RGBA16F profile=2 hdr=1"), "HDR format was not configured.");
    var sdrLog = Path.Combine(logDirectory, "format-sdr.log");
    using (var sdr = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
        sdrLog, width, height, new DlssNrOptions()))
    {
        var input = Rgba(width, height, 100);
        var output = new byte[input.Length];
        fixed (byte* source = input, destination = output)
            Check(sdr.ProcessFrame(source, destination, true), "Native SDR evaluation failed after HDR.");
        Check(output.Any(value => value > 0), "SDR evaluation returned an empty frame.");
    }
    Check(File.ReadAllText(sdrLog).Contains("format=RGBA8 profile=0 hdr=0"), "HDR format leaked into the SDR session.");
}

static unsafe void NativeHistorySwitch()
{
    const int width = 512, height = 288;
    var logDirectory = Path.Combine(Environment.CurrentDirectory, ".qa", "temporal-tests");
    Directory.CreateDirectory(logDirectory);
    var options = new DlssNrOptions { ContinuousHistory = false, TemporalStabilization = false };
    using var host = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
        Path.Combine(logDirectory, "native-history-switch.log"), width, height, options);
    var input = Rgba(width, height, 100);
    var other = Rgba(width, height, 220);
    var expected = new byte[input.Length];
    var actual = new byte[input.Length];
    fixed (byte* source = input, changed = other, reference = expected, destination = actual)
    {
        Check(host.ProcessFrame(source, reference, true), "Explicit-reset reference failed.");
        // A preceding different frame must not affect the next output when history is off.
        Check(host.ProcessFrame(changed, destination, false), "Intervening frame failed.");
        Check(host.ProcessFrame(source, destination, false), "History-disabled frame failed.");
        Check(actual.SequenceEqual(expected), "Disabling NR history did not isolate successive frames.");
        host.UpdateOptions(options with { ContinuousHistory = true });
        Check(host.ProcessFrame(changed, destination, false), "History re-enable failed.");
        host.UpdateOptions(options);
        Check(host.ProcessFrame(source, destination, false), "History disable after re-enable failed.");
        Check(actual.SequenceEqual(expected), "Re-enabling history prevented subsequent per-frame reset.");
    }
    Console.WriteLine("PASS Native continuous-history switch resets each frame and survives option updates");
}

static unsafe void NativeQueueParity()
{
    const int width = 512, height = 288, count = 12;
    var root = Path.Combine(Environment.CurrentDirectory, ".qa", "performance");
    Directory.CreateDirectory(root);
    foreach (var hdr in new[] { false, true })
        foreach (var history in new[] { false, true })
        {
            var inputs = new byte[count][];
            var expected = new byte[count][];
            var motion = new float[width * height * 2];
            for (var pixel = 0; pixel < width * height; pixel++) motion[pixel * 2] = -1;
            for (var index = 0; index < count; index++)
            {
                var rgba = Rgba(width, height, (byte)(40 + index * 15));
                inputs[index] = hdr ? new byte[rgba.Length * 2] : rgba;
                if (hdr)
                    fixed (byte* data = inputs[index])
                        for (var value = 0; value < rgba.Length; value++) ((Half*)data)[value] = (Half)(rgba[value] / 255f);
                expected[index] = new byte[inputs[index].Length];
            }
            var options = new DlssNrOptions { ContinuousHistory = history };
            using (var host = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
                Path.Combine(root, "queue-sync.log"), width, height, options, hdr, hdr ? "smpte2084" : ""))
            {
                host.ConfigureMotionGuidance(true);
                for (var index = 0; index < count; index++)
                    fixed (byte* color = inputs[index], output = expected[index])
                    fixed (float* vectors = motion)
                        Check(host.ProcessVideoFrame(color, vectors, output, index is 0 or 7), "Synchronous NR fixture failed.");
            }
            using (var host = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
                Path.Combine(root, "queue-async.log"), width, height, options, hdr, hdr ? "smpte2084" : ""))
            {
                host.ConfigureMotionGuidance(true);
                Check(host.QueueCapacity >= 2, "Native fixture must support two in-flight frames.");
                var output = new byte[inputs[0].Length];
                var pending = 0;
                var collected = 0;
                void Collect()
                {
                    fixed (byte* destination = output)
                        Check(host.DequeueVideoFrame(destination), "Queued NR readback failed.");
                    Check(output.AsSpan().SequenceEqual(expected[collected]), $"NR queue changed frame {collected}, hdr={hdr}, history={history}.");
                    pending--; collected++;
                }
                for (var index = 0; index < count; index++)
                {
                    fixed (byte* color = inputs[index])
                    fixed (float* vectors = motion)
                        Check(host.EnqueueVideoFrame(color, vectors, index is 0 or 7), "Queued NR submission failed.");
                    if (++pending == host.QueueCapacity) Collect();
                }
                while (pending > 0) Collect();
                Check(collected == count, "Final native queued frame was lost.");
            }
            Console.WriteLine($"PASS Native NR queue exact parity: hdr={hdr}, history={history}, reset midstream");
        }
}

static async Task VsrTransportIntegration(string input)
{
    var root = Path.Combine(Environment.CurrentDirectory, ".qa", "performance", "vsr-transport-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var source = MediaProbe.Probe(input);
    var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var previous = PipelineLog.Sink;
    PipelineLog.Sink = logs.Enqueue;
    try
    {
        TranscodeOptions Options(string name) => new()
        {
            HostDllPath = DemoPaths.HostDllPath, RuntimeDllPath = DemoPaths.RuntimeDllPath,
            LogPath = Path.Combine(root, name + "-nr.log"), VsrLogPath = Path.Combine(root, name + "-sr.log"),
            SuperResolutionScale = 2, MaxFrames = 12, CopyAudio = false,
            Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.None },
        };
        async Task<TranscodeResult> Run(string name, TranscodeOptions options, bool gpu, int crop = 0)
        {
            // Packed RGB10 -> P010 on this driver fails the color fixture;
            // HDR must keep its original pixels through the compatibility path.
            gpu &= !source.IsHdr;
            logs.Clear();
            var result = await DlssVideoPipeline.RunAsync(input, Path.Combine(root, name + ".mp4"), options).WaitAsync(TimeSpan.FromSeconds(60));
            var encoded = MediaProbe.Probe(result.OutputPath);
            Check(encoded.FrameCount == options.MaxFrames && result.FramesProcessed == options.MaxFrames, $"{name}: lost or duplicated tail frames.");
            Check(encoded.Width == source.Width * 2 - crop && encoded.Height == source.Height * 2, $"{name}: dimensions changed.");
            Check(Math.Abs(encoded.Fps - source.Fps) < .0001, $"{name}: fractional frame rate changed.");
            Check(encoded.IsHdr == source.IsHdr, $"{name}: HDR metadata changed.");
            Check(logs.Any(line => line.StartsWith("VSR GPU 编码已启用")) == gpu, $"{name}: unexpected GPU transport/fallback.");
            if (gpu) Check(File.ReadAllText(options.VsrLogPath).Contains("full-frame readback disabled"), "GPU path still downloads full frames.");
            Console.WriteLine($"PASS VSR transport {name}: gpu={gpu}, {encoded.FrameCount} frames @ {encoded.FrameRate}");
            return result;
        }
        var fastOutput = await Run("gpu-no-preview", Options("gpu-no-preview"), true);
        using (var preview = new TranscodePreviewBuffer(160, 90))
        {
            await Run("gpu-preview", Options("gpu-preview") with { PreviewSink = preview, PreviewFramesPerSecond = 1 }, true);
            using var last = preview.TakeLatest();
            Check(last?.FramesDone == 12 && last.Width <= 160 && last.Height <= 90, "GPU transport lost the final preview at a low preview rate.");
        }
        using (var preview = new TranscodePreviewBuffer(160, 90))
        {
            await Run("gpu-one-frame", Options("gpu-one-frame") with { PreviewSink = preview, MaxFrames = 1 }, true);
            using var last = preview.TakeLatest();
            Check(last?.FramesDone == 1, "GPU one-frame processing lost its final preview.");
        }
        var rawOutput = await Run("raw-frames", Options("raw-frames") with { UseGpuVsrEncoding = false }, false);
        var compare = new ProcessStartInfo(DemoPaths.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-i", rawOutput.OutputPath,
            "-i", fastOutput.OutputPath, "-lavfi", "psnr", "-an", "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null" })
            compare.ArgumentList.Add(argument);
        using (var process = Process.Start(compare)!)
        {
            var statistics = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var match = System.Text.RegularExpressions.Regex.Match(await statistics, @"PSNR.*average:([0-9.]+|inf)");
            Check(process.ExitCode == 0 && match.Success, "Could not compare GPU/compatibility colors.");
            var psnr = match.Groups[1].Value == "inf" ? double.PositiveInfinity
                : double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(psnr >= (source.IsHdr ? 50 : 35), $"GPU transport changed color/channel interpretation: PSNR={psnr} dB.");
            Console.WriteLine($"PASS VSR transport pixel/color regression: PSNR={psnr:0.00} dB, hdr={source.IsHdr}");
        }
        await Run("cpu-filter", Options("cpu-filter") with { Video = new VideoTranscodeOptions
            { AudioCodec = AudioCodec.None, CropEnabled = true, CropLeft = 2 } }, false, 2);
        var missingEncoderDirectory = Path.Combine(root, "host-without-encoder");
        Directory.CreateDirectory(missingEncoderDirectory);
        var fallbackHost = Path.Combine(missingEncoderDirectory, "vsr_host.dll");
        File.Copy(DemoPaths.VsrHostDllPath, fallbackHost);
        var fallbackRuntime = Path.Combine(missingEncoderDirectory, "nvngx_vsr.dll");
        File.Copy(DemoPaths.VsrRuntimeDllPath, fallbackRuntime);
        await Run("missing-encoder", Options("missing-encoder") with
            { VsrHostDllPath = fallbackHost, VsrRuntimeDllPath = fallbackRuntime }, false);
        if (!source.IsHdr)
        {
            await Run("h264", Options("h264") with { VideoEncoder = "h264_nvenc" }, true);
            await Run("av1", Options("av1") with { VideoEncoder = "av1_nvenc" }, true);
        }
        var audio = await Run("audio", Options("audio") with { CopyAudio = true,
            Video = new VideoTranscodeOptions { AudioCodec = AudioCodec.Copy } }, true);
        Check(MediaProbe.Probe(audio.OutputPath).HasAudio == source.HasAudio, "GPU remux lost source audio.");
        using (var stop = new CancellationTokenSource())
        {
            try
            {
                await DlssVideoPipeline.RunAsync(input, Path.Combine(root, "cancel.mp4"), Options("cancel"),
                    new InlineProgress(_ => stop.Cancel()), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
                throw new Exception("GPU encoding ignored cancellation with frames in flight.");
            }
            catch (OperationCanceledException) { }
        }
        await Run("restart", Options("restart"), true);
        Console.WriteLine("PASS GPU encoding cancellation, resource cleanup and restart");
    }
    finally { PipelineLog.Sink = previous; }
}

static unsafe void NativeNrBenchmark(string inputPath)
{
    var video = MediaProbe.Probe(inputPath);
    var input = new byte[checked(video.Width * video.Height * 4)];
    var output = new byte[input.Length];
    var start = new ProcessStartInfo(DemoPaths.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    foreach (var argument in new[] { "-v", "error", "-i", inputPath, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" })
        start.ArgumentList.Add(argument);
    using (var decoder = Process.Start(start)!)
    {
        decoder.StandardOutput.BaseStream.ReadExactly(input);
        decoder.WaitForExit();
        Check(decoder.ExitCode == 0, "NR benchmark source decoding failed.");
    }
    foreach (var queued in new[] { false, true })
        foreach (var guidance in new[] { false, true })
        {
            using var host = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
                DemoPaths.LogPath, video.Width, video.Height, new DlssNrOptions());
            host.ConfigureMotionGuidance(guidance);
            var motion = guidance ? new float[checked(video.Width * video.Height * 2)] : null;
            fixed (byte* color = input, destination = output)
            fixed (float* vectors = motion)
            {
                for (var warm = 0; warm < 10; warm++) Check(host.ProcessVideoFrame(color, vectors, destination, warm == 0), "Warm-up failed.");
                const int count = 180;
                var timer = Stopwatch.StartNew();
                var pending = 0;
                for (var frame = 0; frame < count; frame++)
                {
                    if (!queued) Check(host.ProcessVideoFrame(color, vectors, destination, false), "NR evaluation failed.");
                    else
                    {
                        Check(host.EnqueueVideoFrame(color, vectors, false), "NR enqueue failed.");
                        if (++pending == host.QueueCapacity)
                        {
                            Check(host.DequeueVideoFrame(destination), "NR dequeue failed.");
                            pending--;
                        }
                    }
                }
                while (pending-- > 0) Check(host.DequeueVideoFrame(destination), "NR tail failed.");
                timer.Stop();
                Console.WriteLine($"NR_HOST queued={queued} guidance={guidance}: {timer.Elapsed.TotalMilliseconds / count:F2} ms/frame (CPU input/upload/inference/fence/readback; predecoded repeated frame)");
            }
        }
}

static unsafe void NativeFlicker(string inputPath)
{
    const int width = 512, height = 288;
    var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var argument in new[] { "-v", "error", "-i", inputPath, "-vf", $"scale={width}:{height}",
        "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" })
        start.ArgumentList.Add(argument);
    var input = new byte[width * height * 4];
    using (var decoder = System.Diagnostics.Process.Start(start)!)
    {
        decoder.StandardOutput.BaseStream.ReadExactly(input);
        decoder.WaitForExit();
        Check(decoder.ExitCode == 0, "Fixture decoding failed.");
    }
    var logDirectory = Path.Combine(Environment.CurrentDirectory, ".qa", "temporal-tests");
    Directory.CreateDirectory(logDirectory);
    using var host = DlssNrHost.Create(DemoPaths.HostDllPath, DemoPaths.RuntimeDllPath,
        Path.Combine(logDirectory, "native-flicker.log"), width, height, new DlssNrOptions());
    var filter = new DlssNrTemporalStabilizer(width, height, false, 60);
    var output = new byte[input.Length];
    var previousRaw = new byte[input.Length];
    var previousFiltered = new byte[input.Length];
    double rawDelta = 0, filteredDelta = 0;
    fixed (byte* source = input, destination = output)
    {
        for (var frame = 0; frame < 90; frame++)
        {
            var reset = filter.PrepareFrame(source);
            Check(host.ProcessFrame(source, destination, reset), "Native NR evaluation failed.");
            if (frame >= 20)
                for (var pixel = 0; pixel < output.Length; pixel++)
                    if (pixel % 4 != 3) rawDelta += Math.Abs(output[pixel] - previousRaw[pixel]);
            output.CopyTo(previousRaw, 0);
            filter.Apply(source, destination);
            if (frame >= 20)
                for (var pixel = 0; pixel < output.Length; pixel++)
                    if (pixel % 4 != 3) filteredDelta += Math.Abs(output[pixel] - previousFiltered[pixel]);
            output.CopyTo(previousFiltered, 0);
        }
    }
    Check(rawDelta > 0, "This fixture/runtime did not reproduce static NR flicker.");
    Check(filteredDelta < rawDelta * .75, $"Native flicker did not decrease: {rawDelta} -> {filteredDelta}.");
    Console.WriteLine($"PASS Native fixed-input NR flicker reduced {(1 - filteredDelta / rawDelta) * 100:F1}%");
}

static unsafe void NativeOpticalFlow()
{
    const int width = 512, height = 288, shift = 8;
    using var flow = NvidiaOpticalFlow.TryCreate(width, height, false);
    Check(flow != null, "NVIDIA optical flow is unavailable on the test GPU.");
    var random = new Random(314159);
    var previous = new byte[width * height * 4];
    random.NextBytes(previous);
    var current = Rgba(width, height, 0);
    for (var y = 0; y < height; y++)
        for (var x = shift; x < width; x++)
            previous.AsSpan((y * width + x - shift) * 4, 4).CopyTo(current.AsSpan((y * width + x) * 4, 4));
    fixed (byte* source = previous, translated = current)
    {
        flow!.Calculate(source, true);
        Check(flow.Motion.All(value => value == 0), "First-frame optical flow was not zero.");
        flow.Calculate(translated, false);
        var dx = new List<float>();
        var dy = new List<float>();
        for (var y = 64; y < height - 64; y++)
            for (var x = 64; x < width - 64; x++)
            {
                dx.Add(flow.Motion[(y * width + x) * 2]);
                dy.Add(flow.Motion[(y * width + x) * 2 + 1]);
            }
        dx.Sort(); dy.Sort();
        var medianX = dx[dx.Count / 2];
        var medianY = dy[dy.Count / 2];
        Check(Math.Abs(medianX + shift) < 1 && Math.Abs(medianY) < 1,
            $"Wrong motion direction/units: expected (-8,0), got ({medianX},{medianY}).");
        flow.Calculate(translated, true);
        Check(flow.Motion.All(value => value == 0), "Cut-frame optical flow was not zero.");
        flow.Calculate(source, false);
        var center = (height / 2 * width + width / 2) * 2;
        Check(Math.Abs(flow.Motion[center] - shift) < 1, "Optical-flow hints survived the cut/reset.");
        Console.WriteLine($"PASS Native optical flow current→previous displacement ({medianX:F2}, {medianY:F2}), grid={flow.GridSize}");
    }
}

static unsafe void NativeFineMotion()
{
    const int width = 512, height = 288, shift = 8;
    var previous = new byte[width * height * 4];
    new Random(271828).NextBytes(previous);
    var current = new byte[previous.Length];
    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x < width / 2 ? shift : -shift;
            var sourceX = Math.Clamp(x + dx, 0, width - 1);
            previous.AsSpan((y * width + sourceX) * 4, 4).CopyTo(current.AsSpan((y * width + x) * 4, 4));
        }
    var results = new List<(double Error, double Coverage)>();
    foreach (var grid in new[] { 4, 1 })
    {
        using var flow = NvidiaOpticalFlow.TryCreate(width, height, false, grid);
        Check(flow != null, "Native quality comparison requires optical flow.");
        fixed (byte* source = previous, destination = current)
        {
            flow!.Calculate(source, true);
            flow.Calculate(destination, false);
        }
        double error = 0;
        var valid = 0;
        var total = 0;
        for (var y = 48; y < height - 48; y++)
            for (var x = width / 2 - 12; x < width / 2 + 12; x++)
            {
                total++;
                var pixel = y * width + x;
                Check(float.IsFinite(flow!.Motion[pixel * 2]) && float.IsFinite(flow.Motion[pixel * 2 + 1]), "Nonfinite guidance escaped the driver filter.");
                if (flow.RejectedMotion[pixel]) continue;
                var expected = x < width / 2 ? shift : -shift;
                error += Math.Sqrt(Math.Pow(flow.Motion[pixel * 2] - expected, 2) + Math.Pow(flow.Motion[pixel * 2 + 1], 2));
                valid++;
            }
        Check(valid > total * .5, "Quality comparison rejected most of its motion boundary.");
        results.Add((error / valid, (double)valid / total));
        Console.WriteLine($"Native opposing-motion boundary grid={flow!.GridSize}: EPE={error / valid:F3}px, coverage={(double)valid / total:P1}");
    }
    Check(results[1].Error <= results[0].Error + .5, "Fine flow degraded the opposing-motion boundary.");
    Console.WriteLine("PASS Native fine motion retains distinct motion on opposite sides of an edge");
}

static unsafe void CompareVideo(string sourcePath, string beforePath, string afterPath)
{
    const int width = 512, height = 288, frames = 300;
    using var flow = NvidiaOpticalFlow.TryCreate(width, height, false);
    Check(flow != null, "Motion-compensated measurement requires NVIDIA optical flow.");
    var decoders = new List<Process>();
    var current = Enumerable.Range(0, 3).Select(_ => new byte[width * height * 4]).ToArray();
    var previous = Enumerable.Range(0, 3).Select(_ => new byte[width * height * 4]).ToArray();
    var errors = new float[width * height];
    double beforeDelta = 0, afterDelta = 0;
    long samples = 0;
    try
    {
        foreach (var path in new[] { sourcePath, beforePath, afterPath })
        {
            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-v", "error", "-i", path, "-vf", $"scale={width}:{height}",
                "-frames:v", frames.ToString(), "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" })
                start.ArgumentList.Add(argument);
            decoders.Add(Process.Start(start)!);
        }
        for (var frame = 0; frame < frames; frame++)
        {
            for (var index = 0; index < 3; index++) decoders[index].StandardOutput.BaseStream.ReadExactly(current[index]);
            fixed (byte* rgba = current[0]) flow!.Calculate(rgba, frame == 0);
            var motion = flow!.Motion;
            if (frame > 0)
            {
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = y * width + x;
                        var px = x + motion[pixel * 2]; var py = y + motion[pixel * 2 + 1];
                        var valid = px >= 0 && py >= 0 && px <= width - 1 && py <= height - 1;
                        var error = valid ? Vector3.Abs(Color(current[0], pixel) - Warp(previous[0], px, py, width, height)) : new Vector3(float.PositiveInfinity);
                        errors[pixel] = Math.Max(error.X, Math.Max(error.Y, error.Z));
                    }
                // Warmup frames are excluded from a settled-history measurement.
                if (frame >= 20)
                    for (var y = 1; y < height - 1; y++)
                        for (var x = 1; x < width - 1; x++)
                        {
                            var error = 0f; var maximum = 0f;
                            for (var dy = -1; dy <= 1; dy++)
                                for (var dx = -1; dx <= 1; dx++)
                                {
                                    var value = errors[(y + dy) * width + x + dx];
                                    error += value; maximum = Math.Max(maximum, value);
                                }
                            var confidence = (1 - Smooth(.008f, .04f, error / 9)) * (1 - Smooth(.025f, .10f, maximum));
                            if (confidence <= .5f) continue;
                            var pixel = y * width + x;
                            var px = x + motion[pixel * 2]; var py = y + motion[pixel * 2 + 1];
                            var color = Color(current[0], pixel);
                            var oldColor = Warp(previous[0], px, py, width, height);
                            var before = Vector3.Abs((Color(current[1], pixel) - color) - (Warp(previous[1], px, py, width, height) - oldColor));
                            var after = Vector3.Abs((Color(current[2], pixel) - color) - (Warp(previous[2], px, py, width, height) - oldColor));
                            beforeDelta += (before.X + before.Y + before.Z) * 255 / 3;
                            afterDelta += (after.X + after.Y + after.Z) * 255 / 3;
                            samples++;
                        }
            }
            (current, previous) = (previous, current);
        }
    }
    finally
    {
        foreach (var decoder in decoders)
        {
            if (!decoder.HasExited) decoder.Kill();
            decoder.WaitForExit(); decoder.Dispose();
        }
    }
    Check(samples > 0, "No trustworthy motion matches were found.");
    Console.WriteLine($"Motion-compensated encoded correction delta: {beforeDelta / samples:F4} -> {afterDelta / samples:F4}; reduction {(1 - afterDelta / beforeDelta) * 100:F1}%; samples {samples}");
    // This is a measurement command, not a perceptual pass/fail threshold:
    // interpolation, encoding and content all affect the measured percentage.
}

static Vector3 Color(byte[] rgba, int pixel) =>
    new Vector3(rgba[pixel * 4], rgba[pixel * 4 + 1], rgba[pixel * 4 + 2]) / 255;

static Vector3 Warp(byte[] rgba, float x, float y, int width, int height)
{
    var x0 = (int)x; var y0 = (int)y;
    var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
    var a = Vector3.Lerp(Color(rgba, y0 * width + x0), Color(rgba, y0 * width + x1), x - x0);
    var b = Vector3.Lerp(Color(rgba, y1 * width + x0), Color(rgba, y1 * width + x1), x - x0);
    return Vector3.Lerp(a, b, y - y0);
}

static float Smooth(float low, float high, float value)
{
    var t = Math.Clamp((value - low) / (high - low), 0, 1);
    return t * t * (3 - 2 * t);
}

sealed class TestFrame
{
    internal long Value;
}

sealed class InlineProgress(Action<TranscodeProgress> report) : IProgress<TranscodeProgress>
{
    public void Report(TranscodeProgress value) => report(value);
}

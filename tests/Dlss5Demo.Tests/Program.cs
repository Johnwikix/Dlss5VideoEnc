using Dlss5Demo.Core;
using System.Numerics;
using System.Diagnostics;

var tests = new (string Name, Action Run)[]
{
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
};
foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} regression checks passed.");
if (args.Contains("--gpu"))
{
    NativeOpticalFlow();
    NativeFormatSwitch();
    Console.WriteLine("PASS Native HDR to SDR session format switch");
}
var flickerArgument = Array.IndexOf(args, "--gpu-flicker");
if (flickerArgument >= 0)
    NativeFlicker(args[flickerArgument + 1]);
var comparisonArgument = Array.IndexOf(args, "--compare-video");
if (comparisonArgument >= 0)
    CompareVideo(args[comparisonArgument + 1], args[comparisonArgument + 2], args[comparisonArgument + 3]);

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
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
    var logDirectory = Path.Combine(Environment.CurrentDirectory, "build", "temporal-tests");
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
    var logDirectory = Path.Combine(Environment.CurrentDirectory, "build", "temporal-tests");
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
        Console.WriteLine($"PASS Native optical flow current→previous displacement ({medianX:F2}, {medianY:F2})");
    }
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

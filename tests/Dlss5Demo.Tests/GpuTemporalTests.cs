using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Dlss5Demo.Core;
using Dlss5Demo.Tests.Reference;

internal static unsafe class GpuTemporalTests
{
    internal static void Kernels()
    {
        using var cuda = new CudaVideoContext(0); cuda.InitializeTemporal();
        foreach (var size in new[] { (W: 1, H: 1), (W: 7, H: 9), (W: 39, H: 23), (W: 129, H: 257) })
        {
            Expansion(cuda, size.W, size.H);
            foreach (double fps in new[] { 2d, 24d, 60d }) History(cuda, size.W, size.H, fps);
        }
        Console.WriteLine("PASS GPU optical-flow expansion, gray rounding, reset/rejection and correction history match CPU reference");
    }

    private static void Expansion(CudaVideoContext cuda, int w, int h)
    {
        int aw = (w + 15) / 16 * 16, ah = (h + 15) / 16 * 16;
        var random = new Random(3741); byte[] rgba = new byte[w * h * 4]; random.NextBytes(rgba);
        using var input = new CudaVideoContext.DeviceAllocation(cuda, (nuint)rgba.Length);
        using var gray = new CudaVideoContext.DeviceAllocation(cuda, (nuint)(aw * ah));
        cuda.WriteDevice(input.Data, rgba); cuda.TemporalGray(input.Data, w * 4, gray.Data, aw, w, h, aw, ah);
        byte[] luma = new byte[aw * ah]; cuda.ReadDevice(gray.Data, luma);
        for (int y = 0; y < ah; y++) for (int x = 0; x < aw; x++)
        {
            int p = (Math.Min(y, h - 1) * w + Math.Min(x, w - 1)) * 4;
            Require(luma[y * aw + x] == NvidiaOpticalFlow.ToGray(77 * rgba[p] + 150 * rgba[p + 1] + 29 * rgba[p + 2]),
                $"GPU gray/padding differs {w}x{h} at {x},{y}: {luma[y * aw + x]} vs {NvidiaOpticalFlow.ToGray(77 * rgba[p] + 150 * rgba[p + 1] + 29 * rgba[p + 2])}");
        }
        foreach (int grid in new[] { 1, 2, 4 }) foreach (bool confidence in new[] { false, true })
        {
            int gw = aw / grid, gh = ah / grid, pp = gw * 4 + 16, cp = gw + 8;
            short[] packed = new short[pp * gh / 2]; byte[] costs = new byte[cp * gh];
            for (int i = 0; i < packed.Length; i++) packed[i] = (short)random.Next(-180, 181);
            for (int i = 0; i < costs.Length; i++) costs[i] = (byte)random.Next(0, 180);
            using var devicePacked = new CudaVideoContext.DeviceAllocation(cuda, (nuint)(pp * gh));
            using var deviceCost = new CudaVideoContext.DeviceAllocation(cuda, (nuint)costs.Length);
            using var motion = new CudaVideoContext.DeviceAllocation(cuda, (nuint)(w * h * 8));
            using var rejected = new CudaVideoContext.DeviceAllocation(cuda, (nuint)(w * h));
            cuda.WriteDevice(devicePacked.Data, MemoryMarshal.AsBytes(packed.AsSpan())); cuda.WriteDevice(deviceCost.Data, costs);
            cuda.TemporalExpand(new(devicePacked.Data, pp, confidence ? deviceCost.Data : 0, cp, gw, gh, grid, confidence, false), motion.Data, rejected.Data, w, h);
            float[] actual = new float[w * h * 2]; byte[] bad = new byte[w * h];
            cuda.ReadDevice(motion.Data, MemoryMarshal.AsBytes(actual.AsSpan())); cuda.ReadDevice(rejected.Data, bad);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + .5f) / grid - .5f, 0, gw - 1), fy = Math.Clamp((y + .5f) / grid - .5f, 0, gh - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, gw - 1), y1 = Math.Min(y0 + 1, gh - 1);
                float ax = fx - x0, ay = fy - y0;
                float Component(int c)
                {
                    float a = packed[y0 * pp / 2 + x0 * 2 + c] * (1 - ax) + packed[y0 * pp / 2 + x1 * 2 + c] * ax;
                    float b = packed[y1 * pp / 2 + x0 * 2 + c] * (1 - ax) + packed[y1 * pp / 2 + x1 * 2 + c] * ax;
                    return (a * (1 - ay) + b * ay) / 32;
                }
                float mx = Component(0), my = Component(1);
                int cost = grid == 1 ? costs[y0 * cp + x0] : Math.Max(Math.Max(costs[y0 * cp + x0], costs[y0 * cp + x1]), Math.Max(costs[y1 * cp + x0], costs[y1 * cp + x1]));
                bool invalid = confidence && (cost >= 128 || x + mx < 0 || x + mx > w - 1 || y + my < 0 || y + my > h - 1);
                int p = y * w + x;
                Require(actual[p * 2] == (invalid ? 0 : mx) && actual[p * 2 + 1] == (invalid ? 0 : my) && (bad[p] != 0) == invalid, $"GPU expansion differs grid={grid} at {x},{y}");
            }
        }
    }

    private static void History(CudaVideoContext cuda, int w, int h, double fps)
    {
        int pixels = w * h, fused = Fma.IsSupported ? 1 : 0;
        float weight = 1 / fps <= .25 ? (float)Math.Exp(-1 / fps / .12) : 0;
        var owned = new List<IDisposable>();
        nint Allocate(int bytes) { var value = new CudaVideoContext.DeviceAllocation(cuda, (nuint)bytes); owned.Add(value); return value.Data; }
        nint input = Allocate(pixels * 4), output = Allocate(pixels * 4), motion = Allocate(pixels * 8), rejected = Allocate(pixels), errors = Allocate(pixels * 4);
        nint previous = Allocate(pixels * 16), current = Allocate(pixels * 16), residual = Allocate(pixels * 16), next = Allocate(pixels * 16), reset = Allocate(4), half = Allocate(pixels * 4);
        using var flag = new CudaVideoContext.PinnedAllocation(cuda, 4);
        var reference = new ReferenceTemporalStabilizer(w, h, false, fps, true);
        byte[] source = new byte[pixels * 4], raw = new byte[source.Length], expected = new byte[source.Length], actual = new byte[source.Length];
        float[] vectors = new float[pixels * 2], expectedMotion = new float[vectors.Length], error = new float[pixels], actualErrors = new float[pixels];
        bool[] rejection = new bool[pixels]; byte[] rejectBytes = new byte[pixels];
        try
        {
            for (int frame = 0; frame < 36; frame++)
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    for (int c = 0; c < 4; c++)
                    {
                        int value = c == 3 ? (p * 29 + frame) % 256 : (x / 8 + y / 8 + c * 17 + (frame >= 20 ? 90 : 40) + frame % 3) % 200;
                        source[p * 4 + c] = (byte)value;
                        raw[p * 4 + c] = c == 3 ? (byte)value : (byte)Math.Clamp(value + (frame % 2 == 0 ? 25 : -12), 0, 255);
                    }
                    vectors[p * 2] = frame < 4 ? 0 : frame % 6 == 0 ? .375f : -.25f;
                    vectors[p * 2 + 1] = frame % 3 == 0 ? .75f : 0;
                    if (frame == 12 && p % 23 == 0) vectors[p * 2] = float.NaN;
                    if (frame == 13 && p % 17 == 0) vectors[p * 2 + 1] = float.PositiveInfinity;
                    if (frame == 14 && p % 11 == 0) vectors[p * 2] = 20;
                    rejection[p] = frame == 15 && p % 5 == 0; rejectBytes[p] = rejection[p] ? (byte)1 : (byte)0;
                }
                vectors.CopyTo(expectedMotion, 0); raw.CopyTo(expected, 0);
                cuda.WriteDevice(input, source); cuda.WriteDevice(output, raw); cuda.WriteDevice(motion, MemoryMarshal.AsBytes(vectors.AsSpan())); cuda.WriteDevice(rejected, rejectBytes);
                bool expectedReset;
                fixed (byte* s = source, e = expected)
                {
                    expectedReset = reference.PrepareInput(s, expectedMotion, error);
                    bool actualReset = cuda.TemporalPrepare(input, w * 4, previous, current, motion, errors, reset, flag.Data, w, h, frame != 0, fused);
                    cuda.ReadDevice(errors, MemoryMarshal.AsBytes(actualErrors.AsSpan()));
                    for (int p = 0; p < pixels; p++) Require(error[p] == actualErrors[p], $"Input error differs {w}x{h}, frame {frame}, pixel {p}: {error[p]:R} vs {actualErrors[p]:R}");
                    Require(expectedReset == actualReset, "GPU scene/reset differs");
                    if (expectedReset) { Array.Clear(expectedMotion); Array.Clear(rejection); }
                    MotionReliability.Apply(expectedMotion, rejection, error, !expectedReset);
                    reference.SetPreparedFrame(expectedMotion, error, expectedReset); reference.Apply(s, e);
                }
                cuda.TemporalReliability(motion, rejected, errors, half, w * 4, reset, w, h, true);
                cuda.TemporalAccumulate(input, w * 4, output, w * 4, residual, next, motion, errors, reset, w, h, weight, fused);
                cuda.ReadDevice(output, actual);
                for (int p = 0; p < actual.Length; p++) Require(actual[p] == expected[p], $"History differs {w}x{h} fps={fps}, frame {frame}, byte {p}: {expected[p]} vs {actual[p]}");
                (previous, current) = (current, previous); (residual, next) = (next, residual);
            }
        }
        finally { cuda.FinishTemporal(); foreach (var value in owned) value.Dispose(); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

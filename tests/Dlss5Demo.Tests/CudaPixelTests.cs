using System.Runtime.InteropServices;
using Dlss5Demo.Core;

internal static unsafe class CudaPixelTests
{
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyHtoD_v2")] private static extern int CopyToGpu(ulong destination, void* source, nuint length);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyDtoH_v2")] private static extern int CopyToHost(void* destination, ulong source, nuint length);
    [DllImport("nvcuda.dll")] private static extern int cuCtxSynchronize();
    internal static void Upload(CudaVideoContext cuda, CudaVideoFrame destination, byte[] bgra)
    {
        using var scope = cuda.Enter();
        fixed (byte* pixels = bgra) CudaVideoContext.Check(CopyToGpu((ulong)destination.Data, pixels, (nuint)bgra.Length));
        CudaVideoContext.Check(cuCtxSynchronize());
    }
    internal static byte[] Download(CudaVideoContext cuda, CudaVideoFrame source)
    {
        var pixels = new byte[source.Width * source.Height * 4];
        using var scope = cuda.Enter();
        fixed (byte* pointer = pixels) CudaVideoContext.Check(CopyToHost(pointer, (ulong)source.Data, (nuint)pixels.Length));
        return pixels;
    }
    internal static byte[] Bgra(byte[] rgba)
    {
        var result = rgba.ToArray();
        for (int i = 0; i < result.Length; i += 4) (result[i], result[i + 2]) = (result[i + 2], result[i]);
        return result;
    }
    internal static void Scenes()
    {
        using var cuda = new CudaVideoContext(0);
        foreach (var size in new[] { (Width: 32, Height: 32), (Width: 80, Height: 72), (Width: 1920, Height: 1080) })
        {
            using var first = cuda.Allocate(size.Width, size.Height);
            using var second = cuda.Allocate(size.Width, size.Height);
            byte[] a = new byte[size.Width * size.Height * 4], b = new byte[a.Length];
            for (int i = 3; i < a.Length; i += 4) a[i] = 255;
            cuda.UploadRgba(a, first.Frame);
            Require(Bgra(Download(cuda, first.Frame)).AsSpan().SequenceEqual(a), "RGBA GPU upload changed bytes");
            Upload(cuda, first.Frame, a); Upload(cuda, second.Frame, a);
            Require(cuda.HoldPair(first.Frame, second.Frame, true), "Identical frames were not held");
            foreach (byte value in new byte[] { 10, 45, 46, 140, 141, 255 })
            {
                for (int i = 0; i < b.Length; i += 4) { b[i] = b[i + 1] = b[i + 2] = value; b[i + 3] = 255; }
                Upload(cuda, second.Frame, b);
                Require(!cuda.HoldPair(first.Frame, second.Frame, false), "Different pixels marked identical");
                Require(cuda.HoldPair(first.Frame, second.Frame, true) == (value >= 46), $"Scene threshold changed: {size}, value={value}");
            }
            Console.WriteLine($"PASS parallel GPU scene histogram and exact thresholds: {size.Width}x{size.Height}");
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

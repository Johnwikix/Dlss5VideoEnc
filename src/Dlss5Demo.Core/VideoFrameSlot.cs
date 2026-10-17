using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>A session-owned buffer slot; pointers/metadata remain valid through all FIFO stages.</summary>
internal sealed unsafe class VideoFrameSlot : IDisposable
{
    internal int Slot { get; }
    internal byte* Source { get; private set; }
    internal byte* Input { get; private set; }
    internal byte* Output { get; private set; }
    internal byte* FinalOutput { get; private set; }
    internal byte* Encoded { get; private set; }
    internal float[]? Motion { get; }
    internal bool[]? RejectedMotion { get; }
    internal float[]? Errors { get; }
    internal bool Reset { get; set; }
    internal bool HasPreview { get; set; }
    private readonly bool _ownsEncoded;
    private bool _ownsSource, _ownsOutput;

    internal VideoFrameSlot(int slot, long sourceBytes, long processBytes, bool hdr,
        long nrBytes, int pixels, bool motion, bool history, RtxVideoWorker? worker, bool nrFirst, bool enableNr)
    {
        Slot = slot;
        _ownsEncoded = hdr && worker?.GpuEncoding != true;
        try
        {
            var nrBeforeWorker = enableNr && nrFirst && worker != null;
            _ownsSource = worker == null || nrBeforeWorker;
            Source = _ownsSource ? Allocate(sourceBytes) : worker!.Source(slot);
            Input = worker != null && !nrBeforeWorker && enableNr ? worker.Output(slot) : Source;
            _ownsOutput = enableNr && !nrBeforeWorker;
            Output = _ownsOutput ? Allocate(nrBytes) : nrBeforeWorker ? worker!.Source(slot) : Source;
            FinalOutput = worker != null && (nrBeforeWorker || !enableNr) ? worker.Output(slot) : Output;
            Encoded = _ownsEncoded ? Allocate(processBytes) : FinalOutput;
            // Allocated once per slot, never rented/returned while a native call owns a pointer.
            Motion = motion ? GC.AllocateUninitializedArray<float>(checked(pixels * 2), pinned: true) : null;
            RejectedMotion = motion ? new bool[pixels] : null;
            Errors = history ? GC.AllocateUninitializedArray<float>(pixels) : null;
        }
        catch { Dispose(); throw; }
    }

    private static byte* Allocate(long bytes)
    {
        var pointer = (byte*)NativeMemory.Alloc(checked((nuint)bytes));
        if (pointer == null) throw new OutOfMemoryException();
        return pointer;
    }

    public void Dispose()
    {
        if (_ownsSource) NativeMemory.Free(Source);
        if (_ownsOutput) NativeMemory.Free(Output);
        if (_ownsEncoded) NativeMemory.Free(Encoded);
        Source = Input = Output = FinalOutput = Encoded = null;
    }
}

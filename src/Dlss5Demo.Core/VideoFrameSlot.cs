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
    private readonly bool _shared, _hdr, _nrFirst;

    internal VideoFrameSlot(int slot, long sourceBytes, long processBytes, bool hdr,
        long nrBytes, int pixels, bool motion, bool history, RtxVideoWorker? worker, bool nrFirst)
    {
        Slot = slot; _shared = worker != null; _hdr = hdr; _nrFirst = nrFirst && _shared;
        try
        {
            Source = _shared && !_nrFirst ? worker!.Source(slot) : Allocate(sourceBytes);
            Input = _shared && !_nrFirst ? worker!.Output(slot) : Source;
            Output = _nrFirst ? worker!.Source(slot) : Allocate(nrBytes);
            FinalOutput = _nrFirst ? worker!.Output(slot) : Output;
            Encoded = hdr ? Allocate(processBytes) : FinalOutput;
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
        if (!_shared || _nrFirst) NativeMemory.Free(Source);
        if (!_nrFirst) NativeMemory.Free(Output);
        if (_hdr) NativeMemory.Free(Encoded);
        Source = Input = Output = FinalOutput = Encoded = null;
    }
}

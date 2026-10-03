using System.Buffers;
using System.Runtime.InteropServices;

namespace Dlss5Demo.Core;

/// <summary>A synchronous preview consumer. Spans are valid only for the duration of Publish.</summary>
public interface ITranscodePreviewSink
{
    void Publish(ReadOnlySpan<byte> sourceRgba, int sourceWidth, int sourceHeight,
        ReadOnlySpan<byte> processedRgba, int processedWidth, int processedHeight,
        long framesDone, long totalFrames, double sourceFps);

    // Compatibility overload for same-size pipelines.
    void Publish(ReadOnlySpan<byte> sourceRgba, ReadOnlySpan<byte> processedRgba,
        int width, int height, long framesDone, long totalFrames, double sourceFps);
}

/// <summary>One atomic source/result pair. The reader must dispose the pair after uploading its pixels.</summary>
public sealed class TranscodePreviewFrame : IDisposable
{
    private byte[]? _source;
    private byte[]? _processed;
    public int Width { get; }
    public int Height { get; }
    public int ByteLength => checked(Width * Height * 4);
    public long FramesDone { get; }
    public long TotalFrames { get; }
    public double PositionSeconds { get; }
    public ReadOnlySpan<byte> Source => (_source ?? throw new ObjectDisposedException(nameof(TranscodePreviewFrame))).AsSpan(0, ByteLength);
    public ReadOnlySpan<byte> Processed => (_processed ?? throw new ObjectDisposedException(nameof(TranscodePreviewFrame))).AsSpan(0, ByteLength);

    internal TranscodePreviewFrame(int width, int height, long framesDone, long totalFrames, double sourceFps)
    {
        Width = width;
        Height = height;
        FramesDone = framesDone;
        TotalFrames = totalFrames;
        PositionSeconds = Math.Max(0, framesDone - 1) / Math.Max(1, sourceFps);
        _source = ArrayPool<byte>.Shared.Rent(ByteLength);
        try { _processed = ArrayPool<byte>.Shared.Rent(ByteLength); }
        catch { ArrayPool<byte>.Shared.Return(_source); _source = null; throw; }
    }

    internal Span<byte> WritableSource => _source.AsSpan(0, ByteLength);
    internal Span<byte> WritableProcessed => _processed.AsSpan(0, ByteLength);

    public void Dispose()
    {
        var source = Interlocked.Exchange(ref _source, null);
        var processed = Interlocked.Exchange(ref _processed, null);
        if (source != null) ArrayPool<byte>.Shared.Return(source);
        if (processed != null) ArrayPool<byte>.Shared.Return(processed);
    }
}

/// <summary>Bounded, latest-pair mailbox. Superseded frames return to the pool instead of queueing UI work.</summary>
public sealed class TranscodePreviewBuffer : ITranscodePreviewSink, IDisposable
{
    private readonly object _gate = new();
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private TranscodePreviewFrame? _latest;
    private bool _disposed;

    public TranscodePreviewBuffer(int maxWidth = 960, int maxHeight = 540)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
    }

    public void Publish(ReadOnlySpan<byte> sourceRgba, ReadOnlySpan<byte> processedRgba,
        int width, int height, long framesDone, long totalFrames, double sourceFps)
        => Publish(sourceRgba, width, height, processedRgba, width, height,
            framesDone, totalFrames, sourceFps);

    public void Publish(ReadOnlySpan<byte> sourceRgba, int sourceWidth, int sourceHeight,
        ReadOnlySpan<byte> processedRgba, int processedWidth, int processedHeight,
        long framesDone, long totalFrames, double sourceFps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processedWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processedHeight);
        var sourceLength = checked(sourceWidth * sourceHeight * 4);
        var processedLength = checked(processedWidth * processedHeight * 4);
        if (sourceRgba.Length < sourceLength || processedRgba.Length < processedLength)
            throw new ArgumentException("Incomplete preview frame.");
        lock (_gate) { if (_disposed) return; }
        var compareWidth = Math.Max(sourceWidth, processedWidth);
        var compareHeight = Math.Max(sourceHeight, processedHeight);
        var scale = Math.Min(1, Math.Min((double)_maxWidth / compareWidth, (double)_maxHeight / compareHeight));
        var frame = new TranscodePreviewFrame(Math.Max(1, (int)(compareWidth * scale)),
            Math.Max(1, (int)(compareHeight * scale)), framesDone, totalFrames, sourceFps);
        try
        {
            ResizePair(sourceRgba[..sourceLength], sourceWidth, sourceHeight,
                processedRgba[..processedLength], processedWidth, processedHeight, frame);
            lock (_gate)
            {
                if (_disposed) return;
                var previous = _latest;
                _latest = frame;
                frame = null!; // Ownership transfers atomically to the mailbox.
                previous?.Dispose();
            }
        }
        finally { frame?.Dispose(); }
    }

    public TranscodePreviewFrame? TakeLatest()
    {
        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame; // Ownership transfers to the reader.
        }
    }

    private static void ResizePair(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        ReadOnlySpan<byte> processed, int processedWidth, int processedHeight,
        TranscodePreviewFrame frame)
    {
        ResizeOne(source, sourceWidth, sourceHeight, frame.WritableSource, frame.Width, frame.Height);
        ResizeOne(processed, processedWidth, processedHeight, frame.WritableProcessed, frame.Width, frame.Height);
    }

    private static void ResizeOne(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationWidth, int destinationHeight)
    {
        var input = MemoryMarshal.Cast<byte, uint>(source);
        var output = MemoryMarshal.Cast<byte, uint>(destination);
        for (var y = 0; y < destinationHeight; y++)
        {
            var row = (int)((long)y * sourceHeight / destinationHeight) * sourceWidth;
            var destinationRow = y * destinationWidth;
            for (var x = 0; x < destinationWidth; x++)
            {
                var index = row + (int)((long)x * sourceWidth / destinationWidth);
                output[destinationRow + x] = input[index];
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
    }
}

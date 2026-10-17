namespace Dlss5Demo.Core;

/// <summary>编码阶段独占两帧与 RIFE 会话。上游仍由有界阶段队列背压，生成帧不重复执行 NR/SR。</summary>
internal sealed class RifeFrameStream(Stream destination, RifeGpuSession model, int multiplier, CancellationToken ct) : InterpolationFrameStream
{
    private bool _hasFrame, _complete;
    public override void Write(ReadOnlySpan<byte> frame)
    {
        if (_complete) throw new InvalidOperationException("RIFE 输入已经结束。");
        ct.ThrowIfCancellationRequested();
        if (frame.Length != model.FrameBytes) throw new ArgumentException("RIFE 必须整帧写入。");
        if (!_hasFrame)
        {
            model.SetFrame(frame, true);
            destination.Write(frame);
            _hasFrame = true;
            return;
        }
        model.SetFrame(frame, false);
        bool repeat = model.First.SequenceEqual(model.Second);
        bool cut = IsSceneCut();
        for (int i = 1; i < multiplier; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (repeat || cut) WriteFrame(model.First);
            else
            {
                model.Run((float)i / multiplier, ct);
                WriteFrame(model.Output);
            }
        }
        destination.Write(frame);
        model.Second.CopyTo(model.First);
    }
    private bool IsSceneCut()
    {
        Span<int> a = stackalloc int[32], b = stackalloc int[32];
        a.Clear(); b.Clear();
        long difference = 0; int count = 0;
        int rows = model.FrameBytes / model.RowBytes, width = model.RowBytes / 4;
        var first = model.First; var second = model.Second;
        for (int y = 0; y < rows; y += Math.Max(1, rows / 36))
        for (int x = 0; x < width; x += Math.Max(1, width / 64))
        {
            int index = y * model.InputRowBytes + x * 4;
            int lumaA = (first[index] * 77 + first[index + 1] * 150 + first[index + 2] * 29) >> 8;
            int lumaB = (second[index] * 77 + second[index + 1] * 150 + second[index + 2] * 29) >> 8;
            a[lumaA >> 3]++; b[lumaB >> 3]++;
            difference += Math.Abs(lumaA - lumaB); count++;
        }
        int distance = 0;
        for (int i = 0; i < 32; i++) distance += Math.Abs(a[i] - b[i]);
        return difference > count * 255 * .55 || (difference > count * 255 * .18 && distance > count * .8);
    }
    private void WriteFrame(ReadOnlySpan<byte> pixels)
    {
        int height = model.FrameBytes / model.RowBytes;
        if (model.RowBytes == model.InputRowBytes) destination.Write(pixels[..model.FrameBytes]);
        else for (int y = 0; y < height; y++) destination.Write(pixels.Slice(y * model.InputRowBytes, model.RowBytes));
    }
    internal override void Complete()
    {
        if (_complete) return;
        if (_hasFrame)
            for (int i = 1; i < multiplier; i++) { ct.ThrowIfCancellationRequested(); WriteFrame(model.First); }
        _complete = true;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override bool CanWrite => !_complete;
    public override void Flush() => destination.Flush();
}

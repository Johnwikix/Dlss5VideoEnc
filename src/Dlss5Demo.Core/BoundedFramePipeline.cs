using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Dlss5Demo.Core;

/// <summary>FIFO stages with fixed buffer ownership. No buffers are reused before the writer finishes.</summary>
internal static class BoundedFramePipeline
{
    internal readonly record struct Frame<T>(T Buffer, long Index);
    internal delegate void Stage<T>(IEnumerable<Frame<T>> input, Action<Frame<T>> emit, CancellationToken token);

    internal static long Run<T>(IReadOnlyList<T> buffers, Func<T, long, bool> read,
        Action<T, long>[] stages, Action<T, long> write, Action abort, CancellationToken cancellationToken)
        => RunStages(buffers, read, stages.Select(stage => PerFrame(stage)).ToArray(), write, abort, cancellationToken);

    internal static Stage<T> PerFrame<T>(Action<T, long> action) => (input, emit, token) =>
    {
        foreach (var frame in input)
        {
            token.ThrowIfCancellationRequested();
            action(frame.Buffer, frame.Index);
            emit(frame);
        }
    };

    internal static long RunStages<T>(IReadOnlyList<T> buffers, Func<T, long, bool> read,
        Stage<T>[] stages, Action<T, long> write, Action abort, CancellationToken cancellationToken)
    {
        if (buffers.Count < 2) throw new ArgumentException("At least two frame buffers are required.", nameof(buffers));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var free = new BlockingCollection<T>(buffers.Count);
        foreach (var buffer in buffers) free.Add(buffer);
        var queues = new BlockingCollection<Frame<T>>[stages.Length + 1];
        for (var index = 0; index < queues.Length; index++) queues[index] = new(buffers.Count);
        ExceptionDispatchInfo? failure = null;
        long written = 0;
        using var unblock = stop.Token.Register(abort);
        Task Start(Action action) => Task.Factory.StartNew(() =>
        {
            try { action(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null) == null)
                    stop.Cancel();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var workers = new List<Task>(stages.Length + 2);
        try
        {
            workers.Add(Start(() =>
            {
                long index = 0;
                while (true)
                {
                    var buffer = free.Take(stop.Token);
                    if (!read(buffer, index)) break;
                    queues[0].Add(new(buffer, index++), stop.Token);
                }
                queues[0].CompleteAdding();
            }));
            for (var stage = 0; stage < stages.Length; stage++)
            {
                var index = stage;
                workers.Add(Start(() =>
                {
                    stages[index](queues[index].GetConsumingEnumerable(stop.Token),
                        frame => queues[index + 1].Add(frame, stop.Token), stop.Token);
                    queues[index + 1].CompleteAdding();
                }));
            }
            workers.Add(Start(() =>
            {
                foreach (var frame in queues[^1].GetConsumingEnumerable(stop.Token))
                {
                    write(frame.Buffer, frame.Index);
                    written++;
                    free.Add(frame.Buffer, stop.Token);
                }
            }));
            Task.WaitAll(workers.ToArray());
            failure?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
            return written;
        }
        finally
        {
            // Also join already-started stages if creating a later worker fails.
            if (workers.Any(worker => !worker.IsCompleted))
            {
                stop.Cancel();
                try { Task.WaitAll(workers.ToArray()); } catch { }
            }
            foreach (var queue in queues) queue.Dispose();
        }
    }
}

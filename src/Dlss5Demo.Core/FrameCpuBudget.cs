namespace Dlss5Demo.Core;

/// <summary>CPU input/output stages share the machine with decode/encode and GPU submissions.</summary>
internal static class FrameCpuBudget
{
    internal static ParallelOptions Options { get; } = new()
    {
        MaxDegreeOfParallelism = ReadWorkers("DLSS5_CPU_WORKERS", Math.Clamp(Environment.ProcessorCount / 3, 1, 8)),
    };
    internal static ParallelOptions HistoryOptions { get; } = new()
    {
        MaxDegreeOfParallelism = ReadWorkers("DLSS5_HISTORY_WORKERS", Math.Clamp(Environment.ProcessorCount * 2 / 3, 1, 16)),
    };
    private static int ReadWorkers(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var count) ? Math.Clamp(count, 1, Environment.ProcessorCount) : fallback;
}

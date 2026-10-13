using System.Diagnostics;
using System.Globalization;

namespace Dlss5Demo.Core;

/// <summary>Opt-in measurements; no per-frame strings or logging. Set DLSS5_PROFILE=1.</summary>
internal sealed class PipelineStageMetrics(string name, params string[] stages)
{
    internal bool Enabled { get; } = Environment.GetEnvironmentVariable("DLSS5_PROFILE") == "1";
    private readonly long[] _ticks = new long[stages.Length];
    internal long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;
    internal void Record(int stage, long start)
    {
        if (Enabled) _ticks[stage] += Stopwatch.GetTimestamp() - start;
    }
    internal void Report(long frames)
    {
        if (!Enabled || frames == 0) return;
        var values = new string[stages.Length];
        for (var index = 0; index < values.Length; index++)
            values[index] = $"{stages[index]}={(_ticks[index] * 1000.0 / Stopwatch.Frequency / frames).ToString("F2", CultureInfo.InvariantCulture)}ms";
        PipelineLog.Info($"{name} 每帧阶段耗时 ({frames}帧): {string.Join(", ", values)}");
    }
}

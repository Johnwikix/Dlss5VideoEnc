namespace Dlss5Demo.Core;

/// <summary>Rejects unsafe guidance before NR sees it, and prevents rejected pixels from reusing correction history.</summary>
internal static class MotionReliability
{
    internal static void Apply(float[] motion, bool[] rejected, float[]? inputErrors, bool validateColor)
    {
        if (motion.Length != checked(rejected.Length * 2) || inputErrors != null && inputErrors.Length != rejected.Length)
            throw new ArgumentException("Invalid motion confidence planes.");
        Parallel.For(0, (rejected.Length + 2047) / 2048, FrameCpuBudget.Options, block =>
        {
            var end = Math.Min(rejected.Length, (block + 1) * 2048);
            for (var pixel = block * 2048; pixel < end; pixel++)
            {
                var offset = pixel * 2;
                var invalid = rejected[pixel] || !float.IsFinite(motion[offset]) || !float.IsFinite(motion[offset + 1])
                    || validateColor && inputErrors != null && (!float.IsFinite(inputErrors[pixel]) || inputErrors[pixel] >= .12f);
                if (!invalid) continue;
                rejected[pixel] = true;
                // The native host converts NaN to half infinity; never use NaN as an invalid-vector marker.
                motion[offset] = motion[offset + 1] = 0;
                if (inputErrors != null) inputErrors[pixel] = float.PositiveInfinity;
            }
        });
    }
}

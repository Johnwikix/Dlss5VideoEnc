using System.Numerics;

namespace Dlss5Demo.Core;

/// <summary>
/// Stabilizes the signed NR correction, using the unmodified NR input to validate
/// history. Based on the correction-history approach documented by Magpie:
/// https://github.com/SAOG0721/Magpie/blob/experimental/src/Magpie.Core/DLSSNRTemporal.cpp
/// This history is never fed back into NR. Without optical flow, changed image
/// regions must reject history rather than mix unrelated screen-space pixels.
/// </summary>
internal sealed class DlssNrTemporalStabilizer
{
    private readonly int _width;
    private readonly int _height;
    private readonly bool _hdr;
    private readonly float _historyWeight;
    private Vector3[] _previousGuide;
    private Vector3[] _currentGuide;
    private Vector3[] _residualHistory;
    private Vector3[] _nextResidual;
    private readonly float[] _inputError;
    private readonly bool _useMotion;
    private float[]? _motion;
    private bool _hasHistory;
    private bool _reset;

    internal DlssNrTemporalStabilizer(int width, int height, bool hdr, double sourceFps, bool useMotion = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
        _hdr = hdr;
        _useMotion = useMotion;
        var pixels = checked(width * height);
        _previousGuide = new Vector3[pixels];
        _residualHistory = new Vector3[pixels];
        _currentGuide = useMotion ? new Vector3[pixels] : _previousGuide;
        _nextResidual = useMotion ? new Vector3[pixels] : _residualHistory;
        _inputError = new float[pixels];
        var seconds = double.IsFinite(sourceFps) && sourceFps > 0 ? 1 / sourceFps : 1 / 60.0;
        // Source-frame duration, not transcode throughput, determines the EMA.
        _historyWeight = seconds <= .25 ? (float)Math.Exp(-seconds / .12) : 0;
    }

    /// <summary>Call before NR so a cut resets both NR and correction history.</summary>
    internal unsafe bool PrepareFrame(byte* input, float[]? motion = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (motion != null && (!_useMotion || motion.Length != _inputError.Length * 2))
            throw new ArgumentException("Motion must contain one current-to-previous float2 per NR input pixel.", nameof(motion));
        _motion = motion;
        if (_height >= 256)
            Parallel.For(0, _height, row => PrepareRow(input, row));
        else
            for (var row = 0; row < _height; row++) PrepareRow(input, row);

        _reset = !_hasHistory || IsSceneCut();
        return _reset;
    }

    private unsafe void PrepareRow(byte* input, int row)
    {
        var end = (row + 1) * _width;
        for (var pixel = row * _width; pixel < end; pixel++)
        {
            var current = Guide(ReadColor(input, pixel));
            var previous = _motion != null && _hasHistory
                ? (PreviousPosition(pixel, pixel % _width, row, out var x, out var y)
                    ? Sample(_previousGuide, x, y) : new Vector3(float.NaN))
                : _previousGuide[pixel];
            _inputError[pixel] = IsFinite(current) && IsFinite(previous)
                ? MaxComponent(Vector3.Abs(current - previous)) : float.PositiveInfinity;
            _currentGuide[pixel] = current;
        }

    }

    private bool IsSceneCut()
    {
        // Require a large change spread over most of the image. A moving object,
        // subtitle or isolated flash must not reset otherwise useful NR history.
        var changedTiles = 0;
        for (var tileY = 0; tileY < 4; tileY++)
        {
            for (var tileX = 0; tileX < 4; tileX++)
            {
                var total = 0f;
                var changed = 0;
                for (var y = 0; y < 4; y++)
                {
                    var row = Math.Min(_height - 1, (tileY * 4 + y) * _height / 16);
                    for (var x = 0; x < 4; x++)
                    {
                        var column = Math.Min(_width - 1, (tileX * 4 + x) * _width / 16);
                        var error = _inputError[row * _width + column];
                        total += error;
                        if (error > .12f) changed++;
                    }
                }
                if (total / 16 > .18f && changed >= 12) changedTiles++;
            }
        }
        return changedTiles >= 12;
    }

    /// <summary>Runs after successful NR and before encoding or publishing preview.</summary>
    internal unsafe void Apply(byte* input, byte* output)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (input == output) throw new ArgumentException("NR input and output must be separate buffers.");

        // Rows are independent; source, input-error and old correction at other
        // pixels are read-only. Parallelism keeps full-resolution video practical.
        if (_height >= 256)
            Parallel.For(0, _height, row => ApplyRow(input, output, row));
        else
            for (var row = 0; row < _height; row++) ApplyRow(input, output, row);
        _hasHistory = true;
        if (_useMotion)
        {
            (_previousGuide, _currentGuide) = (_currentGuide, _previousGuide);
            (_residualHistory, _nextResidual) = (_nextResidual, _residualHistory);
        }
    }

    private unsafe void ApplyRow(byte* input, byte* output, int row)
    {
        var above = Math.Max(0, row - 1) * _width;
        var center = row * _width;
        var below = Math.Min(_height - 1, row + 1) * _width;
        for (var x = 0; x < _width; x++)
        {
            var pixel = row * _width + x;
            var original = ReadColor(input, pixel);
            var raw = ReadColor(output, pixel);
            if (!IsFinite(original) || !IsFinite(raw))
            {
                _nextResidual[pixel] = new Vector3(float.NaN);
                WriteColor(output, pixel, IsFinite(original) ? original : Vector3.Zero);
                continue;
            }

            var correction = raw - original;
            var confidence = !_reset && _historyWeight > 0 && _inputError[pixel] < .10f
                ? Confidence(x, above, center, below) : 0;
            var history = confidence > 0 && _motion != null
                ? (PreviousPosition(pixel, x, row, out var px, out var py)
                    ? Sample(_residualHistory, px, py) : new Vector3(float.NaN))
                : _residualHistory[pixel];
            if (confidence > 0 && IsFinite(history))
            {
                // A signed correction may turn off for one frame even when
                // the source is unchanged. Allow that history to decay; do
                // not reject it based only on NR's own unstable output.
                var margin = new Vector3(.02f) + confidence * Vector3.Abs(history);
                var safe = Vector3.Clamp(history, correction - margin, correction + margin);
                correction = Vector3.Lerp(correction, safe, _historyWeight * confidence);
                WriteColor(output, pixel, original + correction);
            }

            _nextResidual[pixel] = correction;
            // Reset/rejected pixels retain NR's exact bytes, including alpha.
        }
    }

    private float Confidence(int x, int above, int center, int below)
    {
        var left = Math.Max(0, x - 1);
        var right = Math.Min(_width - 1, x + 1);
        if (_motion != null && (!MotionMatches(center + x, center + left) ||
            !MotionMatches(center + x, center + right) || !MotionMatches(center + x, above + x) ||
            !MotionMatches(center + x, below + x))) return 0;
        var a = _inputError[above + left];
        var b = _inputError[above + x];
        var c = _inputError[above + right];
        var d = _inputError[center + left];
        var e = _inputError[center + x];
        var f = _inputError[center + right];
        var g = _inputError[below + left];
        var h = _inputError[below + x];
        var i = _inputError[below + right];
        var maximum = Math.Max(Math.Max(Math.Max(a, b), Math.Max(c, d)),
            Math.Max(Math.Max(e, f), Math.Max(Math.Max(g, h), i)));
        if (maximum >= .10f) return 0;
        return (1 - SmoothStep(.008f, .04f, (a + b + c + d + e + f + g + h + i) / 9)) *
               (1 - SmoothStep(.025f, .10f, maximum));
    }

    private bool MotionMatches(int pixel, int neighbor) =>
        Math.Abs(_motion![pixel * 2] - _motion[neighbor * 2]) <= 2 &&
        Math.Abs(_motion[pixel * 2 + 1] - _motion[neighbor * 2 + 1]) <= 2;

    private bool PreviousPosition(int pixel, int x, int y, out float previousX, out float previousY)
    {
        previousX = x + _motion![pixel * 2];
        previousY = y + _motion[pixel * 2 + 1];
        return float.IsFinite(previousX) && float.IsFinite(previousY) &&
            previousX >= 0 && previousY >= 0 && previousX <= _width - 1 && previousY <= _height - 1;
    }

    private Vector3 Sample(Vector3[] image, float x, float y)
    {
        var x0 = (int)x; var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, _width - 1); var y1 = Math.Min(y0 + 1, _height - 1);
        var a = Vector3.Lerp(image[y0 * _width + x0], image[y0 * _width + x1], x - x0);
        var b = Vector3.Lerp(image[y1 * _width + x0], image[y1 * _width + x1], x - x0);
        return Vector3.Lerp(a, b, y - y0);
    }

    private Vector3 Guide(Vector3 color) => _hdr ? color / (Vector3.One + Vector3.Abs(color)) : color;

    private unsafe Vector3 ReadColor(byte* frame, int pixel)
    {
        var offset = pixel * 4;
        if (_hdr)
        {
            var halves = (Half*)frame;
            return new Vector3((float)halves[offset], (float)halves[offset + 1], (float)halves[offset + 2]);
        }
        return new Vector3(frame[offset], frame[offset + 1], frame[offset + 2]) / 255;
    }

    private unsafe void WriteColor(byte* frame, int pixel, Vector3 color)
    {
        var offset = pixel * 4;
        if (_hdr)
        {
            var halves = (Half*)frame;
            color = Vector3.Clamp(color, new Vector3(-65504), new Vector3(65504));
            halves[offset] = (Half)color.X;
            halves[offset + 1] = (Half)color.Y;
            halves[offset + 2] = (Half)color.Z;
            if (!Half.IsFinite(halves[offset + 3])) halves[offset + 3] = (Half)1;
        }
        else
        {
            color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
            frame[offset] = (byte)MathF.Round(color.X * 255);
            frame[offset + 1] = (byte)MathF.Round(color.Y * 255);
            frame[offset + 2] = (byte)MathF.Round(color.Z * 255);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float MaxComponent(Vector3 value) => Math.Max(value.X, Math.Max(value.Y, value.Z));

    private static float SmoothStep(float low, float high, float value)
    {
        var t = Math.Clamp((value - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

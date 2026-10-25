using System.Numerics;
using System.Runtime.CompilerServices;

using Dlss5Demo.Core;
namespace Dlss5Demo.Tests.Reference;

/// <summary>Only the preparation stage owns this state; it can run ahead of NR/output history.</summary>
internal sealed class ReferenceInputHistory
{
    private readonly int _width, _height;
    private readonly bool _hdr, _useMotion;
    private Vector3[] _previous, _current;
    private float[] _errors = [];
    private float[]? _motion;
    private bool _hasPrevious;

    internal ReferenceInputHistory(int width, int height, bool hdr, bool useMotion)
    {
        _width = width; _height = height; _hdr = hdr; _useMotion = useMotion;
        _previous = new Vector3[checked(width * height)];
        _current = useMotion ? new Vector3[_previous.Length] : _previous;
    }

    internal unsafe bool Prepare(byte* input, float[]? motion, float[] errors)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (errors.Length != _previous.Length || motion != null && (!_useMotion || motion.Length != errors.Length * 2))
            throw new ArgumentException("Invalid NR input history buffers.");
        _motion = motion; _errors = errors;
        if (_height >= 256) Parallel.For(0, _height, FrameCpuBudget.Options, row => PrepareRow(input, row));
        else for (var row = 0; row < _height; row++) PrepareRow(input, row);
        var reset = !_hasPrevious || IsSceneCut();
        _hasPrevious = true;
        if (_useMotion) (_previous, _current) = (_current, _previous);
        return reset;
    }

    private unsafe void PrepareRow(byte* input, int row)
    {
        for (var x = 0; x < _width; x++)
        {
            var pixel = row * _width + x;
            var offset = pixel * 4;
            var color = _hdr
                ? new Vector3((float)((Half*)input)[offset], (float)((Half*)input)[offset + 1], (float)((Half*)input)[offset + 2])
                : new Vector3(input[offset], input[offset + 1], input[offset + 2]) / 255;
            var current = _hdr ? color / (Vector3.One + Vector3.Abs(color)) : color;
            var previous = _previous[pixel];
            if (_motion != null && _hasPrevious)
            {
                var px = x + _motion[pixel * 2];
                var py = row + _motion[pixel * 2 + 1];
                previous = float.IsFinite(px) && float.IsFinite(py) && px >= 0 && py >= 0 && px <= _width - 1 && py <= _height - 1
                    ? Sample(px, py) : new Vector3(float.NaN);
            }
            var difference = Vector3.Abs(current - previous);
            _errors[pixel] = IsFinite(current) && IsFinite(previous)
                ? Math.Max(difference.X, Math.Max(difference.Y, difference.Z)) : float.PositiveInfinity;
            _current[pixel] = current;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 Sample(float x, float y)
    {
        var x0 = (int)x; var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, _width - 1); var y1 = Math.Min(y0 + 1, _height - 1);
        var a = Vector3.Lerp(_previous[y0 * _width + x0], _previous[y0 * _width + x1], x - x0);
        var b = Vector3.Lerp(_previous[y1 * _width + x0], _previous[y1 * _width + x1], x - x0);
        return Vector3.Lerp(a, b, y - y0);
    }

    private bool IsSceneCut()
    {
        var changedTiles = 0;
        for (var tileY = 0; tileY < 4; tileY++)
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
                        var error = _errors[row * _width + column];
                        total += error;
                        if (error > .12f) changed++;
                    }
                }
                if (total / 16 > .18f && changed >= 12) changedTiles++;
            }
        return changedTiles >= 12;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}


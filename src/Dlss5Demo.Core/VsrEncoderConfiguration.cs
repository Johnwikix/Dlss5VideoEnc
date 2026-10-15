namespace Dlss5Demo.Core;

/// <summary>One worker-owned GPU encode session; the temporary NUT preserves packet PTS/DTS.</summary>
internal sealed record VsrEncoderConfiguration
{
    public required string OutputPath { get; init; }
    public required string Encoder { get; init; }
    public required string Options { get; init; }
    public int FpsNumerator { get; init; }
    public int FpsDenominator { get; init; }
    public int PreviewWidth { get; init; }
    public int PreviewHeight { get; init; }
}

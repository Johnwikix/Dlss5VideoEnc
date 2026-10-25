using Dlss5Demo.Core;

internal static class OutputScaleTests
{
    internal static void Identity()
    {
        foreach (var mode in new[] { ScaleMode.Exact, ScaleMode.FitWithin, ScaleMode.Width, ScaleMode.Height })
        {
            var options = new TranscodeOptions { Video = new VideoTranscodeOptions { ScaleMode = mode, ScaleWidth = 3840, ScaleHeight = 2160 } };
            var normalized = DlssVideoPipeline.NormalizeOutputScale(options, 3840, 2160);
            Require(normalized.Video.ScaleMode == ScaleMode.None, "Same-size scale must retain GPU paths.");
            Require(options.Video.ScaleMode == mode, "Normalization must not mutate user settings.");
            Require(DlssVideoPipeline.NormalizeOutputScale(options, 1920, 1080).Video.ScaleMode == mode, "Real scaling must be preserved.");
            var cropped = options with { Video = options.Video with { CropEnabled = true, CropLeft = 16 } };
            Require(DlssVideoPipeline.NormalizeOutputScale(cropped, 3840, 2160).Video.ScaleMode == mode, "Crop must precede scale.");
        }
        var fit = new TranscodeOptions { Video = new VideoTranscodeOptions { ScaleMode = ScaleMode.FitWithin, ScaleWidth = 3840, ScaleHeight = 2160 } };
        Require(DlssVideoPipeline.NormalizeOutputScale(fit, 3840, 1600).Video.ScaleMode == ScaleMode.FitWithin, "Aspect-ratio bounds cannot be discarded.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

using Dlss5Demo.Core;

namespace Dlss5Demo.App;

/// <summary>无界面批处理模式：与 GUI 共用同一 Core 管线，供自动化验收。</summary>
internal static class Autorun
{
    public static async Task<int> RunAsync(string[] args)
    {
        var input = args[1];
        var output = args[2];
        int? maxFrames = null;
        var twoPass = false;
        var enableNr = true;
        var enableInterpolation = false;
        var gpuFrames = true;
        var gpuEnhancement = true;
        var gpuTemporal = true;
        var interpolationMultiplier = 2;
        var interpolationBackend = FrameInterpolationBackend.NvidiaOpticalFlow;
        var rifeModel = RifeModel.Lite;
        var scale = 1;
        var decodeMode = VideoDecodeMode.Auto;
        var decodeDevice = 0;
        var hdrMode = "auto";
        string? nrRuntime = null;
        for (var index = 3; index < args.Length; index++)
        {
            if (args[index] == "--frames" && index + 1 < args.Length)
                maxFrames = int.Parse(args[++index]);
            else if (args[index] == "--two-pass")
                twoPass = true;
            else if (args[index] == "--nr" && index + 1 < args.Length)
                enableNr = args[++index] != "0";
            else if (args[index] == "--nr-runtime" && index + 1 < args.Length)
                nrRuntime = args[++index];
            else if (args[index] == "--sr" && index + 1 < args.Length)
                scale = RtxVideoSuperResolutionHost.NormalizeScale(int.Parse(args[++index]));
            else if (args[index] == "--interpolation-backend" && index + 1 < args.Length)
                interpolationBackend = FrameInterpolationBackends.Parse(args[++index]);
            else if (args[index] == "--rife-model" && index + 1 < args.Length)
                rifeModel = Enum.Parse<RifeModel>(args[++index], true);
            else if (args[index] == "--gpu-frames" && index + 1 < args.Length)
                gpuFrames = args[++index] != "0";
            else if (args[index] == "--gpu-temporal" && index + 1 < args.Length)
                gpuTemporal = args[++index] != "0";
            else if (args[index] == "--gpu-enhancement" && index + 1 < args.Length)
                gpuEnhancement = args[++index] != "0";
            else if (args[index] == "--interpolate" && index + 1 < args.Length)
                enableInterpolation = args[++index] != "0";
            else if (args[index] == "--interpolation-multiplier" && index + 1 < args.Length)
                interpolationMultiplier = int.Parse(args[++index]);
            else if (args[index] == "--decoder" && index + 1 < args.Length)
                decodeMode = VideoDecodeModes.Parse(args[++index]);
            else if (args[index] == "--decode-device" && index + 1 < args.Length)
                decodeDevice = int.Parse(args[++index]);
            else if (args[index] == "--hdr" && index + 1 < args.Length)
                hdrMode = args[++index].ToLowerInvariant();
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var progress = new Progress<TranscodeProgress>(update =>
            Console.WriteLine(
                $"进度 {update.FramesDone}/{update.TotalFrames} " +
                $"({update.FramesDone * 100 / Math.Max(update.TotalFrames, 1):F1}%) {update.Fps:F1} fps"));

        try
        {
            if (hdrMode is not ("auto" or "force" or "sdr")) throw new ArgumentException("--hdr 只能是 auto、force 或 sdr。");
            var profile = new VideoTranscodeOptions
            {
                VideoCodec = twoPass && hdrMode != "force" ? VideoCodec.H264 : VideoCodec.H265,
                HardwareBackend = twoPass ? HardwareBackend.Software : HardwareBackend.Nvidia,
                RateControl = twoPass ? RateControlMode.Bitrate : RateControlMode.Quality,
                VideoBitrateKbps = 2500,
                TwoPassEnabled = twoPass,
                ForceHdr = hdrMode == "force", PreserveHdr = hdrMode != "sdr",
            };
            var result = await DlssVideoPipeline.RunAsync(input, output, new TranscodeOptions
            {
                FfmpegPath = DemoPaths.FfmpegPath,
                HostDllPath = DemoPaths.HostDllPath,
                RuntimeDllPath = enableNr ? NrRuntimeCatalog.ResolveArgument(nrRuntime) : "",
                LogPath = DemoPaths.LogPath,
                MaxFrames = maxFrames,
                EnableNr = enableNr,
                DecodeMode = decodeMode,
                DecodeDeviceIndex = decodeDevice,
                EnableInterpolation = enableInterpolation,
                UseGpuFramePipeline = gpuFrames,
                UseGpuEnhancementPipeline = gpuEnhancement,
                Dlss = new DlssNrOptions { UseGpuTemporalProcessing = gpuTemporal },
                InterpolationMultiplier = interpolationMultiplier,
                InterpolationBackend = interpolationBackend,
                InterpolationModel = rifeModel,
                SuperResolutionScale = scale,
                Video = profile,
                VideoEncoder = VideoCodecNames.Encoder(profile),
            }, progress);
            Console.WriteLine(
                $"完成: {result.FramesProcessed} 帧，平均 {result.AverageFps:F1} fps，" +
                $"输出 {result.OutputBytes / (1024.0 * 1024.0):F1} MiB → {result.OutputPath}");
            if (enableInterpolation) Console.WriteLine($"补帧输出: {result.FramesProcessed * interpolationMultiplier} 帧（{interpolationMultiplier}×）");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
    }
}

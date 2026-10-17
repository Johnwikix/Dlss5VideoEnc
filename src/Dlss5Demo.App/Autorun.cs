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
        var interpolationMultiplier = 2;
        var interpolationBackend = FrameInterpolationBackend.NvidiaOpticalFlow;
        var rifeModel = RifeModel.Lite;
        var scale = 1;
        var decodeMode = VideoDecodeMode.Auto;
        var decodeDevice = 0;
        for (var index = 3; index < args.Length; index++)
        {
            if (args[index] == "--frames" && index + 1 < args.Length)
                maxFrames = int.Parse(args[++index]);
            else if (args[index] == "--two-pass")
                twoPass = true;
            else if (args[index] == "--nr" && index + 1 < args.Length)
                enableNr = args[++index] != "0";
            else if (args[index] == "--sr" && index + 1 < args.Length)
                scale = RtxVideoSuperResolutionHost.NormalizeScale(int.Parse(args[++index]));
            else if (args[index] == "--interpolation-backend" && index + 1 < args.Length)
                interpolationBackend = FrameInterpolationBackends.Parse(args[++index]);
            else if (args[index] == "--rife-model" && index + 1 < args.Length)
                rifeModel = Enum.Parse<RifeModel>(args[++index], true);
            else if (args[index] == "--gpu-frames" && index + 1 < args.Length)
                gpuFrames = args[++index] != "0";
            else if (args[index] == "--interpolate" && index + 1 < args.Length)
                enableInterpolation = args[++index] != "0";
            else if (args[index] == "--interpolation-multiplier" && index + 1 < args.Length)
                interpolationMultiplier = int.Parse(args[++index]);
            else if (args[index] == "--decoder" && index + 1 < args.Length)
                decodeMode = VideoDecodeModes.Parse(args[++index]);
            else if (args[index] == "--decode-device" && index + 1 < args.Length)
                decodeDevice = int.Parse(args[++index]);
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var progress = new Progress<TranscodeProgress>(update =>
            Console.WriteLine(
                $"进度 {update.FramesDone}/{update.TotalFrames} " +
                $"({update.FramesDone * 100 / Math.Max(update.TotalFrames, 1):F1}%) {update.Fps:F1} fps"));

        try
        {
            var profile = new VideoTranscodeOptions
            {
                VideoCodec = twoPass ? VideoCodec.H264 : VideoCodec.H265,
                HardwareBackend = twoPass ? HardwareBackend.Software : HardwareBackend.Nvidia,
                RateControl = twoPass ? RateControlMode.Bitrate : RateControlMode.Quality,
                VideoBitrateKbps = 2500,
                TwoPassEnabled = twoPass,
            };
            var result = await DlssVideoPipeline.RunAsync(input, output, new TranscodeOptions
            {
                FfmpegPath = DemoPaths.FfmpegPath,
                HostDllPath = DemoPaths.HostDllPath,
                RuntimeDllPath = DemoPaths.RuntimeDllPath,
                LogPath = DemoPaths.LogPath,
                MaxFrames = maxFrames,
                EnableNr = enableNr,
                DecodeMode = decodeMode,
                DecodeDeviceIndex = decodeDevice,
                EnableInterpolation = enableInterpolation,
                UseGpuFramePipeline = gpuFrames,
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

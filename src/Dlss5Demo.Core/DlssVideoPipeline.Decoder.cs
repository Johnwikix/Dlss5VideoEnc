using System.Diagnostics;

namespace Dlss5Demo.Core;

public static partial class DlssVideoPipeline
{
    private static string? ValidateDecodeOptions(TranscodeOptions options, VideoInfo video)
    {
        if (!Enum.IsDefined(options.DecodeMode)) throw new ArgumentOutOfRangeException(nameof(options.DecodeMode));
        if (options.DecodeDeviceIndex < 0) throw new ArgumentOutOfRangeException(nameof(options.DecodeDeviceIndex));
        var surface = CudaDownloadSurface(video);
        if (options.DecodeMode == VideoDecodeMode.Nvdec && surface == null)
            throw new NotSupportedException($"NVDEC 尚未接入 {video.Codec} / {video.PixelFormat}，可选择自动或软件解码。");
        return surface;
    }

    internal static string? CudaDownloadSurface(VideoInfo video)
    {
        if (video.Codec is not ("h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg1video" or "mpeg2video" or "vc1" or "wmv3" or "mjpeg"))
            return null;
        return video.PixelFormat switch
        {
            "yuv420p" or "yuvj420p" or "nv12" => "nv12",
            "yuv420p10le" or "p010le" => "p010le",
            "yuv420p12le" or "p016le" => "p016le",
            "yuv444p" or "yuvj444p" => "yuv444p",
            "yuv444p10le" or "yuv444p12le" or "yuv444p16le" => "yuv444p16le",
            _ => null,
        };
    }

    /// <summary>
    /// Owns the actual decoder, including first-frame negotiation and retry.
    /// Retry occurs before publishing a frame, preserving NR history and audio.
    /// </summary>
    internal sealed class FrameDecoder : IAsyncDisposable
    {
        private readonly string _input;
        private readonly TranscodeOptions _options;
        private readonly VideoInfo _video;
        private readonly bool _hdr;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _cancellation;
        private readonly object _gate = new();
        private readonly string? _surface;
        private CommandDrain _command;
        private bool _nvdec, _aborted;
        private long _frames;

        internal FrameDecoder(string input, TranscodeOptions options, VideoInfo video, bool hdr, CancellationToken token)
        {
            _input = input;
            _options = options;
            _video = video;
            _hdr = hdr;
            _token = token;
            _surface = ValidateDecodeOptions(options, video);
            _nvdec = options.DecodeMode != VideoDecodeMode.Software && _surface != null;
            token.ThrowIfCancellationRequested();
            _command = Start();
            // This callback also covers a cancellation between disposing the
            // failed hardware process and starting its software replacement.
            _cancellation = token.Register(Abort);
            PipelineLog.Info(_nvdec
                ? $"解码请求: NVDEC / CUDA {_options.DecodeDeviceIndex}，{video.Codec} / {video.PixelFormat} → {_surface} → {(hdr ? "RGBA16" : "RGBA8")}；等待实际首帧确认。"
                : $"解码方式: 软件解码（{(options.DecodeMode == VideoDecodeMode.Software ? "显式指定" : $"未接入 {video.Codec}/{video.PixelFormat} 的 NVDEC 表面")}）。");
        }

        internal Process Process => _command.Process;
        internal string ErrorTail => _command.ErrorTail;

        private CommandDrain Start() => new("ffmpeg-decode", _options.FfmpegPath,
            DecoderArguments(_input, _options.MaxFrames, _video, _hdr, _options.EnableInterpolation,
                _nvdec ? _surface : null, _options.DecodeDeviceIndex), redirectStdout: true);

        internal bool ReadFrame(Span<byte> destination)
        {
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                bool available;
                IOException? readFailure = null;
                try { available = Fill(_command.Process.StandardOutput.BaseStream, destination); }
                catch (IOException readError) when (_nvdec && _frames == 0)
                { available = false; readFailure = readError; }
                if (available)
                {
                    if (_frames++ == 0)
                        PipelineLog.Info(_nvdec
                            ? "NVDEC 已确认: 首帧经过 CUDA 硬件表面回读；色彩转换继续沿用现有路径。"
                            : "软件解码已确认: 已收到完整首帧。");
                    return true;
                }
                _token.ThrowIfCancellationRequested();
                if (!_command.Process.WaitForExit(30_000))
                    throw new TimeoutException("ffmpeg 解码进程停止输出后未能退出。");
                _command.WaitForExitAsync(_token).GetAwaiter().GetResult();
                if (_command.Process.ExitCode == 0 && readFailure == null) return false;
                var error = (readFailure == null ? "" : readFailure.Message + "\n") + _command.ErrorTail;
                if (!_nvdec || _frames != 0 || _options.DecodeMode != VideoDecodeMode.Auto)
                    throw new InvalidOperationException($"{(_nvdec ? "NVDEC" : "软件")} 解码失败（已输出 {_frames} 帧）：\n{error}");

                // Do not combine hardware and software decoding after a frame
                // has entered temporal NR or interpolation history.
                _command.DisposeAsync().AsTask().GetAwaiter().GetResult();
                PipelineLog.Info("NVDEC 首帧初始化失败，重新从头软件解码；原因: " + error);
                lock (_gate)
                {
                    _token.ThrowIfCancellationRequested();
                    if (_aborted) throw new OperationCanceledException("解码已停止。", _token);
                    _nvdec = false;
                    _command = Start();
                }
            }
        }

        internal void Abort()
        {
            lock (_gate)
            {
                _aborted = true;
                TryKill(_command.Process);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Dispose();
            await _command.DisposeAsync().ConfigureAwait(false);
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

public static unsafe partial class DlssVideoPipeline
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
    /// 进程内解码器：avformat 解复用 → avcodec 解码（软件或 NVDEC 回读）→ avfilter 转换为
    /// RGBA8 / RGBA64LE 半浮点帧。NVDEC 首帧失败时自动模式从头回退软件解码，
    /// 不在已出帧后混用两种解码（保持时间性 NR/补帧历史一致）。
    /// </summary>
    internal sealed class FrameDecoder : IAsyncDisposable
    {
        private readonly string _input;
        private readonly TranscodeOptions _options;
        private readonly VideoInfo _video;
        private readonly bool _hdr;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _cancellation;
        private readonly object _gate = new();
        private readonly int? _limit;
        private string? _surface;
        private bool _nvdec, _aborted;
        private long _frames;

        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private AVFrame* _frame, _sw, _out;
        private AVPacket* _packet;
        private AVFilterGraph* _graph;
        private AVFilterContext* _src, _sink;
        private AVBufferRef* _hwDevice;
        private int _index;
        private bool _graphReady, _demuxEof;
        private AVColorPrimaries _primaries;
        private AVColorTransferCharacteristic _transfer;
        private AVColorSpace _space;
        private AVColorRange _range;
        private GCHandle _interrupt;
        private readonly ReadCancellation _state;

        private sealed class ReadCancellation(CancellationToken token)
        {
            internal readonly CancellationToken Token = token;
            internal int Aborted;
        }

        private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
        {
            if (opaque == null) return 0;
            var state = (ReadCancellation)GCHandle.FromIntPtr((nint)opaque).Target!;
            return state.Token.IsCancellationRequested || Volatile.Read(ref state.Aborted) != 0 ? 1 : 0;
        };

        private static readonly AVCodecContext_get_format CudaFormat = static (context, formats) =>
        {
            for (var item = formats; *item != AVPixelFormat.AV_PIX_FMT_NONE; item++)
                if (*item == AVPixelFormat.AV_PIX_FMT_CUDA) return *item;
            return ffmpeg.avcodec_default_get_format(context, formats);
        };

        internal FrameDecoder(string input, TranscodeOptions options, VideoInfo video, bool hdr, CancellationToken token)
        {
            _input = input;
            _options = options;
            _video = video;
            _hdr = hdr;
            _token = token;
            _state = new(token);
            _surface = ValidateDecodeOptions(options, video);
            _nvdec = options.DecodeMode != VideoDecodeMode.Software && _surface != null;
            _limit = options.MaxFrames;
            // This callback also covers a cancellation between disposing the
            // failed hardware decoder and starting its software replacement.
            _cancellation = token.Register(Abort);
            try { Open(_nvdec); }
            catch (Exception error) when (_nvdec && options.DecodeMode == VideoDecodeMode.Auto
                && error is not OperationCanceledException)
            {
                PipelineLog.Info("NVDEC 初始化失败，从头软件解码；原因: " + error.Message.Split('\n')[0]);
                _nvdec = false;
                Open(false);
            }
            PipelineLog.Info(_nvdec
                ? $"解码请求: NVDEC / CUDA {options.DecodeDeviceIndex}，{video.Codec} / {video.PixelFormat} → {_surface} → {(hdr ? "RGBA16" : "RGBA8")}；等待实际首帧确认。"
                : $"解码方式: 软件解码（{(options.DecodeMode == VideoDecodeMode.Software ? "显式指定" : $"未接入 {video.Codec}/{video.PixelFormat} 的 NVDEC 表面")}）。");
        }

        private void Open(bool nvdec)
        {
            AVFormatContext* format = null;
            AVBufferRef* device = null;
            try
            {
                _interrupt = GCHandle.Alloc(_state);
                format = ffmpeg.avformat_alloc_context();
                if (format == null) throw new OutOfMemoryException();
                format->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = (void*)GCHandle.ToIntPtr(_interrupt) };
                NativeVideo.Check(ffmpeg.avformat_open_input(&format, _input, null, null), "打开输入");
                _format = format;
                NativeVideo.Check(ffmpeg.avformat_find_stream_info(_format, null), "读取流信息");
                _index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                NativeVideo.Check(_index, "查找视频流");
                var stream = _format->streams[_index];
                var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                if (codec == null) throw new NotSupportedException($"解码器不存在：{stream->codecpar->codec_id}。");
                _codec = ffmpeg.avcodec_alloc_context3(codec);
                _frame = ffmpeg.av_frame_alloc();
                _out = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
                if (_codec == null || _frame == null || _out == null || _packet == null) throw new OutOfMemoryException();
                NativeVideo.Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "解码参数");
                var par = stream->codecpar;
                _primaries = par->color_primaries;
                _transfer = par->color_trc;
                _space = par->color_space;
                _range = par->color_range;
                if (nvdec)
                {
                    NativeVideo.Check(ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                        _options.DecodeDeviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 0), "CUDA 解码设备");
                    _hwDevice = device;
                    device = null;
                    _codec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
                    if (_codec->hw_device_ctx == null) throw new OutOfMemoryException();
                    _codec->get_format = CudaFormat;
                    _codec->thread_count = 1;
                    _codec->extra_hw_frames = 4;
                }
                else _codec->thread_count = 0;
                NativeVideo.Check(ffmpeg.avcodec_open2(_codec, codec, null), nvdec ? "NVDEC 打开" : "软件解码器打开");
            }
            catch
            {
                if (device != null) ffmpeg.av_buffer_unref(&device);
                ReleaseNative();
                throw;
            }
        }

        internal bool ReadFrame(Span<byte> destination)
        {
            while (true)
            {
                try { return ReadOnce(destination); }
                catch (Exception ex) when (_nvdec && _frames == 0
                    && ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    if (_options.DecodeMode != VideoDecodeMode.Auto)
                        throw new InvalidOperationException($"NVDEC 解码失败：{ex.Message}", ex);
                    PipelineLog.Info("NVDEC 首帧失败，重新从头软件解码；原因: " + ex.Message.Split('\n')[0]);
                    lock (_gate)
                    {
                        _token.ThrowIfCancellationRequested();
                        if (_aborted) throw new OperationCanceledException("解码已停止。", _token);
                        ReleaseNative();
                        _nvdec = false;
                        Open(false);
                    }
                }
            }
        }

        private bool ReadOnce(Span<byte> destination)
        {
            _token.ThrowIfCancellationRequested();
            if (_aborted) throw new OperationCanceledException("解码已停止。", _token);
            if (_limit is { } limit && _frames >= limit) return false;
            while (true)
            {
                var result = ffmpeg.avcodec_receive_frame(_codec, _frame);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    if (_graphReady) NativeVideo.Check(ffmpeg.av_buffersrc_add_frame(_src, null), "滤镜 EOF");
                    while (_graphReady && ffmpeg.av_buffersink_get_frame(_sink, _out) >= 0)
                    {
                        try { CopyFrameTo(destination); }
                        finally { ffmpeg.av_frame_unref(_out); }
                        _frames++;
                        return true;
                    }
                    return false;
                }
                if (result == NativeVideo.Again)
                {
                    if (_demuxEof) throw new InvalidDataException("解码排空停滞。");
                    DemuxPacket();
                    continue;
                }
                if (result < 0) NativeVideo.Check(result, "解码输出");

                try
                {
                    AVFrame* source;
                    if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA)
                    {
                        if (!_nvdec) throw new InvalidDataException("软件解码意外返回 CUDA 帧。");
                        if (_frame->hw_frames_ctx == null) throw new InvalidDataException("NVDEC 帧缺少硬件帧池。");
                        source = DownloadHardwareFrame();
                    }
                    else
                    {
                        // hwdownload 只接受硬件帧：解码器静默回退软件输出视为 NVDEC 协商失败。
                        if (_nvdec) throw new InvalidDataException("NVDEC 未协商 CUDA 输出。");
                        source = _frame;
                    }
                    EnsureGraph(source);
                    ApplyColorTags(source);
                    if (_frames == 0)
                        PipelineLog.Info(_nvdec ? "NVDEC 已确认: 首帧经过 CUDA 硬件表面回读。" : "软件解码已确认: 已收到完整首帧。");
                    NativeVideo.Check(ffmpeg.av_buffersrc_add_frame_flags(_src, source,
                        (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "滤镜提交");
                    while (ffmpeg.av_buffersink_get_frame(_sink, _out) >= 0)
                    {
                        try { CopyFrameTo(destination); }
                        finally { ffmpeg.av_frame_unref(_out); }
                        _frames++;
                        return true;
                    }
                    // 转换链无时间性滤镜，正常情况下一次提交必有一帧输出；继续取下一帧。
                }
                finally { ffmpeg.av_frame_unref(_frame); }
            }
        }

        private AVFrame* DownloadHardwareFrame()
        {
            var pool = (AVHWFramesContext*)_frame->hw_frames_ctx->data;
            if (_sw == null)
            {
                _sw = ffmpeg.av_frame_alloc();
                if (_sw == null) throw new OutOfMemoryException();
            }
            ffmpeg.av_frame_unref(_sw);
            _sw->format = (int)pool->sw_format;
            _sw->width = _frame->width;
            _sw->height = _frame->height;
            NativeVideo.Check(ffmpeg.av_hwframe_transfer_data(_sw, _frame, 0), "NVDEC 帧回读");
            var actual = ffmpeg.av_get_pix_fmt_name(pool->sw_format);
            if (!string.IsNullOrEmpty(actual)) _surface = actual;
            return _sw;
        }

        private void EnsureGraph(AVFrame* frame)
        {
            if (_graphReady) return;
            string chain;
            if (_hdr)
            {
                var transfer = string.IsNullOrWhiteSpace(_video.ColorTransfer) ? "smpte2084" : _video.ColorTransfer;
                var primaries = string.IsNullOrWhiteSpace(_video.ColorPrimaries) ? "bt2020" : _video.ColorPrimaries;
                var matrix = string.IsNullOrWhiteSpace(_video.ColorSpace) ? "bt2020nc" : _video.ColorSpace;
                var range = _video.ColorRange is "pc" or "jpeg" or "full" ? "full" : "limited";
                chain = $"zscale=matrixin={matrix}:matrix={matrix}:transferin={transfer}:transfer={transfer}" +
                        $":primariesin={primaries}:primaries={primaries}:rangein={range}:range=full,format=gbrp16le,format=rgba64le";
            }
            else if (_nvdec)
            {
                // 与旧 exe 管线一致：先恢复软件解码的像素布局再做 RGB 转换，
                // NV12→RGBA 与 YUV420P→RGBA 的色度上采样不同（影响 PSNR 对齐）。
                chain = $"format={_surface},format={_video.PixelFormat},format=rgba";
            }
            else chain = "format=rgba";

            var rate = ParseRate(_video.FrameRate);
            _graph = ffmpeg.avfilter_graph_alloc();
            if (_graph == null) throw new OutOfMemoryException();
            _src = CreateBufferSource(_graph, frame->width, frame->height, (AVPixelFormat)frame->format,
                new() { num = 1, den = 90000 }, rate);
            _sink = CreateBufferSink(_graph);
            BindGraph(_graph, _src, _sink, chain);
            _graphReady = true;
        }

        private void ApplyColorTags(AVFrame* frame)
        {
            if (_primaries != AVColorPrimaries.AVCOL_PRI_UNSPECIFIED) frame->color_primaries = _primaries;
            if (_transfer != AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED) frame->color_trc = _transfer;
            if (_space != AVColorSpace.AVCOL_SPC_UNSPECIFIED) frame->colorspace = _space;
            if (_range != AVColorRange.AVCOL_RANGE_UNSPECIFIED) frame->color_range = _range;
        }

        private void CopyFrameTo(Span<byte> destination)
        {
            var bytesPerRow = _hdr ? _out->width * 8 : _out->width * 4;
            if (destination.Length < (long)_out->height * bytesPerRow)
                throw new ArgumentException("目标缓冲区小于解码帧。");
            var source = (byte*)_out->data[0];
            fixed (byte* target = destination)
            {
                for (var y = 0; y < _out->height; y++)
                    Buffer.MemoryCopy(source + y * _out->linesize[0], target + y * bytesPerRow, bytesPerRow, bytesPerRow);
            }
        }

        private void DemuxPacket()
        {
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                var result = ffmpeg.av_read_frame(_format, _packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    NativeVideo.Check(ffmpeg.avcodec_send_packet(_codec, null), "解码 EOF");
                    _demuxEof = true;
                    return;
                }
                if (result < 0) NativeVideo.Check(result, "读包");
                try
                {
                    if (_packet->stream_index == _index)
                    {
                        NativeVideo.Check(ffmpeg.avcodec_send_packet(_codec, _packet), "解码提交");
                        return;
                    }
                }
                finally { ffmpeg.av_packet_unref(_packet); }
            }
        }

        internal void Abort()
        {
            // 原生 demux/IO 只认中断回调；托管检查走 _aborted。
            Volatile.Write(ref _state.Aborted, 1);
            lock (_gate) { _aborted = true; }
        }

        private void ReleaseNative()
        {
            var graph = _graph; var format = _format; var codec = _codec;
            var frame = _frame; var sw = _sw; var output = _out; var packet = _packet;
            var device = _hwDevice;
            _graph = null; _src = null; _sink = null;
            _format = null; _codec = null;
            _frame = null; _sw = null; _out = null; _packet = null; _hwDevice = null;
            _graphReady = false; _demuxEof = false;
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_frame_free(&sw);
            ffmpeg.av_frame_free(&output);
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            ffmpeg.avcodec_free_context(&codec);
            ffmpeg.av_buffer_unref(&device);
            ffmpeg.avformat_close_input(&format);
            if (_interrupt.IsAllocated) _interrupt.Free();
        }

        public ValueTask DisposeAsync()
        {
            _cancellation.Dispose();
            ReleaseNative();
            return ValueTask.CompletedTask;
        }
    }
}

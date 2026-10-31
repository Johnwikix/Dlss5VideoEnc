using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

/// <summary>
/// 进程内编码与封装：原 ffmpeg.exe 子进程的等价实现（libavfilter/libavcodec/libavformat）。
/// rawvideo 帧 → 滤镜链 → 编码器 → 封装 + 源文件音轨（直拷或重编码）。
/// </summary>
public static unsafe partial class DlssVideoPipeline
{
    private static bool TrySetOpt(void* obj, string name, string value)
        => obj != null && ffmpeg.av_opt_set(obj, name, value, 0) >= 0;

    private static string? PtrToString(byte* pointer)
        => pointer == null ? null : Marshal.PtrToStringUTF8((nint)pointer);

    private static AVRational ParseRate(string text)
    {
        var parts = (text ?? "").Split('/');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var num) && num > 0
            && int.TryParse(parts[1], out var den) && den > 0)
            return new() { num = num, den = den };
        return int.TryParse(parts[0], out var single) && single > 0
            ? new() { num = single, den = 1 }
            : new() { num = 30, den = 1 };
    }

    private static AVColorPrimaries PrimariesFromName(string name) => name switch
    {
        "bt709" => AVColorPrimaries.AVCOL_PRI_BT709,
        "bt470m" => AVColorPrimaries.AVCOL_PRI_BT470M,
        "bt470bg" => AVColorPrimaries.AVCOL_PRI_BT470BG,
        "smpte170m" => AVColorPrimaries.AVCOL_PRI_SMPTE170M,
        "smpte240m" => AVColorPrimaries.AVCOL_PRI_SMPTE240M,
        "film" => AVColorPrimaries.AVCOL_PRI_FILM,
        "bt2020" => AVColorPrimaries.AVCOL_PRI_BT2020,
        "smpte428" => AVColorPrimaries.AVCOL_PRI_SMPTE428,
        "smpte431" => AVColorPrimaries.AVCOL_PRI_SMPTE431,
        "smpte432" => AVColorPrimaries.AVCOL_PRI_SMPTE432,
        "jedec-p22" => AVColorPrimaries.AVCOL_PRI_JEDEC_P22,
        _ => AVColorPrimaries.AVCOL_PRI_UNSPECIFIED,
    };

    private static AVColorTransferCharacteristic TransferFromName(string name) => name switch
    {
        "bt709" => AVColorTransferCharacteristic.AVCOL_TRC_BT709,
        "gamma22" => AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22,
        "gamma28" => AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28,
        "smpte170m" => AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M,
        "smpte240m" => AVColorTransferCharacteristic.AVCOL_TRC_SMPTE240M,
        "linear" => AVColorTransferCharacteristic.AVCOL_TRC_LINEAR,
        "log100" => AVColorTransferCharacteristic.AVCOL_TRC_LOG,
        "iec61966-2-4" => AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_4,
        "bt1361e" => AVColorTransferCharacteristic.AVCOL_TRC_BT1361_ECG,
        "iec61966-2-1" => AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
        "bt2020-10" => AVColorTransferCharacteristic.AVCOL_TRC_BT2020_10,
        "bt2020-12" => AVColorTransferCharacteristic.AVCOL_TRC_BT2020_12,
        "smpte2084" => AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
        "arib-std-b67" => AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
        _ => AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED,
    };

    private static AVColorSpace ColorSpaceFromName(string name) => name switch
    {
        "rgb" or "gbr" => AVColorSpace.AVCOL_SPC_RGB,
        "bt709" => AVColorSpace.AVCOL_SPC_BT709,
        "fcc" => AVColorSpace.AVCOL_SPC_FCC,
        "bt470bg" => AVColorSpace.AVCOL_SPC_BT470BG,
        "smpte170m" => AVColorSpace.AVCOL_SPC_SMPTE170M,
        "smpte240m" => AVColorSpace.AVCOL_SPC_SMPTE240M,
        "ycgco" => AVColorSpace.AVCOL_SPC_YCGCO,
        "bt2020nc" or "bt2020_ncl" => AVColorSpace.AVCOL_SPC_BT2020_NCL,
        "bt2020c" or "bt2020_cl" => AVColorSpace.AVCOL_SPC_BT2020_CL,
        "smpte2085" => AVColorSpace.AVCOL_SPC_SMPTE2085,
        _ => AVColorSpace.AVCOL_SPC_UNSPECIFIED,
    };

    private static AVColorRange ColorRangeFromName(string name) => name switch
    {
        "tv" or "mpeg" or "limited" => AVColorRange.AVCOL_RANGE_MPEG,
        "pc" or "jpeg" or "full" => AVColorRange.AVCOL_RANGE_JPEG,
        _ => AVColorRange.AVCOL_RANGE_UNSPECIFIED,
    };

    private static string EncoderName(TranscodeOptions options)
        => string.IsNullOrWhiteSpace(options.VideoEncoder) ? VideoCodecNames.Encoder(options.Video) : options.VideoEncoder;

    /// <summary>目标像素格式（等价旧 VideoEncoderArguments 的 -pix_fmt 选择）。</summary>
    private static string TargetPixelFormat(TranscodeOptions options, VideoInfo video, bool hdr, bool mappedHdr)
    {
        var encoder = EncoderName(options);
        if (encoder == "gif") return "pal8";
        var hdrFormat = !mappedHdr || encoder.EndsWith("_nvenc", StringComparison.Ordinal)
            || encoder.EndsWith("_qsv", StringComparison.Ordinal) || encoder.EndsWith("_amf", StringComparison.Ordinal)
            ? "p010le" : "yuv420p10le";
        return hdr && options.Video.VideoCodec != VideoCodec.H264 ? hdrFormat : "yuv420p";
    }

    private static AVFilterContext* CreateBufferSource(AVFilterGraph* graph, int width, int height,
        AVPixelFormat format, AVRational timeBase, AVRational frameRate)
    {
        var filter = ffmpeg.avfilter_get_by_name("buffer");
        if (filter == null) throw new InvalidOperationException("avfilter 缺少 buffer 源。");
        var context = ffmpeg.avfilter_graph_alloc_filter(graph, filter, "in");
        if (context == null) throw new OutOfMemoryException();
        var args = $"video_size={Math.Max(width, 1)}x{Math.Max(height, 1)}:pix_fmt={(int)format}" +
                   $":time_base={timeBase.num}/{timeBase.den}:pixel_aspect=1/1" +
                   $":frame_rate={frameRate.num}/{frameRate.den}";
        if (ffmpeg.avfilter_init_str(context, args) < 0)
            throw new InvalidOperationException($"buffer 源初始化失败：{args}");
        return context;
    }

    private static AVFilterContext* CreateBufferSink(AVFilterGraph* graph)
    {
        var filter = ffmpeg.avfilter_get_by_name("buffersink");
        if (context_null(filter)) throw new InvalidOperationException("avfilter 缺少 buffersink。");
        var context = ffmpeg.avfilter_graph_alloc_filter(graph, filter, "out");
        if (context_null(context)) throw new OutOfMemoryException();
        if (ffmpeg.avfilter_init_str(context, null) < 0)
            throw new InvalidOperationException("buffersink 初始化失败。");
        return context;

        static bool context_null(void* p) => p == null;
    }

    /// <summary>把 buffersrc/buffersink 绑定到滤镜串两端并配置（ffmpeg 官方 filtering 示例的绑定方式）。</summary>
    private static void BindGraph(AVFilterGraph* graph, AVFilterContext* source, AVFilterContext* sink, string description)
    {
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        if (outputs == null || inputs == null) throw new OutOfMemoryException();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            outputs->pad_idx = 0;
            outputs->next = null;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            inputs->pad_idx = 0;
            inputs->next = null;
            var result = ffmpeg.avfilter_graph_parse_ptr(graph, description, &inputs, &outputs, null);
            if (result < 0) NativeVideo.Check(result, "滤镜图解析（" + description + "）");
            NativeVideo.Check(ffmpeg.avfilter_graph_config(graph, null), "滤镜图配置");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
    }

    /// <summary>配置视频编码器（等价旧 VideoEncoderArguments 的选项映射 + 两通 flags）。</summary>
    private static void ConfigureVideoEncoder(AVCodecContext* encoder, AVCodec* codec, TranscodeOptions options,
        VideoInfo video, VideoInfo outputVideo, AVFilterLink* sinkLink, AVFormatContext* format,
        AVRational frameRate, bool hdr, bool mappedHdr, int pass, string? passLog)
    {
        var name = EncoderName(options);
        var profile = options.Video;
        var quality = Math.Clamp(profile.Quality > 0 ? profile.Quality : options.Quality, 0, 51);

        encoder->thread_count = 0;
        encoder->width = sinkLink->w;
        encoder->height = sinkLink->h;
        var sar = sinkLink->sample_aspect_ratio;
        encoder->sample_aspect_ratio = sar.num <= 0 || sar.den <= 0 ? new() { num = 1, den = 1 } : sar;
        encoder->framerate = frameRate;
        encoder->time_base = new() { num = frameRate.den, den = frameRate.num };
        encoder->pix_fmt = name == "gif"
            ? AVPixelFormat.AV_PIX_FMT_PAL8
            : ffmpeg.av_get_pix_fmt(TargetPixelFormat(options, video, hdr, mappedHdr));

        if ((format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (name.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            TrySetOpt(encoder->priv_data, "preset", string.IsNullOrWhiteSpace(profile.EncoderPreset) ? "p5" : profile.EncoderPreset);
            TrySetOpt(encoder->priv_data, "tune", "hq");
            TrySetOpt(encoder->priv_data, "rc", profile.RateControl == RateControlMode.Bitrate ? "vbr" : "constqp");
            if (profile.RateControl == RateControlMode.Bitrate)
                encoder->bit_rate = Math.Max(100, profile.VideoBitrateKbps) * 1000L;
            else
            {
                TrySetOpt(encoder->priv_data, "cq", quality.ToString());
                encoder->bit_rate = 0;
            }
        }
        else if (name.EndsWith("_qsv", StringComparison.Ordinal) || name.EndsWith("_amf", StringComparison.Ordinal))
        {
            TrySetOpt(encoder->priv_data, "global_quality", quality.ToString());
            if (profile.RateControl == RateControlMode.Bitrate)
                encoder->bit_rate = Math.Max(100, profile.VideoBitrateKbps) * 1000L;
        }
        else if (name != "gif")
        {
            TrySetOpt(encoder->priv_data, "preset", SoftwarePreset(profile.EncoderPreset));
            if (profile.RateControl == RateControlMode.Bitrate)
                encoder->bit_rate = Math.Max(100, profile.VideoBitrateKbps) * 1000L;
            else
                TrySetOpt(encoder->priv_data, "crf", quality.ToString());
        }

        if (profile.PreserveHdr && (video.IsHdr || profile.ForceHdr))
        {
            encoder->color_primaries = PrimariesFromName(string.IsNullOrWhiteSpace(outputVideo.ColorPrimaries) ? "bt2020" : outputVideo.ColorPrimaries);
            encoder->color_trc = TransferFromName(string.IsNullOrWhiteSpace(outputVideo.ColorTransfer) ? "smpte2084" : outputVideo.ColorTransfer);
            encoder->colorspace = ColorSpaceFromName(string.IsNullOrWhiteSpace(outputVideo.ColorSpace) ? "bt2020nc" : outputVideo.ColorSpace);
            if (!string.IsNullOrWhiteSpace(outputVideo.ColorRange))
                encoder->color_range = ColorRangeFromName(outputVideo.ColorRange);
        }

        if (pass == 1)
        {
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_PASS1;
            if (passLog != null && !TrySetOpt(encoder->priv_data, "stats", passLog))
                TrySetOpt(encoder->priv_data, "stats", passLog + "-0.log");
        }
            else if (pass == 2)
        {
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_PASS2;
            if (passLog != null && !TrySetOpt(encoder->priv_data, "stats", passLog))
                TrySetOpt(encoder->priv_data, "stats", passLog + "-0.log");
        }
    }

    private static AVFormatContext* CreateOutputFormat(string outputPath, VideoTranscodeOptions profile, int pass)
    {
        AVFormatContext* format = null;
        var result = pass == 1
            ? ffmpeg.avformat_alloc_output_context2(&format, null, "null", null)
            : ffmpeg.avformat_alloc_output_context2(&format, null, null, outputPath);
        if (result < 0 || format == null)
            NativeVideo.Check(result, pass == 1 ? "null 封装" : $"输出封装（{outputPath}）");
        if (profile.Container == VideoContainer.Gif)
            TrySetOpt(format->priv_data, "loop", Math.Max(0, profile.GifLoop)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        return format;
    }

    private static void OpenOutput(AVFormatContext* format, string outputPath, bool openFile = true)
    {
        if (openFile && (format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            NativeVideo.Check(ffmpeg.avio_open(&format->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "创建输出文件");
        NativeVideo.Check(ffmpeg.avformat_write_header(format, null), "写入文件头");
    }

    private static void AttachHdrMetadata(AVStream* stream, TranscodeOptions options, VideoInfo video, bool mappedHdr)
    {
        if (!options.Video.PreserveHdr || !(video.IsHdr || options.Video.ForceHdr) || mappedHdr) return;
        ffmpeg.av_dict_set(&stream->metadata, "master-display",
            "G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)", 0);
        ffmpeg.av_dict_set(&stream->metadata, "max-cll", "1000,400", 0);
    }

    private static long Micros(long timestamp, AVRational timeBase)
        => timestamp == ffmpeg.AV_NOPTS_VALUE ? 0
            : ffmpeg.av_rescale_q(timestamp, timeBase, ffmpeg.av_get_time_base_q());

    /// <summary>
    /// rawvideo 帧 → 滤镜 → 编码 → 封装的进程内实现，替代 ffmpeg-encode 子进程。
    /// 以 Stream 形式接收逐帧像素，Complete() 冲刷滤镜图、编码器与音轨。
    /// </summary>
    private sealed unsafe class RawVideoEncoder : Stream
    {
        private readonly string _input;
        private readonly TranscodeOptions _options;
        private readonly VideoInfo _video;
        private readonly VideoInfo _outputVideo;
        private readonly bool _mappedHdr;
        private readonly CancellationToken _token;
        private readonly long _frameBytes;
        private readonly AVPixelFormat _rawFormat;

        private AVFormatContext* _format;
        private AVFilterGraph* _graph;
        private AVFilterContext* _src, _sink;
        private AVCodecContext* _codec;
        private AVFrame* _raw, _filtered;
        private AVPacket* _packet;
        private AVStream* _stream;
        private AudioTrack? _audio;
        private long _frames, _lastVideoUs;
        private bool _completed, _disposed;

        internal long OutputFrames => _frames;

        internal RawVideoEncoder(string input, string output, TranscodeOptions options, VideoInfo video,
            int rawWidth, int rawHeight, bool hdr, CancellationToken token,
            string? rawInputPath = null, int pass = 0, string? passLog = null)
        {
            _input = input;
            _options = options;
            _video = video;
            _token = token;
            _mappedHdr = NeedsDriverHdr(options, video);
            _outputVideo = _mappedHdr ? DriverHdrOutputVideo(video) : video;
            var rawFormatName = _mappedHdr ? "x2bgr10le" : hdr ? "rgba64le" : "rgba";
            _rawFormat = ffmpeg.av_get_pix_fmt(rawFormatName);
            var bpp = rawFormatName == "rgba64le" ? 8 : 4;
            _frameBytes = checked((long)rawWidth * rawHeight * bpp);
            _rawInputPath = rawInputPath;
            NativeVideo.Initialize();
            try
            {
                var sourceRate = options.EnableInterpolation
                    ? ParseRate(VulkanFrameInterpolation.OutputRate(video, options.InterpolationMultiplier))
                    : ParseRate(video.FrameRate);
                var name = EncoderName(options);
                var filters = BuildFilterChain(options.Video, _outputVideo, name, options.EnableInterpolation, _mappedHdr);
                if (options.Video.Container != VideoContainer.Gif)
                    filters = string.IsNullOrEmpty(filters)
                        ? "format=" + TargetPixelFormat(options, video, hdr, _mappedHdr)
                        : filters + ",format=" + TargetPixelFormat(options, video, hdr, _mappedHdr);
                if (string.IsNullOrEmpty(filters)) filters = "null";

                _graph = ffmpeg.avfilter_graph_alloc();
                _src = CreateBufferSource(_graph, rawWidth, rawHeight, _rawFormat,
                    new() { num = sourceRate.den, den = sourceRate.num }, sourceRate);
                _sink = CreateBufferSink(_graph);
                BindGraph(_graph, _src, _sink, filters);

                var frameRate = FrameRateFor(options, sourceRate);
                var codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) throw new NotSupportedException($"编码器不存在：{name}");
                _format = CreateOutputFormat(output, options.Video, pass);
                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null) throw new OutOfMemoryException();
                ConfigureVideoEncoder(_codec, codec, options, video, _outputVideo, _sink->inputs[0],
                    _format, frameRate, hdr, _mappedHdr, pass, passLog);
                NativeVideo.Check(ffmpeg.avcodec_open2(_codec, codec, null), $"编码器打开（{name}）");

                _stream = ffmpeg.avformat_new_stream(_format, null);
                if (_stream == null) throw new OutOfMemoryException();
                _stream->time_base = _codec->time_base;
                NativeVideo.Check(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec), "视频流参数");
                AttachHdrMetadata(_stream, options, video, _mappedHdr);
                _stream->avg_frame_rate = frameRate;

                _audio = AudioTrack.TryAttach(input, _format, options, video, pass);
                OpenOutput(_format, output, openFile: pass != 1);

                _raw = ffmpeg.av_frame_alloc();
                _filtered = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
                if (_raw == null || _filtered == null || _packet == null) throw new OutOfMemoryException();
                _raw->format = (int)_rawFormat;
                _raw->width = rawWidth;
                _raw->height = rawHeight;
                NativeVideo.Check(ffmpeg.av_frame_get_buffer(_raw, 0), "编码输入帧缓冲");
            }
            catch { Dispose(); throw; }
        }

        private readonly string? _rawInputPath;

        private static AVRational FrameRateFor(TranscodeOptions options, AVRational sourceRate)
        {
            var profile = options.Video;
            if (profile.Container == VideoContainer.Gif)
                return new() { num = Math.Clamp(profile.GifFps, 1, 30), den = 1 };
            if (!options.EnableInterpolation && profile.FpsMode != FpsMode.SameAsSource && profile.FpsValue > 0)
                return ffmpeg.av_d2q(profile.FpsValue, 4096);
            return sourceRate;
        }

        public override bool CanWrite => !_disposed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_completed || _disposed) throw new InvalidOperationException("编码器已关闭。");
            _token.ThrowIfCancellationRequested();
            if (buffer.Length != _frameBytes)
                throw new ArgumentException($"帧大小不符：期望 {_frameBytes} 字节，收到 {buffer.Length}。");
            NativeVideo.Check(ffmpeg.av_frame_make_writable(_raw), "编码输入帧");
            _raw->pts = _frames++;
            _raw->duration = 1;
            var bytesPerRow = (int)_frameBytes / _raw->height;
            fixed (byte* source = buffer)
            {
                var destination = (byte*)_raw->data[0];
                for (var y = 0; y < _raw->height; y++)
                    Buffer.MemoryCopy(source + y * bytesPerRow, destination + y * _raw->linesize[0], bytesPerRow, bytesPerRow);
            }
            NativeVideo.Check(ffmpeg.av_buffersrc_add_frame_flags(_src, _raw,
                (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "滤镜提交");
            DrainGraph();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (offset != 0 || count != buffer.Length) throw new NotSupportedException("仅支持整帧写入。");
            Write(buffer.AsSpan(offset, count));
        }

        public override void Flush() { }

        private void DrainGraph()
        {
            int result;
            while ((result = ffmpeg.av_buffersink_get_frame(_sink, _filtered)) >= 0)
            {
                try
                {
                    var sinkTimeBase = _sink->inputs[0]->time_base;
                    _filtered->pts = ffmpeg.av_rescale_q(_filtered->pts, sinkTimeBase, _codec->time_base);
                    if (_filtered->duration > 0)
                        _filtered->duration = ffmpeg.av_rescale_q(_filtered->duration, sinkTimeBase, _codec->time_base);
                    NativeVideo.Check(ffmpeg.avcodec_send_frame(_codec, _filtered), "编码提交");
                }
                finally { ffmpeg.av_frame_unref(_filtered); }
                DrainPackets();
            }
            if (result != NativeVideo.Again && result != ffmpeg.AVERROR_EOF)
                NativeVideo.Check(result, "滤镜输出");
        }

        private void DrainPackets()
        {
            int result;
            while ((result = ffmpeg.avcodec_receive_packet(_codec, _packet)) >= 0)
            {
                WriteVideoPacket(_packet);
                ffmpeg.av_packet_unref(_packet);
            }
            if (result != NativeVideo.Again && result != ffmpeg.AVERROR_EOF)
                NativeVideo.Check(result, "编码输出");
        }

        private void WriteVideoPacket(AVPacket* packet)
        {
            var end = Micros(packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts + packet->duration : 0, _codec->time_base);
            if (end > _lastVideoUs) _lastVideoUs = end;
            ffmpeg.av_packet_rescale_ts(packet, _codec->time_base, _stream->time_base);
            packet->stream_index = _stream->index;
            NativeVideo.Check(ffmpeg.av_interleaved_write_frame(_format, packet), "写视频包");
            _audio?.PumpTo(_lastVideoUs);
        }

        /// <summary>冲刷滤镜图、编码器与音轨并写 trailer。之后不得再写入。</summary>
        internal void Complete()
        {
            if (_completed) return;
            _completed = true;
            NativeVideo.Check(ffmpeg.av_buffersrc_add_frame(_src, null), "滤镜 EOF");
            DrainGraph();
            NativeVideo.Check(ffmpeg.avcodec_send_frame(_codec, null), "编码器 EOF");
            DrainPackets();
            // 音轨按已编码视频时长截断（-shortest / -t 的等价物）。
            var frameRate = ffmpeg.av_q2d(_codec->framerate);
            var videoEndUs = frameRate > 0 ? (long)(_frames / frameRate * 1_000_000) : _lastVideoUs;
            _audio?.Finish(videoEndUs);
            NativeVideo.Check(ffmpeg.av_write_trailer(_format), "写文件尾");
        }

        internal void RunFromRawFile()
        {
            if (_rawInputPath == null) throw new InvalidOperationException("未配置 raw 文件输入。");
            var buffer = new byte[(int)_frameBytes];
            using (var stream = new FileStream(_rawInputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                while (stream.Read(buffer) == buffer.Length)
                {
                    _token.ThrowIfCancellationRequested();
                    Write(buffer);
                }
            Complete();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            var graph = _graph; var format = _format; var codec = _codec;
            var raw = _raw; var filtered = _filtered; var packet = _packet;
            _graph = null; _format = null; _codec = null;
            _raw = null; _filtered = null; _packet = null;
            _audio?.Dispose();
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&raw);
            ffmpeg.av_frame_free(&filtered);
            ffmpeg.avcodec_free_context(&codec);
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            if (format != null)
            {
                if (format->pb != null) ffmpeg.avio_closep(&format->pb);
                ffmpeg.avformat_free_context(format);
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>把已编码的中间文件（.nut）视频流与源文件音轨封装为最终输出，替代 encodedInput 封装 pass。</summary>
    private static void RemuxEncoded(string input, string output, TranscodeOptions options, VideoInfo video,
        string encodedPath, long? frameLimit)
    {
        NativeVideo.Initialize();
        AVFormatContext* encoded = null;
        AVFormatContext* format = null;
        AudioTrack? audio = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            if (packet == null) throw new OutOfMemoryException();
            NativeVideo.Check(ffmpeg.avformat_open_input(&encoded, encodedPath, null, null), "打开已编码中间文件");
            NativeVideo.Check(ffmpeg.avformat_find_stream_info(encoded, null), "中间文件流信息");
            var encodedIndex = ffmpeg.av_find_best_stream(encoded, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            NativeVideo.Check(encodedIndex, "中间文件视频流");
            var encodedStream = encoded->streams[encodedIndex];

            format = CreateOutputFormat(output, options.Video, pass: 0);
            var outStream = ffmpeg.avformat_new_stream(format, null);
            if (outStream == null) throw new OutOfMemoryException();
            NativeVideo.Check(ffmpeg.avcodec_parameters_copy(outStream->codecpar, encodedStream->codecpar), "视频流参数复制");
            // 跨容器（NUT→MP4 等）不能沿用源容器 codec_tag，清零让封装器自选。
            outStream->codecpar->codec_tag = 0;
            outStream->time_base = encodedStream->time_base;
            var mappedHdr = NeedsDriverHdr(options, video);
            if (options.Video.PreserveHdr && (video.IsHdr || options.Video.ForceHdr))
            {
                var outputVideo = mappedHdr ? DriverHdrOutputVideo(video) : video;
                outStream->codecpar->color_primaries = PrimariesFromName(string.IsNullOrWhiteSpace(outputVideo.ColorPrimaries) ? "bt2020" : outputVideo.ColorPrimaries);
                outStream->codecpar->color_trc = TransferFromName(string.IsNullOrWhiteSpace(outputVideo.ColorTransfer) ? "smpte2084" : outputVideo.ColorTransfer);
                outStream->codecpar->color_space = ColorSpaceFromName(string.IsNullOrWhiteSpace(outputVideo.ColorSpace) ? "bt2020nc" : outputVideo.ColorSpace);
                if (!string.IsNullOrWhiteSpace(outputVideo.ColorRange))
                    outStream->codecpar->color_range = ColorRangeFromName(outputVideo.ColorRange);
            }
            AttachHdrMetadata(outStream, options, video, mappedHdr);
            outStream->avg_frame_rate = encodedStream->avg_frame_rate;

            audio = AudioTrack.TryAttach(input, format, options, video, pass: 0);
            OpenOutput(format, output);

            long written = 0, videoEndUs = 0;
            int result;
            while ((result = ffmpeg.av_read_frame(encoded, packet)) >= 0)
            {
                try
                {
                    if (packet->stream_index != encodedIndex) continue;
                    if (frameLimit is { } limit && written >= limit) break;
                    var end = Micros(packet->pts != ffmpeg.AV_NOPTS_VALUE
                        ? packet->pts + Math.Max(packet->duration, 0) : 0, encodedStream->time_base);
                    if (end > videoEndUs) videoEndUs = end;
                    ffmpeg.av_packet_rescale_ts(packet, encodedStream->time_base, outStream->time_base);
                    packet->stream_index = outStream->index;
                    NativeVideo.Check(ffmpeg.av_interleaved_write_frame(format, packet), "写视频包");
                    written++;
                    audio?.PumpTo(videoEndUs);
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            if (result < 0 && result != ffmpeg.AVERROR_EOF) NativeVideo.Check(result, "读中间文件");
            audio?.Finish(videoEndUs);
            NativeVideo.Check(ffmpeg.av_write_trailer(format), "写文件尾");
        }
        finally
        {
            audio?.Dispose();
            ffmpeg.av_packet_free(&packet);
            if (format != null)
            {
                if (format->pb != null) ffmpeg.avio_closep(&format->pb);
                ffmpeg.avformat_free_context(format);
            }
            ffmpeg.avformat_close_input(&encoded);
        }
    }

    /// <summary>源文件音轨：直拷或重编码（含增益），按视频进度惰性交错写入。</summary>
    private sealed unsafe class AudioTrack : IDisposable
    {
        private readonly AVFormatContext* _input;
        private readonly int _streamIndex;
        private readonly AVStream* _in;
        private readonly AVStream* _out;
        private readonly AVFormatContext* _format;
        private readonly bool _copy;
        private readonly double _gain;
        private readonly int _bitrateKbps;
        private readonly AVCodecContext* _decoder;
        private readonly AVCodecContext* _encoder;
        private readonly SwrContext* _resampler;
        private readonly AVFrame* _decoded, _converted;
        private readonly AVPacket* _packet, _pending;
        private long _samples, _boundUs = long.MaxValue;
        private bool _eof, _disposed;

        private AudioTrack(AVFormatContext* input, int streamIndex, AVFormatContext* output,
            bool copy, double gain, int bitrateKbps, AVCodecContext* decoder, AVCodecContext* encoder,
            SwrContext* resampler)
        {
            _input = input;
            _streamIndex = streamIndex;
            _in = input->streams[streamIndex];
            _format = output;
            _copy = copy;
            _gain = gain;
            _bitrateKbps = bitrateKbps;
            _decoder = decoder;
            _encoder = encoder;
            _resampler = resampler;
            _out = ffmpeg.avformat_new_stream(output, null);
            if (_out == null) throw new OutOfMemoryException();
            if (copy)
            {
                NativeVideo.Check(ffmpeg.avcodec_parameters_copy(_out->codecpar, _in->codecpar), "音轨参数复制");
                _out->codecpar->codec_tag = 0;
                _out->time_base = _in->time_base;
            }
            else
            {
                _out->time_base = _encoder->time_base;
                NativeVideo.Check(ffmpeg.avcodec_parameters_from_context(_out->codecpar, _encoder), "音频流参数");
            }
            _decoded = ffmpeg.av_frame_alloc();
            _converted = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            _pending = ffmpeg.av_packet_alloc();
            if (_decoded == null || _converted == null || _packet == null || _pending == null)
                throw new OutOfMemoryException();
        }

        internal static AudioTrack? TryAttach(string sourcePath, AVFormatContext* output,
            TranscodeOptions options, VideoInfo video, int pass)
        {
            var profile = options.Video;
            if (!options.CopyAudio || !video.HasAudio || profile.AudioCodec is AudioCodec.None
                || profile.Container == VideoContainer.Gif || pass == 1)
                return null;
            var copy = profile.AudioCodec == AudioCodec.Copy;
            AVFormatContext* input = null;
            AVCodecContext* decoder = null, encoder = null;
            SwrContext* resampler = null;
            try
            {
                NativeVideo.Check(ffmpeg.avformat_open_input(&input, sourcePath, null, null), "打开音频源");
                NativeVideo.Check(ffmpeg.avformat_find_stream_info(input, null), "音频源流信息");
                var index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (index < 0) { ffmpeg.avformat_close_input(&input); return null; }
                var stream = input->streams[index];
                var gain = Math.Abs(profile.AudioGainDb) > 0.01 ? Math.Pow(10, profile.AudioGainDb / 20) : 1;
                if (copy)
                    return new AudioTrack(input, index, output, true, 1,
                        profile.AudioBitrateKbps, null, null, null);
                var decoderCodec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                if (decoderCodec == null) { ffmpeg.avformat_close_input(&input); return null; }
                decoder = ffmpeg.avcodec_alloc_context3(decoderCodec);
                if (decoder == null) throw new OutOfMemoryException();
                NativeVideo.Check(ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar), "音频解码参数");
                decoder->pkt_timebase = stream->time_base;
                NativeVideo.Check(ffmpeg.avcodec_open2(decoder, decoderCodec, null), "音频解码器");

                var name = AudioEncoder(profile.AudioCodec);
                var encoderCodec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (encoderCodec == null) throw new NotSupportedException($"音频编码器不存在：{name}");
                encoder = ffmpeg.avcodec_alloc_context3(encoderCodec);
                if (encoder == null) throw new OutOfMemoryException();
                encoder->sample_rate = decoder->sample_rate > 0 ? decoder->sample_rate : 48000;
                if (name == "libopus" && encoder->sample_rate is not (48000 or 24000 or 16000 or 12000 or 8000))
                    encoder->sample_rate = 48000;
                encoder->sample_fmt = name switch
                {
                    "flac" => AVSampleFormat.AV_SAMPLE_FMT_S32,
                    _ => AVSampleFormat.AV_SAMPLE_FMT_FLTP,
                };
                ffmpeg.av_channel_layout_copy(&encoder->ch_layout, &decoder->ch_layout);
                encoder->bit_rate = Math.Clamp(profile.AudioBitrateKbps, 32, 512) * 1000L;
                if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                NativeVideo.Check(ffmpeg.avcodec_open2(encoder, encoderCodec, null), $"音频编码器（{name}）");

                NativeVideo.Check(ffmpeg.swr_alloc_set_opts2(&resampler,
                    &encoder->ch_layout, encoder->sample_fmt, encoder->sample_rate,
                    &decoder->ch_layout, decoder->sample_fmt, decoder->sample_rate, 0, null), "音频重采样器");
                NativeVideo.Check(ffmpeg.swr_init(resampler), "音频重采样初始化");
                return new AudioTrack(input, index, output, false, gain,
                    profile.AudioBitrateKbps, decoder, encoder, resampler);
            }
            catch
            {
                ffmpeg.swr_free(&resampler);
                var enc = encoder; ffmpeg.avcodec_free_context(&enc);
                var dec = decoder; ffmpeg.avcodec_free_context(&dec);
                ffmpeg.avformat_close_input(&input);
                throw;
            }
        }

        private long PendingMicros()
        {
            if (_pending->data != null || _pending->size != 0)
            {
                var stamp = _pending->pts != ffmpeg.AV_NOPTS_VALUE ? _pending->pts : _pending->dts;
                return Micros(stamp, _in->time_base);
            }
            return long.MaxValue;
        }

        private bool ReadNextPacket()
        {
            while (true)
            {
                var result = ffmpeg.av_read_frame(_input, _pending);
                if (result == ffmpeg.AVERROR_EOF) return false;
                NativeVideo.Check(result, "读音频包");
                if (_pending->stream_index == _streamIndex) return true;
                ffmpeg.av_packet_unref(_pending);
            }
        }

        /// <summary>把时间戳不超过 targetUs 的音轨数据写入输出（与视频包交错）。</summary>
        internal void PumpTo(long targetUs)
        {
            if (_disposed) return;
            while (!_eof && PendingMicros() <= targetUs) Step();
        }

        internal void Finish(long boundUs)
        {
            if (_disposed) return;
            _boundUs = boundUs >= 0 ? boundUs : long.MaxValue;
            while (!_eof) Step();
            if (!_copy)
            {
                // 冲刷解码器 → 重采样器 → 编码器。
                FlushDecoder();
                while (true)
                {
                    ffmpeg.av_frame_unref(_converted);
                    _converted->format = (int)_encoder->sample_fmt;
                    ffmpeg.av_channel_layout_copy(&_converted->ch_layout, &_encoder->ch_layout);
                    _converted->sample_rate = _encoder->sample_rate;
                    if (ffmpeg.swr_convert_frame(_resampler, _converted, null) < 0) break;
                    if (_converted->nb_samples == 0) break;
                    EncodeFrame(_converted);
                }
                ffmpeg.avcodec_send_frame(_encoder, null);
                DrainEncoder();
            }
        }

        private void Step()
        {
            if (_pending->data == null && _pending->size == 0 && !ReadNextPacket()) { FlushDecoder(); return; }
            var stamp = _pending->pts != ffmpeg.AV_NOPTS_VALUE ? _pending->pts : _pending->dts;
            var micros = Micros(stamp, _in->time_base);
            if (micros > _boundUs) { _eof = true; ffmpeg.av_packet_unref(_pending); return; }
            if (_copy)
            {
                var packet = _pending;
                ffmpeg.av_packet_rescale_ts(packet, _in->time_base, _out->time_base);
                packet->stream_index = _out->index;
                NativeVideo.Check(ffmpeg.av_interleaved_write_frame(_format, packet), "写音频包");
                ffmpeg.av_packet_unref(packet);
            }
            else
            {
                NativeVideo.Check(ffmpeg.avcodec_send_packet(_decoder, _pending), "音频解码提交");
                ffmpeg.av_packet_unref(_pending);
                DecodeAll();
            }
        }

        private void DecodeAll()
        {
            int result;
            while ((result = ffmpeg.avcodec_receive_frame(_decoder, _decoded)) >= 0)
            {
                try
                {
                    ffmpeg.av_frame_unref(_converted);
                    _converted->format = (int)_encoder->sample_fmt;
                    ffmpeg.av_channel_layout_copy(&_converted->ch_layout, &_encoder->ch_layout);
                    _converted->sample_rate = _encoder->sample_rate;
                    NativeVideo.Check(ffmpeg.swr_convert_frame(_resampler, _converted, _decoded), "音频重采样");
                    if (_converted->nb_samples > 0) EncodeFrame(_converted);
                }
                finally { ffmpeg.av_frame_unref(_decoded); }
            }
            if (result != NativeVideo.Again && result != ffmpeg.AVERROR_EOF)
                NativeVideo.Check(result, "音频解码输出");
        }

        private void FlushDecoder()
        {
            if (_copy || _eof) { _eof = true; return; }
            ffmpeg.avcodec_send_packet(_decoder, null);
            DecodeAll();
            _eof = true;
        }

        private void EncodeFrame(AVFrame* frame)
        {
            if (_gain != 1 && _encoder->sample_fmt is AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_FLT)
            {
                var channels = _encoder->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP
                    ? frame->ch_layout.nb_channels : 1;
                var count = frame->nb_samples * channels;
                for (var plane = 0; plane < (channels > 1 && _encoder->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP ? channels : 1); plane++)
                {
                    var samples = (float*)frame->data[(uint)plane];
                    var per = _encoder->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP ? frame->nb_samples : count;
                    for (var i = 0; i < per; i++) samples[i] *= (float)_gain;
                }
            }
            frame->pts = _samples;
            _samples += frame->nb_samples;
            NativeVideo.Check(ffmpeg.avcodec_send_frame(_encoder, frame), "音频编码提交");
            DrainEncoder();
        }

        private void DrainEncoder()
        {
            int result;
            while ((result = ffmpeg.avcodec_receive_packet(_encoder, _packet)) >= 0)
            {
                ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _out->time_base);
                _packet->stream_index = _out->index;
                NativeVideo.Check(ffmpeg.av_interleaved_write_frame(_format, _packet), "写音频包");
                ffmpeg.av_packet_unref(_packet);
            }
            if (result != NativeVideo.Again && result != ffmpeg.AVERROR_EOF)
                NativeVideo.Check(result, "音频编码输出");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var input = _input; var decoder = _decoder; var encoder = _encoder;
            var resampler = _resampler; var decoded = _decoded; var converted = _converted;
            var packet = _packet; var pending = _pending;
            ffmpeg.av_packet_free(&pending);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&converted);
            ffmpeg.av_frame_free(&decoded);
            ffmpeg.swr_free(&resampler);
            ffmpeg.avcodec_free_context(&encoder);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&input);
        }
    }
}

using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

/// <summary>进程内 Vulkan 光流。设备在 hwupload 初始化前绑定；原生帧、图与设备各有明确所有者。</summary>
internal sealed unsafe class VulkanInterpolationSession : IDisposable
{
    private AVFilterGraph* _graph;
    private AVFilterContext* _source, _sink;
    private AVBufferRef* _device;
    private AVFrame* _input, _output;
    private readonly int _width, _height, _rowBytes, _multiplier;
    private long _inputs, _outputs;
    private bool _ended;
    private byte[]? _packed;
    internal int FrameBytes => checked(_rowBytes * _height);
    internal long Outputs => _outputs;

    internal VulkanInterpolationSession(int width, int height, bool hdr, VideoInfo video, int multiplier, bool linear)
    {
        NativeVideo.Initialize();
        _width = width; _height = height; _rowBytes = checked(width * (hdr ? 8 : 4)); _multiplier = multiplier;
        if (multiplier is not (2 or 4 or 6)) throw new ArgumentOutOfRangeException(nameof(multiplier));
        try
        {
            if (ffmpeg.avfilter_get_by_name("fruc_vulkan") == null)
                throw new NotSupportedException("runtime 中的 FFmpeg DLL 缺少 fruc_vulkan，请运行 tools/prepare-ffmpeg.ps1。");
            AVDictionary* settings = null;
            AVBufferRef* device = null;
            try
            {
                if (linear) NativeVideo.Check(ffmpeg.av_dict_set(&settings, "linear_images", "1", 0), "Vulkan image options");
                // 按设备名称选择 NVIDIA，避免 Vulkan 枚举顺序与 CUDA/DXGI 编号不同。
                NativeVideo.Check(ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN, "NVIDIA", settings, 0), "Vulkan device");
                _device = device; device = null;
            }
            finally { ffmpeg.av_dict_free(&settings); ffmpeg.av_buffer_unref(&device); }
            _graph = ffmpeg.avfilter_graph_alloc(); _input = ffmpeg.av_frame_alloc(); _output = ffmpeg.av_frame_alloc();
            if (_graph == null || _input == null || _output == null) throw new OutOfMemoryException();
            AVRational rate = ParseRate(video);
            AVPixelFormat format = hdr ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_RGBA;
            AVFilterContext* source = null; AVFilterContext* sink = null;
            NativeVideo.Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "source",
                $"video_size={width}x{height}:pix_fmt={(int)format}:time_base={rate.den}/{rate.num}:pixel_aspect=1/1:frame_rate={rate.num}/{rate.den}", null, _graph), "Vulkan source");
            NativeVideo.Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "sink", null, null, _graph), "Vulkan sink");
            _source = source; _sink = sink;
            // 克隆最后一帧提供 lookahead，输出始终裁到真实源帧数×倍率。
            BindGraph(VulkanFrameInterpolation.Filter(multiplier, hdr, true));
            _input->width = width; _input->height = height; _input->format = (int)format;
            NativeVideo.Check(ffmpeg.av_frame_get_buffer(_input, 32), "Vulkan input allocation");
        }
        catch { Dispose(); throw; }
    }
    internal static AVRational ParseRate(VideoInfo video)
    {
        var parts = video.FrameRate.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den) && num > 0 && den > 0)
            return new() { num = num, den = den };
        return ffmpeg.av_d2q(video.Fps, 1_001_000);
    }
    private void BindGraph(string description)
    {
        AVFilterGraphSegment* segment = null; AVFilterInOut* inputs = null; AVFilterInOut* outputs = null;
        try
        {
            NativeVideo.Check(ffmpeg.avfilter_graph_segment_parse(_graph, description, 0, &segment), "Vulkan graph parse");
            NativeVideo.Check(ffmpeg.avfilter_graph_segment_create_filters(segment, 0), "Vulkan graph filters");
            for (uint i = 0; i < _graph->nb_filters; i++)
            {
                var filter = _graph->filters[i];
                if (filter->hw_device_ctx != null) continue;
                filter->hw_device_ctx = ffmpeg.av_buffer_ref(_device);
                if (filter->hw_device_ctx == null) throw new OutOfMemoryException();
            }
            NativeVideo.Check(ffmpeg.avfilter_graph_segment_apply(segment, 0, &inputs, &outputs), "Vulkan graph initialize");
            if (inputs == null || outputs == null || inputs->next != null || outputs->next != null)
                throw new InvalidOperationException("Vulkan 图必须具有一个输入和一个输出。");
            NativeVideo.Check(ffmpeg.avfilter_link(_source, 0, inputs->filter_ctx, (uint)inputs->pad_idx), "Vulkan graph input link");
            NativeVideo.Check(ffmpeg.avfilter_link(outputs->filter_ctx, (uint)outputs->pad_idx, _sink, 0), "Vulkan graph output link");
            NativeVideo.Check(ffmpeg.avfilter_graph_config(_graph, null), "Vulkan graph configure");
        }
        finally { ffmpeg.avfilter_inout_free(&inputs); ffmpeg.avfilter_inout_free(&outputs); ffmpeg.avfilter_graph_segment_free(&segment); }
    }
    internal void Write(ReadOnlySpan<byte> frame, Stream destination, CancellationToken ct)
    {
        if (_ended) throw new InvalidOperationException("Vulkan 输入已经结束。");
        if (frame.Length != FrameBytes) throw new ArgumentException("Vulkan 必须整帧写入。");
        ct.ThrowIfCancellationRequested();
        NativeVideo.Check(ffmpeg.av_frame_make_writable(_input), "Vulkan writable frame");
        for (int y = 0; y < _height; y++) frame.Slice(y * _rowBytes, _rowBytes).CopyTo(new Span<byte>(_input->data[0] + y * _input->linesize[0], _rowBytes));
        _input->pts = _inputs++; _input->duration = 1;
        NativeVideo.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, _input, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "Vulkan submit frame");
        Drain(destination, ct, false);
    }
    internal void Complete(Stream destination, CancellationToken ct)
    {
        if (_ended) return;
        NativeVideo.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0), "Vulkan end of input");
        Drain(destination, ct, true);
        if (_outputs != _inputs * _multiplier) throw new InvalidOperationException($"Vulkan 输出帧数错误：{_outputs}/{_inputs * _multiplier}");
        _ended = true;
    }
    private void Drain(Stream destination, CancellationToken ct, bool end)
    {
        long deadline = Environment.TickCount64 + 30_000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int result = ffmpeg.av_buffersink_get_frame(_sink, _output);
            if (result == ffmpeg.AVERROR_EOF) return;
            if (result == NativeVideo.Again)
            {
                if (!end) return;
                if (Environment.TickCount64 > deadline) throw new TimeoutException("Vulkan 排空超时。");
                Thread.Sleep(1); continue;
            }
            NativeVideo.Check(result, "Vulkan receive frame");
            try
            {
                if (_outputs >= _inputs * _multiplier) continue;
                if (_output->width != _width || _output->height != _height || _output->format != _input->format)
                    throw new InvalidOperationException("Vulkan 输出像素格式或尺寸改变。");
                if (_output->pts != _outputs) throw new InvalidOperationException($"Vulkan 输出时间戳错误：{_output->pts}/{_outputs}");
                if (_output->linesize[0] == _rowBytes) destination.Write(new ReadOnlySpan<byte>(_output->data[0], FrameBytes));
                else
                {
                    _packed ??= new byte[FrameBytes];
                    for (int y = 0; y < _height; y++) new ReadOnlySpan<byte>(_output->data[0] + y * _output->linesize[0], _rowBytes).CopyTo(_packed.AsSpan(y * _rowBytes, _rowBytes));
                    destination.Write(_packed);
                }
                _outputs++;
            }
            finally { ffmpeg.av_frame_unref(_output); }
        }
    }
    public void Dispose()
    {
        var graph = _graph; var input = _input; var output = _output; var device = _device;
        _graph = null; _input = _output = null; _device = null;
        ffmpeg.av_frame_free(&input); ffmpeg.av_frame_free(&output); ffmpeg.avfilter_graph_free(&graph); ffmpeg.av_buffer_unref(&device);
    }
}

internal sealed class VulkanFrameStream(Stream destination, VulkanInterpolationSession session, CancellationToken ct) : InterpolationFrameStream
{
    public override void Write(ReadOnlySpan<byte> buffer) => session.Write(buffer, destination, ct);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    internal override void Complete() => session.Complete(destination, ct);
    public override void Flush() => destination.Flush();
}

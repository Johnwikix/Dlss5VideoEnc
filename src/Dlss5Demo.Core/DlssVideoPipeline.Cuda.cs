using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

public static partial class DlssVideoPipeline
{
    private static bool CanEncodeRifeOnGpu(TranscodeOptions options) => options.UseGpuFramePipeline
        && options.EnableInterpolation && options.InterpolationBackend == FrameInterpolationBackend.Rife
        && options.VideoEncoder is "hevc_nvenc" or "h264_nvenc" or "av1_nvenc"
        && !options.Video.TwoPassEnabled && options.Video.Container != VideoContainer.Gif
        && !options.Video.ForceHdr && !options.Video.CropEnabled && options.Video.ScaleMode == ScaleMode.None
        && options.Video.Denoise == DenoiseMode.None && options.Video.Deinterlace == DeinterlaceMode.None;

    private static bool CanUseCudaRife(TranscodeOptions options, VideoInfo video) => CanEncodeRifeOnGpu(options)
        && ((!options.EnableNr && options.SuperResolutionScale == 1) || options.UseGpuEnhancementPipeline)
        && (!options.EnableNr || options.Dlss.UseGpuTemporalProcessing && options.Dlss.MotionGuidance)
        && options.DecodeMode != VideoDecodeMode.Software
        && video.PixelFormat is "yuv420p" or "yuvj420p" or "nv12" && !video.IsHdr
        && video.ColorSpace is "" or "unknown" or "unspecified" or "bt709" or "smpte170m" or "bt470bg";

    private static async Task<TranscodeResult> RunCudaRifeAsync(string input, string output, TranscodeOptions options,
        VideoInfo video, long totalFrames, RifeGpuSession model, IProgress<TranscodeProgress>? progress, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"dlss5-cuda-{Guid.NewGuid():N}.nut");
        try
        {
            var outcome = await Task.Run(() => CudaRifePipeline.Run(input, temporary, options, video, totalFrames, model, progress, ct), ct).ConfigureAwait(false);
            long frames = outcome.Frames;
            var finalize = Stopwatch.StartNew();
            int scale = RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale);
            await Task.Run(() => RemuxEncoded(input, output, options, video, temporary,
                frames * options.InterpolationMultiplier), ct).ConfigureAwait(false);
            var elapsed = outcome.Elapsed + finalize.Elapsed;
            progress?.Report(new TranscodeProgress(frames, totalFrames, frames / elapsed.TotalSeconds, elapsed));
            return new TranscodeResult { OutputPath = output, FramesProcessed = frames, Elapsed = elapsed,
                AverageFps = frames / elapsed.TotalSeconds, OutputBytes = new FileInfo(output).Length };
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private sealed class CudaDecodeUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
    private sealed class CudaEnhancementUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>固定槽位：NVDEC/颜色转换 → 两帧 RIFE/NV12 → NVENC。编码器保留的 AVFrame 引用不被提前覆盖。</summary>
    private static unsafe class CudaRifePipeline
    {
        private static readonly AVCodecContext_get_format CudaFormat = static (context, formats) =>
        {
            for (var item = formats; *item != AVPixelFormat.AV_PIX_FMT_NONE; item++)
                if (*item == AVPixelFormat.AV_PIX_FMT_CUDA) return *item;
            return ffmpeg.avcodec_default_get_format(context, formats);
        };
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct CudaHeader { [FieldOffset(8)] internal nint Stream; }

        internal static (long Frames, TimeSpan Elapsed) Run(string input, string temporary, TranscodeOptions options, VideoInfo video, long totalFrames,
            RifeGpuSession model, IProgress<TranscodeProgress>? progress, CancellationToken ct)
        {
            NativeVideo.Initialize();
            if (model.Cuda.DeviceId != options.DecodeDeviceIndex)
                throw new CudaDecodeUnavailableException("Windows ML 推理设备与指定 NVDEC 设备不同，沿用主机帧传输路径。");
            using var cuda = new CudaVideoContext(options.DecodeDeviceIndex);
            int scale = RtxVideoSuperResolutionHost.NormalizeScale(options.SuperResolutionScale);
            var plan = CreateProcessingPlan(video, scale, false, !options.EnableNr || options.NrBeforeSuperResolution);
            var outputVideo = video with { Width = plan.ProcessWidth, Height = plan.ProcessHeight };
            using var enhancement = new EnhancementStage(options, video, plan, ct);
            AVBufferRef* device = null; AVBufferRef* framesPool = null;
            int capacity = Math.Clamp(options.GpuFrameCapacity, 3, 5);
            var slots = new List<Slot>(capacity);
            try
            {
                using var allocator = new FrameAllocator(cuda, outputVideo.Width, outputVideo.Height);
                device = (AVBufferRef*)allocator.Device;
                framesPool = (AVBufferRef*)allocator.Frames;
                // NVDEC and its color conversion must not share the RIFE/NV12
                // stream: otherwise each handoff also waits for unrelated work
                // submitted concurrently by the other pipeline stage.
                using var decodeCuda = new CudaVideoContext(options.DecodeDeviceIndex);
                using var decodeAllocator = new FrameAllocator(decodeCuda, video.Width, video.Height);
                using var decoder = new Decoder(input, (AVBufferRef*)decodeAllocator.Device, decodeCuda, video, options.MaxFrames, ct);
                using var encoder = new Encoder(temporary, options, outputVideo, framesPool);
                for (int i = 0; i < capacity; i++) slots.Add(new Slot(cuda, plan, options.InterpolationMultiplier, enhancement.Active));
                using var synthesized = cuda.Allocate(outputVideo.Width, outputVideo.Height);
                bool input709 = video.ColorSpace.Equals("bt709", StringComparison.OrdinalIgnoreCase);
                var metrics = new PipelineStageMetrics("显存管线", "decode", "pair", "rife", "nv12", "encode", "enhance");
                var clock = Stopwatch.StartNew(); var lastProgress = TimeSpan.FromSeconds(-1); var lastPreview = TimeSpan.FromSeconds(-1);
                var sink = options.EnablePreviewFrames ? options.PreviewSink : null;
                int previewWidth = outputVideo.Width, previewHeight = outputVideo.Height;
                if (sink != null && sink.MaxFrameWidth > 0 && sink.MaxFrameHeight > 0)
                {
                    double ratio = Math.Min(1, Math.Min((double)sink.MaxFrameWidth / outputVideo.Width, (double)sink.MaxFrameHeight / outputVideo.Height));
                    previewWidth = Math.Max(1, (int)(outputVideo.Width * ratio)); previewHeight = Math.Max(1, (int)(outputVideo.Height * ratio));
                }
                using var preview = sink == null ? null : cuda.Allocate(previewWidth, previewHeight);
                byte[]? previewBytes = sink == null ? null : new byte[checked(previewWidth * previewHeight * 4)];
                byte[]? sourcePreviewBytes = sink == null || !enhancement.Active ? previewBytes : new byte[checked(previewWidth * previewHeight * 4)];
                PipelineLog.Info($"显存帧管线：{capacity} 槽；NVDEC → {enhancement.Description}CUDA RIFE → NVENC；像素保留 GPU，预览按需缩小回读。CUDA {cuda.DeviceId}");
                nint poolHandle = (nint)framesPool;
                var stages = new List<BoundedFramePipeline.Stage<Slot>>();
                if (enhancement.Active) stages.Add((inputFrames, emit, token) =>
                {
                    foreach (var current in inputFrames)
                    {
                        token.ThrowIfCancellationRequested();
                        var stamp = metrics.Start(); enhancement.Process(current.Buffer.Source.Frame, current.Buffer.Original.Frame);
                        metrics.Record(5, stamp); emit(current);
                    }
                });
                stages.Add((inputFrames, emit, token) =>
                    {
                        BoundedFramePipeline.Frame<Slot>? previous = null;
                        foreach (var current in inputFrames)
                        {
                            token.ThrowIfCancellationRequested();
                            if (previous is { } first) { Process(first, current, token); emit(first); }
                            previous = current;
                        }
                        if (previous is { } tail) { Process(tail, tail, token); emit(tail); }
                    });
                long written = BoundedFramePipeline.RunStages(slots, (slot, index) => { var stamp = metrics.Start(); bool read = decoder.Read(slot.Source.Frame, ct); metrics.Record(0, stamp); return read; },
                    stages.ToArray(),
                    (slot, index) =>
                    {
                        var encodeStamp = metrics.Start();
                        for (int i = 0; i < options.InterpolationMultiplier; i++) encoder.Write((AVFrame*)slot.Encoded[i]);
                        metrics.Record(4, encodeStamp);
                        if (sink != null && clock.Elapsed - lastPreview >= TimeSpan.FromSeconds(1d / Math.Clamp(options.PreviewFramesPerSecond, 1, 120)))
                        {
                            var source = slot.Original.Frame;
                            cuda.Preview(source.Data, 0, source.Pitch, 0, source.Width, source.Height, preview!.Frame, false, input709, true, previewBytes!);
                            for (int i = 0; i < previewBytes!.Length; i += 4) (previewBytes[i], previewBytes[i + 2]) = (previewBytes[i + 2], previewBytes[i]);
                            if (enhancement.Active)
                            {
                                var original = slot.Source.Frame;
                                cuda.Preview(original.Data, 0, original.Pitch, 0, original.Width, original.Height, preview.Frame, false, input709, true, sourcePreviewBytes!);
                                for (int i = 0; i < sourcePreviewBytes!.Length; i += 4) (sourcePreviewBytes[i], sourcePreviewBytes[i + 2]) = (sourcePreviewBytes[i + 2], sourcePreviewBytes[i]);
                            }
                            sink.Publish(sourcePreviewBytes!, previewWidth, previewHeight, previewBytes, previewWidth, previewHeight, index + 1, totalFrames, video.Fps);
                            lastPreview = clock.Elapsed;
                        }
                        if (clock.Elapsed - lastProgress >= ProgressInterval)
                        {
                            progress?.Report(new TranscodeProgress(index + 1, totalFrames, (index + 1) / clock.Elapsed.TotalSeconds, clock.Elapsed));
                            lastProgress = clock.Elapsed;
                        }
                    }, () => { decoder.Abort(); enhancement.Abort(); }, ct);
                encoder.Complete(); clock.Stop(); metrics.Report(written); enhancement.ReportMetrics(written);
                if (written == 0 || totalFrames > 0 && written < totalFrames) throw new InvalidDataException("NVDEC 视频提前结束。");
                return (written, clock.Elapsed);

                void Process(BoundedFramePipeline.Frame<Slot> first, BoundedFramePipeline.Frame<Slot> second, CancellationToken token)
                {
                    var pairStamp = metrics.Start();
                    bool hold = cuda.HoldPair(first.Buffer.Original.Frame, second.Buffer.Original.Frame, true);
                    if (!hold) model.PrepareCudaPair(first.Buffer.Original.Frame, second.Buffer.Original.Frame, 1f / options.InterpolationMultiplier);
                    metrics.Record(1, pairStamp);
                    for (int i = 0; i < options.InterpolationMultiplier; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var destination = (AVFrame*)first.Buffer.Encoded[i];
                        // NVENC 的异步队列可能仍持有旧帧，重新取引用避免覆盖在途输入。
                        ffmpeg.av_frame_unref(destination);
                        if (i != 0 && hold)
                        {
                            // Repeated/cut/tail frames share immutable NV12 storage, with independent timestamps.
                            NativeVideo.Check(ffmpeg.av_frame_ref(destination, (AVFrame*)first.Buffer.Encoded[0]), "NVENC repeat frame");
                            destination->pts = first.Index * options.InterpolationMultiplier + i;
                            continue;
                        }
                        NativeVideo.Check(ffmpeg.av_hwframe_get_buffer((AVBufferRef*)poolHandle, destination, 0), "NVENC input frame");
                        var source = first.Buffer.Original.Frame;
                        if (i != 0 && !hold)
                        {
                            var rifeStamp = metrics.Start();
                            model.RunPreparedCuda(synthesized.Frame, (float)i / options.InterpolationMultiplier, token);
                            metrics.Record(2, rifeStamp);
                            source = synthesized.Frame;
                        }
                        // 旧 raw RGBA 编码入口没有颜色标记，swscale 默认按 BT.601 转为 NV12。
                        var nv12Stamp = metrics.Start();
                        cuda.ConvertToNv12(source, (nint)destination->data[0], (nint)destination->data[1], destination->linesize[0], destination->linesize[1], false);
                        metrics.Record(3, nv12Stamp);
                        destination->pts = first.Index * options.InterpolationMultiplier + i; destination->duration = 1;
                        destination->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                    }
                }
            }
            finally
            {
                foreach (var slot in slots) slot.Dispose();

            }
        }

        private sealed class EnhancementStage : IDisposable
        {
            private readonly List<IDisposable> _owned = [];
            private readonly GpuNrProcessor? _nr;
            private readonly GpuVsrProcessor? _vsr;
            private readonly RtxVideoWorker? _worker;
            private readonly CudaVideoContext.CudaBuffer? _intermediate;
            private readonly bool _nrFirst;
            internal bool Active => _nr != null || _vsr != null;
            internal string Description => _nr == null ? (_vsr == null ? "" : "共享显存 SR → ")
                : _vsr == null ? "共享显存 NR → " : _nrFirst ? "共享显存 NR → SR → " : "共享显存 SR → NR → ";

            internal EnhancementStage(TranscodeOptions options, VideoInfo video, ProcessingPlan plan, CancellationToken ct)
            {
                _nrFirst = plan.NrFirst;
                if (!options.EnableNr && options.SuperResolutionScale == 1) return;
                try
                {
                    var cuda = Own(new CudaVideoContext(options.DecodeDeviceIndex));
                    _worker = CreateVsr(options, plan, video, ct, deviceFrames: true);
                    if (_worker != null)
                    {
                        Own(_worker);
                        using var cancel = ct.Register(_worker.Abort);
                        _vsr = Own(new GpuVsrProcessor(cuda, _worker, plan.SourceWidth, plan.SourceHeight, plan.ProcessWidth / plan.SourceWidth));
                    }
                    if (options.EnableNr)
                    {
                        if (cuda.DeviceId != 0) throw new InvalidOperationException("NR 光流与解码设备不同。");
                        var host = Own(DlssNrHost.Create(options.HostDllPath, options.RuntimeDllPath, options.LogPath,
                            plan.NrWidth, plan.NrHeight, options.Dlss, false, video.ColorTransfer));
                        _nr = Own(GpuNrProcessor.TryCreate(host, plan.NrWidth, plan.NrHeight, false, video.Fps)
                            ?? throw new InvalidOperationException("当前 NR 配置不支持显存帧交换。"));
                        if (_vsr != null && _nrFirst) _intermediate = Own(cuda.Allocate(plan.NrWidth, plan.NrHeight));
                    }
                }
                catch (Exception error)
                {
                    Dispose(); ct.ThrowIfCancellationRequested();
                    throw new CudaEnhancementUnavailableException("NR/SR 显存衔接初始化失败：" + error.Message, error);
                }
            }

            private T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
            internal void Process(CudaVideoFrame input, CudaVideoFrame output)
            {
                if (_vsr == null) _nr!.ProcessDevice(input, output);
                else if (_nr == null) _vsr.Process(input, output);
                else if (_nrFirst) { _nr.ProcessDevice(input, _intermediate!.Frame); _vsr.Process(_intermediate.Frame, output); }
                else { _vsr.Process(input, output); _nr.ProcessDevice(output, output); }
            }
            internal void ReportMetrics(long frames) => _nr?.ReportMetrics(frames);
            internal void Abort() => _worker?.Abort();
            public void Dispose()
            {
                Exception? failure = null;
                for (int i = _owned.Count - 1; i >= 0; i--)
                    try { _owned[i].Dispose(); } catch (Exception error) { failure ??= error; }
                _owned.Clear();
                if (failure != null) throw failure;
            }
        }

        /// <summary>设备与 NV12 帧池的所有者；编码器和在途 AVFrame 持有各自引用。</summary>
        private sealed class FrameAllocator : IDisposable
        {
            internal nint Device { get; private set; }
            internal nint Frames { get; private set; }
            internal FrameAllocator(CudaVideoContext cuda, int width, int height)
            {
                NativeVideo.Initialize();
                AVBufferRef* device = null; AVBufferRef* frames = null;
                try
                {
                    using (cuda.Enter())
                    {
                        NativeVideo.Check(ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
                            cuda.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 2), "CUDA device");
                        ((CudaHeader*)((AVHWDeviceContext*)device->data)->hwctx)->Stream = cuda.Stream;
                    }
                    frames = ffmpeg.av_hwframe_ctx_alloc(device); if (frames == null) throw new OutOfMemoryException();
                    var config = (AVHWFramesContext*)frames->data;
                    config->format = AVPixelFormat.AV_PIX_FMT_CUDA; config->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                    config->width = width; config->height = height;
                    NativeVideo.Check(ffmpeg.av_hwframe_ctx_init(frames), "NVENC CUDA pool");
                    Device = (nint)device; Frames = (nint)frames; device = frames = null;
                }
                finally { ffmpeg.av_buffer_unref(&frames); ffmpeg.av_buffer_unref(&device); }
            }
            public void Dispose()
            {
                var device = (AVBufferRef*)Device; var frames = (AVBufferRef*)Frames; Device = Frames = 0;
                ffmpeg.av_buffer_unref(&frames); ffmpeg.av_buffer_unref(&device);
            }
        }

        internal sealed class HostInputEncoder : InterpolationFrameStream
        {
            private readonly List<IDisposable> _owned = [];
            private readonly CudaVideoContext _cuda;
            private readonly FrameAllocator _allocator;
            private readonly Encoder _encoder;
            private CudaVideoContext.CudaBuffer _previous, _current;
            private readonly CudaVideoContext.CudaBuffer _generated;
            private readonly RifeGpuSession _model;
            private readonly CancellationToken _token;
            private readonly int _multiplier;
            private AVFrame* _frame;
            private bool _hasPrevious, _complete, _disposed;
            internal long OutputFrames { get; private set; }
            internal HostInputEncoder(string path, TranscodeOptions options, VideoInfo video, RifeGpuSession model, CancellationToken ct)
            {
                _model = model; _token = ct; _multiplier = options.InterpolationMultiplier;
                NativeVideo.Initialize();
                try
                {
                    _cuda = Own(new CudaVideoContext(model.Cuda.DeviceId));
                    _allocator = Own(new FrameAllocator(_cuda, video.Width, video.Height));
                    _encoder = Own(new Encoder(path, options, video, (AVBufferRef*)_allocator.Frames));
                    _previous = Own(_cuda.Allocate(video.Width, video.Height));
                    _current = Own(_cuda.Allocate(video.Width, video.Height));
                    _generated = Own(_cuda.Allocate(video.Width, video.Height));
                    _frame = ffmpeg.av_frame_alloc(); if (_frame == null) throw new OutOfMemoryException();
                    PipelineLog.Info("NR/SR 主机帧 → 一次上传 → CUDA RIFE → NVENC；生成帧不回读 CPU。");
                }
                catch { Dispose(); throw; }
            }
            private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
            public override void Write(ReadOnlySpan<byte> rgba)
            {
                if (_complete || _disposed) throw new InvalidOperationException("RIFE 编码输入已关闭。");
                _token.ThrowIfCancellationRequested();
                if (!_hasPrevious)
                {
                    _cuda.UploadRgba(rgba, _previous.Frame); Encode(_previous.Frame); _hasPrevious = true;
                    return;
                }
                _cuda.UploadRgba(rgba, _current.Frame);
                bool hold = _cuda.HoldPair(_previous.Frame, _current.Frame, true);
                if (!hold) _model.PrepareCudaPair(_previous.Frame, _current.Frame, 1f / _multiplier);
                for (int i = 1; i < _multiplier; i++)
                {
                    _token.ThrowIfCancellationRequested();
                    if (hold) Repeat();
                    else { _model.RunPreparedCuda(_generated.Frame, (float)i / _multiplier, _token); Encode(_generated.Frame); }
                }
                Encode(_current.Frame);
                (_previous, _current) = (_current, _previous);
            }
            private void Encode(CudaVideoFrame source)
            {
                ffmpeg.av_frame_unref(_frame);
                NativeVideo.Check(ffmpeg.av_hwframe_get_buffer((AVBufferRef*)_allocator.Frames, _frame, 0), "NVENC input");
                _cuda.ConvertToNv12(source, (nint)_frame->data[0], (nint)_frame->data[1], _frame->linesize[0], _frame->linesize[1], false);
                _frame->duration = 1; _frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                Repeat();
            }
            private void Repeat()
            {
                // send_frame takes its own reference; only metadata changes while storage stays immutable.
                _frame->pts = OutputFrames++;
                _encoder.Write(_frame);
            }
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            internal override void Complete()
            {
                if (_complete) return;
                if (!_hasPrevious) throw new InvalidDataException("输入中没有完整视频帧。");
                for (int i = 1; i < _multiplier; i++) { _token.ThrowIfCancellationRequested(); Repeat(); }
                _encoder.Complete(); _complete = true;
            }
            public override void Flush() { }
            protected override void Dispose(bool disposing)
            {
                if (_disposed) return; _disposed = true;
                var frame = _frame; _frame = null; ffmpeg.av_frame_free(&frame);
                Exception? failure = null;
                for (int i = _owned.Count - 1; i >= 0; i--)
                    try { _owned[i].Dispose(); } catch (Exception error) { failure ??= error; }
                _owned.Clear(); base.Dispose(disposing);
                if (failure != null) throw failure;
            }
        }

        private sealed class Slot : IDisposable
        {
            internal CudaVideoContext.CudaBuffer Original { get; }
            internal CudaVideoContext.CudaBuffer Source { get; }
            internal nint[] Encoded { get; }
            internal Slot(CudaVideoContext cuda, ProcessingPlan plan, int multiplier, bool enhance)
            {
                Original = cuda.Allocate(plan.ProcessWidth, plan.ProcessHeight); Encoded = new nint[multiplier];
                try
                {
                    Source = enhance ? cuda.Allocate(plan.SourceWidth, plan.SourceHeight) : Original;
                    for (int i = 0; i < multiplier; i++)
                    { Encoded[i] = (nint)ffmpeg.av_frame_alloc(); if (Encoded[i] == 0) throw new OutOfMemoryException(); }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                for (int i = 0; i < Encoded.Length; i++) { var frame = (AVFrame*)Encoded[i]; Encoded[i] = 0; ffmpeg.av_frame_free(&frame); }
                Original.Dispose();
                if (!ReferenceEquals(Source, Original)) Source?.Dispose();
            }
        }

        private sealed class Decoder : IDisposable
        {
            private AVFormatContext* _format; private AVCodecContext* _codec; private AVFrame* _frame; private AVPacket* _packet;
            private int _index; private long _count; private bool _ended;
            private GCHandle _interrupt;
            private sealed class ReadCancellation(CancellationToken token) { internal readonly CancellationToken Token = token; internal int Aborted; }
            private readonly ReadCancellation _readCancellation;
            private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
            {
                if (opaque == null) return 0;
                var state = (ReadCancellation)GCHandle.FromIntPtr((nint)opaque).Target!;
                return state.Token.IsCancellationRequested || Volatile.Read(ref state.Aborted) != 0 ? 1 : 0;
            };
            internal void Abort() => Interlocked.Exchange(ref _readCancellation.Aborted, 1);
            private readonly CudaVideoContext _cuda; private readonly VideoInfo _video; private readonly int? _limit;
            internal Decoder(string input, AVBufferRef* device, CudaVideoContext cuda, VideoInfo video, int? limit, CancellationToken ct)
            {
                _readCancellation = new(ct);
                _cuda = cuda; _video = video; _limit = limit;
                AVFormatContext* format = null;
                try
                {
                    _interrupt = GCHandle.Alloc(_readCancellation);
                    format = ffmpeg.avformat_alloc_context(); if (format == null) throw new OutOfMemoryException();
                    format->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = (void*)GCHandle.ToIntPtr(_interrupt) };
                    int opened = ffmpeg.avformat_open_input(&format, input, null, null); _format = format;
                    NativeVideo.Check(opened, "NVDEC input");
                    NativeVideo.Check(ffmpeg.avformat_find_stream_info(_format, null), "NVDEC stream info");
                    _index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                    NativeVideo.Check(_index, "NVDEC video stream");
                    var stream = _format->streams[_index]; var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                    if (codec == null) throw new CudaDecodeUnavailableException("NVDEC 解码器不存在。");
                    _codec = ffmpeg.avcodec_alloc_context3(codec); _frame = ffmpeg.av_frame_alloc(); _packet = ffmpeg.av_packet_alloc();
                    if (_codec == null || _frame == null || _packet == null) throw new OutOfMemoryException();
                    NativeVideo.Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), "NVDEC parameters");
                    _codec->hw_device_ctx = ffmpeg.av_buffer_ref(device); if (_codec->hw_device_ctx == null) throw new OutOfMemoryException();
                    _codec->get_format = CudaFormat; _codec->thread_count = 1; _codec->extra_hw_frames = 4;
                    NativeVideo.Check(ffmpeg.avcodec_open2(_codec, codec, null), "NVDEC open");
                }
                catch (Exception ex) { Dispose(); throw new CudaDecodeUnavailableException("NVDEC 初始化失败。", ex); }
            }
            internal bool Read(CudaVideoFrame output, CancellationToken ct)
            {
                if (_limit is { } limit && _count >= limit) return false;
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int result = ffmpeg.avcodec_receive_frame(_codec, _frame);
                        if (result == ffmpeg.AVERROR_EOF) return false;
                        if (result >= 0)
                        {
                            try
                            {
                                if (_frame->format != (int)AVPixelFormat.AV_PIX_FMT_CUDA || _frame->hw_frames_ctx == null
                                    || ((AVHWFramesContext*)_frame->hw_frames_ctx->data)->sw_format != AVPixelFormat.AV_PIX_FMT_NV12)
                                    throw new InvalidOperationException("NVDEC 未返回 CUDA NV12 帧。");
                                if (_frame->width != output.Width || _frame->height != output.Height) throw new NotSupportedException("NVDEC 帧尺寸变化。");
                                _cuda.WaitDecoder(_cuda.Stream);
                                _cuda.ConvertNv12((nint)_frame->data[0], (nint)_frame->data[1], _frame->linesize[0], _frame->linesize[1], output,
                                    _video.ColorSpace == "bt709", _video.ColorRange is "pc" or "jpeg" or "full");
                                if (_count++ == 0) PipelineLog.Info("NVDEC 显存帧已确认：首帧 CUDA NV12 直接交给 GPU 色彩转换。");
                                return true;
                            }
                            finally { ffmpeg.av_frame_unref(_frame); }
                        }
                        if (result != NativeVideo.Again) NativeVideo.Check(result, "NVDEC receive");
                        if (_ended) throw new InvalidDataException("NVDEC 排空停滞。");
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            result = ffmpeg.av_read_frame(_format, _packet);
                            if (result == ffmpeg.AVERROR_EOF)
                            { NativeVideo.Check(ffmpeg.avcodec_send_packet(_codec, null), "NVDEC EOF"); _ended = true; break; }
                            NativeVideo.Check(result, "NVDEC demux");
                            try
                            { if (_packet->stream_index == _index) { NativeVideo.Check(ffmpeg.avcodec_send_packet(_codec, _packet), "NVDEC packet"); break; } }
                            finally { ffmpeg.av_packet_unref(_packet); }
                        }
                    }
                }
                catch (Exception ex) when (_count == 0 && ex is not OperationCanceledException)
                { throw new CudaDecodeUnavailableException("NVDEC 首帧失败，允许自动模式从头使用主机帧路径。", ex); }
            }
            public void Dispose()
            {
                var codec = _codec; var format = _format; var frame = _frame; var packet = _packet;
                _codec = null; _format = null; _frame = null; _packet = null;
                ffmpeg.av_packet_free(&packet); ffmpeg.av_frame_free(&frame); ffmpeg.avcodec_free_context(&codec); ffmpeg.avformat_close_input(&format);
                if (_interrupt.IsAllocated) _interrupt.Free();
            }
        }

        private sealed class Encoder : IDisposable
        {
            private AVFormatContext* _format; private AVCodecContext* _codec; private AVPacket* _packet; private AVStream* _stream;
            private bool _complete;
            internal Encoder(string path, TranscodeOptions options, VideoInfo video, AVBufferRef* frames)
            {
                AVFormatContext* format = null;
                try
                {
                    NativeVideo.Check(ffmpeg.avformat_alloc_output_context2(&format, null, "nut", path), "NVENC container"); _format = format;
                    _format->avoid_negative_ts = ffmpeg.AVFMT_AVOID_NEG_TS_DISABLED;
                    var codec = ffmpeg.avcodec_find_encoder_by_name(options.VideoEncoder);
                    if (codec == null) throw new NotSupportedException("NVENC 编码器不存在。");
                    _codec = ffmpeg.avcodec_alloc_context3(codec); _packet = ffmpeg.av_packet_alloc();
                    if (_codec == null || _packet == null) throw new OutOfMemoryException();
                    var rate = VulkanInterpolationSession.ParseRate(video); rate.num = checked(rate.num * options.InterpolationMultiplier);
                    _codec->width = video.Width; _codec->height = video.Height; _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
                    _codec->time_base = new() { num = rate.den, den = rate.num }; _codec->framerate = rate;
                    _codec->sample_aspect_ratio = new() { num = 1, den = 1 };
                    _codec->hw_frames_ctx = ffmpeg.av_buffer_ref(frames); if (_codec->hw_frames_ctx == null) throw new OutOfMemoryException();
                    if ((_format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    var profile = options.Video;
                    NativeVideo.Check(ffmpeg.av_opt_set(_codec->priv_data, "preset", string.IsNullOrWhiteSpace(profile.EncoderPreset) ? "p5" : profile.EncoderPreset, 0), "NVENC preset");
                    NativeVideo.Check(ffmpeg.av_opt_set(_codec->priv_data, "tune", "hq", 0), "NVENC tune");
                    NativeVideo.Check(ffmpeg.av_opt_set(_codec->priv_data, "rc", profile.RateControl == RateControlMode.Bitrate ? "vbr" : "constqp", 0), "NVENC rate control");
                    _codec->bit_rate = profile.RateControl == RateControlMode.Bitrate ? Math.Max(100, profile.VideoBitrateKbps) * 1000L : 0;
                    if (profile.RateControl != RateControlMode.Bitrate)
                        NativeVideo.Check(ffmpeg.av_opt_set(_codec->priv_data, "cq", Math.Clamp(profile.Quality > 0 ? profile.Quality : options.Quality, 0, 51).ToString(), 0), "NVENC quality");
                    NativeVideo.Check(ffmpeg.avcodec_open2(_codec, codec, null), "NVENC open");
                    _stream = ffmpeg.avformat_new_stream(_format, null); if (_stream == null) throw new OutOfMemoryException();
                    _stream->time_base = _codec->time_base; _stream->avg_frame_rate = rate;
                    NativeVideo.Check(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec), "NVENC stream parameters");
                    NativeVideo.Check(ffmpeg.avio_open(&_format->pb, path, ffmpeg.AVIO_FLAG_WRITE), "NVENC file");
                    NativeVideo.Check(ffmpeg.avformat_write_header(_format, null), "NVENC header");
                }
                catch { Dispose(); throw; }
            }
            internal void Write(AVFrame* frame) { NativeVideo.Check(ffmpeg.avcodec_send_frame(_codec, frame), "NVENC submit"); Drain(); }
            private void Drain()
            {
                int result;
                while ((result = ffmpeg.avcodec_receive_packet(_codec, _packet)) >= 0)
                {
                    ffmpeg.av_packet_rescale_ts(_packet, _codec->time_base, _stream->time_base); _packet->stream_index = _stream->index;
                    try { NativeVideo.Check(ffmpeg.av_interleaved_write_frame(_format, _packet), "NVENC packet write"); }
                    finally { ffmpeg.av_packet_unref(_packet); }
                }
                if (result != NativeVideo.Again && result != ffmpeg.AVERROR_EOF) NativeVideo.Check(result, "NVENC receive");
            }
            internal void Complete()
            { if (_complete) return; NativeVideo.Check(ffmpeg.avcodec_send_frame(_codec, null), "NVENC EOF"); Drain(); NativeVideo.Check(ffmpeg.av_write_trailer(_format), "NVENC trailer"); _complete = true; }
            public void Dispose()
            {
                var codec = _codec; var format = _format; var packet = _packet; _codec = null; _format = null; _packet = null;
                ffmpeg.av_packet_free(&packet); ffmpeg.avcodec_free_context(&codec);
                if (format != null) { if (format->pb != null) ffmpeg.avio_closep(&format->pb); ffmpeg.avformat_free_context(format); }
            }
        }
    }
}

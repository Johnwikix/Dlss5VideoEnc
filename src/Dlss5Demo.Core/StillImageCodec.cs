using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Dlss5Demo.Core;

/// <summary>静态图扩展名识别：CLI/GUI 输入白名单与输出格式分派共用。</summary>
public static class StillImage
{
    /// <summary>进程内可解码的静态图扩展名。</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff",
    };

    public static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>可作为输出写出的图片格式（扩展名小写）。</summary>
    public static bool IsWritableOutput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
}

/// <summary>解码后的单帧：紧凑 RGBA8（行长 Width*4）。</summary>
public sealed record StillImageFrame(int Width, int Height, byte[] Rgba);

/// <summary>
/// 进程内单帧图片解码/编码（avcodec + swscale，与视频管线共用 runtime 共享库）。
/// 契约：RGBA8、不透明由调用方管理；更高位深/宽色域经 swscale 落到 8bit sRGB（NR 模型为 LDR sRGB 训练）。
/// </summary>
internal static unsafe class StillImageCodec
{
    // AV_CODEC_FLAG_QSCALE 下 global_quality = qscale * FF_QP2LAMBDA。
    private const int Qp2Lambda = 118;
    // libswscale SWS_BILINEAR（AutoGen 未导出该枚举常量）。
    private const int SwsBilinear = 2;

    public static StillImageFrame Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("输入图片不存在。", path);
        NativeVideo.Initialize();
        AVFormatContext* format = null;
        AVCodecContext* codec = null;
        AVFrame* frame = null;
        AVFrame* rgba = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        try
        {
            NativeVideo.Check(ffmpeg.avformat_open_input(&format, path, null, null),
                $"打开图片（{Path.GetFileName(path)}）");
            NativeVideo.Check(ffmpeg.avformat_find_stream_info(format, null), "读取图片信息");
            AVStream* stream = null;
            for (uint i = 0; i < format->nb_streams; i++)
            {
                if (format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    stream = format->streams[i];
                    break;
                }
            }
            if (stream == null) throw new InvalidDataException("输入图片中没有图像流。");
            var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder == null) throw new NotSupportedException($"图片解码器不存在：{stream->codecpar->codec_id}。");
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            frame = ffmpeg.av_frame_alloc();
            rgba = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (codec == null || frame == null || rgba == null || packet == null) throw new OutOfMemoryException();
            NativeVideo.Check(ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar), "图片解码参数");
            codec->thread_count = 0;
            NativeVideo.Check(ffmpeg.avcodec_open2(codec, decoder, null), "打开图片解码器");

            var decoded = ReceiveDecodedFrame(codec, frame, format, packet, stream->index);
            var width = decoded->width;
            var height = decoded->height;
            if (width <= 0 || height <= 0) throw new InvalidDataException("图片尺寸无效。");

            sws = ffmpeg.sws_getContext(width, height, (AVPixelFormat)decoded->format,
                width, height, AVPixelFormat.AV_PIX_FMT_RGBA, SwsBilinear, null, null, null);
            if (sws == null) throw new InvalidOperationException("创建像素转换失败。");
            rgba->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
            rgba->width = width;
            rgba->height = height;
            NativeVideo.Check(ffmpeg.av_frame_get_buffer(rgba, 0), "分配 RGBA 缓冲");
            NativeVideo.Check(ConvertFrame(sws, decoded, rgba, height), "像素转换");

            var pixels = new byte[checked((long)width * height * 4)];
            var source = rgba->data[0];
            var stride = rgba->linesize[0];
            fixed (byte* target = pixels)
            {
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + (long)y * stride, target + (long)y * width * 4, width * 4, width * 4);
            }
            return new StillImageFrame(width, height, pixels);
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&rgba);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&codec);
            ffmpeg.avformat_close_input(&format);
        }
    }

    /// <summary>
    /// 按输出扩展名编码：.png 无损（有 alpha 用 RGBA，否则 RGB24）；.jpg/.jpeg 有损，
    /// quality 沿用转码的 crf/cq 语义（0..51）线性映射到 mjpeg qscale 2..31。
    /// 偶数尺寸用 yuvj420p（兼容性最好），奇数尺寸用 yuvj444p。
    /// </summary>
    public static void Encode(string path, ReadOnlySpan<byte> rgba, int width, int height, bool keepAlpha, int quality)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg")
        {
            var chroma = width % 2 == 0 && height % 2 == 0
                ? AVPixelFormat.AV_PIX_FMT_YUVJ420P
                : AVPixelFormat.AV_PIX_FMT_YUVJ444P;
            EncodeImage(path, rgba, width, height, "mjpeg", chroma, quality);
        }
        else
        {
            EncodeImage(path, rgba, width, height, "png",
                keepAlpha ? AVPixelFormat.AV_PIX_FMT_RGBA : AVPixelFormat.AV_PIX_FMT_RGB24, quality);
        }
    }

    private static AVFrame* ReceiveDecodedFrame(AVCodecContext* codec, AVFrame* frame,
        AVFormatContext* format, AVPacket* packet, int streamIndex)
    {
        var drained = false;
        while (true)
        {
            var status = ffmpeg.avcodec_receive_frame(codec, frame);
            if (status == ffmpeg.AVERROR_EOF) throw new InvalidDataException("图片解码未产出帧。");
            if (status == NativeVideo.Again)
            {
                if (drained) throw new InvalidDataException("图片解码排空停滞。");
                var read = ffmpeg.av_read_frame(format, packet);
                if (read < 0)
                {
                    drained = true;
                    NativeVideo.Check(ffmpeg.avcodec_send_packet(codec, null), "图片解码排空");
                    continue;
                }
                if (read == 0 && packet->stream_index == streamIndex)
                    NativeVideo.Check(ffmpeg.avcodec_send_packet(codec, packet), "图片解码提交");
                ffmpeg.av_packet_unref(packet);
                continue;
            }
            NativeVideo.Check(status, "图片解码输出");
            return frame;
        }
    }

    private static void EncodeImage(string path, ReadOnlySpan<byte> rgba, int width, int height,
        string encoderName, AVPixelFormat pixFmt, int quality)
    {
        NativeVideo.Initialize();
        var encoder = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        if (encoder == null) throw new NotSupportedException($"图片编码器不存在：{encoderName}。");
        AVCodecContext* codec = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* sws = null;
        try
        {
            codec = ffmpeg.avcodec_alloc_context3(encoder);
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (codec == null || frame == null || packet == null) throw new OutOfMemoryException();
            codec->width = width;
            codec->height = height;
            codec->pix_fmt = pixFmt;
            codec->time_base = new AVRational { num = 1, den = 25 };
            if (encoderName == "mjpeg")
            {
                codec->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                codec->global_quality = Math.Clamp((int)Math.Round(quality / 2.0), 2, 31) * Qp2Lambda;
            }
            NativeVideo.Check(ffmpeg.avcodec_open2(codec, encoder, null), "打开图片编码器");

            frame->format = (int)pixFmt;
            frame->width = width;
            frame->height = height;
            NativeVideo.Check(ffmpeg.av_frame_get_buffer(frame, 0), "分配编码帧");
            sws = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
                width, height, pixFmt, SwsBilinear, null, null, null);
            if (sws == null) throw new InvalidOperationException("创建像素转换失败。");
            fixed (byte* source = rgba)
            {
                var srcSlice = new byte*[] { source, null, null, null };
                var srcStride = new int[] { width * 4, 0, 0, 0 };
                NativeVideo.Check(
                    ffmpeg.sws_scale(sws, srcSlice, srcStride, 0, height, frame->data, frame->linesize),
                    "像素转换");
            }

            NativeVideo.Check(ffmpeg.avcodec_send_frame(codec, frame), "图片编码提交");
            NativeVideo.Check(ffmpeg.avcodec_send_frame(codec, null), "图片编码排空");
            // Reservation 以 FileShare.ReadWrite 持有输出名；这里必须同样共享打开（File.Create 的
            // FileShare.None 会与已持有句柄冲突——视频路径的 avio fopen 也是共享写）。
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            while (true)
            {
                var status = ffmpeg.avcodec_receive_packet(codec, packet);
                if (status == ffmpeg.AVERROR_EOF) break;
                NativeVideo.Check(status, "图片编码输出");
                stream.Write(new ReadOnlySpan<byte>(packet->data, (int)packet->size));
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&codec);
        }
    }

    private static int ConvertFrame(SwsContext* sws, AVFrame* source, AVFrame* target, int height)
    {
        var srcSlice = new byte*[] { source->data[0], source->data[1], source->data[2], source->data[3] };
        var srcStride = new int[] { source->linesize[0], source->linesize[1], source->linesize[2], source->linesize[3] };
        return ffmpeg.sws_scale(sws, srcSlice, srcStride, 0, height, target->data, target->linesize);
    }
}

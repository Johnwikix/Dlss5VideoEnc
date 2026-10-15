// SPDX-License-Identifier: MIT
// Optional GPU transport. FFmpeg owns NVENC surfaces and packet reordering.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/pixdesc.h>
#include <libavutil/opt.h>
}

namespace {
template<class T> void Release(T *&p) { if (p) p->Release(); p = nullptr; }
ID3D11Device *device = nullptr;
ID3D11DeviceContext *context = nullptr;
ID3D11DeviceContext4 *context4 = nullptr;
ID3D11VideoDevice *video_device = nullptr;
ID3D11VideoContext *video_context = nullptr;
ID3D11VideoContext1 *video_context1 = nullptr;
ID3D11VideoProcessorEnumerator *enumerator = nullptr, *preview_enumerator = nullptr;
ID3D11VideoProcessor *processor = nullptr, *preview_processor = nullptr;
ID3D11Texture2D *inputs[3] = {}, *preview_texture = nullptr, *preview_staging = nullptr;
ID3D12Resource *shared_outputs[3] = {};
ID3D11VideoProcessorInputView *input_views[3] = {}, *preview_views[3] = {};
ID3D11VideoProcessorOutputView *preview_output = nullptr;
ID3D11Fence *conversion_fence11 = nullptr;
ID3D12Fence *conversion_fence12 = nullptr;
ID3D12CommandQueue *queue = nullptr;
UINT64 conversion_value = 0;
AVBufferRef *hw_device = nullptr, *hw_frames = nullptr;
AVCodecContext *codec_context = nullptr;
AVFormatContext *muxer = nullptr;
AVStream *stream = nullptr;
AVPacket *packet = nullptr;
bool header_written = false, finished = false;
int width = 0, height = 0, preview_width = 0, preview_height = 0;
int64_t frame_index = 0;

bool Check(HRESULT result, const char *operation)
{
    if (SUCCEEDED(result)) return true;
    fprintf(stderr, "GPU encode %s failed: 0x%08lX\n", operation, result);
    return false;
}
bool CheckAv(int result, const char *operation)
{
    if (result >= 0) return true;
    char message[AV_ERROR_MAX_STRING_SIZE] = {};
    av_strerror(result, message, sizeof(message));
    fprintf(stderr, "GPU encode %s failed: %s\n", operation, message);
    return false;
}
std::string Utf8(const wchar_t *value)
{
    const int length = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string result(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), length, nullptr, nullptr);
    result.resize(length - 1);
    return result;
}

bool CreateProcessor(int target_width, int target_height, ID3D11VideoProcessorEnumerator **e,
    ID3D11VideoProcessor **p, DXGI_COLOR_SPACE_TYPE output_space)
{
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC description = {};
    description.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    description.InputWidth = width; description.InputHeight = height;
    description.OutputWidth = target_width; description.OutputHeight = target_height;
    description.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    if (!Check(video_device->CreateVideoProcessorEnumerator(&description, e), "processor enumerator") ||
        !Check(video_device->CreateVideoProcessor(*e, 0, p), "processor")) return false;
    RECT source = {0, 0, width, height}, destination = {0, 0, target_width, target_height};
    video_context->VideoProcessorSetStreamFrameFormat(*p, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    video_context->VideoProcessorSetStreamSourceRect(*p, 0, TRUE, &source);
    video_context->VideoProcessorSetStreamDestRect(*p, 0, TRUE, &destination);
    video_context->VideoProcessorSetOutputTargetRect(*p, TRUE, &destination);
    video_context->VideoProcessorSetStreamAutoProcessingMode(*p, 0, FALSE);
    const auto input_space = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
    video_context1->VideoProcessorSetStreamColorSpace1(*p, 0, input_space);
    video_context1->VideoProcessorSetOutputColorSpace1(*p, output_space);
    return true;
}

bool DrainPackets()
{
    while (true)
    {
        const int result = avcodec_receive_packet(codec_context, packet);
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return true;
        if (!CheckAv(result, "receive packet")) return false;
        av_packet_rescale_ts(packet, codec_context->time_base, stream->time_base);
        packet->stream_index = stream->index;
        const int written = av_interleaved_write_frame(muxer, packet);
        av_packet_unref(packet);
        if (!CheckAv(written, "write packet")) return false;
    }
}

bool CopyPreview(int slot, void *output)
{
    if (!output || !preview_output) return false;
    D3D11_VIDEO_PROCESSOR_STREAM input = {};
    input.Enable = TRUE; input.pInputSurface = preview_views[slot];
    if (!Check(video_context->VideoProcessorBlt(preview_processor, preview_output, 0, 1, &input), "preview conversion")) return false;
    context->CopyResource(preview_staging, preview_texture);
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (!Check(context->Map(preview_staging, 0, D3D11_MAP_READ, 0, &mapped), "preview map")) return false;
    for (int y = 0; y < preview_height; ++y)
    {
        const auto *row = static_cast<const uint8_t *>(mapped.pData) + size_t(y) * mapped.RowPitch;
        memcpy(static_cast<uint8_t *>(output) + size_t(y) * preview_width * 4, row, size_t(preview_width) * 4);
    }
    context->Unmap(preview_staging, 0);
    return true;
}
}

extern "C" __declspec(dllexport) void vsr_nvenc_close()
{
    // Free the codec first: it retains AVFrame refs until NVENC releases its inputs.
    avcodec_free_context(&codec_context);
    av_packet_free(&packet);
    if (muxer) { avio_closep(&muxer->pb); avformat_free_context(muxer); muxer = nullptr; }
    stream = nullptr;
    av_buffer_unref(&hw_frames); av_buffer_unref(&hw_device);
    if (context) { context->ClearState(); context->Flush(); }
    // Finish outstanding conversions before shared resources and fences disappear.
    if (context4 && conversion_fence11)
    {
        const auto value = ++conversion_value;
        if (SUCCEEDED(context4->Signal(conversion_fence11, value)))
        {
            context->Flush();
            HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (event) { conversion_fence11->SetEventOnCompletion(value, event); WaitForSingleObject(event, 10000); CloseHandle(event); }
        }
    }
    Release(preview_output); Release(preview_texture); Release(preview_staging);
    for (int i = 0; i < 3; ++i) { Release(input_views[i]); Release(preview_views[i]); Release(inputs[i]); Release(shared_outputs[i]); }
    Release(processor); Release(enumerator); Release(preview_processor); Release(preview_enumerator);
    Release(video_context1); Release(video_context); Release(video_device);
    Release(conversion_fence11); Release(conversion_fence12); Release(context4); Release(context); Release(device);
    queue = nullptr; conversion_value = 0; frame_index = 0; header_written = finished = false;
}

extern "C" __declspec(dllexport) int vsr_nvenc_open(ID3D12Device *source_device,
    ID3D12CommandQueue *source_queue, ID3D12Resource **outputs, int count, int w, int h,
    const wchar_t *output_path, const char *encoder, const char *options, int fps_num, int fps_den,
    int pw, int ph)
{
    if (device || count > 3 || count < 1 || w <= 0 || h <= 0 || fps_num <= 0 || fps_den <= 0) return 0;
    width = w; height = h; queue = source_queue;
    preview_width = pw; preview_height = ph;
    IDXGIFactory4 *factory = nullptr;
    IDXGIAdapter *adapter = nullptr;
    if (!Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "DXGI factory")) return 0;
    const HRESULT adapter_result = factory->EnumAdapterByLuid(source_device->GetAdapterLuid(), IID_PPV_ARGS(&adapter));
    Release(factory);
    if (!Check(adapter_result, "same GPU adapter")) return 0;
    const HRESULT created = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
    Release(adapter);
    if (!Check(created, "D3D11 device") || !Check(device->QueryInterface(IID_PPV_ARGS(&video_device)), "video device") ||
        !Check(context->QueryInterface(IID_PPV_ARGS(&video_context)), "video context") ||
        !Check(context->QueryInterface(IID_PPV_ARGS(&video_context1)), "video context 1") ||
        !Check(context->QueryInterface(IID_PPV_ARGS(&context4)), "D3D11 context 4")) return 0;
    ID3D11Device5 *device5 = nullptr;
    for (int i = 0; i < count; ++i)
    {
        D3D11_TEXTURE2D_DESC description = {};
        description.Width = width; description.Height = height; description.MipLevels = description.ArraySize = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        description.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        if (!Check(device->CreateTexture2D(&description, nullptr, &inputs[i]), "shared VSR texture")) return 0;
        IDXGIResource1 *resource = nullptr;
        if (!Check(inputs[i]->QueryInterface(IID_PPV_ARGS(&resource)), "shared resource interface")) return 0;
        HANDLE shared = nullptr;
        const HRESULT handle_result = resource->CreateSharedHandle(nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &shared);
        Release(resource);
        if (!Check(handle_result, "shared VSR handle")) return 0;
        const HRESULT opened = source_device->OpenSharedHandle(shared, IID_PPV_ARGS(&shared_outputs[i]));
        CloseHandle(shared);
        if (!Check(opened, "D3D12 shared VSR texture")) return 0;
    }
    if (!Check(device->QueryInterface(IID_PPV_ARGS(&device5)), "D3D11 device 5")) return 0;
    const HRESULT fence_result = device5->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&conversion_fence11));
    Release(device5);
    if (!Check(fence_result, "conversion fence")) return 0;
    HANDLE shared_fence = nullptr;
    if (!Check(conversion_fence11->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &shared_fence), "shared fence handle")) return 0;
    const HRESULT fence_opened = source_device->OpenSharedHandle(shared_fence, IID_PPV_ARGS(&conversion_fence12));
    CloseHandle(shared_fence);
    if (!Check(fence_opened, "shared conversion fence")) return 0;

    // SDR RGB->YUV matches swscale's default BT.601 limited-range matrix used by
    // the raw-RGBA compatibility encoder.
    DXGI_COLOR_SPACE_TYPE output_space = DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P601;
    if (!CreateProcessor(width, height, &enumerator, &processor, output_space)) return 0;
    for (int i = 0; i < count; ++i)
    {
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC view = {};
        view.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        if (!Check(video_device->CreateVideoProcessorInputView(inputs[i], enumerator, &view, &input_views[i]), "VSR input view")) return 0;
    }

    hw_device = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
    if (!hw_device) return 0;
    auto *hardware = reinterpret_cast<AVHWDeviceContext *>(hw_device->data);
    auto *d3d = static_cast<AVD3D11VADeviceContext *>(hardware->hwctx);
    d3d->device = device; device->AddRef();
    if (!CheckAv(av_hwdevice_ctx_init(hw_device), "hardware device context")) return 0;
    hw_frames = av_hwframe_ctx_alloc(hw_device);
    if (!hw_frames) return 0;
    auto *frames = reinterpret_cast<AVHWFramesContext *>(hw_frames->data);
    frames->format = AV_PIX_FMT_D3D11; frames->sw_format = AV_PIX_FMT_NV12;
    frames->width = width; frames->height = height;
    static_cast<AVD3D11VAFramesContext *>(frames->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
    if (!CheckAv(av_hwframe_ctx_init(hw_frames), "NVENC frame pool")) return 0;
    const AVCodec *codec = avcodec_find_encoder_by_name(encoder);
    if (!codec) return 0;
    codec_context = avcodec_alloc_context3(codec);
    if (!codec_context) return 0;
    codec_context->width = width; codec_context->height = height;
    codec_context->time_base = AVRational{fps_den, fps_num};
    codec_context->framerate = AVRational{fps_num, fps_den};
    codec_context->pix_fmt = AV_PIX_FMT_D3D11;
    codec_context->hw_frames_ctx = av_buffer_ref(hw_frames);
    const auto path = Utf8(output_path);
    if (!CheckAv(avformat_alloc_output_context2(&muxer, nullptr, "nut", path.c_str()), "NUT muxer")) return 0;
    if (muxer->oformat->flags & AVFMT_GLOBALHEADER) codec_context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    AVDictionary *settings = nullptr;
    av_dict_parse_string(&settings, options, "=", ":", 0);
    const int opened = avcodec_open2(codec_context, codec, &settings);
    const bool unused = av_dict_count(settings) != 0;
    if (unused)
    {
        const AVDictionaryEntry *entry = nullptr;
        while ((entry = av_dict_get(settings, "", entry, AV_DICT_IGNORE_SUFFIX)))
            fprintf(stderr, "GPU encode unrecognized encoder option: %s\n", entry->key);
    }
    av_dict_free(&settings);
    if (!CheckAv(opened, "NVENC initialization") || unused) return 0;
    stream = avformat_new_stream(muxer, nullptr);
    if (!stream || !CheckAv(avcodec_parameters_from_context(stream->codecpar, codec_context), "codec parameters")) return 0;
    stream->time_base = codec_context->time_base;
    stream->avg_frame_rate = codec_context->framerate;
    if (!CheckAv(avio_open(&muxer->pb, path.c_str(), AVIO_FLAG_WRITE), "encoded cache") ||
        !CheckAv(avformat_write_header(muxer, nullptr), "NUT header")) return 0;
    header_written = true;
    packet = av_packet_alloc();
    if (!packet) return 0;

    if (pw > 0 && ph > 0)
    {
        const auto rgb_space = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        if (!CreateProcessor(pw, ph, &preview_enumerator, &preview_processor, rgb_space)) return 0;
        D3D11_TEXTURE2D_DESC description = {};
        description.Width = pw; description.Height = ph; description.MipLevels = description.ArraySize = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1; description.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (!Check(device->CreateTexture2D(&description, nullptr, &preview_texture), "preview texture")) return 0;
        description.BindFlags = 0; description.Usage = D3D11_USAGE_STAGING; description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (!Check(device->CreateTexture2D(&description, nullptr, &preview_staging), "preview staging")) return 0;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC view = {};
        view.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        if (!Check(video_device->CreateVideoProcessorOutputView(preview_texture, preview_enumerator, &view, &preview_output), "preview output view")) return 0;
        for (int i = 0; i < count; ++i)
        {
            D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input = {};
            input.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
            if (!Check(video_device->CreateVideoProcessorInputView(inputs[i], preview_enumerator, &input, &preview_views[i]), "preview input view")) return 0;
        }
    }
    for (int i = 0; i < count; ++i) { outputs[i] = shared_outputs[i]; shared_outputs[i] = nullptr; }
    return 1;
}

extern "C" __declspec(dllexport) int vsr_nvenc_frame(int slot, void *preview, int want_preview)
{
    if (!codec_context || finished || slot < 0 || slot >= 3 || !inputs[slot]) return 0;
    AVFrame *frame = av_frame_alloc();
    if (!frame) return 0;
    bool ok = CheckAv(av_hwframe_get_buffer(hw_frames, frame, 0), "NVENC frame");
    ID3D11VideoProcessorOutputView *output = nullptr;
    if (ok)
    {
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC view = {};
        view.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        ok = Check(video_device->CreateVideoProcessorOutputView(reinterpret_cast<ID3D11Texture2D *>(frame->data[0]),
            enumerator, &view, &output), "NVENC output view");
    }
    if (ok)
    {
        D3D11_VIDEO_PROCESSOR_STREAM input = {};
        input.Enable = TRUE; input.pInputSurface = input_views[slot];
        ok = Check(video_context->VideoProcessorBlt(processor, output, 0, 1, &input), "RGB to NV12");
    }
    Release(output);
    if (ok && want_preview) ok = CopyPreview(slot, preview);
    if (ok)
    {
        // GPU-to-GPU dependency: later D3D12 submissions cannot overwrite a VSR
        // texture before D3D11 finishes reading it. No CPU fence wait is needed.
        const auto value = ++conversion_value;
        ok = Check(context4->Signal(conversion_fence11, value), "conversion signal");
        context->Flush();
        if (ok) ok = Check(queue->Wait(conversion_fence12, value), "conversion GPU wait");
        frame->pts = frame_index++;
        frame->color_primaries = codec_context->color_primaries;
        frame->color_trc = codec_context->color_trc;
        frame->colorspace = codec_context->colorspace;
        frame->color_range = codec_context->color_range;
        if (ok) ok = CheckAv(avcodec_send_frame(codec_context, frame), "NVENC submit") && DrainPackets();
    }
    av_frame_free(&frame);
    return ok ? 1 : 0;
}

extern "C" __declspec(dllexport) int vsr_nvenc_finish()
{
    if (!codec_context || finished || !header_written) return 0;
    if (!CheckAv(avcodec_send_frame(codec_context, nullptr), "NVENC drain") || !DrainPackets() ||
        !CheckAv(av_write_trailer(muxer), "NUT trailer")) return 0;
    avio_flush(muxer->pb);
    if (!CheckAv(avio_closep(&muxer->pb), "encoded cache close")) return 0;
    finished = true;
    return 1;
}

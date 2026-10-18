// SPDX-License-Identifier: MIT
// Offscreen RTX Video HDR investigation. Uses the installed D3D11 driver;
// no NGX SDK, external NVIDIA runtime, swap chain, or driver-setting changes.
// Extension layout: https://github.com/mpv-player/mpv/blob/master/video/filter/vf_d3d11vpp.c
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;
static const GUID NvidiaTrueHdr = {
    0xfdd62bb4, 0x620b, 0x4fd7, {0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3}};

static void Check(HRESULT result, const char* operation)
{
    if (FAILED(result)) {
        fprintf(stderr, "%s: 0x%08lX\n", operation, result);
        exit(2);
    }
}

static size_t Compare(const std::vector<unsigned>& a, const std::vector<unsigned>& b,
                      const char* label)
{
    size_t changed = 0;
    double sum = 0;
    unsigned maximum = 0;
    for (size_t i = 0; i < a.size(); ++i) {
        for (unsigned channel = 0; channel < 3; ++channel) {
            const int delta = abs(int((a[i] >> (channel * 10)) & 1023)
                                - int((b[i] >> (channel * 10)) & 1023));
            changed += delta != 0;
            sum += delta;
            maximum = std::max(maximum, unsigned(delta));
        }
    }
    printf("%s changed_rgb10=%zu mean_abs_10bit=%.6f max=%u\n",
           label, changed, sum / (a.size() * 3.0), maximum);
    return changed;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 4) {
        fprintf(stderr, "driver_hdr_probe width height output-prefix\n");
        return 2;
    }
    const int width = _wtoi(argv[1]), height = _wtoi(argv[2]);
    if (width < 2 || height < 2 || width > 16384 || height > 16384
        || (width & 1) || (height & 1)) return 2;
    printf("input=%dx%d output=%dx%d offscreen=true frames_per_mode=5\n",
           width, height, width, height);
    printf("input=RGBA8/BT709/gamma2.2 output=RGB10A2/BT2020/PQ\n");

    ComPtr<IDXGIFactory6> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "factory");
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    for (UINT index = 0; !device; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        const HRESULT result = factory->EnumAdapterByGpuPreference(
            index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter));
        if (result == DXGI_ERROR_NOT_FOUND) break;
        Check(result, "adapter");
        DXGI_ADAPTER_DESC1 description = {};
        Check(adapter->GetDesc1(&description), "adapter description");
        if (description.VendorId != 0x10de) continue;
        Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context), "device");
    }
    if (!device) { fprintf(stderr, "No NVIDIA adapter\n"); return 2; }
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext1> videoContext;
    Check(device.As(&videoDevice), "video device");
    Check(context.As(&videoContext), "video context");

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content = {};
    content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputWidth = content.OutputWidth = width;
    content.InputHeight = content.OutputHeight = height;
    content.InputFrameRate = content.OutputFrameRate = {30, 1};
    content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    Check(videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator), "enumerator");
    Check(videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor), "processor");
    UINT supported = 0;
    const HRESULT capability = videoContext->VideoProcessorGetStreamExtension(
        processor.Get(), 0, &NvidiaTrueHdr, sizeof(supported), &supported);
    printf("truehdr_get_hr=0x%08lX supported=%u\n", capability, supported);
    if (FAILED(capability) || !supported) return 3;

    D3D11_TEXTURE2D_DESC description = {};
    description.Width = width; description.Height = height;
    description.MipLevels = description.ArraySize = 1;
    description.SampleDesc.Count = 1;
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> input, output, staging;
    Check(device->CreateTexture2D(&description, nullptr, &input), "input texture");
    std::vector<unsigned char> pixels(size_t(width) * height * 4);
    for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
        const size_t i = (size_t(y) * width + x) * 4;
        pixels[i] = ((x / 7 + y / 11) % 2) * 130 + (x % 117);
        pixels[i + 1] = ((x / 13 + y / 9) % 2) * 110 + (y % 137);
        pixels[i + 2] = ((x / 17 + y / 5) % 2) * 90 + ((x + y) % 157);
        pixels[i + 3] = 255;
    }
    context->UpdateSubresource(input.Get(), 0, nullptr, pixels.data(), width * 4, 0);
    description.Format = DXGI_FORMAT_R10G10B10A2_UNORM;
    Check(device->CreateTexture2D(&description, nullptr, &output), "output texture");
    description.BindFlags = 0;
    description.Usage = D3D11_USAGE_STAGING;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Check(device->CreateTexture2D(&description, nullptr, &staging), "staging texture");
    ComPtr<ID3D11VideoProcessorInputView> inputView;
    ComPtr<ID3D11VideoProcessorOutputView> outputView;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDescription = {};
    inputDescription.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputDescription = {};
    outputDescription.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    Check(videoDevice->CreateVideoProcessorInputView(input.Get(), enumerator.Get(),
        &inputDescription, &inputView), "input view");
    Check(videoDevice->CreateVideoProcessorOutputView(output.Get(), enumerator.Get(),
        &outputDescription, &outputView), "output view");
    RECT rectangle = {0, 0, width, height};
    videoContext->VideoProcessorSetStreamFrameFormat(processor.Get(), 0,
        D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    videoContext->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &rectangle);
    videoContext->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &rectangle);
    videoContext->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &rectangle);
    videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
    videoContext->VideoProcessorSetStreamColorSpace1(processor.Get(), 0,
        DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    videoContext->VideoProcessorSetOutputColorSpace1(processor.Get(),
        DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);

    std::vector<unsigned> results[3];
    const wchar_t* names[] = {L"off", L"on", L"off-again"};
    for (unsigned mode = 0; mode < 3; ++mode) {
        const unsigned enabled = mode == 1;
        struct { UINT version, method, flags; } extension = {4, 3, enabled};
        Check(videoContext->VideoProcessorSetStreamExtension(processor.Get(), 0,
            &NvidiaTrueHdr, sizeof(extension), &extension), "TrueHDR set");
        auto& result = results[mode];
        result.resize(size_t(width) * height);
        D3D11_VIDEO_PROCESSOR_STREAM stream = {};
        stream.Enable = TRUE; stream.pInputSurface = inputView.Get();
        const auto start = std::chrono::steady_clock::now();
        for (unsigned frame = 0; frame < 5; ++frame) {
            stream.InputFrameOrField = mode * 5 + frame;
            Check(videoContext->VideoProcessorBlt(processor.Get(), outputView.Get(),
                stream.InputFrameOrField, 1, &stream), "video processor blt");
            context->CopyResource(staging.Get(), output.Get());
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "readback");
            for (int y = 0; y < height; ++y)
                memcpy(result.data() + size_t(y) * width,
                    static_cast<const unsigned char*>(mapped.pData) + size_t(y) * mapped.RowPitch,
                    size_t(width) * 4);
            context->Unmap(staging.Get(), 0);
        }
        printf("mode=%u enabled=%u ms/frame_including_readback=%.3f\n", mode, enabled,
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / 5);
        wchar_t path[32768] = {};
        swprintf_s(path, L"%s-%s.rgb10", argv[3], names[mode]);
        FILE* file = nullptr;
        _wfopen_s(&file, path, L"wb");
        if (!file) { fprintf(stderr, "Cannot write RGB10 output\n"); return 2; }
        const size_t written = fwrite(result.data(), sizeof(unsigned), result.size(), file);
        fclose(file);
        if (written != result.size()) return 2;
    }
    const size_t enhanced = Compare(results[0], results[1], "off-vs-on");
    const size_t restored = Compare(results[0], results[2], "off-vs-off-again");
    wchar_t modulePath[32768] = {};
    const HMODULE model = GetModuleHandleW(L"nvAIHDRx.dll");
    if (model) GetModuleFileNameW(model, modulePath, 32768);
    printf("driver_hdr_model_loaded=%d\n", model != nullptr);
    if (model) wprintf(L"driver_hdr_model=%s\n", modulePath);
    return enhanced && !restored ? 0 : 4;
}

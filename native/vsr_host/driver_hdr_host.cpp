// SPDX-License-Identifier: MIT
// Installed-driver RTX Video HDR, using the D3D11 VideoProcessor extension.
// ABI reference: mpv video/filter/vf_d3d11vpp.c (NVIDIA_TRUE_HDR_INTERFACE_GUID).
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstring>
#include <memory>
#include <new>

using Microsoft::WRL::ComPtr;
static const GUID TrueHdr = {
    0xfdd62bb4, 0x620b, 0x4fd7, {0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3}};
struct Failure { HRESULT result; };
static void Check(HRESULT result) { if (FAILED(result)) throw Failure{result}; }

struct Session {
    UINT width, height, frame = 0;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext1> videoContext;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<ID3D11Texture2D> input, output, staging;
    ComPtr<ID3D11VideoProcessorInputView> inputView;
    ComPtr<ID3D11VideoProcessorOutputView> outputView;

    Session(UINT w, UINT h) : width(w), height(h) {
        ComPtr<IDXGIFactory6> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        for (UINT index = 0; !device; ++index) {
            ComPtr<IDXGIAdapter1> adapter;
            HRESULT result = factory->EnumAdapterByGpuPreference(index,
                DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter));
            if (result == DXGI_ERROR_NOT_FOUND) break;
            Check(result);
            DXGI_ADAPTER_DESC1 description = {};
            Check(adapter->GetDesc1(&description));
            if (description.VendorId != 0x10de) continue;
            Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
        }
        if (!device) throw Failure{DXGI_ERROR_UNSUPPORTED};
        Check(device.As(&videoDevice));
        Check(context.As(&videoContext));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content = {};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth = content.OutputWidth = width;
        content.InputHeight = content.OutputHeight = height;
        content.InputFrameRate = content.OutputFrameRate = {30, 1};
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        Check(videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator));
        Check(videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor));
        UINT supported = 0;
        Check(videoContext->VideoProcessorGetStreamExtension(processor.Get(), 0,
            &TrueHdr, sizeof(supported), &supported));
        if (!supported) throw Failure{DXGI_ERROR_UNSUPPORTED};
        struct { UINT version, method, flags; } extension = {4, 3, 1};
        Check(videoContext->VideoProcessorSetStreamExtension(processor.Get(), 0,
            &TrueHdr, sizeof(extension), &extension));

        D3D11_TEXTURE2D_DESC description = {};
        description.Width = width; description.Height = height;
        description.MipLevels = description.ArraySize = 1;
        description.SampleDesc.Count = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        Check(device->CreateTexture2D(&description, nullptr, &input));
        description.Format = DXGI_FORMAT_R10G10B10A2_UNORM;
        Check(device->CreateTexture2D(&description, nullptr, &output));
        description.BindFlags = 0;
        description.Usage = D3D11_USAGE_STAGING;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Check(device->CreateTexture2D(&description, nullptr, &staging));
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC iv = {};
        iv.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ov = {};
        ov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        Check(videoDevice->CreateVideoProcessorInputView(input.Get(), enumerator.Get(), &iv, &inputView));
        Check(videoDevice->CreateVideoProcessorOutputView(output.Get(), enumerator.Get(), &ov, &outputView));
        RECT rectangle = {0, 0, LONG(width), LONG(height)};
        videoContext->VideoProcessorSetStreamFrameFormat(processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        videoContext->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &rectangle);
        videoContext->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &rectangle);
        videoContext->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &rectangle);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
        videoContext->VideoProcessorSetStreamColorSpace1(processor.Get(), 0, DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
        videoContext->VideoProcessorSetOutputColorSpace1(processor.Get(), DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);
    }

    HRESULT Process(const void* rgba, void* rgb10) {
        context->UpdateSubresource(input.Get(), 0, nullptr, rgba, width * 4, 0);
        D3D11_VIDEO_PROCESSOR_STREAM stream = {};
        stream.Enable = TRUE;
        stream.pInputSurface = inputView.Get();
        stream.InputFrameOrField = frame++;
        HRESULT result = videoContext->VideoProcessorBlt(processor.Get(), outputView.Get(),
            stream.InputFrameOrField, 1, &stream);
        if (FAILED(result)) return result;
        context->CopyResource(staging.Get(), output.Get());
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        result = context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(result)) return result;
        for (UINT y = 0; y < height; ++y)
            memcpy(static_cast<unsigned char*>(rgb10) + size_t(y) * width * 4,
                static_cast<const unsigned char*>(mapped.pData) + size_t(y) * mapped.RowPitch,
                size_t(width) * 4);
        context->Unmap(staging.Get(), 0);
        return S_OK;
    }
};

extern "C" __declspec(dllexport) HRESULT __cdecl driver_hdr_create(UINT width, UINT height, void** session) {
    if (!session) return E_POINTER;
    *session = nullptr;
    if (!width || !height || width > 16384 || height > 16384) return E_INVALIDARG;
    try { *session = new Session(width, height); return S_OK; }
    catch (const Failure& error) { return error.result; }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}
extern "C" __declspec(dllexport) HRESULT __cdecl driver_hdr_process(void* session, const void* rgba, void* rgb10) {
    if (!session || !rgba || !rgb10) return E_POINTER;
    return static_cast<Session*>(session)->Process(rgba, rgb10);
}
extern "C" __declspec(dllexport) void __cdecl driver_hdr_destroy(void* session) {
    delete static_cast<Session*>(session);
}

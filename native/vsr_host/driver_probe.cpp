// SPDX-License-Identifier: MIT
// Standalone offscreen experiment. No SDK headers, import libs or driver DLL copies.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

static const GUID NvidiaPpe = {0xd43ce1b3, 0x1f4b, 0x48ac, {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}};
template<class T> static void Release(T*& p) { if (p) p->Release(); p = nullptr; }
static void Check(HRESULT hr, const char* what) { if (FAILED(hr)) { fprintf(stderr, "%s: 0x%08lX\n", what, hr); exit(2); } }

static void Stats(const std::vector<unsigned char>& a, const std::vector<unsigned char>& b, const char* label) {
    double sum = 0, squared = 0; size_t changed = 0; int maximum = 0;
    for (size_t i = 0; i < a.size(); ++i) if (i % 4 != 3) {
        const int d = abs(int(a[i]) - int(b[i])); sum += d; squared += d * d;
        changed += d != 0; maximum = std::max(maximum, d);
    }
    const double count = double(a.size() / 4 * 3);
    printf("%s changed_rgb=%zu mean_abs=%.6f max=%d psnr=%.3f\n", label, changed, sum/count, maximum,
        squared == 0 ? INFINITY : 10 * log10(255.0 * 255.0 * count / squared));
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 5) { fprintf(stderr, "driver_probe width height scale output-prefix [sdk-host runtime-dir]\n"); return 2; }
    SetProcessDPIAware();
    const int w = _wtoi(argv[1]), h = _wtoi(argv[2]), scale = _wtoi(argv[3]);
    const int ow = w * scale, oh = h * scale;
    printf("screen=%dx%d input=%dx%d output=%dx%d offscreen=true\n", GetSystemMetrics(0), GetSystemMetrics(1), w, h, ow, oh);
    if (w <= 0 || h <= 0 || scale < 1 || ow > 16384 || oh > 16384) return 2;
    std::vector<unsigned char> pixels(size_t(w)*h*4), baseline(size_t(ow)*oh*4), enhanced(baseline.size());
    for (int y=0; y<h; ++y) for (int x=0; x<w; ++x) {
        const size_t i=(size_t(y)*w+x)*4;
        pixels[i]=((x/7+y/11)%2)*130+(x%117);
        pixels[i+1]=((x/13+y/9)%2)*110+(y%137);
        pixels[i+2]=((x/17+y/5)%2)*90+((x+y)%157); pixels[i+3]=255;
    }
    ID3D11Device* device=nullptr; ID3D11DeviceContext* context=nullptr;
    IDXGIFactory6* factory=nullptr; Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "factory");
    for (UINT i=0; !device; ++i) {
        IDXGIAdapter1* adapter=nullptr;
        if (factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter))==DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc={}; adapter->GetDesc1(&desc);
        if (desc.VendorId==0x10de) Check(D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,nullptr,
            D3D11_CREATE_DEVICE_VIDEO_SUPPORT|D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context),"device");
        Release(adapter);
    }
    Release(factory); if (!device) return 2;
    ID3D11VideoDevice* vd=nullptr; ID3D11VideoContext1* vc=nullptr;
    Check(device->QueryInterface(IID_PPV_ARGS(&vd)),"video device"); Check(context->QueryInterface(IID_PPV_ARGS(&vc)),"video context");
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content={}; content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputWidth=w; content.InputHeight=h; content.OutputWidth=ow; content.OutputHeight=oh;
    content.InputFrameRate={30,1}; content.OutputFrameRate={30,1}; content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    ID3D11VideoProcessorEnumerator* enumerator=nullptr; ID3D11VideoProcessor* processor=nullptr;
    Check(vd->CreateVideoProcessorEnumerator(&content,&enumerator),"enumerator"); Check(vd->CreateVideoProcessor(enumerator,0,&processor),"processor");
    D3D11_TEXTURE2D_DESC desc={}; desc.Width=w; desc.Height=h; desc.MipLevels=desc.ArraySize=1;
    desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count=1; desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    ID3D11Texture2D* input=nullptr; ID3D11Texture2D* output=nullptr; ID3D11Texture2D* staging=nullptr;
    Check(device->CreateTexture2D(&desc,nullptr,&input),"input texture"); context->UpdateSubresource(input,0,nullptr,pixels.data(),w*4,0);
    desc.Width=ow; desc.Height=oh; Check(device->CreateTexture2D(&desc,nullptr,&output),"output texture");
    desc.BindFlags=0; desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    Check(device->CreateTexture2D(&desc,nullptr,&staging),"staging texture");
    ID3D11VideoProcessorInputView* iv=nullptr; ID3D11VideoProcessorOutputView* ov=nullptr;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC id={}; id.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od={}; od.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
    Check(vd->CreateVideoProcessorInputView(input,enumerator,&id,&iv),"input view");
    Check(vd->CreateVideoProcessorOutputView(output,enumerator,&od,&ov),"output view");
    RECT source={0,0,w,h}, target={0,0,ow,oh};
    vc->VideoProcessorSetStreamFrameFormat(processor,0,D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    vc->VideoProcessorSetStreamSourceRect(processor,0,TRUE,&source); vc->VideoProcessorSetStreamDestRect(processor,0,TRUE,&target);
    vc->VideoProcessorSetOutputTargetRect(processor,TRUE,&target); vc->VideoProcessorSetStreamAutoProcessingMode(processor,0,FALSE);
    vc->VideoProcessorSetStreamColorSpace1(processor,0,DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    vc->VideoProcessorSetOutputColorSpace1(processor,DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
    for (unsigned enabled=0; enabled<2; ++enabled) {
        struct { UINT version, method, enable; } ext={1,2,enabled};
        Check(vc->VideoProcessorSetStreamExtension(processor,0,&NvidiaPpe,sizeof(ext),&ext),"NVIDIA VSR extension");
        D3D11_VIDEO_PROCESSOR_STREAM stream={}; stream.Enable=TRUE; stream.pInputSurface=iv;
        auto& result=enabled?enhanced:baseline;
        const auto start=std::chrono::steady_clock::now();
        for (UINT frame=0; frame<3; ++frame) { stream.InputFrameOrField=frame;
            Check(vc->VideoProcessorBlt(processor,ov,frame,1,&stream),"video processor blt");
            context->CopyResource(staging,output); D3D11_MAPPED_SUBRESOURCE map={};
            Check(context->Map(staging,0,D3D11_MAP_READ,0,&map),"readback");
            for (int y=0;y<oh;++y) memcpy(result.data()+size_t(y)*ow*4,static_cast<unsigned char*>(map.pData)+size_t(y)*map.RowPitch,size_t(ow)*4);
            context->Unmap(staging,0);
        }
        printf("driver enabled=%u ms/frame=%.3f\n",enabled,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count()/3);
        wchar_t path[32768]={}; swprintf_s(path,L"%s-%u.rgba",argv[4],enabled);
        FILE* file=nullptr; _wfopen_s(&file,path,L"wb"); if (!file) return 2;
        fwrite(result.data(),1,result.size(),file); fclose(file);
    }
    Stats(baseline,enhanced,"driver-off-vs-on");
    wchar_t module_path[32768]={}; auto module=GetModuleHandleW(L"nvdlppx.dll");
    if (module) GetModuleFileNameW(module,module_path,32768); printf("driver_model_loaded=%d\n",module!=nullptr); if (module) wprintf(L"driver_model=%s\n",module_path);
    Release(ov); Release(iv); Release(staging); Release(output); Release(input);
    Release(processor); Release(enumerator); Release(vc); Release(vd); Release(context); Release(device);
    if (argc>=7) {
        using Init=int(__cdecl*)(int,int,int,int,int,const wchar_t*,const wchar_t*);
        using Frame=int(__cdecl*)(const void*,void*); using Shutdown=void(__cdecl*)();
        auto host=LoadLibraryW(argv[5]); if (!host) { fprintf(stderr,"SDK host load: %lu\n",GetLastError()); return 2; }
        auto init=reinterpret_cast<Init>(GetProcAddress(host,"vsr_init")); auto frame=reinterpret_cast<Frame>(GetProcAddress(host,"vsr_process"));
        auto shutdown=reinterpret_cast<Shutdown>(GetProcAddress(host,"vsr_shutdown"));
        wchar_t path[32768]={}; swprintf_s(path,L"%s-sdk.log",argv[4]);
        if (!init||!frame||!shutdown||!init(w,h,scale,4,0,argv[6],path)) { fprintf(stderr,"SDK init failed\n"); return 2; }
        std::vector<unsigned char> sdk(baseline.size());
        if (!frame(pixels.data(),sdk.data())) { fprintf(stderr,"SDK evaluate failed\n"); return 2; }
        Stats(baseline,sdk,"driver-off-vs-sdk"); Stats(enhanced,sdk,"driver-on-vs-sdk");
        swprintf_s(path,L"%s-sdk.rgba",argv[4]); FILE* file=nullptr; _wfopen_s(&file,path,L"wb"); if (!file) return 2;
        fwrite(sdk.data(),1,sdk.size(),file); fclose(file);
        // The SDK host has a known blocking shutdown. Process exit owns this isolated experiment.
    }
    return 0;
}

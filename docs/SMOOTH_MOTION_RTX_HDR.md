# Smooth Motion 调查与驱动 RTX Video HDR 接入

2026-10-18，Windows，GeForce RTX 5070 Ti，驱动 617.14。

## 结论

- **Smooth Motion：没有找到可直接替换当前离屏补帧后端的公开接口。** NVIDIA 将其作为驱动对 DX11/DX12/Vulkan 游戏呈现的透明补帧功能提供。启用程序配置不等于应用能取得生成帧；当前 Vulkan 滤镜输出 AVFrame、送入编码器的路径不会因此得到更多帧。播放器呈现可以另行实验，但不能据此承诺转码导出支持。
- **RTX Video HDR：本机已验证可以直接调用驱动，离屏输出真实改变的 10 位 RGB 帧。** 使用 D3D11 VideoProcessor 的 TrueHDR 扩展，不依赖项目的 RTX Video SDK DLL，没有窗口或交换链，没有修改 NVIDIA 全局配置或 Windows 显示设置。
- **“强制 HDR”已接入生产管线。** SDR 源在 NR/SR 和 Vulkan 补帧之后执行驱动映射；原生 HDR 源沿用原路径。按用户要求，RIFE 保持 SDR，跳过强制 HDR 映射。

## 当前实现

`VulkanInterpolationSession` 使用 FFmpeg `fruc_vulkan`，通过 `VK_NV_optical_flow` 估计运动并合成帧；另一后端为 RIFE。它们均不属于 Smooth Motion。

`DlssVideoPipeline.CreateProcessingPlan` 继续由 `video.IsHdr && preserveHdr` 决定源像素路径。新的 `NeedsDriverHdr` 单独决定输出映射：源为 SDR、`ForceHdr && PreserveHdr`，且未启用 RIFE。

独立原生宿主 `driver_hdr_host.dll` 为每个任务拥有一套 D3D11 会话和复用纹理，提供创建、整帧映射、销毁三个入口。`DriverHdrMapper` 使用显式路径加载宿主。驱动能力不支持或处理失败时明确报错，不继续写出只带 HDR 标签的 SDR 像素。

`DriverHdrFrameStream` 位于补帧输出之后，同时覆盖 Vulkan 的延迟输出和尾帧；没有补帧时直接接在 NR/SR 输出后。软件双遍只映射一次并缓存 RGB10，两次编码均读取相同的已映射数据。流适配器可拼接按行写入的数据，退出时检查半帧输入。任务门、工作线程结束和 `using` 释放保证取消后再创建的会话不共享历史。

编码输入明确为 `x2bgr10le`，先转为 `gbrp16le`，再用 zscale 的显式 RGB→BT.2020NC 矩阵转换到有限范围 10-bit YUV；PQ 传递和 BT.2020 原色保持不变。NVENC 使用 P010，软件编码使用 yuv420p10le。输出标签固定为 `bt2020 / smpte2084 / bt2020nc / tv`，不沿用 SDR 源标签。新映射路径不写未经测量的固定 mastering-display / MaxCLL 值；原生 HDR 路径的行为保留。

强制映射支持 HEVC、AV1 和 VP9 10-bit；H.264/GIF 在输出文件预留前拒绝。RIFE 的强制 HDR 请求在进入管线时归一化为 SDR，并记录日志；原生 HDR 输入使用 RIFE 的拒绝规则保留。HDR 预览仍沿用原实现；SDR 强制 HDR 的预览显示映射前的源帧阶段，不代表 HDR 显示效果。

本机 RTX Video SDK 包含 `nvsdk_ngx_helpers_truehdr.h`、`TrueHDR.Available`、`NGX_D3D11_CREATE_TRUEHDR_EXT`、D3D12/CUDA/Vulkan 接口及 `nvngx_truehdr.dll`，因此 SDK 也是可选实现。但现有 `vsr_host.cpp` 只调用 VSR，`hdr` 参数表示处理已有 HDR，不表示启用 TrueHDR。本次没有验证驱动与 SDK 的输出等价性。

## 驱动探针

源码：`native/vsr_host/driver_hdr_probe.cpp`；构建：`native/vsr_host/build-hdr-probe.bat`。

调用顺序：

1. 创建 NVIDIA D3D11 视频设备和 VideoProcessor。
2. 用 `VideoProcessorGetStreamExtension` 查询 TrueHDR 支持。
3. 用 `VideoProcessorSetStreamExtension` 设置 TrueHDR GUID `fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3`，参数为 `{version=4, method=3, flags=enable}`。
4. 输入 RGBA8、BT.709 gamma 2.2；输出 `R10G10B10A2_UNORM`、BT.2020 PQ。开/关时保持输入、格式、色彩空间和其他处理参数相同。
5. 对同一彩色合成测试图执行“关闭→开启→关闭”，每种模式连续处理五帧，回读最后一帧；只比较 RGB 的三个 10 位分量，忽略 alpha。
6. 检查驱动 HDR 模块 `nvAIHDRx.dll` 的加载位置。

| 输入与输出尺寸 | 支持查询 | 开/关改变的 RGB10 分量数 | 关闭两次的差异 | 驱动 HDR 模块 |
| --- | --- | ---: | ---: | --- |
| 1920×1080 | S_OK / 1 | 6,208,628 | 0 | 已加载 |
| 3840×2160 | S_OK / 1 | 24,834,411 | 0 | 已加载 |
| 7680×4320 | S_OK / 1 | 99,337,532 | 0 | 已加载 |

模块实际位于 Windows DriverStore 的 NVIDIA 驱动目录。以上同时验证能力、像素变化、关闭后恢复和模型加载，不只检查接口返回成功。

输出的 `.rgb10` 文件是原始打包 RGB10A2，FFmpeg 对应 `x2bgr10le`；不是 RGBA8。探针不执行编码，耗时包括冷启动和 CPU 回读，仅作功能记录，不能作为转码性能基准。

```powershell
.\native\vsr_host\build-hdr-probe.bat
$root = (Get-Location).Path
.\build\driver-hdr-probe\driver_hdr_probe.exe 1920 1080 "$root\build\driver-hdr-probe\1080p"
.\build\driver-hdr-probe\driver_hdr_probe.exe 3840 2160 "$root\build\driver-hdr-probe\2160p"
.\build\driver-hdr-probe\driver_hdr_probe.exe 7680 4320 "$root\build\driver-hdr-probe\4320p"
```

本次原始日志及帧位于忽略的 `build/driver-hdr-probe`。通过条件为开启后 RGB10 发生变化、再次关闭后 RGB10 恢复一致；不支持返回 3，像素检查失败返回 4。

## 集成验证

已在上述 GPU/驱动完成以下实际转码。小尺寸源包含运动图样和 AAC 音频，SDR 源明确标为 BT.709，使用 24000/1001 有理数帧率。

| 验证 | 结果 |
| --- | --- |
| 无补帧，HEVC / AV1 | 各 6 帧，10-bit、BT.2020/PQ/BT.2020NC、有限范围 |
| Vulkan 2× / 4× / 6× | 6 源帧分别输出 12 / 24 / 36 帧，映射计数与实际输出帧数相同 |
| 单帧 Vulkan 2× | 1 源帧输出 2 帧，两帧均映射 |
| NR + 超分 2× + Vulkan 2× | 3 源帧输出 6 帧，640×384，色彩标签与 10-bit 正确 |
| 软件 HEVC 双遍 | 6 帧，10-bit 与 HDR 标签正确 |
| PQ / HLG 原生 HDR + Vulkan | 强制和自动各输出 12 帧；两种模式解码后 RGB16 逐字节一致，均未创建 TrueHDR 会话 |
| 驱动 RGB10 参考 vs HEVC 解码 YUV10 | 相同显式矩阵转换后比较，PSNR 44.66 dB，超过 35 dB 的通道/范围错误检测阈值；不是 HDR 观感评分 |
| 取消后重启 | 取消正常归并，后续新会话输出 6 帧成功 |
| H.264 / GIF | 在输出预留前拒绝 |
| RIFE | 按要求跳过 GPU 组合测试；CPU 策略测试确认保持 SDR，且禁用补帧时残留的 RIFE 选择不影响 HDR |
| 应用批处理：原生 4K + Vulkan 2× | 3840×2160，2→4 帧，48 fps，HEVC 10-bit，HDR 标签正确 |
| 应用批处理：软件双遍 + Vulkan | 6→12 帧，48000/1001 fps，HEVC 10-bit，HDR 标签正确 |
| CLI：VP9 强制 HDR | 2 帧，10-bit，HDR 标签正确 |

所有集成测试同时检查帧数、帧率、音频存在和视频时长；20 项 CPU 回归检查通过。App、Core、CLI 和 Tests 的 Release 构建通过。软件 HEVC 双遍使用 `-x265-stats` 指定共享的统计文件，避免依赖通用 passlog 的隐式行为。

主要测试入口为 `tests/Dlss5Demo.Tests/DriverHdrIntegration.cs`。原始集成产物位于 `.qa/driver-hdr/aea23426e81042598cddd071a6fa2002`，入口验证位于 `.qa/driver-hdr/entrypoints-20261018`，完整日志为 `build/driver-hdr-tests/integration.log`。精简探测结果保存于 `docs/data/driver-hdr-20261018/validation.json`。

```powershell
.\native\vsr_host\build-hdr.bat
dotnet build tests/Dlss5Demo.Tests -c Release -o build/driver-hdr-tests
dotnet build/driver-hdr-tests/Dlss5Demo.Tests.dll --driver-hdr
```

受限沙箱中如果 x265 无法访问其自动重定向的临时目录，运行测试前把该进程的 `TEMP` / `TMP` 指向仓库内可写临时目录；本次使用 `build/hdr-temp`，两遍测试通过。

未验证真实影片的主观观感、长时间动态画面的时序稳定性、物理 HDR 显示效果、驱动/SDK 等价性、不同显示器/HDR 开关状态及其他 GPU/驱动。当前映射路径含 GPU→CPU 回读和 FFmpeg 色彩转换，不是 GPU 纹理直编。8K 为前述独立探针验证，完整转码组合验证到 4K。

## 上游依据

- [NVIDIA Smooth Motion 官方说明](https://www.nvidia.com/en-us/geforce/news/nvidia-app-global-dlss-overrides-rtx-40-series-smooth-motion/)：驱动补帧、支持的图形 API 与程序配置方式。
- [NVIDIA 驱动文档](https://docs.nvidia.com/datacenter/tesla/driver-installation-guide/gaming.html)：Smooth Motion 对游戏引擎透明。该页面环境变量说明面向 Linux，不作为 Windows 调用方式。
- [RTX Video SDK](https://developer.nvidia.com/rtx-video-sdk)：正式提供 SDR→HDR10 映射，适用于创作与媒体应用。
- [mpv D3D11 视频处理实现](https://github.com/mpv-player/mpv/blob/master/video/filter/vf_d3d11vpp.c)：TrueHDR 扩展 GUID、能力查询、参数及 BT.2020/PQ RGB10 输出设置。
- [Microsoft VideoProcessorSetStreamExtension](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamextension)：厂商视频处理扩展入口。

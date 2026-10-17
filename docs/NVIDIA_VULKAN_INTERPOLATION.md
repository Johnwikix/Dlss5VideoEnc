# NVIDIA Vulkan 光流补帧接入与验证

日期：2026-10-17。GPU：RTX 5070 Ti 16 GB，Windows，驱动 617.14。

后续的 [NVDEC 接入](NVDEC_DECODING.md) 已另行通过 4K/8K SDR、PQ、HLG 及组合管线验证。
下文保留接入 NVDEC 前的原始设备测试记录，NVDEC 的结果与新发布包见该独立报告。

## 实现

参考本机 `G:\SoftwareProject\Avalonia\little-fancy-tool-ava\FancyTool\Services\HardwareVideoInterpolation.cs`
和 `VideoTranscodeService.HardwareInterpolation.cs` 的 NVIDIA 后端。原项目只读，未修改其性能优化中的任何文件。
使用同一 FFmpeg `fruc_vulkan` / `VK_NV_optical_flow` 路径，独立 NvOFFRUC SDK 不参与。
选择 NVIDIA vendor ID 初始化真实 Vulkan 设备，转码前提交三帧测试图验证滤镜，不以显卡名称或滤镜列表代替能力检测。

NR、RTX Video、补帧各自独立。NR/SR 顺序沿用已有设置；随后运行裁剪、去隔行、降噪、输出缩放、补帧、编码。
NR/SR 仅处理源帧，生成帧不重复执行 NR/SR。补帧打开时关闭 VSR 的直接 GPU 编码，避免二次有损压缩，
因此该组合包括 VSR 回读、Vulkan 上传/下载。关闭补帧时保持已有 GPU 编码路径。

启用补帧后按源帧率乘以 2/4/6 输出，覆盖存储的普通 FPS/GIF FPS 选择，关闭补帧后原设置恢复。
处理、预览计数与 `TranscodeResult.FramesProcessed` 均为源帧；GUI 完成状态额外显示输出帧数。
实时对照预览采样处理后的源帧，不播放生成帧。

补帧颜色格式：SDR RGBA8；HDR RGBA16 UNORM。光流估计使用 8 位灰度，但合成直接采样完整 16 位颜色。
保留原有 NR/SR RGBA16F 路径与末端 10 位 HEVC/AV1 编码，明确拒绝保留 HDR + H.264/GIF。
本次验证 PQ、HLG、BT.2020 标签与实际像素精度；未验证 HDR10+/Dolby Vision 动态元数据或物理 HDR 显示器呈现。

## 依赖锁定

- BtbN FFmpeg N-127579-g5cb9fcd211-20261016，GPL shared，ABI avcodec/avformat 63、avutil 61、avfilter 12。
- [固定发布](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-16-13-04)
- ZIP：`ffmpeg-N-127579-g5cb9fcd211-win64-gpl-shared.zip`
- ZIP SHA256：`345ab5b89c11583c1e309a3d47fce5d44dabe8f7acf81cff3fc974485a500070`
- ffmpeg.exe SHA256：`e79a816bd84423c2f5ed04eab4b1c8a6379d9667b2cf8af95aefac300e543237`

`tools/prepare-fruc-ffmpeg.ps1` 下载并校验到独立 `runtime/fruc-ffmpeg`，不改动系统或其他项目 FFmpeg。
`tools/package.ps1` 必须检测到 `fruc_vulkan` 才生成发布包，随包带 FFmpeg 许可和完整构建版本信息。

## 分辨率与 HDR 实测

每项使用六帧 24000/1001 fps 输入和 AAC 音频，经过项目真实 CLI/Core 管线输出；FFprobe 回读检查。
HDR 源包含移动的 10 位亮度/色度渐变，并用编码前 setparams 写入真实 VUI；未将仅有 10 位像素格式的无标签视频算作 HDR。

| 输入尺寸 | 输入色彩 | 输出位深 | 输出帧数 / 帧率 | 结果 |
|---|---|---|---|---|
| 3840×2160 | SDR | 8 | 12 / 48000/1001 | 通过 |
| 3840×2160 | PQ / BT.2020 | 10 | 12 / 48000/1001 | 通过 |
| 3840×2160 | HLG / BT.2020 | 10 | 12 / 48000/1001 | 通过 |
| 7680×4320 | SDR | 8 | 12 / 48000/1001 | 通过 |
| 7680×4320 | PQ / BT.2020 | 10 | 12 / 48000/1001 | 通过 |
| 7680×4320 | HLG / BT.2020 | 10 | 12 / 48000/1001 | 通过 |

全部输出视频时长 0.250250 秒，音频从零起始，时长在一个 AAC 包容差内。
设备探针报告光流尺寸范围 32×32–8192×8192。8K 使用整帧光流，无缩小到 4K 或分块替代。

三项同时启用：3840×2160 PQ 输入三帧 → NR（含运动矢量与历史）→ RTX Video 2× → 补帧 2×，
输出 7680×4320、yuv420p10le、PQ/BT.2020、6 帧、48000/1001 fps、0.125125 秒，保留音频。

这些是短片功能验证。存在其他 GPU 会话并行工作，不用于实时播放或性能承诺；最终耗时包含编码/补帧排空，初始化不在帧处理计时内。

## 像素与边界回归

- 独立真实滤镜像素测试：512×96 RGBA16 三帧 → 六帧；原始帧在第 0/2/4 帧逐字节相等。
- 第一个生成帧与两侧源帧都不同，含 **11,978 个不同 16 位灰度值**，排除 8 位合成或纯重复帧。
- SDR、PQ、HLG：NR/SR/补帧全部开关组合、NR 两种顺序、补帧 2/4/6 倍、单帧、完整片尾、音频、存储 FPS 覆盖、取消及重启。
- 软件双遍编码；无总帧数的单帧与多帧 Matroska。
- HEVC 与 AV1 的 PQ/HLG 10 位输出；缺少 fruc_vulkan 的 FFmpeg 在创建输出文件前明确拒绝。
- 关闭补帧后的 VSR GPU/CPU 编码、H.264/HEVC/AV1、音频、预览、取消重启与像素回归。
- GUI 实际操作：NR/SR 关闭，启用 4× 补帧，24 源帧输出 96 帧，120000/1001 fps，0.800800 秒；开关/倍率保存到 settings.json。

解决的时钟问题：使用精确有理数细分，避免微秒舍入在尾部多出一帧；清除滤镜复制的源帧 duration，再给编码器正确输出帧率，
防止 CFR 自动重复首帧并错移后续原始帧。单帧按片长保持；Matroska 缺少 nb_frames 时只探测前两个包识别单帧，解码关闭自动重复帧。

## 复现

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
.\tools\prepare-fruc-ffmpeg.ps1
dotnet build src\Dlss5Demo.Cli -c Release -o build\fruc\cli
.\tools\test-fruc-capabilities.ps1
dotnet build tests\Dlss5Demo.Tests -c Release -o build\fruc\tests
$env:DLSS5_FFMPEG="$PWD\runtime\fruc-ffmpeg\bin\ffmpeg.exe"
dotnet build\fruc\tests\Dlss5Demo.Tests.dll --interpolation-pixels
dotnet build\fruc\tests\Dlss5Demo.Tests.dll --interpolation <SDR或PQ/HLG测试文件>
.\tools\package.ps1 -FfmpegDirectory "$PWD\runtime\fruc-ffmpeg\bin"
```

原始日志、像素与输出文件在忽略的 `build/fruc`；分辨率测量摘要保存于 [data/nvidia-fruc-20261017/capabilities.json](data/nvidia-fruc-20261017/capabilities.json)。

上游说明：[FFmpeg fruc_vulkan](https://ffmpeg.org/ffmpeg-filters.html#fruc_005fvulkan)、
[滤镜源码](https://github.com/FFmpeg/FFmpeg/blob/5cb9fcd211/libavfilter/vf_fruc_vulkan.c)。

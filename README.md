# Dlss5VideoEnc

Avalonia 视频转码工作台：可选 DLSS NR → 可选 RTX Video Super Resolution（关闭 / 2× / 4×）→ 可选 NVIDIA Vulkan / RIFE GPU 补帧（2× / 4× / 6× 帧率）→ FFmpeg 编码。HDR 视频通过 RGBA16F / 16-bit 补帧 / 10-bit 编码路径处理。

## NVDEC 解码

新增自动（优先 NVDEC）、强制 NVDEC、CPU 软件解码三档，GUI、CLI 与批处理入口共用同一实现。
默认自动；CLI 使用 `--decoder auto|nvdec|cpu`，可用 `--decode-device 0` 指定 CUDA 设备编号。
实际收到 CUDA 硬件表面的首帧后，日志才报告 NVDEC 已确认；主机路径在回读成功后确认。
自动模式在首帧初始化失败时重新从头软件解码；已经交付帧后遇到错误则停止，不混用两种后端的时序历史。

参考 `little-fancy-tool-ava` 的 NVDEC 混合处理方式，保留现有 NR/VSR 的 CPU 帧接口与 HDR 色彩转换。
NR/VSR 原有接口仍有硬件帧回读、CPU 色彩转换和上传；2026-10-18 新增 SDR RIFE 的 NVDEC → CUDA → NVENC 管线，适用条件见下文。
**RTX 5070 Ti / 617.14 已实测 4K/8K 的 SDR、PQ、HLG，以及 AV1 HDR；NVDEC 与软件解码的 RGBA 像素一致。**
已验证独立开关组合、取消重启、软件双遍，以及发布包的 NVDEC → NR → RTX Video 4K→8K → PQ 补帧。
实现边界、短片验证矩阵与解码阶段 CPU 耗时对比见 [NVDEC 接入记录](docs/NVDEC_DECODING.md)。

## NVIDIA 光流补帧

沿用 `little-fancy-tool-ava` 的 `fruc_vulkan` / `VK_NV_optical_flow` 后端，补帧默认关闭，
可以与 NR、RTX Video 任意开关组合。需要带有 `fruc_vulkan` 的 FFmpeg master，独立的 NvOFFRUC SDK 不参与此路径。光流滤镜在进程内通过 DLL 调用；解码、封装等兼容环节仍会使用 FFmpeg EXE。

```powershell
# 下载并校验固定版本；也可以用 -ArchivePath 指定已经下载的 ZIP。
.\tools\prepare-ffmpeg.ps1
# 独立补帧；增加 --nr 1、--sr 2 可组合 NR、超分。
dotnet run --project src/Dlss5Demo.Cli -c Release -- input.mp4 output.mp4 --nr 0 --interpolate 1 --interpolation-multiplier 2
```

匹配的 FFmpeg EXE 与 DLL 统一放在 `runtime` 根目录，已移除多余的 `runtime/fruc-ffmpeg`；开发运行自动发现；`DLSS5_FFMPEG` 仍优先。
打包脚本会检查补帧滤镜并携带匹配的 FFmpeg。

界面提供独立补帧开关与 2×/4×/6× 选择，启用后普通帧率设置暂停生效；关闭后恢复原设置。
补帧在 NR/SR 之后、最终输出空间滤镜和编码之前运行。Vulkan 优先线性图像以降低回读开销；不支持时探测原布局。启用补帧时避免先压缩再补帧。
进度、速度和实时对照预览统计源帧；完成状态同时显示补帧后的帧数，预览不播放生成帧。

RTX 5070 Ti / 617.14 已实测原生 3840×2160、7680×4320 的 SDR、PQ 和 HLG，
以及 NR + RTX Video 4K→8K + PQ 补帧。HDR 补帧使用完整 16 位颜色，输出 HEVC/AV1 10-bit；
保留 HDR 时不接受 H.264/GIF。短片功能验证、像素精度证据与限制见 [补帧验证记录](docs/NVIDIA_VULKAN_INTERPOLATION.md)。

## RIFE GPU 补帧与显存编码

界面可选 NVIDIA Vulkan、RIFE 4.25 Lite、RIFE 4.25。RIFE 需要 Windows 11 24H2+、NVIDIA GPU，以及 Windows ML 的 NVIDIA TensorRT RTX 提供程序；首次使用可能安装提供程序。仅支持 SDR 输入/输出，最大 3840×2160 像素。HDR 请选择 Vulkan，RIFE 不会静默丢弃 HDR 色彩信息。

```powershell
.\tools\prepare-rife-models.ps1
# 两份校验后的 ONNX 模型直接放在 runtime 根目录。
dotnet run --project src/Dlss5Demo.Cli -c Release -- input.mp4 output.mp4 --nr 0 --interpolate 1 --interpolation-backend rife --rife-model lite --interpolation-multiplier 4
# --rife-model full 选择完整版；--gpu-frames 0 对照主机帧路径。
```

默认开启显存帧管线：SDR、8 位 YUV420/NV12、NVDEC、NVENC，且没有 NR/SR/CPU 滤镜时，像素沿 NVDEC → CUDA RIFE → NVENC 传递，预览仅回读缩小画面。NR/RTX Video 组合保持原处理顺序和历史累积，在主机输出后只上传一次，再由 RIFE → NVENC 全程处理生成帧。CPU 滤镜、软件编码、软件双遍等条件沿用兼容路径。

单一任务门控制完整资源生命周期；保留一套同模型、同尺寸的 RIFE 会话，连续任务复用初始化，不共享视频时序历史。切换 GPU 直编与带输出滤镜的兼容路径也可复用网络。换模型、推理尺寸、关闭 RIFE、失败、取消或关闭应用时释放。嵌入 Core 的程序应在关闭时调用 `DlssVideoPipeline.ReleaseGpuResources()`。

RIFE 每对输入帧只准备一次图像张量，4×/6× 补帧仅更新各生成帧的时间参数。兼容路径复用上传后的帧对，省去重复上传与两个像素转换 ONNX 会话；静帧、切镜和尾帧复用不可变 NV12 数据。输出分辨率与处理结果相同时省略多余缩放，保留 GPU 直编。真实缩放仍在补帧后执行，保留 Lanczos 与既有处理顺序。[组合性能与验证记录](docs/PERFORMANCE_NR_VERSIONS_2026-10-25.md)。

源帧计数与输出帧计数不同，4× 输出 fps 应除以 4 才是源帧处理速度。实测、画质差异、原始证据位置与限制见 [2026-10-18 GPU 管线报告](docs/INTERPOLATION_GPU_2026-10-18.md)，模型来源和 SHA256 见 [RIFE 模型说明](docs/RIFE_MODELS.md)。

## 强制 HDR10 输出

SDR 源选择「强制 HDR10 / PQ」后，使用 NVIDIA 驱动 RTX Video HDR 执行真实映射：
`SDR 解码 → NR/超分 → 可选 Vulkan 补帧 → 驱动 TrueHDR → BT.2020/PQ RGB→YUV → 10-bit 编码`。
补帧生成帧及尾帧均执行映射；支持 HEVC、AV1、VP9 10-bit，拒绝 H.264/GIF。
HDR 源继续沿用现有 PQ/HLG 路径；RIFE 保持 SDR 输出，选择 RIFE 时跳过强制 HDR 并记录日志。

驱动接口无需 RTX Video SDK，但需要支持 TrueHDR 的 NVIDIA RTX GPU/驱动和本项目原生宿主：

```powershell
native\vsr_host\build-hdr.bat
dotnet run --project src/Dlss5Demo.Cli -c Release -- input.mp4 output-hdr.mp4 --nr 0 --hdr force --interpolate 1
# --hdr auto 跟随源；--hdr sdr 沿用原有 SDR 输出模式。应用 --autorun 入口也接受该参数。
```

宿主输出 `runtime\driver_hdr_host.dll`；`native\vsr_host\build.bat` 也会构建它，发布脚本随包携带。
初始化不支持或帧处理失败时明确报错。预览继续显示源帧阶段的 SDR 画面。
实现、色彩约定与验证见 [驱动 HDR 接入记录](docs/SMOOTH_MOTION_RTX_HDR.md)。

## RTX Video Super Resolution

DLSS NR 可以独立关闭：取消「启用 DLSS NR」后，跳过 NR、光流和历史累积，也不加载 NR 宿主或运行库。
此时仍可选择 RTX Video 2×/4×；两者均关闭时直接转码。NR 开关会保存到设置；旧设置默认保持开启。
CLI 和发布程序的批处理入口均支持 `--nr 0 --sr 2`（`--nr 1` 恢复 NR）。

驱动 D3D11 接口与 SDK 路径未通过等价性验证，因此当前保留 SDK 后端。
本机 617.14 驱动的 1800p/4K 输入会通过驱动接口静默退回普通缩放，SDK 可以正常处理；
这与输出是否超过屏幕尺寸不同。实验、像素对照结果和复现步骤见 [驱动接口调查](docs/RTX_VIDEO_DRIVER_INVESTIGATION.md)。

VSR 宿主是可选运行组件。要启用界面中的 2× 或 4×，需要安装 RTX Video SDK 1.1（或兼容版本），并把 SDK 根目录设置到 `NV_RTX_VIDEO_SDK`，然后在仓库根目录执行：

```powershell
native\vsr_host\build.bat
```

脚本会生成并放置 `runtime\vsr_host.dll` 和授权的 `runtime\nvngx_vsr.dll`。没有这两个文件时，关闭档仍可使用；选择 2×/4× 会在启动转码时给出明确错误。

界面默认处理顺序（勾选「NR 前置」）：

```text
源视频 → 光流/输入验证 → DLSS NR → 历史累积 →（可选）RTX Video 2×/4× → 编码
```

「NR 前置（速度优先）」默认勾选并保存到设置。取消勾选可使用超分 → NR 的顺序，
NR 会在放大后的分辨率处理，用于优先对照视觉质量；界面即时显示顺序。关闭超分时
顺序开关禁用，保留选择，下次启用超分时恢复。

## DLSS NR 视频时序稳定

视频转换默认保留 NR 会话的连续历史，并使用 NVIDIA 驱动中的 Optical Flow API
估计当前帧到上一帧的运动向量。这些向量同时送入 NR，并用于对齐输出修正量的历史。
向量基于实际 NR 输入生成。默认 NR 在源分辨率处理，随后超分；运动向量与 NR 输入保持同一坐标系。

抗闪烁处理累积的是「NR 输出 − NR 输入」的有符号修正量，再加回当前输入。
局部颜色不匹配、运动边界、越界、无效像素和切镜会拒绝旧历史；切镜还会重置 NR
及光流历史。历史衰减按源视频帧率计算，预览与编码使用同一处理结果。
HDR 使用浮点修正量，保留原有 RGBA16F 输出路径。

SDR 开启光流时，默认由 CUDA 执行灰度转换、运动矢量展开、置信度过滤和修正量累积，
光流估计由 NVOFA 专用引擎执行。NR 与 CUDA 共享 D3D12 显存缓冲，不回读运动场或历史平面。
当前兼容解码/SR/编码接口仍传递主机帧，GPU 阶段只回读 4 字节切镜标志和最终输出帧。
HDR、关闭光流、旧宿主或显存互操作不可用时沿用原路径；Core API 可用
`DlssNrOptions.UseGpuTemporalProcessing = false` 做同配置对照。详见 [实测与边界](docs/GPU_TEMPORAL_2026-10-25.md)。
CLI/autorun 对应 `--gpu-temporal 0`，默认启用。

光流接口不可用或不支持输入尺寸时自动使用静态区域累积。可在日志中确认实际路径。
此处理增加转码耗时；它能降低 NR 修正的闪烁，但错误光流和无真实深度的场景仍可能
存在残余伪影。处理思路参考 [Magpie 的时序稳定实现](https://github.com/SAOG0721/Magpie/blob/experimental/src/Magpie.Core/DLSSNRTemporal.cpp)。

界面中的「光流运动矢量」和「NR 连续历史」为独立复选框，默认勾选，状态随设置保存。
关闭光流时保留静态区域的历史累积；关闭连续历史时每帧重置 NR，并停止修正量累积，
光流仍可单独启用。旧版设置文件缺少这两个字段时按默认启用处理。

Core API 分别用 `DlssNrOptions.MotionGuidance` 控制光流、`ContinuousHistory` 控制 NR
自身历史，`TemporalStabilization` 控制额外的修正量累积。界面的连续历史开关同时控制后两者。
「精细光流匹配」可请求显卡支持的更细输出网格，默认关闭以保留标准档的开销；
标准档和精细档都过滤低可信度、越界及颜色不匹配的运动矢量。精细档由
`DlssNrOptions.FineMotion` / CLI `--fine-motion 1` 启用，不支持时自动回退。
CLI 用 `--motion-guidance 0`、`--continuous-history 0`、`--temporal 0` 分别关闭；
对照原有零运动引导路径需 `--temporal 0 --motion-guidance 0`，仍保留原有 NR 自身历史。

NR 的强度、局部色调、局部结构及皮肤结构统一限制到 0–5×，UI 以 0.1 为步长。
旧设置和 CLI 超出上限的数值也会限制到 5×，非有限数值回退默认值。20× 的局部参数
会使模型输出出现彩色异常，本项目按用户要求保留最高 5× 的调整空间。

输入和输出路径仅在当前会话使用，不写入设置，也不恢复旧设置中的路径。
更换输入视频会重新生成输出名；已有同名文件或目录时自动添加 `_1`、`_2` 等后缀。
Core 在启动编码前原子预留新文件名并保持所有权，编码器只写该新文件；并发任务
使用相同目标时也不会覆盖已有内容。详见 [强度与光流修复记录](docs/NR_STRENGTH_AND_MOTION.md)。

回归检查（不需要额外测试包）：

```powershell
dotnet run --project tests/Dlss5Demo.Tests -c Release
# RTX GPU 检查：光流方向/单位、HDR→SDR 会话切换
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu
# 固定输入的真实 NR 闪烁对照
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu-flicker "输入视频.mp4"
# 三份视频的前 300 帧：以驱动光流对齐可信区域后测量修正量变化
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --compare-video "原片.mp4" "处理前.mp4" "处理后.mp4"
```

HDR 源使用 FFmpeg zscale 解码为 RGBA16F，NR 和历史累积保留浮点像素，之后 VSR 使用 10-bit RGB 纹理并输出 RGBA16F，最后转换回 10-bit RGBA 输入编码器。

## 转码性能与分阶段管线

解码、光流/输入验证、NR、历史累积、SR、编码/预览分别处理不同的帧，用 3–5 个固定
缓冲槽衔接。每个阶段按帧顺序工作；NR 自身历史和额外的修正量历史各有一个状态所有者，
预览与编码仍使用同一处理结果。缓存有上限，但会比原先单帧循环多使用内存。

SR 使用独立工作进程，通过共享内存直接读写帧，控制管道只传槽编号与完成状态。
这样 SR 与 NR 不共用进程级 NGX 客户端，也不会因 SR 驱动清理阻塞而卡住主程序。
取消或阶段异常会停止关联进程、等待各阶段退出，然后释放缓冲。双遍编码的 GPU
资源在生成帧缓存后释放。光流使用独立 CUDA 流与锁页上传缓冲，避免全上下文同步。
HDR 界面预览仅转换显示需要的像素，并复用池化缓冲。

兼容路径的 NR 使用宿主已有的双帧提交队列，上传下一帧与上一帧 GPU 工作重叠；所有 NGX 调用
由同一个阶段线程按序执行。旧宿主没有队列接口时退回同步调用。Release 程序关闭初始
分层编译，使逐像素热路径从第一次调用起使用优化机器码。
GPU 光流/历史路径由一个阶段按序维护 NVOFA、NR 和修正量历史，跨 API 交接时只等待相关流/栅栏，
再交还共享缓冲。其他帧的解码、SR 和编码仍由有界管线重叠处理。

VSR 按帧同步处理，复用一组上传缓冲、纹理和命令分配器。此前的多帧队列在本机
对照中未提高整条管线吞吐，现已移除；UI、CLI 和 Core 均没有 VSR 异步队列选项。
默认的 NR → VSR → NVENC 路径可让超分输出保留在显存，通过共享纹理在 GPU 上转换
为 NV12 后交给编码器，省去超分整帧回读及编码前的重新上传。预览按需回读缩小
后的画面。压缩视频先写临时 NUT，再无损封装并合入音频，临时文件在结束/取消时清理。
这条路径需要可选的 `runtime/vsr_encoder.dll` 和匹配的 FFmpeg 共享库；构建方法见
[RTX Video SDK 配置](tools/RTX_VIDEO_SDK.md)。GPU 直编目前限 SDR；HDR、软件编码、CPU 滤镜、SR 前置等
不兼容配置会使用原有帧传输路径。

本机素材的测速结果和限制见 [性能验证记录](docs/PIPELINE_PERFORMANCE.md)。
可设置 `DLSS5_PROFILE=1`，在日志中查看各阶段的平均毫秒/帧；并行阶段的耗时不能相加
当作总帧耗时。CLI 支持 `--sr 2` / `--sr 4`、`--sr-quality 1..4` 和 `--preview 0`。
对照旧顺序可用 `--nr-before-sr 0`，对照同步 NR 可用 `--nr-queue 0`；Core 对应
`NrBeforeSuperResolution` 和 `UseAsyncNrQueue`，均默认启用。改变顺序会改变 NR 作用的
分辨率及画面结果。宿主独立测速可运行测试程序的 `--nr-benchmark "输入.mp4"`。
GPU 编码传输对照可用 `--sr-gpu-encode 0`，对应 Core 的
`UseGpuVsrEncoding`（默认开启）。

```powershell
$env:DLSS5_PROFILE = '1'
dotnet run --project src/Dlss5Demo.Cli -c Release -- "输入.mp4" "输出.mp4" --frames 180 --sr 2
# 使用至少 12 帧的小素材运行真实管线回归，含 SR、取消/重启与双遍编码
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --pipeline "小素材.mp4"
# VSR 真实 GPU 编码、颜色、预览、音频与取消重启（输入至少 12 帧）
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --vsr-transport "小素材.mp4"
# NR 独立开关：故意缺失 NR DLL，验证仅超分、直接转码、整段/单帧、HDR、预览、音频和取消重启
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --nr-switch "小素材.mp4"
```

将 Core 嵌入其他程序时，在入口先调用 `RtxVideoWorker.TryServe(args)`；返回 `true`
时结束正常启动流程。也可用 `TranscodeOptions.VsrWorkerExecutablePath` 指定具备该入口的
工作程序。桌面程序、CLI 和测试程序已接入，无需额外安装工作程序。

## DLSS NR 运行库选择

界面的「NR 版本」从实际安装目录递归发现 `nvngx_dlssnr*.dll`，显示 DLL 文件版本及目录标签，版本列表没有写死。明确选择的 DLL 用于下一次转码，并按相对路径保存；刷新、重启、移动整个发布目录不会因选项排序变化而选错版本。文件缺失时提示重新选择，不静默替换。

用户自行添加版本：把 DLL 放入 `runtime\任意版本名称\nvngx_dlssnr.dll`，在界面点击「刷新」。可以多层嵌套，也支持 `nvngx_dlssnr_自定义名称.dll`。用文件夹名标注适用显卡和来源；版本号相同的原版/社区版可区分。发布脚本自动携带 runtime 中发现的全部 NR DLL。

导入版本包（保留原默认 DLL）：

```powershell
.\tools\import-nr-runtimes.ps1 -ArchivePath 'H:\download\DLSSNR-DLL-Options.zip'
# CLI 与应用 --autorun 都接受绝对路径、相对路径或列表中的 runtime/... 路径。
dotnet run --project src/Dlss5Demo.Cli -c Release -- input.mp4 output.mp4 --nr-runtime 'runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll'
```

DLSS NR 运行库必须与 NVIDIA RTX 显卡代际匹配。「自动」保留原有策略：`DLSS5_NR_RUNTIME_DLL` → `mods` 覆盖 → `DLSS5_GPU_SERIES` → `runtime\nvngx_dlssnr.dll`。明确的界面/CLI 选择优先于自动策略；关闭 NR 时不解析或加载所选 DLL。

需要并存多个版本时，可使用以下布局，并设置 `DLSS5_GPU_SERIES` 为 `30`、`40` 或 `50`：

```text
runtime\30\nvngx_dlssnr.dll
runtime\40\nvngx_dlssnr.dll
runtime\50\nvngx_dlssnr.dll
```

也可以直接设置 `DLSS5_NR_RUNTIME_DLL` 指向匹配的 DLL。本次提供的 310.9.16 包含 RTX 50 原版及 RTX 20/30/40 社区适配版；本机只验证了 RTX 50 原版，社区版兼容性取决于具体显卡和驱动。AMD/Intel 的 OpenDLSS-NR 后端当前未接入；本项目的 AMD/Intel 支持仅限 FFmpeg AMF/QSV 编码，DLSS NR 与 RTX Video VSR 仍要求 NVIDIA。

## 发布打包

运行以下脚本会生成自带 .NET、FFmpeg、DLSS NR 和 RTX Video VSR 运行文件的 `win-x64` 目录和 ZIP。脚本读取本机 `runtime\`，这些 DLL 已被 Git 忽略：

```powershell
.\tools\package.ps1 -FfmpegDirectory G:\Tool\ffmpeg\bin
```

输出位于 `dist\`，每次使用时间戳目录。若已经为 `win-x64` 还原过依赖，可追加 `-NoRestore`。

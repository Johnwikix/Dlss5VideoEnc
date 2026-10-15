# Dlss5VideoEnc

Avalonia 视频转码工作台：DLSS NR → 可选 RTX Video Super Resolution（关闭 / 2× / 4×）→ FFmpeg 编码。HDR 视频通过 RGBA16F / 10-bit 路径处理并保留 HDR 元数据。

## RTX Video Super Resolution

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
缓冲槽衔接（启用 VSR 异步队列时，在内存预算内最多 6 槽）。每个阶段按帧顺序工作；NR 自身历史和额外的修正量历史各有一个状态所有者，
预览与编码仍使用同一处理结果。缓存有上限，但会比原先单帧循环多使用内存。

SR 使用独立工作进程，通过共享内存直接读写帧，控制管道只传槽编号与完成状态。
这样 SR 与 NR 不共用进程级 NGX 客户端，也不会因 SR 驱动清理阻塞而卡住主程序。
取消或阶段异常会停止关联进程、等待各阶段退出，然后释放缓冲。双遍编码的 GPU
资源在生成帧缓存后释放。光流使用独立 CUDA 流与锁页上传缓冲，避免全上下文同步。
HDR 界面预览仅转换显示需要的像素，并复用池化缓冲。

NR 使用宿主已有的双帧提交队列，上传下一帧与上一帧 GPU 工作重叠；所有 NGX 调用
由同一个阶段线程按序执行。旧宿主没有队列接口时退回同步调用。Release 程序关闭初始
分层编译，使逐像素热路径从第一次调用起使用优化机器码。

VSR 支持可选的单线程 FIFO 异步提交/回收，两帧在途，原生宿主支持三帧。每槽拥有独立的
上传缓冲、纹理和命令分配器，只有对应 fence 完成才读取或复用；EOF 会排空尾队列。
本机对照中异步队列未提高整条管线吞吐，因此默认关闭，可用 `--sr-queue 1` 启用。
默认的 NR → VSR → NVENC 路径可让超分输出保留在显存，通过共享纹理在 GPU 上转换
为 NV12 后交给编码器，省去超分整帧回读及编码前的重新上传。预览按需回读缩小
后的画面。压缩视频先写临时 NUT，再无损封装并合入音频，临时文件在结束/取消时清理。
这条路径需要可选的 `runtime/vsr_encoder.dll` 和匹配的 FFmpeg 共享库；构建方法见
[RTX Video SDK 配置](tools/RTX_VIDEO_SDK.md)。GPU 直编目前限 SDR；HDR、软件编码、CPU 滤镜、SR 前置等
不兼容配置会使用原有帧传输路径，VSR 异步队列仍然有效。

本机素材的测速结果和限制见 [性能验证记录](docs/PIPELINE_PERFORMANCE.md)。
可设置 `DLSS5_PROFILE=1`，在日志中查看各阶段的平均毫秒/帧；并行阶段的耗时不能相加
当作总帧耗时。CLI 支持 `--sr 2` / `--sr 4`、`--sr-quality 1..4` 和 `--preview 0`。
对照旧顺序可用 `--nr-before-sr 0`，对照同步 NR 可用 `--nr-queue 0`；Core 对应
`NrBeforeSuperResolution` 和 `UseAsyncNrQueue`，均默认启用。改变顺序会改变 NR 作用的
分辨率及画面结果。宿主独立测速可运行测试程序的 `--nr-benchmark "输入.mp4"`。
VSR 对照可用 `--sr-queue 1` 和 `--sr-gpu-encode 0`，分别对应 Core 的
`UseAsyncVsrQueue`（默认关闭）和 `UseGpuVsrEncoding`（默认开启）。

```powershell
$env:DLSS5_PROFILE = '1'
dotnet run --project src/Dlss5Demo.Cli -c Release -- "输入.mp4" "输出.mp4" --frames 180 --sr 2
# 使用至少 12 帧的小素材运行真实管线回归，含 SR、取消/重启与双遍编码
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --pipeline "小素材.mp4"
# VSR SDR/HDR FIFO 输出一致性；真实 GPU 编码、预览、音频与取消重启（输入至少 12 帧）
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --vsr-gpu --vsr-transport "小素材.mp4"
```

将 Core 嵌入其他程序时，在入口先调用 `RtxVideoWorker.TryServe(args)`；返回 `true`
时结束正常启动流程。也可用 `TranscodeOptions.VsrWorkerExecutablePath` 指定具备该入口的
工作程序。桌面程序、CLI 和测试程序已接入，无需额外安装工作程序。

## DLSS NR 运行库选择

DLSS NR 运行库必须与 NVIDIA RTX 显卡代际匹配。项目默认读取 `runtime\nvngx_dlssnr.dll`；也支持把对应 DLL 放到应用目录的 `mods\nvngx_dlssnr.dll`，这样无需覆盖默认文件。

需要并存多个版本时，可使用以下布局，并设置 `DLSS5_GPU_SERIES` 为 `30`、`40` 或 `50`：

```text
runtime\30\nvngx_dlssnr.dll
runtime\40\nvngx_dlssnr.dll
runtime\50\nvngx_dlssnr.dll
```

也可以直接设置 `DLSS5_NR_RUNTIME_DLL` 指向匹配的 DLL。RTX 30 系运行库属于社区适配，兼容性取决于具体显卡和驱动。AMD/Intel 的 OpenDLSS-NR 后端当前未接入；本项目的 AMD/Intel 支持仅限 FFmpeg AMF/QSV 编码，DLSS NR 与 RTX Video VSR 仍要求 NVIDIA。

## 发布打包

运行以下脚本会生成自带 .NET、FFmpeg、DLSS NR 和 RTX Video VSR 运行文件的 `win-x64` 目录和 ZIP。脚本读取本机 `runtime\`，这些 DLL 已被 Git 忽略：

```powershell
.\tools\package.ps1 -FfmpegDirectory G:\Tool\ffmpeg\bin
```

输出位于 `dist\`，每次使用时间戳目录。若已经为 `win-x64` 还原过依赖，可追加 `-NoRestore`。

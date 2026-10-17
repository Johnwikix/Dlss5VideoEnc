> 后续更新：2026-10-18 已移除独立 fruc-ffmpeg 目录、接入进程内光流与 RIFE 显存管线。当前依赖位置、处理顺序与性能边界见 [新验证报告](INTERPOLATION_GPU_2026-10-18.md)。下文保留当时的实现与命令。

# NVDEC 解码接入

日期：2026-10-17。

## 当前状态

GUI、CLI 与测试项目均实际构建，0 警告、0 错误。19 项既有 CPU 回归与 6 项独立子进程控制回归通过。
按用户要求等待参考会话完成 GPU 性能测试后，才在 RTX 5070 Ti 16 GB、驱动 617.14 上顺序运行本次设备验证。
**4K/8K SDR、PQ、HLG、AV1 HDR、处理组合及发布包验证均通过**；所有输入的 NVDEC/软件 RGBA 像素对比均为逐字节一致。
这些是短片功能和精度验证，不代表长视频、所有 profile 或所有 NVIDIA 显卡均通过。

## 对照参考项目

只读参考 `little-fancy-tool-ava/FancyTool/Services/VideoTranscodeService.Cuda.cs`、
`VideoTranscodeService.cs` 及 ADR 0011，不写入该项目、不改动运行库、不操作它的进程。

参考项目的完整 CUDA 路径会让 NVDEC、CUDA 色彩转换、模型推理与 NVENC 共用 GPU 帧引用。
本项目 NR/VSR 的现有接口接收主机 RGBA 指针，且 VSR worker 跨进程使用主机帧，因此本次先接入其**硬解码后回读**的混合方式：

```text
NVDEC → CUDA 解码表面 → hwdownload → 恢复原像素布局 → 原有 RGBA8/RGBA16 色彩转换
      → NR / RTX Video / Vulkan 补帧 → 编码
```

这会把视频解码交给 NVDEC，但不会移除 CPU 色彩转换、帧传输或现有跨进程交接。
下文给出单独解码阶段的实测 CPU 耗时；完整 GPU 帧衔接需要另外改变原生宿主的资源与同步接口。

## 配置与行为

- `TranscodeOptions.DecodeMode` 默认 `Auto`。GUI 提供自动、强制 NVDEC、软件三档并保存设置。
- CLI、`--autorun` 共用 `--decoder auto|nvdec|cpu`。`--decode-device <CUDA编号>` 默认 0。
- 使用 FFmpeg 的 `-hwaccel cuda -hwaccel_output_format cuda`。硬件表面必须通过 `hwdownload`，阻止软件输出被误报为 NVDEC。
- 首个完整 RGBA 帧成功回读后记录“NVDEC 已确认”。仅提出硬解码请求不算成功。
- 自动模式在尚未交付首帧时允许一次软件重试。先等待并释放旧进程，再从输入开头重启；部分首帧会被完整覆盖。
- 强制模式报告失败；运行中已经交付帧后发生错误也会停止，避免重新解码破坏 NR 历史、补帧时钟和音频。
- 取消回调覆盖阻塞读取及重试切换的边界，不能在已取消后留下新的解码进程。
- 有界帧槽和 NR/SR/补帧的独立开关保持原有行为；单遍、VSR GPU 编码与软件双遍共用解码器封装。

接入的表面映射包括常见 8 位 420 → NV12、10 位 420 → P010、12 位 420 → P016，以及 444 表面。
候选编码包括 H.264、HEVC、AV1、VP8/9、MPEG-1/2、VC-1/WMV3、MJPEG。
这些是请求路径，不是本次设备支持认证；具体 GPU、驱动、编码 profile 不支持时按上述自动/强制规则处理。
未接入的编码或像素格式在自动模式直接使用软件，在强制模式明确拒绝。

HDR 仍使用原有 zscale 范围/矩阵处理与 RGBA64LE 输出，不插入 8 位中转或 SDR 色调映射。

### 解码精度修正

首轮 H.264 测试发现，直接 `hwdownload,format=nv12,format=rgba` 与原软件路径仅有 32.671 dB 的 RGB PSNR。
进一步对照发现：CPU/NVDEC 的 NV12 YUV 数据哈希完全相同；差异来自 swscale 对 NV12 与 YUV420P 使用不同的色度转换路径。
修正为回读后先恢复源像素布局，例如 `hwdownload,format=nv12,format=yuv420p,format=rgba`，再进行原有转换。
修正后 SDR 的 RGB 哈希一致，所有本次 HDR/SDR 测试也逐字节一致。没有通过放宽像素断言掩盖差异。

640×360、3 帧的诊断哈希（SHA-256）：

| 数据 | CPU | NVDEC |
| --- | --- | --- |
| NV12 | `85510c01e44f8f34e1f36a1dc6d349743a45f71c973bc3cbe40325aed04dcb05` | 相同 |
| 原路径 / 修正后的 RGBA | `e09bc86015e7fe88aa1545bb54ee55177eed34cbdaf06604db4aa3613c3b01e7` | 相同 |

## 已运行的无 GPU 回归

独立模拟解码子进程通过真实 stdin/stdout/stderr 与退出码覆盖：

1. NVDEC 首帧前失败，软件重新从第一帧输出，帧数无重复或丢失。
2. NVDEC 只输出部分首帧后失败，软件帧不混入残留像素。
3. 强制 NVDEC 不回退，保留错误原因。
4. 已输出一帧后失败，不重启或混用后端。
5. 取消终止阻塞的帧读取并回收子进程。
6. 在硬件失败与软件重启之间取消，不再创建替代进程。

命令与本地证据：

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
dotnet build tests/Dlss5Demo.Tests -c Release -o build/nvdec/tests
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --decoder-control
# 记录：build/nvdec/control-tests.log
```

## 实机验证

每个输入先严格请求 NVDEC，与软件解码比较 3 帧完整 RGBA，再实际编码 3 帧及补帧编码 6 帧。
每个输入还用不存在的 CUDA 设备 9999 验证真实初始化失败时的软件回退。

| 输入 | 格式 | 像素一致 | 实际编码 / 补帧 |
| --- | --- | --- | --- |
| 640×360 SDR | H.264 8-bit | 是 | 3 / 6 帧 |
| 320×180 PQ / HLG | HEVC 10-bit，各一份 | 是 | 各 3 / 6 帧 |
| 320×180 PQ / HLG | AV1 10-bit，各一份 | 是 | 各 3 / 6 帧 |
| 3840×2160 SDR / PQ / HLG | HEVC 8/10-bit，各一份 | 是 | 各 3 / 6 帧 |
| 7680×4320 SDR / PQ / HLG | HEVC 8/10-bit，各一份 | 是 | 各 3 / 6 帧 |

HDR 输出保持 PQ/HLG、10-bit；音频、尺寸和帧数检查通过。小尺寸输入另测 NR＋SR 2×＋补帧及 SR 单独编码。
另运行默认自动解码下的完整补帧回归：NR/SR/补帧的 8 种开关组合、SR→NR 顺序、2×/4×/6×、
单帧、未知帧数 Matroska、整段尾帧、取消后重启、普通 FPS 设置覆盖与软件双遍编码均通过。

真实 FFV1 输入在自动模式直接软件解码，3 帧完整保留；强制 NVDEC 则在预留输出文件前明确拒绝。
GUI 经 lvt UI Automation 验证自动模式默认值、强制 NVDEC 选择与退出重启后的设置恢复、CPU 模式选择。

原始摘要保存在 [设备矩阵](data/nvdec-20261017/hardware-matrix.log)、
[进程控制回归](data/nvdec-20261017/control-tests.log)、
[组合与取消回归](data/nvdec-20261017/interpolation-auto-nvdec.log)。

## 解码阶段测量

用户素材 `UMP45&RO_1.mp4`，HEVC 1920×1080 8-bit。两种模式各预热 30 帧，
随后各测 3 次 600 帧并交替顺序，取中位数。计时包含解码器启动、解码、色彩转换、管道写入/读取；
不含 NR、VSR、补帧和编码。CPU 时间是 FFmpeg 解码子进程所有线程累计的处理器时间，不是墙钟时间或整个应用 CPU 占用率。

| 模式 | 墙钟时间 | 吞吐 | 解码子进程 CPU 时间 |
| --- | ---: | ---: | ---: |
| CPU | 2.050 s | 292.6 fps | 6.141 CPU·s |
| NVDEC | 2.070 s | 289.9 fps | 4.188 CPU·s |

该素材中 CPU 时间减少约 32%，吞吐接近，没有测得加速。回读和 CPU 色彩转换仍限制收益，
不能把解码阶段数字当作完整转码吞吐；短样本也不用于推算 4K/8K 或整段速度。
[全部 6 次测量](data/nvdec-20261017/decode-benchmark.log)。

## 发布包

实际生成并解压验证：`dist/Dlss5VideoEnc-win-x64-20261017-164503-891.zip`，284,275,373 字节。
SHA-256：`50d40533ff013f9e994ba8af49f98f6d7f2c85b62c9487db46600a0536d35e7d`。
解压后 248 个清单文件的哈希全部匹配。未设置 `DLSS5_FFMPEG`，直接使用包内 FFmpeg 和运行库：

- 强制 NVDEC → NR → RTX Video 2× → Vulkan 补帧 2×：4K PQ 输入 3 帧，得到 8K PQ 10-bit、6 帧、48000/1001 fps，BT.2020 与 AAC 音频保留。
- CPU 解码＋补帧＋H.264 软件双遍：6 帧 → 12 帧，音频保留。
- 自动模式＋无效 CUDA 设备：实际初始化失败后软件回退，输出 3 帧。
- 强制模式＋无效 CUDA 设备：退出码 1，保留 NVDEC 原始错误。
- FFV1 自动软件处理及强制 NVDEC 提前拒绝均通过。

打包后的真实日志与输出探测结果见 [发布包证据](data/nvdec-20261017/package-verification.log)。
NVDEC 使用 NVIDIA 驱动提供的硬解码能力，无需额外分发 CUDA Toolkit。
RTX Video VSR SDK 的保留原因仍见 [驱动行为调查](RTX_VIDEO_DRIVER_INVESTIGATION.md)。

## 复现入口

已提供 `--nvdec <输入文件>` 集成入口，严格请求 NVDEC：

```powershell
$env:DLSS5_FFMPEG="$PWD\runtime\fruc-ffmpeg\bin\ffmpeg.exe"
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --nvdec build/nr-switch/source-sdr.mp4
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --nvdec build/fruc/source-pq-valid.mp4
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --nvdec build/fruc/source-hlg-valid.mp4
# 另对原生 4K/8K SDR、PQ、HLG，以及 AV1 输入执行相同入口。
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --interpolation build/nr-switch/source-sdr.mp4
# 相同 FrameDecoder 的单独解码测量，各测 3 次 600 帧；需选至少 600 帧素材。
dotnet build/nvdec/tests/Dlss5Demo.Tests.dll --nvdec-benchmark '输入.mp4'
```

该入口对比 NVDEC 与软件解码的逐帧 RGBA 像素与帧数，检查真实初始化失败回退，
并通过项目实际管线验证音频、HDR、补帧及小尺寸 NR/SR/补帧组合。
GUI、取消重启和发布包的设备验证已完成；本次未声称完整 GPU 驻留或整条管线加速。

接口依据：[NVIDIA FFmpeg 硬件加速指南](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/ffmpeg-with-nvidia-gpu/index.html)。

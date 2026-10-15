# 视频管线性能验证

## 工况与结果

2026-10-13，本机 RTX 5070 Ti、驱动 617.14、20 个逻辑处理器、32 GB 内存。
输入为用户提供的 `UMP45&RO_1.mp4`，1920×1080、60 fps、8240 帧。
测量期间游戏已退出，GPU 测试顺序执行。编码为 HEVC NVENC、CQ 19，SR 质量 4，
光流、NR 连续历史和修正量累积全部开启。

| 工况 | 帧数 | 原串行管线 | 最终管线 | 提升 |
| --- | ---: | ---: | ---: | ---: |
| 1080p NR | 180 | 27.4 fps | 66.6 fps | 2.43× |
| 1080p → 4K，SR 2× | 60 | 7.5 fps | 40.4 fps | 5.39× |
| 1080p → 8K，SR 4× | 24 | 1.8 fps | 11.6 fps | 6.44× |

这些数值是处理循环的吞吐，包含管线填充/排空、解码读、编码写和 CLI 预览回调，
不包含 NGX 会话初始化、最终编码器排空和资源清理。CLI 的预览回调只检查帧对，
没有桌面界面的像素缩放和绘制成本。短片的初始成本较明显，不能据此推算整片耗时。
旧 SR 宿主完成输出后会阻塞在 NGX Shutdown；旧版测速脚本在最终进度和 NR 释放后
仅终止它自己启动的基线进程，输出文件已经关闭。

最终日志：`build/performance/final-nr.log`、`final-sr2.log`、`final-sr4.log`。
原版对照：同目录 `matched-*-before.log`。这些机器相关产物在 build 下，不随源码提交。

完整 8240 帧 2× 转换：处理循环 151.3 秒、54.4 fps，输出 3840×2160、90.5 MiB。
视频时长 137.333 秒、音频 137.312 秒，输出帧数 8240。转换中一次 GPU 采样为 81%
占用、约 4.96 GiB 显存；这是瞬时采样，不是平均利用率。
结果文件：`build/performance/UMP45_RO_1_NR_RTX2x.mp4`，日志 `full-sr2.log`。

## NR 宿主耗时与全管线吞吐

另用预解码的同一 1080p 帧、10 帧预热、180 次处理测量 NR 宿主：

| NR 调用方式 | 无运动上传 | 上传 float2 运动向量 |
| --- | ---: | ---: |
| 每帧同步提交/回读 | 6.26 ms/帧 | 9.00 ms/帧 |
| 两帧 FIFO 提交队列 | 5.35 ms/帧 | 5.63 ms/帧 |

这是 CPU 上传、模型执行、同步和回读合计的摊销时间，不是 GPU 时间戳测得的模型
执行延迟。队列改善吞吐，并不表示单帧延迟也降到相同数值。运动上传测量使用零向量，
不含驱动光流计算和额外时序滤波。完整转码帧率还受到这些 CPU 阶段限制。

最终 1080p 管线每帧平均：光流 6.43 ms、输入验证 7.16 ms、NR 调用 7.95 ms、
输出历史 11.59 ms。阶段处理不同的帧，耗时重叠，不能直接相加。
8K 工况的编码写阶段约 75 ms/帧，包含等待编码器消费像素；其 SR 阶段约 25 ms，
NR 仍在源 1080p 处理。8K 的帧传输、色彩转换和编码仍限制吞吐。

NVIDIA [DLSS 5 官方技术报告](https://research.nvidia.com/labs/adlr/files/DLSS5_Report.pdf)
第 17 页 4.3.3 节报告 RTX 5090 处理 3840×2160 的模型执行约 8 ms、峰值 GPU 内存
731 MB。本次读取的报告未列出 RTX 5060 的 9.9 ms 测试条件，因此没有把用户提到
的数字当作整条离线转码管线的直接验收指标。

## 架构与行为

默认顺序：解码 → 光流/输入验证 → NR → 修正量历史 → RTX Video SR → 编码/预览。
NR、运动向量和历史在源分辨率处理，减少 2×/4× 时的像素工作量。
界面「NR 前置（速度优先）」可以切换两种顺序并保存；不启用超分时禁用该控件。
原顺序仍可通过 Core 的 `NrBeforeSuperResolution=false` 或 CLI 的
`--nr-before-sr 0` 对照。改变顺序会改变 NR 作用的分辨率，画面不保证与原顺序相同。

- 各阶段使用专用线程、FIFO 有界队列和 3–5 个固定帧槽。只有编码/预览结束后才归还
  槽，原片、运动、输入误差、NR 输出和最终超分输出都属于同一帧。
- 输入验证与输出修正量历史分别拥有状态，前者可先处理后续帧。NR 内部历史仍按序
  处理，切镜和历史开关继续决定 reset。
- NR 的 enqueue/dequeue 都由同一个线程调用，最大两帧在途，EOF 排空队列；旧宿主
  无相应接口时使用同步 Process。NGX 不是线程安全接口，不能把两类调用分配到并行线程。
- SR 使用独立进程避免共用 NGX 单例，帧通过共享内存传递，控制管道只发送槽编号。
  子进程原生清理 500 ms 内未返回时回收该进程；取消、错误和父进程退出也会回收。
- NVIDIA 光流使用独立非阻塞 CUDA 输入/输出流、锁页灰度上传；只同步相关流，避免
  全上下文同步干扰 NR。保持原有 SLOW 质量、grid=4 和双线性展开。
- Release App/CLI/测试程序关闭分层编译，避免大批逐像素循环先在未优化代码中运行。
  嵌入 Core 的其他应用需要自行选择运行时编译配置。
- HDR 保持 RGBA16F NR/SR 路径，最后转回整数帧编码；界面预览只转换显示所需像素。
  编码器输入设置 AVFrame 色彩标签，避免 NVENC 丢失 PQ/BT2020 标识。
- 双遍编码在生成 raw 帧缓存后释放 GPU 会话。帧数限制同时按源帧率限制音频时长，
  避免音频读取先于视频管道导致尾部延长。

帧槽按约 512 MiB 目标估算，最少 3 槽；原生 GPU 分配、时序状态和运行库工作集另计。
超大帧的三槽下限可能超过此目标。相比原先单帧循环，这种并行方式增加内存占用。

## 回归与复现

Release 构建无警告/错误。16 项 CPU 回归、真实光流方向/单位、HDR→SDR 格式切换、
NR 历史开关、SDR/HDR 原生同步/异步队列的逐字节一致性均通过。
队列测试包含开启/关闭历史以及第 7 帧切镜 reset。

小视频真实管线检查包含四种光流/历史组合、2×/4× 两种顺序、最后预览帧、HDR 元数据、
单帧尾队列排空、取消后重新启动、编码器失败清理及 SDR 的 SR + 软件双遍编码。
桌面 UIA 验证了两个默认勾选项、NR→SR 与 SR→NR 转码、顺序即时提示、重启后设置恢复，
以及关闭超分时禁用顺序控件。原生日志分别确认 NR 在源尺寸和超分尺寸运行，最后一帧
预览保留；顺序开关与提示的完整显示也通过截图检查。

原版与最终版 1080p 输出的 180 帧解码 framemd5 全部一致；固定旧处理顺序时，较早的
管线解耦版 2×/4× 输出也与原版逐帧一致。这个结论不适用于改变 NR/SR 顺序后的画面。

```powershell
dotnet build src/Dlss5Demo.Cli -c Release
./tools/benchmark-video.ps1 -CliAssembly src/Dlss5Demo.Cli/bin/Release/net10.0/Dlss5Demo.Cli.dll `
  -InputVideo '输入.mp4' -OutputVideo '测试.mp4' -Frames 180 -Scale 1
# 使用相同输入、帧数、编码和质量，仅对照顺序或 NR 提交方式：
# -NrFirst $false / -NrQueue $false
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --pipeline '至少12帧的小素材.mp4'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --nr-benchmark '1080p输入.mp4'
```

`DLSS5_PROFILE=1` 开启平均阶段计时。可临时设置 `DLSS5_CPU_WORKERS` 和
`DLSS5_HISTORY_WORKERS` 诊断不同 CPU 的并发预算；降低并发在本机没有改善吞吐，
默认仍为准备阶段最多 8 线程、历史阶段最多 16 线程，并根据逻辑处理器数量调整。

## 2026-10-15：VSR 队列与 GPU 编码传输

RTX 5070 Ti、驱动 617.14，同一 `UMP45&RO_1.mp4` 的前 600 帧，1080p → 4K、
SR 质量 4、HEVC NVENC p5/CQ 19，光流、NR 历史和修正量累积开启，关闭预览。
所有 GPU 测试顺序运行。统计不含会话初始化与清理；兼容路径沿用处理循环口径，
GPU 直编的最终耗时另计编码排空和无损封装（本次约 88–95 ms）。

| VSR 提交 | 编码输入传输 | 最终吞吐 | 耗时 |
| --- | --- | ---: | ---: |
| 单帧同步 | CPU 原始帧 | 48.8 fps | 12.3 s |
| 两帧 FIFO | CPU 原始帧 | 48.9 fps | 12.3 s |
| 单帧同步 | GPU 共享纹理 → NVENC | 54.5–56.7 fps | 10.6–11.0 s |
| 两帧 FIFO | GPU 共享纹理 → NVENC | 53.2–54.3 fps | 11.1–11.3 s |

GPU 直编的两种提交方式各测两次。当前工况下，异步队列没有带来整条管线的吞吐收益；
默认关闭 `UseAsyncVsrQueue`，保留 `--sr-queue 1` 供其他工况对照。
`UseGpuVsrEncoding` 默认开启，主要收益来自消除超分整帧回读和编码前的重新上传。
NR、光流、历史和解码等阶段仍按原有分阶段管线重叠执行。

VSR 的 CPU 提交/回收平均耗时，原始帧路径从同步 13.62 ms 降到异步 6.71 ms；
GPU 直编加异步队列为 1.59 ms。此数值包含等待和传输，不能解释成模型的 GPU 执行时间。
CPU 其他阶段仍限制最终吞吐。异步队列启用时，在原 576 MiB 估算预算内最多使用
6 个帧槽，避免额外在途帧挤占 CPU/NR 所需的缓冲；默认同步 VSR 仍最多 5 槽。

### 数据与回归

本机产物在忽略的 `build/performance/vsr-async/` 下：`final-sync.log`、
`final-queue.log`、`final-gpu.log`、`final-gpu-sync.log`、`repeat-gpu-queue.log`、
`repeat-gpu-sync.log`，同名 MP4 可直接对照。

- 600 帧同步/异步 VSR 的解码 framemd5 全部一致；原生 SDR/HDR、深度 1/2/3、
  11 张不同输入及环形槽复用/尾队列排空也逐字节一致。
- SDR GPU 编码与原始帧编码输出的 600 帧平均 PSNR 为 46.65 dB；GPU 与 swscale
  的色度滤波和舍入不同，编码后像素不保证完全一致。编码仍为 HEVC Main、
  3840×2160、60 fps、600 帧、视频 10.000 s、音频 9.984 s，保留 B 帧时序。
- HDR 的驱动 RGB10 → P010 VideoProcessor 转换出现明显颜色异常；原始 RGB10
  纹理检查正常，异常发生在 GPU 色彩转换阶段。因此 Core 和原生宿主均禁用 HDR
  GPU 直编，保持原传输/颜色转换，可选 FIFO 继续可用。HDR 回退输出的 PSNR 为 ∞，
  PQ/BT.2020/10-bit 元数据保持。没有用元数据检查替代画面比较。
- 19 项 CPU 回归、SDR H.264/HEVC/AV1 GPU 编码、29.97 fps、低频/最终预览、单帧
  排空、音频合入、取消/重启、CPU 滤镜/软件编码及双遍编码、缺少可选编码 DLL、
  旧宿主没有 FIFO 导出时的同步回退均通过。2×/4× 功能检查只用了小尺寸素材，
  本次未进行 8K 性能优化或测速。
- App、CLI 和原生 DLL 已以 Release 构建。GPU 编码需要可选的 `vsr_encoder.dll`
  及匹配的 FFmpeg 共享库；构建方式见 `tools/RTX_VIDEO_SDK.md`。运行产生的压缩
  NUT 缓存在结束/取消后清理，FFmpeg 最后只封装和合入音频，不再次编码视频。

```powershell
# 相同条件对照，只改变 VSR 排队或 GPU 编码传输
./tools/benchmark-video.ps1 -CliAssembly src/Dlss5Demo.Cli/bin/Release/net10.0/Dlss5Demo.Cli.dll `
  -InputVideo '输入.mp4' -OutputVideo 'gpu-sync.mp4' -Frames 600 -Scale 2 -Preview $false
# 追加 -VsrQueue $true 测异步；追加 -GpuVsrEncoding $false 测原始帧传输

dotnet run --project tests/Dlss5Demo.Tests -c Release -- --vsr-gpu --vsr-transport '至少12帧的小素材.mp4'
```

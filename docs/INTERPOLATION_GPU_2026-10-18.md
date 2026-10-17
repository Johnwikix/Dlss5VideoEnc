# Dlss5VideoEnc GPU 补帧与转换管线验证

日期：2026-10-18。替代先前 NVDEC/补帧文档中关于依赖目录、进程外光流及 RIFE 的说明；历史测试保留原文，当前行为以本文为准。

## 改动与适用范围

- 已移除 `runtime/fruc-ffmpeg`。固定版本 FFmpeg 的 EXE、七个匹配 DLL 统一位于 `runtime` 根目录。光流直接调用进程内 `fruc_vulkan`，不通过 EXE 执行滤镜；兼容解码、输出封装与音频合流仍调用 FFmpeg EXE。`tools/prepare-ffmpeg.ps1` 校验固定归档 SHA256，打包不重复复制 NR/VSR DLL。
- NVIDIA Vulkan 在滤镜初始化前绑定硬件设备，真实试运行后选择线性图像；线性不可用时重试原布局，均失败时明确报错，不替换成 CPU/混帧。SDR 使用 RGBA8，HDR 使用 RGBA64LE，保留 16 位颜色；输出 HEVC/AV1 10 位。
- RIFE 4.25 Lite / Full 从 FancyTool 移植，两个校验后的模型直接位于 `runtime/rife_v4.25_lite_v2.onnx`、`runtime/rife_v4.25_v2.onnx`。来源、固定版本、SHA256 和 MIT 许可见 [模型说明](RIFE_MODELS.md)。使用 Windows ML 2.4.89 的 NVIDIA TensorRT RTX EP，不带第二份 ONNX Runtime，禁用 CPU 神经网络回退及并发 CUDA Graph 捕获。
- 纯 RIFE：NVDEC → CUDA NV12/BGRA 转换 → GPU 场景判断/两帧准备 → RIFE IO binding → GPU NV12 → NVENC。五个固定槽位与有界队列提供背压；NVENC 持有的帧引用在完成前不会被覆盖，预览只回读缩小画面。
- NR/RTX Video + RIFE：保留现有解码、NR、运动引导、修正量历史累积、超分及其顺序，主机处理结果只上传一次，之后 RIFE 生成帧直接交给 NVENC。此组合仍有 NR/VSR 主机帧接口，不能称为从解码开始全程显存。
- 软件解码/编码、CPU 空间滤镜、双遍等组合保留 RGBA 兼容路径。所有补帧在 NR/SR 之后、最终输出空间滤镜之前运行；生成帧不重复运行 NR/SR。源帧率为有理数，2×/4×/6× 精确输出 N×倍率帧，尾帧复制，切镜/重复帧保持，源帧和输出帧计数分开。
- RIFE 当前只接受 SDR 输入/输出，最高 3840×2160 像素；HDR 即使取消“保留 HDR”也明确拒绝，因为此 Demo 尚未移植 FancyTool 的正确 GPU HDR 色调映射。HDR 请用 Vulkan。纯显存解码限 8 位 YUV420/NV12 和已支持的 601/709 矩阵；其他可处理的 SDR 格式走兼容路径。
- 只保留一套同模型、同尺寸、同像素策略的 RIFE 会话，避免连续短任务重复初始化；视频对、PTS 和 NR 历史不跨片段复用。完整任务由 `PipelineGate` 串行拥有，换配置、错误、取消及应用关闭释放。嵌入方关闭前调用 `DlssVideoPipeline.ReleaseGpuResources()`；取消需等待所有阶段及原生调用真实退出。

## 环境与测量边界

Windows 11 build 26300、i5-13600KF（14 核/20 线程）、RTX 5070 Ti 16 GB、驱动 617.14；FFmpeg N-127579-g5cb9fcd211-20261016 / AutoGen 9.0.1.1；Release 构建使用 .NET SDK 10.0.402。用户暂停 MAA/MuMu 后串行测试，不并发跑多组 GPU 转码。

统一自然视频输入：1920×1080、24 fps、72 帧、8 位 YUV420、BT.709，无音频，SHA256 `0c4035d1c88e8a36b3a381811e7533ff5764ddf884416f86d9b0d86441f28ecd`。原文件在 FancyTool 根目录 `.qa/optical-flow-2026-10-17/real1080-72.mp4`。补帧倍率 4，NVENC HEVC / p5 / quality 19，关闭预览。纯补帧处理 72→288 帧；NR/SR 组合处理前 24→96 帧，超分输出 3840×2160。

各组运行三次，首次单列为初始化/预热样本，表中取后两次中位数。`处理输出 fps` 的边界为 `TranscodeResult.Elapsed`，包含处理、编码排空与最终封装，排除前置探测/模型与宿主初始化；`总耗时输出 fps` 使用调用 `RunAsync` 前后的实际墙钟，包含初始化。后两次 RIFE 会话复用，换主机/CUDA 策略会重新创建会话。

| 场景 | 优化前处理输出 fps | 优化后处理输出 fps | 提升 | 总耗时输出 fps 前→后 |
| --- | ---: | ---: | ---: | ---: |
| NVIDIA Vulkan | 11.50 | 40.55 | 252.5% | 11.27 → 38.21 |
| RIFE Lite | 83.25 | 116.88 | 40.4% | 82.02 → 114.88 |
| RIFE Full | 69.19 | 103.72 | 49.9% | 68.33 → 102.12 |
| NR + RIFE Lite | 61.90 | 82.26 | 32.9% | 55.59 → 66.73 |
| RTX Video 2× + RIFE Lite | 22.27 | 29.95 | 34.5% | 17.51 → 21.54 |
| NR + RTX Video 2× + RIFE Lite | 20.53 | 28.17 | 37.2% | 16.06 → 19.85 |

光流前后仅改变 Vulkan 图像布局；RIFE 前后分别为 RGBA 上传/回读兼容路径和显存编码路径，模型与倍率相同。各场景不能横向当作同画质比较。所有数值均为**输出帧**；4× 时除以 4 才是源帧处理 fps。

短片首次运行总耗时：RIFE Lite 主机 7.74 秒 / CUDA 4.14 秒（72 帧），NR+SR+RIFE 主机 8.99 秒 / CUDA 7.50 秒（24 帧）。模型编译缓存、提供程序准备、宿主启动与测量次序都会影响冷启动，不把首次速度当成稳态。早期其他轮次的数值仅作诊断，最终对照以 `cache-final`、`mixed-final` 及光流 `final-benchmark` 为准。

## 画质与正确性

RIFE Lite/Full、80×72 与 32×48、t=0.25/0.5/0.75 的 12 组真实设备对照：CUDA 像素接口与 GPU 主机适配器输出逐字节相同；与原始 CPU 像素图参考最多差 1 个 8 位值，最低 PSNR 69.2 dB。原始帧身份、真实生成像素、边界填充/裁剪与尾帧均验证。

最终压缩视频并非逐像素一致：CUDA NV12 转换的矩阵取整与色度重采样不同于兼容路径的 libswscale，NVENC 路径也不同。以第 1 次重复样本逐帧解码对照，均值与最差帧如下。此为相对实现差异，不是真实高帧率参考或主观画质结论。

| 对照 | 平均 PSNR dB | 最差帧 PSNR dB | 平均 SSIM | 最差帧 SSIM |
| --- | ---: | ---: | ---: | ---: |
| Vulkan 原布局 → 线性 | 56.85 | 48.32 | 0.999412 | 0.995971 |
| RIFE Lite 主机 → CUDA | 44.29 | 43.87 | 0.989439 | 0.988167 |
| RIFE Full 主机 → CUDA | 44.31 | 43.90 | 0.989457 | 0.988241 |
| NR + RIFE | 45.77 | 45.33 | 0.990468 | 0.989656 |
| RTX Video 2× + RIFE | 48.29 | 47.82 | 0.992962 | 0.992210 |
| NR + RTX Video 2× + RIFE | 48.11 | 47.67 | 0.992894 | 0.992234 |

Vulkan HDR 像素烟测验证原始帧 16 位精确保留、生成帧不同于两端、11978 种灰阶；自包含发布包从仓库外启动，PQ 输出为 12 帧 `yuv420p10le / smpte2084 / bt2020`。RIFE 发布包实测 6→24 帧及音轨合流。当前变更未重跑历史原生 4K/8K HDR 全矩阵，不能用短片通过替代该范围。

真实集成覆盖两份模型、2×/4×/6×、单帧、未知帧数、带音频、全片、NR/SR 两种顺序、CPU 滤镜/软件双遍、H.264/HEVC/AV1 NVENC、缩小预览、首帧 NVDEC 失败回退、运行中取消/异常与重启、HDR/无效配置提前拒绝。Release App/CLI/Tests 构建零警告；桌面 UI 实测选择两份 RIFE、补帧开关与控件可用状态，截图和 UIA 树保留本地。

## 仍然存在的限制与实验

GPU 总占用百分比不能直接证明计算管线仍有同等幅度的可消除空闲：NVDEC、光流、CUDA、NVENC 是不同引擎，时间依赖、内存带宽与低开销核也会影响采样。此轮针对实际回读、场景判断串行核、重复初始化和生成帧重复上传做了优化，不宣称已达到绝对上限。

- 将场景抽样由一个 GPU 线程改成 256 线程归约；单次诊断的 pair 阶段均值 3.81→0.14 ms/源帧，最后两帧阈值/重复判断回归通过。该分阶段样本只解释瓶颈，不替代上表整片计时。
- 三个 RIFE 会话的单独 GPU 试验相对一会话稳定约 4% 收益，额外会话增加显存与启动开销，未默认引入三会话；输出逐字节一致，原始实验保留。
- `Vulkan fruc_vulkan → hwmap=derive_device=cuda` 在当前 FFmpeg/驱动/RGBA 路径返回 `Failed to map frame / Function not implemented`，因此光流仍需回读。该结果只证明已测试的映射路线不可用，不代表硬件在其他互操作方案下必然不支持。
- NR 的 CPU 修正量历史累积、VSR 现有主机回读接口、RIFE 模型本身仍有成本。继续消除前两项需另做 GPU 时序累积或原生纹理互操作及相应画质验证。
- FancyTool 的 CPU `minterpolate` 参数筛查未找到经过等质量验证的稳定提速，保留原默认值。原生滤镜无切片线程；更高 CPU 利用率需修改滤镜或安全分段并行，不能靠线程选项保证。

## 复现与证据

在仓库根目录执行，输入路径替换为前述固定素材；如需固定 SDK 10，可从 FancyTool 根目录使用绝对 `--project` 路径。首次 Windows ML 提供程序准备可能联网。

```powershell
.\tools\prepare-ffmpeg.ps1
.\tools\prepare-rife-models.ps1
dotnet build src/Dlss5Demo.App -c Release
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --rife-benchmark '<1080p72帧>' '.qa/reproduce-rife' --benchmark-rife-only
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --mixed-benchmark '<1080p72帧>' '.qa/reproduce-mixed'
# 不加 --benchmark-rife-only 可同时比较 Vulkan 的 optimal/linear 布局。
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --interpolation '<至少12帧SDR>' --interpolation-pixels --gpu
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --rife '<至少12帧SDR>' --cuda-pipeline '<至少12帧SDR>'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --cuda-pixels --rife-pixels --rife-guards '<SDR>' '<HDR>'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --vsr-transport '<至少12帧SDR>'
pwsh -File tools/package.ps1 -OutputRoot '.qa/package'
# 强制主机帧对照：其余选项不变，添加 --gpu-frames 0。
dotnet run --project src/Dlss5Demo.Cli -c Release -- input.mp4 output.mp4 --nr 0 --interpolate 1 --interpolation-backend rife --rife-model lite --interpolation-multiplier 4 --preview 0
```

逐帧画质复现（其他组合替换两份文件名即可；Vulkan 对照文件在 `final-benchmark/`）：

```powershell
Push-Location '.qa/interpolation-port-2026-10-17'
& '..\..\runtime\ffmpeg.exe' -i 'cache-final/rife-lite-host-1.mp4' -i 'cache-final/rife-lite-cuda-1.mp4' -filter_complex '[0:v]split[a][c];[1:v]split[b][d];[a][b]psnr=stats_file=psnr.txt[p];[c][d]ssim=stats_file=ssim.txt[s]' -map '[p]' -map '[s]' -an -f null -
Pop-Location
# 已测试并失败的 Vulkan/CUDA 映射路线（当前 FFmpeg）：
& '.\runtime\ffmpeg.exe' -init_hw_device 'vulkan=vk:0,linear_images=1' -filter_hw_device vk -i '<SDR输入>' -vf 'format=rgba,hwupload,fruc_vulkan=fps=48:perf=medium,hwmap=derive_device=cuda' -f null -
```

所有原始文件均忽略且不进入 Git。Demo 根目录 `.qa/interpolation-port-2026-10-17/` 内：

- `cache-final/measurements.json`、`mixed-final/measurements.json`、`final-benchmark/measurements.json`：每次计时、输出帧数和文件路径；同目录 `summary.json` 为最终汇总。
- `quality-final.json`、`quality-final-*-psnr.txt`、`quality-final-*-ssim.txt`：逐帧及汇总画质数据；压缩视频在各测速目录。
- `final-optical.log`、`final-vsr.log`、`final-pixels-guards.log`、`hybrid-integration.log`、`cuda-cases-final.log`：真实回归。UI 见 `ui-final-*.json/png`，打包日志 `final-package.log`，发布包位于 `final-package/`，从仓库外启动日志见 `package-*-final.*`。
- `vulkan-cuda-map.log`、`concurrency-probe.log`、`cuda-profile.log`、`cuda-lazy-profile.log`：未采用路线与瓶颈实测。并发实验源码保留 FancyTool `.qa/GpuConcurrencyProbe/`，不提交。
- `main-cwd-evidence/` 保存曾从 FancyTool 当前目录运行的全部本轮烟测输出；其中 `legacy-build/` 是旧测试入口产生的 40 个日志/媒体/DLL。日志中的旧绝对路径按此映射查找。已把这些测试的输出根目录改为 `.qa`，FancyTool 补充 `/build/` 忽略规则；这些文件从未被跟踪，无需重写 Git 历史。

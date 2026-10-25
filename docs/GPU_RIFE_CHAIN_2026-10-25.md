# RIFE 组合的显存衔接与界面更新

## 数据路径

兼容的 SDR 任务现在使用：

```text
NVDEC → CUDA 色彩转换 → NR / VSR（可选、两种顺序）→ RIFE → NV12 → NVENC
                          ↕                         ↕
                 D3D12 / CUDA 共享缓冲           CUDA 张量
```

- NR 复用已有 GPU 光流和修正量历史处理，新增设备帧输入/输出；纯设备路径不分配整帧主机暂存。
- VSR 仍在独立进程中运行，避免 NR 与 VSR 的 NGX 全局状态冲突。父进程复制共享资源的 NT handle 后导入 CUDA，管道只传句柄、处理命令和完成状态，进程间共享主机映射缩至 1 字节。原生兼容暂存仍会初始化，但不参与这条路径的像素传输。
- RGBA/BGRA 字节换序在 GPU 完成，按实际行距访问。CUDA 流完成后交给 D3D12，D3D12 fence 完成后才读取结果；CUDA import 在工作进程退出前释放。
- 5 个有界槽位保证顺序和帧对生命周期；解码与 RIFE/NV12 使用独立 CUDA 流，避免不同阶段在同一流上互相等待。NVENC 持有独立 AVFrame 引用，静帧/切镜/尾帧继续共享只读 NV12。
- CPU 仍处理容器、压缩数据、任务调度及同步。NR 切镜标志和 RIFE 比较标志是小量回读；开启预览时只回读缩小后的原始帧/处理帧。这里的“全 GPU”指像素处理和阶段衔接，不表示 CPU 利用率为零。

接口依据：[NVIDIA CUDA / D3D12 互操作](https://docs.nvidia.com/cuda/cuda-programming-guide/04-special-topics/graphics-interop.html)。导入前核对适配器 LUID，D3D12 committed resource 使用 dedicated 标记，复制后的 NT handle 在完成导入后关闭。

## 适用范围和退路

SDR 8-bit YUV420/NV12、支持的 BT.601/709 矩阵、NVDEC、NVENC、RIFE Lite/Full，保持现有最高 3840×2160 像素限制。开启 NR 时需要光流及 GPU temporal 开关；支持关闭历史累积、精细光流以及两种 NR/SR 顺序。GPU 必须一致，当前 NR/NVOFA 使用 CUDA 0。

软件解码、CPU 滤镜、软件编码、双遍、禁用 GPU temporal、关闭 NR 光流、不兼容的输入或旧 VSR 显存接口使用原主机传输路径。RIFE 的 HDR 守卫保持不变，HDR 仍应选择 NVIDIA Vulkan。

CLI/autorun `--gpu-enhancement 0` 只关闭新 NR/SR 衔接，便于同一 NR、同一 RIFE 后端对照；`--gpu-frames 0` 关闭整条显存补帧路径。自动模式的 NVDEC 首帧失败会释放已创建的 NR/VSR 设备资源后从头重试；已经处理帧后不会静默混入另一套历史。

## 验证与复现

RTX 5070 Ti（16 GiB）、驱动 617.14、NR 310.9.16 原版，真实游戏片段 1920×1080 / 60 fps；4K 输入为同一片段的 Lanczos 放大版。RIFE Lite，SR Q4，HEVC NVENC P5 CQ19，无音频/预览；每组 1 轮预热、3 轮交替测试。处理速度按源帧计数，两个路径均排除初始化，含帧循环、编码排空和封装；同时保存包含初始化/释放的完整墙钟时间。

```powershell
dotnet build tests/Dlss5Demo.Tests -c Release --no-restore -p:UseSharedCompilation=false
$tests = 'tests/Dlss5Demo.Tests/bin/Release/net10.0-windows10.0.19041.0/Dlss5Demo.Tests.exe'
$nr = 'runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll'
& $tests --gpu-enhancement .qa/performance-20261025/audio-source.mp4 $nr
& $tests --gpu-enhancement-benchmark .qa/nr-version-comparison-20261025 .qa/new-chain-results $nr
python tools/compare-gpu-enhancement.py .qa/new-chain-results
```

传输测试使用非 D3D12 对齐宽度，检查 NR 开/关历史、VSR 2×/4× 和重复槽位复用的逐字节一致性。集成验证涵盖 NR/SR 两种顺序、RIFE Lite/Full、2×/4×/6× 帧率、音频、缩小预览、CPU temporal/软件解码兼容路径、首帧解码回退、取消和重启。

完整输出与旧路径并非逐字节一致：NVDEC 后在 CUDA 转 RGB，而旧路径使用 swscale。已有 GPU NV12 转换算法保留不变，色度重采样与舍入有细微差异；使用全片 SSIM/PSNR 检查，而不把“传输逐字节一致”扩展成“最终编码相同”。

## 最终性能结果

以下是最终独立解码流实现，同批次交替测试的三次中位数。基线也已启用 GPU NR 光流/历史及 RIFE → NVENC，仅切换新 NR/SR 显存衔接。SR2 表示 2× 宽高，RIFE2/4 表示 2×/4× 帧率。

| 输入及组合 | 源帧数 | 原主机传输 fps | 新显存衔接 fps | 变化 | 完整墙钟秒：原 → 新 |
|---|---:|---:|---:|---:|---:|
| 1080p NR → RIFE2 | 180 | 65.82 | 72.59 | +10.28% | 3.296 → 3.041 |
| 1080p NR → RIFE4 | 180 | 30.40 | 31.46 | +3.49% | 6.474 → 6.259 |
| 1080p NR → SR2 → RIFE2 | 120 | 20.82 | 22.00 | +5.66% | 7.401 → 7.093 |
| 1080p NR → SR2 → RIFE4 | 90 | 7.03 | 7.06 | +0.44%，视为持平 | 14.607 → 14.525 |
| 1080p SR2 → NR → RIFE2 | 90 | 14.91 | 15.75 | +5.64% | 7.876 → 7.454 |
| 1080p SR2 → RIFE2 | 120 | 18.43 | 19.96 | +8.33% | 7.731 → 7.195 |
| 4K NR → RIFE2 | 90 | 19.53 | 21.00 | +7.53% | 5.293 → 4.978 |

这是桌面环境、非独占 GPU 测量；跨批次绝对 fps 有波动，只比较同一批次交替 A/B。4K 输出高倍率 RIFE 测试时 GPU 达到约 99%、300 W，减少 PCIe 帧搬运不等于同比降低推理计算。初版共享解码流中这一组曾慢约 2%；3/4/5 槽测试没有稳定收益，因此保留 5 槽，并拆分解码流。最终高倍率结果按“基本持平”处理，不把 0.44% 当成稳定收益，也不把跨批次差值全部归因于拆流。

原始记录：`.qa/gpu-enhancement-streams-20261025/{manifest,measurements,summary}.json`，含 DLL/Core 哈希、每轮数据及阶段日志。初版记录在 `.qa/gpu-enhancement-benchmark-20261025`，槽位诊断在 `.qa/gpu-slots-benchmark-20261025`。`DLSS5_PROFILE=1` 的阶段时间包含同步等待，不能当作纯 GPU kernel 时间相加。

## 最终画质结果

对三次测量中第 1 次的完整编码输出进行 CPU 解码比较，共 **2,280 帧**：

| 组合 | 帧数 | 平均 SSIM | 最低单帧 SSIM | PSNR dB |
|---|---:|---:|---:|---:|
| 1080p NR → RIFE2 | 360 | 0.985178 | 0.975461 | 43.028 |
| 1080p NR → RIFE4 | 720 | 0.985355 | 0.976421 | 43.107 |
| 1080p NR → SR2 → RIFE2 | 240 | 0.988542 | 0.973679 | 43.748 |
| 1080p NR → SR2 → RIFE4 | 360 | 0.988383 | 0.974814 | 43.360 |
| 1080p SR2 → NR → RIFE2 | 180 | 0.988440 | 0.976531 | 43.682 |
| 1080p SR2 → RIFE2 | 240 | 0.988660 | 0.978058 | 43.831 |
| 4K NR → RIFE2 | 180 | 0.989560 | 0.983916 | 44.279 |

SSIM/PSNR 衡量两条路径的差异，并不是相对无损真值的质量评分。未降低 SR 质量、RIFE 模型规格、推理尺寸或处理帧数。NR/VSR 主机与 GPU 传输独立测试逐字节一致；最终视频的差异来自 CUDA 与 swscale 色彩转换。复现数据在 `.qa/gpu-enhancement-streams-20261025/quality.json`。

最终回归日志：`.qa/gpu-enhancement-final-regression.log`。另用上一发布包的真实 `vsr_host.dll` 验证缺少显存接口时从头回退，保留完整 6→12 帧输出：`.qa/gpu-vsr-fallback.log`。

## 目录按钮与图标

选片旁增加“打开目录”，输出路径旁增加独立目录按钮。存在的文件通过 Unicode Shell item ID 定位并选中，待生成输出打开其父目录；空值或不存在的目录禁用入口。已用 lvt 验证中文、空格、方括号和逗号路径，包含源与产物在不同目录、未生成输出和无效输入。

文件定位使用 [SHOpenFolderAndSelectItems](https://learn.microsoft.com/zh-cn/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems)，避免 Explorer 命令行对特殊文件名的歧义。

图标直接替换 PNG 与 EXE 多尺寸 ICO：青色圆角方块、深色播放三角形，两个元素、两个颜色，无发光、纹理和文字。矢量源 `src/Dlss5Demo.App/Assets/dlss5-video-enc.svg`；`python tools/generate-icon.py` 使用 Pillow 独立栅格化 16/20/24/32/40/48/64/96/128/256 px，并已检查窗口标题栏实际显示。

## 发布包

`dist/Dlss5VideoEnc-win-x64-20261025-141135-994.zip`（729,147,464 字节），包含当前宿主和递归发现的全部 3 个 NR DLL。新 `vsr_host.dll` SHA256：`91D1D63B25870576677BBE8E8476EFA4C586679628EB6004C49ADDBCA6014E66`。

实际自包含程序分别选择包内 310.8.0 与 310.9.16，执行 NR → SR2 → RIFE4 → NVENC，均正常退出，6 源帧生成 24 帧、640×360、96 fps、AAC 音频。日志确认所有帧阶段使用显存路径及包内所选 DLL。证据：`.qa/gpu-chain-package-smoke.log`；两次烟测只验证功能和版本切换，不以短片 fps 推断 NR 版本速度差异。

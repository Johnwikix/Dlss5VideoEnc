# 光流前后处理与 NR 历史累积迁移到 GPU

## 处理路径

光流估计原本就使用 NVIDIA 专用 NVOFA 单元。本次迁移的是原来在 CPU 上执行的灰度转换、网格展开、置信度过滤、运动补偿、输入验证、切镜判定和修正量历史混合。NVOFA 保留原来的 SLOW 质量和标准/精细网格选择。

SDR 路径：

```text
主机 RGBA 输入 → CUDA 灰度 → NVOFA
                         ↓
CUDA 展开 / 输入验证 / 切镜 / 运动过滤
                         ↓（共享 D3D12 color / half2 motion）
                        NR
                         ↓（共享 D3D12 output）
CUDA 修正量累积 → 主机最终帧 → 后续 SR / RIFE / 编码
```

运动场、输入验证平面和历史平面不再回到 CPU。切镜判断仅回读 4 字节标志，用于设置 NR 的 reset。共享缓冲通过适配器 LUID 核对，CUDA 写入完成后才允许 D3D12 读取，NR 栅栏完成后才允许 CUDA 消费输出；取消/错误时等待在途工作，再依次释放 CUDA 导入和 D3D12 导出资源。

这是光流/历史计算的 GPU 路径，不代表整条视频流程零主机传输：当前解码器色彩转换、独立 SR 工作进程及部分编码入口仍传递主机帧。最终输出只回读一次供这些既有接口使用。4K 纯 NR 的阶段数据已显示解码/转换开始成为新的限制。

HDR、关闭光流、旧宿主缺少共享接口、NVOFA 不支持尺寸或适配器不匹配时使用兼容路径。初始化失败可以回退；处理开始后的 GPU 错误会终止该次任务，不拿错误/空白 GPU 输出继续转码。`DlssNrOptions.UseGpuTemporalProcessing=false` 可主动使用旧路径做对照。

## 画面与生命周期验证

`--gpu-temporal` 将 CUDA 内核与冻结的原 CPU 实现逐像素比较，覆盖：

- 1×1、7×9、39×23、129×257，边缘填充与非整块尺寸。
- 1/2/4 像素光流网格、不同 pitch、启用/关闭成本置信度。
- 分数位移、运动边界、NaN/Infinity、越界和低可信度矢量。
- 首帧、切镜、静态与变化输入，2/24/60 fps 下的历史衰减。
- RGBA alpha 保留、输入误差/重置标志一致，历史输出逐字节一致。

CUDA 使用与 CPU 相同的浮点运算顺序：网格展开禁止隐式 FMA，历史插值按运行 CPU 的 FMA 能力选择显式舍入方式；运动场 half 转换保留宿主原有规则。

现有原生回归验证光流方向/单位、精细边界、HDR→SDR 会话切换、连续历史切换，以及 SDR/HDR 的同步/异步队列逐字节一致性。完整小视频回归覆盖四种光流/历史开关组合，SR 2×/4× 前后顺序、末帧预览、单帧排空、取消重启、软件双遍、输出名预留和编码失败清理。

额外用真实 NVOFA grid=1 做完整 CPU/GPU 转码，12 帧解码像素全部一致。310.9.16 的 HDR→SR 2× 输出保留 640×384、6 帧、10-bit、BT.2020/PQ。测试辅助上传曾因默认流 DMA 与非阻塞计算流缺少等待而出现错误，补齐等待后连续三次内核回归通过；生产管线一直采用同一流上的锁页上传。

旧宿主兼容性边界：保存的旧 DLL 缺少共享帧接口时会选择 CPU 路径，能完成 6 帧处理；但这份旧 DLL 在原生退出清理时卡住。关闭 GPU 历史的独立对照同样卡住，因此不能把旧宿主的完整生命周期列为验收通过。发布时须使用本次配套的新宿主；它已通过正常退出和取消重启验证，用户后续添加 NR 版本无需替换宿主。

## 性能测试方法

RTX 5070 Ti / 驱动 617.14，固定 NVIDIA 原版 NR **310.9.16.0**，.NET 10.0.13。复用 [NR 版本对照](NR_VERSION_BENCHMARK_2026-10-25.md) 的同一份真实录像测试片；4K 是从 1080p 放大的输入。仅切换 `UseGpuTemporalProcessing`，CPU 组同样使用本次编译的原生宿主和应用。

HEVC NVENC P5/CQ19、SR Q4、RIFE Lite，光流/连续历史/修正量累积启用，关闭音频/预览。每组先预热，再按 GPU→CPU、CPU→GPU、GPU→CPU 顺序各测三次，取中位数。数据均为源帧/秒，包含编码排空与封装，排除会话初始化；原始 JSON 同时记录完整调用耗时。阶段计时是调用墙钟，GPU 等待和传输会计入相应阶段，不能当作纯 GPU 内核时长。

## 完整管线结果

| 工况 | 源帧数 | CPU 前后处理 | GPU 前后处理 | 提升 |
| --- | ---: | ---: | ---: | ---: |
| 1080p NR | 240 | 50.39 | 117.33 | +132.8% |
| 4K NR | 180 | 13.79 | 35.29 | +155.9% |
| 1080p NR → SR 2× | 180 | 53.40 | 73.87 | +38.3% |
| 1080p NR → SR 4×（8K 输出） | 90 | 19.47 | 25.42 | +30.6% |
| 1080p SR 2× → NR（NR 在 4K） | 120 | 13.18 | 33.51 | +154.3% |
| 1080p NR → RIFE 4× | 180 | 29.15 | 30.47 | +4.5% |
| 1080p NR → SR 2× → RIFE 2× | 120 | 20.25 | 21.07 | +4.1% |
| 1080p NR → SR 2× → RIFE 4× | 90 | 8.44 | 8.56 | +1.4% |

1080p NR 的 CPU 三次范围为 44.68–58.60 fps，GPU 为 110.60–124.20 fps；4K NR 分别为 13.57–13.89 和 33.22–35.65 fps。不同批次的 CPU 基线会有波动，应使用本轮交替对照，不与前一份 DLL 版本测试直接相减。

1080p NR 的完整调用耗时中位数从 5.09 s 降至 2.37 s，4K NR 从 13.41 s 降至 5.47 s。NR→SR 2× 因初始化波动，完整调用仅从 4.98 s 降至 4.75 s；NR→SR 2×→RIFE 4× 总耗时基本持平（12.25 s / 12.31 s）。不能把处理阶段百分比直接当作短视频的总耗时收益。

典型 1080p GPU 阶段墙钟：展开/验证/切镜/half 引导准备约 0.27 ms，历史累积连同最终回读约 0.82 ms。4K 样本为 0.82 ms 与 2.98 ms；这时解码/色彩转换读帧约 25.99 ms，已经接近或超过整个 GPU NR 阶段。4K RIFE 4× 组合的 GPU 采样约 96–97%，迁移后的剩余工作主要由补帧等 GPU 计算限制，因此没有同等比例的端到端收益。

八种工况的正式第 1 轮 CPU/GPU 输出分别解码，比较全部帧的 framemd5：**2,130 帧全部一致**，包括 8K 超分输出与 RIFE 生成帧。此结论比较本次两种实现，不代表 NR 相对原始视频无画面变化。原始检查结果在 `quality.json`。

## 复现

```powershell
.\native\build-host.bat
python tools/compile-video-cuda.py --nvrtc 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8\bin\nvrtc64_112_0.dll'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu-temporal
$env:DLSS5_PROFILE = '1'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu-temporal-benchmark .qa/nr-version-comparison-20261025 .qa/gpu-temporal-comparison runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll
```

CUDA Toolkit/NVRTC 仅用于开发时重新生成 PTX，发布程序直接加载内嵌 PTX，不要求用户安装 Toolkit。原生宿主源码现在纳入 `native/nr_host`，附上游 MIT 许可，构建不再依赖忽略目录 `_ref` 中的可变副本。

本轮证据保留在 `.qa/gpu-temporal-comparison-20261025`（清单、每次测量、逐阶段日志和 GPU 采样），基础/管线回归日志为 `.qa/gpu-temporal-integration.log`。

## 发布验证

最终自包含包：`dist/Dlss5VideoEnc-win-x64-20261025-131944-430.zip`，730,370,132 字节。ZIP 包含三个 NR DLL 和新的宿主（SHA256 `E881CD1A5037451422C8142AE1D0FE582E97247AE67E7B14A11A9C9FD64691EB`），附 DLSS5Tool MIT 许可。

包内程序使用包内 310.9.16 完成 NR + SR 2× + RIFE 4×：6 源帧输出 640×360 / 96 fps / 24 帧，保留 AAC 音轨，时长 0.25 s。日志确认进入 GPU 光流/历史路径、释放 NR 会话并正常退出。运行时无需开发机 CUDA Toolkit。

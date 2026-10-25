# 组合性能与 NR 版本选择验证

## 变更与适用范围

RIFE 原先在每个中间时刻重新转换整对输入。现在每对只准备一次六个 RGB 平面，后续只更新时间平面；重用 RunOptions，取消后仍等待在途推理完成。兼容路径使用 CUDA 上传/像素内核并交换上一帧缓冲，移除两个额外的 ONNX 像素转换会话。GPU 直通任务不再分配三份主机 RGBA 帧（4K 约 95 MiB，按分配大小计算，不是进程 RSS 测量）。

重复帧、切镜与尾帧沿用原有判定，GPU 编码复用只读 NV12 表面，各 AVFrame 保留独立时间戳。模型和推理尺寸不变时，切换输出滤镜或编码传输路径可复用网络；任务间重置帧对状态。仍只保留一个网络，失败、取消、模型/尺寸改变时释放。

选择的输出尺寸已等于 SR 处理尺寸时，省略恒等缩放，恢复兼容的 GPU 编码路径。有裁剪时不省略；真正的缩放仍在 RIFE 后使用原 FFmpeg Lanczos 链。未降低 SR 质量、未更换 RIFE 模型、未将 RIFE 移到 SR 前、未开放 HDR/超过 4K 的 RIFE 输入。

## 短片基线

2026-10-25，RTX 5070 Ti，驱动 617.14，.NET 10 Release，HEVC NVENC P5/CQ19，SR 质量 4，禁用预览/音频。固定 `testsrc2` 1920×1080、24 fps、24 帧，缩放成 540p/1080p/2160p 输入。旧新程序在同一机器顺序运行，每组两次；下表取第二次复用会话的数据。NR 组合使用原默认 310.8.0.0，以免新版模型混入性能对照。

单位为**源帧/秒**，包含编码排空/封装，不含模型初始化和最终资源释放。原始 JSON 另记 wallSeconds。1 秒短片对初始化和调度很敏感，数个百分点的变化视为波动，不能推算长片加速比例。

| 输入 / 配置 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 540p，SR 2× | 107.46 | 109.39 |
| 540p，SR 4× → 4K | 63.59 | 63.33 |
| 1080p，SR 4× → 8K | 24.24 | 25.48 |
| 540p，RIFE Lite 2× | 161.27 | 154.28 |
| 1080p，RIFE Lite 4× | 31.26 | 31.68 |
| 4K，RIFE Lite 2× | 23.88 | 24.04 |
| 540p，SR 2× + RIFE Lite 4× | 28.93 | 28.70 |
| 540p，SR 4× + RIFE Lite 2× | 22.66 | 22.60 |
| 1080p，SR 2× + RIFE Lite 2× | 20.39 | 20.64 |
| 540p，NR + SR 2× + RIFE Lite 4× | 23.33 | 23.84 |
| 540p，SR 2× + RIFE Lite 6×，输出 720p | 13.35 | 14.69 |
| 1080p，RIFE Lite 4×，输出 720p | 20.53 | 21.88 |
| 540p，RIFE Lite 6×，主机编码传输 | 34.70 | 38.56 |
| 540p，RIFE Full 4×，输出 720p | 46.96 | 49.10 |

主要收益在带输出滤镜/主机传输的组合；推理占主导的 GPU 直通路径基本持平。以上矩阵测量了帧对/像素/重复帧优化；随后新增的恒等缩放和跨传输路径会话复用另有功能回归，不计入表内的提升。

复现（输入至少 24 帧，建议先关闭其他 GPU 工作负载）：

```powershell
$env:DLSS5_PROFILE = '1'
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --combination-benchmark input.mp4 .qa/my-combination-benchmark
```

本机原始证据：`.qa/performance-20261025/baseline/measurements.json`、`after/measurements.json` 及逐工况日志；二进制和视频不入 Git。沙箱内 Windows ML 无法枚举提供程序，真实基线和修改后矩阵均在同一桌面权限环境运行。

## 正确性验证

- 22 项基础回归通过，包括版本目录发现、路径 ID 稳定性、无文件版本元数据、恒等缩放/裁剪边界，以及原有历史/有界管线检查。
- Lite/Full 在 80×72、32×48 填边输入的 .25/.5/.75 时刻，与原始 CPU 模型比较 PSNR 超过 40 dB；CUDA 与主机适配结果逐像素一致。帧对张量复用与每次重新准备逐像素一致。
- 1080p RIFE 4×→720p、540p SR 2×+RIFE 6×→720p 两组完整输出，旧新版本解码后的 SSIM 都是 1.000000。
- RIFE 2×/4×/6×、首尾/单帧、NR/SR 前后顺序、软件双遍、完整视频和未知帧数音轨通过。HEVC/H.264/AV1 CUDA 输出、缩小预览、软件解码/首帧回退、取消重启通过。
- 同尺寸输出确认仍进入 CUDA 路径；切换真正的输出缩放确认复用同一个 RIFE 网络。HDR 输入、非法倍率/模型/后端仍在输出预留前拒绝。
- 黑/白静帧切镜素材分别经 NVDEC 显存路径和软件解码→GPU 编码路径输出 6× 补帧；验证全部 72 帧像素及第 36 帧切镜边界，确认 NV12 共享没有串帧，尾帧数量正确（`--cuda-repeats`）。

命令及日志：

```powershell
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --rife-pixels --cuda-pipeline audio-source.mp4 --rife audio-source.mp4 --rife-guards audio-source.mp4 hdr-source.mp4
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --nr-versions runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll
```

## NR DLL 与发布

选择器扫描实际 `runtime`/`mods`，读取 PE 文件版本，不加载 DLL 来枚举，不编入版本号或显卡代际列表。保留默认自动策略；显式选择优先，保存的是路径 ID。指定文件消失时明确失败，关闭 NR 时不加载/解析所选运行库。CLI 和 `--autorun` 增加 `--nr-runtime`。

本次导入 `DLSSNR-DLL-Options.zip` 的两个版本并保留 310.8.0.0 默认 DLL：

| DLL | SHA256 |
| --- | --- |
| 310.9.16 NVIDIA Original / RTX50 | `2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F` |
| 310.9.16 Community Patched / RTX20–40 | `A8A237A967469C455A84654932B513AB656FBAA1E0C44504B1E1BF0890F902B8` |

哈希与压缩包 README 一致。RTX 5070 Ti 完成新版原版短片转码，并在同一进程执行默认→新版→默认；每轮检查进程实际加载模块的绝对路径和 NR 输出。社区版仅完成导入及哈希核对，没有 RTX20/30/40 硬件运行验证。

发布脚本按目录结构携带发现的全部 DLL；用户以后只需增加 `runtime/自定义版本/nvngx_dlssnr.dll` 并点击刷新。UIA 验证了发现条目、键盘选择、长目录标签显示、刷新保留选择和 settings.json 的相对路径保存。

自包含发布包 `dist/Dlss5VideoEnc-win-x64-20261025-123427-060.zip` 已生成（730,353,362 字节），ZIP 条目核对包含三个 NR DLL。包内 `--autorun` 使用包内 310.9.16 RTX50 DLL 完成 NR + SR 2× + RIFE 4×：6 源帧输出 640×360 / 96 fps / 24 帧 HEVC，保留 AAC 音轨，时长 0.25 秒；日志确认实际 DLL 路径位于发布目录。

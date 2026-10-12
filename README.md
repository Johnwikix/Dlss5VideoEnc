# Dlss5VideoEnc

Avalonia 视频转码工作台：可选 RTX Video Super Resolution（关闭 / 2× / 4×）→ DLSS NR → FFmpeg 编码。HDR 视频通过 RGBA16F / 10-bit 路径处理并保留 HDR 元数据。

## RTX Video Super Resolution

VSR 宿主是可选运行组件。要启用界面中的 2× 或 4×，需要安装 RTX Video SDK 1.1（或兼容版本），并把 SDK 根目录设置到 `NV_RTX_VIDEO_SDK`，然后在仓库根目录执行：

```powershell
native\vsr_host\build.bat
```

脚本会生成并放置 `runtime\vsr_host.dll` 和授权的 `runtime\nvngx_vsr.dll`。没有这两个文件时，关闭档仍可使用；选择 2×/4× 会在启动转码时给出明确错误。

处理顺序固定为：

```text
源视频 →（可选）RTX Video 2×/4× → DLSS NR → 编码
```

## DLSS NR 视频时序稳定

视频转换默认保留 NR 会话的连续历史，并使用 NVIDIA 驱动中的 Optical Flow API
估计当前帧到上一帧的运动向量。这些向量同时送入 NR，并用于对齐输出修正量的历史。
向量基于实际 NR 输入生成，因此 VSR 放大后的坐标与 NR 输入保持一致。

抗闪烁处理累积的是「NR 输出 − NR 输入」的有符号修正量，再加回当前输入。
局部颜色不匹配、运动边界、越界、无效像素和切镜会拒绝旧历史；切镜还会重置 NR
及光流历史。历史衰减按源视频帧率计算，预览与编码使用同一处理结果。
HDR 使用浮点修正量，保留原有 RGBA16F 输出路径。

光流接口不可用或不支持输入尺寸时自动使用静态区域累积。可在日志中确认实际路径。
此处理增加转码耗时；它能降低 NR 修正的闪烁，但错误光流和无真实深度的场景仍可能
存在残余伪影。处理思路参考 [Magpie 的时序稳定实现](https://github.com/SAOG0721/Magpie/blob/experimental/src/Magpie.Core/DLSSNRTemporal.cpp)。

命令行对照：`--temporal 0` 关闭新增时序处理，`--motion-guidance 0` 保留静态区域累积。
Core API 对应 `DlssNrOptions.TemporalStabilization` 与 `MotionGuidance`。

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

HDR 源使用 FFmpeg zscale 解码为 RGBA16F，VSR 使用 10-bit RGB 纹理，之后继续以 RGBA16F 交给 DLSS NR，最后转换回 10-bit RGBA 输入编码器。

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

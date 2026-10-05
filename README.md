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

# RTX Video 驱动接口与 SDK 对照

2026-10-17，Windows，GeForce RTX 5070 Ti，驱动 617.14，主屏 3840×2160。
SDK 为 RTX Video SDK 1.1 的 `nvngx_vsr.dll` 1.6.0.0，质量 4。

## 结论

驱动确实包含 RTX Video 实现，但不能将它作为当前 SDK 后端的等价替代。
当前驱动通过 D3D11 视频处理扩展接受请求，不代表实际执行了 AI 超分。
在本次 RGB 输入实验中，1800p 和 4K 输入的扩展开/关 RGB 输出完全相同，且未加载 `nvdlppx.dll`；
同一输入通过 SDK 则产生明显不同的超分结果。根据“行为一致才替换”的要求，生产管线保留 SDK。

物理屏幕不是输出尺寸上限：无窗口、无交换链的离屏实验中，1440p→5K、1080p→8K 都能加载驱动模型并改变输出。
本次结果更符合驱动路径对输入尺寸的限制；没有通过更换显示模式来证明它在所有屏幕配置下都独立于屏幕。
NVIDIA 的 [RTX Video FAQ](https://nvidia.custhelp.com/app/answers/detail/a_id/5448)也列出 360p–1440p 输入范围。

## 测量

所有开/关差异和 SDK 对比仅统计 RGB，忽略 alpha。驱动处理每种模式连续三帧，SDK 对同一输入处理一帧。
输入包含固定的彩色图案和边缘；输出为未编码 RGBA，避免视频编码器引入差异。

| 输入 → 输出 | 驱动开/关改变的 RGB 分量 | 加载驱动 AI 模块 | 驱动开启 vs SDK 的 RGB PSNR |
| --- | ---: | --- | ---: |
| 1280×720 → 2560×1440 | 10,547,203 | 是 | ∞，逐分量相同 |
| 2560×1440 → 5120×2880 | 42,187,242 | 是 | 63.118 dB |
| 1920×1080 → 7680×4320 | 94,945,690 | 是 | 35.118 dB |
| 3200×1800 → 6400×3600 | 0 | 否 | 26.298 dB |
| 3840×2160 → 7680×4320 | 0 | 否 | 26.296 dB |
| 4096×2304 → 8192×4608 | 0 | 否 | 26.303 dB |

1800p、4K 和超过屏幕的 4096×2304 输入中，SDK 相对普通缩放分别改变了 65,913,679、94,925,786 和 107,998,271 个 RGB 分量。
4× 的像素差异说明也不能承诺两条路径在可触发范围内始终逐像素等价。
本实验没有更改 NVIDIA 全局开关/质量设置，不能将差异推广为所有驱动版本、所有输入格式或所有质量配置的结论。
HDR 未进行驱动/SDK 等价性验证；SDR 已有反例，足以否决全面替换。

## 复现

`native/vsr_host/driver_probe.cpp` 仅使用 Windows SDK、D3D11 和 DXGI。
驱动扩展 GUID/参数可对照 [mpv 的 D3D11 实现](https://github.com/mpv-player/mpv/blob/master/video/filter/vf_d3d11vpp.c)。
扩展接口定义见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamextension)。

```powershell
.\native\vsr_host\build-probe.bat
$repo = (Get-Location).Path
.\build\driver-probe\driver_probe.exe 3840 2160 2 "$repo\build\driver-probe\2160p" "$repo\runtime\vsr_host.dll" "$repo\runtime"
```

程序打印桌面尺寸、扩展开关耗时、RGB 差异、模块加载位置，输出 `*-0.rgba`、`*-1.rgba`、`*-sdk.rgba`。
不复制驱动内部 DLL，不修改显示设置，也不修改 NVIDIA 全局配置。
SDK 对照使用现有原生宿主，运行于独立实验进程；其已知的阻塞清理由进程退出回收。

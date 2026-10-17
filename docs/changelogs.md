# 变更记录

## 2026-10-18

- 移除多余的 `runtime/fruc-ffmpeg`，统一 FFmpeg 依赖到 runtime，修复进程内 NVIDIA Vulkan 光流设备绑定并优先线性图像；保留 16 位 HDR。
- 接入 RIFE Lite/Full、模型 SHA256 校验、界面/CLI 选择与 SDR 守卫；模型直接放 runtime，不提交权重和测试产物。
- 增加 NVDEC → CUDA RIFE → NVENC 显存管线与 NR/RTX Video 组合的生成帧直编，复用单一模型会话并完善取消、音轨、尾帧和资源释放。1080p 4× RIFE Lite 83.25→116.88 输出 fps；NR+SR+RIFE 20.53→28.17，边界与画质见验证报告。
- Release、真实 GPU/光流/HDR、RIFE/组合管线、取消重启、桌面 UI 与自包含包烟测通过。修正旧烟测输出到 `.qa`，原始证据不进入 Git。

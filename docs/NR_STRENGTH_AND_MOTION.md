# 高强度、光流与输出路径修复（2026-10-14）

## 复现与修复

用用户截图中的 Starfield 视频（2560×1440、60 fps），将强度、局部色调、局部结构、
皮肤结构都设为 20，转换前 60 帧并启用 SR 2×，第 54 帧重现彩色噪声和黑色裂纹。
关闭光流、修正量累积和超分后，原始 NR 输出仍有同类异常；只设总强度 20 没有复现。
四项设为 2 或 5 时，同一位置恢复正常，根因是过大的局部模型参数。

[Magpie 的控件定义](https://github.com/SAOG0721/Magpie/blob/experimental/src/Effects/DLSSNR/DLSSNR_AI_Filter.hlsl)
将这些 NR 参数设为 0–2。本项目按用户要求将调整上限统一为 **5**，不是声称运行库
在任意场景下都保证此范围的视觉效果。保持默认值，UI 细调步长改为 0.1。
Core 的 Create 与 UpdateOptions 共用规范化：旧配置/CLI 的 20 变成 5，负值变成 0，
NaN/Infinity 使用各参数的默认值，正常范围内的值保持原样。

复现与结果保存在 `build/strength20/`：`before-all20.mp4`、`raw-all20.mp4`、
`raw-intensity20.mp4`、`reference-all5.mp4`、`fixed-all20-to5.mp4`、`fine5.mp4`。
PNG 是第 54 帧缩小后的对照。最后两份分别确认旧 20 参数被限制到 5、精细匹配正常输出。

## 光流可靠性与精细匹配

保留 NVOF SLOW 质量。标准档使用原有 4×4 网格；新「精细光流匹配」开关请求驱动
支持的最细网格（本机 1×1），不支持时回退。默认仍用标准档；两档均增加：

- 8-bit cost 缓冲，保守拒绝高匹配代价区域（本项目阈值 128）。
- 拒绝重投影越界，以及有输入历史时 RGB 引导误差明显的矢量（最大分量差 ≥0.12）。
- 送入 NR 的拒绝项使用有限的零矢量，额外历史的对应误差设为正无穷以拒绝累积。
  参考宿主的 FloatToHalf 会把 NaN 转成 half infinity，不能用 NaN 标记 NR 无效运动。

网格能力和 cost 接口来自 NVIDIA 的
[通用 API 定义](https://github.com/NVIDIA/NVIDIAOpticalFlowSDK/blob/master/nvOpticalFlowCommon.h)、
[CUDA 函数表](https://github.com/NVIDIA/NVIDIAOpticalFlowSDK/blob/master/nvOpticalFlowCuda.h)。
1×1 有专门的直接展开路径，避免重复双线性插值。帧缓存估算目标从 512 调至 576 MiB，
容纳置信度元数据，避免常见 1440p→5K 工况少一个在途槽；原生/历史内存另计。

本机 RTX 5070 Ti、617.14 驱动的合成测试：两侧纹理向相反方向移动 8 像素，测量
边界附近 24 像素宽的区域。标准 4×4 平均端点误差 **0.671 px**，精细 1×1
**0.152 px**，两档有效覆盖均为 100%。这是已知运动的合成验证，不表示所有真实
视频的误差都会下降相同比例；重复纹理、遮挡和光照变化仍可能造成错误匹配。

Starfield 前 120 帧、四项 5×、NR→SR 2×：标准与精细均约 20.9 fps，此工况编码
较慢，不能据此认为精细光流没有成本。日志 `standard5.log` / `fine5.log` 保留各阶段耗时。
另测 1080p COD 前 180 帧、不启用超分：标准约 52–53 fps，精细约 44.4 fps。
增加的匹配代价、可靠性筛选和更细网格有实际开销，因此精细档保留为可选项。

## 路径与防覆盖

从 AppSettings 移除 InputPath/OutputPath；旧 JSON 的相应字段不再恢复，保存时移除。
显式 `DLSS5_INPUT` 自动化环境变量仍可用于本次启动。输入变化时同步清空旧输出，
按新输入和容器重新生成建议名；成功时显示 Core 实际采用的文件名。

建议名跳过已有文件和目录，追加 `_1`、`_2` 等后缀。Core 用 CreateNew 原子预留
不存在的文件名，并持有不允许删除共享的句柄，避免编码器启动前被其他任务替换。
FFmpeg 只接收当前任务预留的新路径，双遍编码也保持同一所有权。
本机 FFmpeg 的 `-n` 在拒绝已有文件时会返回 0，仅靠该选项和退出码不能证明输出成功，
因此采用文件所有权方案。已有文件与并发创建者均有回归验证。

## 验证

19 项 CPU 回归：包含 5× 边界、非有限数值、文件名冲突、8 个并发预留者、可靠性
过滤及原有时序测试。真实 GPU 测试包含位移方向/单位、运动边界、HDR→SDR 会话切换
及 NR 同步/异步队列一致性。

SDR/HDR 管线检查包含两种 NR/SR 顺序、各个光流/历史开关、单帧排空、取消重启、
已有文件保留、编码器失败、预留与编码启动间的并发创建；SDR 另含双遍编码。
UIA 检查旧 20× 设置显示为 5×、启动路径为空、换输入重算路径、同名 `_2` 建议、
精细开关随光流禁用、开启精细的真实转换，关闭后设置 JSON 无输入/输出路径。
再次启动路径仍为空；使用默认输出连续转换三次，确认序号递增且不会嵌套旧序号。

```powershell
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --gpu
dotnet run --project tests/Dlss5Demo.Tests -c Release -- --pipeline '至少12帧的小素材.mp4'
dotnet run --project src/Dlss5Demo.Cli -c Release -- '输入.mp4' '输出.mp4' --frames 60 `
  --intensity 5 --local-tone 5 --local-struct 5 --skin 5 --sr 2 --fine-motion 1
```

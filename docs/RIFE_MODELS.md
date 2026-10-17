# RIFE 补帧模型

使用 `tools/prepare-rife-models.ps1` 将 Practical-RIFE 4.25 Lite 和 4.25 直接安装到 `runtime/`。运行时不需要 Python 或 PyTorch。

- 作者： Zhewei Huang (hzwer), https://github.com/hzwer/Practical-RIFE
- 许可：上游代码与所链接的训练模型内容采用 MIT，原文见 `licenses/RIFE-MIT-LICENSE.txt`。
- ONNX 转换来源： https://github.com/AmusementClub/vs-mlrt/releases/tag/external-models
- 上游压缩包： `rife_v4.25_lite.7z` and `rife_v4.25.7z`.
- 下载文件为上游压缩包内未经修改的 `rife_v2` 网络图。
- 单文件镜像： https://huggingface.co/notaneimu/onnx-image-models
- 固定镜像提交： `f7bf1c91e94ef516900f68456528fa781e2e7174`.

| 安装文件 | 上游文件 | SHA-256 |
| --- | --- | --- |
| `rife_v4.25_lite_v2.onnx` | `rife_v2/rife_v4.25_lite.onnx` | `610b5de57cdcfbcce9914c23e60a1cd357779a6f9582a1bcfcb035f8eb38509b` |
| `rife_v4.25_v2.onnx` | `rife_v2/rife_v4.25.onnx` | `65c57a5e4abb17ad67faf35054291ac53affab0506d509d2e66698f6ecd75584` |

网络输入 `[1,7,height,width]` FP32：第一帧 RGB、第二帧 RGB、全尺寸时间步平面；输出 `[1,3,height,width]` RGB。模型自行处理常规填充；尺寸不超过 64 的轴先复制边界扩至 128，结果裁回原尺寸，不切块推理。

模型放在 `runtime/rife_v4.25_lite_v2.onnx` 和 `runtime/rife_v4.25_v2.onnx`，启动时校验 SHA256。
本项目移植 FancyTool 的 GPU 像素转换方案；两个无权重 GraphProto 模板按本项目 RGBA 字节顺序生成。
Windows ML 2.4.89 提供 ONNX Runtime 与 NVIDIA TensorRT RTX EP；不另带第二份 ORT。
当前 RIFE 仅处理 SDR、最多 3840×2160 像素。保留原始帧，生成帧在 GPU 转换与推理；兼容路径传输 RGBA，显存路径通过 CUDA IO binding 直接交给 NVENC。

# 变更记录

## 2026-11-14

- 精简 DLSS NR 运行库：RTX 50 实测可运行面向 RTX 20/30 的社区补丁版，移除 50 系专用与 NVIDIA 原版 DLL。`runtime` 现只保留两个社区版：根目录激活位 310.8.2.0（版本串 `310.8.SF.0`，SF-v2，SHA-256 `6EB209E764F39872625DEBD6ABAF45E2BB6322F6F270F781F70C059AE30B3927`，165,830,144 字节）与 `runtime\nr\Community-Patched-310.9.16-RTX20-RTX30-RTX40-Series` 的 310.9.16.0（SHA-256 `A8A237A967469C455A84654932B513AB656FBAA1E0C44504B1E1BF0890F902B8`，276,162,560 字节）；`download\50x` 解压副本一并删除。`runtime` + `download` 净释放约 729 MiB。当时的恢复来源为 `H:\download\DLSSNR-DLL-Options.zip`（50 专用原版、310.9.16 社区版）与 `download\50.zip`（310.8.0.0 原版）；历史构建副本的后续清理见下一条。版本列表仍由目录递归发现、无版本白名单，故选择逻辑无需改动；同步更新 `DemoPaths.cs` 注释与 README 的运行库章节。历史文档中指向 `NVIDIA-Original-310.9.16-RTX50-Series-Only/...` 的复现命令已失效，已在各文档标题下标注。
- 二次清理构建产物：按 SHA-256 精确匹配删除 `dist/`、`.qa/`、`artifacts/`、`build/`、`src/**/bin`、`packaging/**/bin|obj` 中 54 份旧版 `nvngx_dlssnr.dll` 副本（310.8.0.0 NVIDIA 原版 21 份、310.8.0 社区 RTX 40/50 版 16 份、310.9.16 RTX 50 专用原版 17 份，逻辑合计约 9.93 GiB），并删除 `download\50.zip`（104 MiB），合计释放约 10.0 GiB；空目录一并清理。保留两个社区版及其 PE 证书目录修补副本共 35 份，生产代码无硬编码路径、选择逻辑不变。此后 310.8.0.0 原版与 310.8.0 社区 40/50 版不再有本地备份，如需须重新下载；50 专用原版仍可从 `H:\download\DLSSNR-DLL-Options.zip` 恢复。历史构建包不再自带可用的根目录激活位 DLL，需要时重新执行发布脚本生成。
- 新增 MSIX 发布：`packaging/Dlss5VideoEnc.Msix`（WAP 工程、清单、6 个磁贴图标）与 `tools/package-msix.ps1`（MakeAppx 命令行出包 + 可选测试证书签名）。两条路径共用 `tools/package.ps1` 的完整发布负载，注入 `Dlss5Demo.App\` 子目录并保留 `runtime\` 层级；`package.ps1` 新增 `-OutputName` / `-ReuseOutputDirectory` / `-NoZip`，默认时间戳目录 + ZIP 行为不变。
- 适配 MSIX 只读安装目录：`DemoPaths` 通过包标识检测，把 Serilog 日志、`settings.json`、原生宿主日志及 ONNX / TensorRT 缓存重定向到 `%LOCALAPPDATA%\Dlss5VideoEnc`（可由 `DLSS5_DATA_DIR` 覆盖）；ZIP / 开发运行路径保持不变。日志目录不可写不再阻断启动。
- WAP 工程在解决方案中补齐 `Build.0` 并配套 `Msix.NoDesktopBridge.targets`：VS 部署必需 `Build.0`（否则报 DEP1700 缺 `build.appxrecipe`），而检测不到 DesktopBridge targets 时由空目标接管，使 `dotnet build Dlss5VideoEnc.sln` 仍能成功（命令行会出现一条预期内的 NU1503 还原提示）。
- 修复 VS 部署时舞台命令「"powershell.exe" ... 已退出，代码为 1」：根因是 `tools/package.ps1` / `package-msix.ps1` 为 UTF-8 **无 BOM**，Windows PowerShell 5.1 按 GBK 解码产生 6 / 7 处语法错误，脚本一行都未执行（现象是 `artifacts/msix/payload` 时间戳与内容完全不变）。两脚本改为 UTF-8 with BOM；并把 `OutputRoot.TrimEnd(char[])` 换成纯字符串 `-replace '[\\/]+$', ''`，消除 5.1 的方法重载风险。用真正的 WinPS 5.1 端到端复现：退出码 0、中文警告正常、产出含 FFmpeg / 5 份许可证的完整负载。PowerShell 7 默认按 UTF-8 读无 BOM 脚本，所以此前命令行验证未暴露该问题。
- 修复 VS「未能对 … 签名 / Done Adding Additional Store」：`APPXSIP_LOG=1` 实测得到 `SignedCode::Sign returned error: 0x800700C1`，根因是 `runtime\nr\Community-SF-v2-310.8-RTX20-RTX30-Series\nvngx_dlssnr.dll` 的 PE Security Directory 指向文件末尾之外（偏移 165830144 = 文件长度、长度 10352），AppxSip 为包内 PE 生成代码完整性目录时拒绝签名。`tools/package.ps1` 新增 PE 扫描，只清除确实损坏的 8 字节证书目录项（合法签名不动、只改负载副本、`runtime\` 源文件不变），且在生成 `SHA256SUMS.txt` 前执行。
- 修复 WAP 入口负载前缀推导：原来把多个 `Filename=Dlss5VideoEnc` 项的 `TargetPath` 用 `;` 连接后传给 `GetDirectoryName`，产生含分号的垃圾前缀，导致入口工程托管负载没被移除、又注入一批 `%3B` 乱码路径，包内每个文件出现两份（536 条目 / 1.95 GB）。现在只取首个 `.exe` 项并对含分号前缀直接报错；正常包为 281 条目 / 约 1 GB。`GenerateTemporaryStoreCertificate` 改为 `false`（已显式指定指纹）。
- `tools/package-msix.ps1` 新增 `-PayloadDirectory`，测试证书选择过滤有效期与私钥，避免本机 2100 余张同 Subject 测试证书中挑到过期证书。端到端验证：修复后的负载经 MakeAppx 出包（990.8 MB），`signtool sign /fd SHA256 /sha1 …` 与 `verify /pa` 均成功（此前同一操作报 `0x800700C1`）。
- 修复 VS 部署链路：`tools/package.ps1` 的 `-ReuseOutputDirectory` 改为直接删除并重建目录（原管道 `Remove-Item` 绑定失败），移除 `Path.GetRelativePath` 以兼容 Windows PowerShell 5.1，新增 `-AllowGenericFfmpeg` 供 MSIX 本地调试；WAP 舞台默认改用系统自带 `powershell.exe`，并按显式 `MsixFfmpegDirectory` / `MsixRequireFrucVulkan` 决定是否严格校验 `fruc_vulkan`。
- NR 模型默认改为 A（旧模型），覆盖 GUI、Core、CLI/autorun 及无效值回退。旧版保存的自动档首次升级迁移为 A；B 和后续主动选择的自动档保持。配置源生成序列化回归覆盖缺字段、一次迁移、显式 Auto/B 与重复加载。
- 修复 MP4 编辑列表排除存储样本时的“解码提前结束”误报：容器帧数仅作进度估算，处理至正常 EOF 或显式帧数上限，编码和最终进度采用实际解码帧数。主机、VSR 编码、双遍和 CUDA RIFE 路径共用该规则，解码错误和零帧输入仍失败。原片登记 2100 帧、实际可播放 2099 帧，软件解码与 NVDEC 均完整输出 2099 帧。
- 增加 NR 模型预设选择与持久化：自动／A 旧模型／B 轻量模型，GUI、CLI 和 autorun 均将预设传到原生 Feature 创建及光流资源重建。310.9.16 的 A 使用旧模型，自动与 B 使用轻量模型；310.8 只有旧模型。CLI 参数为 `--nr-preset auto|a|b`。
- 回归命令：`dotnet run --project tests/Dlss5Demo.Tests -c Release -- --decode-counts [原片路径] --nr-presets runtime/nvngx_dlssnr.dll runtime/nr/NVIDIA-Original-310.9.16-RTX50-Series-Only/nvngx_dlssnr.dll`。覆盖 MP4 编辑列表、B 帧排空、两种解码器、帧数限制及 NR A/B 模型和资源重建。（第二个 DLL 已随本次精简移除，复现时请替换为现存社区版。）

## 2026-10-25

- RIFE 组合新增 NVDEC → NR/VSR 共享显存 → RIFE → NVENC，支持 NR/SR 两种顺序；VSR 独立进程只交换句柄和状态，减少中间整帧回读/上传。解码采用独立 CUDA 流；不兼容配置自动沿用主机传输，可用 `--gpu-enhancement 0` 对照。性能口径、画质差异和限制见 [RIFE 显存衔接报告](GPU_RIFE_CHAIN_2026-10-25.md)。
- 选片旁增加“打开目录”，输出路径旁增加目录按钮，支持选中已有文件及打开待生成输出的父目录；通过 Unicode Shell API 处理中文、空格、逗号等路径。
- 直接替换窗口和 EXE 图标为双色扁平播放标记，提供 SVG 源及独立栅格化的 16–256 px ICO。
- SDR NR 的灰度、运动矢量展开/验证与修正量历史迁移到 CUDA，NVOFA 继续负责光流估计，NR 通过共享显存接收运动与交还输出。RTX 5070 Ti 上同一 310.9.16 DLL 的 1080p NR 50.39→117.33 源 fps，4K NR 13.79→35.29；八组输出共 2,130 帧逐像素一致。HDR/旧宿主保留兼容路径，详见 [GPU 管线报告](GPU_TEMPORAL_2026-10-25.md)。
- 完成 310.8.0/310.9.16 交替三轮对照：独立 NR 的新版更快，原 CPU 管线中多数完整组合收益有限；版本差异与管线改进分别记录，见 [NR 版本测试](NR_VERSION_BENCHMARK_2026-10-25.md)。
- NR 版本选择器递归扫描发布目录中的 DLL，显示文件版本与路径标签；支持刷新、稳定路径保存、文件缺失提示，GUI/CLI/autorun 使用所选路径。打包脚本自动携带所有 NR 版本，并提供 ZIP 导入工具。
- 保留默认 310.8.0.0，导入 310.9.16.0 原版及社区版。RTX 5070 Ti 已验证原版短片处理及旧版→新版→旧版同进程切换；未验证其他显卡上的社区版。
- RIFE 复用帧对图像张量与上传缓存，移除兼容路径额外的像素转换推理会话，延迟分配主机帧；静帧/切镜/尾帧共享只读 NV12。GPU 与带滤镜路径可复用同尺寸网络；同尺寸输出不再触发多余缩放和主机回退。
- 14 组组合短片矩阵中，兼容路径典型提升约 6–11%；已有 GPU 直通路径基本持平。像素、音频、取消重启、双遍和真实 NR 切换通过，口径与限制见 [验证记录](PERFORMANCE_NR_VERSIONS_2026-10-25.md)。

## 2026-10-18

- 移除多余的 `runtime/fruc-ffmpeg`，统一 FFmpeg 依赖到 runtime，修复进程内 NVIDIA Vulkan 光流设备绑定并优先线性图像；保留 16 位 HDR。
- 接入 RIFE Lite/Full、模型 SHA256 校验、界面/CLI 选择与 SDR 守卫；模型直接放 runtime，不提交权重和测试产物。
- 增加 NVDEC → CUDA RIFE → NVENC 显存管线与 NR/RTX Video 组合的生成帧直编，复用单一模型会话并完善取消、音轨、尾帧和资源释放。1080p 4× RIFE Lite 83.25→116.88 输出 fps；NR+SR+RIFE 20.53→28.17，边界与画质见验证报告。
- Release、真实 GPU/光流/HDR、RIFE/组合管线、取消重启、桌面 UI 与自包含包烟测通过。修正旧烟测输出到 `.qa`，原始证据不进入 Git。

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$FfmpegDirectory = '',
    # MSIX 打包需要固定、可重复使用的负载目录；默认行为（时间戳目录 + ZIP）不变。
    [string]$OutputName = '',
    [switch]$NoZip,
    [switch]$ReuseOutputDirectory,
    # MSIX 的 VS 本地部署允许先用通用 FFmpeg 出包调试；正式发布必须严格校验。
    [switch]$AllowGenericFfmpeg,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Dlss5Demo.App\Dlss5Demo.App.csproj'
$runtimeSource = Join-Path $repoRoot 'runtime'
$nativeNames = @('dlssnr_host_v2.dll', 'vsr_host.dll', 'nvngx_vsr.dll', 'driver_hdr_host.dll')
$gpuEncoder = Join-Path $runtimeSource 'vsr_encoder.dll'
if (Test-Path -LiteralPath $gpuEncoder -PathType Leaf) { $nativeNames += 'vsr_encoder.dll' }

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少打包文件：$Path"
    }
}

# MSIX/APPX 签名时 AppxSip 会为包内每个 PE 生成代码完整性目录；若某个第三方 DLL
# 的 Authenticode 证书目录被裁剪（指向文件末尾之外），signtool 会报
# 0x800700C1 / ERROR_BAD_EXE_FORMAT 并拒绝签名，且不会指出是哪个文件。
# 这里只清除「已损坏」的 8 字节 Security Directory 目录项，等价于把该 DLL 视为
# 未签名；合法签名的文件一律不动，且只修改发布负载副本，runtime\ 源文件不变。
function Clear-BadPeCertificateDirectory([string]$Path) {
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        if ($stream.Length -lt 0x100) { return $false }
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -le 0 -or ($peOffset + 24) -gt $stream.Length) { return $false }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $false }
        # Optional Header: PE32 的数据目录从 +96 开始，PE32+ 从 +112 开始。
        $stream.Position = $peOffset + 24
        $magic = $reader.ReadUInt16()
        if ($magic -eq 0x10B) { $directoryBase = $peOffset + 24 + 96 }
        elseif ($magic -eq 0x20B) { $directoryBase = $peOffset + 24 + 112 }
        else { return $false }
        # Security Directory 是第 4 项（索引 4），值是文件偏移而非 RVA。
        $securityEntry = $directoryBase + 4 * 8
        if (($securityEntry + 8) -gt $stream.Length) { return $false }
        $stream.Position = $securityEntry
        $certOffset = [int64]$reader.ReadUInt32()
        $certSize = [int64]$reader.ReadUInt32()
        if ($certOffset -eq 0 -and $certSize -eq 0) { return $false }
        $bad = ($certOffset -eq 0) -or ($certSize -eq 0) -or
               (($certOffset % 8) -ne 0) -or ($certOffset -lt ($securityEntry + 8)) -or
               (($certOffset + $certSize) -gt $stream.Length)
        if (-not $bad -and $certSize -ge 8 -and ($certOffset + 8) -le $stream.Length) {
            $stream.Position = $certOffset
            $dwLength = [int64]$reader.ReadUInt32()
            if ($dwLength -lt 8 -or $dwLength -gt $certSize) { $bad = $true }
        }
        if (-not $bad) { return $false }
        $stream.Position = $securityEntry
        $stream.Write([byte[]]@(0, 0, 0, 0, 0, 0, 0, 0), 0, 8)
        return $true
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Repair-MalformedPeCertificates([string]$Root) {
    $count = 0
    $extensions = @('.dll', '.exe', '.sys', '.ocx')
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File)) {
        if ($extensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
        if (Clear-BadPeCertificateDirectory $file.FullName) {
            $count++
            Write-Host "[package] 清除损坏的 PE 证书目录：$($file.FullName.Substring($Root.Length + 1))"
        }
    }
    if ($count -gt 0) {
        Write-Host "[package] 共修复 $count 个 PE 文件（仅负载副本，runtime\ 源文件不变）。"
    }
    return $count
}

Assert-File $project
foreach ($name in $nativeNames) { Assert-File (Join-Path $runtimeSource $name) }
$nrVariants = @(Get-ChildItem -LiteralPath $runtimeSource -Filter 'nvngx_dlssnr*.dll' -File -Recurse)
if ($nrVariants.Count -eq 0) { throw 'runtime 下至少需要一个 nvngx_dlssnr*.dll。' }
$rifeNames = @('rife_v4.25_lite_v2.onnx', 'rife_v4.25_v2.onnx')
$rifeHashes = @('610b5de57cdcfbcce9914c23e60a1cd357779a6f9582a1bcfcb035f8eb38509b', '65c57a5e4abb17ad67faf35054291ac53affab0506d509d2e66698f6ecd75584')
for ($i = 0; $i -lt $rifeNames.Count; $i++) {
    $model = Join-Path $runtimeSource $rifeNames[$i]
    Assert-File $model
    if ((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant() -ne $rifeHashes[$i]) { throw "RIFE 模型校验失败：$model" }
}
$nvidiaLicense = Join-Path $repoRoot 'docs\licenses\NVIDIA_RTX_Video_SDK_License.pdf'
Assert-File $nvidiaLicense
$opticalFlowLicense = Join-Path $repoRoot 'docs\licenses\NVIDIA-Optical-Flow-Headers-LICENSE.txt'
Assert-File $opticalFlowLicense

# Resolve FFmpeg before creating output. Explicit paths take precedence over PATH.
if ([string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
    $configured = [Environment]::GetEnvironmentVariable('DLSS5_FFMPEG')
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        $command = Get-Command $configured -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $command) { throw "DLSS5_FFMPEG 指向的程序不存在：$configured" }
        $FfmpegDirectory = Split-Path -Parent $command.Source
    } elseif (Test-Path -LiteralPath (Join-Path $runtimeSource 'ffmpeg.exe') -PathType Leaf) {
        $FfmpegDirectory = $runtimeSource
    } else {
        $command = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) { $FfmpegDirectory = Split-Path -Parent $command.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
    throw '找不到 FFmpeg；请指定 -FfmpegDirectory，例如 G:\Tool\ffmpeg\bin。'
}
$FfmpegDirectory = (Resolve-Path -LiteralPath $FfmpegDirectory).Path
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) { Assert-File (Join-Path $FfmpegDirectory $name) }
$filters = & (Join-Path $FfmpegDirectory 'ffmpeg.exe') -hide_banner -filters 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $filters -notmatch '\bfruc_vulkan\b') {
    if ($AllowGenericFfmpeg) {
        Write-Warning 'FFmpeg 未包含 fruc_vulkan：此包仅供本地部署调试，补帧功能不可用。正式发布请使用支持 NVIDIA Vulkan 光流的 master shared 构建。'
    } else {
        throw '打包的 FFmpeg 必须包含 fruc_vulkan，请使用支持 NVIDIA Vulkan 光流的 master shared 构建。'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([string]::IsNullOrWhiteSpace($OutputName)) {
    $packageName = 'Dlss5VideoEnc-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
} elseif ($OutputName -match '[\\/]') {
    throw '-OutputName 只能是目录名，不能包含路径分隔符。'
} else {
    $packageName = $OutputName
}
$packageDirectory = Join-Path $OutputRoot $packageName
$archivePath = Join-Path $OutputRoot ($packageName + '.zip')
if ($ReuseOutputDirectory) {
    # 复用目录会清空内容，限定在 artifacts\msix 专用产物目录，避免误删 dist 发布物。
    # 用纯字符串操作去掉尾部分隔符，避免 Windows PowerShell 5.1 对
    # String.TrimEnd(params char[]) 的 string -> char 重载解析差异。
    $normalizedOutputRoot = $OutputRoot -replace '[\\/]+$', ''
    if (-not $normalizedOutputRoot.EndsWith('artifacts\msix', [StringComparison]::OrdinalIgnoreCase)) {
        throw '-ReuseOutputDirectory 仅允许 -OutputRoot 指向 artifacts\msix。'
    }
    if (Test-Path -LiteralPath $packageDirectory -PathType Container) {
        # 直接删除整个目录再重建：Get-ChildItem | Remove-Item 的管道绑定在部分
        # PowerShell 版本上会因 FileInfo/DirectoryInfo 无 Path 属性而失败。
        Remove-Item -LiteralPath $packageDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) { Remove-Item -LiteralPath $archivePath -Force }
} else {
    # Every run gets a new directory. Existing releases and source files are never removed.
    if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath $archivePath)) {
        throw "输出已存在，请重试：$packageDirectory"
    }
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
}

Write-Host '[package] 构建并发布 Windows x64 应用（自带 .NET 运行时）…'
$publishArgs = @(
    'publish', $project, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    '-p:UseSharedCompilation=false', '-o', $packageDirectory
)
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）；没有生成 ZIP。" }
Assert-File (Join-Path $packageDirectory 'Dlss5VideoEnc.exe')

$runtimeDestination = Join-Path $packageDirectory 'runtime'
$licenseDestination = Join-Path $packageDirectory 'licenses'
New-Item -ItemType Directory -Path $runtimeDestination, $licenseDestination -Force | Out-Null
foreach ($name in $nativeNames) {
    Copy-Item -LiteralPath (Join-Path $runtimeSource $name) -Destination $runtimeDestination
}
foreach ($name in $rifeNames) { Copy-Item -LiteralPath (Join-Path $runtimeSource $name) -Destination $runtimeDestination }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\licenses\RIFE-MIT-LICENSE.txt') -Destination $licenseDestination
# Mirror every installed NR variant; no version/GPU-series list is compiled into the package.
foreach ($dll in $nrVariants) {
    # Windows PowerShell 5.1 的 .NET Framework 没有 Path.GetRelativePath；
    # 用字符串截取 + 去掉前导分隔符，避免在 5.1 下做 string -> char[] 的重载解析。
    $relative = $dll.FullName.Substring($runtimeSource.Length) -replace '^[\\/]+', ''
    $destination = Join-Path $runtimeDestination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $dll.FullName -Destination $destination -Force
}
$nrReadme = Join-Path $runtimeSource 'NR_OPTIONS_README.txt'
if (Test-Path -LiteralPath $nrReadme) {
    Copy-Item -LiteralPath $nrReadme -Destination $runtimeDestination
}
# Shared FFmpeg builds need their matching DLLs beside ffmpeg.exe/ffprobe.exe.
foreach ($pattern in @('avcodec-*.dll', 'avdevice-*.dll', 'avfilter-*.dll', 'avformat-*.dll', 'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll')) {
    $libraries = @(Get-ChildItem -LiteralPath $FfmpegDirectory -Filter $pattern -File)
    if ($libraries.Count -ne 1) { throw "FFmpeg 库不唯一或缺失：$pattern" }
    $libraries | Copy-Item -Destination $packageDirectory -Force
}
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory $name) -Destination $packageDirectory
}
Copy-Item -LiteralPath $nvidiaLicense -Destination $licenseDestination
Copy-Item -LiteralPath $opticalFlowLicense -Destination $licenseDestination
Copy-Item -LiteralPath (Join-Path $repoRoot 'native\nr_host\LICENSE') -Destination (Join-Path $licenseDestination 'DLSS5Tool-MIT-LICENSE.txt')
foreach ($ffmpegSource in @((Join-Path $FfmpegDirectory 'SOURCE.txt'), (Join-Path (Split-Path -Parent $FfmpegDirectory) 'SOURCE.txt'))) {
    if (Test-Path -LiteralPath $ffmpegSource -PathType Leaf) {
        Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $packageDirectory 'FFmpeg_SOURCE.txt')
        break
    }
}
foreach ($candidate in @(
    (Join-Path $FfmpegDirectory 'LICENSE.txt'),
    (Join-Path (Split-Path -Parent $FfmpegDirectory) 'LICENSE.txt')
)) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $licenseDestination 'FFmpeg_LICENSE.txt')
        break
    }
}

# 必须在生成 SHA256SUMS.txt / PACKAGE_NOTES.txt 之前完成：清单哈希要反映修复后的文件。
# 仅影响 MSIX 签名可用性；ZIP 解压运行完全不受影响。
Repair-MalformedPeCertificates $packageDirectory | Out-Null

# Verify that the copied tools can load their dependencies before creating the ZIP.
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    & (Join-Path $packageDirectory $name) -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "打包后的 $name 无法运行（$LASTEXITCODE）；没有生成 ZIP。" }
}

$notes = @'
Dlss5VideoEnc

解压后启动 Dlss5VideoEnc.exe。请保留整个目录结构。
应用已包含 .NET、FFmpeg、DLSS NR 和 RTX Video VSR 运行文件。
RTX Video 2x/4x 需要兼容的 NVIDIA GPU 与驱动。
解码默认优先 NVDEC，首帧初始化失败时自动软件回退；也可选择强制 NVDEC 或 CPU。
NVDEC 使用驱动中的 CUDA/解码组件，不额外打包 CUDA Toolkit；兼容的 SDR RIFE + NVENC 组合可通过共享显存衔接 NR/RTX Video，避免中间整帧回读和重复上传。NR 需要启用光流和 GPU 历史处理；CPU 滤镜等不适用条件保留兼容路径。
SDR NR 的光流前后处理和历史累积优先留在显存，使用 NVOFA 专用光流引擎并与 NR 共享 GPU 缓冲；不支持时自动使用兼容路径。
补帧支持 NVIDIA Vulkan 光流和 RIFE 4.25 Lite/4.25，倍率 2x/4x/6x。RIFE 仅支持 SDR，需要 Windows 11 24H2+ 与 Windows ML NVIDIA GPU 提供程序；首次使用可能下载提供程序。
补帧在 NR/RTX Video 之后运行，HDR 颜色使用 16-bit 路径；进度与预览计数源帧。
SDR 强制 HDR10 使用驱动 RTX Video HDR 在 Vulkan 补帧后映射为 BT.2020/PQ 并进行 10-bit 编码；RIFE 保持 SDR，跳过该映射。HDR 源保留原路径。
在「NR 版本」中选择发布包包含的 DLL。后续可将 nvngx_dlssnr.dll 放入 runtime\任意版本名称\，点击刷新即可选择，无需更新程序。目录名可标注适用显卡；DLL 必须匹配 RTX 代际。
「NR 模型预设」默认 A 旧模型，可选自动和 B 轻量模型。310.9.16 的自动档使用 B；A 保留旧模型效果。310.8 仅有旧模型。旧配置自动档首次升级迁移为 A；之后手选自动/B 仍可保存。预设下次开始队列时生效；CLI/autorun 使用 --nr-preset auto|a|b，未指定时使用 A。
「自动」保留 DLSS5_NR_RUNTIME_DLL、mods\nvngx_dlssnr.dll、DLSS5_GPU_SERIES 与默认 DLL 的原有优先级。明确选择的版本优先于自动策略。
选择视频旁的“打开目录”定位源视频；输出路径旁的目录按钮定位产物，未生成时打开输出目录。
AMD/Intel 的 OpenDLSS-NR 后端当前未接入，AMD/Intel 仅用于 FFmpeg AMF/QSV 编码。
NVIDIA 组件仍遵循 licenses\NVIDIA_RTX_Video_SDK_License.pdf 中的许可。
本应用使用 NVIDIA RTX Video SDK。第三方文件不受本项目源码许可覆盖。
FFmpeg 来源和完整编译配置记录在 FFmpeg_VERSION.txt 中。
'@
[IO.File]::WriteAllText((Join-Path $packageDirectory 'PACKAGE_NOTES.txt'), $notes + [Environment]::NewLine, [Text.UTF8Encoding]::new($true))
& (Join-Path $packageDirectory 'ffmpeg.exe') -version |
    Set-Content -LiteralPath (Join-Path $packageDirectory 'FFmpeg_VERSION.txt') -Encoding UTF8

$manifest = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($packageDirectory.Length + 1).Replace('\', '/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
})
[IO.File]::WriteAllLines((Join-Path $packageDirectory 'SHA256SUMS.txt'), $manifest, [Text.UTF8Encoding]::new($false))

if (-not $NoZip) {
    Write-Host '[package] 压缩发布目录…'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "[package] ZIP：$archivePath"
}
Write-Host "[package] 发布目录：$packageDirectory"
Write-Host "[package] PAYLOAD=$packageDirectory"

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$FfmpegDirectory = '',
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Dlss5Demo.App\Dlss5Demo.App.csproj'
$runtimeSource = Join-Path $repoRoot 'runtime'
$nativeNames = @('dlssnr_host_v2.dll', 'nvngx_dlssnr.dll', 'vsr_host.dll', 'nvngx_vsr.dll')

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少打包文件：$Path"
    }
}

Assert-File $project
foreach ($name in $nativeNames) { Assert-File (Join-Path $runtimeSource $name) }
$nvidiaLicense = Join-Path $repoRoot 'docs\licenses\NVIDIA_RTX_Video_SDK_License.pdf'
Assert-File $nvidiaLicense

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

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$packageName = 'Dlss5VideoEnc-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$packageDirectory = Join-Path $OutputRoot $packageName
$archivePath = Join-Path $OutputRoot ($packageName + '.zip')
# Every run gets a new directory. Existing releases and source files are never removed.
if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath $archivePath)) {
    throw "输出已存在，请重试：$packageDirectory"
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

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
# Shared FFmpeg builds need their matching DLLs beside ffmpeg.exe/ffprobe.exe.
Get-ChildItem -LiteralPath $FfmpegDirectory -Filter '*.dll' -File |
    Copy-Item -Destination $packageDirectory -Force
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory $name) -Destination $packageDirectory
}
Copy-Item -LiteralPath $nvidiaLicense -Destination $licenseDestination
foreach ($candidate in @(
    (Join-Path $FfmpegDirectory 'LICENSE.txt'),
    (Join-Path (Split-Path -Parent $FfmpegDirectory) 'LICENSE.txt')
)) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $licenseDestination 'FFmpeg_LICENSE.txt')
        break
    }
}

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

Write-Host '[package] 压缩发布目录…'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "[package] 发布目录：$packageDirectory"
Write-Host "[package] ZIP：$archivePath"

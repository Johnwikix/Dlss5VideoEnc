[CmdletBinding()]
param([string]$ArchivePath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$name = 'ffmpeg-N-127579-g5cb9fcd211-win64-gpl-shared'
$hash = '345ab5b89c11583c1e309a3d47fce5d44dabe8f7acf81cff3fc974485a500070'
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-16-13-04/$name.zip"
$cache = Join-Path $repo 'build\fruc\dependency'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $ArchivePath = Join-Path $cache ($name + '.zip')
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        Invoke-WebRequest $url -OutFile $ArchivePath
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
    throw "FFmpeg SHA256 不匹配：$ArchivePath"
}
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $cache -Force
$source = Join-Path $cache $name
$destination = Join-Path $repo 'runtime\fruc-ffmpeg'
New-Item -ItemType Directory -Path (Join-Path $destination 'bin') -Force | Out-Null
foreach ($file in @('ffmpeg.exe', 'ffprobe.exe', '*.dll')) {
    Get-ChildItem -LiteralPath (Join-Path $source 'bin') -Filter $file -File |
        Copy-Item -Destination (Join-Path $destination 'bin') -Force
}
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination $destination -Force
$filters = & (Join-Path $destination 'bin\ffmpeg.exe') -hide_banner -filters 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $filters -notmatch '\bfruc_vulkan\b') { throw 'FFmpeg 缺少 fruc_vulkan。' }
& (Join-Path $destination 'bin\ffmpeg.exe') -version | Set-Content (Join-Path $destination 'FFmpeg_VERSION.txt') -Encoding UTF8
@("Source: $url", "Archive SHA256: $hash") | Set-Content (Join-Path $destination 'SOURCE.txt') -Encoding UTF8
Write-Host "已准备支持 NVIDIA Vulkan 补帧的 FFmpeg：$destination"

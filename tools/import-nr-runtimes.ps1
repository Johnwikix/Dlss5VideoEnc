[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$RuntimeDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nrRoot = [IO.Path]::GetFullPath((Join-Path $RuntimeDirectory 'nr'))
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    $count = 0
    foreach ($entry in $archive.Entries) {
        if ([IO.Path]::GetFileName($entry.FullName) -ne 'nvngx_dlssnr.dll') { continue }
        $destination = [IO.Path]::GetFullPath((Join-Path $nrRoot $entry.FullName))
        if (-not $destination.StartsWith($nrRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "压缩包路径超出 NR 目录：$($entry.FullName)"
        }
        if (Test-Path -LiteralPath $destination) { throw "版本已存在，请先改名保留旧版本：$destination" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
        Write-Host "NR $version : $destination"
        $count++
    }
    if ($count -eq 0) { throw '压缩包中未找到 nvngx_dlssnr.dll。' }
    $readme = $archive.GetEntry('README.txt')
    if ($null -ne $readme) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($readme, (Join-Path $RuntimeDirectory 'NR_OPTIONS_README.txt'), $true)
    }
    Write-Host "已导入 $count 个 NR 版本；原默认 DLL 保留。"
} finally { $archive.Dispose() }

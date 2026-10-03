[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Archive,
    [string]$Destination = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repoRoot "third_party\RTX_Video_SDK"
}
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "SDK ZIP 不存在：$Archive"
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("dlss5-rtx-video-sdk-" + [guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $tempRoot "extract"
New-Item -ItemType Directory -Force $extractRoot | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $header = Get-ChildItem -LiteralPath $extractRoot -Recurse -Filter nvsdk_ngx_helpers_vsr.h -File | Select-Object -First 1
    if ($null -eq $header) {
        throw "ZIP 中没有 nvsdk_ngx_helpers_vsr.h；请确认下载的是 RTX Video SDK 1.1 Windows SDK。"
    }
    $sdkRoot = $header.Directory.Parent.FullName
    $required = @(
        (Join-Path $sdkRoot "include\nvsdk_ngx_helpers_vsr.h"),
        (Join-Path $sdkRoot "lib\Windows\x64\nvsdk_ngx_s.lib"),
        (Join-Path $sdkRoot "bin\Windows\x64\rel\nvngx_vsr.dll")
    )
    $missing = $required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }
    if ($missing.Count -gt 0) {
        throw "SDK 目录结构不完整，缺少：`n$($missing -join "`n")"
    }

    $destinationPath = [IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $destinationPath) {
        throw "目标目录已存在，为避免覆盖请先移走或删除：$destinationPath"
    }
    New-Item -ItemType Directory -Force $destinationPath | Out-Null
    Copy-Item -LiteralPath (Join-Path $sdkRoot "include") -Destination $destinationPath -Recurse
    Copy-Item -LiteralPath (Join-Path $sdkRoot "lib") -Destination $destinationPath -Recurse
    Copy-Item -LiteralPath (Join-Path $sdkRoot "bin") -Destination $destinationPath -Recurse

    $runtime = Join-Path $destinationPath "bin\Windows\x64\rel\nvngx_vsr.dll"
    $signature = Get-AuthenticodeSignature -LiteralPath $runtime
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtime).Hash
    Write-Host "RTX Video SDK 已安装到：$destinationPath"
    Write-Host "nvngx_vsr.dll SHA256：$hash"
    Write-Host "Authenticode：$($signature.Status) / $($signature.SignerCertificate.Subject)"
    if ($signature.Status -notin @('Valid', 'Unknown')) {
        throw "nvngx_vsr.dll 的 Authenticode 签名校验失败：$($signature.Status)"
    }
    Write-Host "下一步：native\vsr_host\build.bat"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

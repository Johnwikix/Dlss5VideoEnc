[CmdletBinding()]
param([string]$SourceDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repo 'runtime'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$revision = 'f7bf1c91e94ef516900f68456528fa781e2e7174'
$models = @{
    'rife_v4.25_lite_v2.onnx' = '610b5de57cdcfbcce9914c23e60a1cd357779a6f9582a1bcfcb035f8eb38509b'
    'rife_v4.25_v2.onnx' = '65c57a5e4abb17ad67faf35054291ac53affab0506d509d2e66698f6ecd75584'
}
foreach ($name in $models.Keys) {
    $path = Join-Path $destination $name
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -eq $models[$name]) { continue }
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        if ($SourceDirectory) { Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination $temporary }
        else { Invoke-WebRequest "https://huggingface.co/notaneimu/onnx-image-models/resolve/$revision/$name" -OutFile $temporary }
        if ((Get-FileHash -LiteralPath $temporary).Hash.ToLowerInvariant() -ne $models[$name]) { throw "RIFE SHA256 不匹配：$name" }
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Host "RIFE 4.25 Lite / 4.25 已校验：$destination"

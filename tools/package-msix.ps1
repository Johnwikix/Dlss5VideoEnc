[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    # MSIX Identity Version，必须与清单一致（脚本会写入 AppxManifest.xml 与文件名）。
    [string]$Version = '0.1.0.0',
    [string]$FfmpegDirectory = '',
    [string]$OutputRoot = '',
    # 手动指定 makeappx.exe；默认从 NuGet 缓存 / Windows SDK 中探测。
    [string]$MakeAppx = '',
    # 可选签名：证书 Subject 必须与 Package.appxmanifest 的 Publisher 一致。
    [string]$CertificatePath = '',
    [string]$CertificatePassword = '',
    # 显式创建本地测试证书（写入 Cert:\CurrentUser\My 与 artifacts\msix）。
    [switch]$CreateTestCertificate,
    # 复用 artifacts\msix\payload 上次产物，跳过完整发布。
    [switch]$SkipStage,
    # 指定其它已舞台化的负载目录（例如单独验证修复后的负载）。
    [string]$PayloadDirectory = '',
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$packaging = Join-Path $repoRoot 'packaging\Dlss5VideoEnc.Msix'
$manifestSource = Join-Path $packaging 'Package.appxmanifest'
$assetsSource = Join-Path $packaging 'Assets'
$stageRoot = Join-Path $repoRoot 'artifacts\msix'
$payloadDir = Join-Path $stageRoot 'payload'
if (-not [string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    $payloadDir = [IO.Path]::GetFullPath($PayloadDirectory)
}
$layoutDir = Join-Path $stageRoot 'layout'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

# 与 wapproj 的 EntryPayloadPrefix / AssemblyName 保持一致。
$payloadPrefix = 'Dlss5Demo.App'
$executableName = 'Dlss5VideoEnc.exe'
$publisher = 'CN=CF259542-70C0-4852-9523-066A80212473'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "-Version 必须是四段版本号（如 0.1.0.0）：$Version" }
foreach ($required in @($manifestSource, $assetsSource)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少打包资源：$required" }
}

function Resolve-SdkTool([string]$Name, [string]$ExplicitPath) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) { throw "工具不存在：$ExplicitPath" }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }

    $roots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { $roots += $env:NUGET_PACKAGES }
    $roots += @(
        (Join-Path $env:USERPROFILE '.nuget\packages'),
        'G:\.nuget\packages',
        'I:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'
    )
    $packageName = 'microsoft.windows.sdk.buildtools'
    foreach ($root in ($roots | Select-Object -Unique)) {
        $packageRoot = Join-Path $root $packageName
        if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { continue }
        foreach ($versionDir in @(Get-ChildItem -LiteralPath $packageRoot -Directory | Sort-Object Name -Descending)) {
            $binDir = Join-Path $versionDir.FullName 'bin'
            if (-not (Test-Path -LiteralPath $binDir -PathType Container)) { continue }
            $found = @(Get-ChildItem -LiteralPath $binDir -Filter $Name -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1)
            if ($found.Count -gt 0) { return $found[0].FullName }
        }
    }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits -PathType Container) {
        $found = @(Get-ChildItem -LiteralPath $kits -Filter $Name -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } | Select-Object -First 1)
        if ($found.Count -gt 0) { return $found[0].FullName }
    }
    throw "找不到 $Name；请安装 Microsoft.Windows.SDK.BuildTools 或 Windows SDK，或用 -MakeAppx 指定路径。"
}

# 1. 完整发布负载：与 ZIP 发布共用 tools/package.ps1，包含 runtime、FFmpeg 与许可证。
if (-not $SkipStage) {
    $stageArgs = @(
        '-Configuration', $Configuration,
        '-OutputRoot', $stageRoot,
        '-OutputName', 'payload',
        '-ReuseOutputDirectory',
        '-NoZip'
    )
    if (-not [string]::IsNullOrWhiteSpace($FfmpegDirectory)) { $stageArgs += @('-FfmpegDirectory', $FfmpegDirectory) }
    if ($NoRestore) { $stageArgs += '-NoRestore' }
    Write-Host '[msix] 生成完整发布负载（package.ps1）…'
    & (Join-Path $repoRoot 'tools\package.ps1') @stageArgs
}
if (-not (Test-Path -LiteralPath (Join-Path $payloadDir $executableName) -PathType Leaf)) {
    throw "负载不完整，缺少 $executableName：$payloadDir`n请先运行 tools\package.ps1 或去掉 -SkipStage。"
}

# 2. 组装 MakeAppx 布局。负载放在以入口工程命名的子目录，与清单 Executable 对齐。
if (Test-Path -LiteralPath $layoutDir -PathType Container) {
    Get-ChildItem -LiteralPath $layoutDir -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $layoutDir -Force | Out-Null
}
$layoutPayload = Join-Path $layoutDir $payloadPrefix
$layoutAssets = Join-Path $layoutDir 'Assets'
New-Item -ItemType Directory -Path $layoutPayload, $layoutAssets -Force | Out-Null
Get-ChildItem -LiteralPath $payloadDir -Force | Copy-Item -Destination $layoutPayload -Recurse -Force
$icons = @(Get-ChildItem -LiteralPath $assetsSource -Filter '*.png' -File)
if ($icons.Count -ne 6) { throw "图标资产应为 6 个 PNG，实际 $($icons.Count) 个：$assetsSource" }
$icons | Copy-Item -Destination $layoutAssets -Force

# 3. 清单：MakeAppx 不跑 WAP 的清单生成与 makepri，因此把占位符展开为实际值。
[xml]$manifest = Get-Content -LiteralPath $manifestSource -Raw -Encoding UTF8
$identity = $manifest.Package.Identity
$identity.SetAttribute('Version', $Version)
$identity.SetAttribute('ProcessorArchitecture', 'x64')
if ($identity.GetAttribute('Publisher') -ne $publisher) {
    throw "清单 Publisher 与脚本预期不一致：$($identity.GetAttribute('Publisher'))"
}
$application = @($manifest.Package.Applications.Application)[0]
$application.SetAttribute('Executable', "$payloadPrefix\$executableName")
$application.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
# x-generate 由 makepri 解析；直接 MakeAppx 打包需写成具体语言。
foreach ($resource in @($manifest.Package.Resources.Resource)) {
    if ($resource.GetAttribute('Language') -eq 'x-generate') { $resource.SetAttribute('Language', 'zh-CN') }
}
$manifestPath = Join-Path $layoutDir 'AppxManifest.xml'
$manifest.Save($manifestPath)

# 4. 打包。
$makeAppxPath = Resolve-SdkTool 'makeappx.exe' $MakeAppx
$msixPath = Join-Path $OutputRoot "Dlss5VideoEnc_$($Version)_x64.msix"
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Write-Host "[msix] MakeAppx: $makeAppxPath"
& $makeAppxPath pack /d $layoutDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx 失败（$LASTEXITCODE）；未生成 MSIX。" }

# 5. 可选签名。未签名包只能按松散布局注册，可安装分发应使用签名包。
if ($CreateTestCertificate) {
    $now = Get-Date
    # 本机证书存储里可能有大量同 Subject 的测试证书（含已过期/无私钥的），
    # 直接取第一个会随机挑到过期证书并让签名失败。
    $certificate = @(Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $publisher -and $_.HasPrivateKey -and
            $_.NotBefore -le $now -and $_.NotAfter -gt $now
        } | Sort-Object NotAfter -Descending | Select-Object -First 1)[0]
    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher `
            -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
            -FriendlyName 'Dlss5VideoEnc MSIX test signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $pfxPath = Join-Path $stageRoot 'Dlss5VideoEnc.test.pfx'
    $cerPath = Join-Path $stageRoot 'Dlss5VideoEnc.test.cer'
    $passwordValue = 'msix-test'
    $secure = ConvertTo-SecureString -String $passwordValue -Force -AsPlainText
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $secure | Out-Null
    Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
    $CertificatePath = $pfxPath
    $CertificatePassword = $passwordValue
    $importHint = "  Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\CurrentUser\Root"
    Write-Warning "测试证书已生成：$cerPath`n安装前需把该证书导入「受信任的根证书颁发机构」，例如：`n$importHint"
}
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $signToolPath = Resolve-SdkTool 'signtool.exe' ''
    Write-Host '[msix] 签名…'
    & $signToolPath sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool 失败（$LASTEXITCODE）；MSIX 已生成但未签名。" }
}

$item = Get-Item -LiteralPath $msixPath
Write-Host "[msix] 布局目录：$layoutDir"
Write-Host "[msix] MSIX：$($item.FullName)"
Write-Host ("[msix] 大小：{0:N1} MB" -f ($item.Length / 1MB))
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Host '[msix] 当前为未签名包。安装需签名证书，或在开发者模式下用 Add-AppxPackage -Register 注册布局目录中的 AppxManifest.xml。'
}

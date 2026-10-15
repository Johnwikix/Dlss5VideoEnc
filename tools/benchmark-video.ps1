[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliAssembly,
    [Parameter(Mandatory)][string]$InputVideo,
    [Parameter(Mandatory)][string]$OutputVideo,
    [int]$Frames = 180,
    [ValidateSet(1,2,4)][int]$Scale = 1,
    [bool]$NrFirst = $true,
    [bool]$NrQueue = $true,
    [bool]$GpuVsrEncoding = $true,
    [bool]$Preview = $true,
    [switch]$LegacyCleanup
)

$ErrorActionPreference = 'Stop'
$taskStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
$taskStart.StandardOutputEncoding = [Text.Encoding]::UTF8
$taskStart.Environment['DLSS5_PROFILE'] = '1'
foreach ($argument in @((Resolve-Path -LiteralPath $CliAssembly).Path, (Resolve-Path -LiteralPath $InputVideo).Path,
    [IO.Path]::GetFullPath($OutputVideo), '--frames', "$Frames", '--sr', "$Scale", '--preview', [int]$Preview)) {
    $taskStart.ArgumentList.Add($argument)
}
$taskOutputLog = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($OutputVideo), '.log')
if (!$LegacyCleanup) {
    foreach ($argument in @('--nr-before-sr', [int]$NrFirst, '--nr-queue', [int]$NrQueue,
        '--sr-gpu-encode', [int]$GpuVsrEncoding)) {
        $taskStart.ArgumentList.Add([string]$argument)
    }
}
$taskLog = [IO.StreamWriter]::new($taskOutputLog, $false, [Text.UTF8Encoding]::new($false))
$taskProcess = [Diagnostics.Process]::Start($taskStart)
$taskError = $taskProcess.StandardError.ReadToEndAsync()
$taskLegacyStopped = $false
try {
    while ($true) {
        $line = $taskProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(90)).GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        $taskLog.WriteLine($line)
        if ($line -match '每帧阶段耗时|完成:|进度.*100.0%') { Write-Output $line }
        # This baseline's VSR cleanup is known to block. Stop only this owned process,
        # after its final encoded file is closed and the NR feature is released.
        if ($LegacyCleanup -and $line.Contains('NGX NR 会话已释放')) {
            Start-Sleep -Milliseconds 100
            $taskProcess.Kill($true)
            $taskLegacyStopped = $true
            break
        }
    }
    $taskProcess.WaitForExit()
    if (!$taskLegacyStopped -and $taskProcess.ExitCode -ne 0) { throw $taskError.GetAwaiter().GetResult() }
} finally {
    if (!$taskProcess.HasExited) { $taskProcess.Kill($true); $taskProcess.WaitForExit() }
    $taskLog.Dispose()
    $taskProcess.Dispose()
}

[CmdletBinding()]
param(
    [string]$CliPath = '',
    [string]$FfmpegDirectory = '',
    [string]$OutputDirectory = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$CliPath) { $CliPath = Join-Path $repo 'build\fruc\cli\Dlss5Demo.Cli.dll' }
if (!$FfmpegDirectory) { $FfmpegDirectory = Join-Path $repo 'runtime\fruc-ffmpeg\bin' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ('build\fruc\capabilities-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$ffmpeg = Join-Path $FfmpegDirectory 'ffmpeg.exe'
$ffprobe = Join-Path $FfmpegDirectory 'ffprobe.exe'
$env:DLSS5_FFMPEG = $ffmpeg
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$results = @()
foreach ($size in @('3840x2160', '7680x4320')) {
    foreach ($transfer in @('sdr', 'smpte2084', 'arib-std-b67')) {
        $name = "$size-$transfer"
        $source = Join-Path $OutputDirectory ($name + '-input.mp4')
        $output = Join-Path $OutputDirectory ($name + '-2x.mp4')
        if ($transfer -eq 'sdr') {
            $filter = 'format=yuv420p'
            $pixelFormat = 'yuv420p'
        } else {
            # Smooth 10-bit values and moving chroma test more than HDR tags.
            $filter = "format=yuv420p10le,geq=lum='64+mod(X+Y+N*8,876)':cb='512+100*sin(X/200+N)':cr='512+100*cos(Y/200+N)',setparams=color_primaries=bt2020:color_trc=$($transfer):colorspace=bt2020nc:range=limited"
            $pixelFormat = 'p010le'
        }
        & $ffmpeg -hide_banner -loglevel error -n -f lavfi -i "testsrc2=size=$($size):rate=24000/1001:duration=0.25025" `
            -f lavfi -i 'sine=frequency=440:duration=0.25025' -vf $filter -c:v hevc_nvenc -preset p1 -pix_fmt $pixelFormat `
            -c:a aac -frames:v 6 -t 0.25025 $source
        if ($LASTEXITCODE -ne 0) { throw "生成测试源失败：$name" }
        & dotnet $CliPath $source $output --nr 0 --sr 1 --interpolate 1 --interpolation-multiplier 2 --frames 6 --preview 0 *> (Join-Path $OutputDirectory ($name + '.log'))
        if ($LASTEXITCODE -ne 0) { throw "补帧失败：$name，详见日志。" }
        $probe = & $ffprobe -v error -show_streams -show_format -of json $output | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw "输出探测失败：$name" }
        $video = @($probe.streams | Where-Object codec_type -EQ video)[0]
        $audio = @($probe.streams | Where-Object codec_type -EQ audio)[0]
        if ([int]$video.nb_frames -ne 12 -or $video.avg_frame_rate -ne '48000/1001' -or [Math]::Abs([double]$video.duration - 0.25025) -gt .003) {
            throw "输出时间线错误：$name"
        }
        if ($transfer -ne 'sdr' -and ($video.pix_fmt -ne 'yuv420p10le' -or $video.color_transfer -ne $transfer -or $video.color_primaries -ne 'bt2020')) {
            throw "输出 HDR 格式错误：$name"
        }
        if ([Math]::Abs([double]$audio.start_time) -gt .003 -or [Math]::Abs([double]$audio.duration - .25025) -gt .025) {
            throw "输出音频时间线错误：$name"
        }
        $result = [ordered]@{ case=$name; width=$video.width; height=$video.height; frames=$video.nb_frames;
            rate=$video.avg_frame_rate; seconds=$video.duration; pixelFormat=$video.pix_fmt; output=$output }
        $results += $result
        $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
        Write-Host "PASS $name : 12 frames @48000/1001, $($video.pix_fmt), audio preserved"
    }
}
Write-Host "验证记录：$OutputDirectory"

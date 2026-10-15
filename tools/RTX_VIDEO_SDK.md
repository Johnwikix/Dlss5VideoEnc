# NVIDIA RTX Video SDK

The RTX Video SDK and `nvngx_vsr.dll` are proprietary NVIDIA components. They are not included in this repository. Obtain the Windows SDK directly from NVIDIA, accept its license, then stage it locally:

```powershell
.\tools\stage-rtx-video-sdk.ps1 -Archive C:\path\to\rtx_video_sdk_v1.1.0.zip
.\native\vsr_host\build.bat
```

The staging script verifies the expected headers/import library/runtime layout and records the runtime Authenticode status and SHA-256. The SDK remains under the ignored `third_party\RTX_Video_SDK` directory; only the project integration code is committed.

## Optional GPU encoding transport

Build against a shared FFmpeg development distribution with matching headers, import libraries and runtime DLLs:

```powershell
$env:FFMPEG_SDK = 'G:\Tool\ffmpeg' # include/lib/bin; tested with avcodec 63, avformat 63, avutil 61
.\native\vsr_host\build.bat
```

This also builds `runtime\vsr_encoder.dll` and stages its FFmpeg dependencies. The base VSR host does not link to FFmpeg; an absent or incompatible encoder DLL falls back to CPU frame transport. Packaging includes the optional DLL when present. Use the same FFmpeg shared distribution for packaging and building this DLL.

For compatible SDR NR-first NVENC exports, D3D11 creates an NT-shared RGB texture, which D3D12 uses as the VSR output. GPU video processing converts each frame into an NV12 surface retained by libavcodec until NVENC releases it. A shared GPU fence prevents D3D12 from overwriting the RGB texture while D3D11 is still reading it. No full-resolution output frame is downloaded or re-uploaded for encoding. Requested previews alone use a staging texture. A temporary compressed NUT stream preserves packet timestamps/reordering before FFmpeg remuxes video and audio without re-encoding.

The fast path supports SDR H.264/HEVC/AV1. HDR, CPU video filters, software encoding and SR-before-NR retain the compatibility path. The tested driver's packed RGB10-to-P010 video processor produced incorrect colors, so HDR GPU transport is explicitly disabled in both Core and native code. HDR preserves the original color conversion. VSR processes one frame at a time and reuses one set of textures and staging buffers. SDR conversion uses the same BT.601 limited-range RGB-to-YUV matrix as the original FFmpeg raw-RGBA path; GPU and swscale chroma filtering/rounding may differ, so encoded pixels need not be byte-identical. Transport regressions include decoded pixel/PSNR comparison, rather than relying only on metadata.

## Packaging the application

Keep the local NVIDIA and FFmpeg files under `runtime\` and package them only into the application archive:

```powershell
.\tools\package.ps1 -FfmpegDirectory G:\Tool\ffmpeg\bin
```

The script publishes a self-contained `win-x64` application, copies the local NVIDIA runtime DLLs to the package's `runtime\` directory, copies `ffmpeg.exe`/`ffprobe.exe` and their DLLs, includes the NVIDIA license, writes SHA-256 checksums, and creates a timestamped directory and ZIP under `dist\`. The local DLLs stay ignored by Git. If the project was already restored for `win-x64`, `-NoRestore` avoids a second restore.

# NVIDIA RTX Video SDK

The RTX Video SDK and `nvngx_vsr.dll` are proprietary NVIDIA components. They are not included in this repository. Obtain the Windows SDK directly from NVIDIA, accept its license, then stage it locally:

```powershell
.\tools\stage-rtx-video-sdk.ps1 -Archive C:\path\to\rtx_video_sdk_v1.1.0.zip
.\native\vsr_host\build.bat
```

The staging script verifies the expected headers/import library/runtime layout and records the runtime Authenticode status and SHA-256. The SDK remains under the ignored `third_party\RTX_Video_SDK` directory; only the project integration code is committed.

## Packaging the application

Keep the local NVIDIA and FFmpeg files under `runtime\` and package them only into the application archive:

```powershell
.\tools\package.ps1 -FfmpegDirectory G:\Tool\ffmpeg\bin
```

The script publishes a self-contained `win-x64` application, copies the local NVIDIA runtime DLLs to the package's `runtime\` directory, copies `ffmpeg.exe`/`ffprobe.exe` and their DLLs, includes the NVIDIA license, writes SHA-256 checksums, and creates a timestamped directory and ZIP under `dist\`. The local DLLs stay ignored by Git. If the project was already restored for `win-x64`, `-NoRestore` avoids a second restore.

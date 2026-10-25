# DLSS NR host

This directory contains the maintained DLSS5Tool-derived host used by this
application, under the accompanying MIT license. It replaces the build-time
dependency on a mutable, ignored `_ref` checkout. Build with
`native/build-host.bat`; NVIDIA NGX headers/libraries remain in `third_party`.

`gpu_frames.h` adds per-slot committed D3D12 shared buffers for RGBA input,
R16G16 motion and NR output. The managed CUDA owner checks the adapter LUID
before importing them. Existing CPU process/enqueue/dequeue interfaces retain
their behavior, including RGBA16F and tiled processing.

The GPU frame contract is synchronous:

1. Configure motion guidance and call `dlssnr_gpu_frames_create` before importing
   buffers. A pending feature reconfiguration is applied here.
2. Obtain the next idle native slot and fill its color/motion buffers in CUDA.
3. Complete the CUDA stream, then call `dlssnr_gpu_frames_process`. It performs
   GPU copies, NR evaluation and output copy, and waits for its D3D12 fence.
4. CUDA may now consume the output. Finish that work before another transaction
   or shutdown. Feature reconfiguration invalidates imports; the caller must
   release them first. The application holds configuration fixed per transcode.
5. Unmap/free CUDA imports before `dlssnr_gpu_frames_close` or host shutdown.

Shared buffers enter and leave D3D12 in COMMON state. Handles belong to the
exporter and stay open until imports are released. No host memory pointer is
interpreted as a device pointer, and GPU transaction failures are not retried
through the CPU path with sentinel inputs. The shader kernels and their tests
are under `src/Dlss5Demo.Core/Cuda` and `tests/Dlss5Demo.Tests`.

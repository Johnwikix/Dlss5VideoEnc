namespace Dlss5Demo.Core;

public static partial class DlssVideoPipeline
{
    // PipelineGate 覆盖完整任务及释放；仅保留一套形状，避免并发共享绑定或换尺寸时显存叠加。
    private static RifeGpuSession? _rifeSession;
    private static (string Path, long Bytes, long Stamp, int Width, int Height, bool GpuPixels)? _rifeSessionKey;

    private static async Task<RifeGpuSession> AcquireRifeSessionAsync(TranscodeOptions options, VideoInfo video,
        int width, int height, CancellationToken ct)
    {
        bool hdr = video.IsHdr;
        var artifact = RifeGpuSession.ValidateRequest(options, width, height, hdr);
        var file = new FileInfo(artifact.Path);
        var key = (artifact.Path, file.Length, file.LastWriteTimeUtc.Ticks, width, height, CanEncodeRifeOnGpu(options));
        if (_rifeSessionKey == key && _rifeSession != null)
        {
            ct.ThrowIfCancellationRequested();
            PipelineLog.Info("RIFE GPU 已验证：复用已校验的同模型、同尺寸 GPU 会话。");
            return _rifeSession;
        }
        ResetRifeSession();
        var session = await RifeGpuSession.CreateAsync(options, width, height, hdr, ct,
            preferCudaPixels: CanEncodeRifeOnGpu(options)).ConfigureAwait(false);
        _rifeSession = session; _rifeSessionKey = key;
        return session;
    }

    private static void ResetRifeSession()
    {
        var previous = _rifeSession; _rifeSession = null; _rifeSessionKey = null;
        try { previous?.Dispose(); }
        catch (Exception error) { PipelineLog.Info("RIFE 资源释放失败：" + error.Message); }
    }

    /// <summary>嵌入方在关闭时调用；等待真实在途任务退出，再释放保留的 GPU 会话。</summary>
    public static void ReleaseGpuResources()
    {
        PipelineGate.Wait();
        try { ResetRifeSession(); }
        finally { PipelineGate.Release(); }
    }
}

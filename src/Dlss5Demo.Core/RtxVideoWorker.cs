using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Globalization;
using System.Text.Json;
using System.Text;

namespace Dlss5Demo.Core;

/// <summary>
/// Isolates the process-global NGX client used by RTX Video from the NR client.
/// Pixels stay in shared memory; the pipe transports only buffer indices/status.
/// </summary>
public sealed unsafe class RtxVideoWorker : IDisposable
{
    private static bool _hostSupportsWorker;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private byte* _memory;
    private Process? _process;
    private BinaryWriter? _commands;
    private BinaryReader? _responses;
    private Task<string>? _errors;
    private bool _disposed, _ready;
    private long _sourceBytes, _processBytes, _slotBytes;
    internal int QueueCapacity { get; private set; } = 1;
    internal int FrameCapacity { get; private set; }
    internal VsrEncoderConfiguration? Encoder { get; private set; }
    internal bool GpuEncoding => Encoder != null;
    private string? _encodedCache;
    internal byte* Source(int index) => _memory + index * _slotBytes;
    internal byte* Output(int index) => Source(index) + _sourceBytes;

    private RtxVideoWorker() { }

    internal static RtxVideoWorker Create(string host, string runtime, string log,
        int width, int height, int scale, int quality, bool hdr, int capacity,
        string workerExecutable = "", CancellationToken cancellationToken = default,
        VsrEncoderConfiguration? encoder = null)
    {
        var worker = new RtxVideoWorker();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            worker.FrameCapacity = capacity;
            worker._sourceBytes = checked((long)width * height * (hdr ? 8 : 4));
            worker._processBytes = checked(worker._sourceBytes * scale * scale);
            worker._slotBytes = worker._sourceBytes + worker._processBytes;
            var name = "Local\\Dlss5Vsr_" + Guid.NewGuid().ToString("N");
            worker._mapping = MemoryMappedFile.CreateNew(name, checked(worker._slotBytes * capacity));
            worker._view = worker._mapping.CreateViewAccessor();
            byte* pointer = null;
            worker._view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            worker._memory = pointer + worker._view.PointerOffset;

            var currentEntry = Environment.GetCommandLineArgs()[0];
            if (string.IsNullOrWhiteSpace(workerExecutable) && !_hostSupportsWorker)
            {
                workerExecutable = Path.Combine(AppContext.BaseDirectory, "Dlss5VideoEnc.exe");
                if (!File.Exists(workerExecutable))
                    throw new InvalidOperationException("Embedding Core requires VsrWorkerExecutablePath or a host handling RtxVideoWorker.TryServe.");
            }
            var executable = string.IsNullOrWhiteSpace(workerExecutable)
                ? Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate worker host.")
                : Path.GetFullPath(workerExecutable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(currentEntry);
            foreach (var argument in new[] { "--vsr-worker", name, width.ToString(CultureInfo.InvariantCulture),
                height.ToString(CultureInfo.InvariantCulture), scale.ToString(CultureInfo.InvariantCulture),
                quality.ToString(CultureInfo.InvariantCulture), hdr ? "1" : "0", capacity.ToString(CultureInfo.InvariantCulture),
                Path.GetFullPath(host), Path.GetFullPath(runtime), Path.GetFullPath(log),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            worker._process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start RTX Video worker.");
            worker._errors = worker._process.StandardError.ReadToEndAsync();
            worker._commands = new BinaryWriter(worker._process.StandardInput.BaseStream);
            worker._responses = new BinaryReader(worker._process.StandardOutput.BaseStream);
            using var stopStartup = cancellationToken.Register(worker.Abort);
            // Only initialization has a deadline. Frame waits are unblocked by cancellation/abort.
            var status = Task.Run(worker._responses.ReadInt32).WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).GetAwaiter().GetResult();
            if (status != 1) throw new InvalidOperationException("RTX Video worker initialization failed.");
            worker.QueueCapacity = worker._responses.ReadInt32();
            worker._ready = true;
            if (encoder != null)
            {
                worker._encodedCache = encoder.OutputPath;
                worker._commands.Write(-3);
                worker._commands.Write(JsonSerializer.Serialize(encoder)); worker._commands.Flush();
                var encoderStatus = Task.Run(worker._responses.ReadInt32)
                    .WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).GetAwaiter().GetResult();
                if (encoderStatus == 1)
                {
                    worker.Encoder = encoder;
                    PipelineLog.Info("VSR GPU 编码已启用: 共享纹理 → NV12 → NVENC；仅按需回读预览，压缩包通过 NUT 保留时间戳。");
                }
                else PipelineLog.Info("VSR GPU 编码不可用，回退原始帧编码路径。");
            }
            PipelineLog.Info($"RTX VSR 独立进程已就绪: {width}×{height} → {width * scale}×{height * scale}; 共享内存 {capacity} 帧");
            return worker;
        }
        catch (Exception error)
        {
            worker.Abort();
            var details = worker._errors?.IsCompletedSuccessfully == true ? worker._errors.Result : "";
            worker.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("RTX Video worker startup failed. " + details, error);
        }
    }

    internal void ProcessFrame(int slot)
    {
        EnqueueFrame(slot);
        DequeueFrame(slot, true);
    }

    internal void EnqueueFrame(int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _commands!.Write(slot); _commands.Flush();
        if (_responses!.ReadInt32() != 1) throw FrameError("submission");
    }

    internal void DequeueFrame(int slot, bool preview)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _commands!.Write(-2); _commands.Write(slot); _commands.Write(preview); _commands.Flush();
        if (_responses!.ReadInt32() != 1) throw FrameError("collection");
    }

    internal void FinishEncoding()
    {
        _commands!.Write(-4); _commands.Flush();
        if (_responses!.ReadInt32() != 1) throw FrameError("encoder drain");
    }

    private InvalidOperationException FrameError(string operation)
        => new($"RTX Video worker {operation} failed. " + (_errors?.IsCompletedSuccessfully == true ? _errors.Result : ""));

    internal void Abort()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ready && _process is { HasExited: false })
        {
            try
            {
                _commands!.Write(-1); _commands.Flush();
                if (!_process.WaitForExit(500))
                {
                    PipelineLog.Info("RTX VSR 驱动清理未返回，回收隔离进程。");
                    Abort();
                }
            }
            catch { Abort(); }
        }
        else Abort();
        if (_process != null)
        {
            try { _process.WaitForExit(5000); } catch { }
            if (_encodedCache != null && Encoder == null && _errors?.IsCompletedSuccessfully == true
                && !string.IsNullOrWhiteSpace(_errors.Result))
                PipelineLog.Info("VSR GPU 编码回退详情: " + _errors.Result.Trim());
            _commands?.Dispose(); _responses?.Dispose(); _process.Dispose();
        }
        if (_view != null)
        {
            if (_memory != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _mapping?.Dispose();
        if (_encodedCache != null) { try { File.Delete(_encodedCache); } catch { } }
    }

    /// <summary>Call before normal app/CLI initialization. Returns false for normal invocations.</summary>
    public static bool TryServe(string[] args)
    {
        _hostSupportsWorker = true;
        if (args.Length == 0 || args[0] != "--vsr-worker") return false;
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("RTX Video shared-memory worker requires Windows.");
            if (args.Length != 12) throw new ArgumentException("Invalid RTX Video worker arguments.");
            var parent = Process.GetProcessById(int.Parse(args[11], CultureInfo.InvariantCulture));
            // A crashed parent cannot leave an orphan inside a driver teardown call.
            _ = Task.Run(() =>
            {
                try { parent.WaitForExit(); Process.GetCurrentProcess().Kill(); }
                finally { parent.Dispose(); }
            });
            var width = int.Parse(args[2], CultureInfo.InvariantCulture);
            var height = int.Parse(args[3], CultureInfo.InvariantCulture);
            var scale = int.Parse(args[4], CultureInfo.InvariantCulture);
            var quality = int.Parse(args[5], CultureInfo.InvariantCulture);
            var hdr = args[6] == "1";
            var capacity = int.Parse(args[7], CultureInfo.InvariantCulture);
            var sourceBytes = checked((long)width * height * (hdr ? 8 : 4));
            var slotBytes = checked(sourceBytes * (1 + scale * scale));
            using var mapping = MemoryMappedFile.OpenExisting(args[1]);
            using var view = mapping.CreateViewAccessor();
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                using var host = RtxVideoSuperResolutionHost.Create(args[8], args[9], args[10], width, height, scale, hdr, quality);
                using var responses = new BinaryWriter(Console.OpenStandardOutput());
                using var commands = new BinaryReader(Console.OpenStandardInput());
                responses.Write(1); responses.Write(host.QueueCapacity); responses.Flush();
                var pending = new Queue<int>(host.QueueCapacity);
                while (true)
                {
                    var slot = commands.ReadInt32();
                    if (slot == -1) break;
                    if (slot == -3)
                    {
                        var configuration = JsonSerializer.Deserialize<VsrEncoderConfiguration>(commands.ReadString())
                            ?? throw new InvalidOperationException("Missing GPU encoder configuration.");
                        var enabled = host.EnableEncoder(Path.Combine(Path.GetDirectoryName(args[8])!, "vsr_encoder.dll"), configuration);
                        responses.Write(enabled ? 1 : 0); responses.Flush(); continue;
                    }
                    if (slot == -4)
                    {
                        responses.Write(pending.Count == 0 && host.FinishEncoding() ? 1 : 0); responses.Flush(); continue;
                    }
                    if (slot == -2)
                    {
                        slot = commands.ReadInt32();
                        var preview = commands.ReadBoolean();
                        if (pending.Count == 0 || pending.Dequeue() != slot) throw new InvalidOperationException("VSR queue order mismatch.");
                        var output = pointer + view.PointerOffset + slot * slotBytes + sourceBytes;
                        var collected = host.QueueCapacity == 1 || host.DequeueFrame(output, preview);
                        responses.Write(collected ? 1 : 0); responses.Flush();
                        if (!collected) throw new InvalidOperationException("VSR native collection failed.");
                        continue;
                    }
                    if ((uint)slot >= (uint)capacity) throw new ArgumentOutOfRangeException(nameof(slot));
                    if (pending.Count == host.QueueCapacity || pending.Contains(slot)) throw new InvalidOperationException("VSR slot is still in flight.");
                    var source = pointer + view.PointerOffset + slot * slotBytes;
                    var ok = host.QueueCapacity > 1 ? host.EnqueueFrame(source) : host.ProcessFrame(source, source + sourceBytes);
                    if (ok) pending.Enqueue(slot);
                    responses.Write(ok ? 1 : 0); responses.Flush();
                    if (!ok) throw new InvalidOperationException("VSR native evaluation failed.");
                }
            }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }
        return true;
    }
}

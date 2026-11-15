using System.Security.Cryptography;

namespace Dlss5Demo.Core;

/// <summary>一个待迁移的 NR 运行库文件（相对源目录的路径保持原样，方便按目录名识别显卡代际）。</summary>
public sealed record NrMigrationItem(string SourcePath, string RelativePath, long Bytes);

/// <summary>
/// 迁移计划：源目录里发现的全部 <c>nvngx_dlssnr*.dll</c> 与目标路径。计划只读文件系统，
/// 不改动任何文件；执行阶段才复制、校验并删除源文件。
/// </summary>
public sealed record NrMigrationPlan(string SourceRoot, string TargetRoot, IReadOnlyList<NrMigrationItem> Items)
{
    public long TotalBytes { get; } = Items.Sum(item => item.Bytes);

    public static NrMigrationPlan Create(string sourceRoot, string targetRoot)
    {
        string source = Path.GetFullPath(sourceRoot);
        string target = Path.GetFullPath(targetRoot);
        if (NrRuntimeLocations.IsSameDirectory(source, target))
            throw new ArgumentException("源目录与目标目录相同，无需迁移。", nameof(targetRoot));

        var items = new List<NrMigrationItem>();
        if (Directory.Exists(source))
        {
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            try
            {
                foreach (string file in Directory.EnumerateFiles(source, "nvngx_dlssnr*.dll", enumeration))
                {
                    // 目标目录若嵌在源目录内（例如用户把模型目录设在 runtime 下），不能把自己的文件再迁一遍。
                    if (IsUnder(file, target)) continue;
                    long bytes = 0;
                    try { bytes = new FileInfo(file).Length; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    items.Add(new NrMigrationItem(file, Path.GetRelativePath(source, file), bytes));
                }
            }
            catch (IOException) { /* 刷新/迁移期间目录可能被移走。 */ }
            catch (UnauthorizedAccessException) { }
        }

        items.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new NrMigrationPlan(source, target, items);
    }

    internal static bool IsUnder(string path, string root)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>迁移进度：按文件数与字节数双口径，界面只在 UI 线程消费。</summary>
public readonly record struct NrMigrationProgress(int FilesDone, int TotalFiles, long BytesDone, long TotalBytes,
    string CurrentFile)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesDone / TotalBytes, 0, 1) : 0;
}

/// <summary>
/// 迁移结果。成功与否按文件粒度记录，单项失败不阻断其余文件；只有校验通过的副本
/// 才会删除源文件，失败文件保持原样并在 <see cref="Messages"/> 中给出原因。
/// </summary>
public sealed class NrMigrationResult
{
    public int Copied { get; internal set; }
    public int SkippedIdentical { get; internal set; }
    public int Renamed { get; internal set; }
    public int Failed { get; internal set; }

    /// <summary>已复制校验通过、但源文件删不掉（只读安装目录等）的数量。</summary>
    public int SourceKept { get; internal set; }

    public long BytesCopied { get; internal set; }
    public List<string> Messages { get; } = [];

    /// <summary>源绝对路径 → 目标绝对路径，供调用方重映射已保存的版本 ID。</summary>
    public Dictionary<string, string> PathMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasFailures => Failed > 0;
    public bool MovedAnything => Copied > 0 || SkippedIdentical > 0;

    public string Summary()
    {
        var parts = new List<string>();
        if (Copied > 0) parts.Add($"已迁移 {Copied} 个（{BytesCopied / 1048576.0:0.0} MiB）");
        if (SkippedIdentical > 0) parts.Add($"跳过相同文件 {SkippedIdentical} 个");
        if (Renamed > 0) parts.Add($"重名改名 {Renamed} 个");
        if (SourceKept > 0) parts.Add($"源文件保留 {SourceKept} 个");
        if (Failed > 0) parts.Add($"失败 {Failed} 个");
        return parts.Count == 0 ? "没有需要迁移的运行库文件。" : string.Join(" · ", parts);
    }
}

/// <summary>
/// NR 运行库迁移：把旧目录里的 <c>nvngx_dlssnr*.dll</c> 复制到新目录，校验 SHA-256 后删除
/// 源文件（等价于移动，但不依赖跨卷 Move）。同名但内容不同的文件不覆盖，改名为
/// <c>nvngx_dlssnr (2).dll</c> 之类的可用名字保留两份；内容相同则只保留一份。
/// 取消时已完成的文件保持在新目录，未完成的临时文件会被删除。
/// </summary>
public static class NrRuntimeMigration
{
    private const int BufferSize = 1024 * 1024;
    private const long ProgressStep = 4L * 1024 * 1024;

    public static Task<NrMigrationResult> RunAsync(NrMigrationPlan plan, IProgress<NrMigrationProgress>? progress,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCoreAsync(plan, progress, cancellationToken), cancellationToken);

    private static async Task<NrMigrationResult> RunCoreAsync(NrMigrationPlan plan, IProgress<NrMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new NrMigrationResult();
        var totalFiles = plan.Items.Count;
        long bytesDone = 0;
        var filesDone = 0;
        progress?.Report(new NrMigrationProgress(0, totalFiles, 0, plan.TotalBytes, ""));

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = item.SourcePath;
            string target = Path.Combine(plan.TargetRoot, item.RelativePath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    if (new FileInfo(target).Length == item.Bytes && HashesEqual(source, target, cancellationToken))
                    {
                        result.SkippedIdentical++;
                        result.PathMap[source] = target;
                        TryDeleteSource(source, result);
                        bytesDone += item.Bytes;
                        filesDone++;
                        progress?.Report(new NrMigrationProgress(filesDone, totalFiles, bytesDone, plan.TotalBytes, source));
                        continue;
                    }

                    target = AvailablePath(target);
                    result.Renamed++;
                }

                long copied = await CopyWithVerificationAsync(source, target, item, bytesDone, filesDone, totalFiles,
                    plan.TotalBytes, progress, cancellationToken).ConfigureAwait(false);

                result.Copied++;
                result.BytesCopied += copied;
                result.PathMap[source] = target;
                bytesDone += item.Bytes;
                filesDone++;
                TryDeleteSource(source, result);
                progress?.Report(new NrMigrationProgress(filesDone, totalFiles, bytesDone, plan.TotalBytes, source));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException)
            {
                result.Failed++;
                result.Messages.Add($"失败：{item.RelativePath} → {error.Message}");
                bytesDone += item.Bytes;
                filesDone++;
                progress?.Report(new NrMigrationProgress(filesDone, totalFiles, bytesDone, plan.TotalBytes, source));
            }
        }

        if (result.MovedAnything)
            PruneEmptyDirectories(plan.SourceRoot, result.PathMap.Keys);
        return result;
    }

    /// <summary>复制并校验，返回写入的字节数；任何异常都会删除未完成的副本。</summary>
    private static async Task<long> CopyWithVerificationAsync(string source, string target, NrMigrationItem item,
        long bytesDone, int filesDone, int totalFiles, long totalBytes, IProgress<NrMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long total = 0;
        try
        {
            string sourceHash;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                       FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                long reported = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    if (total - reported >= ProgressStep)
                    {
                        reported = total;
                        progress?.Report(new NrMigrationProgress(filesDone, totalFiles, bytesDone + total,
                            totalBytes, source));
                    }
                }
                sourceHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            long targetLength = new FileInfo(target).Length;
            if (targetLength != item.Bytes || targetLength != total)
                throw new IOException($"校验失败：目标大小 {targetLength} 与源 {item.Bytes} 不一致。");

            string targetHash;
            using (var verify = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                       FileOptions.SequentialScan))
                targetHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));

            if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("校验失败：目标副本 SHA-256 与源文件不一致。");
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return total;
    }

    private static bool HashesEqual(string left, string right, CancellationToken cancellationToken)
    {
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        var leftHash = SHA256.HashData(leftStream);
        cancellationToken.ThrowIfCancellationRequested();
        var rightHash = SHA256.HashData(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    /// <summary>源文件删除失败不视为迁移失败：只读安装目录下复制成功即可用，仅在结果里记录。</summary>
    private static void TryDeleteSource(string source, NrMigrationResult result)
    {
        try
        {
            File.SetAttributes(source, FileAttributes.Normal);
            File.Delete(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            result.SourceKept++;
            result.Messages.Add($"已复制但未能删除源文件：{source}（{error.Message}）");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>冲突时按 <c>name (2).ext</c> 递增取名，保留两份而不是覆盖。</summary>
    private static string AvailablePath(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            string candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("目标目录中同名文件过多，无法为迁移副本生成唯一名称。");
    }

    /// <summary>
    /// 只清理「原本装着被迁移文件」的目录链：逐层向上删除已空的父目录，遇到源根目录停止。
    /// 不碰与迁移无关的空目录（例如 runtime\Cache），避免顺手删掉别的功能目录。
    /// </summary>
    private static void PruneEmptyDirectories(string sourceRoot, IEnumerable<string> migratedSources)
    {
        if (!Directory.Exists(sourceRoot)) return;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in migratedSources)
        {
            for (var directory = Path.GetDirectoryName(source);
                 directory != null
                 && !NrRuntimeLocations.IsSameDirectory(directory, sourceRoot)
                 && NrMigrationPlan.IsUnder(directory, sourceRoot);
                 directory = Path.GetDirectoryName(directory))
                candidates.Add(directory);
        }

        foreach (string directory in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                Directory.Delete(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

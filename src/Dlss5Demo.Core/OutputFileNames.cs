namespace Dlss5Demo.Core;

/// <summary>Proposes output names without reusing an existing file or directory.</summary>
public static class OutputFileNames
{
    public static string ForInput(string inputPath, string extension)
        => Available(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!,
            Path.GetFileNameWithoutExtension(inputPath) + "_dlss5" + extension));

    public static string Available(string requestedPath)
    {
        var path = Path.GetFullPath(requestedPath);
        if (!Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (long suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}_{suffix}{extension}");
            if (!Exists(candidate)) return candidate;
        }
    }

    internal static Reservation Reserve(string requestedPath)
    {
        while (true)
        {
            var path = Available(requestedPath);
            try
            {
                // CreateNew claims the name atomically. Keep the handle open
                // without delete sharing so the claimed file cannot be replaced
                // while FFmpeg writes it through a second handle.
                return new Reservation(path, new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.ReadWrite));
            }
            catch (IOException) when (Exists(path)) { /* A competing creator won; choose another suffix. */ }
        }
    }

    internal sealed class Reservation(string path, FileStream ownership) : IDisposable
    {
        internal string Path { get; } = path;
        public void Dispose() => ownership.Dispose();
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

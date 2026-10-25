using Dlss5Demo.Core;

internal static class NrRuntimeCatalogTests
{
    internal static unsafe void Switch(string alternate)
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "nr-versions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] input = new byte[512 * 288 * 4], output = new byte[input.Length];
        for (int i = 0; i < input.Length; i += 4) { input[i] = 80; input[i + 1] = 120; input[i + 2] = 180; input[i + 3] = 255; }
        int iteration = 0;
        foreach (string dll in new[] { DemoPaths.RuntimeDllPath, NrRuntimeCatalog.ResolveArgument(alternate), DemoPaths.RuntimeDllPath })
        {
            using var host = DlssNrHost.Create(DemoPaths.HostDllPath, dll, Path.Combine(root, $"{iteration++}.log"), 512, 288, new DlssNrOptions());
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Require(process.Modules.Cast<System.Diagnostics.ProcessModule>().Any(module =>
                string.Equals(module.FileName, Path.GetFullPath(dll), StringComparison.OrdinalIgnoreCase)), "The selected NR DLL was not loaded.");
            fixed (byte* source = input, destination = output)
                Require(host.ProcessFrame(source, destination, true), "Selected NR runtime failed to evaluate.");
            Require(output.Any(value => value != 0), "Selected NR runtime returned empty output.");
            Console.WriteLine("PASS NR in-process runtime switch: " + dll);
        }
    }

    internal static void Discovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string runtime = Path.Combine(root, "runtime"), mods = Path.Combine(root, "mods");
            Put("runtime/nvngx_dlssnr.dll");
            Put("runtime/nr/future-999/RTX-whatever/nvngx_dlssnr.dll");
            Put("runtime/nvngx_dlssnr_custom.dll");
            Put("runtime/vsr_host.dll");
            Put("mods/custom/nvngx_dlssnr.dll");
            var first = NrRuntimeCatalog.Discover(runtime, [mods, mods], Path.Combine(runtime, "nvngx_dlssnr.dll"));
            Require(first.Count == 4, "NR discovery must include nested/custom versions, exclude other DLLs, and deduplicate paths.");
            Require(first.Any(x => x.Id == "runtime/nr/future-999/RTX-whatever/nvngx_dlssnr.dll"), "Future versions must need no code change.");
            Put("runtime/new-version/nvngx_dlssnr.dll");
            var refreshed = NrRuntimeCatalog.Discover(runtime, [mods]);
            Require(refreshed.Count == 5 && first.All(x => refreshed.Any(y => y.Id == x.Id)), "Refresh must discover new files without changing saved IDs.");
            Require(NrRuntimeCatalog.Discover(Path.Combine(root, "missing"), []).Count == 0, "Empty install must be supported.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        void Put(string relative)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []); // Unknown version metadata must not hide user-added DLLs.
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

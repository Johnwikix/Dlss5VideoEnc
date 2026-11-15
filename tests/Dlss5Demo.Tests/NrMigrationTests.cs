using Dlss5Demo.Core;

/// <summary>
/// 设置页 NR 目录覆盖与迁移的纯文件系统回归：不加载 DLL、不依赖 GPU，
/// 覆盖目录扫描前缀、迁移移动语义、相同文件去重与同名冲突改名。
/// </summary>
internal static class NrMigrationTests
{
    internal static void UserDirectoryDiscovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-locations-" + Guid.NewGuid().ToString("N"));
        try
        {
            string runtime = Path.Combine(root, "runtime");
            string custom = Path.Combine(root, "models");
            Directory.CreateDirectory(Path.Combine(runtime, "nr", "Community-RTX50"));
            Directory.CreateDirectory(custom);
            File.WriteAllBytes(Path.Combine(runtime, "nvngx_dlssnr.dll"), [1]);
            File.WriteAllBytes(Path.Combine(runtime, "nr", "Community-RTX50", "nvngx_dlssnr.dll"), [2]);
            File.WriteAllBytes(Path.Combine(custom, "nvngx_dlssnr.dll"), [3]);

            var withCustom = NrRuntimeCatalog.Discover(runtime, [], null, custom);
            Require(withCustom.Any(item => item.Id == "custom/nvngx_dlssnr.dll"),
                "The user directory must be discovered with a custom/ id.");
            Require(withCustom.Count(item => item.Id.EndsWith("/nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)) == 3,
                "Built-in and user directories must both stay visible after overriding the folder.");

            var withoutCustom = NrRuntimeCatalog.Discover(runtime, [], null, null);
            Require(withoutCustom.All(item => !item.Id.StartsWith("custom/", StringComparison.OrdinalIgnoreCase)),
                "No custom entries may appear when no user directory is configured.");

            // 与 runtime 相同不算覆盖：否则同一批 DLL 会出现两份并让迁移自己复制自己。
            var same = NrRuntimeCatalog.Discover(runtime, [], null, runtime);
            Require(same.Count == withoutCustom.Count, "A user directory equal to runtime must not duplicate entries.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static void MigrationMovesAndDeduplicates()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-migrate-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "runtime");
            string target = Path.Combine(root, "models");
            Directory.CreateDirectory(Path.Combine(source, "nr", "Community-RTX30"));
            Directory.CreateDirectory(target);
            byte[] legacy = [1, 2, 3, 4];
            string rootDll = Path.Combine(source, "nvngx_dlssnr.dll");
            string nestedDll = Path.Combine(source, "nr", "Community-RTX30", "nvngx_dlssnr.dll");
            File.WriteAllBytes(rootDll, legacy);
            File.WriteAllBytes(nestedDll, [9, 9, 9]);

            var plan = NrMigrationPlan.Create(source, target);
            Require(plan.Items.Count == 2 && plan.TotalBytes == 7, "The plan must cover every NR DLL with its size.");

            var result = NrRuntimeMigration.RunAsync(plan, null).GetAwaiter().GetResult();
            Require(result.Copied == 2 && result.Failed == 0, "Both runtime files must migrate without failures.");
            Require(!File.Exists(rootDll) && !File.Exists(nestedDll), "Migrated sources must be removed after verification.");
            Require(File.ReadAllBytes(Path.Combine(target, "nvngx_dlssnr.dll")).SequenceEqual(legacy),
                "The copied file must keep the original bytes.");
            Require(result.PathMap[rootDll] == Path.Combine(target, "nvngx_dlssnr.dll"),
                "The path map must let the caller remap the saved version id.");

            // 第二次迁移：相同内容只保留一份；同名不同内容改名保留两份。
            string second = Path.Combine(root, "legacy");
            Directory.CreateDirectory(second);
            File.WriteAllBytes(Path.Combine(second, "nvngx_dlssnr.dll"), legacy);
            File.WriteAllBytes(Path.Combine(second, "nvngx_dlssnr_other.dll"), [7, 7]);

            var again = NrRuntimeMigration.RunAsync(NrMigrationPlan.Create(second, target), null).GetAwaiter().GetResult();
            Require(again.SkippedIdentical == 1 && again.Copied == 1 && again.Failed == 0,
                "Identical files must be deduplicated instead of copied twice.");
            Require(File.Exists(Path.Combine(second, "nvngx_dlssnr.dll")) is false,
                "A deduplicated source must be removed once the target copy is verified.");

            string third = Path.Combine(root, "legacy-conflict");
            Directory.CreateDirectory(third);
            File.WriteAllBytes(Path.Combine(third, "nvngx_dlssnr.dll"), [5, 5, 5]);
            var conflict = NrRuntimeMigration.RunAsync(NrMigrationPlan.Create(third, target), null).GetAwaiter().GetResult();
            Require(conflict.Renamed == 1 && conflict.Copied == 1 && conflict.Failed == 0,
                "A same-named file with different content must be renamed, never overwritten.");
            Require(File.Exists(Path.Combine(target, "nvngx_dlssnr (2).dll")),
                "The renamed copy must stay discoverable under the target directory.");
            Require(File.ReadAllBytes(Path.Combine(target, "nvngx_dlssnr.dll")).SequenceEqual(legacy),
                "The existing target file must keep its original bytes after a conflict.");
            Require(Directory.GetFiles(target).Length == 3, "Target must hold both versions plus the differently named file.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static void SameDirectoryIsRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-same-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rejected = false;
            try { NrMigrationPlan.Create(root, root); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Migrating a directory onto itself must be rejected instead of copying files in place.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}

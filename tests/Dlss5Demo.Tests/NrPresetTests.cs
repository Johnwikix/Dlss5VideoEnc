using Dlss5Demo.Core;

internal static class NrPresetTests
{
    internal static void Options()
    {
        Require(DlssNrPresets.Parse("A") == DlssNrPreset.Legacy && DlssNrPresets.Parse("1") == DlssNrPreset.Legacy, "Legacy aliases failed.");
        Require(DlssNrPresets.Parse("b") == DlssNrPreset.Lightweight && DlssNrPresets.Parse("auto") == DlssNrPreset.Auto, "Preset aliases failed.");
        try { DlssNrPresets.Parse("c"); throw new Exception("Invalid preset accepted."); }
        catch (ArgumentException) { }
        Require(DlssNrHost.NormalizeOptions(new DlssNrOptions { Preset = (DlssNrPreset)99 }).Preset == DlssNrPreset.Auto,
            "Invalid persisted/model values must use Auto.");
    }

    internal static unsafe void Native(string oldRuntime, string newRuntime)
    {
        const int width = 512, height = 288;
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "nr-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] input = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            input[offset] = (byte)((x * 3 + y) % 256);
            input[offset + 1] = (byte)((y * 2 + (x / 24 % 2) * 80) % 256);
            input[offset + 2] = (byte)((x + y * 3) % 256);
            input[offset + 3] = 255;
        }
        byte[] legacy = Evaluate(oldRuntime, DlssNrPreset.Auto, false, "old");
        byte[] legacyInNew = Evaluate(newRuntime, DlssNrPreset.Legacy, false, "new-A");
        byte[] automatic = Evaluate(newRuntime, DlssNrPreset.Auto, false, "new-auto");
        byte[] lightweight = Evaluate(newRuntime, DlssNrPreset.Lightweight, false, "new-B");
        byte[] recreated = Evaluate(newRuntime, DlssNrPreset.Legacy, true, "new-A-motion-recreate");
        Require(legacy.AsSpan().SequenceEqual(legacyInNew), "310.9.16 preset A did not select the 310.8 model.");
        Require(automatic.AsSpan().SequenceEqual(lightweight), "310.9.16 auto did not match preset B.");
        Require(!legacy.AsSpan().SequenceEqual(lightweight), "A and B selected the same output.");
        Require(legacy.AsSpan().SequenceEqual(recreated), "Motion-guidance resource recreation lost the requested preset.");
        Console.WriteLine("PASS NR A/B model selection, runtime switching and motion-guidance recreation: " + root);

        byte[] Evaluate(string runtime, DlssNrPreset preset, bool motion, string name)
        {
            byte[] output = new byte[input.Length];
            float[]? vectors = motion ? new float[width * height * 2] : null;
            var options = new DlssNrOptions { Preset = preset };
            using var host = DlssNrHost.Create(DemoPaths.HostDllPath, NrRuntimeCatalog.ResolveArgument(runtime), Path.Combine(root, name + ".log"), width, height, options);
            host.ConfigureMotionGuidance(motion);
            host.UpdateOptions(options with { Intensity = .75f });
            host.UpdateOptions(options);
            try
            {
                host.UpdateOptions(options with { Preset = preset == DlssNrPreset.Legacy ? DlssNrPreset.Lightweight : DlssNrPreset.Legacy });
                throw new Exception("A model change was accepted without recreating the feature.");
            }
            catch (InvalidOperationException) { }
            fixed (byte* source = input, destination = output)
            fixed (float* guidance = vectors)
                for (int frame = 0; frame < 12; frame++)
                    Require(host.ProcessVideoFrame(source, guidance, destination, frame == 0), "NR evaluation failed: " + name);
            Require(output.Any(value => value != 0), "NR returned no pixels: " + name);
            return output;
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

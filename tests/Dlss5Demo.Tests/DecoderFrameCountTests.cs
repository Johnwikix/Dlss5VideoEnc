using System.Buffers.Binary;
using System.Diagnostics;
using Dlss5Demo.Core;

internal static class DecoderFrameCountTests
{
    // An MP4 edit list can exclude a stored sample without changing nb_frames.
    // Exercise demux, decoder draining and the real transcode completion check.
    internal static async Task RunAsync(string? original = null)
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".qa", "decoder-counts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string seed = Path.Combine(root, "seed.mp4"), edited = Path.Combine(root, "editlist.mp4");
        Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=60",
            "-frames:v", "24", "-c:v", "libx264", "-bf", "2", "-g", "60", "-movie_timescale", "60000", seed]);
        byte[] bytes = File.ReadAllBytes(seed);
        int elst = FindBox(bytes, "moov", "trak", "edts", "elst");
        Require(bytes[elst + 8] == 0 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 12)) == 1,
            "Fixture requires one version-0 edit-list entry.");
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(elst + 16), duration - 1000); // Exclude the last 1/60 s.
        File.WriteAllBytes(edited, bytes);
        Require(MediaProbe.Probe(edited).FrameCount == 24, "Stored sample count must remain 24.");
        Require(CountFrames(edited) == 23, "Edit list must present 23 frames through FFmpeg.");

        foreach (var mode in new[] { VideoDecodeMode.Software, VideoDecodeMode.Nvdec })
        {
            await Convert(edited, $"edited-{mode}", mode, 23);
            await Convert(seed, $"bframes-{mode}", mode, 24);
            await Convert(edited, $"limit-{mode}", mode, 5, limit: 5);
            await Convert(edited, $"long-limit-{mode}", mode, 23, limit: 100);
        }
        if (original != null)
        {
            long count = CountFrames(original);
            await Convert(original, "original-software", VideoDecodeMode.Software, count);
            await Convert(original, "original-nvdec", VideoDecodeMode.Nvdec, count);
        }
        byte[] broken = File.ReadAllBytes(seed);
        int mdat = FindBox(broken, "mdat");
        BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(mdat + 8), int.MaxValue); // Invalid first H.264 NAL length.
        string corrupt = Path.Combine(root, "corrupt.mp4");
        File.WriteAllBytes(corrupt, broken);
        bool rejected = false;
        try { await Convert(corrupt, "corrupt-output", VideoDecodeMode.Software, 24); }
        catch (InvalidOperationException error) when (error.Message.Contains("解码")) { rejected = true; }
        Require(rejected, "Corrupt packets must still fail decoding, not be accepted as a count correction.");
        Console.WriteLine("PASS corrupt H.264 packets still fail decoding");
        Console.WriteLine("PASS MP4 edit-list counts, B-frame draining and explicit limits: " + root);

        async Task Convert(string input, string name, VideoDecodeMode mode, long expected, int? limit = null)
        {
            var profile = new VideoTranscodeOptions
            {
                VideoCodec = VideoCodec.H264, HardwareBackend = HardwareBackend.Software,
                AudioCodec = AudioCodec.None, EncoderPreset = "p1",
            };
            string output = Path.Combine(root, name + ".mp4");
            var progress = new CompletionProgress();
            var result = await DlssVideoPipeline.RunAsync(input, output, new TranscodeOptions
            {
                EnableNr = false, DecodeMode = mode, MaxFrames = limit,
                EnablePreviewFrames = false, Video = profile, VideoEncoder = VideoCodecNames.Encoder(profile),
            }, progress);
            Require(result.FramesProcessed == expected, $"{name}: expected {expected}, processed {result.FramesProcessed}.");
            Require(CountFrames(result.OutputPath) == expected, name + ": encoded frames differ from decoded frames.");
            Require(progress.Last?.FramesDone == expected && progress.Last.TotalFrames == expected,
                name + ": final progress must use the actual completed frame count.");
            Console.WriteLine($"PASS {name}: {expected} frames");
        }
    }

    private static int FindBox(byte[] data, params string[] path)
    {
        int start = 0, end = data.Length, found = -1;
        foreach (string name in path)
        {
            found = -1;
            for (int offset = start; offset + 8 <= end;)
            {
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
                Require(size >= 8 && offset + size <= end, "Invalid fixture MP4 atom.");
                if (System.Text.Encoding.ASCII.GetString(data, offset + 4, 4) == name)
                { found = offset; start = offset + 8; end = offset + size; break; }
                offset += size;
            }
            Require(found >= 0, "Missing fixture MP4 atom: " + name);
        }
        return found;
    }

    private static long CountFrames(string path) => long.Parse(Run("ffprobe",
        ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", path]).Trim());

    private sealed class CompletionProgress : IProgress<TranscodeProgress>
    {
        internal TranscodeProgress? Last;
        public void Report(TranscodeProgress value) => Last = value;
    }

    private static string Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Require(process.WaitForExit(60_000), executable + " timed out.");
        Require(process.ExitCode == 0, executable + ": " + error.GetAwaiter().GetResult());
        return output;
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

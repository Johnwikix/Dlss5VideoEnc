using System.Security.Cryptography;
using System.Text;

namespace Dlss5Demo.Core;

/// <summary>从已校验的模型提取版本信息，生成无权重 GPU 像素转换会话；网络模型本身不改写。</summary>
internal static class VideoPixelGraph
{
    internal const string Prefix = "fancytool_pixels_v1/";

    internal static (string Before, string After) CreateStages(string sourcePath, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fields = Read(File.ReadAllBytes(sourcePath));
        string Stage(string part)
        {
            byte[] graph = Template(kind, part);
            using var model = new MemoryStream();
            foreach (var field in fields)
            {
                if (field.Number == 7) Write(model, 7, graph);
                else model.Write(field.Raw.Span);
            }
            return Save(model.ToArray(), ct);
        }
        return (Stage("prefix"), Stage("suffix"));
    }

    private static string Save(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string directory = Path.Combine(DemoPaths.RuntimeDir, "Cache", "RifePixels");
        Directory.CreateDirectory(directory);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string path = Path.Combine(directory, hash + ".onnx");
        if (File.Exists(path))
        {
            using var existing = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(existing)) == hash) return path;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static byte[] Template(string kind, string part)
    {
        using var stream = typeof(VideoPixelGraph).Assembly.GetManifestResourceStream($"Dlss5Demo.Pixels.{kind}-{part}.pb")
            ?? throw new InvalidOperationException("Missing video pixel adapter");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    // ONNX protobuf 只在建会话时处理。原始字段原样复制，不解析或重写网络权重。
    private readonly record struct Field(int Number, ReadOnlyMemory<byte> Raw, ReadOnlyMemory<byte> Payload);
    private static List<Field> Read(ReadOnlyMemory<byte> data)
    {
        var fields = new List<Field>();
        int offset = 0;
        while (offset < data.Length)
        {
            int start = offset;
            ulong tag = Varint(data.Span, ref offset);
            int length;
            switch (tag & 7)
            {
                case 0: Varint(data.Span, ref offset); fields.Add(new((int)(tag >> 3), data[start..offset], default)); continue;
                case 1: length = 8; break;
                case 2: length = checked((int)Varint(data.Span, ref offset)); break;
                case 5: length = 4; break;
                default: throw new InvalidDataException("Unsupported ONNX protobuf wire type");
            }
            if (length > data.Length - offset) throw new InvalidDataException("Truncated ONNX protobuf");
            var payload = data.Slice(offset, length);
            offset += length;
            fields.Add(new((int)(tag >> 3), data[start..offset], payload));
        }
        return fields;
    }
    private static ulong Varint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            byte b = data[offset++];
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Invalid ONNX protobuf varint");
    }
    private static void Write(Stream stream, int field, ReadOnlySpan<byte> bytes)
    {
        WriteVarint(stream, (uint)(field << 3 | 2));
        WriteVarint(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }
    private static void WriteVarint(Stream stream, uint value)
    {
        while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
}

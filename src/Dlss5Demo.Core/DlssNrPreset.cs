namespace Dlss5Demo.Core;

/// <summary>NR model hint used when creating the feature. 310.8 has only the legacy model.</summary>
public enum DlssNrPreset
{
    Auto = 0,
    Legacy = 1,
    Lightweight = 2,
}

public static class DlssNrPresets
{
    public static DlssNrPreset Parse(string value) => value.ToLowerInvariant() switch
    {
        "auto" or "0" => DlssNrPreset.Auto,
        "a" or "legacy" or "1" => DlssNrPreset.Legacy,
        "b" or "lightweight" or "2" => DlssNrPreset.Lightweight,
        _ => throw new ArgumentException("NR 模型预设只能是 auto、a（旧模型）或 b（轻量模型）。", nameof(value)),
    };
}

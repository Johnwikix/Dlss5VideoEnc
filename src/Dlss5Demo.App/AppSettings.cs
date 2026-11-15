using System.Text.Json.Serialization;

namespace Dlss5Demo.App;

/// <summary>需跨会话保留的界面设置（与工作台控件一一对应；ComboBox 存选中索引）。</summary>
public sealed class AppSettings
{
    internal const int CurrentNrPresetSettingsVersion = 1;

    /// <summary>外观：0 跟随系统 / 1 浅色 / 2 深色。默认深色，与历史版本一致。</summary>
    public int ThemeIndex { get; set; } = 2;

    /// <summary>设置页指定的 NR 运行库目录；空 = 使用随程序发布的 runtime 目录。</summary>
    public string NrRuntimeDirectory { get; set; } = "";

    // DLSS NR
    public bool EnableNr { get; set; } = true;
    /// <summary>Catalog path ID；空 = 「自动」策略（环境变量 → mods → 设置页目录 → 内置 runtime）。</summary>
    public string NrRuntimeId { get; set; } = "";
    public int DlssNrPreset { get; set; } = 1;
    /// <summary>Distinguishes the old automatic default from future explicit Auto selections.</summary>
    public int NrPresetSettingsVersion { get; set; }
    public int DlssStyle { get; set; }
    public double DlssIntensity { get; set; } = 1;
    public double DlssLocalTone { get; set; } = 1;
    public double DlssLocalStructure { get; set; } = 1;
    public double DlssSkinStructure { get; set; } = 0.5;
    public bool DlssUseAutoMask { get; set; }
    public bool DlssMotionGuidance { get; set; } = true;
    public bool DlssFineMotion { get; set; }
    public bool DlssContinuousHistory { get; set; } = true;

    // 输出与滤镜
    public int SuperResolutionIndex { get; set; }
    public bool NrBeforeSuperResolution { get; set; } = true;
    public bool EnableInterpolation { get; set; }
    public int InterpolationMultiplierIndex { get; set; }
    public int InterpolationBackendIndex { get; set; }
    public int ContainerIndex { get; set; }
    public int CodecIndex { get; set; } = 1;
    public int HardwareIndex { get; set; } = 1;
    public int DecodeModeIndex { get; set; }
    public int EncoderPresetIndex { get; set; } = 2;
    public int AudioIndex { get; set; }
    public int Quality { get; set; } = 19;
    public int HdrIndex { get; set; }
    public int FpsIndex { get; set; }
    public int RateControlIndex { get; set; }
    public int VideoBitrateKbps { get; set; } = 8000;
    public bool TwoPass { get; set; }
    public int AudioBitrateKbps { get; set; } = 192;
    public double AudioGainDb { get; set; }
    public int ScaleIndex { get; set; }
    public int DenoiseIndex { get; set; }
    public int DeinterlaceIndex { get; set; }
    public bool CropEnabled { get; set; }
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLeft { get; set; }
    public int CropRight { get; set; }
    public int GifWidth { get; set; } = 480;
    public int GifFps { get; set; } = 15;
    public int GifMaxColors { get; set; } = 256;
    public int GifDitherIndex { get; set; } = 1;

    // 任务
    public bool FramesLimit { get; set; }

    internal void UpgradeNrPresetDefault()
    {
        // Earlier builds saved Auto even when the user never chose a model.
        // Migrate it once; later explicit Auto/B selections remain persistent.
        if (DlssNrPreset is < 0 or > 2 ||
            (NrPresetSettingsVersion < CurrentNrPresetSettingsVersion && DlssNrPreset == 0))
            DlssNrPreset = 1;
        NrPresetSettingsVersion = Math.Max(NrPresetSettingsVersion, CurrentNrPresetSettingsVersion);
    }
}

/// <summary>System.Text.Json 源生成器上下文：序列化在编译期生成，无运行时反射。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext { }

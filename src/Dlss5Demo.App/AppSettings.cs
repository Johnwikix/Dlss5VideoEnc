using System.Text.Json.Serialization;

namespace Dlss5Demo.App;

/// <summary>需跨会话保留的界面设置（与 MainWindow 控件一一对应；ComboBox 存选中索引）。</summary>
public sealed class AppSettings
{
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";

    // DLSS NR
    public int DlssStyle { get; set; }
    public double DlssIntensity { get; set; } = 1;
    public double DlssLocalTone { get; set; } = 1;
    public double DlssLocalStructure { get; set; } = 1;
    public double DlssSkinStructure { get; set; } = 0.5;
    public bool DlssUseAutoMask { get; set; }

    // 输出与滤镜
    public int SuperResolutionIndex { get; set; }
    public int ContainerIndex { get; set; }
    public int CodecIndex { get; set; } = 1;
    public int HardwareIndex { get; set; } = 1;
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
}

/// <summary>System.Text.Json 源生成器上下文：序列化在编译期生成，无运行时反射。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext { }

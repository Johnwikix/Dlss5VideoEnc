using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dlss5Demo.App.Views.Controls;

/// <summary>轻量状态徽标：圆点颜色表达 ready / missing / off，文字由调用方提供。</summary>
public partial class StatusPill : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusPill, string?>(nameof(Text), string.Empty);

    /// <summary>状态语义：ready（正常）/ missing（缺失）/ off（未启用或中性）。</summary>
    public static readonly StyledProperty<string?> ToneProperty =
        AvaloniaProperty.Register<StatusPill, string?>(nameof(Tone), "off");

    public StatusPill()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => RefreshTone();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty) RefreshTone();
    }

    private void RefreshTone()
    {
        if (Dot is null) return;
        string key = Tone switch
        {
            "ready" => "SystemFillColorSuccessBrush",
            "missing" => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        Dot.Fill = this.TryFindResource(key, out object? brush) && brush is IBrush resolved
            ? resolved
            : Brushes.Gray;
    }
}

using Avalonia;
using Avalonia.Controls;

namespace Dlss5Demo.App.Views.Controls;

/// <summary>页面标题：统一 PageTitle/PageSubtitle 排版，右侧可放状态徽标或操作按钮。</summary>
public partial class ToolPageHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ToolPageHeader, string?>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<ToolPageHeader, string?>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<ToolPageHeader, object?>(nameof(ActionContent));

    public ToolPageHeader() => InitializeComponent();

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}

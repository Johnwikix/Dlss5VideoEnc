using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace Dlss5Demo.App.Views;

/// <summary>
/// 应用外壳：Fluent 导航（工作台 / 设置）+ 自定义标题栏。会话状态在这里创建并注入两个页面，
/// 关闭时由工作台统一保存设置并释放预览资源。
/// </summary>
public partial class MainWindow : FAAppWindow
{
    private readonly AppSession _session;

    public MainWindow()
    {
        // 先建会话：主题在窗口创建前生效，NR 目录覆盖也要早于两个页面的首次发现。
        _session = new AppSession(AppSettingsStore.Load());
        InitializeComponent();
        Nav.SelectedItem = WorkbenchNavItem;
        WorkbenchPage.Attach(_session);
        SettingsPage.Attach(_session);
        _session.NrDirectoryChanged += OnNrDirectoryChanged;
        Closed += MainWindow_Closed;
    }

    /// <summary>当前管线任务；进程退出前等待 native 清理完成。</summary>
    public Task PipelineTask => WorkbenchPage.PipelineTask;

    private void OnNrDirectoryChanged(string? preferredRuntimeId) =>
        WorkbenchPage.OnNrRuntimeDirectoryChanged(preferredRuntimeId);

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _session.NrDirectoryChanged -= OnNrDirectoryChanged;
        WorkbenchPage.Shutdown();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 48;
    }

    private void Nav_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs args)
    {
        bool showSettings = ReferenceEquals(args.SelectedItem, SettingsNavItem) || args.IsSettingsSelected;
        SettingsPage.IsVisible = showSettings;
        WorkbenchPage.IsVisible = !showSettings;
    }

    private void AppTitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Dlss5Demo.App;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // UI 线程异常：记日志后继续运行，避免闪退（错误详情见 logs/app-*.log）。
        Dispatcher.UnhandledException += (_, eventArgs) =>
        {
            Serilog.Log.Error(eventArgs.Exception, "UI 线程未处理异常（已吞并继续）。");
            eventArgs.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

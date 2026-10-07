using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Dlss5Demo.Core;
using Serilog;

namespace Dlss5Demo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureLogging();
        HookGlobalExceptionHandlers();
        try
        {
            // --autorun <input> <output> [--frames N]：无界面跑完整管线（自动验收用）。
            if (args.Length >= 3 && args[0] == "--autorun")
            {
                Environment.ExitCode = Autorun.RunAsync(args).GetAwaiter().GetResult();
                return;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "主流程未处理异常。");
            throw;
        }
        finally
        {
            WaitForPipelineShutdown();
            Log.Information("进程退出。");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Serilog 落到程序根目录 logs/（按天滚动），并接住 Core 管线日志。</summary>
    private static void ConfigureLogging()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        PipelineLog.Sink = message => Log.Information("{PipelineMessage}", message);
        Log.Information("进程启动: {Version}", typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    /// <summary>兜底捕获：终止性未处理异常、未观察的任务异常（记完日志再让默认行为继续）。</summary>
    private static void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            Log.Fatal(eventArgs.ExceptionObject as Exception
                ?? new Exception(Convert.ToString(eventArgs.ExceptionObject)), "未处理异常，进程即将终止。");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "未观察的任务异常（已标记为已观察）。");
            eventArgs.SetObserved();
        };
    }

    /// <summary>
    /// 关窗后 UI 消息循环已停：Core 全链 ConfigureAwait(false)，管线清理在线程池收尾，
    /// 这里同步等它结束再退进程，避免带着活跃 NGX/ffmpeg 会话硬退（退出期闪退）。
    /// </summary>
    private static void WaitForPipelineShutdown()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow is MainWindow window && !window.PipelineTask.IsCompleted)
        {
            try
            {
                window.PipelineTask.Wait(TimeSpan.FromSeconds(10));
            }
            catch { /* 退出路径上的异常只记录不影响退出 */ }
        }
    }
}

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
        if (RtxVideoWorker.TryServe(args)) return;
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
            DlssVideoPipeline.ReleaseGpuResources();
            Log.Information("进程退出。");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Serilog 落到日志目录（按天滚动），并接住 Core 管线日志。MSIX 安装目录
    /// 只读，DemoPaths.LogsDir 在打包运行时指向可写数据目录；即使该目录不可写，
    /// 也不能让日志配置异常阻断启动。
    /// </summary>
    private static void ConfigureLogging()
    {
        var logDirectory = DemoPaths.LogsDir;
        try
        {
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
        }
        catch (Exception error)
        {
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
            System.Diagnostics.Trace.WriteLine($"日志目录不可写 {logDirectory}: {error}");
        }
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

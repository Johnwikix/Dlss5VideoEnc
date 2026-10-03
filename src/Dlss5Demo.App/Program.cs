using Avalonia;
using System;

namespace Dlss5Demo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --autorun <input> <output> [--frames N]：无界面跑完整管线（自动验收用）。
        if (args.Length >= 3 && args[0] == "--autorun")
        {
            var exitCode = Autorun.RunAsync(args).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

namespace Dlss5Demo.Core;

/// <summary>
/// Core 管线的最小日志出口（零依赖）：宿主程序把 Sink 接到 Serilog 等日志框架。
/// 记录会话创建/释放与管线起止，native 崩溃时日志里能看到最后一步。
/// </summary>
public static class PipelineLog
{
    public static Action<string>? Sink { get; set; }

    public static void Info(string message) => Sink?.Invoke(message);
}

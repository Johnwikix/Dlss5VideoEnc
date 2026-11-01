using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Dlss5Demo.Core;

namespace Dlss5Demo.App;

internal enum QueueItemState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
    Skipped,
}

/// <summary>批量队列项：输入路径、探测规格与逐项状态；属性变化即时刷到队列列表 UI。</summary>
internal sealed class QueueItem : INotifyPropertyChanged
{
    private static readonly IBrush WaitingBrush = new ImmutableSolidColorBrush(Color.Parse("#8C9AA7"));
    private static readonly IBrush RunningBrush = new ImmutableSolidColorBrush(Color.Parse("#5CE1E6"));
    private static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.Parse("#7BD88F"));
    private static readonly IBrush FailedBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush StoppedBrush = new ImmutableSolidColorBrush(Color.Parse("#C9A66B"));

    public QueueItem(string inputPath) => InputPath = inputPath;

    public string InputPath { get; }
    public string FileName => Path.GetFileName(InputPath);

    /// <summary>静态图输入：走单帧 NR 路径，输出 PNG/JPEG。</summary>
    public bool IsImage => StillImage.IsImageFile(InputPath);

    /// <summary>详情卡手动指定的输出路径；空 = 自动按「原名_dlss5.ext」命名。</summary>
    public string? OutputOverride { get; set; }

    /// <summary>实际输出路径：该项开始转码时经 OutputFileNames.Available 防冲突后确定。</summary>
    public string? ActualOutput { get; private set; }

    public VideoInfo? Video { get; private set; }
    public QueueItemState State { get; private set; } = QueueItemState.Waiting;
    public string StatusText { get; private set; } = "等待中";

    /// <summary>探测规格一行摘要，详情卡的 InfoText 直接显示。</summary>
    public string Detail { get; private set; } = "正在探测…";

    public IBrush StatusBrush => State switch
    {
        QueueItemState.Running => RunningBrush,
        QueueItemState.Done => DoneBrush,
        QueueItemState.Failed => FailedBrush,
        QueueItemState.Cancelled => StoppedBrush,
        QueueItemState.Skipped => StoppedBrush,
        _ => WaitingBrush,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void SetState(QueueItemState state, string statusText)
    {
        State = state;
        StatusText = statusText;
        Raise(nameof(State), nameof(StatusText), nameof(StatusBrush));
    }

    public void SetProbe(VideoInfo video)
    {
        Video = video;
        Detail = IsImage
            ? $"{video.Codec} · {video.Width}×{video.Height} · 静态图\n单帧 NR → PNG"
            : $"{video.Codec} · {video.Width}×{video.Height} · {video.Fps:0.##} fps · {video.PixelFormat}\n" +
              $"{video.FrameCount} 帧 / {video.DurationSeconds:0.0}s · 音频 {(video.HasAudio ? video.AudioCodec : "无")}";
        Raise(nameof(Video), nameof(Detail));
    }

    public void SetProbeFailed(string error)
    {
        Detail = "探测失败: " + error;
        Raise(nameof(Detail));
    }

    public void SetRunning(string output)
    {
        ActualOutput = output;
        SetState(QueueItemState.Running, "处理中…");
        Raise(nameof(ActualOutput));
    }

    public void SetProgress(long framesDone, long totalFrames, double fps)
    {
        var label = totalFrames > 0 ? $"处理中 {framesDone * 100.0 / totalFrames:0}%" : "处理中";
        SetState(QueueItemState.Running, $"{label} · {fps:0.0} fps");
    }

    public void SetDone(TranscodeResult result, bool interpolated, int multiplier)
    {
        if (IsImage)
        {
            SetState(QueueItemState.Done,
                $"完成 · 图片增强 · {result.Elapsed.TotalSeconds:0.0}s · {result.OutputBytes / 1048576.0:0.0} MiB");
            return;
        }
        var frameLabel = interpolated
            ? $"{result.FramesProcessed}→{result.FramesProcessed * multiplier} 帧"
            : $"{result.FramesProcessed} 帧";
        SetState(QueueItemState.Done, $"完成 · {frameLabel} · {result.AverageFps:0.0} fps · {result.OutputBytes / 1048576.0:0.0} MiB");
    }

    public void SetFailed(string error) => SetState(QueueItemState.Failed, "失败: " + error.Split('\n')[0]);
    public void SetCancelled() => SetState(QueueItemState.Cancelled, "已取消");
    public void SetSkipped() => SetState(QueueItemState.Skipped, "已跳过");
}

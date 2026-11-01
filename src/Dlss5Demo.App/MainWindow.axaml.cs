using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dlss5Demo.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Dlss5Demo.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _lastOutputPath;
    private TranscodePreviewBuffer? _previewBuffer;
    private VideoInfo? _activeVideo;
    /// <summary>内层管线任务：清理（NGX shutdown、ffmpeg 回收）完成即结束，不依赖 UI 线程。</summary>
    private Task? _pipelineTask;
    /// <summary>任务代数：旧任务的收尾 UI 写入不得覆盖新任务的状态。</summary>
    private int _runGeneration;
    private readonly AppSettings _settings;
    private readonly LatestProgress _latestProgress = new();
    private readonly Stopwatch _previewRate = new();
    private int _renderedPreviewFrames;
    private bool _closed;
    private readonly PreviewSurface _sourceSurface;
    private readonly PreviewSurface _outputSurface;
    private readonly DispatcherTimer _previewRenderTimer;
    /// <summary>批量队列：顺序逐项转码，设置对整批共用。</summary>
    private readonly ObservableCollection<QueueItem> _queue = [];
    private QueueItem? _runningItem;
    /// <summary>程序化写详情卡（OutputBox 等）期间抑制其 TextChanged 回写队列项。</summary>
    private bool _updatingDetail;

    /// <summary>当前管线任务；窗口关闭后用于等待 native 清理完成再退进程。</summary>
    public Task PipelineTask => _pipelineTask ?? Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        QueueList.ItemsSource = _queue;
        _sourceSurface = new PreviewSurface(OriginalImage);
        _outputSurface = new PreviewSurface(ProcessedImage);
        _previewRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _previewRenderTimer.Tick += RenderPreviewFrames;
        _previewRenderTimer.Start();
        Closed += MainWindow_Closed;
        SizeChanged += (_, _) => LogBox.Height = Height < 800 ? 70 : 110;
        _settings = AppSettingsStore.Load();
        RefreshNrRuntimes(_settings.NrRuntimeId);
        NrRuntimeBox.SelectionChanged += (_, _) => UpdateProcessingOrderHint();
        MotionGuidanceBox.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(CheckBox.IsChecked)) FineMotionBox.IsEnabled = MotionGuidanceBox.IsChecked == true;
        };
        InterpolationBackendBox.SelectionChanged += (_, _) => UpdateProcessingOrderHint();
        HdrBox.SelectionChanged += (_, _) => UpdateProcessingOrderHint();
        SuperResolutionBox.SelectionChanged += (_, _) => UpdateProcessingOrderHint();
        EnableInterpolationBox.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(CheckBox.IsChecked)) UpdateProcessingOrderHint();
        };
        EnableNrBox.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(CheckBox.IsChecked)) UpdateProcessingOrderHint();
        };
        NrBeforeSuperResolutionBox.PropertyChanged += (_, change) =>
        {
            if (change.Property.Name == nameof(CheckBox.IsChecked)) UpdateProcessingOrderHint();
        };
        AttachValueLabel(IntensitySlider, IntensityValue);
        AttachValueLabel(LocalToneSlider, LocalToneValue);
        AttachValueLabel(LocalStructSlider, LocalStructValue);
        AttachValueLabel(SkinSlider, SkinValue);
        QualitySlider.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(Slider.Value)) QualityValue.Text = ((int)QualitySlider.Value).ToString(CultureInfo.InvariantCulture);
        };
        ApplySettings();
        // 环境变量优先于持久化设置（自动化覆盖手工配置）：直接入队一个文件。
        var inputOverride = Environment.GetEnvironmentVariable("DLSS5_INPUT");
        if (!string.IsNullOrWhiteSpace(inputOverride)) AddFiles([inputOverride]);
        Log("就绪。DLSS 宿主: " + DemoPaths.HostDllPath);
        Log($"DLSS 运行库: {(NrRuntimeBox.SelectedItem as NrRuntimeInfo)?.DllPath ?? DemoPaths.RuntimeDllPath}" +
            (DemoPaths.RuntimeProfile is { } profile ? $" · GPU profile {profile}" : ""));
        Log(File.Exists(DemoPaths.VsrHostDllPath) && File.Exists(DemoPaths.VsrRuntimeDllPath)
            ? "RTX Video VSR: ready（可选 2×/4×）"
            : "RTX Video VSR: optional runtime missing（关闭选项仍可使用）");
        Log("FFmpeg: 进程内共享库（avformat/avcodec/avfilter），无外部 ffmpeg/ffprobe 子进程");
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _previewRenderTimer.Stop();
        AppSettingsStore.Save(CaptureSettings());
        _cancellationTokenSource?.Cancel();
        _previewBuffer?.Dispose();
        _sourceSurface.Dispose();
        _outputSurface.Dispose();
    }

    /// <summary>把控件状态写回设置模型（保存于开始转码与窗口关闭时）。</summary>
    private AppSettings CaptureSettings() => new()
    {
        EnableNr = EnableNrBox.IsChecked == true,
        NrRuntimeId = (NrRuntimeBox.SelectedItem as NrRuntimeInfo)?.Id ?? "",
        DlssStyle = StyleBox.SelectedIndex,
        DlssIntensity = IntensitySlider.Value,
        DlssLocalTone = LocalToneSlider.Value,
        DlssLocalStructure = LocalStructSlider.Value,
        DlssSkinStructure = SkinSlider.Value,
        DlssUseAutoMask = AutoMaskBox.IsChecked == true,
        DlssMotionGuidance = MotionGuidanceBox.IsChecked == true,
        DlssFineMotion = FineMotionBox.IsChecked == true,
        DlssContinuousHistory = ContinuousHistoryBox.IsChecked == true,
        SuperResolutionIndex = SuperResolutionBox.SelectedIndex,
        NrBeforeSuperResolution = NrBeforeSuperResolutionBox.IsChecked == true,
        EnableInterpolation = EnableInterpolationBox.IsChecked == true,
        InterpolationMultiplierIndex = InterpolationMultiplierBox.SelectedIndex,
        InterpolationBackendIndex = InterpolationBackendBox.SelectedIndex,
        ContainerIndex = ContainerBox.SelectedIndex,
        CodecIndex = CodecBox.SelectedIndex,
        HardwareIndex = HardwareBox.SelectedIndex,
        DecodeModeIndex = DecodeModeBox.SelectedIndex,
        EncoderPresetIndex = EncoderPresetBox.SelectedIndex,
        AudioIndex = AudioBox.SelectedIndex,
        Quality = (int)QualitySlider.Value,
        HdrIndex = HdrBox.SelectedIndex,
        FpsIndex = FpsBox.SelectedIndex,
        RateControlIndex = RateControlBox.SelectedIndex,
        VideoBitrateKbps = Int(VideoBitrateBox.Text),
        TwoPass = TwoPassBox.IsChecked == true,
        AudioBitrateKbps = Int(AudioBitrateBox.Text),
        AudioGainDb = Double(AudioGainBox.Text),
        ScaleIndex = ScaleBox.SelectedIndex,
        DenoiseIndex = DenoiseBox.SelectedIndex,
        DeinterlaceIndex = DeinterlaceBox.SelectedIndex,
        CropEnabled = CropBox.IsChecked == true,
        CropTop = Int(CropTopBox.Text),
        CropBottom = Int(CropBottomBox.Text),
        CropLeft = Int(CropLeftBox.Text),
        CropRight = Int(CropRightBox.Text),
        GifWidth = Int(GifWidthBox.Text),
        GifFps = Int(GifFpsBox.Text),
        GifMaxColors = Int(GifColorsBox.Text),
        GifDitherIndex = GifDitherBox.SelectedIndex,
        FramesLimit = FramesLimitBox.IsChecked == true,
    };

    /// <summary>恢复控件状态；索引夹到合法范围、Slider 由控件自身夹取，坏配置不致崩溃。</summary>
    private void ApplySettings()
    {
        EnableNrBox.IsChecked = _settings.EnableNr;
        Select(StyleBox, _settings.DlssStyle);
        IntensitySlider.Value = _settings.DlssIntensity;
        LocalToneSlider.Value = _settings.DlssLocalTone;
        LocalStructSlider.Value = _settings.DlssLocalStructure;
        SkinSlider.Value = _settings.DlssSkinStructure;
        AutoMaskBox.IsChecked = _settings.DlssUseAutoMask;
        MotionGuidanceBox.IsChecked = _settings.DlssMotionGuidance;
        FineMotionBox.IsChecked = _settings.DlssFineMotion;
        FineMotionBox.IsEnabled = MotionGuidanceBox.IsChecked == true;
        ContinuousHistoryBox.IsChecked = _settings.DlssContinuousHistory;
        Select(SuperResolutionBox, _settings.SuperResolutionIndex);
        NrBeforeSuperResolutionBox.IsChecked = _settings.NrBeforeSuperResolution;
        EnableInterpolationBox.IsChecked = _settings.EnableInterpolation;
        Select(InterpolationMultiplierBox, _settings.InterpolationMultiplierIndex);
        Select(InterpolationBackendBox, _settings.InterpolationBackendIndex);
        UpdateProcessingOrderHint();
        Select(ContainerBox, _settings.ContainerIndex);
        Select(CodecBox, _settings.CodecIndex);
        Select(HardwareBox, _settings.HardwareIndex);
        Select(DecodeModeBox, _settings.DecodeModeIndex);
        Select(EncoderPresetBox, _settings.EncoderPresetIndex);
        Select(AudioBox, _settings.AudioIndex);
        QualitySlider.Value = _settings.Quality;
        Select(HdrBox, _settings.HdrIndex);
        Select(FpsBox, _settings.FpsIndex);
        Select(RateControlBox, _settings.RateControlIndex);
        VideoBitrateBox.Text = _settings.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
        TwoPassBox.IsChecked = _settings.TwoPass;
        AudioBitrateBox.Text = _settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
        AudioGainBox.Text = _settings.AudioGainDb.ToString(CultureInfo.InvariantCulture);
        Select(ScaleBox, _settings.ScaleIndex);
        Select(DenoiseBox, _settings.DenoiseIndex);
        Select(DeinterlaceBox, _settings.DeinterlaceIndex);
        CropBox.IsChecked = _settings.CropEnabled;
        CropTopBox.Text = _settings.CropTop > 0 ? _settings.CropTop.ToString(CultureInfo.InvariantCulture) : "";
        CropBottomBox.Text = _settings.CropBottom > 0 ? _settings.CropBottom.ToString(CultureInfo.InvariantCulture) : "";
        CropLeftBox.Text = _settings.CropLeft > 0 ? _settings.CropLeft.ToString(CultureInfo.InvariantCulture) : "";
        CropRightBox.Text = _settings.CropRight > 0 ? _settings.CropRight.ToString(CultureInfo.InvariantCulture) : "";
        GifWidthBox.Text = _settings.GifWidth.ToString(CultureInfo.InvariantCulture);
        GifFpsBox.Text = _settings.GifFps.ToString(CultureInfo.InvariantCulture);
        GifColorsBox.Text = _settings.GifMaxColors.ToString(CultureInfo.InvariantCulture);
        Select(GifDitherBox, _settings.GifDitherIndex);
        FramesLimitBox.IsChecked = _settings.FramesLimit;
    }

    private static void Select(ComboBox box, int index)
        => box.SelectedIndex = box.ItemCount > 0 ? Math.Clamp(index, 0, box.ItemCount - 1) : -1;

    private void RefreshNrRuntimes_Click(object? sender, RoutedEventArgs e)
        => RefreshNrRuntimes((NrRuntimeBox.SelectedItem as NrRuntimeInfo)?.Id);

    private void RefreshNrRuntimes(string? selectedId)
    {
        var entries = new List<NrRuntimeInfo> { new("", DemoPaths.RuntimeDllPath, "自动 · 环境变量 / mods / 根目录激活版 / 按显卡代际匹配") };
        entries.AddRange(NrRuntimeCatalog.Discover());
        var selected = entries.Find(item => string.Equals(item.Id, selectedId ?? "", StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            selected = new NrRuntimeInfo(selectedId!, "", "已缺失 · " + selectedId);
            entries.Add(selected);
        }
        NrRuntimeBox.ItemsSource = entries;
        NrRuntimeBox.SelectedItem = selected;
        UpdateProcessingOrderHint();
    }

    private void RenderPreviewFrames(object? sender, EventArgs e)
    {
        if (_latestProgress.TakeLatest() is { } progress)
            ApplyProgress(progress);
        using var frame = _previewBuffer?.TakeLatest();
        if (frame != null)
        {
            _sourceSurface.Update(frame.Source, frame.Width, frame.Height);
            _outputSurface.Update(frame.Processed, frame.Width, frame.Height);
            SourcePlaceholder.IsVisible = false;
            OutputPlaceholder.IsVisible = false;
            var duration = _activeVideo?.DurationSeconds ?? 0;
            PreviewTimeText.Text = $"{TimeSpan.FromSeconds(frame.PositionSeconds):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(duration):hh\\:mm\\:ss}";
            PreviewProgress.Value = frame.TotalFrames > 0 ? Math.Clamp(frame.FramesDone * 100.0 / frame.TotalFrames, 0, 100) : 0;
            PreviewFrameText.Text = $"第 {frame.FramesDone} 帧 · 左右同步";
            _renderedPreviewFrames++;
            if (_previewRate.Elapsed.TotalSeconds >= 1)
            {
                PreviewFpsText.Text = $"预览 {_renderedPreviewFrames / _previewRate.Elapsed.TotalSeconds:0.0} fps";
                _renderedPreviewFrames = 0;
                _previewRate.Restart();
            }
        }
    }

    private void ApplyProgress(TranscodeProgress update)
    {
        Progress.Value = update.TotalFrames > 0 ? update.FramesDone * 100.0 / update.TotalFrames : 0;
        StatusText.Text = $"{update.FramesDone}/{update.TotalFrames} 帧 · {update.Fps:0.0} fps · {update.Elapsed.TotalSeconds:0.0}s";
        FramesText.Text = $"{update.FramesDone} 帧";
        SpeedText.Text = $"处理 {update.Fps:0.0} fps";
        _runningItem?.SetProgress(update.FramesDone, update.TotalFrames, update.Fps);
    }

    private void ResetPreview()
    {
        _previewBuffer?.Dispose();
        _previewBuffer = null;
        _sourceSurface.Dispose();
        _outputSurface.Dispose();
        _latestProgress.TakeLatest();
        SourcePlaceholder.IsVisible = true;
        OutputPlaceholder.IsVisible = true;
        PreviewProgress.Value = 0;
        PreviewTimeText.Text = "00:00:00 / 00:00:00";
        PreviewFrameText.Text = "等待转码";
        PreviewFpsText.Text = "预览 — fps";
        _renderedPreviewFrames = 0;
        _previewRate.Restart();
    }

    private static void AttachValueLabel(Slider slider, TextBlock label)
        => slider.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(Slider.Value)) label.Text = $"{slider.Value:0.0}×";
        };

    private void Log(string message)
    {
        Serilog.Log.Information("UI {Message}", message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Dispatcher.UIThread.Post(() =>
        {
            var existing = LogBox.Text ?? "";
            LogBox.Text = existing.Length > 50000 ? line : existing + (existing.Length == 0 ? "" : Environment.NewLine) + line;
        });
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".ts", ".webm",
    };

    /// <summary>接受的输入：视频 + 静态图（单帧 NR，输出 PNG/JPEG）。</summary>
    private static readonly HashSet<string> InputExtensions = [.. VideoExtensions, .. StillImage.Extensions];

    private static List<string> GetDroppedVideoPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return paths;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path) &&
                InputExtensions.Contains(Path.GetExtension(path)))
                paths.Add(path);
        }
        return paths;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = GetDroppedVideoPaths(e).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedVideoPaths(e);
        if (paths.Count == 0)
        {
            StatusText.Text = "请拖入视频或图片文件（MP4 / MKV / MOV / TS / WebM / PNG / JPG…）。";
            Log("拖入内容不是受支持的视频/图片文件。");
            e.Handled = true;
            return;
        }

        AddFiles(paths);
        StatusText.Text = $"已接收 {paths.Count} 个拖入文件。";
        e.Handled = true;
    }

    /// <summary>入队新文件：去重后异步探测规格；运行中也可追加（队列会顺序捡到）。</summary>
    private void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var firstAdded = (QueueItem?)null;
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            if (!File.Exists(path) || !InputExtensions.Contains(Path.GetExtension(path))) continue;
            if (_queue.Any(item => string.Equals(item.InputPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new QueueItem(path);
            _queue.Add(item);
            firstAdded ??= item;
            added++;
            ProbeItem(item);
        }
        if (added > 0)
        {
            Log($"已加入队列: {added} 个文件（共 {_queue.Count} 项）。");
            if (QueueList.SelectedItem is null) QueueList.SelectedItem = firstAdded;
        }
        UpdateQueueSummary();
    }

    private async void ProbeItem(QueueItem item)
    {
        try
        {
            var video = await Task.Run(() => MediaProbe.Probe(item.InputPath));
            if (_closed) return;
            item.SetProbe(video);
        }
        catch (Exception error)
        {
            if (_closed) return;
            item.SetProbeFailed(error.Message);
            Log($"探测失败 {item.FileName}: {error.Message}");
        }
        if (!_closed && QueueList.SelectedItem == item) ShowItemDetail();
    }

    /// <summary>队列摘要行 + 空态提示 + 清空按钮可用性。</summary>
    private void UpdateQueueSummary()
    {
        var pending = _queue.Count(item => item.State == QueueItemState.Waiting);
        var finished = _queue.Count(item => item.State is QueueItemState.Done or QueueItemState.Failed or QueueItemState.Cancelled or QueueItemState.Skipped);
        QueueSummaryText.Text = _queue.Count == 0
            ? "队列 0 项"
            : finished > 0 ? $"队列 {_queue.Count} 项 · 待处理 {pending} · 已结束 {finished}" : $"队列 {_queue.Count} 项 · 待处理 {pending}";
        QueueEmptyHint.IsVisible = _queue.Count == 0;
        ClearFinishedButton.IsEnabled = finished > 0;
    }

    private void QueueList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ShowItemDetail();

    /// <summary>详情卡显示选中队列项：探测规格、HDR 说明与输出路径（实际输出优先，其次手动指定）。</summary>
    private void ShowItemDetail()
    {
        var item = QueueList.SelectedItem as QueueItem;
        _updatingDetail = true;
        try
        {
            if (item is null)
            {
                InfoText.Text = "从队列中选择一项查看详情。";
                HdrSourceText.Text = "";
                OutputBox.Text = "";
                OpenVideoFolderButton.IsEnabled = false;
                return;
            }
            OpenVideoFolderButton.IsEnabled = File.Exists(item.InputPath);
            InfoText.Text = item.Detail;
            HdrSourceText.Text = item.Video?.IsHdr == true
                ? $"HDR 源 · {item.Video.ColorPrimaries} / {item.Video.ColorTransfer} / {item.Video.ColorSpace} · 将保留色彩元数据"
                : item.Video is null ? "" : "SDR 源 · 输出会使用 SDR 色彩标记";
            OutputBox.Text = item.ActualOutput ?? item.OutputOverride ?? "";
        }
        finally
        {
            _updatingDetail = false;
            if (OpenOutputFolderButton != null)
                OpenOutputFolderButton.IsEnabled = ExistingVideoDirectory(OutputBox.Text) != null;
        }
    }

    private void RemoveItem_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not QueueItem item) return;
        if (item.State == QueueItemState.Running)
        {
            Log($"正在处理，无法移除: {item.FileName}（可点「取消」停止队列）");
            return;
        }
        var selected = QueueList.SelectedItem == item;
        _queue.Remove(item);
        if (selected) QueueList.SelectedItem = null;
        UpdateQueueSummary();
    }

    private void ClearFinished_Click(object? sender, RoutedEventArgs e)
    {
        var finished = _queue.Where(item => item.State is QueueItemState.Done or QueueItemState.Failed or QueueItemState.Cancelled or QueueItemState.Skipped).ToList();
        var selectedRemoved = QueueList.SelectedItem is QueueItem selected && finished.Contains(selected);
        foreach (var item in finished) _queue.Remove(item);
        if (selectedRemoved) QueueList.SelectedItem = null;
        if (finished.Count > 0) Log($"已清空 {finished.Count} 个已结束的队列项。");
        UpdateQueueSummary();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择输入视频或图片（可多选）",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("媒体文件") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.m4v", "*.avi", "*.ts", "*.webm", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tif", "*.tiff"] },
                new FilePickerFileType("视频文件") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.m4v", "*.avi", "*.ts", "*.webm"] },
                new FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tif", "*.tiff"] },
            ],
        });
        AddFiles(files.Select(file => file.TryGetLocalPath()).Where(path => path is { Length: > 0 })!);
    }

    private void OnOutputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (OpenOutputFolderButton != null)
            OpenOutputFolderButton.IsEnabled = ExistingVideoDirectory(OutputBox.Text) != null;
        if (_updatingDetail) return;
        // 手动编辑输出路径 = 为选中项指定自定义输出；清空 = 恢复自动命名。运行中项的路径已定，不再生效。
        if (QueueList.SelectedItem is QueueItem { State: QueueItemState.Waiting } item)
            item.OutputOverride = string.IsNullOrWhiteSpace(OutputBox.Text) ? null : OutputBox.Text!.Trim().Trim('"');
    }

    private static string? ExistingVideoDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path.Trim().Trim('"')));
            return Directory.Exists(directory) ? directory : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private void OpenVideoFolder_Click(object? sender, RoutedEventArgs e) => OpenVideoDirectory((QueueList.SelectedItem as QueueItem)?.InputPath);
    private void OpenOutputFolder_Click(object? sender, RoutedEventArgs e) => OpenVideoDirectory(OutputBox.Text);

    private void OpenVideoDirectory(string? path)
    {
        try
        {
            var directory = ExistingVideoDirectory(path);
            if (directory == null) { Log("目录不存在，请检查输入路径。"); return; }
            var fullPath = Path.GetFullPath(path!.Trim().Trim('"'));
            if (File.Exists(fullPath))
            {
                // Unicode shell item IDs avoid Explorer's command-line parsing
                // ambiguities for filenames containing commas and brackets.
                Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, 0, out var item, 0, out _));
                try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0)); }
                finally { Marshal.FreeCoTaskMem(item); }
            }
            else Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or ArgumentException or NotSupportedException or COMException)
        { Log("无法打开目录：" + error.Message); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint item, uint count, nint children, uint flags);

    private async void PickOutput_Click(object? sender, RoutedEventArgs e)
    {
        var selectedItem = QueueList.SelectedItem as QueueItem;
        var suggestedExtension = selectedItem is { } candidate && candidate.IsImage ? ".png"
            : ContainerExtension(CollectVideoOptions().Container);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "选择输出文件",
            ShowOverwritePrompt = false,
            SuggestedFileName = selectedItem is { } item && File.Exists(item.InputPath)
                ? Path.GetFileName(OutputFileNames.ForInput(item.InputPath, suggestedExtension))
                : string.IsNullOrWhiteSpace(OutputBox.Text) ? "dlss5-output.mp4" : Path.GetFileName(OutputBox.Text),
            FileTypeChoices =
            [
                new FilePickerFileType("MP4 视频") { Patterns = ["*.mp4"] },
                new FilePickerFileType("MKV 视频") { Patterns = ["*.mkv"] },
                new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG 图片") { Patterns = ["*.jpg"] },
            ],
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path) OutputBox.Text = OutputFileNames.Available(path);
    }

    private DlssNrOptions CollectDlssOptions()
    {
        return new DlssNrOptions
        {
            Style = StyleBox.SelectedIndex,
            Intensity = (float)IntensitySlider.Value,
            LocalTone = (float)LocalToneSlider.Value,
            LocalStructure = (float)LocalStructSlider.Value,
            SkinStructure = (float)SkinSlider.Value,
            UseAutoMask = AutoMaskBox.IsChecked == true,
            MotionGuidance = MotionGuidanceBox.IsChecked == true,
            FineMotion = FineMotionBox.IsChecked == true,
            ContinuousHistory = ContinuousHistoryBox.IsChecked == true,
            TemporalStabilization = ContinuousHistoryBox.IsChecked == true,
        };
    }

    private VideoTranscodeOptions CollectVideoOptions()
    {
        var container = (VideoContainer)Math.Clamp(ContainerBox.SelectedIndex, 0, 4);
        var codec = container == VideoContainer.Gif ? VideoCodec.Gif : (VideoCodec)Math.Clamp(CodecBox.SelectedIndex, 0, 4);
        var hardware = (HardwareBackend)Math.Clamp(HardwareBox.SelectedIndex, 0, 3);
        var audio = container == VideoContainer.Gif ? AudioCodec.None : (AudioCodec)Math.Clamp(AudioBox.SelectedIndex, 0, 7);
        var preset = EncoderPresetBox.SelectedIndex switch { 0 => "p1", 1 => "p3", 3 => "p7", _ => "p5" };
        var fpsMode = EnableInterpolationBox.IsChecked == true || FpsBox.SelectedIndex == 0 ? FpsMode.SameAsSource : FpsMode.Fixed;
        var fps = FpsBox.SelectedIndex switch { 1 => 24, 2 => 30, 3 => 60, _ => 30 };
        var scale = ScaleBox.SelectedIndex switch { 1 => (ScaleMode.FitWithin, 1920, 1080), 2 => (ScaleMode.FitWithin, 2560, 1440), 3 => (ScaleMode.FitWithin, 3840, 2160), _ => (ScaleMode.None, 0, 0) };
        var denoise = (DenoiseMode)Math.Clamp(DenoiseBox.SelectedIndex, 0, 3);
        var gifDither = GifDitherBox.SelectedItem is ComboBoxItem gifItem ? gifItem.Tag?.ToString() ?? "bayer" : "bayer";
        return new VideoTranscodeOptions
        {
            Container = container, VideoCodec = codec, HardwareBackend = hardware, AudioCodec = audio,
            RateControl = RateControlBox.SelectedIndex == 1 ? RateControlMode.Bitrate : RateControlMode.Quality,
            Quality = (int)QualitySlider.Value, VideoBitrateKbps = Int(VideoBitrateBox.Text),
            AudioBitrateKbps = Math.Clamp(Int(AudioBitrateBox.Text), 32, 512), AudioGainDb = Double(AudioGainBox.Text),
            TwoPassEnabled = TwoPassBox.IsChecked == true && RateControlBox.SelectedIndex == 1 && hardware == HardwareBackend.Software,
            EncoderPreset = preset, FpsMode = fpsMode, FpsValue = fps,
            ScaleMode = scale.Item1, ScaleWidth = scale.Item2, ScaleHeight = scale.Item3, Denoise = denoise,
            Deinterlace = (DeinterlaceMode)Math.Clamp(DeinterlaceBox.SelectedIndex, 0, 2),
            CropEnabled = CropBox.IsChecked == true, CropTop = Int(CropTopBox.Text), CropBottom = Int(CropBottomBox.Text),
            CropLeft = Int(CropLeftBox.Text), CropRight = Int(CropRightBox.Text), PreserveHdr = HdrBox.SelectedIndex != 2,
            ForceHdr = HdrBox.SelectedIndex == 1, GifWidth = Math.Clamp(Int(GifWidthBox.Text), 64, 800),
            GifFps = Math.Clamp(Int(GifFpsBox.Text), 1, 30), GifMaxColors = Math.Clamp(Int(GifColorsBox.Text), 32, 256),
            GifDither = gifDither,
        };
    }

    private void UpdateProcessingOrderHint()
    {
        var enabled = CollectSuperResolutionScale() > 1;
        var nrEnabled = EnableNrBox.IsChecked == true;
        NrSettings.IsEnabled = nrEnabled;
        var interpolate = EnableInterpolationBox.IsChecked == true;
        InterpolationMultiplierBox.IsEnabled = interpolate;
        InterpolationBackendBox.IsEnabled = interpolate;
        FpsBox.IsEnabled = !interpolate;
        GifFpsBox.IsEnabled = !interpolate;
        NrBeforeSuperResolutionBox.IsEnabled = enabled && nrEnabled;
        var runtime = NrRuntimeBox.SelectedItem as NrRuntimeInfo;
        bool runtimeExists = File.Exists(runtime?.DllPath ?? DemoPaths.RuntimeDllPath);
        RuntimeBadge.Text = !nrEnabled ? "DLSS NR · 已关闭"
            : runtimeExists ? "DLSS Runtime · ready" : "DLSS Runtime · missing";
        NrRuntimeHint.Text = runtimeExists
            ? "「自动」优先用根目录激活版；未放置时按本机 RTX 代际（20-50 系）匹配 runtime\\nr\\ 下的版本。完整转码建议把适配版本放到 runtime 根目录。"
            : "所选 NR DLL 不存在。请放回文件后刷新，或选择其他版本。";
        ProcessingOrderHint.Text = !nrEnabled ? enabled ? "仅 RTX Video 超分 → 编码。NR、光流和历史累积已跳过。"
                : "NR 与超分均已关闭：直接转码，可使用下方输出滤镜。"
            : !enabled ? "超分已关闭：仅处理 DLSS NR。"
            : NrBeforeSuperResolutionBox.IsChecked == true
                ? "NR → 超分 → 编码。NR 在源分辨率处理，速度更快。"
                : "超分 → NR → 编码。NR 在放大后的分辨率处理，可对照更细的视觉效果，耗时增加。";
        if (interpolate) ProcessingOrderHint.Text += InterpolationBackendBox.SelectedIndex > 0 ? " 编码前启用 RIFE GPU 补帧（仅 SDR）。" : " 编码前启用 NVIDIA 光流补帧。";
        if (HdrBox.SelectedIndex == 1)
            ProcessingOrderHint.Text += interpolate && InterpolationBackendBox.SelectedIndex > 0
                ? " RIFE 保持 SDR 输出，跳过强制 HDR 映射。"
                : " SDR 源在补帧后通过驱动 RTX Video HDR 映射为 HDR10；HDR 源沿用原路径。";
    }

    private int CollectSuperResolutionScale()
    {
        if (SuperResolutionBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var scale))
            return RtxVideoSuperResolutionHost.NormalizeScale(scale);
        return 1;
    }

    private static int Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;
    private static double Double(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, -30, 30) : 0;

    private static string ContainerExtension(VideoContainer container) => container switch
    {
        VideoContainer.Mkv => ".mkv", VideoContainer.Mov => ".mov", VideoContainer.Avi => ".avi",
        VideoContainer.Gif => ".gif", _ => ".mp4",
    };

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (!_queue.Any(item => item.State == QueueItemState.Waiting))
        {
            StatusText.Text = _queue.Count == 0 ? "队列为空，请先添加视频或图片。" : "没有待处理项（已结束的项不会重跑）。";
            return;
        }
        var videoOptions = CollectVideoOptions();
        string runtimeDllPath;
        try { runtimeDllPath = EnableNrBox.IsChecked == true ? NrRuntimeCatalog.Resolve((NrRuntimeBox.SelectedItem as NrRuntimeInfo)?.Id) : ""; }
        catch (FileNotFoundException error)
        {
            StatusText.Text = error.Message;
            RefreshNrRuntimes((NrRuntimeBox.SelectedItem as NrRuntimeInfo)?.Id);
            return;
        }

        // NGX 宿主是进程级单例状态：必须等上一任务完全清理（shutdown + ffmpeg 回收）
        // 后才能开新会话，否则并发 init/shutdown 会导致新任务速度异常甚至闪退。
        if (_pipelineTask is { } previous && !previous.IsCompleted)
        {
            _cancellationTokenSource?.Cancel();
            StatusText.Text = "正在停止上一任务…";
            Log("等待上一任务清理完成…");
            try { await previous; } catch { }
        }
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _pipelineTask = null;
        var generation = ++_runGeneration;

        // 队列开始即快照全部设置：整批共用同一配置，运行中改动控件不影响本批。
        var options = new TranscodeOptions
        {
            HostDllPath = DemoPaths.HostDllPath,
            RuntimeDllPath = runtimeDllPath,
            LogPath = DemoPaths.LogPath,
            EnableNr = EnableNrBox.IsChecked == true,
            DecodeMode = (VideoDecodeMode)Math.Clamp(DecodeModeBox.SelectedIndex, 0, 2),
            EnableInterpolation = EnableInterpolationBox.IsChecked == true,
            InterpolationMultiplier = InterpolationMultiplierBox.SelectedIndex switch { 1 => 4, 2 => 6, _ => 2 },
            InterpolationBackend = InterpolationBackendBox.SelectedIndex > 0 ? FrameInterpolationBackend.Rife : FrameInterpolationBackend.NvidiaOpticalFlow,
            InterpolationModel = InterpolationBackendBox.SelectedIndex == 2 ? RifeModel.Full : RifeModel.Lite,
            VsrHostDllPath = DemoPaths.VsrHostDllPath, VsrRuntimeDllPath = DemoPaths.VsrRuntimeDllPath, VsrLogPath = DemoPaths.VsrLogPath,
            SuperResolutionScale = CollectSuperResolutionScale(), SuperResolutionQuality = 4,
            NrBeforeSuperResolution = NrBeforeSuperResolutionBox.IsChecked == true,
            Dlss = CollectDlssOptions(), Video = videoOptions, VideoEncoder = VideoCodecNames.Encoder(videoOptions),
            Quality = (int)QualitySlider.Value, MaxFrames = FramesLimitBox.IsChecked == true ? 60 : null,
            CopyAudio = videoOptions.AudioCodec != AudioCodec.None, EnablePreviewFrames = true,
            PreviewFramesPerSecond = 60,
        };
        // 转码定参即落盘：之后即使闪退，配置也不丢（窗口关闭时还会再存一次）。
        AppSettingsStore.Save(CaptureSettings());
        var orderLabel = !options.EnableNr ? options.SuperResolutionScale <= 1 ? "直接转码" : $"RTX Video {options.SuperResolutionScale}×"
            : options.SuperResolutionScale <= 1 ? "DLSS NR"
            : options.NrBeforeSuperResolution ? $"DLSS NR → RTX Video {options.SuperResolutionScale}×"
            : $"RTX Video {options.SuperResolutionScale}× → DLSS NR";
        if (options.EnableInterpolation) orderLabel += $" → {options.InterpolationBackend} 补帧 {options.InterpolationMultiplier}×";
        var pendingCount = _queue.Count(item => item.State == QueueItemState.Waiting);
        Log($"开始队列: {pendingCount} 个待处理文件 · 设置按当前快照对整批生效 · {orderLabel} · {options.VideoEncoder}");
        if (options.EnableNr)
            Log($"光流运动矢量: {(options.Dlss.MotionGuidance ? "开启" : "关闭")} · NR 连续历史: {(options.Dlss.ContinuousHistory ? "开启" : "关闭")}");

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        StartButton.IsEnabled = false; CancelButton.IsEnabled = true; CompareButton.IsEnabled = false; Progress.Value = 0;
        _pipelineTask = RunQueueAsync(options, orderLabel, generation, token);
        try { await _pipelineTask; }
        catch { /* 队列异常已在 RunQueueAsync 内按项消化 */ }
        finally
        {
            if (generation == _runGeneration) { StartButton.IsEnabled = true; CancelButton.IsEnabled = false; }
        }
    }

    /// <summary>批量队列主循环：顺序处理 Waiting 项（Core 的 PipelineGate 保证全局串行），
    /// 单项失败标记后继续下一项；取消则当前项中止、剩余标「已跳过」。运行中新入队项会被顺序捡到。</summary>
    private async Task RunQueueAsync(TranscodeOptions batchOptions, string orderLabel, int generation, CancellationToken token)
    {
        var extension = ContainerExtension(batchOptions.Video.Container);
        int done = 0, failed = 0, skipped = 0, index = 0;
        while (!token.IsCancellationRequested)
        {
            var item = _queue.FirstOrDefault(entry => entry.State == QueueItemState.Waiting);
            if (item is null) break;
            index++;

            string output;
            try
            {
                var itemExtension = item.IsImage ? ".png" : extension;
                output = !string.IsNullOrWhiteSpace(item.OutputOverride)
                    ? item.OutputOverride!.Trim().Trim('"')
                    : OutputFileNames.ForInput(item.InputPath, itemExtension);
                if (Path.GetExtension(output).Length == 0) output = Path.ChangeExtension(output, itemExtension);
                output = OutputFileNames.Available(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                item.SetFailed("输出路径无效: " + error.Message);
                failed++;
                Log($"失败 ({index}): {item.FileName} · 输出路径无效: {error.Message.Split('\n')[0]}");
                UpdateQueueSummary();
                continue;
            }

            item.SetRunning(output);
            _runningItem = item;
            _activeVideo = item.Video;
            ResetPreview();
            _previewBuffer = new TranscodePreviewBuffer(960, 540);
            if (generation == _runGeneration)
            {
                Progress.Value = 0;
                PipelineStatus.Text = $"{orderLabel} 正在处理帧…";
                LivePreviewText.Text = "SYNC · 源帧与输出帧同步";
                var remaining = _queue.Count(entry => entry.State == QueueItemState.Waiting);
                QueueStatusText.Text = $"队列进行中 · 第 {index} 项 · 待处理 {remaining} · 成功 {done} · 失败 {failed}";
                if (QueueList.SelectedItem == item) ShowItemDetail();
            }
            Log($"开始转码 ({index}): {item.FileName} → {Path.GetFileName(output)} · {batchOptions.VideoEncoder} · {orderLabel}");
            try
            {
                var progress = new Progress<TranscodeProgress>(update => _latestProgress.Publish(update));
                var result = await DlssVideoPipeline.RunAsync(item.InputPath, output, batchOptions with { PreviewSink = _previewBuffer }, progress, token);
                _lastOutputPath = result.OutputPath;
                item.SetDone(result, batchOptions.EnableInterpolation, batchOptions.InterpolationMultiplier);
                done++;
                if (generation == _runGeneration)
                {
                    if (QueueList.SelectedItem == item) ShowItemDetail();
                    Progress.Value = 100;
                    CompareButton.IsEnabled = File.Exists(result.OutputPath);
                    OutputText.Text = $"{result.OutputBytes / 1048576.0:0.0} MiB";
                    StatusText.Text = $"完成 · {item.FileName} · {result.AverageFps:0.0} fps";
                    LivePreviewText.Text = "完成 · 停留在最后一帧";
                }
                Log($"完成 ({index}): {item.FileName} · {result.FramesProcessed} 帧 · {result.AverageFps:0.0} fps · {result.OutputBytes / 1048576.0:0.0} MiB");
            }
            catch (OperationCanceledException)
            {
                item.SetCancelled();
                Log($"已取消: {item.FileName} · 队列停止");
                break;
            }
            catch (Exception error)
            {
                item.SetFailed(error.Message);
                failed++;
                Log($"失败 ({index}): {item.FileName} · {error.Message.Split('\n')[0]}");
            }
            finally
            {
                _runningItem = null;
                UpdateQueueSummary();
            }
        }

        foreach (var pending in _queue.Where(entry => entry.State == QueueItemState.Waiting).ToList())
        {
            pending.SetSkipped();
            skipped++;
            Log($"已跳过: {pending.FileName}");
        }
        UpdateQueueSummary();
        if (generation == _runGeneration)
        {
            QueueStatusText.Text = $"队列结束 · 成功 {done} · 失败 {failed} · 跳过 {skipped}";
            PipelineStatus.Text = failed == 0 && skipped == 0 && done > 0 ? "队列完成 · 全部成功"
                : done + failed + skipped == 0 ? "管线就绪" : $"队列结束 · 成功 {done} / 失败 {failed} / 跳过 {skipped}";
        }
        Log($"队列结束: 成功 {done} · 失败 {failed} · 跳过 {skipped}");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) { _cancellationTokenSource?.Cancel(); StatusText.Text = "正在取消…"; }

    private async void Compare_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastOutputPath is { } output && File.Exists(output))
        {
            LivePreviewText.Text = "输出已载入 · 预览保持与转码最后一帧同步";
            Log("输出已载入；双路画面保持在最近一次转码帧。");
        }
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        LivePreviewText.Text = "请先开始转码；SOURCE 将按处理帧同步显示";
    }

    private void Pause_Click(object? sender, RoutedEventArgs e) { LivePreviewText.Text = "双路预览跟随转码进度"; }

    private void Replay_Click(object? sender, RoutedEventArgs e)
    {
        ResetPreview();
        LivePreviewText.Text = "已清空预览，开始转码后将同步刷新";
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e) => LogBox.Text = "";

    private sealed class LatestProgress
    {
        private readonly object _gate = new();
        private TranscodeProgress? _latest;

        public void Publish(TranscodeProgress progress)
        {
            lock (_gate) _latest = progress;
        }

        public TranscodeProgress? TakeLatest()
        {
            lock (_gate)
            {
                var progress = _latest;
                _latest = null;
                return progress;
            }
        }
    }

    private sealed class PreviewSurface : IDisposable
    {
        private readonly Image _image;
        private WriteableBitmap? _bitmap;
        private int _width;
        private int _height;

        public PreviewSurface(Image image) => _image = image;

        public unsafe void Update(ReadOnlySpan<byte> data, int width, int height)
        {
            if (width <= 0 || height <= 0 || data.Length < checked(width * height * 4)) return;
            if (_bitmap is null || _width != width || _height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
                _width = width;
                _height = height;
                _image.Source = _bitmap;
            }
            using var locked = _bitmap.Lock();
            var sourceStride = width * 4;
            for (var y = 0; y < height; y++)
                data.Slice(y * sourceStride, sourceStride)
                    .CopyTo(new Span<byte>((void*)IntPtr.Add(locked.Address, y * locked.RowBytes), sourceStride));
            _image.InvalidateVisual();
        }

        public void Dispose()
        {
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}

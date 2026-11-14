using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AITranslate.Core.Configuration;
using AITranslate.Core.Translation;
using AITranslate.Core.Translation.Concurrency;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITranslate.WPF.ViewModels;

/// <summary>单个文件页签的翻译状态。</summary>
public enum TranslateState
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// 一个文件页签的视图模型：承载文件信息、流式预览、进度与日志，并驱动一个翻译任务。
/// </summary>
public sealed partial class TranslateTabViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueue<string> _deltaQueue = new();

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private TranslateState _state = TranslateState.Pending;

    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string? _referencePath;

    public TranslateTabViewModel(string filePath, GameProfile profile)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Profile = profile;
        OutputPath = DeriveOutputPath(filePath, profile);

        _dispatcher = Application.Current.Dispatcher;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, FlushDeltas, _dispatcher);
        _flushTimer.Start();
    }

    public string FilePath { get; }
    public string FileName { get; }

    /// <summary>本页签所属的翻译目标（创建后不再变化——切换游戏会清空页签）。</summary>
    public GameProfile Profile { get; }

    /// <summary>是否需要「参考文件」这一行（仅 BG3 需要）。</summary>
    public bool SupportsReferenceFile => Profile.SupportsReferenceFile;

    public ObservableCollection<string> Logs { get; } = new();

    public bool IsBusy => State == TranslateState.Running;
    public bool HasError => State == TranslateState.Failed;

    public string StateText => State switch
    {
        TranslateState.Pending => "就绪",
        TranslateState.Running => "翻译中",
        TranslateState.Completed => "已完成",
        TranslateState.Failed => "失败",
        TranslateState.Canceled => "已取消",
        _ => string.Empty,
    };

    /// <summary>启动（或重新）翻译。引擎由本页签的翻译目标决定。</summary>
    public async Task StartAsync(ChatCompletionClient client, GlobalRateLimiter limiter)
    {
        if (State == TranslateState.Running)
        {
            return;
        }

        ResetForRun();
        State = TranslateState.Running;
        _cts = new CancellationTokenSource();

        var engine = TranslationEngineFactory.Create(Profile.Kind, client, limiter);
        var request = new TranslateRequest(FilePath, OutputPath, ReferencePath);
        var progress = new ProgressSink(this);

        try
        {
            await Task.Run(() => engine.TranslateAsync(request, progress, _cts.Token)).ConfigureAwait(true);
            // 正常返回：引擎已上报 FileCompleted；若因某种原因未上报，此处兜底
            if (State == TranslateState.Running)
            {
                State = TranslateState.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            State = TranslateState.Canceled;
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            State = TranslateState.Failed;
            AppendLog("错误: " + ex.Message);
        }
        finally
        {
            FlushDeltas(null, EventArgs.Empty);
            engine.Dispose();
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>取消当前翻译（命令入口）。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => RequestCancel();

    /// <summary>请求取消，供外部（如主视图模型）直接调用。</summary>
    public void RequestCancel() => _cts?.Cancel();

    private bool CanCancel() => State == TranslateState.Running;

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        try
        {
            string file = OutputPath;
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
            }
        }
        catch
        {
            // 忽略
        }
    }

    private bool CanOpenOutputFolder() => !string.IsNullOrEmpty(OutputPath);

    private void ResetForRun()
    {
        Percent = 0;
        PreviewText = string.Empty;
        Logs.Clear();
        while (_deltaQueue.TryDequeue(out _)) { }
    }

    /// <summary>进度回调入口（跨线程）。</summary>
    private void OnProgress(TranslationProgressItem item)
    {
        switch (item.Stage)
        {
            // 两类都是「往预览里追加文本」：OutputLine 是已定稿的输出文件行，ChunkReceived 是模型原始增量。
            // 频率高，只入队，由 DispatcherTimer 批量刷到 UI。
            case TranslationStage.OutputLine:
            case TranslationStage.ChunkReceived:
                _deltaQueue.Enqueue(item.Text);
                break;

            default:
                _dispatcher.BeginInvoke(new Action(() => ApplyState(item)));
                break;
        }
    }

    /// <summary>在 UI 线程上应用非流式进度。</summary>
    private void ApplyState(TranslationProgressItem item)
    {
        switch (item.Stage)
        {
            case TranslationStage.FileStarted:
                AppendLog("▶ " + (item.Message ?? "开始翻译"));
                break;

            case TranslationStage.BatchCompleted:
                if (item.Percent >= 0 && item.Percent <= 100)
                {
                    Percent = item.Percent;
                }
                if (!string.IsNullOrEmpty(item.Message))
                {
                    AppendLog(item.Message);
                }
                break;

            case TranslationStage.FileCompleted:
                Percent = 100;
                if (!string.IsNullOrEmpty(item.Message))
                {
                    AppendLog(item.Message);
                }
                break;

            case TranslationStage.Error:
                AppendLog("⚠ " + (item.Message ?? "未知错误"));
                break;

            case TranslationStage.Status:
                if (!string.IsNullOrEmpty(item.Message))
                {
                    AppendLog(item.Message);
                }
                break;
        }
    }

    /// <summary>把排队的流式增量合并到预览文本（UI 线程，由 DispatcherTimer 调度）。</summary>
    private void FlushDeltas(object? sender, EventArgs e)
    {
        if (_deltaQueue.IsEmpty)
        {
            return;
        }

        var sb = new System.Text.StringBuilder(PreviewText);
        while (_deltaQueue.TryDequeue(out var delta))
        {
            sb.Append(delta);
        }
        PreviewText = sb.ToString();
    }

    private void AppendLog(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        // 限制日志长度，避免大文件翻译时内存无限增长
        while (Logs.Count > 1000)
        {
            Logs.RemoveAt(0);
        }
    }

    private static string DeriveOutputPath(string inputPath, GameProfile profile)
    {
        string? dir = Path.GetDirectoryName(inputPath);
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string outputName = name + profile.OutputSuffix;
        return string.IsNullOrEmpty(dir) ? outputName : Path.Combine(dir, outputName);
    }

    /// <summary>自定义 IProgress 实现：流式增量入队，其余状态切回 UI 线程处理。</summary>
    private sealed class ProgressSink : IProgress<TranslationProgressItem>
    {
        private readonly TranslateTabViewModel _owner;

        public ProgressSink(TranslateTabViewModel owner) => _owner = owner;

        public void Report(TranslationProgressItem value) => _owner.OnProgress(value);
    }
}

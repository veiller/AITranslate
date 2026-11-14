using System.Collections.ObjectModel;
using System.IO;
using AITranslate.Core.Configuration;
using AITranslate.Core.Translation;
using AITranslate.Core.Translation.Concurrency;
using AITranslate.WPF.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITranslate.WPF.ViewModels;

/// <summary>
/// 主窗口视图模型：管理多页签、拖拽接入、并发启停与全局状态。
/// <para>「翻译目标」由设置决定，切换时会清空已加入的页签（不同游戏的引擎与输出后缀不同）。</para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialog;

    private ChatCompletionClient? _client;
    private GlobalRateLimiter? _limiter;
    private int _limiterCapacity = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSummary))]
    [NotifyPropertyChangedFor(nameof(GameDisplayName))]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    [NotifyPropertyChangedFor(nameof(DropHint))]
    private AppSettings _settings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickReferenceCommand))]
    private TranslateTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public MainViewModel(AppSettings settings, IDialogService dialog)
    {
        Settings = settings;
        _dialog = dialog;

        Tabs.CollectionChanged += (_, _) => RefreshCommandStates();
    }

    public ObservableCollection<TranslateTabViewModel> Tabs { get; } = new();

    /// <summary>当前翻译目标的显示名。</summary>
    public string GameDisplayName => Settings.Profile.DisplayName;

    /// <summary>空态提示（随翻译目标变化）。</summary>
    public string EmptyHint => $"把 {Settings.Profile.FileExtension} 文件或文件夹拖到这里";

    /// <summary>拖拽区副标题。</summary>
    public string DropHint => $"当前目标：{Settings.Profile.DisplayName}（可在「设置」中切换）";

    public string SettingsSummary =>
        string.IsNullOrWhiteSpace(Settings.Model) ? "未配置模型" : $"{Settings.Model} @ {Settings.BaseUrl}";

    public bool HasTabs => Tabs.Count > 0;
    public bool HasStartable => Tabs.Any(t => t.State is TranslateState.Pending or TranslateState.Failed or TranslateState.Canceled);
    public bool HasRunning => Tabs.Any(t => t.IsBusy);
    public int ActiveCount => Tabs.Count(t => t.IsBusy);
    public int CompletedCount => Tabs.Count(t => t.State == TranslateState.Completed);

    /// <summary>拖拽 / 粘贴接入的路径（文件或文件夹），只接受当前游戏的文件类型。</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateSupportedFiles(path))
                {
                    if (AddFile(file)) added++; else skipped++;
                }
            }
            else if (File.Exists(path))
            {
                if (AddFile(path)) added++; else skipped++;
            }
            else
            {
                skipped++;
            }
        }

        RefreshCommandStates();
        string expected = Settings.Profile.FileExtension;
        StatusMessage = added > 0
            ? $"已加入 {added} 个文件" + (skipped > 0 ? $"，跳过 {skipped} 个（非 {expected} 或重复）" : string.Empty)
            : $"没有可加入的文件：当前翻译目标是 {Settings.Profile.DisplayName}，只接受 {expected} 文件";
    }

    /// <summary>停止所有任务并释放资源。</summary>
    public void Shutdown()
    {
        StopAll();
        _client?.Dispose();
        _limiter?.Dispose();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var result = _dialog.ShowSettings(Settings);
        if (result is null)
        {
            return;
        }

        bool gameChanged = result.Game != Settings.Game;
        if (gameChanged && HasTabs)
        {
            if (_dialog.Confirm(
                    $"切换翻译目标会清空当前已加入的 {Tabs.Count} 个文件（不同游戏的引擎与输出后缀不同）。是否继续？"))
            {
                Tabs.Clear();
                SelectedTab = null;
            }
            else
            {
                // 用户放弃切换：保留原翻译目标，但仍应用 AI 参数改动
                result.Game = Settings.Game;
                gameChanged = false;
            }
        }

        Settings = result;
        SettingsService.Save(result);

        // 配置变化后重建客户端，下次翻译生效
        if (!HasRunning)
        {
            _client?.Dispose();
            _client = null;
            _limiter?.Dispose();
            _limiter = null;
            _limiterCapacity = -1;
        }

        StatusMessage = gameChanged
            ? $"翻译目标已切换为 {Settings.Profile.DisplayName}，请加入 {Settings.Profile.FileExtension} 文件"
            : "设置已保存";
    }

    [RelayCommand]
    private void AddFiles()
    {
        var files = _dialog.PickFiles(Settings.Profile);
        if (files is { Length: > 0 })
        {
            AddPaths(files);
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        var folder = _dialog.PickFolder();
        if (!string.IsNullOrEmpty(folder))
        {
            AddPaths(new[] { folder });
        }
    }

    /// <summary>启动所有可启动的页签，受 <see cref="TranslationDefaults.MaxConcurrentFiles"/> 并发限制。</summary>
    [RelayCommand(CanExecute = nameof(CanStartAll))]
    private async Task StartAllAsync()
    {
        if (!IsConfigured())
        {
            return;
        }

        var pending = Tabs
            .Where(t => t.State is TranslateState.Pending or TranslateState.Failed or TranslateState.Canceled)
            .ToList();

        if (pending.Count == 0)
        {
            StatusMessage = "没有需要翻译的文件";
            return;
        }

        EnsureClient();

        StatusMessage = $"正在翻译 {pending.Count} 个文件（并发上限 {TranslationDefaults.MaxConcurrentFiles}）…";
        RefreshCommandStates();

        try
        {
            await Task.WhenAll(pending.Select(t => t.StartAsync(_client!, _limiter!)));
        }
        finally
        {
            RefreshCommandStates();
            int completed = CompletedCount;
            int failed = Tabs.Count(t => t.State == TranslateState.Failed);
            StatusMessage = failed > 0
                ? $"完成 {completed} 个，失败 {failed} 个"
                : $"全部完成，共 {completed} 个文件";
        }
    }

    private bool CanStartAll() => HasStartable;

    /// <summary>启动单个页签。</summary>
    [RelayCommand]
    private async Task StartTabAsync(TranslateTabViewModel? tab)
    {
        if (tab is null || tab.IsBusy || !IsConfigured())
        {
            return;
        }

        EnsureClient();
        StatusMessage = $"正在翻译 {tab.FileName}…";
        RefreshCommandStates();
        try
        {
            await tab.StartAsync(_client!, _limiter!);
        }
        finally
        {
            RefreshCommandStates();
        }
    }

    /// <summary>停止所有正在运行的页签。</summary>
    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private void StopAll()
    {
        foreach (var tab in Tabs.Where(t => t.IsBusy).ToList())
        {
            tab.RequestCancel();
        }
        StatusMessage = "已请求停止";
    }

    private bool CanStopAll() => HasRunning;

    [RelayCommand]
    private void RemoveTab(TranslateTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.RequestCancel();
        Tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs.FirstOrDefault();
        }
        RefreshCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        RemoveTab(SelectedTab);
    }

    private bool CanRemoveSelected() => SelectedTab != null;

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished()
    {
        foreach (var tab in Tabs.Where(t => t.State is TranslateState.Completed or TranslateState.Failed or TranslateState.Canceled).ToList())
        {
            Tabs.Remove(tab);
        }
        SelectedTab = Tabs.FirstOrDefault();
        RefreshCommandStates();
    }

    private bool CanClearFinished() => Tabs.Any(t => !t.IsBusy);

    [RelayCommand(CanExecute = nameof(CanPickReference))]
    private void PickReference()
    {
        if (SelectedTab is not { SupportsReferenceFile: true } tab)
        {
            return;
        }

        var file = _dialog.PickReferenceFile();
        if (!string.IsNullOrEmpty(file))
        {
            tab.ReferencePath = file;
        }
    }

    private bool CanPickReference() => SelectedTab is { SupportsReferenceFile: true };

    [RelayCommand]
    private void MoveLeft(TranslateTabViewModel? tab)
    {
        if (tab is not null) MoveTab(tab, -1);
    }

    [RelayCommand]
    private void MoveRight(TranslateTabViewModel? tab)
    {
        if (tab is not null) MoveTab(tab, 1);
    }

    private void MoveTab(TranslateTabViewModel tab, int delta)
    {
        int index = Tabs.IndexOf(tab);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Tabs.Count)
        {
            return;
        }
        Tabs.Move(index, target);
        SelectedTab = tab;
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasStartable));
        OnPropertyChanged(nameof(HasRunning));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        StartAllCommand.NotifyCanExecuteChanged();
        StopAllCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>加入单个文件，返回是否成功（非当前游戏的文件、或已存在则返回 false）。</summary>
    private bool AddFile(string filePath)
    {
        if (!TranslationEngineFactory.IsSupported(Settings.Game, filePath))
        {
            return false;
        }

        if (Tabs.Any(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var tab = new TranslateTabViewModel(filePath, Settings.Profile);
        Tabs.Add(tab);
        SelectedTab = tab;
        return true;
    }

    private void EnsureClient()
    {
        _client ??= new ChatCompletionClient(Settings.ToAiSettings());

        int capacity = Math.Max(1, TranslationDefaults.MaxConcurrentFiles);
        if (_limiter is null || _limiterCapacity != capacity)
        {
            // 仅在没有运行任务时重建限流器
            if (_limiter is null || !HasRunning)
            {
                _limiter?.Dispose();
                _limiter = new GlobalRateLimiter(capacity);
                _limiterCapacity = capacity;
            }
        }
    }

    /// <summary>校验 BaseUrl / 模型是否已配置，未配置则提示并返回 false。</summary>
    private bool IsConfigured()
    {
        if (!Settings.IsConfigured)
        {
            StatusMessage = "请先在「设置」中填写接口地址与模型";
            _dialog.ShowMessage("请先在「设置」中填写接口地址（BaseUrl）与模型名称，再开始翻译。");
            return false;
        }
        return true;
    }

    private IEnumerable<string> EnumerateSupportedFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => TranslationEngineFactory.IsSupported(Settings.Game, f))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

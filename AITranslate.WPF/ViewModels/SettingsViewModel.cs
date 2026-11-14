using AITranslate.Core.Configuration;
using AITranslate.Core.Translation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITranslate.WPF.ViewModels;

/// <summary>
/// 设置窗口的视图模型：
/// <list type="bullet">
///   <item>「翻译目标」——选 XCOM 2 还是博德之门 3（只能选一个），决定接受的文件类型与输出后缀；</item>
///   <item>「AI 接口」——BaseUrl / ApiKey / Model 三项。</item>
/// </list>
/// <para>其余翻译参数为固定值，见 <see cref="TranslationDefaults"/>，不对外开放。</para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBg3Selected))]
    [NotifyPropertyChangedFor(nameof(GameHint))]
    private GameProfile _selectedGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(FullUrl))]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _testResult = "尚未测试。填写后点「测试连接」可验证配置是否正确。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _isTesting;

    public SettingsViewModel(AppSettings source)
    {
        SelectedGame = source.Profile;
        BaseUrl = source.BaseUrl;
        ApiKey = source.ApiKey;
        Model = source.Model;
    }

    /// <summary>可选游戏列表（下拉框数据源）。</summary>
    public IReadOnlyList<GameProfile> Games { get; } = GameProfiles.All;

    /// <summary>当前是否选择了 BG3。</summary>
    public bool IsBg3Selected => SelectedGame.Kind == GameKind.Bg3;

    /// <summary>随选择变化的说明文字。</summary>
    public string GameHint => SelectedGame.Kind == GameKind.Bg3
        ? "只接受 .xml 文件，译文输出为 .chs.xml；可指定参考文件复用其中已有的翻译。"
        : "只接受 .int 文件，译文输出为 .chn。";

    /// <summary>必填项是否已填写（BaseUrl 与 Model），用于控制「保存」按钮可用性。</summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>实际会被请求的完整地址（自动补全 <c>/chat/completions</c>），供界面回显。</summary>
    public string FullUrl => AiSettings.BuildChatCompletionsUrl(BaseUrl);

    public AppSettings ToSettings() => new()
    {
        Game = SelectedGame.Kind,
        BaseUrl = BaseUrl?.Trim() ?? string.Empty,
        ApiKey = ApiKey?.Trim() ?? string.Empty,
        Model = Model?.Trim() ?? string.Empty,
    };

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestResult = "正在测试…";
        try
        {
            using var client = new ChatCompletionClient(ToSettings().ToAiSettings());
            string? error = await client.TestAsync(CancellationToken.None);
            TestResult = error is null
                ? "✓ 连接成功，配置可用。"
                : $"✗ 连接失败：{error}";
        }
        catch (Exception ex)
        {
            TestResult = "✗ 异常：" + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => !IsTesting;
}

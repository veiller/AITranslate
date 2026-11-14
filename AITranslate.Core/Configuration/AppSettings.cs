namespace AITranslate.Core.Configuration;

/// <summary>
/// 应用配置（持久化根对象）。
/// <para>
/// 运行时可切换「翻译目标」（一次只能选一个），它决定接受的文件类型、使用的引擎、
/// 输出后缀以及是否显示参考文件。AI 接口参数见 <see cref="AiSettings"/>（这里是其可持久化形式）。
/// </para>
/// </summary>
public sealed class AppSettings
{
    /// <summary>当前翻译目标。</summary>
    public GameKind Game { get; set; } = GameKind.Xcom2;

    /// <summary>接口地址（base_url，填到版本号为止）。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>当前游戏档案。</summary>
    public GameProfile Profile => GameProfiles.For(Game);

    /// <summary>AI 是否已配置完整（BaseUrl 与 Model 非空）。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>取出引擎需要的 AI 配置。</summary>
    public AiSettings ToAiSettings() => new()
    {
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
    };

    /// <summary>用设置窗口的结果回写 AI 部分。</summary>
    public void CopyFrom(AiSettings ai)
    {
        BaseUrl = ai.BaseUrl;
        ApiKey = ai.ApiKey;
        Model = ai.Model;
    }
}

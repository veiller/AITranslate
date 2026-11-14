namespace AITranslate.Core.Configuration;

/// <summary>
/// 翻译引擎的固定参数。
/// <para>
/// 这些值不对外开放配置：它们与具体的游戏格式与接口约定绑定，由代码统一维护，
/// 避免用户改动后导致译文错乱或触发接口限流。
/// </para>
/// </summary>
public static class TranslationDefaults
{
    /// <summary>BG3 XML 每批翻译的节点数量。</summary>
    public const int BatchSize = 150;

    /// <summary>每批 API 调用之间的延迟（毫秒），用于降低触发限流的概率。</summary>
    public const int ApiDelayMs = 2000;

    /// <summary>BG3 XML 中需要翻译的节点 XPath。</summary>
    public const string TranslatableNodeXPath = "//content";

    /// <summary>单次请求的超时时间（秒）。</summary>
    public const int TimeoutSeconds = 120;

    /// <summary>请求失败后的最大重试次数。</summary>
    public const int MaxRetries = 3;

    /// <summary>同时并发翻译的文件数上限。</summary>
    public const int MaxConcurrentFiles = 2;

    /// <summary>目标语言。</summary>
    public const string TargetLanguage = "简体中文";

    /// <summary>XCOM <c>.int</c> 每批最多翻译的**唯一值**条数（去重后计数）。</summary>
    public const int XcomMaxEntriesPerBatch = 50;

    /// <summary>XCOM <c>.int</c> 每批的原文总字符上限，避免单批过大导致回复被截断。</summary>
    public const int XcomMaxCharsPerBatch = 6000;
}

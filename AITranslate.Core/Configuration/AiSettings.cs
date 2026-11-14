namespace AITranslate.Core.Configuration;

/// <summary>
/// AI 接口配置。
/// <para>
/// 只需要用户填写三项——其余翻译参数（批量大小、批间隔、XPath、超时、重试、并发数等）
/// 均为固定值，见 <see cref="TranslationDefaults"/>。
/// </para>
/// </summary>
public sealed class AiSettings
{
    /// <summary>固定路径后缀：OpenAI 兼容接口的 chat completions 端点。</summary>
    public const string ChatCompletionsPath = "/chat/completions";

    /// <summary>
    /// 接口地址（OpenAI 兼容接口的 base_url），填到版本号为止即可，例如
    /// <c>https://api.deepseek.com/v1</c>；<c>/chat/completions</c> 由程序自动补全。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API 密钥，由服务商提供，形如 <c>sk-xxxxxxxx</c>。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型 ID，由服务商提供，例如 <c>deepseek-chat</c>、<c>gpt-4o-mini</c>。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>实际请求地址：在 <see cref="BaseUrl"/> 基础上补全 <c>/chat/completions</c>。</summary>
    public string ChatCompletionsUrl => BuildChatCompletionsUrl(BaseUrl);

    /// <summary>是否已填写完整（BaseUrl 与 Model 非空）。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// 由 base_url 拼出完整的 chat completions 地址。
    /// <para>
    /// 会先去掉结尾多余的 <c>/</c>；若用户直接粘贴了以 <c>/chat/completions</c> 结尾的完整地址，
    /// 则原样返回（避免拼成 <c>.../chat/completions/chat/completions</c>）。
    /// </para>
    /// </summary>
    public static string BuildChatCompletionsUrl(string? baseUrl)
    {
        string trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ChatCompletionsPath;
    }
}

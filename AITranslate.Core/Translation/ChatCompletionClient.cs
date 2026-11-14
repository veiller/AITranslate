using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITranslate.Core.Configuration;

namespace AITranslate.Core.Translation;

/// <summary>一条对话消息。</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// OpenAI 兼容的流式 Chat Completion 客户端。
/// <para>通过 <c>onDelta</c> 回调把每个增量片段（<c>delta.content</c>）实时交出去用于流式预览，同时返回完整的累计文本用于落盘 / 解析。</para>
/// <para>内置超时与退避重试；<c>onStatus</c> 用于把重试等状态消息上报给上层。超时与重试次数取自 <see cref="TranslationDefaults"/>。</para>
/// </summary>
public sealed class ChatCompletionClient : IDisposable
{
    private readonly AiSettings _settings;
    private readonly HttpClient _client;
    private bool _disposed;

    public ChatCompletionClient(AiSettings settings)
    {
        _settings = settings;

        // 共享的 HttpClient，不设置超时——超时由每次请求内的 CancellationTokenSource 控制。
        // Authorization 在每次请求时单独设置，以便读取最新的 ApiKey。
        _client = new HttpClient();
    }

    /// <summary>
    /// 执行一次流式翻译。
    /// </summary>
    /// <param name="messages">system + user 消息。</param>
    /// <param name="onDelta">每个增量片段回调（用于流式预览）。</param>
    /// <param name="onStatus">状态消息回调（如重试、超时），可为空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完整的累计文本。</returns>
    public async Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onDelta,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        int attempt = 0;
        int maxRetries = Math.Max(0, TranslationDefaults.MaxRetries);

        while (true)
        {
            try
            {
                return await CompleteOnceAsync(messages, onDelta, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                attempt++;
                int delaySeconds = (int)Math.Pow(2, attempt);
                onStatus?.Invoke($"请求失败（HTTP），{delaySeconds} 秒后重试 (第 {attempt}/{maxRetries} 次)");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                // 请求超时（非用户取消）
                attempt++;
                int delaySeconds = (int)Math.Pow(2, attempt);
                onStatus?.Invoke($"请求超时，{delaySeconds} 秒后重试 (第 {attempt}/{maxRetries} 次)");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 校验连接与配置是否可用。发送一条非流式的最小请求。
    /// </summary>
    /// <returns>成功返回 <c>null</c>，失败返回错误描述。</returns>
    public async Task<string?> TestAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, TranslationDefaults.TimeoutSeconds)));

            var requestBody = new
            {
                model = _settings.Model,
                messages = new[] { new { role = "user", content = "ping" } },
                stream = false,
            };

            string json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.ChatCompletionsUrl) { Content = content };
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return $"HTTP {(int)response.StatusCode}: {body}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string> CompleteOnceAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, TranslationDefaults.TimeoutSeconds)));

        var requestBody = new
        {
            model = _settings.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
        };

        string json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.ChatCompletionsUrl) { Content = content };
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new HttpRequestException(
                $"API 返回错误状态码: {(int)response.StatusCode} {response.ReasonPhrase}, 响应: {errorBody}");
        }

        var full = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            string data = line["data:".Length..].Trim();
            if (data.StartsWith("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

            string? delta = TryParseDeltaContent(data);
            if (!string.IsNullOrEmpty(delta))
            {
                full.Append(delta);
                onDelta?.Invoke(delta);
            }
        }

        return full.ToString();
    }

    private static string? TryParseDeltaContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch
        {
            // 忽略单行解析错误
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

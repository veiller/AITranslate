using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace KimiTranslate
{
    public class TranslationApiService
    {
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private readonly Settings _settings;
        public TranslationApiService(Settings settings)
        {
            _settings = settings;
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_settings.ApiKey}");
        }

        public async Task<Dictionary<string, string>> TranslateBatchAsync(Dictionary<string, string> batch, int maxRetries = 3)
        {
            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine("你是一位顶级的游戏汉化专家，正在翻译《博德之门3》的MOD文本。");
            promptBuilder.AppendLine("请严格遵守以下规则：");
            promptBuilder.AppendLine("1. 将下面 <translations> 标签内每一个 <entry> 的文本内容翻译成简体中文，并保持《博德之门3》的奇幻风格。");
            promptBuilder.AppendLine("2. 必须完整保留所有的XML标签，例如 <LSTag ...>...</LSTag>。");
            promptBuilder.AppendLine("3. 必须完整保留所有的方括号占位符，例如 [1], [2] 等。");
            promptBuilder.AppendLine("4. 你的回答必须是结构完全相同的XML，只替换文本内容。不要在XML前后添加任何多余的文字。");
            promptBuilder.AppendLine("---");
            promptBuilder.AppendLine("<translations>");
            foreach (var item in batch)
            {
                promptBuilder.AppendLine($"  <entry id=\"{item.Key}\">{SecurityElement.Escape(item.Value)}</entry>");
            }
            promptBuilder.AppendLine("</translations>");
            var requestBody = new ApiRequest
            {
                Model = _settings.Model,
                Messages = new[]
                {
                new Message { Role = "system", Content = "你是一个遵循指令的XML翻译引擎。用户会给你一个带id的XML片段，你必须返回一个结构完全相同，但文本内容已被翻译的XML片段。不要有任何额外的解释性文字。" },
                new Message { Role = "user", Content= promptBuilder.ToString() }
            },
                Stream = true
            };
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    return await TranslateOnceAsync(content);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries - 1)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n请求失败 (尝试 {attempt}/{maxRetries - 1})，{delay.TotalSeconds}秒后重试... 错误: {ex.Message}");
                    Console.ResetColor();
                    await Task.Delay(delay);
                }
                catch (TaskCanceledException) when (attempt < maxRetries - 1)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n请求超时 (尝试 {attempt}/{maxRetries - 1})，{delay.TotalSeconds}秒后重试...");
                    Console.ResetColor();
                    await Task.Delay(delay);
                }
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n翻译失败，已达到最大重试次数。");
            Console.ResetColor();
            return null;
        }

        private async Task<Dictionary<string, string>> TranslateOnceAsync(StringContent content)
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, _settings.ApiUrl) { Content = content };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            response.EnsureSuccessStatusCode();
            var fullContentBuilder = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            Console.WriteLine("--- 实时AI响应流 (原始内容) ---");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                if (line == "data: [DONE]") break;
                string jsonData = line["data: ".Length..].Trim();
                var streamChunk = JsonConvert.DeserializeObject<ApiStreamResponse>(jsonData);
                var deltaContent = streamChunk?.Choices?[0]?.Delta?.Content;
                if (deltaContent != null)
                {
                    Console.Write(deltaContent);
                    fullContentBuilder.Append(deltaContent);
                }
            }

            Console.ResetColor();
            Console.WriteLine();
            return ParseTranslatedXml(fullContentBuilder.ToString());
        }
        // 解析逻辑保持不变，它处理的是完整的XML字符串
        private Dictionary<string, string> ParseTranslatedXml(string xmlContent)
        {
            // ... 这个方法的代码和之前完全一样，此处省略 ...
            var result = new Dictionary<string, string>();
            try
            {
                var match = Regex.Match(xmlContent, @"<translations>.*?</translations>", RegexOptions.Singleline);
                if (!match.Success)
                {
                    Console.WriteLine("  [解析错误] AI返回的内容中未找到有效的 <translations> XML块。");
                    return result;
                }
                string cleanXml = match.Value;
                XDocument doc = XDocument.Parse(cleanXml);
                foreach (var element in doc.Descendants("entry"))
                {
                    var id = element.Attribute("id")?.Value;
                    if (!string.IsNullOrEmpty(id))
                    {
                        result[id] = element.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [解析严重错误] 解析AI返回的XML时出错: {ex.Message}");
            }
            return result;
        }
    }
}

using Newtonsoft.Json;
using System.Text;

namespace KimiTranslate
{
    internal class XCOMTranslater : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiUrl;
        private readonly string _model;
        private readonly HttpClient _client;
        private readonly TimeSpan _timeout = TimeSpan.FromSeconds(120);

        public XCOMTranslater(string apiKey, string apiUrl, string model)
        {
            _apiKey = apiKey;
            _apiUrl = apiUrl;
            _model = model;
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        /// <summary>
        /// 翻译文本，使用流式输出到console
        /// </summary>
        public async Task<string> TranslateTextStreamAsync(string text, int maxRetries = 3)
        {
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = "你是一个游戏领域翻译专家，请根据游戏XCOM2的风格和背景汉化游戏文本，翻译双引号内的文字，源文件中的注释和占位符等保持不变" },
                    new { role = "user", content = text }
                },
                stream = true
            };

            string json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

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
                    Console.WriteLine($"\n请求失败 (尝试 {attempt}/{maxRetries - 1})，{delay.TotalSeconds}秒后重试... 错误: {ex.Message}");
                    await Task.Delay(delay);
                }
                catch (TaskCanceledException) when (attempt < maxRetries - 1)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    Console.WriteLine($"\n请求超时 (尝试 {attempt}/{maxRetries - 1})，{delay.TotalSeconds}秒后重试...");
                    await Task.Delay(delay);
                }
            }

            Console.WriteLine("\n翻译失败，已达到最大重试次数。");
            return "";
        }

        private async Task<string> TranslateOnceAsync(StringContent content)
        {
            using var cts = new CancellationTokenSource(_timeout);
            using var response = await _client.PostAsync(_apiUrl, content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API返回错误状态码: {response.StatusCode}, 响应: {errorContent}");
            }

            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var fullContent = new StringBuilder();
            string line;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("data: "))
                {
                    var data = line["data: ".Length..];

                    if (data == "[DONE]")
                        break;

                    try
                    {
                        var json_obj = JsonConvert.DeserializeObject<dynamic>(data);
                        var delta = json_obj?.choices?[0]?.delta;

                        if (delta != null && delta.content != null)
                        {
                            string chunk = delta.content;
                            Console.Write(chunk);
                            fullContent.Append(chunk);
                        }
                    }
                    catch
                    {
                        // 忽略解析错误
                    }
                }
            }

            Console.WriteLine(); // 换行
            var result = fullContent.ToString();
            return result.Replace("&lt;", "<").Replace("&gt;", ">");
        }

        /// <summary>
        /// 翻译文件
        /// </summary>
        public async Task TranslateFileAsync(string inputFilePath, string outputFilePath)
        {
            Console.WriteLine($"翻译后的文件为：{outputFilePath}");
            Console.WriteLine("正在开始翻译...\n");

            // 读取输入文件
            string[] lines = File.ReadAllLines(inputFilePath);

            // 创建输出文件流，使用UTF16-LE编码（自动添加BOM），不手动写入BOM
            using (StreamWriter writer = new StreamWriter(outputFilePath, false, Encoding.Unicode))
            {
                string currentText = "";
                int lineCount = 0;

                foreach (string line in lines)
                {
                    if (lineCount >= 70 || (lineCount >= 50 && line.Trim() == ""))
                    {
                        if (currentText != "")
                        {
                            // 调用流式翻译接口
                            string translation = await TranslateTextStreamAsync(currentText);
                            await Task.Delay(2000);
                            writer.WriteLine(translation);
                            writer.WriteLine(); // 添加空行
                            currentText = "";
                            lineCount = 0;
                        }

                        if (line.Trim() == "")
                        {
                            writer.WriteLine(); // 保留原始空行
                        }
                    }
                    else
                    {
                        currentText += line + "\n";
                        lineCount++;
                    }
                }

                // 处理最后一段文本
                if (currentText != "")
                {
                    string translation = await TranslateTextStreamAsync(currentText);
                    writer.WriteLine(translation);
                }
            }

            Console.WriteLine("\n翻译完成，结果已保存到文件中。");
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}

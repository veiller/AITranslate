using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using AITranslate.Core.Configuration;
using AITranslate.Core.Translation.Concurrency;

namespace AITranslate.Core.Translation;

/// <summary>
/// BG3 XML 文件的翻译核心：用 XPath 定位可翻译节点，复用参考文件的已有翻译，剩余节点分批调用 AI。
/// <para>批量大小、批间隔与 XPath 等参数取自 <see cref="TranslationDefaults"/>（固定值，不对外开放配置）。</para>
/// </summary>
public sealed class XmlTranslationService
{
    private readonly ChatCompletionClient _client;
    private readonly GlobalRateLimiter _limiter;

    public XmlTranslationService(ChatCompletionClient client, GlobalRateLimiter limiter)
    {
        _client = client;
        _limiter = limiter;
    }

    /// <summary>
    /// 翻译一个 XML 文件。
    /// </summary>
    /// <returns>无返回值；失败时抛出异常。</returns>
    public async Task TranslateXmlFileAsync(
        string inputPath,
        string outputPath,
        string? referenceXmlPath,
        string fileId,
        IProgress<TranslationProgressItem> progress,
        CancellationToken ct)
    {
        try
        {
            XDocument doc = XDocument.Load(inputPath, LoadOptions.PreserveWhitespace);
            var referenceTranslations = LoadReferenceTranslations(referenceXmlPath);

            var allNodes = doc.XPathSelectElements(TranslationDefaults.TranslatableNodeXPath).ToList();
            if (allNodes.Count == 0)
            {
                SaveDocument(doc, outputPath);
                progress.Report(new(fileId, TranslationStage.Status, Message: "警告：按 XPath 未找到可翻译节点，已原样保存。"));
                progress.Report(new(fileId, TranslationStage.FileCompleted, Percent: 100, Message: "翻译完成（无节点）"));
                return;
            }

            progress.Report(new(fileId, TranslationStage.Status, Message: $"共找到 {allNodes.Count} 个待处理节点。"));

            var nodesToUpdateFromReference = new List<XElement>();
            var nodesToTranslateViaApi = new List<XElement>();

            foreach (var node in allNodes)
            {
                ct.ThrowIfCancellationRequested();
                string? contentUid = node.Attribute("contentuid")?.Value;
                if (contentUid != null && referenceTranslations.TryGetValue(contentUid, out var translatedText))
                {
                    node.Value = translatedText;
                    nodesToUpdateFromReference.Add(node);
                }
                else
                {
                    nodesToTranslateViaApi.Add(node);
                }
            }

            if (nodesToUpdateFromReference.Count > 0)
            {
                progress.Report(new(fileId, TranslationStage.Status, Message: $"从参考文件复用了 {nodesToUpdateFromReference.Count} 个节点的翻译。"));
            }

            if (nodesToTranslateViaApi.Count > 0)
            {
                await ProcessNodesWithApiAsync(nodesToTranslateViaApi, fileId, progress, ct).ConfigureAwait(false);
            }
            else
            {
                progress.Report(new(fileId, TranslationStage.Status, Message: "所有节点均已从参考文件更新，无需调用 AI。"));
            }

            SaveDocument(doc, outputPath);
            progress.Report(new(fileId, TranslationStage.FileCompleted, Percent: 100, Message: $"翻译完成，已保存至 {outputPath}"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progress.Report(new(fileId, TranslationStage.Status, Message: "已取消"));
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new(fileId, TranslationStage.Error, Message: $"错误: {ex.Message}"));
            throw;
        }
    }

    private async Task ProcessNodesWithApiAsync(
        List<XElement> nodesToTranslate,
        string fileId,
        IProgress<TranslationProgressItem> progress,
        CancellationToken ct)
    {
        int batchSize = Math.Max(1, TranslationDefaults.BatchSize);
        int totalBatches = (int)Math.Ceiling((double)nodesToTranslate.Count / batchSize);
        progress.Report(new(fileId, TranslationStage.Status, Message: $"将分为 {totalBatches} 批处理（每批最多 {batchSize} 个节点）。"));

        int processed = 0;
        for (int i = 0; i < totalBatches; i++)
        {
            ct.ThrowIfCancellationRequested();
            var currentBatch = nodesToTranslate.Skip(i * batchSize).Take(batchSize).ToList();

            using var lease = await _limiter.AcquireAsync(ct).ConfigureAwait(false);

            progress.Report(new(fileId, TranslationStage.Status, Message: $"处理第 {i + 1} / {totalBatches} 批 (AI 翻译)"));
            var batchWithIds = new Dictionary<string, string>();
            for (int j = 0; j < currentBatch.Count; j++)
            {
                batchWithIds.Add($"id_{j}", currentBatch[j].Value);
            }

            string xml = await TranslateBatchAsync(batchWithIds, fileId, progress, ct).ConfigureAwait(false);
            var translatedBatch = ParseTranslatedXml(xml);

            if (translatedBatch.Count > 0)
            {
                foreach (var item in translatedBatch)
                {
                    if (int.TryParse(item.Key.Replace("id_", ""), out int itemIndex) && itemIndex < currentBatch.Count)
                    {
                        currentBatch[itemIndex].Value = item.Value;
                    }
                }
                processed += currentBatch.Count;
                int percent = (int)(processed * 100d / nodesToTranslate.Count);
                progress.Report(new(fileId, TranslationStage.BatchCompleted, Percent: Math.Clamp(percent, 0, 100), Message: $"第 {i + 1}/{totalBatches} 批完成"));
            }
            else
            {
                progress.Report(new(fileId, TranslationStage.Error, Message: $"第 {i + 1} 批翻译失败或未返回有效结果，已跳过。"));
            }

            if (i < totalBatches - 1 && TranslationDefaults.ApiDelayMs > 0)
            {
                await Task.Delay(TranslationDefaults.ApiDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> TranslateBatchAsync(
        Dictionary<string, string> batch,
        string fileId,
        IProgress<TranslationProgressItem> progress,
        CancellationToken ct)
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
            promptBuilder.AppendLine($"  <entry id=\"{item.Key}\">{System.Security.SecurityElement.Escape(item.Value)}</entry>");
        }
        promptBuilder.AppendLine("</translations>");

        var messages = new[]
        {
            new ChatMessage("system", "你是一个遵循指令的XML翻译引擎。用户会给你一个带id的XML片段，你必须返回一个结构完全相同，但文本内容已被翻译的XML片段。不要有任何额外的解释性文字。"),
            new ChatMessage("user", promptBuilder.ToString()),
        };

        string full = await _client.CompleteAsync(
            messages,
            onDelta: delta => progress.Report(new(fileId, TranslationStage.ChunkReceived, Text: delta)),
            onStatus: msg => progress.Report(new(fileId, TranslationStage.Status, Message: msg)),
            ct).ConfigureAwait(false);

        return full;
    }

    private Dictionary<string, string> LoadReferenceTranslations(string? referenceXmlPath)
    {
        var dictionary = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(referenceXmlPath))
        {
            return dictionary;
        }

        try
        {
            XDocument refDoc = XDocument.Load(referenceXmlPath);
            var refNodes = refDoc.XPathSelectElements(TranslationDefaults.TranslatableNodeXPath);

            foreach (var node in refNodes)
            {
                string? contentUid = node.Attribute("contentuid")?.Value;
                if (contentUid != null && !dictionary.ContainsKey(contentUid))
                {
                    dictionary.Add(contentUid, node.Value);
                }
            }
        }
        catch
        {
            // 参考文件加载失败不影响主流程
        }

        return dictionary;
    }

    private static Dictionary<string, string> ParseTranslatedXml(string xmlContent)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(xmlContent)) return result;

        try
        {
            var match = Regex.Match(xmlContent, @"<translations>.*?</translations>", RegexOptions.Singleline);
            if (!match.Success) return result;

            XDocument doc = XDocument.Parse(match.Value);
            foreach (var element in doc.Descendants("entry"))
            {
                var id = element.Attribute("id")?.Value;
                if (!string.IsNullOrEmpty(id))
                {
                    result[id] = element.Value;
                }
            }
        }
        catch
        {
            // 解析失败返回空，由上层判断并跳过该批
        }
        return result;
    }

    private static void SaveDocument(XDocument doc, string outputPath)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), // UTF-8 无 BOM
            Indent = true,
            OmitXmlDeclaration = doc.Declaration == null,
        };
        using var writer = XmlWriter.Create(outputPath, settings);
        doc.Save(writer);
    }
}

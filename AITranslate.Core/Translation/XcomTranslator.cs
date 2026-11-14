using System.Text;
using AITranslate.Core.Configuration;
using AITranslate.Core.Translation.Concurrency;

namespace AITranslate.Core.Translation;

/// <summary>
/// XCOM2 .int 文件翻译引擎（V1，<b>已被 <see cref="XcomTranslatorV2"/> 取代，不再由工厂注册</b>）。
/// <para>
/// 保留原因：仓库没有提交历史，删掉不可恢复；如需回退可在 <see cref="TranslationEngineFactory"/> 中改回本类。
/// </para>
/// <para>
/// 已知缺陷（V2 已修复，故新文件不要再采用这种做法）：把整段原文（含 <c>键=</c> 与空行）发给模型、
/// 再把整段回复原样写盘，结构正确性完全依赖模型自觉；不区分无需翻译的值；不做去重。
/// </para>
/// </summary>
public sealed class XcomTranslator : ITranslationEngine
{
    private const string SystemPrompt =
        "你是一个游戏领域翻译专家，请根据游戏XCOM2的风格和背景汉化游戏文本，翻译双引号内的文字，原文件中的注释和占位符等保持不变，只按照我给你的内容翻译，别输出其他信息";

    private readonly ChatCompletionClient _client;
    private readonly GlobalRateLimiter _limiter;
    private bool _disposed;

    public XcomTranslator(ChatCompletionClient client, GlobalRateLimiter limiter)
    {
        _client = client;
        _limiter = limiter;
    }

    public string FileId { get; set; } = string.Empty;

    public async Task TranslateAsync(TranslateRequest request, IProgress<TranslationProgressItem> progress, CancellationToken ct)
    {
        FileId = Path.GetFileName(request.InputPath);
        progress.Report(new(FileId, TranslationStage.FileStarted, Message: $"开始翻译 {FileId}"));

        try
        {
            string[] lines = await File.ReadAllLinesAsync(request.InputPath, ct).ConfigureAwait(false);
            var items = BuildItems(lines);

            if (items.Count == 0)
            {
                await File.WriteAllTextAsync(request.OutputPath, string.Empty, new UnicodeEncoding(false, true), ct).ConfigureAwait(false);
                progress.Report(new(FileId, TranslationStage.FileCompleted, Percent: 100, Message: "文件为空"));
                return;
            }

            int totalChunks = items.Count(i => !i.IsBlank);
            int doneChunks = 0;
            using var writer = new StreamWriter(request.OutputPath, false, new UnicodeEncoding(false, true));

            for (int k = 0; k < items.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[k];

                if (item.IsBlank)
                {
                    writer.WriteLine();
                }
                else
                {
                    doneChunks++;
                    progress.Report(new(FileId, TranslationStage.Status, Message: $"翻译第 {doneChunks}/{totalChunks} 段"));
                    string translation = await TranslateChunkAsync(item.Chunk, progress, ct).ConfigureAwait(false);
                    writer.WriteLine(translation);
                    writer.WriteLine(); // 段后保留空行
                }

                int percent = (int)((k + 1) * 100d / items.Count);
                progress.Report(new(FileId, TranslationStage.BatchCompleted, Percent: Math.Clamp(percent, 0, 100), Message: $"进度 {percent}%"));
            }

            progress.Report(new(FileId, TranslationStage.FileCompleted, Percent: 100, Message: "翻译完成"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progress.Report(new(FileId, TranslationStage.Status, Message: "已取消"));
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new(FileId, TranslationStage.Error, Message: $"错误: {ex.Message}"));
            throw;
        }
    }

    private async Task<string> TranslateChunkAsync(string chunk, IProgress<TranslationProgressItem> progress, CancellationToken ct)
    {
        using var lease = await _limiter.AcquireAsync(ct).ConfigureAwait(false);

        var messages = new[]
        {
            new ChatMessage("system", SystemPrompt),
            new ChatMessage("user", chunk),
        };

        string full = await _client.CompleteAsync(
            messages,
            onDelta: delta => progress.Report(new(FileId, TranslationStage.ChunkReceived, Text: delta)),
            onStatus: msg => progress.Report(new(FileId, TranslationStage.Status, Message: msg)),
            ct).ConfigureAwait(false);

        return full.Replace("&lt;", "<").Replace("&gt;", ">");
    }

    /// <summary>
    /// 把源文件切分为顺序执行的「翻译块 / 空行」列表。
    /// </summary>
    private static List<XcomItem> BuildItems(string[] lines)
    {
        var items = new List<XcomItem>();
        var current = new StringBuilder();
        int count = 0;

        foreach (var line in lines)
        {
            bool boundary = count >= 70 || (count >= 50 && line.Trim() == "");
            if (boundary)
            {
                if (count > 0)
                {
                    items.Add(new XcomItem(current.ToString(), false));
                    current.Clear();
                    count = 0;
                }

                if (line.Trim() == "")
                {
                    items.Add(new XcomItem(string.Empty, true));
                    continue;
                }
                // 边界触发的非空行走 else 分支，归入下一段
            }

            current.Append(line).Append('\n');
            count++;
        }

        if (count > 0)
        {
            items.Add(new XcomItem(current.ToString(), false));
        }

        return items;
    }

    private readonly record struct XcomItem(string Chunk, bool IsBlank);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

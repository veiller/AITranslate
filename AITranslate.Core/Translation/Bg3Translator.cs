using AITranslate.Core.Configuration;
using AITranslate.Core.Translation.Concurrency;

namespace AITranslate.Core.Translation;

/// <summary>
/// BG3 .xml 文件翻译引擎，包装 <see cref="XmlTranslationService"/>，实现统一 <see cref="ITranslationEngine"/> 接口。
/// </summary>
public sealed class Bg3Translator : ITranslationEngine
{
    private readonly XmlTranslationService _service;
    private bool _disposed;

    public Bg3Translator(ChatCompletionClient client, GlobalRateLimiter limiter)
    {
        _service = new XmlTranslationService(client, limiter);
    }

    public string FileId { get; set; } = string.Empty;

    public async Task TranslateAsync(TranslateRequest request, IProgress<TranslationProgressItem> progress, CancellationToken ct)
    {
        FileId = Path.GetFileName(request.InputPath);
        progress.Report(new(FileId, TranslationStage.FileStarted, Message: $"开始翻译 {FileId}"));

        await _service.TranslateXmlFileAsync(
            request.InputPath,
            request.OutputPath,
            request.ReferencePath,
            FileId,
            progress,
            ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

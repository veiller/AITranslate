namespace AITranslate.Core.Translation;

/// <summary>一次翻译请求的文件信息。</summary>
public sealed record TranslateRequest(
    string InputPath,
    string OutputPath,
    string? ReferencePath = null);

/// <summary>
/// 单个文件的翻译引擎。一个引擎实例对应一个文件（一个页签），通过 <see cref="IProgress{T}"/> 上报流式预览与进度。
/// </summary>
public interface ITranslationEngine : IDisposable
{
    /// <summary>用于区分多页签 / 日志的文件标识（通常为文件名）。</summary>
    string FileId { get; }

    /// <summary>
    /// 翻译一个文件。<paramref name="progress"/> 为进度上报，<paramref name="ct"/> 用于取消。
    /// </summary>
    Task TranslateAsync(TranslateRequest request, IProgress<TranslationProgressItem> progress, CancellationToken ct);
}

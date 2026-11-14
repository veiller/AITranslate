namespace AITranslate.Core.Translation;

/// <summary>
/// 翻译进度阶段。引擎通过 <see cref="IProgress{T}"/> 上报，UI 层据此驱动流式预览、进度条与日志。
/// </summary>
public enum TranslationStage
{
    /// <summary>文件开始翻译。</summary>
    FileStarted,

    /// <summary>流式增量文本（<see cref="TranslationProgressItem.Text"/> 是增量片段）。</summary>
    ChunkReceived,

    /// <summary>
    /// 已定稿的**输出文件行**（<see cref="TranslationProgressItem.Text"/> 是最终要写进文件的整行，含换行符）。
    /// <para>
    /// 引擎边翻译边按文件顺序把它推送出来，界面可直接追加显示，看到目标文档逐行长成——
    /// 比展示模型原始响应直观得多。只有能把「模型输出」映射回「文件行」的引擎才会发送（如 XCOM `.int`）。
    /// </para>
    /// </summary>
    OutputLine,

    /// <summary>某一批 / 某一段翻译完成。</summary>
    BatchCompleted,

    /// <summary>整个文件翻译完成。</summary>
    FileCompleted,

    /// <summary>出错。</summary>
    Error,

    /// <summary>一般状态消息（如复用参考翻译、进度说明）。</summary>
    Status,
}

/// <summary>
/// 一次进度上报。Text 仅在 ChunkReceived 时携带增量片段；Percent 为当前文件总体进度（0-100）。
/// </summary>
public sealed record TranslationProgressItem(
    string FileId,
    TranslationStage Stage,
    string Text = "",
    int Percent = 0,
    string? Message = null);

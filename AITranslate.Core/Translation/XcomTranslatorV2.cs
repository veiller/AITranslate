using System.Text;
using System.Text.RegularExpressions;
using AITranslate.Core.Configuration;
using AITranslate.Core.Translation.Concurrency;

namespace AITranslate.Core.Translation;

/// <summary>
/// XCOM 2 <c>.int</c> 文件翻译引擎（V2，结构化 + 编号行协议）。
/// <para>
/// 与 V1 的区别：V1 把整段原文（含 <c>键=</c> 与空行）丢给模型、再把整段回复原样写盘，结构正确性完全依赖模型自觉。
/// V2 改为：
/// </para>
/// <list type="number">
///   <item>逐行解析成结构化行（<c>[组]</c> / <c>键="值"</c> / <c>键=值</c> / <c>;注释</c> / 空行），原样保留非翻译行；</item>
///   <item>只把「值」按 <c>编号|值</c> 的单行格式发给模型，<b>键名与结构不进入请求</b>，模型无从破坏；</item>
///   <item>本地重建每一行，结构 1:1 由代码保证；</item>
///   <item>按值去重，只翻译唯一值（样例文件 72 条 → 41 条唯一，省 42%）；</item>
///   <item>跳过无需翻译的值（纯符号/数字、资源路径等）；</item>
///   <item>回填后校验 id 缺失则回退原文并告警，绝不写出损坏文件。</item>
/// </list>
/// <para>
/// 协议为何用「编号行」而不是带 id 的 XML：标记要付出<b>两次</b>代价——模型必须把结构原样回显，
/// 而输出 token 更贵。实测同一份文件（41 条唯一值）：XML <c>id</c>+<c>group</c> 每条标记 82 字符，
/// 仅 <c>id</c> 是 26 字符，而 <c>编号|值</c> 只有 4 字符。且 <c>.int</c> 的值天然单行，
/// 行协议下无需任何转义（XML 需处理 <c>&amp;</c>、<c>&lt;</c>、<c>&gt;</c>）。
/// </para>
/// <para>不支持参考文件（XCOM 无复用需求）。</para>
/// </summary>
public sealed class XcomTranslatorV2 : ITranslationEngine
{
    private static readonly Regex ResourcePathRegex = new(
        @"^(?:[A-Za-z]:)?[A-Za-z0-9_.\-/\\]+\.(?:upk|umap|png|dds|tga|wav|ogg|jpg|jpeg|ini|int|chn|kor)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>匹配「编号 + 分隔符 + 译文」。容忍模型把分隔符写成全角或其它标点、或加了空格。</summary>
    private static readonly Regex NumberedLineRegex = new(
        @"^\s*(\d+)\s*[|｜:：.、\-—–]\s*(.*)$",
        RegexOptions.Compiled);

    private readonly ChatCompletionClient _client;
    private readonly GlobalRateLimiter _limiter;
    private bool _disposed;

    public XcomTranslatorV2(ChatCompletionClient client, GlobalRateLimiter limiter)
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
            var parsed = ParseLines(lines);

            var entries = parsed.Where(l => l.Kind == XcomLineKind.Entry && ShouldTranslate(l.Value)).ToList();
            var translatable = entries
                .GroupBy(e => e.Value)
                .Select(g => new UniqueEntry(g.Key, g.ToList()))
                .ToList();

            int totalEntries = parsed.Count(l => l.Kind == XcomLineKind.Entry);
            int totalGroups = parsed.Count(l => l.Kind == XcomLineKind.Group);
            int skipped = totalEntries - entries.Count;
            foreach (var line in entries)
            {
                line.NeedsTranslation = true;
            }

            progress.Report(new(FileId, TranslationStage.Status, Message:
                $"共 {totalGroups} 个分组、{totalEntries} 条文本，其中 {entries.Count} 条需要翻译" +
                $"（{translatable.Count} 条唯一，{entries.Count - translatable.Count} 条重复，{skipped} 条无需翻译）"));

            // 先推一次「骨架」：分组行、注释行与无需翻译的条目立刻可见，随后译文逐行补进来
            var emitter = new DocumentEmitter(parsed, FileId, progress);
            emitter.Flush();

            if (translatable.Count > 0)
            {
                await TranslateUniqueAsync(translatable, progress, emitter, ct).ConfigureAwait(false);
            }

            emitter.Flush(); // 兜底：确保所有已定稿的行都推完

            // 本地按原行模板重建：结构由代码保证，模型无法改坏键名或顺序
            var rebuilt = new string[parsed.Count];
            for (int i = 0; i < parsed.Count; i++)
            {
                rebuilt[i] = parsed[i].Rebuild();
            }

            if (rebuilt.Length != lines.Length)
            {
                throw new InvalidOperationException(
                    $"内部错误：重建行数 {rebuilt.Length} 与源文件 {lines.Length} 不一致，已中止以免写坏文件。");
            }

            await WriteAllLinesAsync(request.OutputPath, rebuilt, ct).ConfigureAwait(false);

            int failedCount = translatable.Sum(t => t.Items.Count(x => x.TranslationFailed));
            string tail = failedCount > 0 ? $"，{failedCount} 条回退为原文" : string.Empty;
            progress.Report(new(FileId, TranslationStage.FileCompleted, Percent: 100,
                Message: $"翻译完成，已保存至 {request.OutputPath}{tail}"));
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

    /// <summary>按值去重后分批翻译；每批一边流式解析一边把已定稿的文件行推给界面。</summary>
    private async Task TranslateUniqueAsync(
        List<UniqueEntry> translatable,
        IProgress<TranslationProgressItem> progress,
        DocumentEmitter emitter,
        CancellationToken ct)
    {
        var batches = BuildBatches(translatable);
        progress.Report(new(FileId, TranslationStage.Status,
            Message: $"将分 {batches.Count} 批翻译（每批最多 {TranslationDefaults.XcomMaxEntriesPerBatch} 条）"));

        int done = 0;
        for (int b = 0; b < batches.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[b];
            var byId = batch.ToDictionary(e => e.Id, e => e);
            var committed = new HashSet<string>();

            void Apply(string id, string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!byId.TryGetValue(id, out var item)) return;   // 未知/多余 id：忽略
                if (!committed.Add(id)) return;                     // 重复 id：以第一次为准
                foreach (var line in item.Items)
                {
                    line.Translation = text;
                }
                emitter.Flush();   // 该条定稿后，可能带出一串按文件顺序就绪的行
            }

            using var lease = await _limiter.AcquireAsync(ct).ConfigureAwait(false);
            progress.Report(new(FileId, TranslationStage.Status, Message: $"翻译第 {b + 1} / {batches.Count} 批（{batch.Count} 条）"));

            var messages = new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", BuildPrompt(batch)),
            };

            // 流式增量解析：模型每吐完一条「编号|译文」，就立刻定稿并推给界面
            var stream = new IncrementalBatchParser(batch);
            string response = await _client.CompleteAsync(
                messages,
                onDelta: delta =>
                {
                    foreach (var (id, text) in stream.Append(delta))
                    {
                        Apply(id, text);
                    }
                },
                onStatus: msg => progress.Report(new(FileId, TranslationStage.Status, Message: msg)),
                ct).ConfigureAwait(false);

            // 权威解析：整批响应拿到后用它收尾（尤其最后一条编号行之后的尾巴，只有它能正确丢弃）
            var authoritative = ParseTranslations(response, batch);
            foreach (var (id, text) in stream.Finish(authoritative))
            {
                Apply(id, text);
            }
            foreach (var kv in authoritative)
            {
                if (!committed.Contains(kv.Key))
                {
                    Apply(kv.Key, kv.Value);
                }
            }

            int missing = 0;
            foreach (var item in batch)
            {
                if (committed.Contains(item.Id)) continue;
                // 关键兜底：该条回退为原文，保证输出文件始终结构完整、内容可用
                missing++;
                foreach (var line in item.Items)
                {
                    line.TranslationFailed = true;
                }
            }
            emitter.Flush();   // 让被跳过的条目也能随文件顺序推进

            if (missing > 0)
            {
                progress.Report(new(FileId, TranslationStage.Error,
                    Message: $"第 {b + 1} 批有 {missing} 条未返回有效译文，已回退为原文。"));
            }

            done += batch.Count;
            int percent = (int)(done * 100d / translatable.Count);
            progress.Report(new(FileId, TranslationStage.BatchCompleted, Percent: Math.Clamp(percent, 0, 100),
                Message: $"第 {b + 1}/{batches.Count} 批完成（{done}/{translatable.Count} 条唯一文本）"));

            if (b < batches.Count - 1 && TranslationDefaults.ApiDelayMs > 0)
            {
                await Task.Delay(TranslationDefaults.ApiDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>按条数与总字符数切批；同一批内的语体上下文由提示词统一说明，不再逐条附带。</summary>
    private static List<List<UniqueEntry>> BuildBatches(List<UniqueEntry> translatable)
    {
        var batches = new List<List<UniqueEntry>>();
        var current = new List<UniqueEntry>();
        int chars = 0;

        for (int i = 0; i < translatable.Count; i++)
        {
            var item = new UniqueEntry(translatable[i].Value, translatable[i].Items) { Id = i.ToString() };
            int len = item.Value.Length;

            if (current.Count > 0 &&
                (current.Count >= TranslationDefaults.XcomMaxEntriesPerBatch || chars + len > TranslationDefaults.XcomMaxCharsPerBatch))
            {
                batches.Add(current);
                current = new List<UniqueEntry>();
                chars = 0;
            }

            current.Add(item);
            chars += len;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static string BuildPrompt(List<UniqueEntry> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一位顶级的游戏汉化专家，正在翻译《XCOM 2》MOD的文本。");
        sb.AppendLine("本批文本包含角色名、技能名称与技能说明、任务简报、界面标签等；全大写短语请译为简短有力的中文标题。");
        sb.AppendLine();
        sb.AppendLine("请严格遵守以下规则：");
        sb.AppendLine("1. 把下面每一行「|」后面的文本翻译成简体中文，保持《XCOM 2》军事科幻的语感、简洁有力。");
        sb.AppendLine("2. 输出必须与输入行数相同、格式相同：行首编号原样保留，用「|」连接译文。");
        sb.AppendLine("3. 不要增删或合并行，每条译文都必须在一行内，不要换行。");
        sb.AppendLine("4. 不要添加任何解释、注释、标题或 Markdown 代码块。");
        sb.AppendLine("5. 完整保留原文中的占位符与转义序列（如 %s、%d、{0}、\\n）。");
        sb.AppendLine("6. 专有名词沿用官方中文习惯，XCOM、ADVENT、GREMLIN 等按官方译名处理。");
        sb.AppendLine("7. 若某行内容无需翻译（如纯代码、路径），原样返回。");
        sb.AppendLine();
        sb.AppendLine("待翻译：");
        foreach (var item in batch)
        {
            sb.Append(item.Id).Append('|').AppendLine(item.Value);
        }
        return sb.ToString();
    }

    private const string SystemPrompt =
        "你是一个严格遵循格式的翻译引擎。用户会给你若干「编号|英文文本」的行，你必须返回行数与编号完全对应、" +
        "文本已翻译成简体中文的「编号|译文」行。不要有任何额外文字、解释或 Markdown 代码块，也不要改动编号、增删行或把一条拆成多行。";

    /// <summary>
    /// 解析模型返回的「编号|译文」行，得到 id → 译文。
    /// <para>
    /// 容错策略：先剥离 Markdown 围栏；只用「首个编号行到最后一个编号行」之间的内容（自动忽略模型前后多余的话）；
    /// 分隔符容忍全角与常见标点；不带编号的非空行视为上一条的续行并入；
    /// 若整体完全没有编号但行数与请求一致，则退化为按顺序对应。
    /// </para>
    /// </summary>
    private static Dictionary<string, string> ParseTranslations(string response, IReadOnlyList<UniqueEntry> batch)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(response)) return result;

        string cleaned = StripCodeFence(response);
        string[] lines = cleaned.Split('\n');

        int first = -1, last = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (NumberedLineRegex.IsMatch(lines[i])) { first = i; break; }
        }
        if (first < 0)
        {
            // 退化路径：模型没带编号。行数能对上就按顺序回填，否则放弃（由上层回退原文并告警）。
            var plain = lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
                .ToList();
            if (plain.Count == batch.Count)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    result[batch[i].Id] = plain[i];
                }
            }
            return result;
        }
        for (int i = lines.Length - 1; i >= first; i--)
        {
            if (NumberedLineRegex.IsMatch(lines[i])) { last = i; break; }
        }

        string? currentId = null;
        var current = new StringBuilder();

        void Flush()
        {
            if (currentId is null) return;
            string text = current.ToString().Trim();
            if (text.Length > 0 && !result.ContainsKey(currentId))
            {
                result[currentId] = text;
            }
            currentId = null;
            current.Clear();
        }

        for (int i = first; i <= last; i++)
        {
            var m = NumberedLineRegex.Match(lines[i]);
            if (m.Success)
            {
                Flush();
                currentId = m.Groups[1].Value;
                current.Append(m.Groups[2].Value.Trim());
            }
            else
            {
                string extra = lines[i].Trim();
                if (extra.Length == 0 || currentId is null) continue;
                // 模型把一条拆成了多行：并入上一条（中日韩边界不加空格，其余加空格）
                // 注意：必须先取出已有内容再 Clear，否则会把上一条清空
                string merged = JoinWrapped(current.ToString(), extra);
                current.Clear().Append(merged);
            }
        }
        Flush();

        return result;
    }

    /// <summary>去掉 Markdown 代码围栏行（模型偶尔会包一层）。</summary>
    private static string StripCodeFence(string text)
    {
        if (!text.Contains("```", StringComparison.Ordinal)) return text;
        var kept = text.Split('\n').Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        return string.Join('\n', kept);
    }

    /// <summary>拼接被模型换行拆开的片段：中日韩字符之间不加空格，其余加空格。</summary>
    private static string JoinWrapped(string previous, string next)
    {
        if (previous.Length == 0) return next;
        if (next.Length == 0) return previous;
        bool joinTight = IsCjk(previous[^1]) && IsCjk(next[0]);
        return joinTight ? previous + next : previous + " " + next;
    }

    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||   // 基本汉字
        (c >= '\u3400' && c <= '\u4DBF') ||   // 扩展 A
        (c >= '\uF900' && c <= '\uFAFF') ||   // 兼容汉字
        (c >= '\u3000' && c <= '\u303F') ||   // 中文标点
        (c >= '\uFF00' && c <= '\uFFEF');     // 全角形式

    // ---------- 解析 ----------

    private static List<XcomLine> ParseLines(string[] lines)
    {
        var result = new List<XcomLine>(lines.Length);
        string group = string.Empty;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                result.Add(new XcomLine(XcomLineKind.Blank, i, raw, group));
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                group = trimmed;
                result.Add(new XcomLine(XcomLineKind.Group, i, raw, group));
                continue;
            }

            if (trimmed.StartsWith(';'))
            {
                result.Add(new XcomLine(XcomLineKind.Comment, i, raw, group));
                continue;
            }

            var entry = TryParseEntry(raw, i, group);
            result.Add(entry ?? new XcomLine(XcomLineKind.Raw, i, raw, group));
        }

        return result;
    }

    /// <summary>
    /// 解析一行 <c>键=值</c>。支持值带双引号与**不带双引号**两种写法，并保留 <c>=</c> 前后的空白，
    /// 以便重建时与源文件逐字节一致。
    /// </summary>
    private static XcomLine? TryParseEntry(string raw, int index, string group)
    {
        int eq = raw.IndexOf('=');
        if (eq <= 0) return null;

        string keyPart = raw[..eq];
        if (keyPart.Contains('"') || keyPart.Trim().Length == 0) return null;

        string rest = raw[(eq + 1)..];

        int lead = 0;
        while (lead < rest.Length && char.IsWhiteSpace(rest[lead])) lead++;
        string leadWhitespace = rest[..lead];
        string body = rest[lead..];

        string quote;
        string value;
        string tail;

        if (body.StartsWith('"'))
        {
            int close = body.LastIndexOf('"');
            if (close <= 0) return null; // 只有开引号，视为原始行不翻译
            quote = "\"";
            value = body[1..close];
            tail = body[(close + 1)..];
        }
        else
        {
            int end = body.Length;
            while (end > 0 && char.IsWhiteSpace(body[end - 1])) end--;
            quote = string.Empty;
            value = body[..end];
            tail = body[end..];
        }

        return new XcomLine(XcomLineKind.Entry, index, raw, group, keyPart, leadWhitespace, quote, value, tail);
    }

    /// <summary>判断某个值是否值得送去翻译。</summary>
    private static bool ShouldTranslate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!value.Any(char.IsLetter)) return false;
        if (LooksLikeResource(value)) return false;
        return true;
    }

    private static bool LooksLikeResource(string value)
    {
        string v = value.Trim();
        if (v.Contains("://", StringComparison.Ordinal)) return true;
        return ResourcePathRegex.IsMatch(v);
    }

    private static async Task WriteAllLinesAsync(string path, string[] lines, CancellationToken ct)
    {
        // XCOM2 的本地化文件使用 UTF-16 LE + BOM（与游戏自带的 .chn 一致）
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UnicodeEncoding(false, true));
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    // ---------- 内部模型 ----------

    private enum XcomLineKind
    {
        Blank,
        Group,
        Comment,
        Entry,
        Raw,
    }

    /// <summary>一行结构化文本。非 Entry 行原样写回；Entry 行用原文模板 + 译文重建。</summary>
    private sealed class XcomLine
    {
        public XcomLine(
            XcomLineKind kind,
            int index,
            string raw,
            string group,
            string keyPart = "",
            string leadWhitespace = "",
            string quote = "",
            string value = "",
            string tail = "")
        {
            Kind = kind;
            Index = index;
            Raw = raw;
            Group = group;
            KeyPart = keyPart;
            LeadWhitespace = leadWhitespace;
            Quote = quote;
            Value = value;
            Tail = tail;
        }

        public XcomLineKind Kind { get; }
        public int Index { get; }
        public string Raw { get; }
        public string Group { get; }
        public string KeyPart { get; }
        public string LeadWhitespace { get; }
        public string Quote { get; }
        public string Value { get; }
        public string Tail { get; }

        /// <summary>译文（未翻译/失败时为 null，则回退原文）。</summary>
        public string? Translation { get; set; }

        /// <summary>该条是否需要送翻译（无需翻译的条目从一开始就是定稿态）。</summary>
        public bool NeedsTranslation { get; set; }

        /// <summary>该条是否未能取到译文（用于统计与告警）。</summary>
        public bool TranslationFailed { get; set; }

        /// <summary>该行是否已定稿——即内容不会再变，可以写入文件、也可以推给界面。</summary>
        public bool IsFinal =>
            Kind != XcomLineKind.Entry || !NeedsTranslation || Translation is not null || TranslationFailed;

        /// <summary>按原始模板重建整行。</summary>
        public string Rebuild()
        {
            if (Kind != XcomLineKind.Entry)
            {
                return Raw;
            }

            string text = string.IsNullOrEmpty(Translation) ? Value : Translation;
            return KeyPart + "=" + LeadWhitespace + Quote + text + Quote + Tail;
        }
    }

    /// <summary>
    /// 按文件顺序把「已定稿」的行推给界面，让用户直接看到 `.chn` 文档逐行长成。
    /// <para>
    /// 只推送<em>按顺序连续就绪</em>的部分：某一行还没定稿时，它后面的行即使已译好也要等——
    /// 这样预览里的内容顺序与最终文件完全一致。
    /// </para>
    /// <para>
    /// <b>空行也会推送</b>：预览要能 1:1 还原最终文件（含空行造成的分段），否则用户看到的
    /// 段落结构与实际落盘的文件不一致。
    /// </para>
    /// </summary>
    private sealed class DocumentEmitter
    {
        private readonly List<XcomLine> _lines;
        private readonly string _fileId;
        private readonly IProgress<TranslationProgressItem> _progress;
        private int _cursor;

        public DocumentEmitter(List<XcomLine> lines, string fileId, IProgress<TranslationProgressItem> progress)
        {
            _lines = lines;
            _fileId = fileId;
            _progress = progress;
        }

        public void Flush()
        {
            while (_cursor < _lines.Count && _lines[_cursor].IsFinal)
            {
                var line = _lines[_cursor];
                _cursor++;
                // 非 Entry 行原样写回（空行就是空行、分组的原缩进也保留），Entry 行用原文模板 + 译文重建
                _progress.Report(new(_fileId, TranslationStage.OutputLine, Text: line.Rebuild() + "\n"));
            }
        }
    }

    /// <summary>
    /// 把流式增量按「编号|译文」逐条增量解析，**一收到下一条编号行就定稿上一条**。
    /// <para>
    /// 之所以要「留一条缓冲」：模型偶尔会把一条译文拆成多行，只有看到下一条编号行才能确认上一条已经结束。
    /// 最后一条在 <see cref="Finish"/> 时用权威解析结果裁决——因为只有整批响应能正确区分
    /// 「最后一条的续行」与「编号行之后的废话」。
    /// </para>
    /// </summary>
    private sealed class IncrementalBatchParser
    {
        private readonly Dictionary<string, UniqueEntry> _byId;
        private readonly StringBuilder _buffer = new();
        private string? _pendingId;
        private readonly StringBuilder _pendingText = new();

        public IncrementalBatchParser(IEnumerable<UniqueEntry> batch)
        {
            _byId = batch.ToDictionary(e => e.Id, e => e);
        }

        /// <summary>追加一段增量，返回本次新定稿的条目。</summary>
        public List<(string Id, string Text)> Append(string delta)
        {
            var done = new List<(string, string)>();
            _buffer.Append(delta);

            while (true)
            {
                int nl = _buffer.ToString().IndexOf('\n');
                if (nl < 0) break;

                string line = _buffer.ToString(0, nl);
                _buffer.Remove(0, nl + 1);
                HandleLine(line, done);
            }

            return done;
        }

        /// <summary>整批响应结束：用权威解析结果裁决最后一条。</summary>
        public List<(string Id, string Text)> Finish(IReadOnlyDictionary<string, string> authoritative)
        {
            var done = new List<(string, string)>();
            if (_pendingId is not null && _byId.ContainsKey(_pendingId))
            {
                if (authoritative.TryGetValue(_pendingId, out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    done.Add((_pendingId, text));
                }
                else if (_pendingText.Length > 0)
                {
                    done.Add((_pendingId, _pendingText.ToString()));
                }
            }

            _pendingId = null;
            _pendingText.Clear();
            return done;
        }

        private void HandleLine(string line, List<(string, string)> done)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return;
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) return;   // Markdown 围栏

            var m = NumberedLineRegex.Match(line);
            if (m.Success)
            {
                CommitPending(done);
                _pendingId = m.Groups[1].Value;
                _pendingText.Clear().Append(m.Groups[2].Value.Trim());
                return;
            }

            // 不带编号的行：视为上一条的续行（仅在确实有上一条时）
            if (_pendingId is not null)
            {
                string merged = JoinWrapped(_pendingText.ToString(), trimmed);
                _pendingText.Clear().Append(merged);
            }
        }

        private void CommitPending(List<(string, string)> done)
        {
            if (_pendingId is null) return;
            if (_byId.ContainsKey(_pendingId) && _pendingText.Length > 0)
            {
                done.Add((_pendingId, _pendingText.ToString()));
            }
            _pendingId = null;
            _pendingText.Clear();
        }
    }

    /// <summary>一个唯一待译值及其在文件中的所有出现位置。</summary>
    private sealed class UniqueEntry
    {
        public UniqueEntry(string value, List<XcomLine> items)
        {
            Value = value;
            Items = items;
        }

        public string Value { get; }
        public List<XcomLine> Items { get; }
        public string Id { get; set; } = string.Empty;
    }
}

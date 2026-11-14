using AITranslate.Core.Configuration;
using AITranslate.Core.Translation.Concurrency;

namespace AITranslate.Core.Translation;

/// <summary>
/// 按「翻译目标」创建对应的翻译引擎。
/// <para>
/// 引擎由用户在设置里选定的 <see cref="GameKind"/> 决定，而不是按文件扩展名猜测，
/// 避免同名扩展名或用户选错目标时静默走错管线。
/// </para>
/// </summary>
public static class TranslationEngineFactory
{
    /// <summary>判断文件是否属于当前选定的游戏（按扩展名）。</summary>
    public static bool IsSupported(GameKind game, string path)
    {
        var profile = GameProfiles.For(game);
        return Path.GetExtension(path).Equals(profile.FileExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按游戏创建引擎。</summary>
    public static ITranslationEngine Create(
        GameKind game,
        ChatCompletionClient client,
        GlobalRateLimiter limiter)
    {
        return game switch
        {
            GameKind.Bg3 => new Bg3Translator(client, limiter),
            _ => new XcomTranslatorV2(client, limiter),
        };
    }
}

namespace AITranslate.Core.Configuration;

/// <summary>支持翻译的游戏。</summary>
public enum GameKind
{
    /// <summary>XCOM 2（<c>.int</c> 语言文件）。</summary>
    Xcom2 = 0,

    /// <summary>博德之门 3（<c>.xml</c> 语言文件）。</summary>
    Bg3 = 1,
}

/// <summary>一种游戏的翻译特征：文件类型、输出后缀、是否支持参考文件等。</summary>
public sealed record GameProfile(
    GameKind Kind,
    string DisplayName,
    string FileExtension,
    string FileDialogFilter,
    string OutputSuffix,
    bool SupportsReferenceFile);

/// <summary>内置的游戏档案。</summary>
public static class GameProfiles
{
    public static readonly GameProfile Xcom2 = new(
        GameKind.Xcom2,
        "XCOM 2",
        ".int",
        "XCOM2 语言文件 (*.int)|*.int",
        ".chn",
        SupportsReferenceFile: false);

    public static readonly GameProfile Bg3 = new(
        GameKind.Bg3,
        "博德之门 3",
        ".xml",
        "BG3 XML 文件 (*.xml)|*.xml",
        ".chs.xml",
        SupportsReferenceFile: true);

    /// <summary>全部游戏档案（用于设置界面的下拉框）。</summary>
    public static IReadOnlyList<GameProfile> All { get; } = new[] { Xcom2, Bg3 };

    /// <summary>按枚举取档案。</summary>
    public static GameProfile For(GameKind kind) => kind == GameKind.Bg3 ? Bg3 : Xcom2;
}

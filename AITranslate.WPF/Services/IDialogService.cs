using AITranslate.Core.Configuration;

namespace AITranslate.WPF.Services;

/// <summary>
/// 与窗口 / 对话框交互的抽象，便于视图模型解耦。
/// </summary>
public interface IDialogService
{
    /// <summary>选择多个文件（按当前游戏过滤），取消返回 null。</summary>
    string[]? PickFiles(GameProfile profile);

    /// <summary>选择文件夹，取消返回 null。</summary>
    string? PickFolder();

    /// <summary>选择单个参考文件，取消返回 null。</summary>
    string? PickReferenceFile();

    /// <summary>
    /// 打开设置窗口。用户保存返回新的配置，取消返回 null。
    /// </summary>
    AppSettings? ShowSettings(AppSettings current);

    /// <summary>显示提示信息。</summary>
    void ShowMessage(string message);

    /// <summary>询问是否确认（用于切换翻译目标等破坏性操作）。</summary>
    bool Confirm(string message);
}

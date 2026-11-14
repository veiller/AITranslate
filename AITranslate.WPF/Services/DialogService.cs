using System.Windows;
using AITranslate.Core.Configuration;
using AITranslate.WPF.ViewModels;
using AITranslate.WPF.Views;
using Microsoft.Win32;

namespace AITranslate.WPF.Services;

/// <summary>
/// 基于 WPF 的对话框服务实现。
/// </summary>
public sealed class DialogService : IDialogService
{
    public string[]? PickFiles(GameProfile profile)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"选择要翻译的 {profile.DisplayName} 文件",
            Filter = $"{profile.FileDialogFilter}|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含语言文件的文件夹",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickReferenceFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择参考的已翻译文件（用于复用已有翻译）",
            Filter = "XML 文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public AppSettings? ShowSettings(AppSettings current)
    {
        var vm = new SettingsViewModel(current);
        var window = new SettingsWindow(vm)
        {
            Owner = Application.Current.MainWindow,
        };

        return window.ShowDialog() == true ? vm.ToSettings() : null;
    }

    public void ShowMessage(string message)
    {
        MessageBox.Show(Application.Current.MainWindow, message, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Confirm(string message)
    {
        return MessageBox.Show(Application.Current.MainWindow, message, "确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }
}

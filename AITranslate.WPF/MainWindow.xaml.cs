using System.IO;
using System.Windows;
using System.Windows.Controls;
using AITranslate.Core.Translation;
using AITranslate.WPF.Services;
using AITranslate.WPF.ViewModels;

namespace AITranslate.WPF;

/// <summary>
/// 主窗口：承载工具栏、拖拽区、多页签与状态栏。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var settings = SettingsService.Load();
        _viewModel = new MainViewModel(settings, new DialogService());
        DataContext = _viewModel;

        Closing += (_, _) => _viewModel.Shutdown();
    }

    /// <summary>供命令行参数使用：把启动参数里的文件加入页签。</summary>
    public void AddPaths(IEnumerable<string> paths) => _viewModel.AddPaths(paths);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.AddPaths(paths);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 仅当拖入的内容里存在「当前翻译目标支持的文件」时才接受拖拽，
    /// 否则光标显示禁止，避免拖进来却什么都没发生。
    /// </summary>
    private bool CanAcceptDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var game = _viewModel.Settings.Game;
        foreach (var path in paths)
        {
            if (File.Exists(path) && TranslationEngineFactory.IsSupported(game, path))
            {
                return true;
            }

            if (Directory.Exists(path))
            {
                try
                {
                    if (Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                        .Any(f => TranslationEngineFactory.IsSupported(game, f)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // 无权限的目录忽略
                }
            }
        }

        return false;
    }

    /// <summary>流式预览更新后自动滚动到底部。</summary>
    private void PreviewBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}

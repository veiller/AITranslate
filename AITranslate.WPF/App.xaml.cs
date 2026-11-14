using System.Windows;

namespace AITranslate.WPF;

/// <summary>
/// 应用入口。启动时创建主窗口，并把命令行参数中的文件加入页签。
/// </summary>
public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0)
        {
            window.AddPaths(e.Args);
        }
    }
}

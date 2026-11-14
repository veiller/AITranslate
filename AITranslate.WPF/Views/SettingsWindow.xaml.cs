using System.Windows;
using System.Windows.Controls;
using AITranslate.WPF.ViewModels;

namespace AITranslate.WPF.Views;

/// <summary>
/// 设置窗口。ApiKey 通过 PasswordBox 输入，在代码后置中与视图模型同步。
/// </summary>
public partial class SettingsWindow : HandyControl.Controls.Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // PasswordBox.Password 不支持直接绑定，这里初始化并与视图模型保持一致
        ApiKeyBox.Password = viewModel.ApiKey;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.ApiKey = box.Password;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.ToSettings();
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            MessageBox.Show(this, "「接口地址」与「模型名称」为必填项。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

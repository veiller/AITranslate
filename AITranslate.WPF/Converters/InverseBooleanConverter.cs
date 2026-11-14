using System.Globalization;
using System.Windows.Data;

namespace AITranslate.WPF.Converters;

/// <summary>布尔取反转换器（用于「翻译本文件」按钮的可用性）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

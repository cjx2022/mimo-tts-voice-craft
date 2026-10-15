using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MimoTtsVoiceCraft.Helpers;

/// <summary>字符串为空（或空白）时折叠元素，用于隐藏示例记录里没有的文本行。</summary>
public class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

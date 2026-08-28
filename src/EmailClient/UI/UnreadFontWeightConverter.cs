using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EmailClient.UI;

public sealed class UnreadFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

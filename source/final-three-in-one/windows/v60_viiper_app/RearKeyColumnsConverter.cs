using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Y700Switch2V60Viiper;

public sealed class RearKeyColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? width >= 710 ? 6 : width >= 500 ? 5 : 4 : 4;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TodoApp;

/// <summary>层级 → 卡片缩进（子级层层右移）</summary>
public class LevelIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = System.Convert.ToInt32(value);
        var left = level <= 0 ? 0 : 6 + 12 * Math.Min(level, 3);
        return new Thickness(left, 0, 0, 8);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>层级 → 卡片缩放（越深略小，3 层封底）</summary>
public class LevelScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = System.Convert.ToInt32(value);
        return 1.0 - 0.05 * Math.Min(level, 3);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Collapsed（悬停弹窗无图时折叠白色衬底）</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

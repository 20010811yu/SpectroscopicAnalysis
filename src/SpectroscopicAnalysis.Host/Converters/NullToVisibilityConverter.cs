using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpectroscopicAnalysis.Host.Converters;

/// <summary>null → Collapsed、非 null → Visible（对象存在性控制显隐；Inverse=True 时反转）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>是否反转（非 null → Collapsed、null → Visible）。</summary>
    public bool Inverse { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        if (Inverse)
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("NullToVisibilityConverter 不支持双向转换");
}

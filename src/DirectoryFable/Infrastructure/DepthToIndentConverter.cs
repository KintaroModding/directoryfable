using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryFable.Infrastructure;

/// <summary>Folder tree depth → left margin (18 DIP per level).</summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public const double IndentPerLevel = 18;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * IndentPerLevel : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

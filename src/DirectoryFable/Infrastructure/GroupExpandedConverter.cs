using System.Globalization;
using System.Windows.Data;
using DirectoryFable.ViewModels;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// Start state of a group divider: [group name, tab] → open unless the tab has the group in
/// its collapsed set. Used OneTime; clicks update the set (FilePane), so a re-created
/// divider (scrolling, refresh, restart) comes back the way the user left it.
/// </summary>
public sealed class GroupExpandedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        !(values is [string name, TabViewModel tab] && tab.CollapsedGroups.Contains(name));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

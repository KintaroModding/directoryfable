using System.Globalization;

namespace DirectoryFable.Infrastructure;

/// <summary>Size and date text of the file list.</summary>
public static class DisplayFormat
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary>"165 bytes", "7,45 KB", "17,1 MB", "171 MB" (3 significant digits, base 1024).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes} bytes";

        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        // 999.7 KB would print as "1.000 KB"; show "0,98 MB" instead.
        while (value >= 999.5 && unit < Units.Length - 1);

        string number = value switch
        {
            < 9.995 => value.ToString("0.00", CultureInfo.CurrentCulture),
            < 99.95 => value.ToString("0.0", CultureInfo.CurrentCulture),
            _ => value.ToString("0", CultureInfo.CurrentCulture),
        };
        return $"{number} {Units[unit]}";
    }

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// "Today  18:45", "Yesterday  18:45", the weekday for the rest of the last
    /// week ("Sunday  21:55", English as the UI), else "04.11.2026  18:45" (system date format).
    /// </summary>
    public static string Date(DateTime value)
    {
        if (value == DateTime.MinValue)
            return ""; // Unknown (e.g. network shares).

        DateTime today = DateTime.Today;
        string day = value.Date == today ? "Today"
            : value.Date == today.AddDays(-1) ? "Yesterday"
            : value.Date > today.AddDays(-7) && value.Date < today ? English.DateTimeFormat.GetDayName(value.DayOfWeek)
            : value.ToString("d", CultureInfo.CurrentCulture);
        return $"{day}  {value.ToString("t", CultureInfo.CurrentCulture)}";
    }
}

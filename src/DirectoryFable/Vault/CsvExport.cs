using System.Globalization;
using System.IO;
using System.Text;

namespace DirectoryFable.Vault;

/// <summary>
/// Writes the vault's entries as a CSV file that other password managers (and KeeSeek's own import) read: the
/// header "Group","Title","Username","Password","URL","Notes","Created","Last Modified", standard quoting ("" inside
/// quotes), UTF-8 with a byte order mark, Windows line ends. Categories in their order, entries in the user's order;
/// the group is "KeeSeek/&lt;category&gt;" (a category "Email / Work" becomes "KeeSeek/Email/Work"), so an import
/// takes the category back. Divider entries and colors are KeeSeek's own and are not written.
/// </summary>
public static class CsvExport
{
    private static readonly string[] Header = ["Group", "Title", "Username", "Password", "URL", "Notes", "Created", "Last Modified"];

    /// <summary>Writes the file; returns how many entries it holds.</summary>
    public static int Write(VaultData data, string path)
    {
        var text = new StringBuilder();
        AppendRecord(text, Header);
        int count = 0;
        var categories = data.Categories.ToDictionary(c => c.Id);
        // Entries of a category that no longer exists (should not happen) come last, under "Imported".
        var order = data.Categories.Select(c => c.Id).ToList();
        foreach (var entry in data.Entries.Where(e => !e.IsDivider)
                     .OrderBy(e => order.IndexOf(e.CategoryId) is var i and >= 0 ? i : int.MaxValue))
        {
            string category = categories.TryGetValue(entry.CategoryId, out var c) ? c.Name : "Imported";
            AppendRecord(text,
            [
                "KeeSeek/" + string.Join("/", category.Split(" / ")),
                entry.Title,
                entry.UserName,
                entry.Password,
                entry.Url,
                entry.Notes,
                Date(entry.Created),
                Date(entry.Modified),
            ]);
            count++;
        }
        // Written in one go, without a temp copy: a plain text file of passwords should exist only once.
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return count;
    }

    private static string Date(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void AppendRecord(StringBuilder text, IEnumerable<string> fields)
    {
        text.Append(string.Join(",", fields.Select(f => "\"" + (f ?? "").ReplaceLineEndings("\r\n").Replace("\"", "\"\"") + "\"")));
        text.Append("\r\n");
    }
}

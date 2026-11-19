using System.Globalization;
using System.IO;
using System.Text;

namespace DirectoryFable.Vault;

/// <summary>
/// Reads a password CSV export into vault entries. Columns are found by their header names (any order, any case):
/// group, title / account, user name / login name, password, URL / web site, notes / comments, created,
/// last modified. Two quoting styles: standard CSV ("" inside quotes) and the 1.x style that escapes with a
/// backslash (\" and \\), recognised by its header "Account","Login Name","Password","Web Site","Comments".
/// A group path "Root/Email/Work" becomes the category "Email / Work"; without a group column: "Imported".
/// </summary>
public static class CsvImport
{
    public sealed record Row(string Category, VaultEntry Entry);

    public static List<Row> Read(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        string firstLine = text.Split('\n', 2)[0].Trim();
        bool backslash = firstLine.Equals("\"Account\",\"Login Name\",\"Password\",\"Web Site\",\"Comments\"", StringComparison.OrdinalIgnoreCase);
        var records = Parse(text, backslash);
        if (records.Count == 0)
            return [];

        var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int group = Col("group", "groups", "folder", "category");
        int title = Col("title", "account", "name", "entry");
        int user = Col("username", "user name", "login name", "login", "user");
        int password = Col("password", "pass");
        int url = Col("url", "web site", "website", "site");
        int notes = Col("notes", "comments", "comment", "note");
        int created = Col("created", "creation time", "creation");
        int modified = Col("last modified", "modified", "last modification time", "last modification");
        if (password < 0)
            throw new InvalidDataException("This CSV file has no \"Password\" column.");

        string Field(List<string> record, int column) => column >= 0 && column < record.Count ? record[column] : "";
        var rows = new List<Row>();
        foreach (var record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
                continue;
            var entry = new VaultEntry
            {
                Title = Field(record, title).Trim(),
                UserName = Field(record, user),
                Password = Field(record, password),
                Url = Field(record, url).Trim(),
                Notes = Field(record, notes).Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            };
            if (entry.Title.Length == 0)
                entry.Title = entry.Url.Length > 0 ? entry.Url : entry.UserName.Length > 0 ? entry.UserName : "Entry";
            if (Date(Field(record, created)) is { } c)
                entry.Created = c;
            entry.Modified = entry.PasswordChanged = Date(Field(record, modified)) ?? entry.Created;
            rows.Add(new Row(CategoryName(Field(record, group)), entry));
        }
        return rows;
    }

    private static string CategoryName(string groupPath)
    {
        var parts = groupPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 1)
            parts.RemoveAt(0); // The database's root group.
        return parts.Count == 0 ? "Imported" : string.Join(" / ", parts);
    }

    private static DateTime? Date(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d) ? d
        : DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out d) ? d
        : null;

    /// <summary>CSV records (fields may hold commas, quotes and line breaks inside quotes).</summary>
    private static List<List<string>> Parse(string text, bool backslash)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (backslash && c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
                {
                    field.Append(text[++i]);
                }
                else if (c == '"')
                {
                    if (!backslash && i + 1 < text.Length && text[i + 1] == '"')
                        field.Append(text[++i]);
                    else
                        quoted = false;
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (any || field.Length > 0)
                    {
                        record.Add(field.ToString());
                        records.Add(record);
                    }
                    record = [];
                    field.Clear();
                    any = false;
                    break;
                default:
                    field.Append(c);
                    any = true;
                    break;
            }
        }
        if (any || field.Length > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}

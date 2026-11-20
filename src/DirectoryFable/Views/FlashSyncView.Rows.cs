using System.IO;
using System.Windows.Media;
using DirectoryFable.Infrastructure;
using DirectoryFable.Sync;

namespace DirectoryFable.Views;

/// <summary>The colors of FlashSync's cards: the job's mode and the state of its last run.</summary>
public static class SyncColors
{
    public static readonly Brush Blue = Frozen("#3399FF");
    public static readonly Brush Green = Frozen("#4CC38A");
    public static readonly Brush Orange = Frozen("#FF9A47");
    public static readonly Brush Red = Frozen("#FF7B72");
    public static readonly Brush Gray = Frozen("#8A8A8A");

    public static readonly Brush UpdateBadge = Frozen("#1F6FC0");
    public static readonly Brush WipeBadge = Frozen("#F57C22");
    public static readonly Brush UpdateTagBack = Brushes.Transparent;
    public static readonly Brush UpdateTagText = Frozen("#8CC8FF");
    public static readonly Brush WipeTagBack = Brushes.Transparent;
    public static readonly Brush WipeTagText = Frozen("#FF9A47");

    public static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    /// <summary>The same color, faint, as the background of a chip.</summary>
    public static Brush Faint(Brush brush)
    {
        var color = ((SolidColorBrush)brush).Color;
        var faint = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        faint.Freeze();
        return faint;
    }

    public static Brush Of(SyncStatus? status) => status switch
    {
        SyncStatus.Done => Green,
        SyncStatus.DoneWithErrors => Orange,
        SyncStatus.Failed => Red,
        _ => Gray,
    };
}

/// <summary>A ticked folder and where it goes, for the job's "Folders" card.</summary>
public sealed record SyncSourceRow(string Source, string Target);

/// <summary>Something that is left out (a folder, or a name pattern), as a chip.</summary>
public sealed record SyncChip(string Glyph, string Text, string Tip);

/// <summary>A number of the last run ("22", "Files").</summary>
public sealed record SyncStat(string Value, string Caption, Brush Color);

/// <summary>A job in FlashSync's list, and everything its cards show.</summary>
public sealed class SyncJobRow(SyncJob job)
{
    public SyncJob Job { get; } = job;

    public string Name => Job.Name;
    public string Destination => Job.Destination;

    // ---------- Mode ----------

    public string ModeTag => Job.Wipe ? "WIPE FIRST" : "UPDATE";
    public Brush ModeTagBack => Job.Wipe ? SyncColors.WipeTagBack : SyncColors.UpdateTagBack;
    public Brush ModeTagText => Job.Wipe ? SyncColors.WipeTagText : SyncColors.UpdateTagText;
    public Brush ModeBadge => Job.Wipe ? SyncColors.WipeBadge : SyncColors.UpdateBadge;

    public string ModeText => Job.Wipe
        ? $"Each run first deletes everything in {Job.Destination}, then copies the folders into it."
        : $"Each run copies new and changed files into {Job.Destination}. Nothing is deleted there.";

    // ---------- Folders ----------

    public IReadOnlyList<SyncSourceRow> Sources => [.. Job.Sources.Select(s => new SyncSourceRow(s, Job.TargetOf(s)))];

    public string SourcesCaption => Job.Sources.Count == 1 ? "1 folder" : $"{Job.Sources.Count} folders";

    // ---------- Left out ----------

    public IReadOnlyList<SyncChip> LeftOut =>
    [
        .. Job.ExcludedFolders.Select(folder => new SyncChip("", ShortFolder(folder), folder)),
        .. Job.ExcludedNames.Select(name => new SyncChip("", name, $"Files and folders named \"{name}\" are left out everywhere in the job.")),
    ];

    public bool NothingLeftOut => Job.ExcludedFolders.Count == 0 && Job.ExcludedNames.Count == 0;

    /// <summary>A left-out folder as it lies in its source ("Alpha\skipme").</summary>
    private string ShortFolder(string folder)
    {
        var source = Job.Sources.FirstOrDefault(s => SyncSafety.IsSameOrInside(folder, s));
        if (source is null)
            return folder;
        string inner = folder.Length > source.TrimEnd('\\').Length ? folder[(source.TrimEnd('\\').Length + 1)..] : "";
        return inner.Length > 0 ? Path.Combine(SyncJob.TargetName(source), inner) : SyncJob.TargetName(source);
    }

    // ---------- Last run ----------

    /// <summary>The last run's state; null = never run. Jobs saved before the state was kept: read from the result's words.</summary>
    public SyncStatus? Status
    {
        get
        {
            if (Job.LastRun is null)
                return null;
            if (Enum.TryParse<SyncStatus>(Job.LastStatus, out var status))
                return status;
            string text = Job.LastResult;
            return text.StartsWith("Done with", StringComparison.Ordinal) ? SyncStatus.DoneWithErrors
                : text.StartsWith("Done", StringComparison.Ordinal) ? SyncStatus.Done
                : text.StartsWith("Stopped", StringComparison.Ordinal) ? SyncStatus.Cancelled
                : SyncStatus.Failed;
        }
    }

    public Brush StatusColor => Job.LastRun is null ? SyncColors.Gray : SyncColors.Of(Status);

    public Brush StatusBack => SyncColors.Faint(StatusColor);

    public string StatusWord => Status switch
    {
        null => "Not run yet",
        SyncStatus.Done => "Done",
        SyncStatus.DoneWithErrors => Job.LastErrors is int errors ? $"Done, {SyncRunner.Problems(errors)}" : "Done with problems",
        SyncStatus.Cancelled => "Stopped",
        _ => "Failed",
    };

    public string LastRunDate => Job.LastRun is { } time ? DisplayFormat.Date(time) : "";

    /// <summary>The list row's last line: "Yesterday 23:55 · 22 files · 2,19 MB".</summary>
    public string LastRunShort
    {
        get
        {
            if (Job.LastRun is not { } time)
                return "Not run yet";
            var parts = new List<string> { DisplayFormat.Date(time) };
            if (Job.LastFiles is long files && Status != SyncStatus.Failed)
            {
                parts.Add(SyncRunner.Files(files));
                if (Job.LastBytes is long bytes)
                    parts.Add(DisplayFormat.Size(bytes));
            }
            else
            {
                parts.Add(StatusWord);
            }
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The numbers of the last run; empty for runs saved before the numbers were kept.</summary>
    public IReadOnlyList<SyncStat> Stats
    {
        get
        {
            if (Job.LastRun is null || Job.LastFiles is not long files)
                return [];
            int errors = Job.LastErrors ?? 0;
            return
            [
                new(files.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), files == 1 ? "file copied" : "files copied", SyncColors.Blue),
                new(DisplayFormat.Size(Job.LastBytes ?? 0), "copied", SyncColors.Blue),
                new(SyncRunner.Duration(TimeSpan.FromSeconds(Job.LastSeconds ?? 0)), "time", SyncColors.Blue),
                new(errors.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), errors == 1 ? "problem" : "problems",
                    errors > 0 ? SyncColors.Orange : SyncColors.Green),
            ];
        }
    }

    public bool HasStats => Stats.Count > 0;

    /// <summary>The result line is shown when the tiles do not say it all: no numbers kept, a failed or a stopped run.</summary>
    public bool ShowResult => !HasStats || Status is SyncStatus.Failed or SyncStatus.Cancelled;

    /// <summary>The run's one-line result (alone for an older run; the reason of a failed one).</summary>
    public string LastResult => Job.LastRun is null ? "Run the job to see its result here." : Job.LastResult;
}

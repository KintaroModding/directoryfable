using System.Text.Json;
using System.Text.RegularExpressions;

namespace DirectoryFable.Editor;

/// <summary>
/// A small reader for TextMate grammars (the JSON format of VS Code's language modes), for grammars whose
/// regions end at the end of the line, as the Technical Notes grammar does: every line is tokenized on its own.
/// It knows patterns, includes ("#name", "$self"), match rules with captures (also captures with patterns),
/// begin/end rules with name, contentName, beginCaptures, endCaptures and back-references (\1) in end.
/// Like VS Code: at each position the earliest match wins, on a tie the first pattern; the end of the open
/// region wins a tie; captures with patterns are tokenized again inside the capture (the text ends there).
/// </summary>
internal sealed class TextMateGrammar
{
    private sealed class Rule
    {
        public string? Include;
        public Regex? Match;
        public string? Name;
        public Dictionary<int, Capture>? Captures;
        public Regex? Begin;
        public string? End;
        public string? ContentName;
        public Dictionary<int, Capture>? BeginCaptures;
        public Dictionary<int, Capture>? EndCaptures;
        public List<Rule>? Patterns;
        public List<Rule>? Leaves;   // Patterns with includes resolved (match and begin rules only).
    }

    private sealed class Capture
    {
        public string? Name;
        public List<Rule>? Patterns;
        public List<Rule>? Leaves;
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly List<Rule> _top;
    private readonly Dictionary<string, Rule> _repository = new(StringComparer.Ordinal);
    private readonly List<Rule> _topLeaves;
    private readonly Dictionary<string, Regex> _endCache = new(StringComparer.Ordinal);

    public string ScopeName { get; }

    private TextMateGrammar(JsonElement root)
    {
        ScopeName = Text(root, "scopeName") ?? "source";
        if (root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object)
            foreach (var entry in repository.EnumerateObject())
                _repository[entry.Name] = ReadRule(entry.Value);
        _top = ReadPatterns(root);
        _topLeaves = Leaves(_top);
    }

    /// <exception cref="JsonException">Not a grammar.</exception>
    /// <exception cref="ArgumentException">A regular expression .NET cannot read.</exception>
    public static TextMateGrammar Load(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new TextMateGrammar(document.RootElement);
    }

    // ---------- Reading ----------

    private Rule ReadRule(JsonElement element)
    {
        var rule = new Rule
        {
            Include = Text(element, "include"),
            Name = Text(element, "name"),
            ContentName = Text(element, "contentName"),
            End = Text(element, "end"),
            Captures = ReadCaptures(element, "captures"),
            BeginCaptures = ReadCaptures(element, "beginCaptures"),
            EndCaptures = ReadCaptures(element, "endCaptures"),
        };
        if (Text(element, "match") is { } match)
            rule.Match = Compile(match);
        if (Text(element, "begin") is { } begin)
            rule.Begin = Compile(begin);
        if (element.TryGetProperty("patterns", out _))
            rule.Patterns = ReadPatterns(element);
        return rule;
    }

    private List<Rule> ReadPatterns(JsonElement element)
    {
        var list = new List<Rule>();
        if (element.TryGetProperty("patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array)
            foreach (var item in patterns.EnumerateArray())
                list.Add(ReadRule(item));
        return list;
    }

    private Dictionary<int, Capture>? ReadCaptures(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var captures) || captures.ValueKind != JsonValueKind.Object)
            return null;
        var result = new Dictionary<int, Capture>();
        foreach (var entry in captures.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out int group))
                continue;
            result[group] = new Capture
            {
                Name = Text(entry.Value, "name"),
                Patterns = entry.Value.TryGetProperty("patterns", out _) ? ReadPatterns(entry.Value) : null,
            };
        }
        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Compiled: about twice as fast per line; compiling the grammar's few dozen expressions happens once.
    private static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    /// <summary>The match and begin rules a pattern list stands for, with includes and pure pattern lists resolved.</summary>
    private List<Rule> Leaves(List<Rule> patterns)
    {
        var result = new List<Rule>();
        void Add(Rule rule, int depth)
        {
            if (depth > 32)
                return; // An include loop: stop.
            if (rule.Include is { } include)
            {
                if (include == "$self" || include == "$base")
                    foreach (var top in _top)
                        Add(top, depth + 1);
                else if (include.StartsWith('#') && _repository.TryGetValue(include[1..], out var target))
                    Add(target, depth + 1);
            }
            else if (rule.Match is not null || rule.Begin is not null)
            {
                result.Add(rule);
            }
            else if (rule.Patterns is not null)
            {
                foreach (var child in rule.Patterns)
                    Add(child, depth + 1);
            }
        }
        foreach (var rule in patterns)
            Add(rule, 0);
        return result;
    }

    private List<Rule> LeavesOf(Rule rule) => rule.Leaves ??= Leaves(rule.Patterns ?? []);

    private List<Rule> LeavesOf(Capture capture) => capture.Leaves ??= Leaves(capture.Patterns ?? []);

    // ---------- Tokenizing ----------

    /// <summary>The scope stack of each character of <paramref name="line"/> (null = only the grammar's scope).</summary>
    public Scope?[] Tokenize(string line)
    {
        var scopes = new Scope?[line.Length];
        var root = new Scope(ScopeName, null);
        try
        {
            Scan(line, 0, _topLeaves, null, root, scopes, out _);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological line: what was found so far stays.
        }
        return scopes;
    }

    /// <summary>Tokenizes from <paramref name="position"/> until <paramref name="end"/> matches or the text ends.</summary>
    /// <returns>Where the region's end starts (or the text's end).</returns>
    private int Scan(string text, int position, List<Rule> leaves, Regex? end, Scope scope, Scope?[] scopes, out Match? endMatch)
    {
        var cache = new Match?[leaves.Count];
        while (true)
        {
            endMatch = end?.Match(text, position);
            Match? best = null;
            int bestIndex = -1;
            for (int i = 0; i < leaves.Count; i++)
            {
                var cached = cache[i];
                if (cached is null || (cached.Success && cached.Index < position))
                    cache[i] = cached = (leaves[i].Match ?? leaves[i].Begin)!.Match(text, position);
                if (cached.Success && (best is null || cached.Index < best.Index))
                {
                    best = cached;
                    bestIndex = i;
                }
            }
            if (endMatch is { Success: true } && (best is null || endMatch.Index <= best.Index))
            {
                Paint(scopes, position, endMatch.Index, scope);
                return endMatch.Index;
            }
            endMatch = null;
            if (best is null)
            {
                Paint(scopes, position, text.Length, scope);
                return text.Length;
            }
            Paint(scopes, position, best.Index, scope);
            var rule = leaves[bestIndex];
            var ruleScope = scope.Push(rule.Name);
            int next;
            if (rule.Match is not null)
            {
                PaintMatch(text, best, rule.Captures, ruleScope, scopes);
                next = best.Index + best.Length;
            }
            else
            {
                PaintMatch(text, best, rule.BeginCaptures ?? rule.Captures, ruleScope, scopes);
                var endRegex = EndRegex(rule.End ?? "$", best);
                int inner = Scan(text, best.Index + best.Length, LeavesOf(rule), endRegex, ruleScope.Push(rule.ContentName), scopes, out var innerEnd);
                if (innerEnd is not null)
                {
                    PaintMatch(text, innerEnd, rule.EndCaptures ?? rule.Captures, ruleScope, scopes);
                    next = innerEnd.Index + innerEnd.Length;
                }
                else
                {
                    next = inner;
                }
            }
            if (next <= position && next < text.Length)
            {
                // No progress (empty matches): give this character to the current scope and go on.
                Paint(scopes, position, position + 1, scope);
                next = position + 1;
            }
            position = Math.Max(next, best.Index);
            if (position >= text.Length && end is null)
                return text.Length;
        }
    }

    /// <summary>A match gets the rule's scope; its captures their own scope, or are tokenized again with their patterns.</summary>
    private void PaintMatch(string text, Match match, Dictionary<int, Capture>? captures, Scope scope, Scope?[] scopes)
    {
        Paint(scopes, match.Index, match.Index + match.Length, scope);
        if (captures is null)
            return;
        foreach (var (number, capture) in captures.OrderBy(c => c.Key))
        {
            if (number >= match.Groups.Count || match.Groups[number] is not { Success: true, Length: > 0 } group)
                continue;
            var captureScope = scope.Push(capture.Name);
            if (capture.Patterns is { Count: > 0 })
            {
                // As VS Code: the text ends where the capture ends; lookbehind still sees the text before it.
                string truncated = text[..(group.Index + group.Length)];
                Scan(truncated, group.Index, LeavesOf(capture), null, captureScope, scopes, out _);
            }
            else
            {
                Paint(scopes, group.Index, group.Index + group.Length, captureScope);
            }
        }
    }

    /// <summary>The end regex of a region; back-references (\1) stand for the begin match's text.</summary>
    private Regex EndRegex(string end, Match begin)
    {
        string source = Regex.Replace(end, @"\\(\d)", m =>
        {
            int group = m.Groups[1].Value[0] - '0';
            return group < begin.Groups.Count ? Regex.Escape(begin.Groups[group].Value) : "";
        });
        if (!_endCache.TryGetValue(source, out var regex))
            _endCache[source] = regex = Compile(source);
        return regex;
    }

    private static void Paint(Scope?[] scopes, int from, int to, Scope scope)
    {
        for (int i = Math.Max(0, from); i < Math.Min(to, scopes.Length); i++)
            scopes[i] = scope;
    }
}

/// <summary>A scope stack, innermost first ("keyword.control.capitalized..." inside "meta.group.round..." ...).</summary>
internal sealed class Scope(string name, Scope? parent)
{
    public string Name { get; } = name;
    public Scope? Parent { get; } = parent;

    public Scope Push(string? name) => string.IsNullOrEmpty(name) ? this : new Scope(name, this);

    /// <summary>The scopes from the innermost outwards.</summary>
    public IEnumerable<string> Names()
    {
        for (var s = this; s is not null; s = s.Parent)
            yield return s.Name;
    }
}

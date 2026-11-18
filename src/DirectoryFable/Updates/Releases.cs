using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DirectoryFable.Updates;

/// <summary>A problem of the update check or the update, with a message for the user.</summary>
internal sealed class UpdateException(string message) : Exception(message);

/// <summary>The app's zip on a release: its download link, size and, when GitHub gives one, its SHA-256.</summary>
internal sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>A published release of the app.</summary>
/// <param name="Version">The version without the tag's "v", such as "1.2.0".</param>
/// <param name="Notes">The release notes as written on GitHub (Markdown).</param>
/// <param name="Asset">The zip this app updates from; null when the release has none.</param>
internal sealed record Release(string Version, DateTimeOffset? Date, string Notes, string? PageUrl, ReleaseAsset? Asset);

/// <summary>Reads the app's releases from GitHub's API (or a test copy of its answer).</summary>
internal static class Releases
{
    /// <summary>
    /// The published releases (no drafts, no pre-releases), newest version first.
    /// </summary>
    /// <exception cref="UpdateException">GitHub could not be reached or gave no usable answer.</exception>
    public static async Task<List<Release>> FetchAsync(HttpClient http, string url, CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException("No release is published yet.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub's limit for checks from this network is reached. Try again in an hour.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            var list = Parse(await response.Content.ReadAsStringAsync(timeout.Token), IsLocal(url));
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            if (cancellation.IsCancellationRequested)
                throw;
            throw new UpdateException(ex is TaskCanceledException
                ? "GitHub did not answer in time. Check the connection, then try again."
                : $"The releases could not be read: {ex.Message}");
        }
    }

    /// <summary>GitHub's answer for the release list, as releases newest first.</summary>
    /// <param name="allowHttp">Accept http links too: only for a test list on this computer.</param>
    public static List<Release> Parse(string json, bool allowHttp = false)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<Release>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return releases;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (Flag(item, "draft") || Flag(item, "prerelease") || Text(item, "tag_name") is not { } tag)
                continue;
            string version = FromTag(tag);
            var date = Text(item, "published_at") is { } published
                && DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;
            releases.Add(new Release(version, date, Text(item, "body") ?? "", Link(item, "html_url", allowHttp),
                FindAsset(item, UpdateSource.AssetName(version), allowHttp)));
        }
        // Newest version first; versions that are no dotted numbers keep GitHub's order behind them.
        return [.. releases.OrderByDescending(r => r.Version, Comparer<string>.Create((a, b) => Compare(a, b) ?? 0))];
    }

    private static ReleaseAsset? FindAsset(JsonElement release, string wanted, bool allowHttp)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), wanted, StringComparison.OrdinalIgnoreCase)
                || Link(asset, "browser_download_url", allowHttp) is not { } url)
                continue;
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            // GitHub gives "sha256:<64 hex digits>".
            string? digest = Text(asset, "digest");
            string? sha256 = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && digest[7..].All(char.IsAsciiHexDigit)
                    ? digest[7..]
                    : null;
            return new ReleaseAsset(wanted, url, size, sha256);
        }
        return null;
    }

    /// <summary>"1.2.0" for the tag "v1.2.0"; other tags stay as they are.</summary>
    public static string FromTag(string tag)
    {
        string trimmed = tag.Trim();
        return trimmed.Length > 1 && trimmed[0] is 'v' or 'V' && char.IsAsciiDigit(trimmed[1]) ? trimmed[1..] : trimmed;
    }

    /// <summary>Is <paramref name="candidate"/> a newer version than <paramref name="current"/>? Only dotted numbers count.</summary>
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>Dotted numbers compared number by number ("1.6" = "1.6.0"); null when either is no dotted number.</summary>
    public static int? Compare(string a, string b)
    {
        if (Numbers(a) is not { } x || Numbers(b) is not { } y)
            return null;
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long left = i < x.Length ? x[i] : 0;
            long right = i < y.Length ? y[i] : 0;
            if (left != right)
                return left.CompareTo(right);
        }
        return 0;
    }

    private static long[]? Numbers(string version)
    {
        string[] parts = FromTag(version).Split('.');
        var numbers = new long[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return numbers;
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>An https link (http too when allowed), else null.</summary>
    private static string? Link(JsonElement element, string name, bool allowHttp) =>
        Text(element, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp))
                ? uri.AbsoluteUri
                : null;

    /// <summary>A test list on this computer (http://127.0.0.1/..., http://localhost/...).</summary>
    private static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
}

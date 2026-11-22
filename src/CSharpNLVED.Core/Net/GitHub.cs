using System.Text.Json;

namespace CSharpNLVED.Core.Net;

public sealed record GitHubAsset(string Name, Uri DownloadUrl, long Size, DateTimeOffset UpdatedAt, string? Sha256);

public sealed record GitHubRelease(string Tag, string? Name, DateTimeOffset PublishedAt, IReadOnlyList<GitHubAsset> Assets);

/// <summary>
/// Minimal read-only GitHub releases client (anonymous; 60 requests/hour per IP,
/// which is plenty for a couple of checks per launch). Reused later for app updates.
/// </summary>
public static class GitHub
{
    public static Task<GitHubRelease> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default) =>
        GetReleaseAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);

    public static Task<GitHubRelease> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct = default) =>
        GetReleaseAsync($"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);

    private static async Task<GitHubRelease> GetReleaseAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var doc = await Http.GetJsonAsync(request, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var assets = new List<GitHubAsset>();
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            // GitHub exposes "digest": "sha256:<hex>" on release assets; older assets may lack it.
            string? sha256 = null;
            if (a.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                var value = digest.GetString();
                if (value is not null && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = value["sha256:".Length..];
            }

            assets.Add(new GitHubAsset(
                a.GetProperty("name").GetString()!,
                new Uri(a.GetProperty("browser_download_url").GetString()!),
                a.GetProperty("size").GetInt64(),
                a.GetProperty("updated_at").GetDateTimeOffset(),
                sha256));
        }

        var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetDateTimeOffset()
            : DateTimeOffset.MinValue;

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()
            : null;

        return new GitHubRelease(root.GetProperty("tag_name").GetString()!, name, published, assets);
    }
}

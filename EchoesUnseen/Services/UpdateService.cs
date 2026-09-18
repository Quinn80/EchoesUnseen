using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services;

/// <summary>
/// Checks whether a newer Echoes Unseen release is available and, if so, hands
/// back the version and the page to get it. Fully optional and best-effort:
/// any failure (offline, rate-limited, repo moved) is swallowed silently — the
/// app never blocks on it and never nags on error.
///
/// SOURCE: the GitHub "latest release" API for the public repo. GitHub redirects
/// /releases/latest to whatever release is marked latest, and the API returns its
/// tag (e.g. "b1.5") and page URL. Point <see cref="Repo"/> at the real repo.
///
/// PRIVACY: this is an anonymous GET to GitHub's public API. No account, key, or
/// personal data is sent — just a User-Agent, which GitHub requires.
/// </summary>
public static class UpdateService
{
    // owner/name of the public GitHub repository that publishes releases.
    private const string Repo = "Quinn80/EchoesUnseen";
    private const string DownloadPage = "https://github.com/" + Repo + "/releases/latest";

    public record UpdateInfo(string LatestLabel, string Url);

    /// <summary>Returns update info when a newer release exists, else null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoesUnseen-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? DownloadPage) : DownloadPage;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (!TryVersion(tag, out int major, out int minor)) return null;

            bool newer = major > current.Major || (major == current.Major && minor > current.Minor);
            return newer ? new UpdateInfo(tag, url) : null;
        }
        catch
        {
            return null;   // offline / rate-limited / repo not found — never nag
        }
    }

    /// <summary>Pull the first "X.Y" number pair out of a tag like "b1.5" or "v1.5.0".</summary>
    private static bool TryVersion(string tag, out int major, out int minor)
    {
        major = minor = 0;
        var m = Regex.Match(tag, @"(\d+)\.(\d+)");
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out major) & int.TryParse(m.Groups[2].Value, out minor);
    }
}

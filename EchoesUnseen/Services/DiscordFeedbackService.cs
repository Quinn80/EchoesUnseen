using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;

namespace EchoesUnseen.Services;

/// <summary>
/// Posts a feedback / bug report to a Discord channel through a webhook.
///
/// WHY DISCORD AND NOT EMAIL. Email needs a mailbox that exists, that someone checks,
/// and that doesn't bounce — and the address baked into the build pointed at a mailbox
/// nobody had made, so every report a user sent would have vanished. A webhook posts
/// straight into the project's own feedback channel, where reports arrive in order,
/// keep their attachments, and can be replied to in the place the community already is.
///
/// THE URL IS A SECRET. Anyone holding a webhook URL can post to that channel, so it is
/// never committed: it is read at runtime from a file in %APPDATA%, or baked in at
/// PUBLISH time from an MSBuild property that only exists on the maintainer's machine.
/// If it ever leaks, deleting the webhook in Discord kills it instantly.
///
/// NOTHING IS EVER SENT WITHOUT THE USER SAYING SO. Every send is a button press, and
/// the caller shows exactly what is going before it goes.
/// </summary>
public static class DiscordFeedbackService
{
    /// <summary>Discord's attachment ceiling is about 10 MB on an unboosted server;
    /// staying well under it leaves room for the message itself and for the limit to
    /// change without silently breaking every report.</summary>
    private const long AttachmentBudget = 7_000_000;

    /// <summary>Where a maintainer drops the webhook URL by hand. One line, nothing
    /// else. Deliberately outside the source tree so it cannot be committed.</summary>
    public static string WebhookFilePath =>
        Path.Combine(App.Settings.AppDataDirectory, "discord-webhook.txt");

    private static string? _cached;
    private static bool _resolved;

    /// <summary>The webhook to post to, or null if none is configured.</summary>
    public static string? WebhookUrl
    {
        get
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = ResolveWebhook();
            return _cached;
        }
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);

    /// <summary>Forget the cached URL, so dropping the file in takes effect without a
    /// restart.</summary>
    public static void Reload() { _resolved = false; _cached = null; }

    private static string? ResolveWebhook()
    {
        // 1. A file the maintainer put there by hand — how the dev build is configured.
        try
        {
            if (File.Exists(WebhookFilePath))
            {
                foreach (var line in File.ReadAllLines(WebhookFilePath))
                {
                    var t = line.Trim();
                    if (LooksLikeWebhook(t)) return t;
                }
            }
        }
        catch (Exception ex) { CrashLogger.Log("DiscordFeedback.ResolveWebhook(file)", ex); }

        // 2. Baked in at publish time:  dotnet publish -p:DiscordWebhook=https://...
        //    The value lives only on the machine that ran the publish, never in git.
        try
        {
            var baked = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DiscordWebhook")?.Value;
            if (LooksLikeWebhook(baked)) return baked!.Trim();
        }
        catch (Exception ex) { CrashLogger.Log("DiscordFeedback.ResolveWebhook(baked)", ex); }

        return null;
    }

    private static bool LooksLikeWebhook(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        (s.Trim().StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         s.Trim().StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
         s.Trim().StartsWith("https://ptb.discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // -- Keeping the channel usable ------------------------------------------
    //
    // These stop the ordinary kind of flooding: a stuck key, an impatient user pressing
    // Send eight times, someone bored. They are client-side, so anyone willing to edit
    // the app can step around them - which is why the durable protection is that the
    // webhook can be revoked, and why every report carries a reporter id so one person
    // can be blocked without punishing everyone else.
    private const int CooldownSeconds = 120;
    private const int MaxPerDay = 12;
    private const int MinMessageChars = 10;

    /// <summary>Whether this message may be sent right now, and if not, why - phrased to
    /// be spoken to the person rather than logged at them. Takes what the USER TYPED,
    /// not the assembled report: the report always has a version and an OS line in it,
    /// so length-checking that would wave through an empty complaint.</summary>
    public static (bool Ok, string Why) MaySend(string userMessage)
    {
        if ((userMessage ?? "").Trim().Length < MinMessageChars)
            return (false, "Please describe the problem in a few more words first.");
        return MaySendNow();
    }

    /// <summary>The rate limit alone, without judging the message.</summary>
    public static (bool Ok, string Why) MaySendNow()
    {
        var s = App.Settings.Current;
        var since = DateTime.UtcNow - s.FeedbackLastSentUtc;
        if (since.TotalSeconds < CooldownSeconds)
        {
            int wait = (int)Math.Ceiling(CooldownSeconds - since.TotalSeconds);
            return (false, $"Just a moment - you can send another report in {wait} seconds.");
        }

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (s.FeedbackSentOn == today && s.FeedbackSentCount >= MaxPerDay)
            return (false, "That is as many reports as can be sent today. Your copy is still saved in Downloads.");

        return (true, "");
    }

    private static void RecordSend()
    {
        var s = App.Settings.Current;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (s.FeedbackSentOn != today) { s.FeedbackSentOn = today; s.FeedbackSentCount = 0; }
        s.FeedbackSentCount++;
        s.FeedbackLastSentUtc = DateTime.UtcNow;
        App.Settings.NotifyChanged();
    }

    /// <summary>This installation's id, made once and kept.</summary>
    public static string ReporterId
    {
        get
        {
            var s = App.Settings.Current;
            if (string.IsNullOrWhiteSpace(s.ReporterId))
            {
                s.ReporterId = Guid.NewGuid().ToString("N")[..12];
                App.Settings.NotifyChanged();
            }
            return s.ReporterId;
        }
    }

    /// <summary>
    /// Post a report. <paramref name="zipPath"/> is optional; if it is too big for
    /// Discord it is thinned rather than dropped, so something useful still arrives.
    /// Returns whether it went, and a sentence fit to speak aloud either way.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SendAsync(
        string title, string reportText, string? zipPath)
    {
        var url = WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
            return (false, "No feedback channel is set up in this build, so nothing was sent.");

        var (allowed, why) = MaySendNow();
        if (!allowed) { DiagLog.Log("FEEDBACK", "blocked: " + why); return (false, why); }

        string? attach = null;
        string? temp = null;
        try
        {
            if (zipPath != null && File.Exists(zipPath))
            {
                if (new FileInfo(zipPath).Length <= AttachmentBudget) attach = zipPath;
                else { temp = ThinZip(zipPath); attach = temp; }
            }

            using var form = new MultipartFormDataContent();

            // Discord renders the first 2000 characters of "content"; the rest of the
            // report rides in the attachment, so keep this to the headline.
            var content = Trim($"{title}  \u00b7  reporter `{ReporterId}`\n```\n"
                               + Headline(reportText) + "\n```", 1900);
            form.Add(new StringContent(content), "content");

            if (attach != null)
            {
                var bytes = await File.ReadAllBytesAsync(attach);
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                form.Add(file, "files[0]", Path.GetFileName(attach));
            }
            else if (!string.IsNullOrWhiteSpace(reportText))
            {
                // No recording — send the report itself as a small text file so the
                // whole thing is readable rather than truncated into the message.
                var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(reportText));
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                form.Add(file, "files[0]", $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            }

            var res = await Http.PostAsync(url, form);
            if (res.IsSuccessStatusCode)
            {
                RecordSend();
                DiagLog.Log("FEEDBACK", $"sent to Discord ok (attachment: {attach ?? "none"})");
                return (true, attach == null
                    ? "Report sent to the feedback channel."
                    : "Report sent to the feedback channel, with the recording attached.");
            }

            var body = await res.Content.ReadAsStringAsync();
            DiagLog.Log("FEEDBACK", $"Discord refused it: {(int)res.StatusCode} {res.ReasonPhrase} {Trim(body, 300)}");
            return (false, $"The feedback channel refused the report ({(int)res.StatusCode}). Your copy is still saved in Downloads.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("DiscordFeedback.SendAsync", ex);
            DiagLog.Log("FEEDBACK", "send failed: " + ex.Message);
            return (false, "Could not reach the feedback channel. Your copy is still saved in Downloads.");
        }
        finally
        {
            if (temp != null) { try { File.Delete(temp); } catch { /* temp file */ } }
        }
    }

    /// <summary>
    /// Rebuild a too-large report zip small enough to send, keeping every text file and
    /// an EVENLY SPACED sample of the frames.
    ///
    /// Evenly spaced, not the first N: a bug usually shows up part-way through a
    /// recording, and a sample spread across the whole two minutes still shows the
    /// before and after. Taking the first few frames would reliably capture the moment
    /// nothing was wrong yet.
    /// </summary>
    private static string ThinZip(string zipPath)
    {
        var outPath = Path.Combine(Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(zipPath) + "-compact.zip");
        if (File.Exists(outPath)) File.Delete(outPath);

        using var src = ZipFile.OpenRead(zipPath);
        var texts = src.Entries.Where(e => !e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToList();
        var frames = src.Entries.Where(e => e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(e => e.FullName).ToList();

        long budget = AttachmentBudget - texts.Sum(e => e.Length) - 64 * 1024;
        int keep = frames.Count;
        if (frames.Count > 0)
        {
            long avg = Math.Max(1, frames.Sum(e => e.Length) / frames.Count);
            keep = (int)Math.Clamp(budget / avg, 1, frames.Count);
        }

        using (var dst = new ZipArchive(File.Create(outPath), ZipArchiveMode.Create))
        {
            foreach (var e in texts) CopyEntry(e, dst, e.FullName);

            if (frames.Count > 0)
            {
                double step = frames.Count / (double)keep;
                for (int i = 0; i < keep; i++)
                {
                    var e = frames[Math.Min(frames.Count - 1, (int)Math.Round(i * step))];
                    CopyEntry(e, dst, e.FullName);
                }
                AddText(dst, "frames/NOTE.txt",
                    $"This is a reduced copy for Discord: {keep} of {frames.Count} frames, " +
                    $"spread evenly across the recording.\r\n" +
                    $"The complete recording is in the reporter's Downloads folder as " +
                    $"{Path.GetFileName(zipPath)}.\r\n");
            }
        }
        return outPath;
    }

    private static void CopyEntry(ZipArchiveEntry from, ZipArchive to, string name)
    {
        var e = to.CreateEntry(name, CompressionLevel.Optimal);
        using var i = from.Open();
        using var o = e.Open();
        i.CopyTo(o);
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open());
        w.Write(text);
    }

    /// <summary>The first handful of lines — enough to see what a report is about in
    /// the channel without opening the attachment.</summary>
    private static string Headline(string report)
    {
        var lines = report.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(12);
        return string.Join("\n", lines);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

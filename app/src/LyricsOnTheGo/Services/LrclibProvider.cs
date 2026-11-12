using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Fetches lyrics from the hosted LRCLIB API, tolerant of the varying metadata quality different
/// players report. Three endpoints are queried concurrently — exact /api/get, structured
/// /api/search (track+artist), and a free-text /api/search?q=title+artist (the shape the website's
/// search box uses, which matches when structured metadata does not). Concurrency bounds the worst
/// case to a single request timeout instead of the sum of all three. A synced hit wins, preferring
/// the exact get, then structured, then free-text — each duration-matched; otherwise the best
/// plain-only hit. Browser sources without structured metadata take a separate title-only path (see
/// <see cref="FetchBrowserAsync"/>); with it the standard path runs first and that path is the fallback. The disk cache is consulted by the caller, not here.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Name => "lrclib";

    private static readonly HttpClient Http = CreateHttp();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>App version (major.minor.patch) pulled from the assembly, so the User-Agent tracks
    /// the single version defined in the .csproj rather than a hard-coded string.</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static HttpClient CreateHttp()
    {
        // Generous timeout: at peak, LRCLIB's structured search can take ~9 s and still return valid
        // synced results. Because the three queries run concurrently, this bounds the whole lookup.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"LyricsOnTheGo/{AppVersion} (https://github.com/The-MuffinDev/LyricsOnTheGo)");
        return http;
    }

    public async Task<LyricsResult> FetchAsync(LyricsQuery query, CancellationToken ct)
    {
        if (!query.FromBrowser)
            return await FetchStandardAsync(query.Title, query.Artist, query.Album, query.DurationMs, ct, "standard");
        if (!query.Structured)
            return await FetchBrowserAsync(query.Title, query.Artist, query.DurationMs, ct, "browser");

        // Structured browser metadata (real track + artist): standard path first, then the
        // title-only browser path (raw video title + cleaned title) when it yields no synced lyrics.
        var s = await FetchStandardAsync(query.Title, query.Artist, query.Album, query.DurationMs, ct, "structured");
        if (IsSynced(s) || s.Instrumental)
            return s;

        string raw = string.IsNullOrWhiteSpace(query.RawTitle) ? query.Title : query.RawTitle;
        var b = await FetchBrowserAsync(raw, query.Artist, query.DurationMs, ct, "structured>browser");
        if (IsSynced(b))
            return b;
        if (s.Found)
            return s; // plain-only from the structured path beats plain/nothing from the fallback
        return b.Found || b.TimedOut || !s.TimedOut ? b : s;
    }

    private static bool IsSynced(LyricsResult r) =>
        r.Found && !r.Instrumental && !string.IsNullOrWhiteSpace(r.Synced);

    // ---- Duration tolerance ------------------------------------------------------------------
    // A candidate's length is compared with the player's (video) length asymmetrically, because a
    // YouTube music video usually has an intro/outro/skit that the studio track (= the lyrics) lacks:
    //   video longer than lyrics : normal when the excess is <= 60 s OR <= 15 % of the lyric length
    //                              (typical MV intro/outro; 15 % covers long songs where 60 s is tight)
    //   lyrics longer than video : normal only within 5 s (a video shorter than the track is a
    //                              cut/edit or a different version; only encoder/rounding slack is expected)
    // Beyond that a candidate is dropped unless title AND artist both match exactly after
    // normalization (a re-timed same-song upload) - see Accept. Unknown length (0) skips the check.
    private const double VideoLongerMaxSec = 60;
    private const double VideoLongerMaxRatio = 0.15;
    private const double LyricLongerMaxSec = 5;

    private static bool WithinTolerance(double lyricSec, double videoSec)
    {
        double excess = videoSec - lyricSec; // > 0: video is longer
        return excess >= 0
            ? excess <= VideoLongerMaxSec || excess <= lyricSec * VideoLongerMaxRatio
            : -excess <= LyricLongerMaxSec;
    }

    /// <summary>True when the entry's length fits the tolerance (or either length is unknown), or - as a
    /// last resort - its name and artist both equal the query's (any of <paramref name="titles"/>) after normalization.</summary>
    private static bool Accept(LrcLibEntry e, double targetSeconds, string artist, params string[] titles)
    {
        double dur = e.Duration ?? 0;
        if (targetSeconds <= 0 || dur <= 0 || WithinTolerance(dur, targetSeconds))
            return true;

        string a = TextNorm.Normalize(artist);
        string n = TextNorm.Normalize(e.TrackName ?? "");
        if (a.Length == 0 || n.Length == 0 || TextNorm.Normalize(e.ArtistName ?? "") != a)
            return false;
        foreach (var t in titles)
            if (TextNorm.Normalize(t) == n)
                return true;
        return false;
    }

    private static double Diff(LrcLibEntry e, double targetSeconds)
    {
        double dur = e.Duration ?? 0;
        return (targetSeconds > 0 && dur > 0) ? Math.Abs(dur - targetSeconds) : double.MaxValue;
    }

    private const string ProviderName = "lrclib";

    /// <summary>One diagnostics line per lookup: path name, candidate count, |delta| seconds - never track/artist text.</summary>
    private static LyricsResult Logged(string path, int candidates, double diffSec, LyricsResult r)
    {
        string diff = diffSec == double.MaxValue ? "n/a" : diffSec.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var kind = r.TimedOut ? DiagResult.Error
            : !r.Found ? DiagResult.NotFound
            : r.Instrumental ? DiagResult.Instrumental
            : !string.IsNullOrWhiteSpace(r.Synced) ? DiagResult.Synced
            : DiagResult.Plain;
        DiagLog.LogCompleted("(lookup path)", ProviderName, kind, $"path={path} candidates={candidates} dur-diff={diff}", 0, chosen: false);
        return r;
    }

    /// <summary>
    /// Browser sources (YouTube in Chrome/Edge/...) often report the uploader/channel as the
    /// artist, so the artist is not sent to the search. Two concurrent title-only free-text searches - the
    /// raw video title and a cleaned version with "(Official Video)"-style decorations stripped -
    /// are merged into ONE candidate pool ranked by (entry name contained in the video title,
    /// duration proximity, pass). Ranking across both lists matters: the raw-title search can
    /// return a synced hit for the WRONG song (FTS ranks decoration/artist tokens high) while the
    /// cleaned search holds a near-exact match, so neither pass may win on arrival order alone.
    /// Length tolerance (see <see cref="Accept"/>) drops wrong versions; <paramref name="artist"/> is
    /// used only for its exact-match last resort.
    /// </summary>
    private async Task<LyricsResult> FetchBrowserAsync(string title, string artist, double durationMs, CancellationToken ct, string path)
    {
        double targetSeconds = durationMs / 1000.0;

        string cleaned = TitleCleaner.Clean(title);
        bool hasCleaned = cleaned.Length > 0 && !string.Equals(cleaned, title, StringComparison.OrdinalIgnoreCase);

        var rawTask = GetAsync<List<LrcLibEntry>>($"https://lrclib.net/api/search?q={Esc(title)}", ct);
        var cleanedTask = hasCleaned
            ? GetAsync<List<LrcLibEntry>>($"https://lrclib.net/api/search?q={Esc(cleaned)}", ct)
            : null;

        var (rawList, t1) = await rawTask;
        List<LrcLibEntry>? cleanedList = null;
        bool t2 = false;
        if (cleanedTask is not null)
            (cleanedList, t2) = await cleanedTask;
        bool timedOut = t1 || t2;

        var titleTokens = TextNorm.TokenSet(title);

        LyricsResult? bestSynced = null, bestPlain = null;
        var syncedScore = WorstScore;
        var plainScore = WorstScore;
        int candidates = 0;

        void Consider(List<LrcLibEntry>? list, string detail, int pass)
        {
            if (list is null)
                return;
            foreach (var e in list)
            {
                if (!Accept(e, targetSeconds, artist, title, cleaned))
                    continue;
                var r = EntryToResult(e, detail, targetSeconds);
                if (r is null)
                    continue;
                candidates++;

                var score = (TextNorm.TitleContains(titleTokens, e.TrackName ?? ""), Diff(e, targetSeconds), pass);

                if (!string.IsNullOrWhiteSpace(e.SyncedLyrics))
                {
                    if (Better(score, syncedScore)) { bestSynced = r; syncedScore = score; }
                }
                else
                {
                    if (Better(score, plainScore)) { bestPlain = r; plainScore = score; }
                }
            }
        }

        Consider(rawList, "search (q=video title)", 0);
        Consider(cleanedList, "search (q=cleaned title)", 1);

        if (bestSynced is not null)
            return Logged(path, candidates, syncedScore.Diff, bestSynced);
        if (bestPlain is not null)
            return Logged(path, candidates, plainScore.Diff, bestPlain);

        return Logged(path, candidates, double.MaxValue, timedOut
            ? LyricsResult.NotFound with { TimedOut = true, Detail = "timeout (LRCLIB slow)" }
            : LyricsResult.NotFound);
    }

    private static readonly (bool Match, double Diff, int Pass) WorstScore = (false, double.MaxValue, int.MaxValue);

    /// <summary>Candidate ranking for browser mode: title containment dominates, then duration
    /// proximity; the earlier pass (raw video title) only breaks exact ties.</summary>
    private static bool Better((bool Match, double Diff, int Pass) a, (bool Match, double Diff, int Pass) b)
    {
        if (a.Match != b.Match)
            return a.Match;
        if (Math.Abs(a.Diff - b.Diff) > 0.001)
            return a.Diff < b.Diff;
        return a.Pass < b.Pass;
    }

    private async Task<LyricsResult> FetchStandardAsync(string title, string artist, string album, double durationMs, CancellationToken ct, string path)
    {
        double targetSeconds = durationMs / 1000.0;
        int durSec = Math.Max(0, (int)(durationMs / 1000));

        string getUrl = "https://lrclib.net/api/get"
            + $"?artist_name={Esc(artist)}&track_name={Esc(title)}&album_name={Esc(album)}&duration={durSec}";
        string structuredUrl = $"https://lrclib.net/api/search?track_name={Esc(title)}&artist_name={Esc(artist)}";
        string combined = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        string qUrl = $"https://lrclib.net/api/search?q={Esc(combined)}";

        // Start all three at once; awaiting in order just collects results already in flight.
        var getTask = GetAsync<LrcLibEntry>(getUrl, ct);
        var structuredTask = GetAsync<List<LrcLibEntry>>(structuredUrl, ct);
        var qTask = GetAsync<List<LrcLibEntry>>(qUrl, ct);

        var (getEntry, t1) = await getTask;
        var (structuredList, t2) = await structuredTask;
        var (qList, t3) = await qTask;
        bool timedOut = t1 || t2 || t3;

        LyricsResult? bestPlain = null;
        double plainDiff = double.MaxValue;
        int candidates = 0;

        // 1. Exact get - ideal when the player reports clean metadata (e.g. Spotify).
        if (getEntry is not null && Accept(getEntry, targetSeconds, artist, title))
        {
            var r = EntryToResult(getEntry, "api/get (exact)", targetSeconds);
            if (r is not null)
            {
                candidates++;
                if (!string.IsNullOrWhiteSpace(r.Synced))
                    return Logged(path, candidates, Diff(getEntry, targetSeconds), r);
                bestPlain ??= r;
                plainDiff = Diff(getEntry, targetSeconds);
            }
        }

        // 2. Structured search, then 3. combined free-text search (title + artist, like the website).
        var searches = new[]
        {
            (structuredList, "search (track+artist)"),
            (qList, "search (q=title+artist)"),
        };
        foreach (var (list, detail) in searches)
        {
            var pick = SelectBest(list ?? new List<LrcLibEntry>(), targetSeconds, detail, title, artist);
            candidates += pick.Count;
            if (pick.Synced is not null)
                return Logged(path, candidates, pick.SyncedDiff, pick.Synced);
            if (bestPlain is null && pick.Plain is not null)
            {
                bestPlain = pick.Plain;
                plainDiff = pick.PlainDiff;
            }
        }

        // No synced lyrics under the whole credit list: try the main artist alone (one more round, only on a miss).
        string first = TitleCleaner.FirstArtist(artist);
        if (first != artist.Trim())
        {
            var f = await FetchStandardAsync(title, first, album, durationMs, ct, path + ">first-artist");
            if (IsSynced(f) || (bestPlain is null && f.Found))
                return f;
        }

        if (bestPlain is not null)
            return Logged(path, candidates, plainDiff, bestPlain);

        // Distinguish a timeout from a genuine miss so diagnostics can report it accurately.
        return Logged(path, candidates, double.MaxValue, timedOut
            ? LyricsResult.NotFound with { TimedOut = true, Detail = "timeout (LRCLIB slow)" }
            : LyricsResult.NotFound);
    }

    private readonly record struct Pick(
        LyricsResult? Synced, double SyncedDiff, LyricsResult? Plain, double PlainDiff, int Count);

    /// <summary>Among length-acceptable entries, picks the synced hit whose duration is closest to the
    /// player's, and likewise the closest plain-only fallback. Count = acceptable entries with lyrics.</summary>
    private Pick SelectBest(
        List<LrcLibEntry> entries, double targetSeconds, string detail, string title, string artist)
    {
        LyricsResult? bestSynced = null, bestPlain = null;
        double syncedDiff = double.MaxValue, plainDiff = double.MaxValue;
        int count = 0;

        foreach (var entry in entries)
        {
            if (!Accept(entry, targetSeconds, artist, title))
                continue;

            var r = EntryToResult(entry, detail, targetSeconds);
            if (r is null)
                continue;
            count++;

            double diff = Diff(entry, targetSeconds);
            if (!string.IsNullOrWhiteSpace(entry.SyncedLyrics))
            {
                if (bestSynced is null || diff < syncedDiff)
                {
                    bestSynced = r;
                    syncedDiff = diff;
                }
            }
            else if (bestPlain is null || diff < plainDiff)
            {
                bestPlain = r;
                plainDiff = diff;
            }
        }

        return new Pick(bestSynced, syncedDiff, bestPlain, plainDiff, count);
    }

    /// <summary>Maps an LRCLIB entry to a <see cref="LyricsResult"/> (Detail carries the matched
    /// entry's name/artist and its duration + Δ vs the player's), or null when it carries no lyrics.</summary>
    private LyricsResult? EntryToResult(LrcLibEntry e, string detail, double targetSeconds)
    {
        string name = e.TrackName ?? "";
        string artist = e.ArtistName ?? "";
        if (name.Length > 0)
            detail += artist.Length > 0 ? $": “{name}” · {artist}" : $": “{name}”";
        detail += DurLabel(e.Duration ?? 0, targetSeconds);

        if (e.Instrumental)
            return new LyricsResult { Found = true, Instrumental = true, Source = Name, Detail = detail };

        string synced = e.SyncedLyrics ?? "";
        string plain = e.PlainLyrics ?? "";
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return null;

        return new LyricsResult { Found = true, Synced = synced, Plain = plain, Source = Name, Detail = detail };
    }

    /// <summary>" · 3:33 (Δ2.0s)" for the matched entry, or "" when it reports no duration.</summary>
    private static string DurLabel(double dur, double target)
    {
        if (dur <= 0)
            return "";
        string label = $" · {(int)(dur / 60)}:{(int)dur % 60:00}";
        if (target > 0)
            label += $" (Δ{Math.Abs(dur - target):0.0}s)";
        return label;
    }

    /// <summary>GETs and deserializes; the bool reports a timeout (HttpClient.Timeout elapsed) as
    /// distinct from a track-change cancellation or a genuine empty/error response.</summary>
    private static async Task<(T? Value, bool TimedOut)> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (default, false);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return (await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, true); // the request timeout elapsed, not a caller-requested cancellation
        }
        catch
        {
            return (default, false);
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s ?? "");

    private sealed class LrcLibEntry
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>The kind of lyrics state currently shown for a track.</summary>
public enum LyricsKind { NoTrack, Searching, Synced, Plain, Instrumental, NotFound, OnlyUnsynced, Skipped }

/// <summary>Immutable snapshot of the lyrics state, carrying synced lines or plain text when applicable.
/// <see cref="Source"/> is where the lyrics came from (e.g. "youtube-captions"), empty when unknown.</summary>
public sealed record LyricsState(
    LyricsKind Kind,
    IReadOnlyList<LyricLine>? Lines = null,
    string? PlainText = null,
    string Source = "");

/// <summary>
/// Drives lyrics lookup for the current track. Order: cached lyrics are shown at once; official YouTube
/// captions (reported by the extension, passing the lyrics filter, not switched off by the user for that
/// video) beat the provider pipeline; otherwise the provider pipeline (max 2 attempts, 2500 ms backoff)
/// result replaces the cached one only when it differs and is not a lower-grade kind. Raises
/// <see cref="Changed"/> on the calling (UI) thread as the state progresses. Plain text is surfaced only
/// when <see cref="PlainFallback"/> is on; otherwise the track reports OnlyUnsynced.
/// </summary>
public sealed class LyricsController
{
    private const int MaxAttempts = 2;
    private const int RetryMs = 2500;
    private const int CaptionWaitMs = 1500;     // how long to hold back for the extension's caption report

    private readonly LyricsAggregator _client = new(LyricsProviders.Entries);
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;   // constructed on the UI thread
    private string _currentKey = "";
    private CancellationTokenSource? _cts;
    private NowPlaying? _np;                    // latest track handed to OnTrack
    private NowPlaying? _lastRaw;               // latest state exactly as reported (also when the lookup was skipped)
    private string _captionVideo = "";          // video whose caption-derived lyrics (official or re-timed) are displayed
    private string _captionKind = "";           // "captions" (official) | "aligned" (reference lyrics re-timed on auto captions)
    private string _refVideo = "";              // video the last provider result (re-timing reference) belongs to
    private string _mixVideo = "";              // video whose track list is being / was tried for the current lookup
    private LyricsResult? _captionResult;       // the caption-derived lyrics on screen (re-shown after an ad break)
    private CancellationToken _waitOwner;       // lookup currently waiting for a caption report (the late-arrival handler stays out)
    private string _waitVideo = "";
    private const int LongVideoMs = 600_000;    // only videos at least this long are treated as possible mixes
    private LyricsResult? _refResult;
    private const double MinShiftMs = 1500;     // re-timing that moves lines less than this on average is not worth the risk

    public LyricsController() => CaptionHub.Received += OnCaptionsReceived;

    public bool PlainFallback { get; set; } = true;

    public event Action<LyricsState>? Changed;

    /// <summary>Raised on the UI thread when caption availability / use for the current video may have changed.</summary>
    public event Action? CaptionsChanged;

    /// <summary>True when the official captions of <paramref name="videoId"/> are what is displayed.</summary>
    public bool ShowingCaptions(string videoId) => CaptionsShown(videoId);

    // A track without a video id (a plain media-session source such as Spotify) can never be "showing captions":
    // comparing two empty ids as equal made its lookup result silently never reach the screen.
    private bool CaptionsShown(string videoId) => videoId.Length > 0 && _captionVideo == videoId;

    /// <summary>"captions" | "aligned" | "mix" when caption-derived lyrics are displayed for <paramref name="videoId"/>, else "".</summary>
    public string CaptionKind(string videoId) => ShowingCaptions(videoId) ? _captionKind : "";

    /// <summary>Reacts to a now-playing change: cancels any in-flight lookup and starts a new one when the track changes.</summary>
    public void OnTrack(NowPlaying np)
    {
        _lastRaw = np;
        // The user said this "non-music" video is music: look it up like any song.
        if (np.Skip == "nonmusic" && np.VideoId.Length > 0 && CaptionHub.IsForcedMusic(np.VideoId))
            np = np with { Skip = "" };

        // Bridge says ad / non-music: no lookup; PlainText carries the reason code ("ad" | "nonmusic").
        if (np.HasSession && np.Skip.Length > 0)
        {
            string skipKey = np.Key + "|skip:" + np.Skip;
            if (skipKey == _currentKey)
                return;
            _currentKey = skipKey;
            DiagLog.LogCompleted(LyricsAggregator.Track(np.Title, np.Artist), "lookup", DiagResult.Plain,
                $"[{np.VideoId}|dur {np.DurationMs / 1000:0}s] skipped: {np.Skip}", 0, false);
            _cts?.Cancel();
            Raise(new LyricsState(LyricsKind.Skipped, PlainText: np.Skip));
            return;
        }

        if (!np.HasSession || (string.IsNullOrWhiteSpace(np.Title) && string.IsNullOrWhiteSpace(np.Artist)))
        {
            if (_currentKey != "")
            {
                _currentKey = "";
                _cts?.Cancel();
                _np = null;
                _captionVideo = "";
                Raise(new LyricsState(LyricsKind.NoTrack));
            }
            return;
        }

        _np = np;

        // The in-flight identity includes whether structured metadata (track/artist) has arrived: the
        // extension may report a video first without it and fill it in a moment later, and that must
        // trigger a (better) lookup. The on-disk cache key stays np.Key / KeyRaw(np.Key) - unchanged.
        string key = np.Key + (np.Structured ? "|structured" : "");
        if (key == _currentKey)
            return;

        // Caption-derived lyrics already shown for this very video: structured metadata arriving later changes nothing.
        // Coming back from an ad break the screen still says "ad", so show them again.
        if (np.VideoId.Length > 0 && _captionVideo == np.VideoId && !CaptionHub.IsRejected(np.VideoId))
        {
            bool fromAd = _currentKey.Contains("|skip:", StringComparison.Ordinal);
            _currentKey = key;
            if (fromAd && _captionResult is { } keep)
                Deliver(keep);
            return;
        }

        _currentKey = key;
        DiagLog.LogCompleted(LyricsAggregator.Track(np.Title, np.Artist), "lookup", DiagResult.Plain,
            $"[{np.VideoId}|dur {np.DurationMs / 1000:0}s] structured={np.Structured} meta={np.MetaSource} browser={np.IsBrowser}", 0, false);
        if (_captionVideo != np.VideoId)
            _captionVideo = "";
        _mixVideo = "";   // every new lookup may try the track list again (a cancelled attempt must not block it)
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = FetchLoopAsync(np, _cts.Token);
    }

    /// <summary>User override for a video wrongly classified as non-music: remember it and look the song up now.</summary>
    public void ForceMusic(string videoId)
    {
        if (videoId.Length == 0) return;
        CaptionHub.SetForcedMusic(videoId);
        _currentKey = "";
        if (_lastRaw is { } raw && raw.VideoId == videoId)
            OnTrack(raw);
    }

    /// <summary>User toggle: stop / resume using the official captions of <paramref name="videoId"/>, then redo the lookup.</summary>
    public void SetCaptionsRejected(string videoId, bool rejected)
    {
        CaptionHub.SetRejected(videoId, rejected);
        _currentKey = "";
        _captionVideo = "";
        _captionKind = "";
        _mixVideo = "";
        if (_np is { } np && np.VideoId == videoId)
            OnTrack(np);
        CaptionsChanged?.Invoke();
    }

    private async Task FetchLoopAsync(NowPlaying np, CancellationToken ct)
    {
        string cacheKey = np.VideoId.Length > 0
            ? LyricsCache.KeyRaw(np.Key)   // per video: stable across title/length edits
            : LyricsCache.Key(np.Title, np.Artist, np.Album, np.DurationMs);

        // A browser video without structured metadata is a provisional lookup (extension data not ready yet:
        // title/channel guess). Its result must not be cached under the per-video key, or it would be served
        // again once the real track/artist arrives.
        bool provisional = np.IsBrowser && np.VideoId.Length > 0 && !np.Structured;
        // With a local database linked, lookups are already instant - bypass the disk cache entirely so
        // results stay fresh and every lookup is timed live in diagnostics.
        bool useCache = !LocalDbConfig.IsLinked && !provisional;
        bool captionsEligible = np.IsBrowser && np.VideoId.Length > 0 && !CaptionHub.IsRejected(np.VideoId);

        LyricsResult? shown = null;
        if (useCache)
        {
            var cached = LyricsCache.Read(cacheKey);
            if (cached is not null)
            {
                DiagLog.LogCompleted(
                    LyricsAggregator.Track(np.Title, np.Artist), "cache",
                    DiagResult.CacheHit, cached.Source, 0, chosen: true);
                Deliver(cached);
                shown = cached;
            }
        }
        if (shown is null)
            Raise(new LyricsState(LyricsKind.Searching));

        var query = new LyricsQuery(np.Title, np.Artist, np.Album, np.DurationMs, np.IsBrowser,
            np.Structured, np.RawTitle);
        // A long video may be a mix: hold the provider lookup back until the extension's report (with the description's
        // track list) is in, so a wrong title-based guess is never shown first. Shorter videos look up concurrently.
        bool maybeMix = captionsEligible && np.DurationMs >= LongVideoMs;
        // Cached and re-checked within a week: no network lookup, the cached result goes through the same path below
        // (re-timing to captions etc.). Older entries are looked up again and replaced only if the lyrics changed.
        bool fresh = shown is not null && LyricsCache.IsFresh(cacheKey);
        Task<LyricsResult>? provTask = maybeMix || fresh ? null : FetchProvidersAsync(query, ct);

        if (captionsEligible)
        {
            _waitOwner = ct; _waitVideo = np.VideoId;
            CaptionsInfo? info;
            try { info = await CaptionHub.WaitAsync(np.VideoId, CaptionWaitMs, ct); }
            finally { if (_waitOwner == ct) _waitVideo = ""; }
            if (ct.IsCancellationRequested)
                return;
            if (MixEligible(np, info) && _mixVideo != np.VideoId)
            {
                if (await RunMixAsync(np, info!, ct))
                    return;
                if (shown is not null)
                    Deliver(shown);   // RunMixAsync put "Searching" on screen: bring the cached lyrics back
            }
            if (ct.IsCancellationRequested)
                return;
            if (TryCaptions(np, info))
            {
                if (provTask is not null)
                    _ = CacheLaterAsync(provTask, useCache, cacheKey);   // keep the provider result for when captions are switched off
                CaptionsChanged?.Invoke();
                return;
            }
        }

        provTask ??= fresh ? Task.FromResult(shown!) : FetchProvidersAsync(query, ct);
        var result = await provTask;
        if (ct.IsCancellationRequested)
            return;

        if (result.Found)
        {
            _refVideo = np.VideoId;
            _refResult = result;
            // Same lyrics: keep what is shown. Different: take the fresh result unless it is a lower-grade kind
            // (synced > instrumental > plain) than the cached one. The cache always keeps the ORIGINAL timing.
            bool replace = shown is null || (!SameLyrics(shown, result) && Rank(result) >= Rank(shown));
            if (replace && useCache)
                LyricsCache.Write(cacheKey, result);
            else if (useCache && !fresh)
                LyricsCache.Touch(cacheKey);   // re-checked, unchanged: quiet for another week
            if (!CaptionsShown(np.VideoId))   // late captions may have taken over meanwhile
            {
                bool aligned = captionsEligible && await Task.Run(() => TryAlign(np, result, CaptionHub.Get(np.VideoId), ct));
                if (ct.IsCancellationRequested)
                    return;   // the track changed while the re-timing ran: do not show this result on the new song
                if (!aligned && replace)
                    Deliver(result);
            }
        }
        else if (shown is null && !CaptionsShown(np.VideoId))
            Raise(new LyricsState(LyricsKind.NotFound));
        CaptionsChanged?.Invoke();
    }

    /// <summary>The provider pipeline with its bounded retry.</summary>
    private async Task<LyricsResult> FetchProvidersAsync(LyricsQuery query, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxAttempts && !ct.IsCancellationRequested; attempt++)
        {
            LyricsResult result;
            try { result = await _client.FetchAsync(query, ct); }
            catch { result = LyricsResult.NotFound; }

            if (ct.IsCancellationRequested || result.Found)
                return result;

            try { await Task.Delay(RetryMs, ct); }
            catch (TaskCanceledException) { break; }
        }
        return LyricsResult.NotFound;
    }

    private static async Task CacheLaterAsync(Task<LyricsResult> prov, bool useCache, string cacheKey)
    {
        try
        {
            var r = await prov;
            if (useCache && r.Found)
                LyricsCache.Write(cacheKey, r);
        }
        catch { /* cache is best-effort */ }
    }

    private static int Rank(LyricsResult r) =>
        !r.Found ? 0 : r.Instrumental ? 2 : !string.IsNullOrWhiteSpace(r.Synced) ? 3 : !string.IsNullOrWhiteSpace(r.Plain) ? 1 : 0;

    private static bool SameLyrics(LyricsResult a, LyricsResult b) =>
        a.Instrumental == b.Instrumental && a.Synced == b.Synced && a.Plain == b.Plain;

    /// <summary>Delivers official captions as the lyrics when they pass the lyrics filter. Logs one diagnostics row.</summary>
    private bool TryCaptions(NowPlaying np, CaptionsInfo? info)
    {
        string track = LyricsAggregator.Track(np.Title, np.Artist);
        if (info is null)
        {
            DiagLog.LogCompleted(track, CaptionLyrics.SourceName, DiagResult.NotFound, "no caption report from the extension", 0, false);
            return false;
        }
        var r = CaptionLyrics.ToLyrics(info, np.DurationMs, out string reason);
        DiagLog.LogCompleted(track, CaptionLyrics.SourceName, r is null ? DiagResult.NotFound : DiagResult.Synced,
            $"[{np.VideoId}|dur {np.DurationMs / 1000:0}s] " + (r?.Detail ?? "not used: " + reason), 0, chosen: r is not null);
        if (r is null)
            return false;
        _captionVideo = np.VideoId;
        _captionKind = "captions";
        _captionResult = r;
        Deliver(r);
        return true;
    }

    /// <summary>Re-times the reference (provider) lyrics onto the video's auto-generated captions, when those exist, the
    /// match is trustworthy and the timing really differs (cover / video with an intro). Logs one diagnostics row.
    /// The maths run on a worker thread; the delivery is posted back to the UI thread.</summary>
    private bool TryAlign(NowPlaying np, LyricsResult reference, CaptionsInfo? info, CancellationToken ct)
    {
        bool plainRef = string.IsNullOrWhiteSpace(reference.Synced);   // untimed lyrics: sync them on the captions instead
        if (info is null || info.AsrWords.Count < 8 || reference.Instrumental
            || (plainRef && string.IsNullOrWhiteSpace(reference.Plain)))
            return false;   // nothing to try: stay silent
        string track = LyricsAggregator.Track(np.Title, np.Artist);
        var refLines = plainRef ? LyricAligner.FromPlain(reference.Plain, np.DurationMs) : LrcParser.Parse(reference.Synced);
        var r = LyricAligner.Align(refLines, info.AsrWords, out string reason);
        if (r is null)
        {
            DiagLog.LogCompleted(track, LyricAligner.SourceName, DiagResult.NotFound, "not re-timed: " + reason, 0, false);
            return false;
        }
        double shift = LyricAligner.MeanShiftMs(refLines, r.Synced);
        if (!plainRef && shift < MinShiftMs)
        {
            DiagLog.LogCompleted(track, LyricAligner.SourceName, DiagResult.NotFound, $"kept original timing (mean shift {shift / 1000:0.0}s)", 0, false);
            return false;
        }
        DiagLog.LogCompleted(track, LyricAligner.SourceName, DiagResult.Synced,
            plainRef ? $"{r.Detail}, synced from plain lyrics" : $"{r.Detail}, mean shift {shift / 1000:0.0}s", 0, chosen: true);
        PostToUi(() =>
        {
            if (ct.IsCancellationRequested || _np?.VideoId != np.VideoId || CaptionHub.IsRejected(np.VideoId)) return;   // moved on / switched off meanwhile
            _captionVideo = np.VideoId;
            _captionKind = "aligned";
            _captionResult = r;
            Deliver(r);
            CaptionsChanged?.Invoke();
        });
        return true;
    }

    private static bool MixEligible(NowPlaying np, CaptionsInfo? info) =>
        info?.Tracks is { Count: >= TrackList.MinTracks } t && np.DurationMs >= LongVideoMs && t[^1].StartMs < np.DurationMs;

    /// <summary>Mix / playlist upload with a timestamped track list in the description: look every song up and join them
    /// on one timeline. False (nothing shown yet) when no song had synced lyrics, so the normal lookup can run.</summary>
    private async Task<bool> RunMixAsync(NowPlaying np, CaptionsInfo info, CancellationToken ct)
    {
        _mixVideo = np.VideoId;
        Raise(new LyricsState(LyricsKind.Searching));
        string track = LyricsAggregator.Track(np.Title, np.Artist);
        var (res, found, total) = await MixLyrics.BuildAsync(info.Tracks!, (int)np.DurationMs, FetchOneSongAsync, ct);
        if (ct.IsCancellationRequested)
            return true;   // superseded: whoever cancelled owns the display now (and resets _mixVideo for its own lookup)
        if (res is null)
        {
            DiagLog.LogCompleted(track, MixLyrics.SourceName, DiagResult.NotFound, $"track list found but no song had synced lyrics (0/{total})", 0, false);
            return false;
        }
        DiagLog.LogCompleted(track, MixLyrics.SourceName, DiagResult.Synced, res.Detail, 0, chosen: true);
        _captionVideo = np.VideoId;
        _captionKind = "mix";
        _captionResult = res;
        Deliver(res);
        CaptionsChanged?.Invoke();
        return true;
    }

    /// <summary>One song of a mix: disk cache first, then the provider pipeline (single attempt).</summary>
    private async Task<LyricsResult> FetchOneSongAsync(LyricsQuery q, CancellationToken ct)
    {
        string key = LyricsCache.Key(q.Title, q.Artist, "", 0);
        if (!LocalDbConfig.IsLinked && LyricsCache.Read(key) is { Found: true } cached)
            return cached;
        var r = await _client.FetchAsync(q, ct).ConfigureAwait(false);
        if (r.Found && !LocalDbConfig.IsLinked)
            LyricsCache.Write(key, r);
        return r;
    }

    private async Task MixLateAsync(NowPlaying np, CaptionsInfo info, CancellationToken ct)
    {
        if (!await RunMixAsync(np, info, ct) && !ct.IsCancellationRequested)
        {
            _currentKey = "";   // no lyrics for any song of the list: back to the normal lookup
            OnTrack(np);
        }
    }

    private void PostToUi(Action a)
    {
        if (_ui is not null) _ui.Post(_ => a(), null); else a();
    }

    /// <summary>Captions arrived after the lookup started (slow extension / slow fetch): switch to them when usable.</summary>
    private void OnCaptionsReceived(string videoId)
    {
        void Apply()
        {
            var np = _np;
            if (np is null || np.VideoId != videoId || !np.IsBrowser || CaptionHub.IsRejected(videoId))
                return;
            if (_waitVideo == videoId)
                return;   // the lookup is waiting for this very report and handles it itself (no double delivery)
            if (_captionVideo != videoId)
            {
                var info = CaptionHub.Get(videoId);
                if (MixEligible(np, info) && _mixVideo != videoId)
                {
                    // late track list: take over from the lookup in flight (started here = on the UI thread)
                    _cts?.Cancel();
                    _cts = new CancellationTokenSource();
                    _ = MixLateAsync(np, info!, _cts.Token);
                }
                else if (!TryCaptions(np, info) && _refVideo == videoId && _refResult is { } rr)
                {
                    var ct = _cts?.Token ?? CancellationToken.None;
                    _ = Task.Run(() => TryAlign(np, rr, info, ct));
                }
            }
            CaptionsChanged?.Invoke();
        }
        PostToUi(Apply);
    }

    /// <summary>Translates a fetched result into a <see cref="LyricsState"/> and raises it, parsing synced LRC into lines.</summary>
    private void Deliver(LyricsResult result)
    {
        DiagLog.LogCompleted(_np is { } d ? LyricsAggregator.Track(d.Title, d.Artist) : "", "deliver", DiagResult.Plain,
            $"[{_np?.VideoId}] instrumental={result.Instrumental} synced={!string.IsNullOrWhiteSpace(result.Synced)} plain={!string.IsNullOrWhiteSpace(result.Plain)} source={result.Source}", 0, false);
        if (result.Instrumental)
        {
            Raise(new LyricsState(LyricsKind.Instrumental));
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Synced))
        {
            var lines = LrcParser.Parse(result.Synced);
            if (lines.Count > 0)
            {
                Raise(new LyricsState(LyricsKind.Synced, Lines: lines, Source: result.Source));
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            Raise(PlainFallback
                ? new LyricsState(LyricsKind.Plain, PlainText: result.Plain, Source: result.Source)
                : new LyricsState(LyricsKind.OnlyUnsynced));
            return;
        }

        Raise(new LyricsState(LyricsKind.NotFound));
    }

    private void Raise(LyricsState state) => Changed?.Invoke(state);
}

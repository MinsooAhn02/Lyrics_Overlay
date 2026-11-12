using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Builds one synced lyric list for a mix / playlist upload: every song of its track list is looked up on its own and
/// placed at the song's start time, with a "♪ Artist - Title" marker line at each song change. Lines that would run
/// into the next song are dropped. Songs without synced lyrics keep only their marker. Needs no audio recognition:
/// the song list comes from the description.
/// </summary>
public static class MixLyrics
{
    public const string SourceName = "mix";
    private const int Parallel = 4;
    private const int PerSongTimeoutMs = 15000;

    /// <param name="fetch">Looks one song up (the caller adds disk caching).</param>
    /// <returns>The combined result, or null when no song had synced lyrics at all.</returns>
    public static async Task<(LyricsResult? Result, int Found, int Total)> BuildAsync(
        IReadOnlyList<TrackEntry> tracks, int videoDurationMs,
        Func<LyricsQuery, CancellationToken, Task<LyricsResult>> fetch, CancellationToken ct)
    {
        var found = new LyricsResult?[tracks.Count];
        using var gate = new SemaphoreSlim(Parallel);
        var work = new List<Task>();
        for (int i = 0; i < tracks.Count; i++)
        {
            int k = i;
            work.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    one.CancelAfter(PerSongTimeoutMs);
                    var t = tracks[k];
                    // Duration 0 = unknown: mixes trim / crossfade songs, so the usual length check would drop good matches.
                    var q = new LyricsQuery(t.Title, t.Artist, "", 0, FromBrowser: true, Structured: true, RawTitle: t.Raw);
                    var r = await fetch(q, one.Token).ConfigureAwait(false);
                    if (r.Found && !r.Instrumental && !string.IsNullOrWhiteSpace(r.Synced)) found[k] = r;
                }
                catch (OperationCanceledException) { /* this song timed out (or the whole build was cancelled) */ }
                catch { /* a failing lookup only costs this song its lyrics */ }
                finally { gate.Release(); }
            }, ct));
        }
        try { await Task.WhenAll(work).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, 0, tracks.Count); }

        var all = new List<(double Ms, string Text)>();
        int n = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            double start = t.StartMs;
            double end = i + 1 < tracks.Count ? tracks[i + 1].StartMs : (videoDurationMs > 0 ? videoDurationMs : double.MaxValue);
            all.Add((start, "♪ " + (t.Artist.Length > 0 ? t.Artist + " - " + t.Title : t.Title)));
            if (found[i] is not { } r) continue;
            n++;
            foreach (var l in LrcParser.Parse(r.Synced))
            {
                double at = start + l.TimeMs;
                if (at >= end) break;                                    // belongs to the next song's time slot
                all.Add((at, l.Text));
            }
        }
        if (n == 0) return (null, 0, tracks.Count);

        // stable by time: a marker precedes lyric lines that start at the same moment
        var ordered = new List<(double Ms, string Text)>(all.Count);
        foreach (var x in all.Select((v, idx) => (v, idx)).OrderBy(p => p.v.Ms).ThenBy(p => p.idx)) ordered.Add(x.v);

        var sb = new StringBuilder();
        foreach (var (ms, text) in ordered)
        {
            int cs = (int)(ms / 10);
            sb.Append('[').Append((cs / 6000).ToString("00", CultureInfo.InvariantCulture)).Append(':')
              .Append((cs / 100 % 60).ToString("00", CultureInfo.InvariantCulture)).Append('.')
              .Append((cs % 100).ToString("00", CultureInfo.InvariantCulture)).Append("] ").Append(text).Append('\n');
        }
        return (new LyricsResult
        {
            Found = true,
            Synced = sb.ToString(),
            Source = SourceName,
            Detail = $"track list: {n}/{tracks.Count} songs with lyrics",
        }, n, tracks.Count);
    }
}

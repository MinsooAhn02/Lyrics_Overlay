using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Turns official YouTube captions into a synced LRC result - or null when they do not look like lyrics
/// (sound-effect tags, too few lines, only the start of the video covered). The user can still switch a
/// video's captions off by hand; this filter only keeps the obvious non-lyrics out.
/// </summary>
public static class CaptionLyrics
{
    public const string SourceName = "youtube-captions";
    private const int MinLines = 6;
    private const double MinKeptRatio = 0.5;     // kept lines / all lines
    private const double MinCoverage = 0.4;      // last kept line must start after this share of the video

    // Whole-line sound/speaker tags: [Music], (♪), ♪♪, >> NAME:, ♫ ...
    private static readonly Regex Bracketed = new(@"[\[\(（【][^\]\)）】]*[\]\)）】]", RegexOptions.Compiled);
    private static readonly Regex Leading = new(@"^(?:\s|>>+|-(?=\s)|[♪♫♬])+", RegexOptions.Compiled);
    private static readonly Regex Trailing = new(@"[\s♪♫♬]+$", RegexOptions.Compiled);
    private static readonly Regex NoLetters = new(@"^[\s\p{P}\p{S}♪♫♬]*$", RegexOptions.Compiled);

    public static LyricsResult? ToLyrics(CaptionsInfo c, double durationMs, out string reason)
    {
        reason = "";
        if (c.Status != "ok" || c.Lines.Count == 0) { reason = "status=" + c.Status; return null; }

        var kept = new List<(int Ms, string Text)>();
        foreach (var (ms, raw) in c.Lines)
        {
            string t = Trailing.Replace(Leading.Replace(Bracketed.Replace(raw, " "), ""), "");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (t.Length == 0 || NoLetters.IsMatch(t)) continue;
            if (kept.Count > 0 && kept[^1].Text == t && ms - kept[^1].Ms < 1500) continue;   // rolling-caption duplicate
            kept.Add((ms, t));
        }

        if (kept.Count < MinLines) { reason = $"only {kept.Count} lyric-like lines"; return null; }
        if ((double)kept.Count / c.Lines.Count < MinKeptRatio) { reason = $"mostly tags ({kept.Count}/{c.Lines.Count})"; return null; }
        if (durationMs > 0 && kept[^1].Ms < durationMs * MinCoverage) { reason = "covers only the start of the video"; return null; }

        var sb = new StringBuilder();
        foreach (var (ms, t) in kept)
        {
            int cs = ms / 10;
            sb.Append('[').Append((cs / 6000).ToString("00", CultureInfo.InvariantCulture)).Append(':')
              .Append((cs / 100 % 60).ToString("00", CultureInfo.InvariantCulture)).Append('.')
              .Append((cs % 100).ToString("00", CultureInfo.InvariantCulture)).Append("] ").Append(t).Append('\n');
        }
        return new LyricsResult
        {
            Found = true,
            Synced = sb.ToString(),
            Source = SourceName,
            Detail = $"official captions ({c.Lang}) {kept.Count}/{c.Lines.Count} lines",
        };
    }
}

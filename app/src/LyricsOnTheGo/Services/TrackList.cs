using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LyricsOnTheGo.Services;

/// <summary>One entry of a timestamped track list from a video description (mix / playlist upload / full album).</summary>
public sealed record TrackEntry(int StartMs, string Title, string Artist, string Raw);

/// <summary>
/// Parses the track list that mix / playlist / album uploads put in their description:
/// lines like "00:00 Artist - Title", "1:02:30 Title", "[03:15] 2. Artist – Title".
/// Returns null unless there are at least <see cref="MinTracks"/> entries with strictly increasing times.
/// "Artist - Title" is assumed for "A - B" (the common order); the lyric lookup also retries with the raw line.
/// </summary>
public static class TrackList
{
    public const int MinTracks = 3;
    private const int MaxTracks = 60;

    private static readonly Regex Line = new(
        @"^\s*(?:[\[\(]\s*)?(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\s*(?:[\]\)])?\s*[-–—:|.)]*\s*(?<rest>\S.*?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Numbering = new(@"^\d{1,3}\s*[.)\-]\s+", RegexOptions.Compiled);
    private static readonly string[] Seps = { " - ", " – ", " — ", " | " };

    public static List<TrackEntry>? Parse(string? description, int durationMs = 0)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var list = new List<TrackEntry>();
        foreach (var raw in description.Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (s > 59 || (h > 0 && mi > 59)) continue;                       // not a time ("12:75")
            long ms = ((h * 60L + mi) * 60 + s) * 1000;
            if (ms > 86_400_000) continue;

            string rest = Numbering.Replace(m.Groups["rest"].Value, "").Trim().Trim('"', '“', '”', '\'');
            if (rest.Length < 2) continue;

            string artist = "", title = rest;
            foreach (var sep in Seps)
            {
                int i = rest.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0 && i + sep.Length < rest.Length) { artist = rest[..i].Trim(); title = rest[(i + sep.Length)..].Trim(); break; }
            }
            if (list.Count > 0 && ms <= list[^1].StartMs) return null;           // not a track list (times must increase)
            if (durationMs > 0 && ms >= durationMs) return null;                  // times past the end: not this video's list
            list.Add(new TrackEntry((int)ms, title, artist, rest));
            if (list.Count > MaxTracks) return null;
        }
        return list.Count >= MinTracks ? list : null;
    }
}

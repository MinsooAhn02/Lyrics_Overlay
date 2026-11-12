using System;
using System.Linq;

namespace LyricsOnTheGo.Services;

/// <summary>Does a browser media session (title only) still show the song of the last extension report?
/// Used when the extension's reports go stale (background-tab timers throttled to ~1/min) so the app keeps
/// the video's identity instead of flipping to the session and searching again.</summary>
public static class SameSong
{
    public static readonly TimeSpan MaxReportAge = TimeSpan.FromMinutes(3);

    public static bool Matches(string? sessionTitle, string? reportTitle, string? reportTrack)
    {
        string s = Norm(sessionTitle);
        if (s.Length < 2)
            return false;
        // Exact (normalized) match only: the session shows the video title (YouTube) or the track (YT Music),
        // the same strings the report carries. Substring matching would keep "Love" when "Love Story" autoplays.
        return s == Norm(reportTitle) || s == Norm(reportTrack);
    }

    private static string Norm(string? v) => new((v ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

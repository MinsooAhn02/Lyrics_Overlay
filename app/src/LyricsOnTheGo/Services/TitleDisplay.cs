using System.Text.RegularExpressions;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Title as shown in the overlay header: the song name without the clutter uploaders add - emoji and symbols
/// ("(♬)", "🎵", "★"), hashtags, and bracketed decorations ("[Official MV]", "【公式MV】", "[SUB]"). A featuring credit
/// "(feat. X)" stays. Display only: lookups use their own cleaning.
/// </summary>
public static class TitleDisplay
{
    // Symbols (So: emoji, ♪♫♬, ★, ♡ ...), surrogate halves of astral emoji, variation selectors, joiners.
    private static readonly Regex Symbols = new(@"[\p{So}\p{Cs}︀-️​-‍⁠﻿]", RegexOptions.Compiled);
    private static readonly Regex Hashtag = new(@"(?<![\p{L}\p{N}])#[\p{L}\p{N}_]+", RegexOptions.Compiled);
    private static readonly Regex EmptyBrackets = new(@"[(\[{【（]\s*[)\]}】）]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex EdgePunct = new(@"^[\s\-–—|:~·•]+|[\s\-–—|:~·•]+$", RegexOptions.Compiled);

    /// <summary>Cleaned title; the trimmed original when cleaning would leave nothing; "" for blank input.</summary>
    public static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string s = Symbols.Replace(title, " ");
        s = Hashtag.Replace(s, " ");
        s = TitleCleaner.Clean(s);                       // "(Official MV)", "[Lyrics]", "【公式MV】", trailing "M/V" ...
        s = EmptyBrackets.Replace(s, " ");               // what the removals above left behind: "( )"
        s = Spaces.Replace(s, " ");
        s = EdgePunct.Replace(s, "").Trim();
        return s.Length > 0 ? s : title.Trim();
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Cleans YouTube-style video titles for lyric search: strips bracketed decorations like
/// "(Official Video)" or "[Letra]" and pipe-separated decoration segments, then collapses the
/// leftovers. It never splits artist from title — the cleaned string is searched whole as free
/// text, since LRCLIB's FTS matches its words across both the name and artist columns.
/// </summary>
public static class TitleCleaner
{
    // Latin decoration words. \b-delimited; multi-word forms first so they win over their parts.
    private const string LatinDecor =
        @"\b(?:music\s+video|performance\s+video|official|oficial|video|audio|lyric\w*|letra|visuali[sz]er" +
        @"|m/v|mv|hd|hq|4k|8k|remaster\w*|explicit|videoclip|karaoke|sub\w*|topic|teaser|inst)\b";

    // Hangul / Japanese: \b is useless between CJK letters ("공식뮤직비디오", "公式MV"), so use
    // lookarounds against the same script instead. '+' lets compounds ("공식 MV" is two tokens, "공식뮤비" one).
    // Keeps a word like "가사랑" intact (followed by a Hangul syllable).
    private const string HangulDecor =
        @"(?<![가-힣])(?:공식|뮤직비디오|뮤비|가사\s*포함|가사|라이브|티저)+(?![가-힣])";
    private const string JpDecor =
        @"(?<![\u3040-\u30ff\u3400-\u9fff])(?:公式|歌詞|ミュージックビデオ|リリックビデオ|ライブ)+(?![\u3040-\u30ff\u3400-\u9fff])";

    private const string Decor = "(?:" + LatinDecor + "|" + HangulDecor + "|" + JpDecor + ")";

    // Bracket pairs, incl. full-width 【】（）. 「」 are deliberately NOT brackets: Japanese titles are quoted with them.
    private const string Open = @"[(\[{【（]";
    private const string Close = @"[)\]}】）]";
    private const string Inner = @"[^()\[\]{}【】（）]*";

    // "(…official…)", "[…letra…]", "{…mv…}" — removed only when a decoration word appears inside.
    private static readonly Regex Bracketed = new(
        $@"{Open}{Inner}{Decor}{Inner}{Close}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "(Live …)", "[Live at …]", "(Prod. …)": only when the group STARTS with the word, so a
    // title such as "(I Live For You)" survives. "feat./ft." is intentionally kept (part of LRCLIB names).
    private static readonly Regex LeadWordBracketed = new(
        $@"{Open}\s*(?:live|prod)\b\.?{Inner}{Close}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DecorSegment = new(Decor, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare trailing decorations without brackets ("Song M/V", "Song Official Audio"): if they
    // survived, PrepareInput would turn "m/v" into the FTS terms "m" AND "v", killing the match.
    private static readonly Regex TrailingDecor = new(
        // The first decoration may directly follow a closing quote/bracket with no space ("「夜に駆ける」Official Music Video").
        $@"((?:\s+|(?<=[」』”’)\]】）])){Decor})(\s+{Decor})*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    // Expected output (Clean(input) -> output):
    //  | input                                                   | output                                   |
    //  | Adele - Hello (Official Music Video)                    | Adele - Hello                            |
    //  | 아이유(IU) '좋은 날(Good Day)' Official MV                | 아이유(IU) '좋은 날(Good Day)'            |
    //  | Dua Lipa - Levitating (feat. DaBaby) (Official Lyric Video) | Dua Lipa - Levitating (feat. DaBaby) |
    //  | Coldplay - Yellow (Live in Paris 2020)                  | Coldplay - Yellow                        |
    //  | [가사/Lyrics] 에스파 - Supernova                          | 에스파 - Supernova                       |
    //  | Radiohead - (Nice Dream) (Official Video)               | Radiohead - (Nice Dream)  (paren is the title: kept) |
    //  | 米津玄師 - Lemon【公式MV】                                  | 米津玄師 - Lemon                         |
    //  | YOASOBI「夜に駆ける」Official Music Video                   | YOASOBI「夜に駆ける」                     |
    //  | 선우정아 - 도망가자 (Prod. by X) 뮤비                       | 선우정아 - 도망가자                       |
    //  | (Official Video)                                        | (Official Video)  (empty result -> original) |
    // "Jvcki Wai, Coogie, Paloalto & 뱃사공" / "A x B" / "A feat. B" -> "A". LRCLIB files collaborations under the main
    // artist, so a lookup with the whole list finds nothing. " and " is not a separator (band names: "Simon and Garfunkel").
    private static readonly Regex ArtistSeparator = new(
        @"\s*(?:,|&|＆|、|/|\s[xX×]\s|\s(?:feat\.?|ft\.?|featuring|with)\s)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The first artist of a credit list; the trimmed input when it names only one (or the first is under 2 chars).</summary>
    public static string FirstArtist(string artist)
    {
        string a = (artist ?? "").Trim();
        string first = ArtistSeparator.Split(a, 2)[0].Trim();
        return first.Length >= 2 ? first : a;
    }

    /// <summary>Returns the cleaned title; the trimmed original when cleaning would leave nothing; "" only for blank input.</summary>
    public static string Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        string s = Bracketed.Replace(title, " ");
        s = LeadWordBracketed.Replace(s, " ");

        // Pipe-separated decoration ("Song | Official Video"): drop only the segments containing
        // a decoration word — some channels use '|' between artist and song, which must survive.
        if (s.IndexOf('|') >= 0)
        {
            var kept = new List<string>();
            foreach (var part in s.Split('|'))
                if (!DecorSegment.IsMatch(part))
                    kept.Add(part.Trim());
            if (kept.Count > 0)
                s = string.Join(" ", kept);
        }

        s = TrailingDecor.Replace(s, " ");
        s = MultiSpace.Replace(s, " ").Trim().Trim(' ', '-', '–', '—', '|');
        return s.Length > 0 ? s : title.Trim();
    }
}

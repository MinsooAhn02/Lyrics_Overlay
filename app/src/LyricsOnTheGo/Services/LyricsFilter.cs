using System.Collections.Generic;

namespace LyricsOnTheGo.Services;

public static class LyricsFilter
{
    /// <summary>
    /// True when kana (hiragana U+3040-309F, katakana U+30A0-30FF, half-width katakana U+FF66-FF9F)
    /// make up >= 20% of all letters (whitespace, punctuation, digits excluded). Kanji alone never
    /// counts, so Chinese / Hanja-mixed Korean is not misdetected. Null/empty input -> false.
    ///
    /// Hand-checked (letters = char.IsLetter):
    ///   input                              letters kana  ratio  result
    ///   "夜に駆ける 君の声が聞こえる"             12     7    58%    true
    ///   "Hello ありがとう"                       10     5    50%    true
    ///   "I wanna hold your hand"            18     0     0%    false
    ///   "사랑해요 너무나"                         7     0     0%    false
    ///   "我爱你中国"                              5     0     0%    false
    ///   "Café ア love you so much"          18     1   5.6%    false  (one stray kana)
    ///   boundary: 4 kana of 20 letters      20     4    20%    true   (>=)
    ///   null / [] / ["", "  ", "♪"]          0     0     -     false  (no letters)
    /// </summary>
    public static bool IsMostlyJapanese(IEnumerable<string> lines)
    {
        if (lines is null)
            return false;
        long letters = 0, kana = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
                continue;
            foreach (char c in line)
            {
                if (!char.IsLetter(c))
                    continue;
                letters++;
                if ((c >= '぀' && c <= 'ゟ') || (c >= '゠' && c <= 'ヿ')
                    || (c >= 'ｦ' && c <= 'ﾟ'))
                    kana++;
            }
        }
        return letters > 0 && kana * 5 >= letters;
    }
}

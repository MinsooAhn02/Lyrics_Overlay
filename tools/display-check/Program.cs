// Run: dotnet run -c Release --project tools\display-check   (exit code = number of failed checks)
using LyricsOnTheGo.Services;

int fails = 0;
void T(string name, string input, string want)
{
    string got = TitleDisplay.Clean(input);
    bool ok = got == want; if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}  '{input}' -> '{got}'" + (ok ? "" : $"   (want '{want}')"));
}

T("(♬) prefix (the reported case)", "(♬) 고스타그램 (GHOSTagram)", "고스타그램 (GHOSTagram)");
T("emoji around the title", "🎵 Blueming 🎵", "Blueming");
T("emoji + official tag", "Dynamite (Official MV) 🔥", "Dynamite");
T("featuring credit is kept, tags and hashtags go", "Song (feat. DaBaby) [Lyrics] #kpop #신곡", "Song (feat. DaBaby)");
T("[SUB] decoration", "[SUB] 따마(THAMA) - Do It For Love (Feat. X)", "따마(THAMA) - Do It For Love (Feat. X)");
T("stars and hearts", "★ Hype Boy ♡", "Hype Boy");
T("emoji with variation selector", "Title ❤️", "Title");
T("astral emoji (surrogate pair)", "Fire 🔥🔥 track", "Fire track");
T("Japanese brackets", "米津玄師 - Lemon【公式MV】", "米津玄師 - Lemon");
T("music note and tilde", "♪~Hello", "Hello");
T("plain title untouched", "Spider-Man: No Way Home", "Spider-Man: No Way Home");
T("dots and Hangul jamo untouched", "ㄷ.R.E.A.M", "ㄷ.R.E.A.M");
T("only emoji -> keep the original rather than show nothing", "🎵", "🎵");
T("hashtag glued to a word is not a hashtag", "C#minor study", "C#minor study");
T("brackets emptied by the removals vanish", "Title (🎵)", "Title");
T("artist - title kept as is", "IU - Blueming", "IU - Blueming");
T("blank", "   ", "");
// SameSong: does the browser's media session still show the stale extension report's song?
void S(string name, string? session, string? title, string? track, bool want)
{
    bool got = SameSong.Matches(session, title, track);
    bool ok = got == want; if (!ok) fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} same-song {name} -> {got}");
}
S("YouTube: session title = video title", "청혼", "청혼", null, true);
S("YT Music: session title = track", "Fedexx Girl (Feat. 창모)", "BIBI - Fedexx Girl (Feat. 창모) [Official MV]", "Fedexx Girl (Feat. 창모)", true);
S("punctuation / case differ", "FEDEXX GIRL", "Fedexx-Girl!", null, true);
S("different song", "Completely Different Tune", "BIBI - Fedexx Girl", "Fedexx Girl", false);
S("empty session title", "", "청혼", "청혼", false);
S("session title only symbols", "♪ ♬", "♪ 청혼", null, false);
S("no report text", "청혼", null, "", false);
S("one-letter title never matches", "a", "a", null, false);
S("autoplay to a longer title is a new song", "Love Story", "Love", null, false);
S("autoplay to a live version is a new song", "Song", "Song (Live)", null, false);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

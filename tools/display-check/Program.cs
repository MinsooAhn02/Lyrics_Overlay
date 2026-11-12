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
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

// Run: dotnet run -c Release --project tools\aligner-check   (exit code = number of failed checks)
using System.Globalization;
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

int fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {extra}"); if (!ok) fails++; }

var rnd = new Random(7);
string[] en = "love fire night road heart rain dream light fall stay home sky blue run cry sing hold time gone wild free mind soul dance burn fade ocean silver shadow river".Split(' ');
string[] ko = "사랑 하늘 바람 꿈속 그대 마음 나의 별빛 오늘 영원 눈물 기억 거리 시간 노래 언제나 슬픈 아름다운 세상 우리".Split(' ');

// Reference: original timing, 3 s per ~5-word line, chorus (lines 4-7) repeated at the end.
List<LyricLine> MakeRef(string[] vocab, int lines, int chorusAt = 4, int chorusLen = 4)
{
    var texts = new List<string>();
    for (int i = 0; i < lines; i++) texts.Add(string.Join(' ', Enumerable.Range(0, 5).Select(_ => vocab[rnd.Next(vocab.Length)])));
    for (int i = 0; i < chorusLen; i++) texts.Add(texts[chorusAt + i]);
    return texts.Select((t, i) => new LyricLine(10000 + i * 3000.0, t)).ToList();
}

// ASR for a "cover": every ref line sung at map(origTime); words spread over 2.4 s; noise = share of letters replaced.
List<(int, string)> MakeAsr(IReadOnlyList<LyricLine> r, Func<double, double> map, double noise, Func<int, bool>? skip = null, double jitter = 0, double drop = 0)
{
    var asr = new List<(int, string)>();
    for (int i = 0; i < r.Count; i++)
    {
        if (skip != null && skip(i)) continue;
        var words = r[i].Text.Split(' ');
        for (int w = 0; w < words.Length; w++)
        {
            if (drop > 0 && rnd.NextDouble() < drop) continue;                       // ASR missed this word
            var cs = words[w].ToCharArray();
            for (int k = 0; k < cs.Length; k++) if (rnd.NextDouble() < noise) cs[k] = (char)('a' + rnd.Next(26));
            asr.Add(((int)(map(r[i].TimeMs) + w * 2400.0 / words.Length + (rnd.NextDouble() * 2 - 1) * jitter), new string(cs)));
        }
    }
    return asr;
}

// Mean / max |aligned - expected| over all lines.
(double mean, double max) Err(LyricsResult res, IReadOnlyList<LyricLine> r, Func<double, double> map)
{
    var got = LrcParser2(res.Synced);
    var e = got.Select((g, i) => Math.Abs(g - map(r[i].TimeMs))).ToList();
    return (e.Average(), e.Max());
}
List<double> LrcParser2(string lrc) => lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l =>
{
    int mm = int.Parse(l.Substring(1, 2)), ss = int.Parse(l.Substring(4, 2)), cs = int.Parse(l.Substring(7, 2));
    return (mm * 60 + ss) * 1000.0 + cs * 10;
}).ToList();

// 1. pure offset, clean ASR
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 7000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0), out var why);
    var (mean, max) = res == null ? (999, 999) : Err(res, r, f);
    Check("offset +7s, clean ASR", res != null && max < 600, $"mean={mean:0} max={max:0} {why}");
}
// 2. noisy ASR (25% wrong letters)
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 4000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.25), out var why);
    var (mean, max) = res == null ? (999, 999) : Err(res, r, f);
    Check("offset +4s, 25% wrong letters", res != null && mean < 800, $"mean={mean:0} max={max:0} {why}");
}
// 3. tempo 1.1x slower and offset
{
    var r = MakeRef(en, 24); Func<double, double> f = t => 3000 + (t - 10000) * 1.1 + 10000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.15), out var why);
    var (mean, max) = res == null ? (999, 999) : Err(res, r, f);
    Check("tempo x1.1 + offset", res != null && mean < 900, $"mean={mean:0} max={max:0} {why}");
}
// 4. a verse (lines 8-13) not sung in the cover: lines around it stay right, order never goes backwards
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 5000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.1, i => i is >= 8 and <= 13), out var why);
    bool mono = false; double e0 = 999;
    if (res != null)
    {
        var got = LrcParser2(res.Synced);
        mono = got.Zip(got.Skip(1), (a, b) => b >= a).All(x => x);
        e0 = Math.Abs(got[2] - f(r[2].TimeMs));
    }
    Check("skipped verse: monotonic, early lines right", res != null && mono && e0 < 600, $"err(line2)={e0:0} {why}");
}
// 5. Korean with jamo-level errors (a wrong final consonant per word must not break matching)
{
    var r = MakeRef(ko, 20); Func<double, double> f = t => t + 6000;
    var asr = MakeAsr(r, f, 0).Select(a => (a.Item1, a.Item2.Length > 1 ? a.Item2[..^1] + "은" : a.Item2)).ToList();
    var res = LyricAligner.Align(r, asr, out var why);
    var (mean, max) = res == null ? (999, 999) : Err(res, r, f);
    Check("Korean, last syllable wrong in every word", res != null && mean < 800, $"mean={mean:0} max={max:0} {why}");
}
// 6. unrelated captions -> refuse (null) with a reason
{
    var r = MakeRef(en, 24);
    var other = MakeAsr(MakeRef(ko, 24), t => t, 0);
    var res = LyricAligner.Align(r, other, out var why);
    Check("unrelated captions rejected", res == null && why.Length > 0, why);
}
// 7. no / too little ASR, too few lines -> null, no throw
{
    var r = MakeRef(en, 24);
    Check("empty ASR rejected", LyricAligner.Align(r, new List<(int, string)>(), out var w1) == null && w1.Length > 0, w1);
    Check("tiny reference rejected", LyricAligner.Align(r.Take(2).ToList(), MakeAsr(r, t => t, 0), out var w2) == null, w2);
}
// 8. [Music] tags and empty words in ASR are ignored, not fatal
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 2000;
    var asr = MakeAsr(r, f, 0.1); asr.Insert(3, (5000, "[Music]")); asr.Insert(9, (9000, "")); asr.Insert(12, (9500, "♪"));
    var res = LyricAligner.Align(r, asr, out var why);
    var (mean, _) = res == null ? (999, 999) : Err(res, r, f);
    Check("[Music]/empty/♪ tokens ignored", res != null && mean < 800, $"mean={mean:0} {why}");
}
// 10. hard case: sections drift differently (+3.0 s then +6.5 s), 20% letters wrong, 15% words missed, +-300 ms timing jitter.
//     Also proves the check can fail: the result must beat the UNALIGNED timing by a wide margin.
{
    var r = MakeRef(en, 30); Func<double, double> f = t => t < 40000 ? t + 3000 : t + 6500;
    var asr = MakeAsr(r, f, 0.2, null, 300, 0.15);
    var res = LyricAligner.Align(r, asr, out var why);
    double baseline = r.Average(l => Math.Abs(l.TimeMs - f(l.TimeMs)));
    var (mean, max) = res == null ? (999999, 999999) : Err(res, r, f);
    Check("hard case beats unaligned timing", res != null && mean < 1200 && mean < baseline / 3, $"aligned mean={mean:0} max={max:0} unaligned mean={baseline:0} {why}");
}
// 11. wrong-order trap: a line that also occurs LATER must not pull an early line forward (chorus repeated at the end)
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 3000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.1), out _);
    var got = res == null ? new List<double>() : LrcParser2(res.Synced);
    int chorus = 4, repeat = r.Count - 4;                                         // line 4 and its repeat
    Check("repeated chorus placed at both places", got.Count == r.Count && Math.Abs(got[chorus] - f(r[chorus].TimeMs)) < 600
        && Math.Abs(got[repeat] - f(r[repeat].TimeMs)) < 600, got.Count == r.Count ? $"first={got[chorus] - f(r[chorus].TimeMs):0} last={got[repeat] - f(r[repeat].TimeMs):0}" : "no result");
}
// 12. unrelated text in the SAME script and vocabulary must be refused (earlier test used Korean vs English only)
{
    var r = MakeRef(en, 24);
    var other = MakeAsr(MakeRef(en, 24), t => t, 0);          // different random lines drawn from the same words
    var res = LyricAligner.Align(r, other, out var why);
    Check("unrelated same-vocabulary captions rejected", res == null, res == null ? why : "ACCEPTED: " + res.Detail);
}
// 13. a real jump in the cover (verse cut: everything after line 12 is 18 s earlier) is followed, not treated as outlier
{
    var r = MakeRef(en, 30); Func<double, double> f = t => t < 46000 ? t + 4000 : t - 14000;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.1, null, 150, 0.05), out var why);
    var (mean, max) = res == null ? (999999, 999999) : Err(res, r, f);
    Check("timeline jump (cut verse) followed", res != null && mean < 2500, $"mean={mean:0} max={max:0} {why}");
}
// 9. output order equals reference order and line count is preserved
{
    var r = MakeRef(en, 24); Func<double, double> f = t => t + 1500;
    var res = LyricAligner.Align(r, MakeAsr(r, f, 0.2), out _);
    Check("line count preserved", res != null && res.Synced.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == r.Count);
}
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

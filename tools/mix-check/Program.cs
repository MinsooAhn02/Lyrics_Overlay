// Run: dotnet run -c Release --project tools\mix-check   (exit code = number of failed checks)
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

int fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} {extra}"); if (!ok) fails++; }

// ---- TrackList.Parse ----
var d1 = "Best K-pop mix 🎧\n\nTracklist:\n00:00 IU - Blueming\n03:41 NewJeans – Hype Boy\n07:02 Title only song\n\nfollow me";
var t1 = TrackList.Parse(d1);
Check("basic list parsed", t1 is { Count: 3 } && t1[0].Artist == "IU" && t1[0].Title == "Blueming" && t1[1].StartMs == 221000 && t1[1].Artist == "NewJeans" && t1[2].Artist == "" && t1[2].Title == "Title only song");
var t2 = TrackList.Parse("[00:00] 1. A - One\n[03:10] 2. B - Two\n(06:20) 3) C - Three\n1:02:30 - D - Four");
Check("brackets, numbering, hours", t2 is { Count: 4 } && t2[0].Title == "One" && t2[2].Title == "Three" && t2[3].StartMs == 3750000 && t2[3].Title == "Four", t2 == null ? "null" : string.Join(" | ", t2.Select(x => $"{x.StartMs}:{x.Artist}/{x.Title}")));
Check("CRLF line endings", TrackList.Parse("0:00 A - a1\r\n3:00 B - b1\r\n6:00 C - c1\r\n") is { Count: 3 });
Check("fewer than 3 rejected", TrackList.Parse("0:00 A - a\n3:00 B - b") == null);
Check("non-increasing times rejected", TrackList.Parse("0:00 A - a\n6:00 B - b\n3:00 C - c") == null);
Check("duplicate time rejected", TrackList.Parse("0:00 A - a\n3:00 B - b\n3:00 C - c") == null);
Check("times past the video end rejected", TrackList.Parse("0:00 A - a\n3:00 B - b\n99:00 C - c", 1200000) == null);
Check("not times (12:75) ignored", TrackList.Parse("0:00 A - a\n12:75 nope\n3:00 B - b\n6:00 C - c") is { Count: 3 });
Check("no description / empty", TrackList.Parse(null) == null && TrackList.Parse("") == null && TrackList.Parse("just words") == null);
Check("more than 60 rejected", TrackList.Parse(string.Join("\n", Enumerable.Range(0, 70).Select(i => $"{i}:00 A - t{i}"))) == null);
Check("separator inside quoted title kept", TrackList.Parse("0:00 \"Don't Stop - Remix\"\n3:00 B - b\n6:00 C - c") is { Count: 3 } x && x[0].Title.Length > 0);

// ---- MixLyrics.BuildAsync with a fake lookup ----
var tracks = TrackList.Parse("0:00 A - One\n3:00 B - Two\n6:00 C - Three\n9:00 D - Four")!;   // 4 songs, 3 min each
string Lrc(params (int s, string t)[] l) => string.Join("\n", l.Select(x => $"[{x.s / 60:00}:{x.s % 60:00}.00] {x.t}"));
Task<LyricsResult> Fake(LyricsQuery q, CancellationToken ct) => q.Title switch
{
    "One" => Task.FromResult(new LyricsResult { Found = true, Synced = Lrc((2, "one-a"), (60, "one-b")) }),
    "Two" => Task.FromResult(new LyricsResult { Found = true, Synced = Lrc((0, "two-a"), (100, "two-b"), (190, "two-LATE")) }),  // 190 s > 180 s slot
    "Three" => Task.FromResult(new LyricsResult { Found = true, Plain = "plain only" }),                                      // no timing
    _ => throw new InvalidOperationException("lookup failed"),                                                              // Four
};
var (res, found, total) = await MixLyrics.BuildAsync(tracks, 12 * 60 * 1000, Fake, CancellationToken.None);
var text = res?.Synced ?? "";
Check("songs found / total", found == 2 && total == 4, $"{found}/{total}");
Check("markers for every song", text.Contains("♪ A - One") && text.Contains("♪ B - Two") && text.Contains("♪ C - Three") && text.Contains("♪ D - Four"));
Check("lyrics shifted by song start", text.Contains("[00:02.00] one-a") && text.Contains("[03:00.00] two-a") && text.Contains("[04:40.00] two-b"));
Check("line past the song's slot dropped", !text.Contains("two-LATE"));
Check("plain-only and failing songs keep only a marker", !text.Contains("plain only"));
Check("marker precedes a lyric at the same time", text.IndexOf("♪ B - Two") < text.IndexOf("two-a"));
var times = LrcParser.Parse(text).Select(l => l.TimeMs).ToList();
Check("times are non-decreasing", times.Zip(times.Skip(1), (a, b) => b >= a).All(x => x));

var (none, f0, _) = await MixLyrics.BuildAsync(tracks, 0, (q, c) => Task.FromResult(LyricsResult.NotFound), CancellationToken.None);
Check("no song found -> null", none == null && f0 == 0);

using var cts = new CancellationTokenSource(); cts.Cancel();
var (cancelled, _, _) = await MixLyrics.BuildAsync(tracks, 0, Fake, cts.Token);
Check("cancelled -> null, no throw", cancelled == null);

var slow = await MixLyrics.BuildAsync(tracks.Take(3).ToList(), 0, async (q, c) => { await Task.Delay(50, c); return new LyricsResult { Found = true, Synced = Lrc((1, q.Title)) }; }, CancellationToken.None);
Check("parallel lookups all complete", slow.Found == 3, slow.Found.ToString());

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

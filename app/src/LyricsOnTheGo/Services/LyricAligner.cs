using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Re-times reference lyrics (e.g. the ORIGINAL song's lines from LRCLIB) onto a video whose own timing differs
/// (cover, live, different edit) using that video's auto-generated captions as a noisy clock. No ML, no audio:
///  1. Reference lines and ASR text are normalized to a letter stream (lowercase, no punctuation, Hangul split into jamo
///     so one wrong final consonant costs one edit, not a whole syllable).
///  2. Each reference line is searched in the ASR stream by approximate substring matching (edit distance, free start/end).
///  3. A monotonic chain over the per-line candidates picks the best ordered set of anchors (repeated choruses cannot
///     jump backwards; skipped sections just leave lines unmatched).
///  4. Unmatched lines are placed by interpolating the original timestamps between neighbouring anchors.
/// Returns null (with a reason) when too few lines match to trust the result.
/// </summary>
public static class LyricAligner
{
    public const string SourceName = "aligned";

    private const double MinLineSim = 0.5;        // a line counts as found when 1 - editDistance/length >= this
    private const double GainBase = 0.4;          // gain of a match = length * (sim - GainBase): weak matches add little
    private const int CandidatesPerLine = 6;
    private const double MinAnchorRatio = 0.3;    // anchors / lines
    private const int MinAnchors = 4;
    private const int MinLineChars = 8;           // shorter lines (normalized) match anywhere by chance: never anchors
    private const double MinMeanSim = 0.6;

    /// <param name="reference">Reference lines with their ORIGINAL timestamps (synced LRC parsed).</param>
    /// <param name="asr">ASR words: start time (ms) and text, in time order.</param>
    public static LyricsResult? Align(IReadOnlyList<LyricLine> reference, IReadOnlyList<(int Ms, string Text)> asr,
        out string reason)
    {
        reason = "";
        if (reference.Count < 4 || asr.Count < 8) { reason = "too little text to align"; return null; }

        // ---- ASR stream: normalized letters, each with a time ----
        var text = new List<char>();
        var textMs = new List<double>();
        for (int i = 0; i < asr.Count; i++)
        {
            if (asr[i].Text.TrimStart().StartsWith('[')) continue;          // [Music] etc.
            string w = Norm(asr[i].Text);
            if (w.Length == 0) continue;
            double t0 = asr[i].Ms;
            double t1 = i + 1 < asr.Count ? Math.Max(t0, asr[i + 1].Ms) : t0 + 400;
            if (t1 - t0 > 3000) t1 = t0 + 3000;                       // long pause: do not stretch the word
            for (int k = 0; k < w.Length; k++) { text.Add(w[k]); textMs.Add(t0 + (t1 - t0) * k / w.Length); }
        }
        if (text.Count < 30) { reason = "too little ASR text"; return null; }
        char[] T = text.ToArray();

        // ---- Per-line candidates ----
        int n = reference.Count;
        var pats = new string[n];
        for (int i = 0; i < n; i++) pats[i] = Norm(reference[i].Text);
        var cands = new List<Cand>[n];
        for (int i = 0; i < n; i++) cands[i] = pats[i].Length >= MinLineChars ? Candidates(pats[i], T) : new List<Cand>();

        // ---- Monotonic chain (maximize total gain) ----
        var nodes = new List<(int Line, Cand C, double Gain)>();
        for (int i = 0; i < n; i++)
            foreach (var c in cands[i])
                nodes.Add((i, c, pats[i].Length * (c.Sim - GainBase)));
        if (nodes.Count == 0) { reason = "no line found in the captions"; return null; }

        var best = new double[nodes.Count];
        var prev = new int[nodes.Count];
        for (int a = 0; a < nodes.Count; a++)
        {
            best[a] = nodes[a].Gain; prev[a] = -1;
            for (int b = 0; b < a; b++)
            {
                if (nodes[b].Line >= nodes[a].Line) continue;
                int slack = pats[nodes[a].Line].Length / 4;                 // neighbouring lines may overlap a little
                if (nodes[b].C.End > nodes[a].C.Start + slack) continue;
                if (best[b] + nodes[a].Gain > best[a]) { best[a] = best[b] + nodes[a].Gain; prev[a] = b; }
            }
        }
        int top = 0;
        for (int a = 1; a < nodes.Count; a++) if (best[a] > best[top]) top = a;
        var chain = new List<int>();
        for (int a = top; a >= 0; a = prev[a]) chain.Add(a);
        chain.Reverse();

        double meanSim = 0;
        foreach (int a in chain) meanSim += nodes[a].C.Sim;
        meanSim /= chain.Count;
        if (chain.Count < Math.Max(MinAnchors, (int)Math.Ceiling(n * MinAnchorRatio)))
        { reason = $"only {chain.Count}/{n} lines matched"; return null; }
        if (meanSim < MinMeanSim) { reason = $"weak matches (mean {meanSim:0.00})"; return null; }

        // ---- Anchor pairs: original time -> time in this video ----
        var anchorLine = new List<int>();
        var anchorOrig = new List<double>();
        var anchorNew = new List<double>();
        foreach (int a in chain)
        {
            var (line, c, _) = nodes[a];
            double tNew = textMs[Math.Min(c.Start, textMs.Count - 1)];
            if (anchorNew.Count > 0 && tNew < anchorNew[^1]) continue;       // keep times monotonic
            anchorLine.Add(line); anchorOrig.Add(reference[line].TimeMs); anchorNew.Add(tNew);
        }

        if (anchorLine.Count < MinAnchors) { reason = "anchors not monotonic"; return null; }

        // ---- Place every line ----
        var outLines = new List<(double Ms, string Text)>(n);
        double last = 0;
        for (int i = 0; i < n; i++)
        {
            double o = reference[i].TimeMs, t;
            int k = anchorLine.IndexOf(i);
            if (k >= 0) t = anchorNew[k];
            else if (o <= anchorOrig[0]) t = anchorNew[0] + (o - anchorOrig[0]);                 // before first: same spacing
            else if (o >= anchorOrig[^1]) t = anchorNew[^1] + (o - anchorOrig[^1]);              // after last: same spacing
            else
            {
                int j = 0;
                while (j + 1 < anchorOrig.Count && anchorOrig[j + 1] <= o) j++;
                double span = anchorOrig[j + 1] - anchorOrig[j];
                double f = span > 0 ? (o - anchorOrig[j]) / span : 0;
                t = anchorNew[j] + f * (anchorNew[j + 1] - anchorNew[j]);
            }
            t = Math.Max(Math.Max(0, t), last);                                                  // never go backwards
            last = t;
            outLines.Add((t, reference[i].Text));
        }

        var sb = new StringBuilder();
        foreach (var (ms, tx) in outLines)
        {
            int cs = (int)(ms / 10);
            sb.Append('[').Append((cs / 6000).ToString("00", CultureInfo.InvariantCulture)).Append(':')
              .Append((cs / 100 % 60).ToString("00", CultureInfo.InvariantCulture)).Append('.')
              .Append((cs % 100).ToString("00", CultureInfo.InvariantCulture)).Append("] ").Append(tx).Append('\n');
        }
        return new LyricsResult
        {
            Found = true,
            Synced = sb.ToString(),
            Source = SourceName,
            Detail = $"re-timed from captions: {anchorLine.Count}/{n} lines anchored, mean match {meanSim:0.00}",
        };
    }

    /// <summary>Mean |aligned - original| start time in ms (aligned lines keep the reference order and count).</summary>
    public static double MeanShiftMs(IReadOnlyList<LyricLine> reference, string alignedLrc)
    {
        var aligned = LrcParser.Parse(alignedLrc);
        int n = Math.Min(reference.Count, aligned.Count);
        if (n == 0) return 0;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(aligned[i].TimeMs - reference[i].TimeMs);
        return sum / n;
    }

    private readonly record struct Cand(int Start, int End, double Sim);

    /// <summary>Best non-overlapping approximate occurrences of <paramref name="p"/> in <paramref name="t"/>
    /// (Sellers: edit distance with free start and end in the text).</summary>
    private static List<Cand> Candidates(string p, char[] t)
    {
        int m = p.Length, N = t.Length;
        var cost = new int[N + 1];
        var start = new int[N + 1];
        for (int j = 0; j <= N; j++) { cost[j] = 0; start[j] = j; }
        var nc = new int[N + 1];
        var ns = new int[N + 1];
        for (int k = 1; k <= m; k++)
        {
            nc[0] = k; ns[0] = 0;
            char pc = p[k - 1];
            for (int j = 1; j <= N; j++)
            {
                int diag = cost[j - 1] + (pc == t[j - 1] ? 0 : 1), up = cost[j] + 1, left = nc[j - 1] + 1;
                if (diag <= up && diag <= left) { nc[j] = diag; ns[j] = start[j - 1]; }
                else if (up <= left) { nc[j] = up; ns[j] = start[j]; }
                else { nc[j] = left; ns[j] = ns[j - 1]; }
            }
            (cost, nc) = (nc, cost);
            (start, ns) = (ns, start);
        }

        var all = new List<Cand>();
        for (int j = 1; j <= N; j++)
        {
            double sim = 1.0 - (double)cost[j] / m;
            int len = j - start[j];
            if (sim >= MinLineSim && len >= m / 2 && len <= m * 2) all.Add(new Cand(start[j], j, sim));
        }
        all.Sort((a, b) => b.Sim.CompareTo(a.Sim));
        var picked = new List<Cand>();
        foreach (var c in all)
        {
            bool near = false;
            foreach (var q in picked) if (Math.Abs(q.End - c.End) < m) { near = true; break; }
            if (!near) { picked.Add(c); if (picked.Count >= CandidatesPerLine) break; }
        }
        return picked;
    }

    /// <summary>Lowercase letters/digits only; Hangul syllables split into jamo; accents dropped.</summary>
    internal static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) continue;
        }
        return sb.ToString();
    }
}

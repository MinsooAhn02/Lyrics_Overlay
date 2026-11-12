using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LyricsOnTheGo.Services;

/// <summary>Official (manual, non-auto-generated) YouTube captions reported by the extension for one video.
/// <see cref="Status"/>: ok | none (no manual track) | empty (track exists, nothing came back - blocked) | error | http###.</summary>
public sealed record CaptionsInfo(string VideoId, string Status, string Lang, string Name, IReadOnlyList<(int Ms, string Text)> Lines,
    IReadOnlyList<(int Ms, string Text)>? Asr = null, IReadOnlyList<TrackEntry>? Tracks = null)
{
    /// <summary>Auto-generated captions as words with start times (a clock for re-timing lyrics, never lyrics themselves).</summary>
    public IReadOnlyList<(int Ms, string Text)> AsrWords => Asr ?? Array.Empty<(int Ms, string Text)>();
}

/// <summary>
/// App-wide store of the captions the extension sent (last few videos) plus the user's per-video "don't use the
/// YouTube captions for this video" list (persisted). BrowserBridge writes, LyricsController / the UI read.
/// </summary>
public static class CaptionHub
{
    private const int Keep = 12;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo", "caption-rejects.json");

    private static readonly string ForcePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo", "force-music.json");
    private static readonly HashSet<string> ForcedMusic = LoadSet(ForcePath);

    /// <summary>The user said "this is music" for a video the extension classified as non-music.</summary>
    public static bool IsForcedMusic(string videoId) { lock (ForcedMusic) return ForcedMusic.Contains(videoId); }

    public static void SetForcedMusic(string videoId)
    {
        lock (ForcedMusic)
        {
            if (!ForcedMusic.Add(videoId)) return;
            if (ForcedMusic.Count > 500) { ForcedMusic.Clear(); ForcedMusic.Add(videoId); }   // keep the file small
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ForcePath)!);
                File.WriteAllText(ForcePath, JsonSerializer.Serialize(ForcedMusic));
            }
            catch (Exception ex) { DiagLog.LogCompleted("captions", "CaptionHub", DiagResult.Error, "save force-music failed: " + ex.GetType().Name, 0, false); }
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CaptionsInfo> Store = new();
    private static readonly Queue<string> Order = new();
    private static readonly HashSet<string> Rejected = Load();

    /// <summary>Raised (any thread) after captions for a video arrived.</summary>
    public static event Action<string>? Received;

    public static void Set(CaptionsInfo c)
    {
        lock (Gate)
        {
            if (!Store.ContainsKey(c.VideoId)) Order.Enqueue(c.VideoId);
            Store[c.VideoId] = c;
            while (Order.Count > Keep) Store.Remove(Order.Dequeue());
        }
        try { Received?.Invoke(c.VideoId); }
        catch (Exception ex) { DiagLog.LogCompleted("captions", "CaptionHub", DiagResult.Error, "subscriber threw: " + ex.GetType().Name, 0, false); }
    }

    public static CaptionsInfo? Get(string videoId)
    {
        lock (Gate) return Store.TryGetValue(videoId, out var c) ? c : null;
    }

    /// <summary>Waits until captions (any status) for <paramref name="videoId"/> arrived; null on timeout/cancel.</summary>
    public static async Task<CaptionsInfo?> WaitAsync(string videoId, int timeoutMs, CancellationToken ct)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!ct.IsCancellationRequested)
        {
            var c = Get(videoId);
            if (c is not null) return c;
            if (Environment.TickCount64 >= until) return null;
            try { await Task.Delay(100, ct); } catch (TaskCanceledException) { return null; }
        }
        return null;
    }

    public static bool IsRejected(string videoId) { lock (Gate) return Rejected.Contains(videoId); }

    public static void SetRejected(string videoId, bool rejected)
    {
        lock (Gate)
        {
            if (!(rejected ? Rejected.Add(videoId) : Rejected.Remove(videoId))) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Rejected));
            }
            catch (Exception ex) { DiagLog.LogCompleted("captions", "CaptionHub", DiagResult.Error, "save rejects failed: " + ex.GetType().Name, 0, false); }
        }
    }

    private static HashSet<string> Load() => LoadSet(FilePath);

    private static HashSet<string> LoadSet(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? new();
        }
        catch { /* unreadable: start empty */ }
        return new();
    }
}

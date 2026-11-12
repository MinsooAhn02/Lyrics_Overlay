using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>
/// On-disk lyrics cache: one JSON file per song under %LOCALAPPDATA%\LyricsOnTheGo\
/// lyrics-cache. A song is downloaded from LRCLIB at most once. The cache is versioned
/// so improvements to the matching logic re-fetch already-cached songs.
/// </summary>
public static class LyricsCache
{
    // Bump when the matching logic changes so stale entries are dropped on next launch.
    private const string CacheVersion = "3";   // 3: drops entries cached from provisional (title-only) browser lookups

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricsOnTheGo", "lyrics-cache");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>FNV-1a 64-bit hex of normalized "title|artist|album|durationSec".</summary>
    public static string Key(string title, string artist, string album, double durationMs)
    {
        long durationSec = (long)(durationMs / 1000);
        return KeyRaw($"{title.ToLowerInvariant()}|{artist.ToLowerInvariant()}|{album.ToLowerInvariant()}|{durationSec}");
    }

    /// <summary>FNV-1a 64-bit hex of an already-built identity string (used as-is, no case folding).</summary>
    public static string KeyRaw(string raw)
    {
        ulong hash = 0xcbf29ce484222325UL;
        foreach (byte b in Encoding.UTF8.GetBytes(raw))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return hash.ToString("x16");
    }

    public static LyricsResult? Read(string key)
    {
        try
        {
            string path = Path.Combine(Dir, key + ".json");
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<LyricsResult>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Checked against LRCLIB within the last week: lyrics practically never change, so a fresh entry is shown
    /// without a network lookup (3 parallel searches carrying ~18 full lyrics each, per song).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    public static bool IsFresh(string key)
    {
        try
        {
            string path = Path.Combine(Dir, key + ".json");
            return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < FreshFor;
        }
        catch { return false; }
    }

    /// <summary>Marks an entry as just re-checked (the lookup found the same lyrics, so nothing was rewritten).</summary>
    public static void Touch(string key)
    {
        try { File.SetLastWriteTimeUtc(Path.Combine(Dir, key + ".json"), DateTime.UtcNow); }
        catch { /* best-effort */ }
    }

    public static void Write(string key, LyricsResult result)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, key + ".json"), JsonSerializer.Serialize(result, JsonOptions));
        }
        catch { /* cache is best-effort */ }
    }

    /// <summary>One-time migration: drop cached .json if the cache version changed.</summary>
    public static void EnsureVersion()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string marker = Path.Combine(Dir, ".version");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == CacheVersion)
                return;

            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
                TryDelete(f);

            File.WriteAllText(marker, CacheVersion);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Deletes all cached .json (keeps the .version marker). Returns the count removed.</summary>
    public static int Clear()
    {
        int count = 0;
        try
        {
            if (!Directory.Exists(Dir))
                return 0;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
                if (TryDelete(f))
                    count++;
        }
        catch { /* best-effort */ }
        return count;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }
}

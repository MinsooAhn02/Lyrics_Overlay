namespace LyricsOnTheGo.Models;

/// <summary>
/// A snapshot of the currently playing track from Windows SMTC, with the position
/// already interpolated to "now".
/// </summary>
public sealed record NowPlaying
{
    public bool HasSession { get; init; }

    /// <summary>SMTC SourceAppUserModelId of the session (e.g. "Spotify.exe", "MSEdge"). Empty without a session.</summary>
    public string SourceAppId { get; init; } = "";

    /// <summary>True when the session comes from a web browser, where the reported artist is
    /// often the uploader/channel (YouTube) rather than the real artist. Set by SmtcReader.</summary>
    public bool IsBrowser { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public double DurationMs { get; init; }
    public double PositionMs { get; init; }
    public bool IsPlaying { get; init; }

    /// <summary>YouTube video id when the state comes from the browser extension bridge; empty for SMTC.</summary>
    public string VideoId { get; init; } = "";

    /// <summary>Non-empty when no lookup should run for this state: "ad" or "nonmusic" (bridge only).</summary>
    public string Skip { get; init; } = "";

    /// <summary>True when Title/Artist/Album come from the extension's structured music metadata
    /// (a real track name + artist) instead of the raw video title + channel. Bridge only.</summary>
    public bool Structured { get; init; }

    /// <summary>Where the structured metadata came from: "ytm" | "musicSection" | "topic" | "title" | "".</summary>
    public string MetaSource { get; init; } = "";

    /// <summary>The raw video title when <see cref="Structured"/> (Title then holds the track name); used as a search fallback.</summary>
    public string RawTitle { get; init; } = "";

    public static NowPlaying None { get; } = new();

    /// <summary>Stable identity of the track — changes on song change. Bridge source: "yt:&lt;videoId&gt;"
    /// (case-sensitive, immune to album/duration jitter); SMTC: title/artist/album/duration.</summary>
    public string Key => VideoId.Length > 0
        ? "yt:" + VideoId
        : $"{Title}|{Artist}|{Album}|{(int)(DurationMs / 1000)}".ToLowerInvariant();
}

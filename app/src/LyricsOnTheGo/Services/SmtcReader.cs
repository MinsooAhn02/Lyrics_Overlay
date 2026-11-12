using System;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;
using Windows.Media.Control;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Reads the active media session from Windows System Media Transport Controls. Prefers a
/// Spotify session, else the current one. Interpolates position using the session's
/// LastUpdatedTime so the highlight tracks real playback between SMTC updates.
/// </summary>
public sealed class SmtcReader
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            _manager = null;
        }
    }

    public async Task<NowPlaying> GetNowPlayingAsync()
    {
        var session = PickSession();
        if (session is null)
            return NowPlaying.None;

        try
        {
            string appId = session.SourceAppUserModelId ?? "";
            var media = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();

            bool playing = playback?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            double durationMs = (timeline.EndTime - timeline.StartTime).TotalMilliseconds;
            double positionMs = timeline.Position.TotalMilliseconds;

            // SMTC only reports position on play/pause/seek — add wall-clock time elapsed
            // since LastUpdatedTime to get the live position while playing.
            if (playing)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero)
                    positionMs += elapsed.TotalMilliseconds;
            }

            if (durationMs > 0)
                positionMs = Math.Clamp(positionMs, 0, durationMs);

            return new NowPlaying
            {
                HasSession = true,
                SourceAppId = appId,
                IsBrowser = BrowserDetect.IsBrowser(appId),
                Title = media?.Title ?? "",
                Artist = media?.Artist ?? "",
                Album = media?.AlbumTitle ?? "",
                DurationMs = durationMs,
                PositionMs = positionMs,
                IsPlaying = playing,
            };
        }
        catch
        {
            return NowPlaying.None;
        }
    }

    // ---- Transport controls (same session as GetNowPlayingAsync) ----------------

    /// <summary>Toggle play/pause on the current session. False when there is no session or the
    /// app rejected/doesn't support it (the reason is written to DiagLog).</summary>
    public Task<bool> TogglePlayPauseAsync() => ControlAsync("TogglePlayPause", s => s.TryTogglePlayPauseAsync());

    public Task<bool> SkipPreviousAsync() => ControlAsync("SkipPrevious", s => s.TrySkipPreviousAsync());

    public Task<bool> SkipNextAsync() => ControlAsync("SkipNext", s => s.TrySkipNextAsync());

    /// <summary>Seek the current session to <paramref name="positionMs"/> (SMTC takes 100 ns ticks).
    /// False when there is no session or the app doesn't support seeking (logged to DiagLog).</summary>
    public async Task<bool> SeekAsync(long positionMs)
    {
        bool ok = await ControlAsync("Seek", s => s.TryChangePlaybackPositionAsync(positionMs * TimeSpan.TicksPerMillisecond));
        // Failures already have a row from ControlAsync (with the reason); this one records the outcome either way.
        DiagLog.LogCompleted("seek", "SMTC", ok ? DiagResult.Synced : DiagResult.Error,
            $"smtc seek result={(ok ? "ok" : "failed")} target={positionMs}", 0, false);
        return ok;
    }

    private async Task<bool> ControlAsync(string name,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        var session = PickSession();
        if (session is null)
        {
            DiagLog.LogCompleted("SMTC control", name, DiagResult.Error, "no media session", 0, false);
            return false;
        }

        try
        {
            bool ok = await command(session);
            if (!ok)
                DiagLog.LogCompleted("SMTC control", name, DiagResult.Error,
                    $"rejected or unsupported by {session.SourceAppUserModelId}", 0, false);
            return ok;
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("SMTC control", name, DiagResult.Error, ex.Message, 0, false);
            return false;
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null)
            return null;

        try
        {
            foreach (var s in _manager.GetSessions())
            {
                if (s.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true)
                    return s;
            }
        }
        catch { /* fall through to current session */ }

        return _manager.GetCurrentSession();
    }
}

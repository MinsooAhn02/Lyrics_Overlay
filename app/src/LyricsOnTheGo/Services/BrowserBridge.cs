using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LyricsOnTheGo.Models;

namespace LyricsOnTheGo.Services;

/// <summary>Playback state reported by the Chrome extension (camelCase JSON, see tasks/contract.md).</summary>
public sealed record BrowserNowPlaying(
    string VideoId,
    string Title,
    string Channel,
    int DurationMs,
    int PositionMs,
    bool Paused,
    bool IsAd,
    bool IsMusic,
    long SentAtMs,
    string? Track = null,
    string? Artist = null,
    string? Album = null,
    string? MetaSource = null,
    string? Dbg = null,
    string? Shuffle = null);   // youtube.com playlist: off | busy | on | err:<reason>; empty/absent = no shuffle button

/// <summary>
/// Loopback-only HTTP receiver for the Chrome/Firefox extension. POST /v1/now-playing returns
/// {"commands":[...]} (queued via <see cref="EnqueueCommand"/>, delivered once). Runs on
/// thread-pool threads: <see cref="Updated"/> is raised there, subscribers marshal to the UI.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const int Port = 47831;
    public const string AllowedExtensionId = "khoankekndekkeoamaopgjhpogbooldk";

    private const string Path_ = "/v1/now-playing";
    private const string CapPath_ = "/v1/captions";
    private const string QuitPath_ = "/v1/quit";
    private const int MaxBody = 8 * 1024;
    private const int MaxCaptionBody = 256 * 1024;
    private const int MaxQueuedCommands = 16;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly string HostHeader = "127.0.0.1:" + Port;
    private static readonly string AllowedOrigin = "chrome-extension://" + AllowedExtensionId;
    // Firefox gives every install a random moz-extension://<uuid> origin, so it cannot be pinned like the Chrome id.
    // A web page cannot forge a moz-extension:// Origin (the browser sets it, scripts cannot override it), and
    // only extensions installed in the user's own browser can send one. This is NOT authentication: any such
    // extension passes this check, so the secret X-Bridge-Token is always verified separately.
    // Strict: lowercase UUID, anchored, \z (not $) so a trailing newline cannot slip through.
    private static readonly Regex FirefoxOriginRe = new(
        @"^moz-extension://[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.Compiled);
    private static readonly Regex VideoIdRe =new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex SeekRe = new(@"^seek:[0-9]{1,8}\z", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedCommands = new() { "toggle", "next", "prev", "shuffle:on", "shuffle:off" };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record Snapshot(BrowserNowPlaying Value, DateTimeOffset At);

    private readonly Settings _settings;
    private readonly List<string> _commands = new();
    private readonly object _cmdLock = new();
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile Snapshot? _snap;
    private volatile string? _lastError;
    private string _lastReportSig = "";
    private long _lastReportTick;
    private long _lastLogTicks;
    private string _lastLogMsg = "";

    public BrowserBridge(Settings settings) => _settings = settings;

    public event Action<BrowserNowPlaying>? Updated;

    /// <summary>The extension popup asked the app to exit (authenticated like every other request). Raised on a pool thread.</summary>
    public event Action? QuitRequested;

    public BrowserNowPlaying? Latest => _snap?.Value;
    public DateTimeOffset LatestAt => _snap?.At ?? default;
    public string? LastError => _lastError;

    /// <summary>Fills <see cref="Settings.BridgeToken"/> with 32 random bytes (hex) when empty.</summary>
    public static void EnsureToken(Settings s)
    {
        if (!string.IsNullOrEmpty(s.BridgeToken)) return;
        s.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        s.Save();
    }

    /// <summary>
    /// Accepted: toggle, next, prev, seek:&lt;0..86400000&gt; (ms). Anything else is ignored with one DiagLog row.
    /// A new seek replaces any queued seek (only the last one is kept). Queue is capped at 16.
    /// </summary>
    public void EnqueueCommand(string cmd)
    {
        if (cmd is null || !(AllowedCommands.Contains(cmd) || IsValidSeek(cmd)))
        {
            Log("command rejected: " + (cmd is null ? "null" : cmd.Length > 24 ? "too long" : cmd));
            return;
        }
        lock (_cmdLock)
        {
            if (cmd.StartsWith("seek:", StringComparison.Ordinal)) _commands.RemoveAll(c => c.StartsWith("seek:", StringComparison.Ordinal));
            if (_commands.Count < MaxQueuedCommands) _commands.Add(cmd);
            else { Log("command dropped: queue full"); return; }
        }
        Info("cmd queued " + (cmd.StartsWith("seek:", StringComparison.Ordinal) ? "seek" : cmd) + (_listener is null ? " (bridge not listening)" : ""));
    }

    private static bool IsValidSeek(string cmd) =>
        SeekRe.IsMatch(cmd) && int.Parse(cmd.Substring(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture) <= 86_400_000;

    public void Start()
    {
        if (!_settings.BridgeEnabled) return;
        lock (_gate)
        {
            if (_listener is not null) return;
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try
            {
                l.Start();
            }
            catch (Exception ex) // port in use (32/183), access denied (5), ...
            {
                l.Close();
                _lastError = $"Bridge cannot listen on 127.0.0.1:{Port}: {ex.Message}";
                Log(_lastError);
                return;
            }
            _lastError = null;
            _listener = l;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(l));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            var l = _listener;
            _listener = null;
            _cts?.Cancel();
            _cts = null;
            try { l?.Close(); } catch { /* already disposed */ }
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await l.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || !l.IsListening)
            {
                return; // Stop() was called
            }
            catch (Exception ex)
            {
                Log("accept failed: " + ex.GetType().Name + ": " + ex.Message);
                await Task.Delay(200).ConfigureAwait(false); // avoid a hot loop on a persistent fault
                continue;
            }
            _ = Task.Run(() => HandleSafe(ctx));
        }
    }

    private async Task HandleSafe(HttpListenerContext ctx)
    {
        try
        {
            await Handle(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("request failed: " + ex.GetType().Name + ": " + ex.Message);
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // DNS-rebinding / non-loopback defence
        if (req.RemoteEndPoint is null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address)
            || req.Headers["Host"] != HostHeader)
        { Reply(res, 403, Err("forbidden")); return; }

        bool isCaps = req.Url?.AbsolutePath == CapPath_;
        bool isQuit = req.Url?.AbsolutePath == QuitPath_;
        if (req.Url?.AbsolutePath != Path_ && !isCaps && !isQuit) { Reply(res, 404, Err("not found")); return; }
        int maxBody = isCaps ? MaxCaptionBody : MaxBody;

        var origin = req.Headers["Origin"];
        // Echoing the request Origin in Access-Control-Allow-Origin is safe only because originOk gates every
        // path that reaches Reply(..., origin): a non-matching Origin gets 403 and no CORS header.
        bool originOk = origin is not null
            && (string.Equals(origin, AllowedOrigin, StringComparison.Ordinal) || FirefoxOriginRe.IsMatch(origin));

        if (req.HttpMethod == "OPTIONS")
        {
            if (!originOk) { Reply(res, 403, Err("forbidden origin")); return; }
            res.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Bridge-Token";
            res.Headers["Access-Control-Max-Age"] = "600";
            Reply(res, 204, null, origin);
            return;
        }
        if (req.HttpMethod != "POST") { res.Headers["Allow"] = "POST, OPTIONS"; Reply(res, 405, Err("method not allowed")); return; }
        if (!originOk) { Reply(res, 403, Err("forbidden origin")); return; }
        if (!TokenOk(req.Headers["X-Bridge-Token"])) { Reply(res, 401, Err("unauthorized")); return; }

        var ct = req.ContentType?.Split(';')[0].Trim();
        if (!string.Equals(ct, "application/json", StringComparison.OrdinalIgnoreCase))
        { Reply(res, 415, Err("content-type must be application/json"), origin); return; }

        if (req.ContentLength64 > maxBody) { Reply(res, 413, Err("body too large"), origin); return; }

        byte[]? body;
        using (var cts = new CancellationTokenSource(ReadTimeout))
        using (cts.Token.Register(() => { try { res.Abort(); } catch { } }))
            body = await ReadBody(req.InputStream, maxBody, cts.Token).ConfigureAwait(false);
        if (body is null) { Reply(res, 413, Err("body too large"), origin); return; }

        if (isQuit)
        {
            Info("quit requested by the extension");
            Reply(res, 200, "{}", origin);
            try { QuitRequested?.Invoke(); }
            catch (Exception ex) { Log("quit subscriber threw: " + ex.GetType().Name); }
            return;
        }

        if (isCaps)
        {
            if (!TryParseCaptions(body, out var caps)) { Reply(res, 400, Err("invalid body"), origin); return; }
            CaptionHub.Set(caps!);
            Info($"captions vid={caps!.VideoId} status={caps.Status} lang={caps.Lang} lines={caps.Lines.Count} last={(caps.Lines.Count > 0 ? caps.Lines[^1].Ms : 0)}ms asr={caps.AsrWords.Count} tracks={caps.Tracks?.Count ?? 0}");
            Reply(res, 200, "{}", origin);
            return;
        }

        if (!TryParse(body, out var np, out var ping)) { Reply(res, 400, Err("invalid body"), origin); return; }

        // Diagnostics: which extension copy / tab reports what. One row whenever the (video, title) pair changes, at most
        // twice a second, so alternating tabs or stale titles show up in captions.log without flooding it.
        if (!ping)
        {
            string sig = np!.VideoId + "|" + np.Title;
            long tick = Environment.TickCount64;
            if (sig != _lastReportSig && tick - _lastReportTick > 500)
            {
                _lastReportSig = sig; _lastReportTick = tick;
                string o = origin ?? "";
                string who = o.Length > 8 ? o[^8..] : o;
                string T(string x, int n) => x.Length > n ? x[..n] : x;
                Info($"report ext=..{who} vid={np.VideoId} music={np.IsMusic} paused={np.Paused} dur={np.DurationMs / 1000}s title='{T(np.Title, 40)}' ch='{T(np.Channel, 20)}' {np.Dbg}");
            }
        }

        // Several YouTube tabs (or browsers) report concurrently: a paused tab must not overwrite a
        // different video that is actively playing (reported within the last 3 s), or lyrics flip between songs.
        var cur = _snap;
        bool shadowed = !ping && np!.Paused && cur is not null && !cur.Value.Paused
            && cur.Value.VideoId != np.VideoId && DateTimeOffset.UtcNow - cur.At < TimeSpan.FromSeconds(3);

        if (!ping && !shadowed)
        {
            _snap = new Snapshot(np!, DateTimeOffset.UtcNow);
            try { Updated?.Invoke(np!); }
            catch (Exception ex) { Log("Updated subscriber threw: " + ex.GetType().Name + ": " + ex.Message); }
        }

        var cmds = new List<string>();
        // Commands belong to the tab whose report is shown: a ping (connection test) or a shadowed paused tab must not
        // take them, or "next"/"shuffle" would act on a background tab.
        if (!ping && !shadowed)
            lock (_cmdLock) { cmds.AddRange(_commands); _commands.Clear(); }
        Reply(res, 200, JsonSerializer.Serialize(new { commands = cmds }), origin);
        if (cmds.Count > 0) Info("cmd delivered n=" + cmds.Count);
    }

    private bool TokenOk(string? provided)
    {
        var expected = _settings.BridgeToken;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided)) return false;
        // hash first so length is not leaked and FixedTimeEquals gets equal-size inputs
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; null when the real body exceeds it. Memory follows the actual
    /// body size (a pooled 16 KB chunk), not the cap: a full-cap buffer per captions POST was 256 KB of large-object garbage.</summary>
    private static async Task<byte[]?> ReadBody(Stream s, int max, CancellationToken ct)
    {
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            int r;
            while ((r = await s.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + r > max) return null;
                ms.Write(chunk, 0, r);
            }
            return ms.ToArray();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(chunk); }
    }

    private static readonly HashSet<string> CaptionStatuses = new() { "ok", "none", "empty", "error" };

    /// <summary>{"videoId","status","lang","name","lines":[[ms,"text"],...]}: at most 600 lines of 300 chars; unknown status/shape = invalid.</summary>
    private static bool TryParseCaptions(byte[] body, out CaptionsInfo? caps)
    {
        caps = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string vid = root.TryGetProperty("videoId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "";
            if (!VideoIdRe.IsMatch(vid)) return false;
            if (!CaptionStatuses.Contains(status) && !Regex.IsMatch(status, @"^http[0-9]{3}\z")) return false;
            string lang = root.TryGetProperty("lang", out var lg) && lg.ValueKind == JsonValueKind.String ? StripControl(lg.GetString()) : "";
            string name = root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? StripControl(nm.GetString()) : "";
            if (lang.Length > 16 || name.Length > 80) return false;
            var lines = new List<(int, string)>();
            if (root.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                if (ls.GetArrayLength() > 600) return false;
                foreach (var l in ls.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() != 2
                        || l[0].ValueKind != JsonValueKind.Number || l[1].ValueKind != JsonValueKind.String) return false;
                    if (!l[0].TryGetInt32(out int ms) || ms < 0 || ms > 86_400_000) return false;
                    string t = StripControl(l[1].GetString());
                    if (t.Length > 300) return false;
                    if (t.Length > 0) lines.Add((ms, t));
                }
            }
            List<(int, string)>? asrWords = null;
            if (root.TryGetProperty("asr", out var asr) && asr.ValueKind == JsonValueKind.Object
                && asr.TryGetProperty("lines", out var al) && al.ValueKind == JsonValueKind.Array)
            {
                if (al.GetArrayLength() > 3000) return false;
                asrWords = new();
                foreach (var l in al.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() != 2
                        || l[0].ValueKind != JsonValueKind.Number || l[1].ValueKind != JsonValueKind.String) return false;
                    if (!l[0].TryGetInt32(out int ams) || ams < 0 || ams > 86_400_000) return false;
                    string w = StripControl(l[1].GetString());
                    if (w.Length > 40) return false;
                    if (w.Length > 0) asrWords.Add((ams, w));
                }
            }
            // Description of a mix / playlist upload: newlines are kept (StripControl would remove them), other control chars dropped.
            List<TrackEntry>? tracks = null;
            if (root.TryGetProperty("desc", out var dsc) && dsc.ValueKind == JsonValueKind.String)
            {
                string d = dsc.GetString()!;
                if (d.Length > 6000) return false;
                var sbd = new StringBuilder(d.Length);
                foreach (var ch in d) if (ch == '\n' || !char.IsControl(ch)) sbd.Append(ch);
                tracks = TrackList.Parse(sbd.ToString());
            }
            caps = new CaptionsInfo(vid, status, lang, name, lines, asrWords, tracks);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParse(byte[] body, out BrowserNowPlaying? np, out bool ping)
    {
        np = null; ping = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // {"ping":true}: authenticated connection test from the popup, carries no playback state
            if (root.TryGetProperty("ping", out var p) && p.ValueKind == JsonValueKind.True) { ping = true; return true; }

            var v = root.Deserialize<BrowserNowPlaying>(JsonOpts);
            if (v is null || v.VideoId is null || !VideoIdRe.IsMatch(v.VideoId)) return false;
            v = v with
            {
                Title = v.Title ?? "", Channel = v.Channel ?? "",
                Track = StripControl(v.Track), Artist = StripControl(v.Artist),
                Album = StripControl(v.Album), MetaSource = StripControl(v.MetaSource),
                Dbg = StripControl(v.Dbg).Length > 120 ? "" : StripControl(v.Dbg),
                Shuffle = StripControl(v.Shuffle).Length > 40 ? "" : StripControl(v.Shuffle),
            };
            if (v.Title.Length > 300 || v.Channel.Length > 200) return false;
            if (v.Track!.Length > 300 || v.Artist!.Length > 200 || v.Album!.Length > 200 || v.MetaSource!.Length > 16) return false;
            if (v.DurationMs < 0 || v.DurationMs > 86_400_000) return false;
            if (v.PositionMs < 0 || v.PositionMs > 86_400_000) return false;
            if (v.SentAtMs < 0) return false;
            np = v;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Optional metadata fields: null -> "", control characters (incl. U+0080..9F) removed, trimmed.</summary>
    private static string StripControl(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString().Trim();
    }

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });

    private static void Reply(HttpListenerResponse res, int status, string? json, string? origin = null)
    {
        res.StatusCode = status;
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        if (origin is not null) res.Headers["Access-Control-Allow-Origin"] = origin; // only ever the validated extension origin
        if (json is null) { res.ContentLength64 = 0; res.Close(); return; }
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    /// <summary>Normal (non-error) DiagLog row, not throttled: command kind/count only, never user data.</summary>
    private static void Info(string msg) =>
        DiagLog.LogCompleted("bridge", "BrowserBridge", DiagResult.Plain, msg, 0, false);

    /// <summary>DiagLog row, throttled (same message at most every 10 s) so a flood cannot spam the UI.</summary>
    private void Log(string msg)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (msg == _lastLogMsg && now - _lastLogTicks < 10_000) return;
            _lastLogMsg = msg; _lastLogTicks = now;
        }
        DiagLog.LogCompleted("bridge", "BrowserBridge", DiagResult.Error, msg, 0, false);
    }
}

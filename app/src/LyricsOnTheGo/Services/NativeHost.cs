using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Browser native-messaging host. The browser launches this same exe (see <see cref="NativeHostRegistrar"/>) with
/// stdin/stdout pipes; each message is a 4-byte little-endian length followed by UTF-8 JSON. It can only do two
/// things: report whether the overlay app is running, and start it. Nothing else is accepted.
/// </summary>
public static class NativeHost
{
    public const string SingleInstanceMutex = @"Local\LyricsOverlay.Single";
    /// <summary>Set by "start" when the app already runs: the app brings its overlay back from the tray.</summary>
    public const string ShowEvent = @"Local\LyricsOverlay.Show";
    private const int MaxMessage = 4096;

    /// <summary>Chrome passes the caller's origin as the first argument, Firefox passes the manifest path and the extension id.</summary>
    public static bool IsHostLaunch(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("chrome-extension://", StringComparison.Ordinal)) return true;
            if (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && a.Contains(NativeHostRegistrar.HostName, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static bool AppRunning()
    {
        if (!Mutex.TryOpenExisting(SingleInstanceMutex, out var m)) return false;
        m.Dispose();
        return true;
    }

    /// <summary>Serves messages until the browser closes the pipe. Never throws.</summary>
    public static void Run()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var lenBuf = new byte[4];
            while (ReadExact(stdin, lenBuf))
            {
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > MaxMessage) { Write(stdout, new { ok = false, error = "bad length" }); return; }
                var body = new byte[len];
                if (!ReadExact(stdin, body)) return;
                Write(stdout, Handle(body));
            }
        }
        catch { /* the browser went away: nothing left to tell */ }
    }

    private static object Handle(byte[] body)
    {
        string cmd;
        try
        {
            using var doc = JsonDocument.Parse(body);
            cmd = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("cmd", out var c)
                  && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        }
        catch (JsonException) { return new { ok = false, error = "invalid json" }; }

        switch (cmd)
        {
            case "status":
                return new { ok = true, running = AppRunning() };
            case "start":
                if (AppRunning())
                {
                    bool shown = false;
                    try { if (EventWaitHandle.TryOpenExisting(ShowEvent, out var ev)) using (ev) shown = ev.Set(); } catch { /* old app build: no event */ }
                    return new { ok = true, running = true, started = false, shown };
                }
                return StartApp();
            default:
                return new { ok = false, error = "unknown command" };
        }
    }

    private static string TicketPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo", "launch-ticket");

    /// <summary>True (once) when the extension's "start" asked for this launch within the last 30 s.</summary>
    public static bool ConsumeLaunchTicket()
    {
        try
        {
            if (!File.Exists(TicketPath)) return false;
            string text = File.ReadAllText(TicketPath);
            File.Delete(TicketPath);
            return long.TryParse(text, out long t) && t <= DateTime.UtcNow.Ticks
                   && DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) < TimeSpan.FromSeconds(30);
        }
        catch { return false; }
    }

    private static object StartApp()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return new { ok = false, error = "app path unknown" };
        try
        {
            // explorer.exe can't pass arguments, so the "started from the extension" signal is a short-lived file.
            Directory.CreateDirectory(Path.GetDirectoryName(TicketPath)!);
            File.WriteAllText(TicketPath, DateTime.UtcNow.Ticks.ToString());
            // Through the shell, not as a child of this host: the browser may tear down the host's process tree when it
            // closes, and the overlay must outlive that.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) { return new { ok = false, error = "cannot start: " + ex.GetType().Name }; }

        for (int i = 0; i < 40; i++)   // up to ~8 s for the overlay to come up
        {
            Thread.Sleep(200);
            if (AppRunning()) return new { ok = true, running = true, started = true };
        }
        return new { ok = false, error = "started but did not come up in time" };
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf, n, buf.Length - n);
            if (r <= 0) return false;
            n += r;
        }
        return true;
    }

    private static void Write(Stream s, object reply)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        s.Write(BitConverter.GetBytes(json.Length), 0, 4);
        s.Write(json, 0, json.Length);
        s.Flush();
    }
}

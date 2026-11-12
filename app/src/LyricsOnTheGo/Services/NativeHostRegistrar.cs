using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LyricsOnTheGo.Services;

/// <summary>
/// Registers this exe as the browsers' native-messaging host so the extension popup can start the app. Per-user only
/// (HKCU, no admin): a host manifest per browser family under %LOCALAPPDATA%\LyricsOnTheGo\native-host plus one registry
/// value pointing to it. Runs at every app start so a moved exe re-registers itself; <see cref="Unregister"/> removes it all.
/// </summary>
public static class NativeHostRegistrar
{
    public const string HostName = "com.lyricsoverlay.host";
    public const string FirefoxExtensionId = "lyrics-overlay-bridge@local";

    private static readonly string[] ChromeKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName,
    };
    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsOnTheGo", "native-host");

    public static void EnsureRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            Directory.CreateDirectory(Dir);

            string chromeJson = Path.Combine(Dir, HostName + ".chrome.json");
            string firefoxJson = Path.Combine(Dir, HostName + ".firefox.json");
            WriteIfChanged(chromeJson, JsonSerializer.Serialize(new
            {
                name = HostName,
                description = "Starts the Lyrics Overlay app",
                path = exe,
                type = "stdio",
                allowed_origins = new[] { "chrome-extension://" + BrowserBridge.AllowedExtensionId + "/" },
            }));
            WriteIfChanged(firefoxJson, JsonSerializer.Serialize(new
            {
                name = HostName,
                description = "Starts the Lyrics Overlay app",
                path = exe,
                type = "stdio",
                allowed_extensions = new[] { FirefoxExtensionId },
            }));

            foreach (var k in ChromeKeys) SetDefault(k, chromeJson);
            SetDefault(FirefoxKey, firefoxJson);
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("native-host", "NativeHostRegistrar", DiagResult.Error, "register failed: " + ex.GetType().Name + ": " + ex.Message, 0, false);
        }
    }

    /// <summary>Removes every registry value and file this class created.</summary>
    public static void Unregister()
    {
        try
        {
            foreach (var k in ChromeKeys) Registry.CurrentUser.DeleteSubKey(k, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKey(FirefoxKey, throwOnMissingSubKey: false);
            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("native-host", "NativeHostRegistrar", DiagResult.Error, "unregister failed: " + ex.GetType().Name, 0, false);
        }
    }

    private static void SetDefault(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (!string.Equals(key.GetValue("") as string, value, StringComparison.Ordinal))
            key.SetValue("", value);
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }
}

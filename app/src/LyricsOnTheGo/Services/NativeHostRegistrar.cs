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

    /// <summary>Registers this exe; true when anything had to be written (first run, or the exe moved) or registering
    /// failed — the caller then lets this launch through, so a user is never locked out.</summary>
    public static bool EnsureRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return true;
            Directory.CreateDirectory(Dir);

            string chromeJson = Path.Combine(Dir, HostName + ".chrome.json");
            string firefoxJson = Path.Combine(Dir, HostName + ".firefox.json");
            bool changed = false;
            changed |= WriteIfChanged(chromeJson, JsonSerializer.Serialize(new
            {
                name = HostName,
                description = "Starts the Lyrics Overlay app",
                path = exe,
                type = "stdio",
                allowed_origins = new[] { "chrome-extension://" + BrowserBridge.AllowedExtensionId + "/" },
            }));
            changed |= WriteIfChanged(firefoxJson, JsonSerializer.Serialize(new
            {
                name = HostName,
                description = "Starts the Lyrics Overlay app",
                path = exe,
                type = "stdio",
                allowed_extensions = new[] { FirefoxExtensionId },
            }));

            foreach (var k in ChromeKeys) changed |= SetDefault(k, chromeJson);
            changed |= SetDefault(FirefoxKey, firefoxJson);
            return changed;
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("native-host", "NativeHostRegistrar", DiagResult.Error, "register failed: " + ex.GetType().Name + ": " + ex.Message, 0, false);
            return true;
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

    private static bool SetDefault(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (string.Equals(key.GetValue("") as string, value, StringComparison.Ordinal)) return false;
        key.SetValue("", value);
        return true;
    }

    private static bool WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        File.WriteAllText(path, text);
        return true;
    }
}

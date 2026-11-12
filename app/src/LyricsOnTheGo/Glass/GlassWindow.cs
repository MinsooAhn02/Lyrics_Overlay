using System;
using System.Threading;
using LyricsOnTheGo.Interop;

namespace LyricsOnTheGo.Glass;

/// <summary>
/// Owns the persistent-acrylic backdrop window on its OWN dedicated STA thread (with its
/// own system DispatcherQueue + message loop). Keeping the Composition off the WPF UI
/// thread avoids any dispatcher contention; the WPF window just sits transparently on top.
/// </summary>
public sealed class GlassWindow
{
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private GlassHost? _host;
    private Windows.System.DispatcherQueueController? _dispatcherController;

    // Glass-thread-only state (all mutations are marshalled through the dispatcher queue).
    private bool _backdropEnabled = true;   // false = forced level 0
    private float _level = 1f;              // last requested blur level (0..1)
    private bool _uiVisible = true;         // UI window shown (tray hide/show)
    private int _w, _h;
    private byte _tr = 0x08, _tg = 0x08, _tb = 0x08;
    private float _tOpacity = 0.35f;

    public IntPtr Hwnd { get; private set; }

    /// <param name="backdropEnabled">false = start with blur level 0 (window still shown, draws nothing visible).</param>
    public void Start(int x, int y, int width, int height, bool backdropEnabled = true)
    {
        _backdropEnabled = backdropEnabled;
        _thread = new Thread(() => ThreadProc(x, y, width, height))
        {
            IsBackground = true,
            Name = "GlassComposition",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void ThreadProc(int x, int y, int width, int height)
    {
        _dispatcherController = Native.EnsureSystemDispatcherQueueOnCurrentThread();

        _w = width; _h = height;
        Hwnd = Native.CreateGlassWindow("LyricsOnTheGoGlass", x, y, width, height);
        // Always build the composition tree (level 0 when disabled) so toggling later never
        // creates/hides anything: first commit is already fully transparent.
        EnsureHost();
        ApplyVisibility();

        _ready.Set();

        Native.RunMessageLoop();

        GC.KeepAlive(_host);
        GC.KeepAlive(_dispatcherController);
    }

    // Glass thread only. Builds the composition tree on first need and syncs cached size/tint.
    private void EnsureHost()
    {
        if (_host is not null || Hwnd == IntPtr.Zero)
            return;
        var host = new GlassHost();
        host.Initialize(Hwnd, _w, _h);
        host.SetBackdropOpacity(EffectiveLevel);
        host.UpdateTint(_tr, _tg, _tb, _tOpacity);
        _host = host;
    }

    private float EffectiveLevel => _backdropEnabled ? _level : 0f;

    // Glass thread only. Shown whenever the UI is visible; transparency is done by Opacity, not by hiding.
    private void ApplyVisibility() => Native.SetVisible(Hwnd, _uiVisible);

    private void Post(Action a)
    {
        var dq = _dispatcherController?.DispatcherQueue;
        if (dq is null)
            return;
        dq.TryEnqueue(() => a());
    }

    /// <summary>Updates the acrylic's rounded clip to the new size (marshalled to the glass thread).</summary>
    public void Resize(int width, int height) => Post(() =>
    {
        _w = width; _h = height;
        _host?.Resize(width, height);
    });

    /// <summary>Live-updates the tint colour + opacity (marshalled to the glass thread).</summary>
    public void UpdateTint(byte r, byte g, byte b, float opacity) => Post(() =>
    {
        _tr = r; _tg = g; _tb = b; _tOpacity = opacity;
        _host?.UpdateTint(r, g, b, opacity);
    });

    /// <summary>Blur (HostBackdrop) strength 0..1: 0 = no blur, 1 = full acrylic. Cached and marshalled.</summary>
    public void SetBackdropLevel(float level01) => Post(() =>
    {
        _level = Math.Clamp(float.IsNaN(level01) ? 1f : level01, 0f, 1f);
        _host?.SetBackdropOpacity(EffectiveLevel);
    });

    /// <summary>Shows/hides the glass together with the UI (marshalled; tray hide/show).</summary>
    public void SetVisible(bool visible) => Post(() =>
    {
        _uiVisible = visible;
        ApplyVisibility();
    });

    /// <summary>Compat: false = blur level 0, true = last cached level. Never shows/hides the window.</summary>
    public void SetBackdropEnabled(bool enabled) => Post(() =>
    {
        _backdropEnabled = enabled;
        _host?.SetBackdropOpacity(EffectiveLevel);
    });

    public void Close()
    {
        if (Hwnd != IntPtr.Zero)
            Native.PostClose(Hwnd);
    }
}

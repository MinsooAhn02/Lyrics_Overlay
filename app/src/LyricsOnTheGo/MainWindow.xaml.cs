using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LyricsOnTheGo.Glass;
using LyricsOnTheGo.Interop;
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo;

public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 8;

    // Backdrop gains: stored BgOpacity (0..1, higher = denser) -> blur level / tint alpha.
    // 체감 보정용, 사용자 피드백으로 튜닝 (perceptual correction; tune from user feedback).
    private const float BlurGain = 1.5f;
    private const float TintGain = 0.8f;
    private const int SeekStepMs = 5000;   // bottom bar -/+ : jump back / forward this far
    private (byte, byte, byte, float, float)? _lastBackdrop;   // last values sent to the glass (skip identical re-sends)
    private bool _japaneseHidden;                              // current lyrics are replaced by the "hidden" status
    private bool? _hideJpApplied;                              // HideJapanese value last rendered (null = never)

    // Lyric rendering tokens, derived from settings by ApplySettings(). All lines share the
    // text colour; the active line is fully opaque, the rest use the configured opacity.
    private Brush _textBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
    private double _fontSize = 24;
    private double _inactiveOpacity = 0.55;
    private double _offsetMs;
    private TextAlignment _align = TextAlignment.Left;

    private readonly Settings _settings = Settings.Load();
    private bool _settingsOpen;

    private GlassWindow? _glass;
    private IntPtr _hwnd;

    private readonly SmtcReader _smtc = new();
    private BrowserBridge? _bridge;   // Chrome-extension receiver; its Updated event fires on a thread-pool thread
    private readonly LyricsController _lyrics = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _interpTimer;

    private IReadOnlyList<LyricLine>? _lines;
    private readonly List<System.Windows.Controls.TextBlock> _lineBlocks = new();
    private int _activeIndex = -1;
    private double _lastScrollTarget = double.NaN;

    private NowPlaying _lastNp = NowPlaying.None;
    private DateTime _lastNpAt = DateTime.UtcNow;

    private const int HeaderHideDelayMs = 1000;  // grace period after the mouse leaves before the controls hide

    private const int HoverIdleMs = 3000;        // mouse resting this long over the window hides the controls until it moves again
    private DispatcherTimer? _idleTimer;         // runs only while hovering with auto-hide on
    private System.Drawing.Point _lastCursor;
    private DateTime _lastMoveAt;
    private DispatcherTimer? _headerTimer;   // single-shot hide countdown (started when the mouse leaves)
    private bool _hover;                     // mouse is over the window
    private bool _headerShown = true;        // current header-controls visibility (avoids re-animating)

    // Click-through: with WS_EX_TRANSPARENT set the window receives NO mouse events at all, so
    // MouseEnter/Leave can't work. A cursor poll (only while click-through is on) drives hover and
    // lifts the transparent flag just while the cursor is over the header buttons.
    private DispatcherTimer? _clickThroughTimer;
    private bool _passThrough;               // WS_EX_TRANSPARENT currently applied to the UI window

    // Window drag: remember where the left button went down; start DragMove only after a real move.
    private bool _dragPending;
    private Point _dragOrigin;

    private TrayIcon? _tray;
#if DEBUG
    private Diagnostics.DiagnosticsWindow? _diagWindow;
#endif
    private bool _pinned = true;   // window starts always-on-top
    private bool _exiting;         // set only by the tray "Quit" path (the real exit)

    private DispatcherTimer? _searchTimer; // animates the "Searching…" trailing dots
    private System.Windows.Documents.Run? _searchRun;
    private string _searchBase = "";
    private int _searchDots;

    // Lyrics viewport margins. Top = header height (single line: max(28, title height)); bottom = footer height
    // (progress/times, when on) or, while the hover bar is shown, bar height + its margin above the footer. The viewport itself shrinks, so lyrics, mask and clip never overlap the bar.
    private double _viewTop = 28;
    private double _viewBottom;
    private const int BarShiftMs = 150;
    private const double MinViewportHeight = 24;

    // Lyrics/title font: null = default (Settings.FontFamily empty or unusable).
    private FontFamily? _font;

    // Title marquee: signature of the last measured state (skip restarts while nothing changed).
    private string _titleSig = "";
    private const double MarqueePxPerSec = 40;
    private const double MarqueePauseSec = 1.5;

    // Wheel peek: while true the auto-centre is suspended; PeekReturnMs after the last wheel notch the
    // view eases back to the active line and following resumes.
    private const int PeekReturnMs = 3000;
    private bool _peeking;
    private DispatcherTimer? _peekTimer;
    private bool _syncingSlider;   // true while code (not the user) moves BgOpacitySlider
    private bool _sliderReady;     // false until the first ApplySettings has synced the slider

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => SyncGlass();
        SizeChanged += (_, _) => { SyncGlass(); QueueRecenter(); };
        Activated += (_, _) => SyncGlass();
        SizeChanged += (_, _) => { UpdateEdgeFade(); UpdateBottomInset(); };
        // The header line grows (downwards) with the title size: keep the lyrics fully below it.
        HeaderBar.SizeChanged += (_, _) =>
        {
            double top = Math.Max(28, HeaderBar.ActualHeight);
            if (Math.Abs(_viewTop - top) < 0.5)
                return;
            _viewTop = top;
            ApplyViewportMargin(animate: false);
        };
        // Any viewport size change (window resize, header/bar margin change, bar animation) refits mask + centring.
        LyricsViewport.SizeChanged += (_, _) => { UpdateEdgeFade(); QueueRecenter(); };
        TitleCanvas.SizeChanged += (_, _) => UpdateTitleMarquee();
        IsVisibleChanged += (_, _) => { _titleSig = ""; UpdateTitleMarquee(); };
        Loaded += OnLoaded;
        Deactivated += (_, _) => _dragPending = false;
        Closing += (_, e) =>
        {
            // Anything but the tray "Quit" (incl. Alt+F4) hides to tray instead of exiting.
            if (_exiting) { SaveWindowPlacement(); return; }
            e.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
#if DEBUG
            _diagWindow?.Close();
#endif
            if (_bridge is not null)
            {
                _bridge.Updated -= OnBridgeUpdated;
                _bridge.Dispose();
            }
            _tray?.Dispose();
            _glass?.Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_settingsOpen) CloseSettings();
            else HideToTray();
        };
    }

    // ---- Glass backdrop glue ----------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProcHook);

        RestoreWindowPlacement();   // apply last size/position BEFORE the glass is placed behind it

        var (x, y, w, h) = GetPhysicalRect();
        _glass = new GlassWindow();
        // Always start enabled: the blur level (ApplyBackdrop) decides visibility, so leaving
        // TransparentMode later can never be stuck behind a stale "backdrop disabled" flag.
        _glass.Start(x, y, w, h, backdropEnabled: true);
        ApplyBackdrop();
        SyncGlass();
    }

    /// <summary>Restore the last window bounds, if any and still on a connected monitor.</summary>
    private void RestoreWindowPlacement()
    {
        var p = WindowPlacementStore.Load();
        if (p is null || p.Width <= 0 || p.Height <= 0)
            return;

        var rect = new Native.RECT { left = p.X, top = p.Y, right = p.X + p.Width, bottom = p.Y + p.Height };
        if (IsOnScreen(rect))
            Native.SetBounds(_hwnd, p.X, p.Y, p.Width, p.Height);
    }

    /// <summary>Persist the current window bounds (using the pre-mode rect when in fullscreen,
    /// so we never restore to a full-screen size).</summary>
    private void SaveWindowPlacement()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        Native.RECT r;
        if (_fullscreen) r = _preFsRect;
        else Native.GetWindowRect(_hwnd, out r);

        int w = r.right - r.left, h = r.bottom - r.top;
        if (w > 0 && h > 0)
            WindowPlacementStore.Save(new WindowPlacement(r.left, r.top, w, h));
    }

    /// <summary>True if the rectangle overlaps any connected monitor (guards against off-screen restores).</summary>
    private static bool IsOnScreen(Native.RECT r)
    {
        var rect = new System.Drawing.Rectangle(
            r.left, r.top, Math.Max(1, r.right - r.left), Math.Max(1, r.bottom - r.top));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            if (screen.Bounds.IntersectsWith(rect))
                return true;
        return false;
    }

    private void SyncGlass()
    {
        if (_glass is null || _glass.Hwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
            return;
        var (x, y, w, h) = GetPhysicalRect();
        Native.PositionBehind(_glass.Hwnd, _hwnd, x, y, w, h);
        _glass.Resize(w, h);
    }

    private (int x, int y, int w, int h) GetPhysicalRect()
    {
        Native.GetWindowRect(_hwnd, out var r);
        return (r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private bool IsOverInteractive(int screenX, int screenY)
    {
        try
        {
            Point p = PointFromScreen(new Point(screenX, screenY));
            if (InputHitTest(p) is not DependencyObject d)
                return false;
            for (DependencyObject? cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            {
                if (cur is System.Windows.Controls.Primitives.ButtonBase
                    or System.Windows.Controls.Primitives.RangeBase    // Slider
                    or System.Windows.Controls.Primitives.TextBoxBase
                    or System.Windows.Controls.Primitives.ScrollBar
                    or Controls.SettingsPanel)
                    return true;
            }
        }
        catch { /* hit-test can fail mid-layout */ }
        return false;
    }

    // ---- Settings ---------------------------------------------------------------

    private void ApplySettings()
    {
        Color textColor = ParseColor(_settings.TextColor, Color.FromRgb(0xED, 0xED, 0xED));
        _textBrush = new SolidColorBrush(textColor);

        _fontSize = _settings.FontSize;
        // Inverted: higher "Dim" = more transparent (lower opacity).
        _inactiveOpacity = (100 - _settings.Dim) / 100.0;
        _offsetMs = _settings.Offset;
        _align = (_settings.Align == "center") ? TextAlignment.Center : TextAlignment.Left;

        // Font: empty / unusable name -> default. new FontFamily(list) accepts "A, B" fallbacks and never
        // throws for unknown names (WPF falls back while rendering); the try/catch covers malformed input.
        FontFamily? ff = null;
        string fontName = (_settings.FontFamily ?? "").Trim();
        if (fontName.Length > 0)
        {
            try { ff = LyricsOnTheGo.Controls.SettingsPanel.BundledFont(fontName) ?? new FontFamily(fontName); }
            catch { ff = null; }
        }
        _font = ff;

        foreach (var tb in _lineBlocks)
        {
            tb.FontSize = _fontSize;
            tb.LineHeight = _fontSize * 1.28;
            tb.TextAlignment = _align;
            tb.Foreground = _textBrush;
            ApplyFont(tb);
        }
        ApplyFont(PlainTextBlock);
        PlainTextBlock.FontSize = _fontSize;
        PlainTextBlock.LineHeight = _fontSize * 1.28;
        PlainTextBlock.Foreground = _textBrush;
        PlainTextBlock.TextAlignment = _align;

        if (_lineBlocks.Count > 0)
        {
            UpdateHighlight();
            Dispatcher.BeginInvoke(new Action(RecenterInstant), DispatcherPriority.Loaded);
        }

        // Click-through / transparent mode = pure HUD: fully transparent (derived, not stored).
        ApplyBackdrop();
        OffsetLabel.Text = FormatOffset(_settings.Offset);
        OffsetLabel.Visibility = _settings.Offset != 0 ? Visibility.Visible : Visibility.Collapsed;

        // Header slider shows transparency (100 - BgOpacity); a no-op when the slider itself caused this.
        double sv = 100 - _settings.BgOpacity;
        if (Math.Abs(BgOpacitySlider.Value - sv) > 0.01)
        {
            _syncingSlider = true;
            BgOpacitySlider.Value = sv;
            _syncingSlider = false;
        }
        _sliderReady = true;

        // Hide/show Japanese lyrics immediately when the setting flips (cache is untouched).
        if (_hideJpApplied != _settings.HideJapanese)
        {
            _hideJpApplied = _settings.HideJapanese;
            OnLyricsState(_lastState);
        }

        _lyrics.PlainFallback = _settings.PlainFallback;
        _tray?.SetClickThroughChecked(_settings.ClickThrough);
        UpdateClickThrough();
        UpdateClickThroughVisual();

        // Readability outline (off by default): ONE shared frozen shadow, applied only to the smallest
        // elements (status, title, and the active line in UpdateHighlight). Never on a window-sized
        // container (LyricsViewport/PlainScroll) — that risks a black-rectangle off-screen surface.
        Effect? fx = _settings.TextOutline ? TextShadow : null;
        StatusText.Effect = fx;
        HeaderTitle.Effect = fx;
        HeaderTitle.FontSize = _settings.TitleSize;       // header line height follows (min 28); the control group stays 28 px, top-pinned
        HeaderTitle.LineHeight = _settings.TitleSize * 1.25;
        TitleCanvas.Height = Math.Ceiling(_settings.TitleSize * 1.25);
        ApplyFont(HeaderTitle);
        UpdateTitleMarquee();

        // The progress fill tracks the chosen lyrics text colour.
        ProgressFill.Background = _textBrush;

        UpdateFooterLayout();
        ApplyHeaderMode();
    }

    private void ApplyFont(System.Windows.Controls.TextBlock tb)
    {
        if (_font is null)
            tb.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
        else
            tb.FontFamily = _font;
    }

    // ---- Title marquee (endless one-way ticker) -----------------------------------
    //
    // A title wider than the row is shown as two copies (HeaderTitle, HeaderTitle2 with a 48 px left margin) in
    // TitleStrip. The strip slides left at 40 px/s by exactly one period (copy width + gap); at that point copy 2
    // sits where copy 1 started, so the loop restart (X back to the start) is invisible. The first lap waits
    // 1.5 s (BeginTime applies once, not per repeat). Short titles (or hidden window/title) get no copy and no
    // animation, so nothing ticks. Restarted only when text, size, font or row width changed (signature), because
    // ApplySettings/UpdateHeaderTrack run very often. A 12 px horizontal fade mask is used only while scrolling;
    // the strip starts 12 px in so the first letters are fully opaque at rest.

    private const double TitleGapPx = 48;     // keep in sync with HeaderTitle2.Margin in MainWindow.xaml
    private const double TitleFadePx = 12;

    private void UpdateTitleMarquee()
    {
        double rowW = TitleCanvas.ActualWidth;
        bool active = IsVisible && HeaderInfo.Visibility == Visibility.Visible && rowW > 0;
        string sig = $"{active}|{HeaderTitle.Text}|{HeaderTitle.FontSize}|{HeaderTitle.FontFamily}|{rowW:0}";
        if (sig == _titleSig)
            return;
        _titleSig = sig;

        TitleTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        TitleTranslate.X = 0;
        HeaderTitle2.Visibility = Visibility.Collapsed;
        TitleCanvas.OpacityMask = null;
        if (!active)
            return;

        HeaderTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = HeaderTitle.DesiredSize.Width;
        if (textW - rowW <= 1)
            return;   // fits: static, left aligned, single copy

        // Gap = a full row (+ a little) so the title scrolls fully out, the row stays empty, then it re-enters from the right.
        double gap = Math.Max(TitleGapPx, rowW + 24);
        HeaderTitle2.Margin = new Thickness(gap, 0, 0, 0);
        double period = Math.Ceiling(textW) + gap;
        HeaderTitle2.Visibility = Visibility.Visible;
        double f = Math.Min(TitleFadePx / rowW, 0.25);
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, f));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();
        TitleCanvas.OpacityMask = mask;

        TitleTranslate.X = TitleFadePx;   // base value during the initial hold
        var anim = new DoubleAnimation(TitleFadePx, TitleFadePx - period, TimeSpan.FromSeconds(period / MarqueePxPerSec))
        {
            BeginTime = TimeSpan.FromSeconds(MarqueePauseSec),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        TitleTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void ApplyBackdrop()
    {
        if (_glass is null)
            return;
        Color bg = ParseColor(_settings.BgColor, Color.FromRgb(0x08, 0x08, 0x08));
        float o = _settings.BgOpacity / 100f;
        bool clear = _settings.TransparentMode || _settings.ClickThrough;
        float level = clear ? 0f : Math.Clamp(o * BlurGain, 0f, 1f);
        float tint = clear ? 0f : Math.Clamp(o * TintGain, 0f, 1f);
        var key = (bg.R, bg.G, bg.B, level, tint);
        if (_lastBackdrop == key)
            return;
        _lastBackdrop = key;
        _glass.SetBackdropLevel(level);
        _glass.UpdateTint(bg.R, bg.G, bg.B, tint);
    }

    /// <summary>"+0.5s" / "-1.0s" / "0.0s". Positive = lyrics delayed (see OnInterpTick: pos -= offset).</summary>
    private static string FormatOffset(int ms)
        => ms == 0 ? "0.0s" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(ms > 0 ? "+" : "-")}{Math.Abs(ms) / 1000.0:0.0}s");

    // Dark, tight, zero-offset glow: cheap (small blur radius) and reads as an outline on any backdrop.
    private static readonly Effect TextShadow = CreateTextShadow();

    private static Effect CreateTextShadow()
    {
        var fx = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 0,
            BlurRadius = 5,
            Opacity = 0.9,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        };
        fx.Freeze();
        return fx;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplySettings();
        _settings.Save();

        if (e.PropertyName == nameof(Settings.BridgeEnabled))
            ApplyBridgeEnabled();

        // A user-driven offset change is saved per-song; loading a song's offset is not (guarded).
        if (e.PropertyName == nameof(Settings.Offset) && !_loadingOffset)
            OffsetStore.Set(_offsetKey, _settings.Offset);
    }

    // ---- Footer: progress bar + times (README §3.6) -----------------------------

    private void UpdateFooterLayout()
    {
        bool p = _settings.ShowProgress;
        bool t = _settings.ShowTimes;
        FooterBar.Visibility = (p || t) ? Visibility.Visible : Visibility.Collapsed;
        // Collapse (not Hide) the track when off: the middle column stays * so the times,
        // when shown, are pushed to the left/right edges as if the bar were still there.
        ProgressTrack.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
        TimeCurrent.Visibility = t ? Visibility.Visible : Visibility.Collapsed;
        TimeTotal.Visibility = t ? Visibility.Visible : Visibility.Collapsed;
        UpdateBottomInset();   // also lifts the hover bar above the footer and reserves the footer's height
        UpdateProgress();
        UpdateEdgeFade();   // bottom fade grows/shrinks with the footer's presence
    }

    private void UpdateProgress()
    {
        if (FooterBar.Visibility != Visibility.Visible)
            return;

        double dur = _lastNp.DurationMs;
        bool hasDur = dur > 0 && !double.IsNaN(dur) && !double.IsInfinity(dur);
        double pos = _lastNp.PositionMs;
        if (_lastNp.IsPlaying)
            pos += (DateTime.UtcNow - _lastNpAt).TotalMilliseconds;
        if (hasDur)
            pos = Math.Clamp(pos, 0, dur);

        if (_settings.ShowProgress)
        {
            double frac = hasDur ? pos / dur : 0;   // unknown length: empty bar
            ProgressFill.Width = Math.Max(0, frac * ProgressTrack.ActualWidth);
        }
        if (_settings.ShowTimes)
        {
            TimeCurrent.Text = FormatTime(pos);
            TimeTotal.Text = hasDur ? FormatTime(dur) : "--:--";
        }
    }

    private static string FormatTime(double ms)
    {
        if (ms < 0 || double.IsNaN(ms))
            ms = 0;
        int total = (int)(ms / 1000);
        return $"{total / 60}:{total % 60:00}";
    }

    // ---- Hover controls (header buttons show only while the mouse is over the window) -----

    // Temporary strings until the lead adds I18n keys: Tx() uses the I18n table when the key exists,
    // else the (en, ko) fallback below.
    private static string Tx(string key, string en, string ko)
    {
        string s = I18n.T(key);
        return s != key ? s : (I18n.Lang == "ko" ? ko : en);
    }

    private static readonly Geometry PlayGeometry = Geometry.Parse("M6 3 20 12 6 21Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M6 4H10V20H6Z M14 4H18V20H14Z");

    /// <summary>Apply the auto-hide setting: when off, the controls are always shown; when on they
    /// follow the mouse (hidden after the grace delay if it is not over the window).</summary>
    private void ApplyHeaderMode()
    {
        if (!_settings.AutoHideHeader)
        {
            _headerTimer?.Stop();
            SetHeaderShown(true);
        }
        else if (_hover)
            SetHeaderShown(true);
        else
            RestartHeaderTimer();
    }

    /// <summary>Mouse entered/left the window. Entering shows the controls at once; leaving hides
    /// them after <see cref="HeaderHideDelayMs"/>. Idempotent so the click-through poll can call it.</summary>
    private void SetHover(bool hover)
    {
        if (hover == _hover)
            return;
        _hover = hover;
        if (hover)
        {
            _headerTimer?.Stop();
            _lastCursor = System.Windows.Forms.Cursor.Position;
            _lastMoveAt = DateTime.UtcNow;
            _idleTimer?.Start();
            if (!_headerShown)
                SetHeaderShown(true);
        }
        else
        {
            _idleTimer?.Stop();
            RestartHeaderTimer();
        }
    }

    /// <summary>While hovering: hide the controls after <see cref="HoverIdleMs"/> without cursor movement and
    /// show them again on the next movement. Uses the real cursor position so it also works in click-through mode.</summary>
    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!_settings.AutoHideHeader || _settingsOpen)
            return;
        var pos = System.Windows.Forms.Cursor.Position;
        if (pos != _lastCursor)
        {
            _lastCursor = pos;
            _lastMoveAt = DateTime.UtcNow;
            if (!_headerShown)
                SetHeaderShown(true);
        }
        else if (_headerShown && (DateTime.UtcNow - _lastMoveAt).TotalMilliseconds >= HoverIdleMs)
            SetHeaderShown(false);
    }

    private void RestartHeaderTimer()
    {
        if (_headerTimer is null)
            return;
        _headerTimer.Stop();
        if (_settings.AutoHideHeader)
            _headerTimer.Start();
    }

    private void OnHeaderTimerTick(object? sender, EventArgs e)
    {
        _headerTimer!.Stop();
        // MouseLeave also fires when the cursor merely moves onto the 8 px resize border; the real
        // cursor position is the source of truth, so keep waiting while it is still over the window.
        if (_hover || _settingsOpen || CursorInWindow())
        {
            if (!_hover)
                _headerTimer.Start();
            return;
        }
        HideHeaderIfAllowed();
    }

    private void HideHeaderIfAllowed()
    {
        if (!_settings.AutoHideHeader || _hover || _settingsOpen)
            return;
        if (_headerShown)
            SetHeaderShown(false);
    }

    /// <summary>Fade the header buttons and the bottom bar in/out. The title (HeaderInfo) is outside HeaderButtons and
    /// always stays visible.</summary>
    private void SetHeaderShown(bool shown)
    {
        _headerShown = shown;
        HeaderButtons.IsHitTestVisible = shown;

        HeaderButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(shown ? 1 : 0,
            TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // Bottom bar: slide up + fade in (150 ms); slide back down + fade out.
        BottomBar.IsHitTestVisible = shown;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BottomBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(shown ? 1 : 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        BottomBarTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(shown ? 0 : 14, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });

        UpdateBottomInset();   // lyrics move up by the bar height while it is shown, back when hidden
    }

    private void OnBottomBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBottomInset();

    /// <summary>Shrinks the lyrics viewport by the space the bar takes from the bottom of the window (0 when
    /// hidden). The viewport's own SizeChanged then refits mask/clip and re-centres the line frame by frame.</summary>
    private void UpdateBottomInset()
    {
        // One calculation for bar + footer: the footer (when on) always takes its height from the bottom; the hover bar
        // sits above it (its bottom margin = footer height) and, while shown, adds its own height on top.
        double footer = FooterBar.Visibility == Visibility.Visible ? FooterBar.ActualHeight : 0;
        double barMargin = footer > 0 ? footer : 10;
        if (Math.Abs(BottomBar.Margin.Bottom - barMargin) > 0.01)
            BottomBar.Margin = new Thickness(0, 0, 0, barMargin);
        // The hover bar overlays the lyrics (they no longer shift up while it is shown); only the footer reserves space.
        double inset = footer;
        // Tiny window: keep a sliver of viewport (otherwise the bar necessarily overlaps).
        inset = Math.Clamp(inset, 0, Math.Max(0, ActualHeight - _viewTop - MinViewportHeight));
        if (Math.Abs(inset - _viewBottom) < 0.5)
            return;
        _viewBottom = inset;
        ApplyViewportMargin(animate: true);
    }

    /// <summary>Single writer of the viewport margin (top = header, bottom = hover bar). Animated for the bar.</summary>
    private void ApplyViewportMargin(bool animate)
    {
        var m = new Thickness(18, _viewTop, 18, _viewBottom);
        PlainScroll.Margin = new Thickness(18, _viewTop, 18, Math.Max(14, _viewBottom));
        StatusText.Margin = new Thickness(24, _viewTop, 24, Math.Max(8, _viewBottom));
        if (animate)
        {
            LyricsViewport.BeginAnimation(MarginProperty, new ThicknessAnimation(m, TimeSpan.FromMilliseconds(BarShiftMs))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            LyricsViewport.BeginAnimation(MarginProperty, null);
            LyricsViewport.Margin = m;
        }
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_settings.ClickThrough)   // click-through: the poll drives hover (no mouse events arrive)
            SetHover(true);
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _dragPending = false;
        if (!_settings.ClickThrough)
            SetHover(false);
    }

    private bool CursorInWindow() => CursorInWindow(System.Windows.Forms.Cursor.Position);

    private bool CursorInWindow(System.Drawing.Point pt)
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r))
            return false;
        return pt.X >= r.left && pt.X < r.right && pt.Y >= r.top && pt.Y < r.bottom;
    }

    // ---- Click-through ----------------------------------------------------------
    //
    // Pre-existing behaviour: Native.SetClickThrough sets WS_EX_TRANSPARENT on the (layered) WPF
    // window, so the OS excludes the WHOLE window from hit-testing — the header buttons were
    // unreachable, and only the tray menu could turn it off. (HTTRANSPARENT from WM_NCHITTEST is not
    // an option: it only forwards to windows of the SAME thread, so clicks would never reach other apps.)
    //
    // Now: while click-through is ON a 100 ms poll reads the real cursor position. The window stays
    // transparent EXCEPT while the cursor is over the visible header buttons (or the settings
    // overlay is open), where the flag is lifted so the buttons get the click. Recovery paths:
    // the unlock button, the tray "Click-through" item, and tray Show/Quit are all independent of it.

    private void UpdateClickThrough()
    {
        if (_settings.ClickThrough)
        {
            _clickThroughTimer?.Start();                 // no-op if already running
            OnClickThroughTick(null, EventArgs.Empty);   // settle the state now, not 100 ms later
        }
        else if (_clickThroughTimer?.IsEnabled == true)
        {
            _clickThroughTimer.Stop();
            SetPassThrough(false);
            SetHover(CursorInWindow());                  // hand hover back to the MouseEnter/Leave events
        }
    }

    private void OnClickThroughTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
            return;
        var pt = System.Windows.Forms.Cursor.Position;
        bool inside = CursorInWindow(pt);
        SetHover(inside);
        bool interactive = inside && (_settingsOpen || (_headerShown && (IsOverElement(HeaderButtons, pt) || IsOverElement(BottomBar, pt))));
        SetPassThrough(!interactive);
    }

    private static bool IsOverElement(FrameworkElement el, System.Drawing.Point pt)
    {
        try
        {
            Point tl = el.PointToScreen(new Point(0, 0));
            Point br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            return pt.X >= tl.X && pt.X < br.X && pt.Y >= tl.Y && pt.Y < br.Y;
        }
        catch (InvalidOperationException) { return false; }   // not (yet) connected to a presentation source
    }

    private void SetPassThrough(bool on)
    {
        if (on == _passThrough)
            return;
        _passThrough = on;
        Native.SetClickThrough(_hwnd, on);
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e)
        => _settings.ClickThrough = !_settings.ClickThrough;   // -> OnSettingChanged -> ApplySettings

    /// <summary>Green when click-through is ON (the button is then the "turn it off" control).</summary>
    private void UpdateClickThroughVisual()
    {
        bool on = _settings.ClickThrough;
        ClickThroughIcon.Stroke = on ? AccentBrush : Brushes.White;
        ClickThroughButton.Opacity = on ? 1.0 : 0.5;
        ClickThroughButton.ToolTip = on
            ? Tx("clickThroughOff", "Click-through is on - click to turn off", "클릭 무시 켜짐 - 눌러서 해제")
            : Tx("clickThroughOn", "Click-through (ignore mouse clicks)", "클릭 무시 (마우스 클릭이 뒤 창으로 통과)");
    }

    // ---- Transport controls (prev / play-pause / next via SMTC) ------------------

    private void OnPrevTrack(object sender, RoutedEventArgs e) => _ = SendTransport("prev");
    private void OnPlayPause(object sender, RoutedEventArgs e) => _ = SendTransport("toggle");
    private void OnNextTrack(object sender, RoutedEventArgs e) => _ = SendTransport("next");

    // Offset > 0 delays the lyrics (OnInterpTick: pos -= offset); "+" = later, "-" = earlier.
    // Settings.Offset clamps itself; OnSettingChanged persists it per-song via OffsetStore.
    private void OnSeekBack(object sender, RoutedEventArgs e) => _ = SendSeek((long)(CurrentPositionMs() - SeekStepMs));
    private void OnSeekForward(object sender, RoutedEventArgs e) => _ = SendSeek((long)(CurrentPositionMs() + SeekStepMs));

    /// <summary>Current playback position: last reported value plus the time since (while playing).</summary>
    private double CurrentPositionMs()
        => _lastNp.PositionMs + (_lastNp.IsPlaying ? (DateTime.UtcNow - _lastNpAt).TotalMilliseconds : 0);

    /// <summary>CC button: shown only when this video has usable official captions (in use) or the user switched them off;
    /// bright = captions are the lyrics source, dim = switched off (click to use them again).</summary>
    private void UpdateCaptionButton()
    {
        string id = _lastNp.VideoId;
        if (_lastState is { Kind: LyricsKind.Skipped, PlainText: "nonmusic" } && id.Length > 0)
        {   // wrongly classified as non-music? one click looks the song up anyway
            CaptionButton.Visibility = Visibility.Visible;
            CaptionButton.Opacity = 1.0;
            CaptionButton.Content = "♪";
            CaptionButton.ToolTip = Tx("forceMusic", "This is music - look up lyrics anyway", "이 영상은 음악이에요 - 가사 찾기");
            return;
        }
        string kind = _lyrics.CaptionKind(id);          // "captions" | "aligned" | ""
        bool rejected = id.Length > 0 && CaptionHub.IsRejected(id);
        var info = id.Length > 0 ? CaptionHub.Get(id) : null;
        bool available = info is not null && (info.Status == "ok" || info.AsrWords.Count >= 8 || info.Tracks is not null);
        CaptionButton.Visibility = available && (kind.Length > 0 || rejected) ? Visibility.Visible : Visibility.Collapsed;
        CaptionButton.Opacity = kind.Length > 0 ? 1.0 : 0.4;
        CaptionButton.Content = kind switch { "aligned" => "보정", "mix" => "MIX", _ => "CC" };
        CaptionButton.ToolTip = kind switch
        {
            "captions" => Tx("ccOn", "Using YouTube captions - click to use LRCLIB lyrics instead", "유튜브 자막 사용 중 - 눌러서 LRCLIB 가사로 전환"),
            "mix" => Tx("ccMix", "Lyrics joined from the description's track list - click to turn off", "설명란 시간표로 곡별 가사를 이어 붙임 - 눌러서 끄기"),
            "aligned" => Tx("ccAligned", "Lyrics re-timed to this video - click to use the original timing", "이 영상에 맞게 시간 보정됨 - 눌러서 원본 타이밍으로 전환"),
            _ => Tx("ccOff", "YouTube captions / re-timing off for this video - click to use them", "이 영상의 자막/시간 보정 꺼짐 - 눌러서 다시 사용"),
        };
    }

    private void OnToggleCaptions(object sender, RoutedEventArgs e)
    {
        string id = _lastNp.VideoId;
        if (id.Length == 0)
            return;
        if (_lastState is { Kind: LyricsKind.Skipped, PlainText: "nonmusic" })
        {
            _lyrics.ForceMusic(id);
            return;
        }
        _lyrics.SetCaptionsRejected(id, _lyrics.ShowingCaptions(id));   // showing -> switch off; off -> switch on
        UpdateCaptionButton();
    }

    /// <summary>Reset the current song's sync offset to 0 (OnSettingChanged persists it per song).</summary>
    private void OnResync(object sender, RoutedEventArgs e) => _settings.Offset = 0;

    /// <summary>Slider is shown inverted (right = more transparent): BgOpacity = 100 - value.</summary>
    private void OnBgOpacitySlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sliderReady || _syncingSlider)
            return;
        _settings.BgOpacity = 100 - (int)Math.Round(e.NewValue);   // -> OnSettingChanged -> ApplySettings
    }

    /// <summary>Single entry point for media commands ("prev" | "toggle" | "next"); the only place to
    /// change when the browser-extension transport is wired in. Re-polls shortly after so the
    /// play/pause icon and title follow. SmtcReader logs failures to DiagLog; this also guards the UI.</summary>
    private async System.Threading.Tasks.Task SendTransport(string cmd)
    {
        try
        {
            // Bridge source: queued for the extension, delivered in the response to its next POST
            // (up to ~1 s later). Play/pause icon and position follow via BrowserBridge.Updated.
            if (BridgeActive(out _) && cmd is "prev" or "toggle" or "next")
            {
                _bridge!.EnqueueCommand(cmd);
                return;
            }

            var command = cmd switch
            {
                "prev" => _smtc.SkipPreviousAsync(),
                "toggle" => _smtc.TogglePlayPauseAsync(),
                "next" => _smtc.SkipNextAsync(),
                _ => throw new ArgumentException("unknown transport command", nameof(cmd)),
            };
            await command;
            await System.Threading.Tasks.Task.Delay(300);
            await PollAsync();
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("transport", "MainWindow", DiagResult.Error, ex.Message, 0, false);
        }
    }

    /// <summary>Seek the player to <paramref name="positionMs"/> (clamped to the track). The one place to
    /// change when the browser-extension transport is wired in; re-polls shortly after like SendTransport.</summary>
    private async System.Threading.Tasks.Task SendSeek(long positionMs)
    {
        try
        {
            positionMs = Math.Max(0, positionMs);
            if (_lastNp.DurationMs > 0)
                positionMs = Math.Min(positionMs, (long)_lastNp.DurationMs);

            EndPeek(scrollBack: false);   // the active line changes next poll and the normal follow takes over

            // Bridge source: delivered with the extension's next POST response (up to ~1 s later); the new
            // position then arrives via BrowserBridge.Updated and re-aligns the lyrics (> 300 ms jump).
            if (BridgeActive(out _))
            {
                SeekLog($"seek via=bridge target={positionMs} dur={_lastNp.DurationMs:0} (queued for the extension's next POST)");
                _bridge!.EnqueueCommand($"seek:{positionMs}");
                return;
            }

            // Why not the bridge: log the numbers that decide BridgeActive (enabled / bridge exists / fresh report / video id).
            bool fresh = BridgeFreshReport(out _, out var at);
            SeekLog($"seek via=smtc target={positionMs} bridgeEnabled={_settings.BridgeEnabled} bridgeFresh={fresh} "
                + $"ageMs={(fresh || at == default ? 0 : (long)(DateTimeOffset.UtcNow - at).TotalMilliseconds)} videoId={(_lastNp.VideoId.Length > 0 ? "set" : "empty")}");
            await _smtc.SeekAsync(positionMs);   // SmtcReader logs the result row
            await System.Threading.Tasks.Task.Delay(300);
            await PollAsync();
        }
        catch (Exception ex)
        {
            SeekLog("seek failed: " + ex.GetType().Name + " " + ex.Message, error: true);
        }
    }

    private void UpdateTransportVisual()
    {
        bool playing = _lastNp.HasSession && _lastNp.IsPlaying;
        PlayPauseIcon.Data = playing ? PauseGeometry : PlayGeometry;
        PlayPauseButton.ToolTip = playing
            ? Tx("pause", "Pause", "일시정지")
            : Tx("play", "Play", "재생");
        PrevButton.ToolTip = Tx("prevTrack", "Previous track", "이전 곡");
        NextButton.ToolTip = Tx("nextTrack", "Next track", "다음 곡");
        SyncMinusButton.ToolTip = Tx("syncEarlier", "Show lyrics 0.5 s earlier (-)", "가사를 0.5초 앞당김 (-)");
        SyncPlusButton.ToolTip = Tx("syncLater", "Show lyrics 0.5 s later (+)", "가사를 0.5초 늦춤 (+)");
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { /* invalid hex while typing */ }
        return fallback;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        SettingsOverlay.OnOpened();
        SettingsOverlay.Visibility = Visibility.Visible;
        UpdateClickThrough();   // click-through: make the whole window clickable while settings are open
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        UpdateClickThrough();
    }

    private void OnCloseToTray(object sender, RoutedEventArgs e) => HideToTray();

    // ---- Tray / visibility / pin -----------------------------------------------

    /// <summary>Hide the overlay to the tray: both the WPF UI and the glass backdrop. The app
    /// keeps running; only the tray "Quit" item actually exits.</summary>
    private void HideToTray()
    {
        SaveWindowPlacement();   // remember where/what size we were when the overlay is dismissed
        if (_settingsOpen)
            CloseSettings();
        Hide();
        _glass?.SetVisible(false);
        _tray?.SetVisibleState(false);
    }

    private void ShowFromTray()
    {
        Show();
        _glass?.SetVisible(true);
        SyncGlass();
        Activate();
        _tray?.SetVisibleState(true);
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
            HideToTray();
        else
            ShowFromTray();
    }

    /// <summary>The only real exit (tray "Quit"). Closing the window then shuts down the app.</summary>
    private void Quit()
    {
        _exiting = true;
        Close();
    }

#if DEBUG
    /// <summary>Open the diagnostics window (single instance: reuse + focus if already open).
    /// Debug builds only — excluded from Release/the MSI so end users never see it.</summary>
    private void ShowDiagnostics()
    {
        if (_diagWindow is null)
        {
            _diagWindow = new Diagnostics.DiagnosticsWindow();
            _diagWindow.Closed += (_, _) => _diagWindow = null;
            _diagWindow.Show();
            return;
        }

        if (_diagWindow.WindowState == WindowState.Minimized)
            _diagWindow.WindowState = WindowState.Normal;
        _diagWindow.Activate();
    }
#endif

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        // Re-glue the glass directly behind the UI window. PositionBehind inherits the UI's
        // topmost band, so the backdrop never floats above (and hides) the UI — the bug that
        // SetTopmost(glass, ...) caused by raising the glass to the top of the topmost band.
        SyncGlass();
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        // Accent stroke + full opacity when pinned; dim white when free-floating.
        PinIcon.Stroke = _pinned
            ? new SolidColorBrush(Color.FromRgb(0x17, 0xD3, 0x46))
            : Brushes.White;
        PinButton.Opacity = _pinned ? 1.0 : 0.5;
    }

    // ---- Header drag / now-playing title ----------------------------------------

    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0x17, 0xD3, 0x46));

    // Window-level handlers (wired in XAML). Buttons mark their own clicks handled, so they never
    // reach here; the settings overlay is excluded explicitly.

    /// <summary>Left button down anywhere on the body: arm a drag. A double-click on the header row
    /// does nothing; below it, it seeks to the clicked lyric line.</summary>
    private void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settingsOpen)
            return;

        // Slider input (thumb/track) never arms a window drag.
        DependencyObject? src = e.OriginalSource as DependencyObject;
        for (var d = src; d is not null; d = (d is Visual or System.Windows.Media.Media3D.Visual3D)
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.RangeBase)
                return;

        Point p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            _dragPending = false;
            if (p.Y <= HeaderBar.ActualHeight)
            {
                // header (title) double-click: intentionally nothing
            }
            else if (_lines is null)
                SeekLog($"dblclick ignored: no synced lyrics y={p.Y:0}");
            else if (LyricLineAt(p) is int i and >= 0 && i < _lines.Count)
            {
                // Seek so that line i is the highlighted one: OnInterpTick uses pos - offset >= TimeMs.
                long target = (long)Math.Round(_lines[i].TimeMs + _offsetMs);
                SeekLog($"dblclick line={i} t={_lines[i].TimeMs:0} offset={_offsetMs:0} target={target}");
                _ = SendSeek(target);
            }
            else
                SeekLog($"dblclick ignored: no line at x={p.X:0} y={p.Y:0} viewport={LyricsViewport.ActualHeight:0}");
            return;
        }
        _dragPending = true;
        _dragOrigin = p;
    }

    /// <summary>Start the actual window drag only after the pointer moved past the system drag
    /// threshold, so a plain click (or a tiny jitter) does nothing.</summary>
    private void OnBodyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragPending = false;
            return;
        }

        Point p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragPending = false;
        try { DragMove(); }
        catch (InvalidOperationException) { /* button was released between the move and the call */ }
    }

    private void OnBodyMouseUp(object sender, MouseButtonEventArgs e) => _dragPending = false;

    /// <summary>Index of the lyric line whose slot (block + its 5 px top/bottom margin) contains the window point
    /// <paramref name="windowPoint"/>, else -1. Position based on purpose: a TextBlock only hit-tests its glyph
    /// boxes, so a double-click beside the text, in the 10 px gap between lines or on a short line used to
    /// resolve to no line and was silently ignored.</summary>
    private int LyricLineAt(Point windowPoint)
    {
        if (_lineBlocks.Count == 0 || LyricsViewport.Visibility != Visibility.Visible)
            return -1;
        try
        {
            Point v = TranslatePoint(windowPoint, LyricsViewport);
            if (v.X < 0 || v.Y < 0 || v.X > LyricsViewport.ActualWidth || v.Y > LyricsViewport.ActualHeight)
                return -1;   // outside the lyrics area (header, hover bar, margins)
            double y = TranslatePoint(windowPoint, LyricsPanel).Y;   // includes the scroll translate
            double top = 0;
            for (int i = 0; i < _lineBlocks.Count; i++)
            {
                double next = top + _lineBlocks[i].ActualHeight + 10;
                if (y >= top && y < next)
                    return i;
                top = next;
            }
        }
        catch (InvalidOperationException) { /* not connected to a presentation source */ }
        return -1;
    }

    /// <summary>One DiagLog row for the double-click seek chain (numbers only; no track names).</summary>
    private static void SeekLog(string detail, bool error = false)
        => DiagLog.LogCompleted("seek", "MainWindow", error ? DiagResult.Error : DiagResult.Plain, detail, 0, false);

    private bool _fullscreen;
    private Native.RECT _preFsRect;

    private string _offsetKey = "";   // song key the current offset belongs to
    private bool _loadingOffset;      // guards offset-load from being saved back as a user edit

    /// <summary>Toggle borderless windowed-fullscreen (covers the current monitor; not exclusive).</summary>
    internal void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            Native.GetWindowRect(_hwnd, out _preFsRect);
            var s = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
            Native.SetBounds(_hwnd, s.Left, s.Top, s.Width, s.Height);
        }
        else
        {
            var r = _preFsRect;
            Native.SetBounds(_hwnd, r.left, r.top, r.right - r.left, r.bottom - r.top);
        }
    }

    private void UpdateHeaderTrack()
    {
        if (_lastNp.HasSession && !string.IsNullOrWhiteSpace(_lastNp.Title))
        {
            HeaderTitle.Text = TitleDisplay.Clean(_lastNp.Title);   // no emoji / hashtags / [Official MV] clutter
            HeaderInfo.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderInfo.Visibility = Visibility.Collapsed;
        }
        UpdateTitleMarquee();   // no-op unless text/width/visibility changed
        UpdateTransportVisual();
    }

    // ---- Ctrl+wheel font size (the lyrics otherwise auto-follow; no manual scroll) ----

    private void OnLyricsWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
            return;

        // Ctrl + wheel resizes the lyrics text directly (2px per notch, clamped 14-72).
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _settings.FontSize += e.Delta > 0 ? 2 : -2;
            e.Handled = true;
            return;
        }

        // Plain wheel: peek at earlier/later lines (one notch = one line), clamped to the whole lyric range.
        if (_lineBlocks.Count == 0 || LyricsViewport.ActualHeight <= 0)
            return;
        double hi = TargetFor(0), lo = TargetFor(_lineBlocks.Count - 1);   // first line centred .. last line centred
        double y = LyricsTranslate.Y;                                     // current (possibly mid-animation) offset
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsTranslate.Y = Math.Clamp(y + e.Delta / 120.0 * (_fontSize * 1.28 + 10), Math.Min(lo, hi), hi);
        _lastScrollTarget = double.NaN;
        _peeking = true;
        _peekTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PeekReturnMs) };
        _peekTimer.Tick -= OnPeekTimer;
        _peekTimer.Tick += OnPeekTimer;
        _peekTimer.Stop();
        _peekTimer.Start();   // restart: the 3 s count from the LAST notch
        e.Handled = true;
    }

    private void OnPeekTimer(object? sender, EventArgs e) => EndPeek(scrollBack: true);

    /// <summary>Leave peek mode; optionally ease back to the active line (auto-follow resumes either way).</summary>
    private void EndPeek(bool scrollBack)
    {
        _peekTimer?.Stop();
        if (!_peeking)
            return;
        _peeking = false;
        _lastScrollTarget = double.NaN;   // Y was moved by hand: force the next ScrollToIndex to run
        if (scrollBack)
            ScrollToIndex(Math.Max(_activeIndex, 0), animate: true);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Re-glue the glass backdrop on ANY position/size/z change — far more reliable
        // than WPF's LocationChanged/SizeChanged (which miss maximize, Snap, etc.).
        if (msg == WM_WINDOWPOSCHANGED)
        {
            SyncGlass();
            return IntPtr.Zero; // not handled — let WPF process it too
        }

        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        handled = true;

        // When click-through is on, the UI window carries WS_EX_LAYERED | WS_EX_TRANSPARENT, so
        // the OS excludes it from hit-testing entirely and this hook isn't even called — the mouse
        // falls straight through to whatever is behind (the click-through poll lifts the flag only
        // over the header buttons, see OnClickThroughTick). Hover show/hide is event-driven
        // (MouseEnter/Leave) and not handled here.

        long lp = lParam.ToInt64();
        int px = (short)(lp & 0xFFFF);
        int py = (short)((lp >> 16) & 0xFFFF);

        Native.GetWindowRect(_hwnd, out var r);

        bool left = px < r.left + ResizeBorder;
        bool right = px >= r.right - ResizeBorder;
        bool top = py < r.top + ResizeBorder;
        bool bottom = py >= r.bottom - ResizeBorder;

        int code = (top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            // Interior is HTCLIENT (not HTCAPTION): the whole body drags the window through
            // DragMove (OnBodyMouseMove) after a drag threshold, so plain clicks stay clicks.
            _ => HTCLIENT,
        };

        // The resize border wins over interactive controls. Previously IsOverInteractive ran first and
        // the full-window SettingsPanel overlay counted as "interactive", so with settings open every
        // edge pixel returned HTCLIENT and the window could not be resized.
        if (code == HTCLIENT && IsOverInteractive(px, py))
            return (IntPtr)HTCLIENT;
        return (IntPtr)code;
    }

    // ---- Now-playing + lyrics ---------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LyricsCache.EnsureVersion();
        _lyrics.Changed += OnLyricsState;
        _lyrics.CaptionsChanged += UpdateCaptionButton;

        I18n.SetLanguage(_settings.Lang);
        I18n.Changed += Localize;

        _tray = new TrayIcon();
        _tray.ToggleVisibilityRequested += ToggleVisibility;
        _tray.ClickThroughToggled += on => _settings.ClickThrough = on;
#if DEBUG
        _tray.DiagnosticsRequested += ShowDiagnostics;
#endif
        _tray.QuitRequested += Quit;
        _tray.SetVisibleState(IsVisible);
        _tray.SetClickThroughChecked(_settings.ClickThrough);

        SettingsOverlay.Initialize(_settings);
        SettingsOverlay.CloseRequested += CloseSettings;
        _settings.PropertyChanged += OnSettingChanged;
        _headerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HeaderHideDelayMs) };
        _headerTimer.Tick += OnHeaderTimerTick;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _idleTimer.Tick += OnIdleTick;
        _clickThroughTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _clickThroughTimer.Tick += OnClickThroughTick;

        // Recompute the bottom edge fade when the footer appears/disappears or changes height,
        // so its height is included once layout has settled.
        FooterBar.SizeChanged += (_, _) => { UpdateBottomInset(); UpdateEdgeFade(); };

        // Ctrl+wheel over the synced lyrics changes the font size (no manual scrolling).
        LyricsViewport.MouseWheel += OnLyricsWheel;

        ApplySettings();
        Localize();
        UpdatePinVisual();
        UpdateEdgeFade();

        try
        {
            BrowserBridge.EnsureToken(_settings);
            _bridge = new BrowserBridge(_settings);
            _bridge.Updated += OnBridgeUpdated;
            _bridge.QuitRequested += () => Dispatcher.BeginInvoke(new Action(Quit));   // popup "앱 끄기"
            ApplyBridgeEnabled();
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("bridge", "MainWindow", DiagResult.Error, "init failed: " + ex.Message, 0, false);
        }

        await _smtc.InitializeAsync();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();

        _interpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _interpTimer.Tick += OnInterpTick;
        _interpTimer.Start();

        await PollAsync();
    }

    private async System.Threading.Tasks.Task PollAsync()
    {
        NowPlaying np = PreferBridge(await _smtc.GetNowPlayingAsync());
        ApplyNowPlaying(np);
    }

    // ---- Browser bridge (Chrome extension) --------------------------------------

    private static readonly TimeSpan BridgeFresh = TimeSpan.FromSeconds(3);
    private const double BridgeResyncMs = 300;   // smaller drifts between report and local interpolation are ignored

    private static string Truncate(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    private void ApplyBridgeEnabled()
    {
        if (_bridge is null)
            return;
        if (_settings.BridgeEnabled)
        {
            _bridge.Start();   // no-op when already listening
            if (_bridge.LastError is { } err)
                DiagLog.LogCompleted("bridge", "MainWindow", DiagResult.Error, "start failed: " + err, 0, false);
        }
        else
            _bridge.Stop();
    }

    /// <summary>Latest bridge report when the bridge is on and it was received within <see cref="BridgeFresh"/>.</summary>
    private bool BridgeFreshReport(out BrowserNowPlaying report, out DateTimeOffset at)
    {
        report = null!;
        at = default;
        if (_bridge is null || !_settings.BridgeEnabled)
            return false;
        at = _bridge.LatestAt;
        var b = _bridge.Latest;
        if (b is null || DateTimeOffset.UtcNow - at > BridgeFresh)
            return false;
        report = b;
        return true;
    }

    /// <summary>True when the displayed state comes from a fresh bridge report (commands go to the extension).</summary>
    private bool BridgeActive(out BrowserNowPlaying report)
        => BridgeFreshReport(out report, out _) && _lastNp.VideoId.Length > 0;

    private static NowPlaying FromBridge(BrowserNowPlaying b, DateTimeOffset at)
    {
        // Position is measured from the receive time (LatestAt), not the extension's sentAtMs (clock skew).
        double pos = b.PositionMs;
        if (!b.Paused)
            pos += Math.Max(0, (DateTimeOffset.UtcNow - at).TotalMilliseconds);
        if (b.DurationMs > 0)
            pos = Math.Min(pos, b.DurationMs);
        // Structured music metadata from the extension wins: real track/artist/album instead of video title/channel.
        // Key stays "yt:<videoId>" (VideoId), so caches and saved offsets are unaffected.
        bool structured = !string.IsNullOrWhiteSpace(b.Track);
        string channel = Truncate(b.Channel, 200);
        if (channel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            channel = channel[..^" - Topic".Length].TrimEnd();
        return new NowPlaying
        {
            HasSession = true,
            SourceAppId = "browser-extension",
            IsBrowser = true,   // still a browser source (local provider / diagnostics); Structured selects the search path
            Structured = structured,
            MetaSource = structured ? b.MetaSource ?? "" : "",
            RawTitle = structured ? Truncate(b.Title, 300) : "",
            Title = structured ? Truncate(b.Track!.Trim(), 300) : Truncate(b.Title, 300),
            Artist = structured && !string.IsNullOrWhiteSpace(b.Artist) ? Truncate(b.Artist.Trim(), 200)
                   : structured ? channel : Truncate(b.Channel, 200),
            Album = structured ? Truncate(b.Album?.Trim() ?? "", 200) : "",
            DurationMs = b.DurationMs,
            PositionMs = pos,
            IsPlaying = !b.Paused,
            VideoId = b.VideoId,
            Skip = b.IsAd ? "ad" : !b.IsMusic ? "nonmusic" : "",
        };
    }

    /// <summary>Source selection: a fresh bridge report wins over SMTC, except while it is paused and
    /// SMTC reports a different (non-browser) app playing.</summary>
    private NowPlaying PreferBridge(NowPlaying smtc)
    {
        try
        {
            if (BridgeFreshReport(out var b, out var at)
                && !(b.Paused && smtc.HasSession && smtc.IsPlaying && !smtc.IsBrowser))
                return FromBridge(b, at);
            // Stale report, but the browser's own session still shows that song: keep the video identity
            // (same key → no second lookup, captions stay usable). Play state/position come from the live
            // session; a stale ad report is not reused (the ad may be over).
            if (_bridge is { Latest: { IsAd: false } last } && _settings.BridgeEnabled && smtc.HasSession && smtc.IsBrowser
                && DateTimeOffset.UtcNow - _bridge.LatestAt < SameSong.MaxReportAge
                && SameSong.Matches(smtc.Title, last.Title, last.Track))
            {
                var kept = FromBridge(last, _bridge.LatestAt);
                return kept with
                {
                    IsPlaying = smtc.IsPlaying,
                    // Firefox sessions may carry no position (0): then extrapolate from the report instead.
                    PositionMs = smtc.PositionMs > 0 ? smtc.PositionMs : kept.PositionMs,
                };
            }
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("bridge", "MainWindow", DiagResult.Error, "source select failed: " + ex.Message, 0, false);
        }
        return smtc;
    }

    /// <summary>Runs on a thread-pool thread: only marshals to the UI thread, never touches UI state.</summary>
    private void OnBridgeUpdated(BrowserNowPlaying b)
    {
        try { Dispatcher.BeginInvoke(new Action(() => ApplyBridgeUpdate(b))); }
        catch (Exception ex)   // dispatcher shut down
        {
            DiagLog.LogCompleted("bridge", "MainWindow", DiagResult.Error, "dispatch failed: " + ex.Message, 0, false);
        }
    }

    /// <summary>UI thread. Applies a report at once (no 1 s poll wait). A same-video report whose position
    /// is within <see cref="BridgeResyncMs"/> of the local interpolation is ignored (smooth); a larger
    /// difference (seek, buffering, drift) re-aligns immediately. A video change goes through OnTrack.</summary>
    private void ApplyBridgeUpdate(BrowserNowPlaying b)
    {
        try
        {
            if (_bridge is null || !_settings.BridgeEnabled || !ReferenceEquals(_bridge.Latest, b))
                return;   // disabled, or superseded by a newer report (which has its own dispatch)
            if (b.Paused && _lastNp.HasSession && _lastNp.IsPlaying && !_lastNp.IsBrowser)
                return;   // another app is playing: leave the source choice to the poll

            var np = FromBridge(b, _bridge.LatestAt);
            UpdateShuffleButton();   // shuffle state changes without any playback change
            if (_lastNp.VideoId == np.VideoId && _lastNp.Skip == np.Skip && _lastNp.IsPlaying == np.IsPlaying
                && _lastNp.Title == np.Title && Math.Abs(_lastNp.DurationMs - np.DurationMs) < 1000)
            {
                double local = _lastNp.PositionMs + (_lastNp.IsPlaying ? (DateTime.UtcNow - _lastNpAt).TotalMilliseconds : 0);
                if (Math.Abs(np.PositionMs - local) < BridgeResyncMs)
                    return;
            }
            ApplyNowPlaying(np);
        }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("bridge", "MainWindow", DiagResult.Error, "update failed: " + ex.Message, 0, false);
        }
    }

    /// <summary>UI thread: make <paramref name="np"/> the current state (header, per-song offset, lyrics lookup).</summary>
    private void ApplyNowPlaying(NowPlaying np)
    {
        _lastNp = np;
        _lastNpAt = DateTime.UtcNow;

        // Keep the diagnostics header aware of which app feeds the lookups (and if it's a browser).
        DiagLog.SetSource(np.SourceAppId, np.IsBrowser, np.DurationMs);

        UpdateHeaderTrack();

        // Per-song lyrics offset: on a song change (or no track) load that song's saved offset,
        // resetting to 0 for songs never adjusted — so a tuned song doesn't shift the rest.
        string offsetKey = np.HasSession ? np.Key : "";
        if (offsetKey != _offsetKey)
        {
            _offsetKey = offsetKey;
            _loadingOffset = true;
            _settings.Offset = string.IsNullOrEmpty(offsetKey) ? 0 : OffsetStore.Get(offsetKey);
            _loadingOffset = false;
        }

        _lyrics.OnTrack(np);
        UpdateCaptionButton();
        UpdateShuffleButton();
    }

    /// <summary>Shuffle button: shown on youtube.com playlists (the extension reports a shuffle state for them).
    /// Bright = our shuffle is on; "…" while the playlist is being read; a failure shows its reason as the tooltip.</summary>
    private void UpdateShuffleButton()
    {
        string s = _bridge?.Latest is { } b && b.VideoId == _lastNp.VideoId && _lastNp.VideoId.Length > 0 ? b.Shuffle ?? "" : "";
        ShuffleButton.Visibility = s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.Opacity = s == "on" ? 1.0 : 0.45;
        ShuffleButton.Content = s == "busy" ? "…" : "셔플";
        ShuffleButton.ToolTip = s switch
        {
            "on" => "재생목록 전체를 한 번씩 섞어 재생 중 - 눌러서 끄기",
            "busy" => "재생목록을 읽는 중",
            _ when s.StartsWith("err:", StringComparison.Ordinal) => "셔플 실패: " + s[4..] + " - 눌러서 다시 시도",
            _ => "재생목록 전체를 한 번씩 섞어 재생",
        };
    }

    private void OnToggleShuffle(object sender, RoutedEventArgs e)
    {
        if (_bridge?.Latest is not { Shuffle: { Length: > 0 } s } || s == "busy")
            return;
        _bridge.EnqueueCommand(s == "on" ? "shuffle:off" : "shuffle:on");
    }

    private LyricsState _lastState = new(LyricsKind.NoTrack);

    private void OnLyricsState(LyricsState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLyricsState(state));
            return;
        }

        _lastState = state;
        UpdateCaptionButton();   // the skipped screen offers a "this is music" button
        _japaneseHidden = false;

        // Filter at display time only; the cached/fetched result stays untouched.
        if (_settings.HideJapanese
            && ((state.Kind == LyricsKind.Synced && state.Lines is { Count: > 0 } sl
                    && LyricsFilter.IsMostlyJapanese(sl.Select(l => l.Text)))
                || (state.Kind == LyricsKind.Plain && state.PlainText is not null
                    && LyricsFilter.IsMostlyJapanese(new[] { state.PlainText }))))
        {
            _japaneseHidden = true;
            ShowStatus(Tx("japaneseHidden", "Japanese lyrics hidden", "일본어 가사 숨김"));
            return;
        }

        switch (state.Kind)
        {
            case LyricsKind.Synced when state.Lines is { Count: > 0 }:
                ShowSynced(state.Lines);
                break;
            case LyricsKind.Plain when state.PlainText is not null:
                ShowPlain(state.PlainText);
                break;
            case LyricsKind.Searching:
                ShowSearching();
                break;
            case LyricsKind.Instrumental:
                ShowStatus(I18n.T("instrumental"));
                break;
            case LyricsKind.Skipped:
                ShowStatus(SkippedText(state.PlainText));
                break;
            case LyricsKind.NotFound:
                ShowStatus(I18n.T("notfound"));
                break;
            case LyricsKind.OnlyUnsynced:
                ShowStatus(I18n.T("onlyUnsynced"));
                break;
            default:
                ShowStatus(I18n.T("openSpotify"));
                break;
        }
    }

    private static string SkippedText(string? reason) => reason == "ad"
        ? Tx("adPlaying", "Ad playing", "광고 재생 중")
        : Tx("notMusic", "Not a music video - lyrics lookup skipped (hover: ♪ button)", "음악 영상이 아니어서 가사 조회를 생략함 (마우스를 올리고 ♪ 버튼)");

    /// <summary>Re-render whatever status is currently shown in the new language. Synced/plain
    /// lyrics are content, not localized, so they are left untouched.</summary>
    private void Localize()
    {
        BgOpacitySlider.ToolTip = Tx("bgOpacityTip", "Background opacity (blur)", "배경 투명도(블러)");
        ResyncButton.ToolTip = Tx("resyncTip", "Reset sync to 0 s", "싱크 0초로 초기화");
        PinButton.ToolTip = I18n.T("pin");
        SettingsButton.ToolTip = I18n.T("settingsTitle");
        CloseButton.ToolTip = I18n.T("close");
        UpdateTransportVisual();
        UpdateClickThroughVisual();

        if (_japaneseHidden)
            OnLyricsState(_lastState);

        switch (_lastState.Kind)
        {
            case LyricsKind.Searching:
            case LyricsKind.Instrumental:
            case LyricsKind.NotFound:
            case LyricsKind.OnlyUnsynced:
            case LyricsKind.Skipped:
            case LyricsKind.NoTrack:
                OnLyricsState(_lastState);
                break;
        }
    }

    private void ShowStatus(string text)
    {
        StopSearchDots();
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Two-line "searching" status: the track being looked up, then "Searching…".</summary>
    private void ShowSearching()
    {
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;

        StatusText.Inlines.Clear();
        string shownTitle = TitleDisplay.Clean(_lastNp.Title), shownArtist = TitleDisplay.Clean(_lastNp.Artist);
        string track = !string.IsNullOrWhiteSpace(shownArtist)
            ? $"{shownTitle} — {shownArtist}"
            : shownTitle;
        if (!string.IsNullOrWhiteSpace(track))
        {
            StatusText.Inlines.Add(new System.Windows.Documents.Run(track)
            { FontWeight = FontWeights.SemiBold, FontSize = 16 });
            StatusText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }

        // Animate the trailing dots (1→2→3→…) so there's motion while the lookup runs.
        _searchBase = I18n.T("searching").TrimEnd('.', '…', ' ');
        _searchDots = 1;
        _searchRun = new System.Windows.Documents.Run(_searchBase + ".");
        StatusText.Inlines.Add(_searchRun);
        StatusText.Visibility = Visibility.Visible;
        StartSearchDots();
    }

    private void StartSearchDots()
    {
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchTimer.Tick -= OnSearchDotsTick;
        _searchTimer.Tick += OnSearchDotsTick;
        _searchTimer.Start();
    }

    private void OnSearchDotsTick(object? sender, EventArgs e)
    {
        _searchDots = _searchDots % 3 + 1;       // 1, 2, 3, 1, …
        if (_searchRun is not null)
            _searchRun.Text = _searchBase + new string('.', _searchDots);
    }

    private void StopSearchDots()
    {
        _searchTimer?.Stop();
        _searchRun = null;
    }

    private void ShowPlain(string text)
    {
        StopSearchDots();
        _lines = null;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();
        LyricsViewport.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        PlainTextBlock.Text = text;
        PlainScroll.ScrollToVerticalOffset(0);
        PlainScroll.Visibility = Visibility.Visible;
    }

    private void ShowSynced(IReadOnlyList<LyricLine> lines)
    {
        StopSearchDots();
        StatusText.Visibility = Visibility.Collapsed;
        PlainScroll.Visibility = Visibility.Collapsed;
        LyricsViewport.Visibility = Visibility.Visible;

        _lines = lines;
        _lineBlocks.Clear();
        LyricsPanel.Children.Clear();

        foreach (var line in lines)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = _fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = _textBrush,
                Opacity = _inactiveOpacity,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = _align,
                LineHeight = _fontSize * 1.28,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 5, 0, 5),
            };
            ApplyFont(tb);
            _lineBlocks.Add(tb);
            LyricsPanel.Children.Add(tb);
        }

        _activeIndex = -1;
        _lastScrollTarget = double.NaN;
        _peeking = false;
        _peekTimer?.Stop();
        LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LyricsTranslate.Y = 0;

        // Pre-place on line 0 (after layout) so playback reaching it doesn't jump and the
        // first line is properly centred from the start.
        Dispatcher.BeginInvoke(new Action(() => { ScrollToIndex(0, animate: false); }),
            DispatcherPriority.Loaded);
    }

    private void OnInterpTick(object? sender, EventArgs e)
    {
        UpdateProgress();

        if (_lines is null || _lines.Count == 0 || LyricsViewport.Visibility != Visibility.Visible)
            return;

        double pos = _lastNp.PositionMs;
        if (_lastNp.IsPlaying)
            pos += (DateTime.UtcNow - _lastNpAt).TotalMilliseconds;
        pos -= _offsetMs;

        // The 250 ms tick only DETECTS when the active line changes (derived from the
        // interpolated playback time); the actual re-centring is event-driven.
        int active = FindActiveIndex(pos);
        if (active == _activeIndex)
            return;

        _activeIndex = active;
        UpdateHighlight();
        ScrollToIndex(Math.Max(active, 0), animate: true);   // always follow: the current line stays centred
    }

    // Fixed-pixel top/bottom fade (so it does NOT grow with a taller window). Pure
    // transparency on the lyrics — faithful to whatever glass/colour is behind, no band.
    // Tune this freely; it behaves as absolute pixels up to ~half the viewport height (above
    // that the top and bottom fade bands would meet, so it is clamped to h/2).
    private const double EdgeFadePx = 30;

    private void UpdateEdgeFade()
    {
        // Use the viewport's (Grid) height — a reliable, content-independent size.
        double h = LyricsViewport.ActualHeight;
        if (h <= 0)
            return;

        // In a short window (3-line default) a fixed 30 px fade would blur the previous/next line,
        // so cap the fade at ~12% of the viewport height.
        double fade = Math.Min(EdgeFadePx, h * 0.12);
        double top = fade;
        // When the progress/times row is shown, fade the lyrics out further at the BOTTOM — enough
        // to clear the footer's own height — so less text overlaps the bar/times and they read
        // cleanly.
        double bottom = fade;
        // (only the part of the footer the viewport still overlaps: none while the hover bar lifts the viewport)
        if (FooterBar.Visibility == Visibility.Visible)
            bottom += Math.Max(0, FooterBar.ActualHeight - LyricsViewport.Margin.Bottom);

        // Keep a sliver of fully-opaque lyrics between the two bands (clamp so they never overlap).
        top = Math.Min(top, h / 2);
        bottom = Math.Min(bottom, h - top - 1);

        // ABSOLUTE mapping pins the mask to the visible viewport box (0..h), NOT to the
        // scrolling StackPanel's content bounds — so only the top/bottom edges fade, the
        // same fixed amount regardless of where playback is in the song.
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, h),
            MappingMode = BrushMappingMode.Absolute,
        };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, top / h));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - bottom / h));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();

        // Apply the mask to the CANVAS that wraps the scrolling StackPanel (not the panel itself),
        // so the Absolute mapping above is anchored to the fixed viewport box and only the visible
        // top/bottom edges fade — never the song's start/end.
        LyricsCanvas.OpacityMask = mask;
    }

    /// <summary>Re-centre the current active line instantly (e.g. after a font change).</summary>
    private void RecenterInstant()
    {
        if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
            return;
        _lastScrollTarget = double.NaN; // force recompute
        ScrollToIndex(Math.Max(_activeIndex, 0), animate: false);
    }

    private bool _recenterQueued;

    /// <summary>Keep the active line centred DURING a resize: recenter instantly (no animation, so
    /// nothing trails) but deferred one dispatcher cycle to AFTER layout, so line heights have
    /// re-wrapped to the new width before we measure. Coalesced so we recenter at most once per
    /// frame.</summary>
    private void QueueRecenter()
    {
        if (_recenterQueued)
            return;
        _recenterQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _recenterQueued = false;
            if (_lines is null || LyricsViewport.Visibility != Visibility.Visible)
                return;
            _lastScrollTarget = double.NaN;
            ScrollToIndex(Math.Max(_activeIndex, 0), animate: false);
        }), DispatcherPriority.Render);
    }

    private int FindActiveIndex(double pos)
    {
        int idx = -1;
        for (int i = 0; i < _lines!.Count; i++)
        {
            if (_lines[i].TimeMs <= pos) idx = i;
            else break;
        }
        return idx;
    }

    private void UpdateHighlight()
    {
        for (int i = 0; i < _lineBlocks.Count; i++)
        {
            var tb = _lineBlocks[i];
            tb.Foreground = _textBrush;
            tb.Opacity = i == _activeIndex ? 1.0 : _inactiveOpacity;
            tb.Effect = (i == _activeIndex && _settings.TextOutline) ? TextShadow : null;
        }
    }

    /// <summary>TranslateTransform.Y that centres line <paramref name="idx"/> in the (already bar-shrunk) viewport.</summary>
    private double TargetFor(int idx)
    {
        // Compute the line's layout offset by summing real heights (transform-independent,
        // so the centring never drifts as the panel scrolls). Each block has 5px top+bottom
        // margin, so its slot is ActualHeight + 10 and its content starts 5px in.
        const double lineMargin = 10;
        double top = 5;
        for (int i = 0; i < idx; i++)
            top += _lineBlocks[i].ActualHeight + lineMargin;

        double center = top + _lineBlocks[idx].ActualHeight / 2;
        return Math.Round(LyricsViewport.ActualHeight / 2 - center); // pixel-snap
    }

    private void ScrollToIndex(int idx, bool animate, int durationMs = 400)
    {
        // While the user is peeking with the wheel the auto-centre must not overwrite their position.
        if (_peeking || idx < 0 || idx >= _lineBlocks.Count || LyricsViewport.ActualHeight <= 0)
            return;

        double target = TargetFor(idx);

        if (!double.IsNaN(_lastScrollTarget) && Math.Abs(target - _lastScrollTarget) < 1)
            return; // already there — avoid re-animating to the same spot every tick
        _lastScrollTarget = target;

        if (animate)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else
        {
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsTranslate.Y = target;
        }
    }
}

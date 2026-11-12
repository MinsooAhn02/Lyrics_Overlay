using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using LyricsOnTheGo.Models;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo.Controls;

public partial class SettingsPanel : UserControl
{
    private Settings? _settings;

    /// <summary>Raised when the user closes the panel (host hides the overlay).</summary>
    public event Action? CloseRequested;

    public SettingsPanel()
    {
        InitializeComponent();
        Picker.ColorChanged += OnPickerColor;
    }

    public void Initialize(Settings settings)
    {
        _settings = settings;
        DataContext = settings;
        settings.PropertyChanged += OnSettingsChanged;

        I18n.Changed += Localize;
        Unloaded += (_, _) => I18n.Changed -= Localize;

        UpdateSegments();
        UpdateOffsetLabel();
        Localize();
    }

    /// <summary>Pull every label/hint/button text in the current language (README §7).</summary>
    private void Localize()
    {
        TitleText.Text = I18n.T("settingsTitle");

        // Group titles are shown uppercased to match the original visual style.
        HdrAppearance.Text = I18n.T("grpAppearance").ToUpperInvariant();
        HdrSync.Text = I18n.T("grpSync").ToUpperInvariant();
        HdrVisibility.Text = I18n.T("grpVisibility").ToUpperInvariant();
        HdrBehavior.Text = I18n.T("grpBehavior").ToUpperInvariant();
        HdrCache.Text = I18n.T("grpCache").ToUpperInvariant();
        HdrBridge.Text = I18n.T("grpBridge").ToUpperInvariant();

        LblTextOutline.Text = I18n.T("textOutline");
        LblBridgeEnabled.Text = I18n.T("bridgeEnabled");
        LblBridgeToken.Text = I18n.T("bridgeToken");
        HintBridgeToken.Text = I18n.T("bridgeTokenHint");
        BridgeCopyLabel.Text = I18n.T("bridgeCopy");
        UpdateBridgeToken();

        LblTextColor.Text = I18n.T("textColor");
        LblBgColor.Text = I18n.T("bgColor");
        HdrText.Text = I18n.T("grpText").ToUpperInvariant();
        LblTextSize.Text = I18n.T("textSize");
        LblTitleSize.Text = I18n.T("titleSize");
        LblDim.Text = I18n.T("dimInactive");
        LblFont.Text = I18n.T("fontFamily");
        HintFont.Text = I18n.T("fontHint");
        if (_fontsBuilt) BuildFonts(); // list labels are localized
        LblAlignment.Text = I18n.T("alignment");
        AlignLeftBtn.ToolTip = I18n.T("alignLeft");
        AlignCenterBtn.ToolTip = I18n.T("alignCenter");

        LblOffset.Text = I18n.T("offset");
        HintOffset.Text = I18n.T("offsetHint");

        LblAutohide.Text = I18n.T("autohide");
        HintAutohide.Text = I18n.T("autohideHint");
        LblShowProgress.Text = I18n.T("showProgress");
        LblShowTimes.Text = I18n.T("showTimes");

        LblClickthrough.Text = I18n.T("clickthrough");
        HintClickthrough.Text = I18n.T("clickthroughHint");
        LblPlainFallback.Text = I18n.T("plainFallback");
        LblHideJapanese.Text = I18n.T("hideJapanese");
        HintHideJapanese.Text = I18n.T("hideJapaneseHint");

        HintCache.Text = I18n.T("cacheHint");
        ClearCacheLabel.Text = I18n.T("clearCache");
        ClearOffsetsLabel.Text = I18n.T("clearOffsets");

        HdrLocalDb.Text = I18n.T("grpLocalDb").ToUpperInvariant();
        HintLocalDb.Text = I18n.T("localDbHint");
        DbDumpsText.Text = I18n.T("localDbDownload");
        WarnLocalDb.Text = I18n.T("localDbWarn");
        SelectDbLabel.Text = I18n.T("selectDb");
        UnlinkDbLabel.Text = I18n.T("unlinkDb");
        UpdateLocalDbUi();

        ResetLabel.Text = I18n.T("reset");
    }

    /// <summary>Reflect the linked-database state: current path, status, and Unlink availability.</summary>
    private void UpdateLocalDbUi()
    {
        string? path = LocalDbConfig.DbPath;
        bool linked = LocalDbConfig.IsLinked;

        LocalDbPathText.Text = string.IsNullOrEmpty(path) ? I18n.T("localDbNone") : path;
        UnlinkDbBtn.IsEnabled = !string.IsNullOrEmpty(path);
        UnlinkDbBtn.Opacity = string.IsNullOrEmpty(path) ? 0.5 : 1.0;

        // The 117 GB size warning is only relevant before a database is linked.
        WarnLocalDb.Visibility = string.IsNullOrEmpty(path)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (string.IsNullOrEmpty(path))
            LocalDbStatus.Text = "";
        else if (linked)
            LocalDbStatus.Text = I18n.T("localDbLinked");
        else
            LocalDbStatus.Text = I18n.T("localDbMissing");
        LocalDbStatus.Visibility = string.IsNullOrEmpty(LocalDbStatus.Text)
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void OnSelectDb(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = I18n.T("selectDb"),
            Filter = "SQLite database (*.sqlite3;*.db;*.sqlite)|*.sqlite3;*.db;*.sqlite|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        if (!LocalDbConfig.IsValidDump(dialog.FileName))
        {
            LocalDbStatus.Text = I18n.T("localDbInvalid");
            LocalDbStatus.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        LocalDbConfig.SetPath(dialog.FileName);
        UpdateLocalDbUi();
    }

    private void OnUnlinkDb(object sender, System.Windows.RoutedEventArgs e)
    {
        LocalDbConfig.SetPath(null);
        UpdateLocalDbUi();
    }

    private void OnDbDumps(object sender, System.Windows.RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://lrclib.net/db-dumps") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    /// <summary>Reset transient status text each time the panel is opened.</summary>
    public void OnOpened()
    {
        CacheStatus.Text = "";
        CacheStatus.Visibility = System.Windows.Visibility.Collapsed;
        UpdateLocalDbUi();
        if (!_fontsBuilt) BuildFonts(); // lazy: first open only (enumerating ~200 fonts)
    }

    internal sealed record FontEntry(string Value, string Label, System.Windows.Media.FontFamily Family, bool IsHeader);

    /// <summary>Embedded Pretendard for a saved value of "Pretendard" / "Pretendard Medium" (case-insensitive,
    /// installed or not); null for anything else or on failure (caller falls back to the default font).
    /// WPF loads embedded font files lazily when text is first measured with the family, so building the
    /// FontFamily here costs nothing; do not pre-load. The folder Fonts\ holds Regular+Bold under family
    /// "Pretendard" (WPF picks Regular/Bold by FontWeight) and Medium as the separate family "Pretendard Medium".</summary>
    internal static System.Windows.Media.FontFamily? BundledFont(string? name)
    {
        string n = (name ?? "").Trim();
        if (!n.Equals("Pretendard", StringComparison.OrdinalIgnoreCase) &&
            !n.Equals("Pretendard Medium", StringComparison.OrdinalIgnoreCase)) return null;
        try { return new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Pretendard"); }
        catch (Exception ex)
        {
            DiagLog.LogCompleted("font", "SettingsPanel", DiagResult.Error, "bundled Pretendard failed: " + ex.Message, 0, false);
            return null;
        }
    }

    private bool _fontsBuilt, _syncingFont;
    private System.Collections.Generic.List<FontEntry> _fontItems = new();

    /// <summary>Default, then "Recommended" (installed only), then every installed font. Stored value
    /// is the en-US family name, usable directly as new FontFamily(value).</summary>
    private void BuildFonts()
    {
        var ui = System.Windows.Markup.XmlLanguage.GetLanguage(I18n.Lang == "ko" ? "ko-kr" : "en-us");
        var en = System.Windows.Markup.XmlLanguage.GetLanguage("en-us");
        var all = new System.Collections.Generic.Dictionary<string, FontEntry>(StringComparer.OrdinalIgnoreCase);
        var byName = new System.Collections.Generic.Dictionary<string, FontEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var ff in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            string value = ff.FamilyNames.TryGetValue(en, out var e) ? e : ff.Source;
            int hash = value.LastIndexOf('#');
            if (hash >= 0) value = value[(hash + 1)..];
            if (value.Length == 0 || all.ContainsKey(value)) continue;
            string label = ff.FamilyNames.TryGetValue(ui, out var l) ? l : value;
            var entry = new FontEntry(value, label, ff, false);
            all[value] = entry;
            foreach (var n in ff.FamilyNames.Values) byName.TryAdd(n, entry);
        }

        var rec = new System.Collections.Generic.List<FontEntry>();
        // Bundled Pretendard is always offered first; drop an installed duplicate from the lists below.
        var bundled = BundledFont("Pretendard");
        if (bundled is not null)
        {
            rec.Add(new FontEntry("Pretendard", "Pretendard" + I18n.T("fontBundledSuffix"), bundled, false));
            all.Remove("Pretendard");
        }
        foreach (var name in new[] { "Pretendard", "SUIT", "Noto Sans KR", "Malgun Gothic", "Segoe UI" })
            if (!(bundled is not null && name == "Pretendard") && byName.TryGetValue(name, out var m) && !rec.Contains(m)) rec.Add(m);

        var items = new System.Collections.Generic.List<FontEntry>
            { new("", I18n.T("fontDefault"), FontFamily, false) };
        if (rec.Count > 0)
        {
            items.Add(new("\u0001rec", I18n.T("fontRecommended"), FontFamily, true));
            items.AddRange(rec);
        }
        items.Add(new("\u0001all", I18n.T("fontAll"), FontFamily, true));
        items.AddRange(all.Values.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase));

        _fontItems = items;
        _syncingFont = true;
        try { FontCombo.ItemsSource = items; }
        finally { _syncingFont = false; }
        _fontsBuilt = true;
        SyncFontSelection();
    }

    /// <summary>Show the saved family, or "Default" if it is not installed. Never writes to Settings.</summary>
    private void SyncFontSelection()
    {
        if (!_fontsBuilt || _settings is null) return;
        string v = _settings.FontFamily ?? "";
        var match = _fontItems.FirstOrDefault(x => !x.IsHeader && string.Equals(x.Value, v, StringComparison.OrdinalIgnoreCase));
        _syncingFont = true;
        try { FontCombo.SelectedItem = match ?? _fontItems[0]; }
        finally { _syncingFont = false; }
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFont || _settings is null) return;
        if (FontCombo.SelectedItem is FontEntry { IsHeader: false } f) _settings.FontFamily = f.Value;
        else SyncFontSelection(); // header or cleared selection: restore, keep the saved value
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.Offset):
                UpdateOffsetLabel();
                break;
            case nameof(Settings.Align):
                UpdateSegments();
                break;
            case nameof(Settings.Lang):
                I18n.SetLanguage(_settings?.Lang);
                UpdateSegments();
                break;
            case nameof(Settings.BridgeToken):
                UpdateBridgeToken();
                break;
            case nameof(Settings.FontFamily):
                SyncFontSelection();
                break;
        }
    }

    /// <summary>The token is a secret: show only a mask; the Copy button is the only way to get the plaintext.</summary>
    private void UpdateBridgeToken()
    {
        bool has = !string.IsNullOrEmpty(_settings?.BridgeToken);
        BridgeTokenText.Text = has ? new string('•', 16) : I18n.T("bridgeTokenEmpty");
        BridgeCopyBtn.IsEnabled = has;
        BridgeCopyBtn.Opacity = has ? 1.0 : 0.5;
    }

    private void OnBridgeCopy(object sender, System.Windows.RoutedEventArgs e)
    {
        string? token = _settings?.BridgeToken;
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            System.Windows.Clipboard.SetText(token);
            BridgeCopyLabel.Text = I18n.T("bridgeCopied");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            BridgeCopyLabel.Text = I18n.T("bridgeCopyFailed"); // clipboard locked by another app
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { timer.Stop(); BridgeCopyLabel.Text = I18n.T("bridgeCopy"); };
        timer.Start();
    }

    private void UpdateOffsetLabel()
    {
        if (_settings is null) return;
        int ms = _settings.Offset;
        string sign = ms > 0 ? "+" : ms < 0 ? "−" : "";
        OffsetValue.Text = $"{sign}{Math.Abs(ms) / 1000.0:0.0}s";
    }

    private void UpdateSegments()
    {
        if (_settings is null) return;
        AlignLeftBtn.Tag = _settings.Align == "left" ? "selected" : null;
        AlignCenterBtn.Tag = _settings.Align == "center" ? "selected" : null;
        LangEnBtn.Tag = _settings.Lang != "ko" ? "selected" : null; // anything but "ko" (e.g. a stale "es") shows as English
        LangKoBtn.Tag = _settings.Lang == "ko" ? "selected" : null;
    }

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Drag the window from the settings header (ignoring the lang/close buttons); a
    /// double-click toggles borderless fullscreen, matching the main header.</summary>
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        for (System.Windows.DependencyObject? cur = e.OriginalSource as System.Windows.DependencyObject;
             cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase)
                return;
        }

        if (e.ClickCount == 2)
        {
            (System.Windows.Window.GetWindow(this) as MainWindow)?.ToggleFullscreen();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { System.Windows.Window.GetWindow(this)?.DragMove(); }
            catch { /* DragMove can throw if the button was already released */ }
        }
    }

    private void OnAlignLeft(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Align = "left";
    }

    private void OnAlignCenter(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Align = "center";
    }

    private string _colorTarget = "";

    private void OnPickTextColor(object sender, System.Windows.RoutedEventArgs e)
        => OpenColorPicker("text", _settings?.TextColor, TextSwatch);

    private void OnPickBgColor(object sender, System.Windows.RoutedEventArgs e)
        => OpenColorPicker("bg", _settings?.BgColor, BgSwatch);

    private void OpenColorPicker(string target, string? current, System.Windows.UIElement anchor)
    {
        _colorTarget = target;
        Picker.SetColor(current);
        ColorPopup.PlacementTarget = anchor;
        ColorPopup.IsOpen = true;
    }

    private void OnPickerColor(string hex)
    {
        if (_settings is null) return;
        if (_colorTarget == "text") _settings.TextColor = hex;
        else if (_colorTarget == "bg") _settings.BgColor = hex;
    }

    private void OnLangEn(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Lang = "en";
    }

    private void OnLangKo(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is not null) _settings.Lang = "ko";
    }

    /// <summary>Clicking anywhere on a slider jumps the thumb to that point and immediately starts
    /// dragging it (so a press-and-hold keeps tracking the cursor), not just a single jump.</summary>
    private void OnSliderTrackPress(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.Template.FindName("PART_Track", slider) is not System.Windows.Controls.Primitives.Track track
            || track.Thumb is null)
            return;

        // A press already on the thumb uses the default drag.
        if (track.Thumb.IsMouseOver)
            return;

        slider.Value = track.ValueFromPoint(e.GetPosition(track));

        // Force the track to re-arrange the thumb to the NEW value BEFORE handing it the gesture,
        // otherwise the thumb captures from its old position and the drag snaps back to it.
        slider.UpdateLayout();

        // Hand the gesture to the thumb so the same hold becomes a drag from the clicked point.
        track.Thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent,
            Source = track.Thumb,
        });
        e.Handled = true;
    }

    private void OnClearCache(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            int n = LyricsCache.Clear();
            CacheStatus.Text = I18n.T("cacheCleared").Replace("{n}", n.ToString());
        }
        catch
        {
            CacheStatus.Text = I18n.T("clearFailed");
        }
        CacheStatus.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnClearOffsets(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            int n = OffsetStore.Clear();
            CacheStatus.Text = I18n.T("offsetsCleared").Replace("{n}", n.ToString());
            // Reset the live slider so the current song reflects the cleared state.
            if (_settings is not null) _settings.Offset = 0;
        }
        catch
        {
            CacheStatus.Text = I18n.T("clearFailed");
        }
        CacheStatus.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnReset(object sender, System.Windows.RoutedEventArgs e)
    {
        _settings?.ResetToDefaults();
    }
}

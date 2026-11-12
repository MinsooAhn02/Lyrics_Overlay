using System;
using System.Linq;
using System.Threading;
using System.Windows;
using LyricsOnTheGo.Services;

namespace LyricsOnTheGo;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launched by a browser as its native-messaging host: answer on stdin/stdout, show nothing, exit.
        if (NativeHost.IsHostLaunch(e.Args)) { NativeHost.Run(); Shutdown(); return; }
        if (e.Args.Contains("--unregister-native-host")) { NativeHostRegistrar.Unregister(); Shutdown(); return; }

        // One overlay at a time (a second copy could not bind the bridge port anyway).
        _single = new Mutex(true, NativeHost.SingleInstanceMutex, out bool first);
        if (!first) { _single.Dispose(); _single = null; Shutdown(); return; }

        // The app runs only when the extension's "앱 켜기" starts it. A plain launch just (re)registers this exe so the
        // extension can; it opens the overlay only on that first registration (first run / exe moved).
        bool firstRegistration = NativeHostRegistrar.EnsureRegistered();
        if (!NativeHost.ConsumeLaunchTicket() && !firstRegistration)
        {
            MessageBox.Show("등록되어 있습니다. 앱은 브라우저 확장 팝업의 '앱 켜기'로 켜 주세요.", "Lyrics Overlay",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        new MainWindow().Show();

        // Leak testing only (tools\leak-check.ps1 sets it): every 30 s a FULL collection, then the managed heap that is
        // really retained goes to captions.log. Never active in normal use (the forced GC costs a few ms).
        if (Environment.GetEnvironmentVariable("LOTG_MEMLOG") == "1")
            _memLog = new Timer(_ => DiagLog.LogCompleted("bridge", "memory", DiagResult.Plain,
                $"memlog heapMB={GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:0.0} gen2={GC.CollectionCount(2)}", 0, false),
                null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private Timer? _memLog;

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { /* not owned */ }
        _single?.Dispose();
        base.OnExit(e);
    }
}

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

        NativeHostRegistrar.EnsureRegistered();   // lets the extension popup start this app
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { /* not owned */ }
        _single?.Dispose();
        base.OnExit(e);
    }
}

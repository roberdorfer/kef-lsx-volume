// ≙ Helper/main.swift.
//
// One program, two modes (docs/DESIGN.md §3.3, §10.9): without arguments the launcher (Launcher.cs, ≙ launchd KeepAlive;
// `--relaunched` after an update handover), `--agent` the tray app itself: start log line, discovery, controller,
// StatusMenu, then the message loop (≙ app.run()). The orderly end ExitApp (§10.9) is here too, reached by WM_CLOSE
// (e.g. `taskkill /PID <pid>` without /F), WM_ENDSESSION, the menu's Restart and the self-relaunch (§5.15). M4: the OSD
// (VolumeOsd.cs) and the hook renewal on unlock, console change and resume (A66). M5: the launcher and the start check
// for the minimum Windows version (A71). M6: the once-per-second installId check (update handover §10.4, uninstall §10.7).

using System.Globalization;
using System.Runtime.InteropServices;

static class Program
{
    /// Exit code of the agent when the session ends (WM_ENDSESSION): the launcher does not start it again (§10.9).
    /// ERROR_SHUTDOWN_IN_PROGRESS.
    public const int endSessionExitCode = 1115;
    /// A71 (≙ LSMinimumSystemVersion): Windows 11, build 22000. Older systems end without a dialog and without a
    /// restart (the launcher checks before it starts the agent). ERROR_OLD_WIN_VERSION.
    const int minimumBuild = 22000, oldWindowsExitCode = 1150;

    [STAThread]
    static int Main(string[] args)
    {
        if (Environment.OSVersion.Version.Build < minimumBuild) return oldWindowsExitCode;
        if (args is ["--agent"])
        {
            // An exception in a UI handler ends the process like a crash on macOS (no WinForms error dialog on the user's
            // desktop); the launcher starts the agent again.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            // Visual styles, GDI text rendering, PerMonitorV2 (ApplicationHighDpiMode in the csproj).
            ApplicationConfiguration.Initialize();
            var appDelegate = new AppDelegate();
            appDelegate.ApplicationDidFinishLaunching();
            Application.Run();   // message loop on the UI thread, no form (≙ app.run() with .accessory)
            return 0;
        }
        if (args.Length == 0) return Launcher.Run(relaunched: false);
        if (args is ["--relaunched"]) return Launcher.Run(relaunched: true);
        return 2;   // unknown arguments (the Run value and the launcher pass none or the two above)
    }
}

/// ≙ AppDelegate: the agent's start sequence (§5.18).
sealed class AppDelegate
{
    internal static AppDelegate? shared;
    public StatusMenu? statusMenu;
    public readonly Controller controller = new();
    internal AppWindow? window;
    internal int uiThreadId;

    public void ApplicationDidFinishLaunching()
    {
        shared = this;
        uiThreadId = Environment.CurrentManagedThreadId;
        Log($"{appName} {appVersion} starting; speaker={Config.SpeakerHostOverride ?? "auto (Bonjour)"} range={Config.MinSpeakerVolume.ToString(CultureInfo.InvariantCulture)}-{Config.MaxSpeakerVolume.ToString(CultureInfo.InvariantCulture)}");
        if (Config.SpeakerHostOverride == null) SpeakerDiscovery.shared.Start();
        controller.Start();
        window = new AppWindow(this);
        statusMenu = new StatusMenu(controller, window);
        // §6.5: the OSD window lives on the UI thread; the surface shows it for the own keys only.
        controller.SetOsd(new VolumeOsd());
        StartInstallIdCheck();
    }

    /// §10.4, §10.7 (M6): once per second compare `HKLM\…\installId` with its value at start. Changed: a new version was
    /// installed, hand over (the launcher starts the fixed path with `--relaunched`); gone: uninstalled, end (the launcher
    /// ends too). Both through ExitApp, so the tray icon is removed (no ghost icon). On the UI thread (a WinForms timer):
    /// a registry read per second, and ExitApp runs directly.
    void StartInstallIdCheck()
    {
        _ = TryReadInstallId(out string? atStart);
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            if (!TryReadInstallId(out string? now)) return;   // unreadable: unchanged
            switch (Launcher.InstallIdChange(atStart, now))
            {
                case Launcher.InstallChange.Updated:
                    timer.Stop();
                    Log("update installed: handing over to the new version");
                    ExitApp(0);
                    break;
                case Launcher.InstallChange.Uninstalled:
                    timer.Stop();
                    Log("uninstalled: exiting");
                    ExitApp(0);
                    break;
            }
        };
        timer.Start();
    }
}

/// Addition (Windows): the agent's hidden top-level window. It receives what Windows sends to applications and has no
/// macOS counterpart: WM_CLOSE (`taskkill` without /F, uninstall), WM_QUERYENDSESSION/WM_ENDSESSION (sign-out),
/// WM_SETTINGCHANGE (light/dark design, "ImmersiveColorSet"), DPI and display changes (icon size), and ExitApp requests
/// from other threads. Top-level, not message-only, and no tool window: broadcasts reach only top-level windows, and
/// taskkill (measured in M2) posted its WM_CLOSE to NotifyIcon's plain hidden window but not to a WS_EX_TOOLWINDOW one.
sealed partial class AppWindow : NativeWindow, IMessageFilter
{
    const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_SETTINGCHANGE = 0x001A;
    const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
    const int WM_POWERBROADCAST = 0x0218, PBT_APMRESUMEAUTOMATIC = 0x12;
    const int WM_WTSSESSION_CHANGE = 0x02B1, WTS_CONSOLE_CONNECT = 0x1, WTS_SESSION_UNLOCK = 0x8;
    internal const int WM_APP_EXITAPP = 0x8000 + 1;
    readonly AppDelegate app;

    public AppWindow(AppDelegate app)
    {
        this.app = app;
        CreateHandle(new CreateParams { Caption = appName });
        // A WM_CLOSE posted to another window of the UI thread (taskkill picked NotifyIcon's hidden window, whose default
        // handling would only destroy that window and take the tray icon away) ends the agent the same orderly way.
        Application.AddMessageFilter(this);
        // Addition (A66): unlock and console change renew the keyboard hook (resume comes as WM_POWERBROADCAST).
        _ = WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_CLOSE || m.HWnd == Handle) return false;
        Exit("WM_CLOSE");
        return true;
    }

    static void Exit(string what)
    {
        Log($"{what} received: exiting");
        ExitApp(0);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_CLOSE:
                Exit("WM_CLOSE");
                return;
            case WM_QUERYENDSESSION:
                m.Result = 1;
                return;
            case WM_ENDSESSION:
                if (m.WParam != IntPtr.Zero)
                {
                    Log("WM_ENDSESSION received: exiting");
                    ExitApp(Program.endSessionExitCode);
                }
                m.Result = IntPtr.Zero;
                return;
            case WM_SETTINGCHANGE:
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                app.statusMenu?.AppearanceChanged();
                break;
            case WM_APP_EXITAPP:
                ExitApp((int)m.WParam);
                return;
            case WM_WTSSESSION_CHANGE:
                if (m.WParam == WTS_SESSION_UNLOCK) Controller.ReinstallKeyHook("unlock");
                else if (m.WParam == WTS_CONSOLE_CONNECT) Controller.ReinstallKeyHook("console change");
                break;
            case WM_POWERBROADCAST:
                if (m.WParam == PBT_APMRESUMEAUTOMATIC) Controller.ReinstallKeyHook("resume");
                break;
        }
        base.WndProc(ref m);
    }

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);
}

static partial class Globals
{
    /// ExitApp (Config.cs): the orderly end of the agent (docs/DESIGN.md §10.9), the one routine for every orderly exit:
    /// Restart and the self-relaunch, the update handover (M6), uninstall, WM_CLOSE and WM_ENDSESSION. On the UI thread:
    /// remove the tray icon (NIM_DELETE; a bare Environment.Exit would leave a ghost icon), then Environment.Exit(code).
    /// From another thread (the controller) it is only posted to the UI thread, without waiting (§3.3).
    static partial void ExitAppCore(int code)
    {
        AppDelegate? app = AppDelegate.shared;
        if (app?.window is AppWindow w && Environment.CurrentManagedThreadId != app.uiThreadId)
        {
            _ = PostMessageW(w.Handle, AppWindow.WM_APP_EXITAPP, code, IntPtr.Zero);
            return;
        }
        app?.statusMenu?.RemoveIcon();
        Environment.Exit(code);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hWnd, int msg, nint wParam, IntPtr lParam);
}

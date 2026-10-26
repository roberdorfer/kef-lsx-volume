// ≙ the LaunchAgent's `KeepAlive true` + `ThrottleInterval 5` (scripts/install.sh; docs/DESIGN.md §10.9, A31, A60).
//
// Launcher mode (`LSXIIVolumeFixer.exe` without arguments, started by the HKLM Run value):
//  1. One launcher per session: the mutex `Local\io.github.roberdorfer.lsxiivolumefixer`; taken → end silently. With
//     `--relaunched` (update handover, §10.4) wait up to 10 s for the old launcher; an abandoned mutex counts as taken.
//  2. A job object with KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK: the agent ends with the launcher, its own children
//     (the editor of "Open log", the elevated admin script of "Apply these settings for all users…") do not.
//  3. Start `--agent`, assign it to the job, wait for its end; after every exit code start it again at the earliest 5 s
//     after its last start. Log `launcher: agent exited (<code>), relaunching in <s> s` for exit codes other than 0.
//  4. Stop conditions (Addition): exit code 1115 (the agent got WM_ENDSESSION); installId or the own exe gone
//     (uninstalled); installId changed (update: start the fixed path with `--relaunched` and end).
// Addition (A82): a hidden top-level window takes WM_CLOSE (`taskkill /PID <launcher>` without /F): the launcher asks the
// agent to end the orderly way (WM_CLOSE to its windows, ExitApp: no ghost icon), waits for it and ends without a restart;
// WM_ENDSESSION only stops the restarts (the agent ends with 1115 itself).

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static partial class Launcher
{
    static readonly Lock gate = new();
    static readonly ManualResetEvent stopRequested = new(false);
    static volatile bool sessionEnding;
    /// Held until the process ends (Environment.Exit); a later launcher then finds it abandoned, which counts as free.
    static Mutex? mutex;
    static LauncherWindow? window;

    public static int Run(bool relaunched)
    {
        mutex = new Mutex(false, mutexName);
        bool owned;
        try { owned = mutex.WaitOne(relaunched ? TimeSpan.FromSeconds(10) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        IntPtr job = CreateKillOnCloseJob();
        window = new LauncherWindow();
        var supervisor = new Thread(() => Supervise(job)) { Name = "launcher", IsBackground = true };
        supervisor.Start();
        Application.Run();   // the window's messages; the supervisor ends the process
        return 0;
    }

    /// The KeepAlive loop. Ends the process itself (Environment.Exit), so the window's message loop ends with it.
    static void Supervise(IntPtr job)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) Environment.Exit(1);
        _ = TryReadInstallId(out string? installId);
        while (true)
        {
            if (stopRequested.WaitOne(0)) Environment.Exit(0);
            Process agent;
            try
            {
                agent = Process.Start(new ProcessStartInfo(exe, "--agent") { UseShellExecute = false })!;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log($"launcher: starting the agent failed ({e.GetType().Name}: {e.Message})");
                Environment.Exit(1);
                return;
            }
            long started = Stopwatch.GetTimestamp();
            if (job != IntPtr.Zero) _ = AssignProcessToJobObject(job, agent.Handle);

            using (var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(agent.Handle, ownsHandle: false) })
            {
                if (WaitHandle.WaitAny([exited, stopRequested]) == 1)
                {
                    // WM_CLOSE to the launcher: end the agent the orderly way (its WM_CLOSE → ExitApp, tray icon removed).
                    // Repeated each second, for an agent that has no window yet.
                    do PostCloseToWindowsOf(agent.Id); while (!agent.WaitForExit(1000));
                    Environment.Exit(0);
                }
            }
            agent.WaitForExit();
            int code = agent.ExitCode;
            agent.Dispose();

            if (stopRequested.WaitOne(0) || sessionEnding || code == Program.endSessionExitCode) Environment.Exit(0);
            string? now = installId;   // unreadable registry: unchanged
            if (TryReadInstallId(out string? read)) now = read;
            if (now == null || !File.Exists(exe)) Environment.Exit(0);   // uninstalled
            if (InstallIdChange(installId, now) == InstallChange.Updated)
            {
                // Update (§10.4): the new exe at the fixed path takes over; it waits for this launcher's mutex. A launcher
                // from build\ (development) hands over to the installed exe as well, once there is one.
                string target = HandoverTarget(exe, File.Exists(installedExePath));
                try { Process.Start(new ProcessStartInfo(target, "--relaunched") { UseShellExecute = false })?.Dispose(); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                Environment.Exit(0);
            }
            TimeSpan delay = ThrottleDelay(Stopwatch.GetElapsedTime(started));
            if (ExitLogLine(code, delay) is string line) Log(line);
            if (stopRequested.WaitOne(delay)) Environment.Exit(0);
        }
    }

    // MARK: job object

    const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    /// The job handle stays open until the process ends; closing it (also when the launcher is killed) ends the agent.
    /// Zero when the job cannot be made: the agent then runs without it (it would outlive a killed launcher).
    static unsafe IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            _ = CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    // MARK: window (Addition, A82)

    const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

    /// Hidden top-level window (neither message-only nor a tool window, for the same reasons as the agent's AppWindow):
    /// WM_CLOSE from `taskkill` stops launcher and agent in order; WM_ENDSESSION stops the restarts.
    sealed class LauncherWindow : NativeWindow
    {
        public LauncherWindow() => CreateHandle(new CreateParams { Caption = appName });

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_CLOSE:
                    Log("launcher: WM_CLOSE received: ending the agent and exiting");
                    stopRequested.Set();
                    return;
                case WM_QUERYENDSESSION:
                    m.Result = 1;
                    return;
                case WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero) sessionEnding = true;
                    m.Result = IntPtr.Zero;
                    return;
            }
            base.WndProc(ref m);
        }
    }

    /// WM_CLOSE (as `taskkill` without /F sends it) to the top-level windows of `pid` titled `appName`: the agent's
    /// AppWindow (and any other window of its UI thread, through its message filter) turns it into ExitApp(0). Windows of
    /// other threads (GDI+, SystemEvents, IME) are left alone.
    static unsafe void PostCloseToWindowsOf(int pid)
    {
        var found = new List<IntPtr>();
        GCHandle h = GCHandle.Alloc(found);
        try { _ = EnumWindows(&CollectWindow, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        char* title = stackalloc char[64];
        foreach (IntPtr w in found)
        {
            _ = GetWindowThreadProcessId(w, out uint owner);
            if (owner != (uint)pid) continue;
            int n = GetWindowTextW(w, title, 64);
            if (new ReadOnlySpan<char>(title, n).SequenceEqual(appName)) _ = PostMessageW(w, WM_CLOSE, 0, IntPtr.Zero);
        }
    }

    [UnmanagedCallersOnly]
    static int CollectWindow(IntPtr hwnd, IntPtr state)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(IntPtr hJob, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(IntPtr hWnd, char* text, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hWnd, int msg, nint wParam, IntPtr lParam);
}

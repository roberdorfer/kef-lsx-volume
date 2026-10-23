// LSX II Volume Fixer — tray app that drives the KEF LSX II's network volume from the PC's volume keys.
// Windows port of Helper/Config.swift; the design is docs/DESIGN.md.
//
// Responsibilities (the macOS list with the Windows v1 mechanics, docs/DESIGN.md §5):
//  1. Map the PC's volume keys (keyboard hook, 50 steps) to the speaker's own volume over its local HTTP API
//     (player:volume, 0–100). v1 has no virtual audio device: the keys drive a virtual volume that plays the
//     role of the macOS proxy device (§6.7).
//  2. Mirror speaker volume changes (remote, app) back to that virtual volume.
//  3. Pin the real USB endpoint's volume at 0 dB so the USB path is not scaled.
//  4. Keep the LSX II as the default output (per Windows role); when the USB link drops and comes back,
//     restore it. A deliberate device change by the user while USB is unplugged cancels that.
//  5. Optional, off by default: the same restore for the default *input* device (`restoreInputUIDPrefix`),
//     for setups where the microphone shares the USB hub. Unrelated to the speaker itself; opt-in on purpose.
//  6. Pin the USB endpoint's shared-mode format to 96 kHz / 24-bit integer; any other shared format is
//     reverted. Players in exclusive mode keep their source rate (A15).
//  7. Multi-user: one instance per signed-in session (HKLM Run value + launcher). Only the instance in the
//     console session acts; others idle until they own the console.
//  8. Tray icon and menu (StatusMenu.cs) with live status, volume range sliders and the settings below; own
//     volume OSD (VolumeOsd.cs).
//
// Design decision: Volume is controlled from the PC's keys, and the audio on the USB cable is not scaled;
// remote/app-only control is not enough.
//
// Naming (docs/DESIGN.md §4): Swift names carried over mechanically; types, methods and (computed) properties
// start with a capital letter (`keyStepTable` → `KeyStepTable`, `dB` → `DB`), fields and constants keep
// their Swift names (`suiteName`, `userKeys`). Swift argument labels become parameter names, so calls read
// the same: `Config.KeyStepTable(min: 12, max: 65)`, `Config.SpeakerVolume(forScalar: s)`.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// MARK: - Identifiers and free functions (Swift: module-level globals of Config.swift)

/// Swift keeps these at module level. C# has no free functions; Directory.Build.props imports this class with
/// `global using static`, so `appName`, `Log(…)`, `IsConsoleSession()` and `L(…)` are used unqualified.
static partial class Globals
{
    public const string appName = "LSX II Volume Fixer";
    public const string appSlug = "LSXIIVolumeFixer";   // exe/log file name (no spaces)
    public const string appVersion = "1.0";
    // Design decision: Identifiers use the GitHub reverse-DNS form io.github.roberdorfer.*.
    public const string suiteName = "io.github.roberdorfer.lsxiivolumefixer";

    /// UID of the macOS proxy device. Reserved for the v2 driver (docs/DESIGN.md §13); v1 has no proxy device.
    public const string proxyUID = "io.github.roberdorfer.kefproxy.device";
    /// The real LSX II: first active render endpoint whose device UID (parent device instance ID) contains
    /// this, ignoring case (deviation Windows, A23; macOS: ModelUID contains "27AC:1060").
    public const string realModelMatch = "VID_27AC&PID_1060";

    // Addition (Windows): where the identifiers above show up on Windows (docs/DESIGN.md §8.1, §10.1, §10.9).
    /// Settings of the user, HKCU (≙ the user's defaults domain).
    public const string userKeyPath = @"Software\" + suiteName;
    /// Machine-wide settings, HKLM 64-bit view (≙ /Library/Preferences/<suiteName>.plist); also `installId`.
    public const string machineKeyPath = @"SOFTWARE\" + suiteName;
    /// Machine-wide state (`volume`, `muteResetBoot`; §5.10, §5.14), writable by users for values only.
    public const string stateKeyPath = machineKeyPath + @"\State";
    /// Autostart (≙ the global LaunchAgent): HKLM Run value named after the identifier.
    public const string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string runValueName = suiteName;
    /// One launcher per session (≙ one launchd job per gui/<uid> domain).
    public const string mutexName = @"Local\" + suiteName;
    /// The fixed path install.ps1 copies the exe to (§10.1, ≙ /Applications/LSXIIVolumeFixer.app); the update handover
    /// starts it (§10.4).
    public static readonly string installedExePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), appSlug, appSlug + ".exe");

    /// Reads `HKLM\SOFTWARE\<suiteName>\installId` (§10.4): true with the value (null when the key or the value is
    /// missing, i.e. not installed or uninstalled); false when the registry could not be read, which callers treat as
    /// "unchanged" so that a transient error never looks like an uninstall.
    public static bool TryReadInstallId(out string? installId)
    {
        installId = null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(machineKeyPath);
            installId = key?.GetValue("installId") as string;
            return true;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
    /// Event context of the app's own endpoint writes (volume pin, mute; docs/DESIGN.md §5.7.1, §6.4): a fixed UUID v5 of
    /// `suiteName` in the DNS namespace, so the callbacks can tell our writes from foreign ones.
    public static readonly Guid ownGuid = UuidV5(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), suiteName);

    /// RFC 4122 name-based UUID, version 5 (SHA-1).
    public static Guid UuidV5(Guid ns, string name)
    {
        byte[] n = ns.ToByteArray(bigEndian: true);
        byte[] s = Encoding.UTF8.GetBytes(name);
        byte[] h = System.Security.Cryptography.SHA1.HashData([.. n, .. s]);
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    // MARK: Log

    /// `%LOCALAPPDATA%\LSXIIVolumeFixer\LSXIIVolumeFixer.log`, per user, not roaming (deviation Windows, A39:
    /// there is no ~/Library/Logs; file name and scope stay). A path, the Swift name is kept.
    public static readonly string logURL = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appSlug, appSlug + ".log");

    /// ISO 8601 in UTC with milliseconds, e.g. 2026-10-17T09:41:07.123Z (≙ ISO8601DateFormatter with
    /// .withInternetDateTime and .withFractionalSeconds).
    internal const string iso8601Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// ≙ the lazy global `logHandle`: opened on the first Log() call only (an explicit static constructor
    /// makes the initialization exact, so merely touching the identifiers never creates the file).
    static class LogFile
    {
        internal static readonly SafeFileHandle? handle;
        internal static readonly Lock gate = new();
        static LogFile() { handle = Open(); }

        /// Launcher and agent of a session (during an update briefly two launchers) write to the same file.
        /// FILE_APPEND_DATA without FILE_WRITE_DATA makes Windows append every write at the current end of
        /// file; a FileStream in Append mode would write at its own position and overwrite the other writer.
        static SafeFileHandle? Open()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(logURL)!); } catch (Exception) { }
            var h = CreateFileW(logURL, FILE_APPEND_DATA | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                IntPtr.Zero, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return h;
        }
    }

    /// `<timestamp> <message>\n`, UTF-8 without BOM, LF; one WriteFile call per line under a lock. Without a
    /// log file the line goes to stderr (as on macOS, practically invisible for a background app).
    public static void Log(string s)
    {
        byte[] line = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString(iso8601Format, CultureInfo.InvariantCulture) + " " + s + "\n");
        lock (LogFile.gate)
        {
            if (LogFile.handle is { } h) { WriteLine(h, line); return; }
            try { using var e = Console.OpenStandardError(); e.Write(line); } catch (Exception) { }
        }
    }

    static unsafe void WriteLine(SafeFileHandle h, byte[] line)
    {
        fixed (byte* p = line) { WriteFile(h, p, (uint)line.Length, out _, IntPtr.Zero); }
    }

    // MARK: Console session

    // Design decision: The app must work for every Windows user on the machine exactly as for the installing
    // user. Implementation: HKLM Run value, one instance per session, speaker/default-device actions gated on
    // owning the console so background sessions stay passive.
    /// True when this session owns the console (fast user switching puts other sessions in the background).
    /// Polled every second by the controller (docs/DESIGN.md §5.14).
    public static bool IsConsoleSession()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint own)) return true;   // ≙ no session dictionary
        bool now = IsConsoleDecision(WTSGetActiveConsoleSessionId(), own, lastConsoleState);
        lastConsoleState = now;
        return now;
    }
    /// The answer of the previous IsConsoleSession() call; true before the first one, like macOS.
    static volatile bool lastConsoleState = true;
    /// WTSGetActiveConsoleSessionId() while the console is being attached to another session.
    public const uint noConsoleSession = 0xFFFFFFFF;
    /// Pure decision behind IsConsoleSession(): the console's session is ours; `0xFFFFFFFF` (console being
    /// reattached) keeps the last state (deviation Windows, A57).
    public static bool IsConsoleDecision(uint consoleSessionId, uint ownSessionId, bool last) =>
        consoleSessionId == noConsoleSession ? last : consoleSessionId == ownSessionId;

    // MARK: Orderly end (docs/DESIGN.md §10.9)

    /// The orderly end of the agent: remove the tray icon, then Environment.Exit(code). Implemented in main.cs; declared
    /// here so that the controller and the menu (which call it for the self-relaunch and Restart) also build into the
    /// tools that do not contain main.cs (the render harness, the tests), where it does nothing.
    public static void ExitApp(int code) => ExitAppCore(code);
    static partial void ExitAppCore(int code);

    // MARK: Format pin

    // Design decision: The output format stays fixed at 96 kHz / 24-bit and cannot be changed (sound control
    // panel, apps) — reason: the LSX II's DSP runs at 96 kHz with the speakers linked by cable, so 192 kHz only
    // added a second resampling step. On Windows this is the shared-mode format; exclusive mode stays allowed.
    // Physical format the real USB device is pinned to: 96 kHz / 24-bit integer.
    public const double pinnedRealRate = 96000;
    public const uint pinnedRealBits = 24;

    // MARK: Language

    /// German UI when the Windows display language is German, English otherwise (≙ first preferred language;
    /// evaluated once at start, docs/DESIGN.md §7.3).
    public static readonly bool isGerman = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    public static string L(string en, string de) => isGerman ? de : en;

    // MARK: Win32 (docs/DESIGN.md §4: P/Invoke declarations live in the file that uses them)

    const uint FILE_APPEND_DATA = 0x0004, SYNCHRONIZE = 0x0010_0000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    const uint OPEN_ALWAYS = 4, FILE_ATTRIBUTE_NORMAL = 0x80;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
}

// MARK: - Pure helpers of StatusMenu (docs/DESIGN.md §11.1: they live in Config.cs, so that ConfigTests still builds from
// Config.cs and SpeakerDiscovery.cs only, like the macOS Makefile's test binary)

partial class StatusMenu
{
    /// Stable part of a device UID (≙ `StatusMenu.uidPrefix(_:)`): the prefix stored in `restoreInputUIDPrefix` and
    /// the port-independent `defaultOutputSetFor` marker (§5.12). Deviation (Windows, A25, §5.13): the UID is the
    /// parent device's instance ID; for USB devices its last `\` segment hangs on the port (or is the serial number) and
    /// is cut off, `USB\VID_1234&PID_5678&MI_00\A&5E6F7A8B&0&0000` → `USB\VID_1234&PID_5678&MI_00`. Other IDs (HDAUDIO,
    /// Bluetooth, SWD) stay whole, as on macOS every UID but an Apple USB one. Device IDs ignore case, so does `USB\`.
    public static string UidPrefix(string uid)
    {
        if (!uid.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return uid;
        // ≙ `parts.count >= 3`: enumerator, device and instance segment; anything shorter stays whole.
        if (uid.Split('\\').Length < 3) return uid;
        return uid[..uid.LastIndexOf('\\')];
    }

    /// The admin script of "Apply these settings for all users…" (≙ the shell commands of `applyForAllUsers()`, §8.4):
    /// for every `settingKeys` entry its effective value (`value(key)`: the user's, else the machine-wide raw registry
    /// value, or null) is written to HKLM, unset ones are deleted there. Bools as REG_DWORD 0/1 (a value that is no
    /// REG_DWORD 0/1 counts as true, ≙ `v as? Bool ?? true`), REG_DWORD numbers as REG_DWORD, everything else as a string
    /// without quote characters (≙ removing `'`; also the typographic ones, which PowerShell reads as delimiters too), so
    /// `'…'` needs no further escaping. The key is created only when it is missing, never with `New-Item -Force` (that
    /// would delete it with `State` and `installId`); `State` and `installId` are never touched. Exit 0 or 1.
    internal static string AdminScriptForAllUsers(Func<string, object?> value)
    {
        var s = new StringBuilder();
        s.Append("# ").Append(appName).Append(": write these settings as the default for all users\r\n");
        s.Append("$ErrorActionPreference = 'Stop'\r\n");
        s.Append("try {\r\n");
        s.Append("    $k = 'HKLM:\\").Append(machineKeyPath).Append("'\r\n");
        s.Append("    if (-not (Test-Path -LiteralPath $k)) { New-Item -Path $k | Out-Null }\r\n");
        foreach (string k in Config.settingKeys)
        {
            object? v = value(k);
            if (v == null)
            {
                s.Append("    Remove-ItemProperty -LiteralPath $k -Name '").Append(k).Append("' -ErrorAction SilentlyContinue\r\n");
                continue;
            }
            string val, type;
            if (Config.boolKeys.Contains(k)) { val = (RegistryDefaults.AsBool(v) ?? true) ? "1" : "0"; type = "DWord"; }
            else if (v is int or long) { val = RegistryDefaults.IntegerValue(v).ToString(CultureInfo.InvariantCulture); type = "DWord"; }
            else { val = "'" + StripQuotes(v is string[] a ? string.Join("\n", a) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "") + "'"; type = "String"; }
            s.Append("    Set-ItemProperty -LiteralPath $k -Name '").Append(k).Append("' -Value ").Append(val).Append(" -Type ").Append(type).Append("\r\n");
        }
        s.Append("    exit 0\r\n");
        s.Append("} catch {\r\n");
        s.Append("    exit 1\r\n");
        s.Append("}\r\n");
        return s.ToString();
    }

    /// The characters PowerShell takes as single-quote string delimiters: ' and the typographic ‘ ’ ‚ ‛.
    static string StripQuotes(string v) => string.Concat(v.Where(c => c is not ('\'' or '‘' or '’' or '‚' or '‛')));
}

// MARK: - Pure helper of the launcher (docs/DESIGN.md §10.9, §11.1 test_launcherThrottle; the rest is in Launcher.cs)

static partial class Launcher
{
    /// ≙ launchd's `ThrottleInterval 5`: the agent is started again at the earliest this long after its last start.
    public static readonly TimeSpan throttleInterval = TimeSpan.FromSeconds(5);

    /// Wait before the next start after the agent ran for `sinceLastStart`: the rest of the throttle interval, never
    /// negative (after any exit code, ≙ `KeepAlive true`).
    public static TimeSpan ThrottleDelay(TimeSpan sinceLastStart) =>
        sinceLastStart >= throttleInterval ? TimeSpan.Zero
        : sinceLastStart <= TimeSpan.Zero ? throttleInterval
        : throttleInterval - sinceLastStart;

    /// The launcher's log line for an agent exit (§9), or null: only for exit codes other than 0 (Restart and the
    /// self-relaunch exit with 0 and are logged by the agent).
    public static string? ExitLogLine(int code, TimeSpan delay) =>
        code == 0 ? null
        : "launcher: agent exited (" + code.ToString(CultureInfo.InvariantCulture) + "), relaunching in "
          + delay.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// What a change of `installId` means (§10.4, §10.7), for the agent's once-per-second check and the launcher.
    public enum InstallChange { None, Updated, Uninstalled }

    /// `atStart` is the value read when the process started, `now` the current one (null = missing). Missing now but
    /// present at start: uninstalled. Any other difference (also missing → present, an install over a development run):
    /// a new installation, hand over to it. Missing at start and now (a run from build\ without the setup): nothing.
    public static InstallChange InstallIdChange(string? atStart, string? now) =>
        string.Equals(atStart, now, StringComparison.Ordinal) ? InstallChange.None
        : now == null ? InstallChange.Uninstalled
        : InstallChange.Updated;

    /// The exe the update handover starts (§10.4 step 2): the fixed install path when it exists, else the launcher's
    /// own exe (a development run from build\ whose installId was renewed by scripts\dev-setup.ps1).
    public static string HandoverTarget(string ownExe, bool installedExists) => installedExists ? installedExePath : ownExe;
}

// MARK: - Config

/// The three keys the keyboard hook takes (docs/DESIGN.md §6.2): VK_VOLUME_UP, VK_VOLUME_DOWN, VK_VOLUME_MUTE.
enum VolumeKey { Up, Down, Mute }

static partial class Config
{
    /// ≙ `UserDefaults(suiteName:)`: HKCU over HKLM over the built-in default, read live on every access.
    public static readonly RegistryDefaults defaults = new();
    /// Keys a user can set; also the set copied by "apply for all users".
    /// (As on macOS this comment is not what the code does: "apply for all users" copies `settingKeys`, §8.2.)
    public static readonly string[] userKeys = ["speakerHost", "minSpeakerVolume", "maxSpeakerVolume", "autoSwitchDefault", "autoWakeOnPlayback", "restoreInputUIDPrefix", "showVolumeInMenuBar", "defaultOutputSetFor"];
    public static readonly string[] settingKeys = ["speakerHost", "minSpeakerVolume", "maxSpeakerVolume", "autoSwitchDefault", "autoWakeOnPlayback", "restoreInputUIDPrefix", "showVolumeInMenuBar"];
    /// The Bool-typed keys among `settingKeys` (written as REG_DWORD by "apply for all users").
    public static readonly string[] boolKeys = ["autoSwitchDefault", "autoWakeOnPlayback", "showVolumeInMenuBar"];
    /// Optional fixed speaker host name or IP (`reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer
    /// /v speakerHost /d <host> /f`). Empty/unset: the speaker is found via DNS-SD (`SpeakerDiscovery`),
    /// which is the normal case.
    public static string? SpeakerHostOverride
    {
        get
        {
            var v = (defaults.String(forKey: "speakerHost") ?? "").Trim();
            return v.Length == 0 ? null : v;
        }
    }
    // `SpeakerHost` (override, else the discovered host) lives in SpeakerDiscovery.cs so the pure-function
    // tests can build Config.cs alone.

    // Design decision: PC 100 % maps to speaker 65 (−17.5 dB), not 100: the upper part of the old 12–100
    // mapping was louder than useful, which wasted most key steps. Only the ceiling moves. Changed from the
    // tray menu slider or `reg add … /v maxSpeakerVolume /t REG_DWORD /d N /f`.
    public static int MaxSpeakerVolume => MaxSpeakerVolumeFor(defaults.Integer(forKey: "maxSpeakerVolume"));
    // Design decision: The first key step is the minimum — reason: the quietest key step should be very
    // quiet; it is reached from above (see `ApproachFromAbove`). PC 0 % stays speaker 0 + mute; speaker
    // values below the minimum are not reachable from the keys.
    // Deviation (user, A74): default 15, macOS 12.
    // Deviation (Bugfix, EDGE-01, A29): without a stored minimum the default is clamped below the maximum,
    // min(15, max − 1); macOS crashes when maxSpeakerVolume is 1…11 and minSpeakerVolume is unset.
    public static int MinSpeakerVolume => MinSpeakerVolumeFor(defaults.Integer(forKey: "minSpeakerVolume"), max: MaxSpeakerVolume);

    public const int defaultMaxSpeakerVolume = 65;
    public const int defaultMinSpeakerVolume = 15;
    /// The rules of `MaxSpeakerVolume` / `MinSpeakerVolume` for a stored value `v` (0 = unset or unreadable).
    public static int MaxSpeakerVolumeFor(int v) => v > 0 ? Math.Min(v, 100) : defaultMaxSpeakerVolume;
    public static int MinSpeakerVolumeFor(int v, int max) => v > 0 ? Math.Min(v, max - 1) : Math.Min(defaultMinSpeakerVolume, max - 1);

    /// Number of volume key steps. Deviation (user, A73): 50 like the Windows volume keys (2 % steps,
    /// 50 positions above 0); macOS 16. No finer steps (A7).
    public const int keySteps = 50;
    /// One volume key step on the 0–1 scalar scale (macOS: 1/16).
    public const float keyStep = 1f / keySteps;
    // Design decision: "Minimum" is the speaker value of the FIRST audible key step (1/N), not of PC 0 % —
    // reason: with an origin-at-0 % mapping the lowest key step would not land on the chosen minimum.
    // PC 0 % stays speaker 0 + mute.
    // Design decision: The N key steps are spread EQUALLY in measured dB between Minimum and Maximum: every
    // key press makes the speaker louder by the same number of dB (reason: each step should mean the same
    // difference in dB, and the step size should follow only from the two range values). The dB values come
    // from the MEASURED from-above curve of the speaker (`DB(_)`), not from KEF's
    // `settings:/mediaPlayer/volumeMap` (that table is wrong for the USB path: it claims 2 dB per unit below 20
    // and 0.5 dB per unit above; measured: 1 dB per unit from 8 to 60, 0.5 dB per unit 60–65).
    // Implementation detail (not part of the decision): each step target is rounded to the nearest speaker
    // value by measured dB (ties: the smaller value).
    // Deviation (user, A75; docs/DESIGN.md §5.2): stricter rounding than macOS ("never below the previous
    // step"), so that no key press is without effect:
    //  - max − min ≥ steps − 1: only values strictly above the previous step and at most hi − (steps − 1 − i)
    //    (so every later step can still go one value higher);
    //  - narrower ranges: only the previous step or the value above it (at most hi), never below
    //    hi − (steps − 1 − i). Equal neighbours are unavoidable then, but exactly (steps − 1) − (max − min) of
    //    them, and no value is skipped.
    // Where the macOS rule gives no equal neighbours both rules give the same table, so the 16-step macOS
    // tables are unchanged (12–65: 12 15 19 22 25 29 32 36 39 42 46 49 52 56 59 65).
    // With 15–65 and 50 steps (47.5 dB / 49 = 0.97 dB per step): 15 16 17 … 62 63 65 — 1 dB per key from 15
    // to 60, 0.5 dB for 60 → 61 → 62 → 63, 1 dB for 63 → 65. Each key press stays within 1 dB of the ideal
    // step, but a step can drift from its ideal dB position: every press below 60 moves at least 1 dB against
    // the ideal 0.97 dB, so step 45 (= 60) sits 1.4 dB above it (the 16-step test's 0.5-dB bound does not hold).
    /// Target speaker value of each of the `steps` key steps (index 0 = first step = minimum, steps − 1 =
    /// maximum). The range defaults to the configured one; tests pass their own (and 16 steps for the macOS tests).
    public static int[] KeyStepTable(int? min = null, int? max = null, int steps = keySteps)
    {
        int lo = min ?? MinSpeakerVolume, hi = max ?? MaxSpeakerVolume;
        double stepDB = (DB(hi) - DB(lo)) / (steps - 1);
        var table = new List<int>(steps) { lo };
        int prev = lo;
        bool wide = hi - lo >= steps - 1;
        for (int i = 1; i < steps - 1; i++)
        {
            // The Swift expression, evaluated as written (no fused multiply-add): exact dB ties between two
            // candidates are common (e.g. 15–61), and a target rounded differently would pick the other one.
            double target = DB(lo) + stepDB * i;
            int floor = hi - (steps - 1 - i);   // so many steps still follow, each needs one more value
            // Never empty for lo ≤ hi: wide, prev ≤ floor − 1; narrow, floor − 1 ≤ prev ≤ hi.
            int first = wide ? prev + 1 : Math.Max(prev, floor);
            int last = wide ? floor : Math.Min(prev + 1, hi);
            // Nearest speaker value by measured dB among the candidates; ties keep the smaller one.
            int best = first;
            double bestErr = double.PositiveInfinity;
            for (int v = first; v <= last; v++)
            {
                double e = Math.Abs(DB(v) - target);
                if (e < bestErr) { bestErr = e; best = v; }
            }
            table.Add(best);
            prev = best;
        }
        table.Add(hi);
        return table.ToArray();
    }

    // The two mappings are ported literally (docs/DESIGN.md §5.1, §5.4), generalized from 15 to N − 1 intervals,
    // with the exact Float32/Double mix of the Swift expressions. The explicit (float) casts force Float32
    // rounding of every Float32 operation (C# would allow more intermediate precision); `.rounded()` is
    // schoolbook rounding, MidpointRounding.AwayFromZero, not .NET's default banker's rounding.

    /// Speaker value for the virtual volume `s` (0…1): 0 → speaker 0 (+ mute); k/N → table value k − 1;
    /// linear between the table values.
    public static int SpeakerVolume(float forScalar, int? min = null, int? max = null, int steps = keySteps)
    {
        float s = forScalar;
        if (s <= 0.0005f) return 0;
        var t = KeyStepTable(min: min, max: max, steps: steps);
        float keyStep = 1f / steps;
        int n = steps - 1;
        double pos = SwiftMax(0, SwiftMin(n, (double)(float)((float)(s - keyStep) / (float)(1f - keyStep)) * n));
        int i = Math.Min(n - 1, (int)pos);
        double f = pos - i;
        return (int)Math.Round(t[i] + f * (t[i + 1] - t[i]), MidpointRounding.AwayFromZero);
    }
    /// Virtual volume for speaker value `v`: 0 → 0; at or below the first step → 1/N; at or above the last
    /// step → 1; in between the segment and its linear fraction. Values outside the range show at the nearest
    /// end of the scale; the speaker keeps its value.
    public static float Scalar(int forSpeakerVolume, int? min = null, int? max = null, int steps = keySteps)
    {
        int v = forSpeakerVolume;
        if (v <= 0) return 0;
        var t = KeyStepTable(min: min, max: max, steps: steps);
        float keyStep = 1f / steps;
        int n = steps - 1;
        if (v <= t[0]) return keyStep;
        if (v >= t[n]) return 1;
        int i = 0;
        while (i < n - 1 && t[i + 1] < v) i += 1;
        double f = t[i + 1] == t[i] ? 0.0 : (double)(v - t[i]) / (t[i + 1] - t[i]);
        return (float)(keyStep + (float)((float)((i + f) / n) * (float)(1f - keyStep)));
    }
    // Swift's min/max: `min` returns y only when y < x, `max` returns y when y >= x, so a NaN never wins
    // (Math.Min/Math.Max would propagate it).
    static double SwiftMin(double x, double y) => y < x ? y : x;
    static double SwiftMax(double x, double y) => y >= x ? y : x;

    public static bool AutoSwitchDefault => RegistryDefaults.AsBool(defaults.Object(forKey: "autoSwitchDefault")) ?? true;
    // Design decision: Also restore the default input device after a USB re-enumeration (useful when a USB
    // mic sits on the same hub and Windows falls back to another mic) — kept as a generic, opt-in setting;
    // reason: it has nothing to do with the speaker itself.
    // `reg add HKCU\Software\io.github.roberdorfer.lsxiivolumefixer /v restoreInputUIDPrefix /d "USB\VID_…&PID_…&MI_…" /f`
    // or via the menu. Not trimmed (as on macOS).
    public static string? RestoreInputUIDPrefix
    {
        get
        {
            var v = defaults.String(forKey: "restoreInputUIDPrefix") ?? "";
            return v.Length == 0 ? null : v;
        }
    }
    // Design decision: Wake the speaker from standby and select USB automatically when the PC starts playing
    // — reason: the manual "wake" item should not be needed in the common case. Only from standby (never away
    // from TV/Bluetooth/…), and only after sustained playback (`AutoWakeDelay`) so short system sounds do not
    // wake the speaker.
    public static bool AutoWakeOnPlayback => RegistryDefaults.AsBool(defaults.Object(forKey: "autoWakeOnPlayback")) ?? true;
    public static double AutoWakeDelay => 2.0;
    public static bool ShowVolumeInMenuBar => RegistryDefaults.AsBool(defaults.Object(forKey: "showVolumeInMenuBar")) ?? true;

    /// Stores `value` for the user (HKCU); null deletes it.
    public static void Set(object? value, string forKey)
    {
        if (value != null) defaults.Set(value, forKey: forKey); else defaults.RemoveObject(forKey: forKey);
    }

    // MARK: Measured USB output curve (docs\usb-volume-curve.md)
    //
    // Measured 2026-10-12 at the listening position with five simultaneous tones (150 Hz – 7.3 kHz; all five
    // give the same steps within 0.3 dB). The speaker has TWO curves for `player:volume` on the USB path and a
    // simple rule for switching between them, confirmed on 230 measured steps:
    //  - reached from ABOVE: 1.0 dB per unit from 8 to 60, 0.5 dB per unit from 60 to 65 (`DB(_)`);
    //  - reached from BELOW: identical above ≈40, steeper below (2 dB per unit around 13–16), and flat below
    //    13 (`DBFromBelow(_)`);
    //  - raising the value never makes the output quieter than it is, lowering it never louder; so the
    //    output after a change is max(current, fromBelow(v)) when raising and min(current, fromAbove(v)) when
    //    lowering. Dwell time does not matter (0.1 s behaves like 4 s).
    // Hence the same value is up to 10 dB louder from above than from below (12: 10 dB, 16: 5 dB, 20: 2.7 dB,
    // 28: 0.9 dB, ≥40: 0), and 12→17 from below changes the output by under 1 dB.
    // The curve is a property of the speaker, measured with USB host volume 0 dB; Windows v1 keeps the endpoint
    // at 0 dB too, so it applies unchanged (docs/DESIGN.md §5.2).
    //
    // Design decision: A key step must give the same loudness regardless of the direction it is reached from
    // — reason: going up, steps 1 and 2 were indistinguishable, and the same value was up to 10 dB louder from
    // above. Every value is reached from above (brief overshoot, see `ApproachFromAbove(_)`), so only the
    // from-above curve counts and it is the one the key steps are placed on.

    /// Output level in dB relative to speaker 65 when the value is reached from above (the curve the keys
    /// use). Below 8 extrapolated (not measured, not reachable with the defaults).
    public static double DB(int v)
    {
        if (v <= 0) return -120;
        if (v >= 60) return -2.5 + 0.5 * (v - 60);
        return v - 62.5;
    }
    /// Output level in dB relative to speaker 65 when the value is reached from below (1 kHz, 1-unit steps
    /// from 8 upwards). Same scale as `DB(_)`; equal to it from 41 up.
    public static readonly IReadOnlyDictionary<int, double> fromBelowCurve = new Dictionary<int, double>
    {
        [12] = -60.7, [13] = -57.7, [14] = -55.4, [15] = -53.3, [16] = -51.4, [17] = -49.8,
        [18] = -48.2, [19] = -46.6, [20] = -45.1, [21] = -43.7, [22] = -42.3, [23] = -41.1, [24] = -39.8, [25] = -38.6, [26] = -37.6, [27] = -36.4,
        [28] = -35.3, [29] = -34.2, [30] = -33.0, [31] = -32.0, [32] = -31.0, [33] = -30.0, [34] = -28.9, [35] = -27.9, [36] = -26.8, [37] = -25.7,
        [38] = -24.6, [39] = -23.6, [40] = -22.7,
    };
    public static double DBFromBelow(int v)
    {
        if (v <= 12) return fromBelowCurve[12];      // flat: raising within 8–12 changes nothing
        return fromBelowCurve.TryGetValue(v, out double d) ? d : DB(v);
    }
    /// Value to set for a moment before `v` so that `v` is reached from above whatever the speaker's current
    /// state: the smallest overshoot whose from-below level is at least the from-above level of `v`
    /// (0.2 dB margin). 12 → 17, 15 → 19, 17 → 20, 23 → 25, 28–40 → +1, 41 and up → none. Capped at `cap`
    /// (the controller passes maxSpeakerVolume).
    public static int? ApproachFromAbove(int v, int cap = 100)
    {
        if (!(v > 0)) return null;
        if (DBFromBelow(v) >= DB(v)) return null;          // both curves agree (41 and up)
        double target = DB(v) + 0.2;
        for (int k = 1; k <= 8; k++)
        {
            if (!(v + k <= cap)) continue;
            if (DBFromBelow(v + k) >= target) return v + k;
        }
        return null;
    }
    /// Pause between the overshoot and the target write; 0.1 s was measured to behave like 4 s.
    public static double ApproachHold => 0.1;

    // MARK: Volume keys (deviation v1, docs/DESIGN.md §5.1, §6.2)

    /// One volume key on the virtual volume `s` and the endpoint mute (§5.1): up/down move by 1/steps (also with
    /// modifiers held, A7) after lifting the mute; down to s ≤ 0.0005 is 0 + mute (PC 0 % = speaker 0 + mute); the mute
    /// key toggles the mute only. Pure, so the tests can check the 50-key run (KeyHookVolumeSurface applies it).
    public static (float scalar, bool muted) ApplyVolumeKey(VolumeKey key, float s, bool muted, int steps = keySteps)
    {
        if (key == VolumeKey.Mute) return (s, !muted);
        float d = 1f / steps;
        float n = Math.Clamp(key == VolumeKey.Up ? s + d : s - d, 0f, 1f);
        if (key == VolumeKey.Down && n <= 0.0005f) return (0, true);
        return (n, false);
    }

    /// The stored virtual volume `HKLM\…\State\volume` (§5.14): REG_SZ, invariant culture, clamped to 0…1; missing or
    /// unreadable → 0.5, the driver's default.
    public static float StoredVolume(object? value) =>
        value is string str && double.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d)
            ? (float)Math.Clamp(d, 0, 1) : 0.5f;
    /// The text written to `State\volume`: the Float32 in shortest round-trip form, invariant (§9).
    public static string StoredVolumeText(float s) => s.ToString("R", CultureInfo.InvariantCulture);

    /// Hidden setting; REG_SZ (invariant number) or REG_DWORD; > 0, else 1.0.
    public static double PollInterval { get { double v = defaults.Double(forKey: "pollInterval"); return v > 0 ? v : 1.0; } }
    // A speaker volume jump of at least this many units between two polls, without a PC-side change, is treated
    // as a speaker-side reset (e.g. 15 -> 53 after a USB re-enumeration) and the PC's value is pushed back
    // instead of adopted. Remote presses move 1 unit per step.
    public static int ResetJumpThreshold { get { int v = defaults.Integer(forKey: "resetJumpThreshold"); return v > 0 ? v : 25; } }
}

// MARK: - Default output and Plan B (docs/DESIGN.md §5.6, §5.11, §5.12, A84, A85; pure, tested by test_defaultOutputPlanB)

static partial class Config
{
    /// HKCU state value (REG_DWORD bit mask; bit k = render role k: 1 console, 2 multimedia, 4 communications): the
    /// return of the LSX II as default output is pending for that role (`restoreOnReturn`, §5.12). Addition (Windows, A84):
    /// macOS keeps it in memory only; on Windows the LSX II leaves USB whenever it idles, so a sign-in without it is the
    /// normal case and the flag has to survive the restart.
    public const string restorePendingKey = "restoreOutputOnReturn";

    /// The per-role pending flags at start (A84): the stored mask when there is one. Without one, a fresh install (no
    /// `defaultOutputSetFor` marker) with the LSX II absent counts as pending for every role, so that the first run can
    /// still bring it in (Plan B); with the LSX II present the first-run step does that. Otherwise nothing is pending: a
    /// deliberate choice of another device is never overridden at start.
    public static bool[] RestorePendingAtStart(object? stored, string? defaultOutputSetFor, bool realPresent, int roles = 3)
    {
        var pending = new bool[roles];
        if (stored != null)
        {
            int mask = RegistryDefaults.IntegerValue(stored);
            for (int k = 0; k < roles; k++) pending[k] = ((mask >> k) & 1) != 0;
        }
        else if (string.IsNullOrEmpty(defaultOutputSetFor) && !realPresent)
        {
            for (int k = 0; k < roles; k++) pending[k] = true;
        }
        return pending;
    }

    /// The stored form of the per-role pending flags.
    public static int RestorePendingMask(IReadOnlyList<bool> pending)
    {
        int mask = 0;
        for (int k = 0; k < pending.Count; k++) if (pending[k]) mask |= 1 << k;
        return mask;
    }

    /// The branches of `checkDefaultOutput()` for one role (§5.12); (a) is decided before and never reaches this.
    public enum DefaultOutputBranch
    {
        /// (b) the default is the target.
        IsTarget,
        /// (c) the default left the target while the USB device is absent: restore on return (logged).
        LeftWhileAbsent,
        /// Deviation (Windows, A84): a pending restore survives an evaluation that shows no new choice, i.e. the first
        /// one after start, or one whose default is still the substitute seen when the restore became pending (settings
        /// change, console regained). macOS falls through to (d) there; it never meets this case at start, because its
        /// proxy is always present and stays the default.
        StillPending,
        /// (d) any other default is the user's choice; a pending restore is dropped.
        UserChoice,
    }

    public readonly record struct DefaultOutputResult(DefaultOutputBranch Branch, bool IsDefault, bool RestoreOnReturn,
                                                      bool SubstituteKnown, string? Substitute);

    /// One step of the per-role default-output state machine (§5.12, A84). `def`: the role's current default (null: none);
    /// `target`: the surface's output; `substituteKnown`/`substitute`: the default seen when the restore became pending
    /// (unknown after start).
    public static DefaultOutputResult DefaultOutputStep(string? def, string? target, bool realPresent, bool isDefault,
                                                        bool restoreOnReturn, bool substituteKnown, string? substitute)
    {
        if (def != null && def == target)
            return new(DefaultOutputBranch.IsTarget, true, false, false, null);
        if (isDefault && !realPresent)
            return new(DefaultOutputBranch.LeftWhileAbsent, false, true, true, def);
        if (restoreOnReturn && !realPresent && (!substituteKnown || def == substitute))
            return new(DefaultOutputBranch.StillPending, false, true, true, def);
        return new(DefaultOutputBranch.UserChoice, false, false, false, null);
    }

    /// The restore of one role when the LSX II comes back (§5.12): set it as default when pending, unless it already
    /// is. Without IPolicyConfig the app cannot set it; the flag stays until Windows' own restore (b) or another
    /// evaluation (d) settles it.
    public static (bool set, bool pending) RestoreOnReturn(bool pending, bool policyConfigAvailable, bool alreadyDefault) =>
        !pending ? (false, false)
        : !policyConfigAvailable ? (false, true)
        : alreadyDefault ? (false, false)
        : (true, false);

    /// Plan B (§5.11, A84): playback on the substitute default output counts as "running" while the LSX II is gone from
    /// USB and its return is pending for some role.
    public static bool PlanBActive(bool realPresent, bool autoSwitchDefault, IReadOnlyList<bool> restoreOnReturn) =>
        !realPresent && autoSwitchDefault && restoreOnReturn.Any(x => x);

    /// After selecting `usb` again (Plan B), the LSX II should be back on USB within this time; otherwise the attempt
    /// counts as failed (logged once per loss episode).
    public static readonly TimeSpan usbReselectVerdict = TimeSpan.FromSeconds(10);
    /// Wait after a failed attempt before the next one (Addition, A84).
    public static readonly TimeSpan usbReselectBackoff = TimeSpan.FromSeconds(60);

    /// What the auto-wake does after 2 s of playback (§5.11).
    public enum WakeAction { None, WakeFromStandby, ReselectUsb }

    /// The auto-wake decision for the speaker's source `source` (§5.11): `standby` wakes, as on macOS. Deviation
    /// (Windows, A84): `usb` while the LSX II is gone from USB selects `usb` again, once per loss episode
    /// (`lastReselect` is cleared when the endpoint returns); when the endpoint did not return, the next attempt comes
    /// no earlier than `usbReselectVerdict` + `usbReselectBackoff` after the last one. Every other source is left alone.
    public static WakeAction AutoWakeAction(string? source, bool realPresent, DateTime? lastReselect, DateTime now)
    {
        if (source == "standby") return WakeAction.WakeFromStandby;
        if (source != "usb" || realPresent) return WakeAction.None;
        if (lastReselect is DateTime t && now - t < usbReselectVerdict + usbReselectBackoff) return WakeAction.None;
        return WakeAction.ReselectUsb;
    }

    /// Deviation (Windows, A85, §5.6): when the LSX II endpoint reappears, the PC's value is pushed only if the speaker
    /// does not already have it (`speakerVolume` from a GET right before; null: unknown, push). The LSX II re-enumerates
    /// after every idle period on Windows, and a push from "unknown" always overshoots first (approach from above).
    public static bool ReappearPushNeeded(int? speakerVolume, int target) => speakerVolume is not int v || v != target;
}

// MARK: - Machine-wide state (docs/DESIGN.md §5.10, §5.14)

/// `HKLM\SOFTWARE\<suiteName>\State`: values only. Users may set values there (KEY_SET_VALUE, given by the installer or
/// the development setup), nothing else, so the key is opened with exactly QueryValues | SetValue; OpenSubKey(path,
/// true), CreateSubKey and Registry.SetValue would ask for KEY_WRITE and fail for users (§5.14).
static class MachineState
{
    public static object? Get(string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(stateKeyPath);
            return key?.GetValue(name);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// Writes a REG_SZ value; false when the key is missing or not writable.
    public static bool Set(string name, string value) => SetWithError(name, value) == 0;

    /// Like Set, with the error for the log: 0, the exception's HRESULT, or 0x80070002 (ERROR_FILE_NOT_FOUND) when the
    /// key is missing.
    public static int SetWithError(string name, string value)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(stateKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                System.Security.AccessControl.RegistryRights.QueryValues | System.Security.AccessControl.RegistryRights.SetValue);
            if (key == null) return unchecked((int)0x80070002);
            key.SetValue(name, value, RegistryValueKind.String);
            return 0;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return e.HResult != 0 ? e.HResult : unchecked((int)0x80070005); }
    }

    /// Start time of the running system (now − uptime; the uptime counts standby, §5.10).
    public static DateTime BootTime() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// Two start times belong to the same system start when they are at most 120 s apart (W32Time moves "now" but not
    /// the uptime, §5.10, A70).
    public static bool SameBoot(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) <= 120;
}

// MARK: - Settings storage

/// ≙ `UserDefaults(suiteName: suiteName)` (storage adapted, docs/DESIGN.md §8.1): the user's values in
/// HKCU\Software\<suiteName> win over the machine-wide ones in HKLM\SOFTWARE\<suiteName> (64-bit view), which
/// win over the built-in default. Read live on every access, no cache.
sealed class RegistryDefaults
{
    static readonly (RegistryHive hive, string path)[] searchList =
        [(RegistryHive.CurrentUser, userKeyPath), (RegistryHive.LocalMachine, machineKeyPath)];

    /// The raw registry value of the first domain that has the key (int for REG_DWORD, long for REG_QWORD,
    /// string for REG_SZ/REG_EXPAND_SZ, …), or null.
    public object? Object(string forKey)
    {
        foreach (var (hive, path) in searchList)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path);
                if (key?.GetValue(forKey) is { } v) return v;
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }
    public string? String(string forKey) => StringValue(Object(forKey));
    public int Integer(string forKey) => IntegerValue(Object(forKey));
    public double Double(string forKey) => DoubleValue(Object(forKey));
    public DateTime? Date(string forKey) => DateValue(Object(forKey));

    /// Writes the user's value: bool → REG_DWORD 0/1, int → REG_DWORD, string → REG_SZ, double/float → REG_SZ
    /// (invariant), DateTime → REG_SZ (ISO 8601 UTC). Failures are silent, like `UserDefaults.set`.
    public void Set(object value, string forKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.CreateSubKey(userKeyPath, writable: true);
            switch (value)
            {
                case bool b: key.SetValue(forKey, b ? 1 : 0, RegistryValueKind.DWord); break;
                case int i: key.SetValue(forKey, i, RegistryValueKind.DWord); break;
                case string s: key.SetValue(forKey, s, RegistryValueKind.String); break;
                case double d: key.SetValue(forKey, d.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String); break;
                case float f: key.SetValue(forKey, f.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String); break;
                case DateTime t: key.SetValue(forKey, DateString(t), RegistryValueKind.String); break;
                default: throw new ArgumentException($"unsupported settings value type {value.GetType()}", nameof(value));
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
    }
    public void RemoveObject(string forKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(userKeyPath, writable: true);
            key?.DeleteValue(forKey, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    // MARK: Reading rules (docs/DESIGN.md §8.2; pure, tested by test_settingsParsing)

    /// ≙ `object(forKey:) as? Bool`: only REG_DWORD 0/1 are bools; REG_SZ "true"/"1" is not (R13: 1:1).
    public static bool? AsBool(object? raw) => raw is int i && (i == 0 || i == 1) ? i == 1 : null;

    /// ≙ `integer(forKey:)`: REG_DWORD as is, REG_QWORD saturated, REG_SZ by its leading integer
    /// (like NSString.integerValue: " 60 " → 60, "12.7" → 12); missing or unreadable → 0, i.e. the default.
    public static int IntegerValue(object? raw) => raw switch
    {
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        string s when leadingInteger.Match(s) is { Success: true } m => ParseSaturated(m.Groups[1].Value),
        _ => 0,
    };
    /// ≙ `double(forKey:)`: REG_DWORD/REG_QWORD as numbers, REG_SZ by its leading number, always parsed with
    /// the invariant culture ("1.5" is 1.5 under a German culture too; "1,5" is 1); otherwise 0.
    public static double DoubleValue(object? raw) => raw switch
    {
        int i => i,
        long l => l,
        string s when leadingDouble.Match(s) is { Success: true } m
            && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
        _ => 0,
    };
    /// ≙ `string(forKey:)`: strings as they are, numbers as their (invariant) text, everything else null.
    public static string? StringValue(object? raw) => raw switch
    {
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
    /// Dates (`lastSelfRelaunch`) are stored as REG_SZ in ISO 8601 UTC.
    public static DateTime? DateValue(object? raw) =>
        raw is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    public static string DateString(DateTime d) => d.ToUniversalTime().ToString(iso8601Format, CultureInfo.InvariantCulture);

    static readonly Regex leadingInteger = new(@"^\s*([+-]?[0-9]+)", RegexOptions.CultureInvariant);
    static readonly Regex leadingDouble = new(@"^\s*([+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)", RegexOptions.CultureInvariant);
    static int ParseSaturated(string digits) =>
        long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)
            ? (int)Math.Clamp(l, int.MinValue, int.MaxValue)
            : digits.StartsWith('-') ? int.MinValue : int.MaxValue;
}

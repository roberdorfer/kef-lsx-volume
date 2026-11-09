// ≙ Helper/Controller.swift.
//
// M2 (docs/DESIGN.md §16): the controller reads Core Audio and the speaker.
//  - Core Audio (CoreAudioSupport.cs): one IMMNotificationClient replaces the three system listeners and only enqueues;
//    Rescan finds the LSX II endpoint (`real device: <id|none>`), the proxy role (`proxy device: keyhook`,
//    VolumeSurface.cs) and the configured input (`input device: <id|none>`); CheckDefaultOutput/CheckDefaultInput keep
//    the per-role state machines of §5.12/§5.13; the snapshot carries every field of macOS's Status; the audio sessions
//    of the LSX II endpoint give the running state (§5.11).
//  - Speaker: poll (1 s, then pollInterval) and source (3 s, then 15 s), GET only, as in M1.
// M3 writes to Core Audio (console only, §5.14): the volume pin (§5.7) with the pin timer (2 s, then 10 s) and the re-pin
// of foreign changes after 300 ms of quiet (§6.4 rule A), the format pin with its listener (§5.8), the default-output
// state machine per role with firstRunDefaultOutput and the restore on return (§5.12), the optional input restore
// (§5.13) and the mute reset once per system start (§5.10).
// M4: the proxy role's volume and the send path. The keyboard hook (KeyHook.cs, §6.2) feeds the surface's keys
// (VolumeSurface.cs); proxyVolumeChanged → scheduleSend (0.03 s, last value wins) → flushSend with approach-from-above
// and the 0.1-s hold (§5.3); applyToProxy mirrors speaker values (§5.4), pushProxyToSpeaker and the reset-jump rule (§5.5)
// as on macOS. interceptActive is set on every default-device, device and session event (§6.2). The menu's "Turn speaker off
// and on (USB)" calls PowerCycleSpeaker.
// M5: the auto-wake wakes a speaker in standby after 2 s of playback (§5.11), and speakerPersistentlyUnreachable ends the
// agent for the launcher to restart (§5.15). Console gating as on macOS (§5.14): rescan, checkDefault*, keys, auto-wake,
// poll, pin and source timers, format and endpoint-volume callbacks, the mute reset and the escalation act only on the
// console; running sends, menu actions and the snapshot do not depend on it.
// Build with SEND_TRACE (used for measurement O20) for `send trace:` lines with the timer delays (O20); the app has none.

using System.Collections.Concurrent;

// MARK: - Controller

sealed partial class Controller
{
    public readonly SerialQueue queue = new(suiteName);
    public readonly Speaker speaker = new();

    // CTRL-02 (docs/DESIGN.md §5.18; internal, like Swift's default access).
    internal IVolumeSurface? proxy = null;              // the surface; in v1 always set after the first rescan
    internal string? real = null;                       // endpoint ID of the LSX II
    /// Windows reported the `real` endpoint no longer active. Its ID survives a USB re-enumeration, unlike macOS's
    /// AudioObjectID, so the next rescan on the console takes the endpoint for a new instance even when it is back
    /// before that rescan runs (§5.6).
    internal bool realInstanceGone = false;
    internal IVolumeSurface? listenersInstalledFor = null;   // surface whose events rescan subscribed (§5.4)
    internal int? lastSentSpeaker = null;               // last value we pushed to the speaker (written by the send path, M4)
    internal int? lastKnownSpeaker = null;              // last value read from the speaker
    internal float? expectedProxyScalar = null;         // unused in v1 (no echo), kept for v2 (§6.7)
    /// Per render role (eConsole, eMultimedia, eCommunications): default output == the surface's output; the default
    /// left it only because the USB device vanished (§5.12). `proxyIsDefault` is isDefault[eConsole].
    internal readonly bool[] isDefault = new bool[3], restoreOnReturn = new bool[3];
    /// Per render role: the default seen when the restore became pending (A84); unknown after start.
    internal readonly bool[] substituteKnown = new bool[3];
    internal readonly string?[] substituteDefault = new string?[3];
    /// `restoreOutputOnReturn` as last read from or written to HKCU (null: none stored); written only on change (A84).
    int? storedRestoreMask = null;
    internal string? input = null;                      // configured input device, if present
    internal string? realFormatListenerFor = null;      // endpoint with format observation (§5.8)
    internal bool onConsole = true;
    /// Per capture role, like isDefault/restoreOnReturn (§5.13); the menu shows eConsole.
    internal readonly bool[] inputIsDefault = new bool[3], restoreInputOnReturn = new bool[3];
    internal CancellationTokenSource? pendingSend = null;   // ≙ DispatchWorkItem: cancelled by the next scheduleSend
    internal bool sendInFlight = false;
    internal int? queuedValue = null;
    internal DateTime? lastSpeakerContact = null;       // last successful speaker API answer
    internal DateTime? consoleGainedAt = null;          // when this session last became (or started as) the console
    internal string? speakerSource = null;

    internal System.Threading.Timer? pollTimer, pinTimer, sourceTimer;

    /// v1: the surface that plays the proxy role (§6.7), always present.
    readonly KeyHookVolumeSurface surface;
    /// v1: the real USB output, the LSX II endpoint with the pins (§5.7, §5.8).
    readonly UsbRealOutput realOutput;
    /// IPolicyConfig could be created (checked at start, §5.8, §5.12); without it the default-device writes and the
    /// format pin are off and the format is only shown.
    bool policyConfigAvailable = true;
    /// Rule A (§6.4): the latest foreign level change; the re-pin runs when none followed for 300 ms (A70).
    int foreignChangeGeneration;
    static readonly TimeSpan repinQuiet = TimeSpan.FromMilliseconds(300);
    /// ≙ the three system listeners; kept alive while registered.
    readonly EndpointNotifications notifications;

    static readonly ERole[] roles = [ERole.eConsole, ERole.eMultimedia, ERole.eCommunications];
    /// Role suffix of the Windows log lines (§5.12, §9).
    static string RoleName(ERole role) => role switch
    {
        ERole.eConsole => "console",
        ERole.eMultimedia => "multimedia",
        _ => "communications",
    };

    public Controller()
    {
        realOutput = new UsbRealOutput(queue);
        realOutput.Changed += RealVolumeChangedExternally;
        surface = new KeyHookVolumeSurface(queue, () => real ?? FindReal(), realOutput);
        notifications = new EndpointNotifications(
            devicesChanged: (id, gone) => queue.Async(() =>
            {
                if (gone && real != null && string.Equals(id, real, StringComparison.OrdinalIgnoreCase)) realInstanceGone = true;
                Rescan();
                UpdateInterceptActive();
            }),
            defaultChanged: (flow, role) =>
            {
                if (flow == EDataFlow.eRender) queue.Async(() => { CheckDefaultOutput(role); UpdateInterceptActive(); });
                else if (flow == EDataFlow.eCapture) queue.Async(() => CheckDefaultInput(role));
            },
            propertyChanged: (id, key) =>
            {
                // ≙ the format and rate listeners of installRealFormatListener (§5.8): realFormatListenerFor is set with
                // the first format pin of an endpoint.
                if (key == PKEY_AudioEngine_DeviceFormat) queue.Async(() => { if (onConsole && id == realFormatListenerFor) PinRealFormat(); });
            });
    }

    /// Read-only view for the tray UI (≙ Controller.Status); taken on the controller queue.
    public readonly record struct Status(
        int? SpeakerVolume,
        bool SpeakerReachable,
        string? SpeakerSource,
        bool ProxyPresent,
        bool RealPresent,
        bool ProxyIsDefault,
        bool InputPresent,
        bool InputIsDefault,
        string? RealFormat,
        bool OnConsole);

    public Status Snapshot() => queue.Sync(() => new Status(
        SpeakerVolume: lastKnownSpeaker,
        SpeakerReachable: lastSpeakerContact is DateTime c && (DateTime.UtcNow - c).TotalSeconds < 3 * Config.PollInterval + 2,
        SpeakerSource: speakerSource,
        ProxyPresent: proxy != null,
        RealPresent: real != null,
        ProxyIsDefault: isDefault[(int)ERole.eConsole],
        InputPresent: input != null,
        InputIsDefault: inputIsDefault[(int)ERole.eConsole],
        // A58: the shared-mode device format, "<Int(rate/1000)> kHz / <bits>-bit".
        RealFormat: real is string r ? DeviceFormatText(r) : null,
        OnConsole: onConsole));

    /// The active capture endpoints for the input submenu (≙ StatusMenu.inputDevices(), §5.13): display name and
    /// DeviceUid. Taken on the controller queue, which owns the Core Audio objects.
    public (string name, string uid)[] InputDevices() =>
        queue.Sync(() => AllDevices(EDataFlow.eCapture).Select(id => (DeviceName(id), DeviceUid(id))).ToArray());

    /// Called by the UI after a setting changed: re-evaluate devices and re-apply the PC value with the new range.
    public void SettingsChanged(bool pushVolume)
    {
        queue.Async(() =>
        {
            Rescan(); CheckDefaultOutput(); CheckDefaultInput();
            if (pushVolume) PushProxyToSpeaker("settings changed");
        });
    }

    /// ≙ setSpeakerSource(_:): POST, log, read back after 2 s. Called by PowerCycleSpeaker and by the auto-wake
    /// (§5.11); not bound to the console.
    public void SetSpeakerSource(string src)
    {
        speaker.SetSource(src, ok => queue.Async(() =>
        {
            Log($"speaker source -> {src}: {(ok ? "sent" : "request failed")}");
            if (ok) speakerSource = src;
            // The speaker echoes any value; read back what it actually did.
            queue.AsyncAfter(TimeSpan.FromSeconds(2), () =>
                speaker.GetSource(s => queue.Async(() => { speakerSource = s; Log($"speaker source now: {s ?? "unknown"}"); })));
        }));
    }

    // Design decision: The menu item turns the speaker off and on again like the remote (standby, then USB),
    // on Windows as on macOS — reason: selecting USB alone did nothing when the speaker was stuck; only off/on helped.
    /// ≙ powerCycleSpeaker(): standby, then USB after `Config.PowerCycleOffTime`. The speaker re-enumerates on USB
    /// afterwards and the usual re-enumeration handling restores output device, volume, format and mute.
    public void PowerCycleSpeaker()
    {
        Log("power-cycling speaker: standby, then USB");
        speaker.SetSource("standby", ok => queue.Async(() =>
        {
            Log($"speaker source -> standby: {(ok ? "sent" : "request failed")}");
            if (ok) speakerSource = "standby";
            queue.AsyncAfter(TimeSpan.FromSeconds(Config.PowerCycleOffTime), () => SetSpeakerSource("usb"));
        }));
    }

    public void Start()
    {
        // ≙ the driver reading its host storage at start (`initialize volume=…`, §5.14).
        surface.LoadStoredVolume(initial: true);
        // ≙ the three AudioObjectAddPropertyListenerBlock calls: one IMMNotificationClient on the controller thread, which
        // owns the Core Audio objects; its callbacks only enqueue (§3.3). Then the first evaluation, queued before the
        // console status is read (the benign race CTRL-05, as on macOS).
        queue.Async(() => { try { RegisterEndpointNotifications(notifications); } catch (Exception) { } });
        // Addition (Windows, §5.8, §5.12, §9): the undocumented IPolicyConfig is checked once at start.
        queue.Async(() =>
        {
            if (PolicyConfig(out int err) == null)
            {
                policyConfigAvailable = false;
                Log($"IPolicyConfig unavailable ({ErrText(err)}): default output and format pins disabled");
            }
        });
        queue.Async(() => { LoadRestorePending(); Rescan(); CheckDefaultOutput(); CheckDefaultInput(); UpdateInterceptActive(); });
        onConsole = IsConsoleSession();
        if (onConsole) consoleGainedAt = DateTime.UtcNow;
        Log($"console session: {(onConsole ? "true" : "false")}");
        // §6.2: the hook thread only enqueues; the key is handled on the controller queue, and only on the console
        // (§5.14). interceptActive is still false here; the first evaluation above sets it.
        KeyHook.Start(key => queue.Async(() => { if (onConsole) surface.HandleKey(key); }));
        speaker.onPersistentFailure = () => queue.Async(SpeakerPersistentlyUnreachable);
        // A change of pollInterval needs a restart, as on macOS.
        pollTimer = queue.Timer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(Config.PollInterval), () =>
        {
            RefreshConsole();
            if (onConsole) PollSpeaker();
            if (real == null) surface.AttachReal(PlanBTarget());   // Plan B follows the substitute default (A84)
            surface.ScanSessions();   // §5.11: the session scan as a safety net for the session events
        });
        pinTimer = queue.Timer(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), () =>
        {
            if (onConsole) { PinRealVolume(); PinRealFormat(); }
        });
        sourceTimer = queue.Timer(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), () =>
        {
            if (onConsole) speaker.GetSource(s => queue.Async(() => speakerSource = s));
        });
    }

    /// ≙ findProxy(): v1 has no proxy device; the surface is always there (§4).
    IVolumeSurface? FindProxy() => surface;

    /// The OSD (§6.5), created by AppDelegate on the UI thread.
    public void SetOsd(VolumeOsd osd) => surface.osd = osd;

    /// Addition (A66): unlock, console change and resume renew the keyboard hook (AppWindow, main.cs).
    public static void ReinstallKeyHook(string reason) => KeyHook.Reinstall(reason);

    /// §6.2: interceptActive = onConsole && LSX II endpoint present && LSX II default for eConsole or eMultimedia, read
    /// live with GetDefaultAudioEndpoint (independent of autoSwitchDefault and of isDefault[]). Set on every
    /// default-device, device and session event; otherwise the volume keys go to Windows.
    void UpdateInterceptActive()
    {
        bool active = false;
        if (onConsole && real is string r)
        {
            foreach (ERole role in new[] { ERole.eConsole, ERole.eMultimedia })
            {
                var (ok, def) = DefaultDevice(EDataFlow.eRender, role);
                if (ok && string.Equals(def, r, StringComparison.OrdinalIgnoreCase)) active = true;
            }
        }
        KeyHook.InterceptActive = active;
    }

    void Rescan()
    {
        if (!onConsole) return;
        IVolumeSurface? p = FindProxy();
        string? r = FindReal();
        if (p != proxy) Log($"proxy device: {p?.Kind ?? "none"}");
        // ≙ `r != real` on macOS, where a re-enumerated device has a new AudioObjectID: another endpoint, or the same ID
        // after Windows reported it gone (realInstanceGone, §5.6).
        bool realChanged = r != real || (realInstanceGone && r != null);
        realInstanceGone = false;
        bool realReappeared = realChanged && r != null;
        // A84: the LSX II is on USB again; the next loss episode may re-select `usb` once more.
        if (r != null) { usbReselectAt = null; usbReselectBackoffLogged = false; }
        // A new endpoint instance gets a new IAudioEndpointVolume with callback (§5.6); no-op while the endpoint stays
        // the same and is attached, a failed activation is retried.
        realOutput.Attach(r, newInstance: realChanged);
        if (realChanged)
        {
            Log($"real device: {r ?? "none"}");
            if (r != null) { PinRealVolume(r); PinRealFormat(r); InstallRealFormatListener(r); }
        }
        proxy = p;
        real = r;
        // v1: the LSX II endpoint's sessions are the surface's "running" (§6.7); a new endpoint instance gets a new
        // session manager (§5.6). No-op while the endpoint stays the same and is attached. While the LSX II is gone and
        // its return is pending, Plan B watches the substitute default (§5.11, A84; the pending flags are loaded at start).
        surface.AttachReal(r ?? PlanBTarget(), newInstance: realChanged);
        // §5.10: the endpoint mute is lifted once per system start, as soon as the LSX II is there.
        if (r != null) surface.ResetMuteOncePerBoot();
        // The speaker may reset its own volume when the USB link re-enumerates (wake, source switch).
        // The PC's value is the user's last intent: push it, never pull a reset value into the PC.
        // Deviation (Windows, A85): only when the speaker does not already have it (GET first).
        if (realReappeared && listenersInstalledFor == p && p != null) PushProxyToSpeakerIfDifferent("real device reappeared");
        if (r != null && p != null) FirstRunDefaultOutput();
        string? i = FindInput();
        bool inputReappeared = i != input && i != null;
        if (i != input) Log($"input device: {i ?? "none"}");
        input = i;
        if (inputReappeared && i != null && policyConfigAvailable)
        {
            // §5.13, per capture role: only the roles that were on the device when it vanished.
            foreach (ERole role in roles)
            {
                int k = (int)role;
                if (!restoreInputOnReturn[k]) continue;
                restoreInputOnReturn[k] = false;
                var (_, def) = DefaultDevice(EDataFlow.eCapture, role);
                if (def != i)
                {
                    int err = SetDefaultEndpoint(i, role);
                    Log($"input device back; restored it as default input (was {def ?? "none"}, {ErrText(err)}) [{RoleName(role)}]");
                    inputIsDefault[k] = err == 0;
                }
            }
        }
        if (realReappeared && Config.AutoSwitchDefault && p?.OutputEndpointId is string target && policyConfigAvailable)
        {
            // §5.12, per render role: only the roles that were on the LSX II when it vanished.
            foreach (ERole role in roles)
            {
                int k = (int)role;
                if (!restoreOnReturn[k]) continue;
                var (_, def) = DefaultDevice(EDataFlow.eRender, role);
                var (set, pending) = Config.RestoreOnReturn(restoreOnReturn[k], policyConfigAvailable, alreadyDefault: def == target);
                restoreOnReturn[k] = pending;
                if (!pending) substituteKnown[k] = false;
                if (set)
                {
                    int err = SetDefaultEndpoint(target, role);
                    Log($"real device back; restored {p.TargetName} as default output (was {def ?? "none"}, {ErrText(err)}) [{RoleName(role)}]");
                    isDefault[k] = err == 0;
                }
            }
            PersistRestorePending();
        }
        if (p != null && listenersInstalledFor != p)
        {
            p.RunningChanged += () => queue.Async(ProxyRunningChanged);
            p.PresenceChanged += () => queue.Async(Rescan);
            // ≙ the proxy's volume listener; v1 adds the mute key, which also sends (§5.1).
            p.UserScalarChanged += () => queue.Async(ProxyVolumeChanged);
            p.UserMuteChanged += () => queue.Async(ProxyVolumeChanged);
            listenersInstalledFor = p;
            // Initial sync: speaker -> proxy (the one pull, §5.4).
            speaker.GetVolume(v => queue.Async(() =>
            {
                if (v is int value) { lastKnownSpeaker = value; ApplyToProxy(value); }
            }));
        }
    }

    // MARK: auto-wake

    CancellationTokenSource? pendingWake;

    bool ProxyIsRunning() => proxy?.IsRunningSomewhere ?? false;

    /// A84: when `usb` was last selected again while the LSX II was gone from USB (null: not in this loss episode), and
    /// whether the failed attempt was logged.
    DateTime? usbReselectAt;
    bool usbReselectBackoffLogged;

    /// Plan B (§5.11, A84): while the LSX II is gone from USB and its return as default output is pending, playback on
    /// the substitute default output counts as "running", so the wake can fire (on macOS the proxy stays the default).
    string? PlanBTarget()
    {
        if (!Config.PlanBActive(realPresent: real != null, Config.AutoSwitchDefault, restoreOnReturn)) return null;
        var (ok, def) = DefaultDevice(EDataFlow.eRender, ERole.eMultimedia);
        return ok ? def : null;
    }

    /// IO on the surface started or stopped (§5.11). After `autoWakeDelay` of continuous playback, wake the speaker if
    /// (and only if) it is in standby; other active sources are left alone.
    void ProxyRunningChanged()
    {
        pendingWake?.Cancel();
        pendingWake = null;
        if (!onConsole || !Config.AutoWakeOnPlayback || !ProxyIsRunning()) return;
        var w = new CancellationTokenSource();
        pendingWake = w;
        queue.AsyncAfter(TimeSpan.FromSeconds(Config.AutoWakeDelay), () =>
        {
            if (w.IsCancellationRequested) return;
            if (!ProxyIsRunning()) return;
            speaker.GetSource(s => queue.Async(() =>
            {
                speakerSource = s;
                switch (Config.AutoWakeAction(s, realPresent: FindReal() != null, usbReselectAt, DateTime.UtcNow))
                {
                    case Config.WakeAction.WakeFromStandby:
                        Log("playback started while speaker in standby: waking it on USB");
                        SetSpeakerSource("usb");
                        break;
                    case Config.WakeAction.ReselectUsb:
                        // Deviation (Windows, A84): the LSX II leaves USB after a while but still reports source usb;
                        // selecting usb again is the wake. Once per loss episode; after a failed attempt (not back
                        // within 10 s) the next one waits at least 60 s, logged once per episode.
                        Log("playback started while LSX II is gone from USB: selecting USB again");
                        DateTime at = DateTime.UtcNow;
                        usbReselectAt = at;
                        SetSpeakerSource("usb");
                        queue.AsyncAfter(Config.usbReselectVerdict, () =>
                        {
                            if (usbReselectAt != at || usbReselectBackoffLogged || FindReal() != null) return;
                            usbReselectBackoffLogged = true;
                            Log($"LSX II did not return to USB within {Config.usbReselectVerdict.TotalSeconds:0} s after selecting USB again; next attempt in {Config.usbReselectBackoff.TotalSeconds:0} s at the earliest");
                        });
                        break;
                }
            }));
        });
    }

    /// ≙ `proxyVolumeChanged()`: a key changed the virtual volume (or the mute, §5.1). `expectedProxyScalar` is never set
    /// in v1 (SetFromSpeaker raises no event, §6.7), so the echo check stays inert; kept for v2.
    void ProxyVolumeChanged()
    {
        if (!onConsole || proxy?.Scalar is not float s) return;
        if (expectedProxyScalar is float e && Math.Abs(e - s) < 0.001) { expectedProxyScalar = null; return; }
        int v = Config.SpeakerVolume(forScalar: s);
        ScheduleSend(v);
    }

    /// ≙ `pushProxyToSpeaker(reason:)`: send the PC's value as if the speaker's state were unknown (approached from above).
    void PushProxyToSpeaker(string reason)
    {
        if (proxy?.Scalar is not float s) return;
        int v = Config.SpeakerVolume(forScalar: s);
        Log($"push proxy -> speaker: {v} ({reason})");
        lastKnownSpeaker = null;
        lastSentSpeaker = null;
        ScheduleSend(v);
    }

    /// GET of the speaker volume in flight for PushProxyToSpeakerIfDifferent; the poll does not mirror meanwhile.
    bool reappearCheckInFlight = false;

    /// Deviation (Windows, A85, §5.6): the LSX II re-enumerates after every idle period, and a push always overshoots
    /// first (from "unknown", §5.3). So read the speaker first: if it already has the PC's value, take it as known and send
    /// nothing; otherwise (also on a failed GET, or when a key send is under way) push as on macOS.
    void PushProxyToSpeakerIfDifferent(string reason)
    {
        if (proxy?.Scalar is not float) return;
        reappearCheckInFlight = true;
        speaker.GetVolume(v => queue.Async(() =>
        {
            reappearCheckInFlight = false;
            if (proxy?.Scalar is not float s) return;
            int target = Config.SpeakerVolume(forScalar: s);
            if (v is int value && !sendInFlight && queuedValue == null && !Config.ReappearPushNeeded(value, target))
            {
                lastSpeakerContact = DateTime.UtcNow;
                lastKnownSpeaker = value;
                Log($"push proxy -> speaker: skipped, speaker already at {value} ({reason})");
                SpeakerVolumeChanged?.Invoke();
                return;
            }
            PushProxyToSpeaker(reason);
        }));
    }

#if SEND_TRACE
    long traceScheduledAt;
    static double TraceMs(long since) => System.Diagnostics.Stopwatch.GetElapsedTime(since).TotalMilliseconds;
#endif

    /// ≙ `scheduleSend(_:)` (§5.3): the last value wins; flushSend 0.03 s after the last call.
    void ScheduleSend(int v)
    {
        queuedValue = v;
        pendingSend?.Cancel();
        var w = new CancellationTokenSource();
        pendingSend = w;
#if SEND_TRACE
        traceScheduledAt = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        queue.AsyncAfter(TimeSpan.FromSeconds(0.03), () =>
        {
            if (w.IsCancellationRequested) return;
#if SEND_TRACE
            Log(FormattableString.Invariant($"send trace: flush {v} after {TraceMs(traceScheduledAt):F1} ms (asked 30)"));
#endif
            FlushSend();
        });
    }

    /// ≙ `flushSend()` (§5.3).
    void FlushSend()
    {
        if (sendInFlight || queuedValue is not int v) return;
        queuedValue = null;
        if (v == lastSentSpeaker && v == lastKnownSpeaker) return;
        sendInFlight = true;
        // Reach every value from above (Config.ApproachFromAbove): when the value goes up, or when the speaker's state is
        // unknown (start, reset, re-enumeration), set a slightly higher value first and the target right after. Going
        // down from a known value already arrives from above.
        int? from = lastKnownSpeaker ?? lastSentSpeaker;
        bool goingUp = from is not int f || v > f;
        int? via = goingUp ? Config.ApproachFromAbove(v, cap: Config.MaxSpeakerVolume) : null;
        lastSentSpeaker = v;
        void Finish(bool ok) => queue.Async(() =>
        {
            sendInFlight = false;
            if (ok)
            {
                lastKnownSpeaker = v; lastSpeakerContact = DateTime.UtcNow;
                Log($"proxy -> speaker: {v}" + (via is int x ? $" (via {x})" : ""));
                SpeakerVolumeChanged?.Invoke();   // A81: the tray number follows a key press at once
            }
            else Log($"speaker setVolume {v} failed");
            if (queuedValue != null) FlushSend();
        });
        if (via is not int viaValue) { speaker.SetVolume(v, Finish); return; }
        speaker.SetVolume(viaValue, ok =>
        {
            if (!ok) { Finish(false); return; }
#if SEND_TRACE
            long held = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            queue.AsyncAfter(TimeSpan.FromSeconds(Config.ApproachHold), () =>
            {
#if SEND_TRACE
                Log(FormattableString.Invariant($"send trace: target {v} after {TraceMs(held):F1} ms hold (asked {Config.ApproachHold * 1000:F0})"));
#endif
                speaker.SetVolume(v, ok2 =>
                {
                    if (!ok2)
                    {
                        // The overshoot landed but the target did not: the speaker now sits at `via`, louder than asked.
                        // Record that truthfully (so the next poll does not mirror `via` to the PC as a user change) and
                        // send the target once more; from `via` it goes down, so no new overshoot.
                        queue.Async(() => { lastKnownSpeaker = viaValue; if (queuedValue == null) queuedValue = v; });
                    }
                    Finish(ok2);
                });
            });
        });
    }

    /// Raised on the controller queue when a poll reads a new speaker value, a send succeeded, or the reappear check found
    /// the PC's value on the speaker. Addition (A81): the tray icon redraws at once instead of waiting for its 2-s timer
    /// (macOS: up to 2 s more delay in the menu bar).
    public event Action? SpeakerVolumeChanged;

    void PollSpeaker()
    {
        speaker.GetVolume(v => queue.Async(() =>
        {
            if (v is not int value) return;
            lastSpeakerContact = DateTime.UtcNow;
            if (value != lastKnownSpeaker)
            {
                int? previous = lastKnownSpeaker;
                lastKnownSpeaker = value;
                SpeakerVolumeChanged?.Invoke();
                if (sendInFlight || queuedValue != null || reappearCheckInFlight) return;
                if (lastSentSpeaker is int sent && sent == value) return;
                if (previous is int p && Math.Abs(value - p) >= Config.ResetJumpThreshold)
                {
                    PushProxyToSpeaker($"speaker jumped {p} -> {value}, treating as reset");
                    return;
                }
                ApplyToProxy(value);
            }
        }));
    }

    /// ≙ `applyToProxy(_:)` (§5.4): the speaker's value into the virtual volume, silently (no OSD, no user event, so no
    /// echo); stored in State\volume. Mute stays untouched. expectedProxyScalar is not set in v1 (§6.7).
    void ApplyToProxy(int speakerVolume)
    {
        if (proxy is not IVolumeSurface p) return;
        float s = Config.Scalar(forSpeakerVolume: speakerVolume);
        if (p.Scalar is float cur && Math.Abs(cur - s) < 0.001) return;
        int err = p.SetFromSpeaker(s);
        if (err != 0) Log($"set proxy scalar failed {ErrText(err)}");
        else Log($"speaker -> proxy: {speakerVolume}");
    }

    /// ≙ `pinRealVolume(_:)` (§5.7.1): channels (elements 1, 2) and master (element 0) of the LSX II endpoint to 1.0,
    /// with `ownGuid`, mute untouched; the work and the log lines are in UsbRealOutput.PinVolume. `dev`: the endpoint
    /// just found by rescan (attached right before), else `real`.
    void PinRealVolume(string? dev = null)
    {
        if ((dev ?? real) is not string r || realOutput.EndpointId != r) return;
        realOutput.PinVolume();
    }

    /// Rule A (§6.4, Addition): a foreign level change on the LSX II endpoint is never taken over; once 300 ms passed
    /// without another one, pinRealVolume runs with its usual log lines. Console only (§5.14); the 10-s pin timer stays as
    /// the fallback.
    void RealVolumeChangedExternally()
    {
        if (!onConsole) return;
        int g = ++foreignChangeGeneration;
        queue.AsyncAfter(repinQuiet, () =>
        {
            if (g == foreignChangeGeneration && onConsole) PinRealVolume();
        });
    }

    /// ≙ `checkDefaultOutput()`: every render role (§5.12).
    void CheckDefaultOutput()
    {
        foreach (ERole role in roles) CheckDefaultOutput(role);
    }

    /// The state machine of `checkDefaultOutput()` for one role (§5.12): it keeps the state that the menu line "Output"
    /// shows and that the restore in rescan uses.
    void CheckDefaultOutput(ERole role)
    {
        if (!onConsole || !Config.AutoSwitchDefault) return;
        var (ok, def) = DefaultDevice(EDataFlow.eRender, role);
        if (!ok) return;
        IVolumeSurface? p = proxy ?? FindProxy();
        string? target = p?.OutputEndpointId;
        if ((real ?? FindReal()) is string r && def == r && p != null && target != r)
        {
            // (a) The default is the real device but the target is another endpoint: v2 only (the driver's endpoint).
            // In v1 the target is the LSX II endpoint itself, so this never happens (§5.12, A20); kept for v2.
            if (target == null || !policyConfigAvailable) return;
            int err = SetDefaultEndpoint(target, role);
            Log($"default output was real LSX II; switched to {p.TargetName} ({ErrText(err)}) [{RoleName(role)}]");
            isDefault[(int)role] = err == 0;
            return;
        }
        int k = (int)role;
        // (b) default is the target; (c) lost it because the USB device went away, not by user choice (restored in
        // rescan on return); A84: a pending restore survives an evaluation without a new choice (start, settings change,
        // console regained); (d) anything else is the user's choice. Pure rules: Config.DefaultOutputStep.
        var step = Config.DefaultOutputStep(def, target, realPresent: FindReal() != null, isDefault[k], restoreOnReturn[k],
                                            substituteKnown[k], substituteDefault[k]);
        isDefault[k] = step.IsDefault;
        restoreOnReturn[k] = step.RestoreOnReturn;
        substituteKnown[k] = step.SubstituteKnown;
        substituteDefault[k] = step.Substitute;
        // `none`: no endpoint of this direction (E_NOTFOUND), where macOS logs kAudioObjectUnknown as `0` (A40).
        if (step.Branch == Config.DefaultOutputBranch.LeftWhileAbsent)
            Log($"default output left {p?.TargetName ?? "LSX II"} while USB device absent (now {def ?? "none"}); will restore on return [{RoleName(role)}]");
        PersistRestorePending();
    }

    /// A84: the per-role pending restores at start (Config.RestorePendingAtStart): the stored HKCU mask, else pending for
    /// every role on a fresh install without the LSX II. No log line; the menu and Plan B pick it up.
    void LoadRestorePending()
    {
        object? raw = Config.defaults.Object(forKey: Config.restorePendingKey);
        storedRestoreMask = raw != null ? RegistryDefaults.IntegerValue(raw) : null;
        bool[] pending = Config.RestorePendingAtStart(raw, Config.defaults.String(forKey: "defaultOutputSetFor"),
                                                      realPresent: FindReal() != null, roles: restoreOnReturn.Length);
        for (int k = 0; k < restoreOnReturn.Length; k++) restoreOnReturn[k] = pending[k];
    }

    /// A84: write the per-role pending restores to HKCU when they changed.
    void PersistRestorePending()
    {
        int mask = Config.RestorePendingMask(restoreOnReturn);
        if (storedRestoreMask == mask) return;
        storedRestoreMask = mask;
        Config.Set(mask, forKey: Config.restorePendingKey);
    }
}

// MARK: - ≙ extension Controller

sealed partial class Controller
{
    void RefreshConsole()
    {
        bool now = IsConsoleSession();
        if (now != onConsole)
        {
            onConsole = now;
            Log($"console session: {(now ? "true" : "false")}");
            if (now)
            {
                consoleGainedAt = DateTime.UtcNow;
                speaker.ResetSession("session became console");
                Rescan(); CheckDefaultOutput(); CheckDefaultInput();
                // Addition (§5.14, A26): another session may have changed the machine-wide volume meanwhile.
                surface.LoadStoredVolume(initial: false);
                PushProxyToSpeaker("session became console");
            }
            UpdateInterceptActive();
        }
    }

    /// ≙ `speakerPersistentlyUnreachable()` (§5.15): last escalation for the stale-session failure (every speaker request
    /// failing after a user switch, cured only by a new process): if a fresh HttpClient did not help within two minutes of
    /// becoming console, exit so the launcher (≙ launchd KeepAlive) starts the agent again. At most once per 10 minutes
    /// (`lastSelfRelaunch`, HKCU, ISO 8601), so a speaker that is really off the network cannot keep the agent in a
    /// relaunch loop. ExitApp from the controller thread only posts to the UI thread (§3.3).
    void SpeakerPersistentlyUnreachable()
    {
        if (!onConsole || consoleGainedAt is not DateTime t || (DateTime.UtcNow - t).TotalSeconds >= 120) return;
        if (Config.defaults.Date(forKey: "lastSelfRelaunch") is DateTime last && (DateTime.UtcNow - last).TotalSeconds < 600) return;
        Config.Set(DateTime.UtcNow, forKey: "lastSelfRelaunch");
        Log("speaker still unreachable after a new network session since this session became console: exiting so the launcher relaunches the helper");
        ExitApp(0);
    }

    /// ≙ `firstRunDefaultOutput()` (§5.12): make the LSX II the default output once per user and target, for all three
    /// roles. The marker `defaultOutputSetFor` (HKCU) is the port-independent ID of the target,
    /// StatusMenu.UidPrefix(DeviceUid(target)) (A24, ≙ proxyUID); it is stored only when every role succeeded, otherwise
    /// the step is tried again on every rescan (as on macOS).
    void FirstRunDefaultOutput()
    {
        if (!Config.AutoSwitchDefault || !policyConfigAvailable) return;
        if ((proxy ?? FindProxy()) is not IVolumeSurface p || (real ?? FindReal()) == null) return;
        if (p.OutputEndpointId is not string target) return;
        string marker = StatusMenu.UidPrefix(DeviceUid(target));
        if (Config.defaults.String(forKey: "defaultOutputSetFor") == marker) return;
        bool all = true;
        foreach (ERole role in roles)
        {
            int err = SetDefaultEndpoint(target, role);
            Log($"first run: {p.TargetName} set as default output ({ErrText(err)}) [{RoleName(role)}]");
            if (err == 0) isDefault[(int)role] = true; else all = false;
        }
        if (all) Config.Set(marker, forKey: "defaultOutputSetFor");
    }

    /// ≙ `pinRealFormat(_:)` (§5.8): unless the shared-mode device format is 96 kHz / 24-bit integer,
    /// IPolicyConfig::SetDeviceFormat; log `pin real format: <rate> Hz/<bits>-bit[ float] -> 96000 Hz/24-bit (<err>)`.
    /// The work is in UsbRealOutput.PinFormat. Without IPolicyConfig the format is only shown.
    void PinRealFormat(string? dev = null)
    {
        if (!policyConfigAvailable) return;
        if ((dev ?? real) is not string r || realOutput.EndpointId != r) return;
        realOutput.PinFormat();
    }

    /// ≙ `installRealFormatListener(_:)` (§5.8): OnPropertyValueChanged(PKEY_AudioEngine_DeviceFormat) of this endpoint
    /// re-pins (the one IMMNotificationClient of Start delivers it; this only names the endpoint).
    void InstallRealFormatListener(string r)
    {
        if (realFormatListenerFor == r) return;
        realFormatListenerFor = r;
    }

    /// ≙ `checkDefaultInput()`: every capture role (§5.13).
    void CheckDefaultInput()
    {
        foreach (ERole role in roles) CheckDefaultInput(role);
    }

    /// The state machine of `checkDefaultInput()` for one role; independent of autoSwitchDefault, as on macOS.
    void CheckDefaultInput(ERole role)
    {
        if (!onConsole) return;
        if (Config.RestoreInputUIDPrefix == null) return;
        var (ok, def) = DefaultDevice(EDataFlow.eCapture, role);
        if (!ok) return;
        string? i = input ?? FindInput();
        int k = (int)role;
        if (i != null && def == i)
        {
            inputIsDefault[k] = true;
            restoreInputOnReturn[k] = false;
        }
        else if (inputIsDefault[k] && FindInput() == null)
        {
            // `none` ≙ macOS `0` (kAudioObjectUnknown), as in CheckDefaultOutput.
            inputIsDefault[k] = false;
            restoreInputOnReturn[k] = true;
            Log($"default input left configured device while it is absent (now {def ?? "none"}); will restore on return [{RoleName(role)}]");
        }
        else
        {
            inputIsDefault[k] = false;
            restoreInputOnReturn[k] = false;
        }
    }
}

// MARK: - Serial queue (≙ DispatchQueue)

/// ≙ `DispatchQueue(label:)`: one MTA thread runs the work items in order (docs/DESIGN.md §3.3). Delayed work and timers
/// only enqueue. Background thread: it never keeps the process alive.
sealed class SerialQueue
{
    readonly BlockingCollection<Action> items = new();
    readonly Thread thread;

    public SerialQueue(string label)
    {
        thread = new Thread(() => { foreach (var work in items.GetConsumingEnumerable()) work(); }) { Name = label, IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// ≙ `async`.
    public void Async(Action work) => items.Add(work);

    /// ≙ `asyncAfter(deadline:)`: a timer that only enqueues.
    public void AsyncAfter(TimeSpan delay, Action work) => _ = Task.Delay(delay).ContinueWith(_ => Async(work), TaskScheduler.Default);

    /// ≙ `sync`: runs `work` on the queue and waits for it. Never from the queue itself (deadlock, as with GCD).
    public T Sync<T>(Func<T> work)
    {
        if (Thread.CurrentThread == thread) throw new InvalidOperationException("SerialQueue.Sync on its own thread");
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Async(() => { try { result.SetResult(work()); } catch (Exception e) { result.SetException(e); } });
        return result.Task.GetAwaiter().GetResult();
    }

    /// ≙ `DispatchSource.makeTimerSource(queue:)` + `schedule(deadline: .now() + first, repeating: every)`. Like a
    /// dispatch timer it does not pile up: a tick that is still waiting on the queue absorbs the next one.
    public System.Threading.Timer Timer(TimeSpan first, TimeSpan every, Action handler)
    {
        int queued = 0;
        return new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref queued, 1) == 1) return;
            Async(() => { Volatile.Write(ref queued, 0); handler(); });
        }, null, first, every);
    }
}

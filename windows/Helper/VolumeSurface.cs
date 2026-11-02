// The seam for a later driver (docs/DESIGN.md §6.7, §13.1): the role of the macOS proxy device (volume, mute, "running")
// behind an interface, and the real USB output behind another. Controller and Config see only the interfaces.
//
// v1's KeyHookVolumeSurface: always present (`proxy device: keyhook`), the LSX II endpoint as output (the target of
// autoSwitchDefault, §5.12), the audio sessions of that endpoint as "running" (§5.11), the endpoint's hardware mute as
// Muted and, since M3, the mute reset once per system start (§5.10). M4: the virtual volume (Scalar), stored
// machine-wide in HKLM\…\State\volume (§5.14), the volume keys of the keyboard hook (KeyHook.cs, §5.1, §6.2) with the
// mute key (the endpoint's hardware mute, §5.10) and the OSD (VolumeOsd.cs, §6.5).
//
// M3: UsbRealOutput is v1's IRealOutput, the LSX II endpoint with the volume pin (§5.7), the format pin (§5.8) and the
// change callback that tells foreign volume changes from our own (§6.4).

// macOS role: volume/mute controls of the proxy device + DeviceIsRunningSomewhere.
interface IVolumeSurface : IDisposable
{
    string Kind { get; }                 // "keyhook" (v1) | "virtual-endpoint" (v2)
    bool IsPresent { get; }              // proxy != nil
    float? Scalar { get; }               // 0…1, 50 steps (1/50) like the Windows keys
    bool Muted { get; }
    int SetFromSpeaker(float scalar);    // applyToProxy; raises no user event; 0 or the error (≙ setProp's OSStatus)
    bool IsRunningSomewhere { get; }     // auto-wake
    string? OutputEndpointId { get; }    // target of autoSwitchDefault / firstRunDefaultOutput
    string TargetName { get; }           // "LSX II" (v1) | "proxy" (v2), for logs and menu
    event Action UserScalarChanged;      // -> proxyVolumeChanged -> scheduleSend
    event Action UserMuteChanged;
    event Action RunningChanged;         // -> proxyRunningChanged
    event Action PresenceChanged;        // -> rescan
}

// macOS role: the real USB device.
interface IRealOutput
{
    string? EndpointId { get; }          // found via DeviceUid ∋ "VID_27AC&PID_1060"
    void PinVolume();                    // channels + master -> 1.0, mute untouched
    void PinFormat();                    // v1: IPolicyConfig (shared format); v2: the bridge opens exclusively
    string? FormatText { get; }          // "96 kHz / 24-bit"
    event Action Changed;
}

/// v1's real output: the LSX II USB endpoint (§5.7, §5.8). Controller thread only. Attach takes a new endpoint instance
/// (new IAudioEndpointVolume with callback, §5.6); the old objects are released here, on the controller thread.
/// `Changed` is raised (enqueued) for foreign level changes: a notification with another event context than `ownGuid`
/// and a channel or the master below the pin threshold (a foreign mute change alone is nothing to do, §6.4 rule A 1).
sealed class UsbRealOutput(SerialQueue queue) : IRealOutput
{
    EndpointVolume? volume;
    /// pinRealVolume's threshold (≙ `cur < 0.999`).
    const float pinThreshold = 0.999f;

    public string? EndpointId { get; private set; }
    public event Action Changed = delegate { };
    /// The endpoint volume of the attached endpoint, for the surface's mute (§5.10).
    internal EndpointVolume? Volume => volume;

    /// Watches `endpointId` (null: none). `newInstance`: Windows keeps the endpoint ID across a USB re-enumeration
    /// (§5.6). A failed activation is retried on the next call.
    internal void Attach(string? endpointId, bool newInstance = false)
    {
        if (!newInstance && endpointId == EndpointId && (endpointId == null || volume != null)) return;
        volume?.Dispose();
        volume = null;
        EndpointId = endpointId;
        if (endpointId != null) volume = EndpointVolume.Open(endpointId, OnNotify);
    }

    /// COM callback thread: only enqueue (§3.3).
    void OnNotify(VolumeNotification n)
    {
        if (n.EventContext == ownGuid) return;
        if (n.Master >= pinThreshold && n.Channels.All(c => c >= pinThreshold)) return;
        queue.Async(() => Changed());
    }

    /// ≙ `pinRealVolume(_:)` (§5.7.1): every channel below 0.999 to 1.0, logged as element ch+1, then the master as
    /// element 0; with `ownGuid`; mute untouched.
    public void PinVolume()
    {
        if (EndpointId != null && volume == null) Attach(EndpointId);   // retry a failed activation
        if (volume is not EndpointVolume v) return;
        uint n = v.ChannelCount();
        for (uint ch = 0; ch < n; ch++)
        {
            if (v.ChannelScalar(ch) is float cur && cur < pinThreshold)
            {
                int err = v.SetChannelScalar(ch, 1.0f, ownGuid);
                Log($"pin real USB volume element {ch + 1}: {cur.ToString(System.Globalization.CultureInfo.InvariantCulture)} -> 1.0 ({ErrText(err)})");
            }
        }
        if (v.MasterScalar() is float m && m < pinThreshold)
        {
            int err = v.SetMasterScalar(1.0f, ownGuid);
            Log($"pin real USB volume element 0: {m.ToString(System.Globalization.CultureInfo.InvariantCulture)} -> 1.0 ({ErrText(err)})");
        }
    }

    /// ≙ `pinRealFormat(_:)` (§5.8): unless the shared-mode device format is 96 kHz / 24-bit integer, write it with
    /// IPolicyConfig::SetDeviceFormat and log `pin real format: <rate> Hz/<bits>-bit[ float] -> 96000 Hz/24-bit (<err>)`.
    public void PinFormat()
    {
        if (EndpointId is not string r || DeviceFormat(r) is not { } cur) return;
        if (Math.Abs(cur.rate - pinnedRealRate) < 0.5 && cur.bits == pinnedRealBits && !cur.isFloat) return;
        string was = $"{cur.rate.ToString(System.Globalization.CultureInfo.InvariantCulture)} Hz/{cur.bits.ToString(System.Globalization.CultureInfo.InvariantCulture)}-bit{(cur.isFloat ? " float" : "")}";
        int err = SetDeviceFormat(r, (uint)pinnedRealRate, (int)pinnedRealBits, cur.channels, cur.channelMask);
        Log($"pin real format: {was} -> {((int)pinnedRealRate).ToString(System.Globalization.CultureInfo.InvariantCulture)} Hz/{pinnedRealBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}-bit ({ErrText(err)})");
    }

    public string? FormatText => EndpointId is string r ? DeviceFormatText(r) : null;
}

/// v1 (§6.7): hook, OSD, stored scalar, endpoint mute, the LSX II endpoint's sessions as "running", OutputEndpointId =
/// the LSX II endpoint. Everything but the OSD runs on the controller thread (the hook only enqueues, §6.2).
sealed class KeyHookVolumeSurface : IVolumeSurface
{
    readonly Func<string?> lsxEndpoint;
    readonly SessionWatcher sessions;
    readonly UsbRealOutput realOutput;
    /// The system start whose mute reset is done (also when storing muteResetBoot failed, §5.10).
    DateTime? muteResetDoneFor;
    /// The virtual volume (≙ the driver's gVolume): read from State\volume at start, default 0.5 (§5.14).
    float scalar = 0.5f;
    /// The OSD of the UI thread (set by AppDelegate after it is created); Show posts to the UI thread.
    internal volatile VolumeOsd? osd;

    /// `lsxEndpoint`: the controller's LSX II endpoint (`real ?? FindReal()`), so that in v1 the output always is the
    /// endpoint branch (a) of checkDefaultOutput compares with, and that branch stays inactive (§5.12). `realOutput`:
    /// the same endpoint's volume object, whose hardware mute is the surface's mute (§5.10).
    public KeyHookVolumeSurface(SerialQueue queue, Func<string?> lsxEndpoint, UsbRealOutput realOutput)
    {
        this.lsxEndpoint = lsxEndpoint;
        this.realOutput = realOutput;
        sessions = new SessionWatcher(queue.Async, () => RunningChanged());
    }

    public string Kind => "keyhook";
    public bool IsPresent => true;
    /// The virtual volume (§5.1, §5.14), machine-wide like the driver's host storage.
    public float? Scalar => scalar;
    /// The endpoint's hardware mute (§5.10); false without the LSX II.
    public bool Muted => realOutput.Volume?.Muted() ?? false;

    /// §5.10 (≙ the driver's `gMute = 0` at start): once per system start, as soon as the LSX II endpoint is there, the
    /// console instance lifts its hardware mute (SetMute(false, ownGuid)), then stores the start time as
    /// `State\muteResetBoot`. A failed store is remembered in memory, so a later rescan does not lift a mute of the user.
    /// No log line (§9 has none). Controller thread, called from rescan (console only).
    internal void ResetMuteOncePerBoot()
    {
        if (realOutput.Volume is not EndpointVolume v) return;
        DateTime boot = MachineState.BootTime();
        if (muteResetDoneFor is DateTime done && MachineState.SameBoot(done, boot)) return;
        if (RegistryDefaults.DateValue(MachineState.Get("muteResetBoot")) is DateTime stored && MachineState.SameBoot(stored, boot))
        {
            muteResetDoneFor = boot;
            return;
        }
        if (v.SetMute(false, ownGuid) < 0) return;   // retried on the next rescan
        muteResetDoneFor = boot;
        _ = MachineState.Set("muteResetBoot", RegistryDefaults.DateString(boot));
    }
    /// ≙ applyToProxy's setProp (§5.4): store the virtual volume, without OSD and without a user event (so v1 has no
    /// echo, §6.7). Returns 0, or the error of writing State\volume (the controller logs `set proxy scalar failed`).
    public int SetFromSpeaker(float s) => Store(s);

    /// Reads State\volume at start (log `initialize volume=<%.3f>`, ≙ the driver) and, Addition (§5.14, A26), again before
    /// the push when this session becomes the console: another session may have changed it meanwhile.
    internal void LoadStoredVolume(bool initial)
    {
        float s = Config.StoredVolume(MachineState.Get("volume"));
        if (initial)
        {
            scalar = s;
            Log($"initialize volume={s.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        else if (Math.Abs(s - scalar) > 1e-6)
        {
            scalar = s;
            Log($"volume set {s.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    /// Every change by more than 1e-6 (≙ the driver): log `volume set <%.4f>`, write State\volume (REG_SZ, invariant).
    /// The value in memory changes also when the write fails (as the driver's gVolume does).
    int Store(float s)
    {
        if (Math.Abs(s - scalar) <= 1e-6) return 0;
        scalar = s;
        Log($"volume set {s.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
        return MachineState.SetWithError("volume", Config.StoredVolumeText(s));
    }

    /// A volume key from the hook (§5.1), on the controller thread and only on the console: lift the mute and step by
    /// 1/50, 0 = speaker 0 + mute, the mute key toggles the endpoint's hardware mute (SetMute with ownGuid, §5.10); then
    /// store, show the OSD and raise the user event (→ proxyVolumeChanged → scheduleSend).
    internal void HandleKey(VolumeKey key)
    {
        bool muted = Muted;
        var (s, mute) = Config.ApplyVolumeKey(key, scalar, muted);
        if (mute != muted && realOutput.Volume is EndpointVolume v) _ = v.SetMute(mute, ownGuid);
        _ = Store(s);
        osd?.Show(scalar, Muted);
        if (key == VolumeKey.Mute) UserMuteChanged(); else UserScalarChanged();
    }

    public bool IsRunningSomewhere => sessions.IsRunningNow();
    public string? OutputEndpointId => lsxEndpoint();
    public string TargetName => "LSX II";

    /// Raised by the volume keys (HandleKey), never by SetFromSpeaker.
    public event Action UserScalarChanged = delegate { };
    /// Raised by the mute key (HandleKey).
    public event Action UserMuteChanged = delegate { };
    public event Action RunningChanged = delegate { };
    /// v1: the surface is always present; never raised.
    public event Action PresenceChanged { add { } remove { } }

    /// The LSX II endpoint whose sessions count as "running" (§5.11); a new endpoint instance gets a new session
    /// manager (§5.6), also under the same endpoint ID (`newInstance`). Controller thread.
    internal void AttachReal(string? endpointId, bool newInstance = false) => sessions.Attach(endpointId, newInstance);
    /// The 1-s safety-net scan (§5.11). Controller thread.
    internal void ScanSessions() => sessions.Scan();

    public void Dispose() { }
}

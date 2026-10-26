// ≙ Helper/CoreAudioSupport.swift.
//
// Core Audio of Windows (MMDevice API and WASAPI audio sessions), docs/DESIGN.md §4: endpoint enumeration, property store,
// DeviceUid, FindReal, FindInput, the default endpoints, the shared-mode format, the running state (sessions), and the
// COM declarations ([GeneratedComInterface], §10.5). Every object belongs to the controller thread (MTA, §3.3); the
// COM callbacks (IMMNotificationClient, IAudioEndpointVolumeCallback, IAudioSessionNotification, IAudioSessionEvents)
// only enqueue.
//
// M3 (§16): the writes of the pins and the default devices, and nothing else:
//  - IAudioEndpointVolume: channel and master scalar (volume pin, §5.7) and mute (reset once per boot, §5.10), always
//    with the own event context `ownGuid`; IAudioEndpointVolumeCallback reports foreign changes (§6.4);
//  - IPolicyConfig (undocumented, §5.8, §5.12): SetDeviceFormat (format pin) and SetDefaultEndpoint (default devices);
//    GetMixFormat is read-only;
//  - IPropertyStore is still declared only up to GetValue (no SetValue, no Commit) and always opened with STGM_READ;
//  - IAudioClient is not declared, so there is no stream of any kind (shared or exclusive);
//  - the vtable slots that come before a needed method and would change something else
//    (IAudioSessionControl2.SetDisplayName/SetIconPath/SetGroupingParam, IConnector.ConnectTo/Disconnect,
//    IAudioSessionManager2.GetSimpleAudioVolume, IAudioEndpointVolume.SetMasterVolumeLevel/SetChannelVolumeLevel (dB),
//    IPolicyConfig.ResetDeviceFormat/SetProcessingPeriod/SetShareMode/SetPropertyValue …) are declared for the vtable
//    layout only, with raw pointer parameters, and are never called.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

// MARK: - Helpers (≙ addr, getProp, getString, allDevices, findReal, findInput)

static partial class Globals
{
    /// Wraps raw interface pointers that this file obtains itself (Activate, enumerators); the generated marshallers use
    /// their own instance.
    internal static readonly StrategyBasedComWrappers comWrappers = new();

    /// The device enumerator of the calling thread. Core Audio objects are created and used on the controller thread
    /// only (§3.3); the render harness's `devices` mode (a development tool, not published) uses an MTA thread of its own, because
    /// session notifications reach only registrations made on an MTA thread.
    [ThreadStatic] static IMMDeviceEnumerator? t_enumerator;

    internal static IMMDeviceEnumerator DeviceEnumerator()
    {
        if (t_enumerator is { } e) return e;
        // The controller thread is MTA; S_FALSE (already initialized) and RPC_E_CHANGED_MODE (an STA thread) are fine.
        _ = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_INPROC_SERVER, typeof(IMMDeviceEnumerator).GUID, out nint p));
        return t_enumerator = WrapCom<IMMDeviceEnumerator>(p);
    }

    /// The managed wrapper of a raw interface pointer; takes over the caller's reference. `unique`: a wrapper of its
    /// own, so ReleaseCom can release it at once (on the controller thread, never in a callback, §5.6).
    internal static T WrapCom<T>(nint p, bool unique = false) where T : class
    {
        try { return (T)comWrappers.GetOrCreateObjectForComInstance(p, unique ? CreateObjectFlags.UniqueInstance : CreateObjectFlags.None); }
        finally { Marshal.Release(p); }
    }

    /// Releases a wrapper made with `unique: true` now; other wrappers are released by the GC.
    internal static void ReleaseCom(object? o)
    {
        if (o is ComObject c) c.FinalRelease();
    }

    /// ≙ allDevices(): the endpoint IDs of the active endpoints of one direction, in enumeration order. "Active" means
    /// present and enabled (DEVICE_STATE_ACTIVE); macOS has no disabled devices (A23).
    public static string[] AllDevices(EDataFlow flow) => AllDevices(flow, DEVICE_STATE_ACTIVE);

    /// Any device states (the render harness also lists absent and disabled endpoints, DEVICE_STATEMASK_ALL = 0xF).
    internal static string[] AllDevices(EDataFlow flow, uint stateMask)
    {
        try
        {
            IMMDeviceCollection c = DeviceEnumerator().EnumAudioEndpoints(flow, stateMask);
            uint n = c.GetCount();
            var ids = new List<string>((int)n);
            for (uint i = 0; i < n; i++)
            {
                try { ids.Add(c.Item(i).GetId()); } catch (Exception) { }   // vanished meanwhile
            }
            return [.. ids];
        }
        catch (Exception) { return []; }
    }

    static IMMDevice? Device(string endpointId)
    {
        try { return DeviceEnumerator().GetDevice(endpointId, out IMMDevice? d) >= 0 ? d : null; }
        catch (Exception) { return null; }
    }

    /// ≙ getString(id, kAudioDevicePropertyDeviceUID): the device instance ID of the endpoint's parent device, e.g.
    /// `USB\VID_27AC&PID_1060&MI_01\8&1A2B3C4D&0&0001` (docs/DESIGN.md §4). Source: the endpoint property
    /// {b3f8fa53-0004-438e-9003-51a46e139bfc},2 without its `{1}.` prefix; fallback 1: the device topology (connector 0,
    /// GetDeviceIdConnectedTo); fallback 2: the endpoint ID. Not used: ContainerId, the display name ("2- LSX II") or the
    /// endpoint ID as the main key (all of them hang on the USB port).
    public static string DeviceUid(string endpointId)
    {
        if (Device(endpointId) is not IMMDevice d) return endpointId;
        if (StringProperty(d, PKEY_DeviceParentInstanceId) is string s && StripIdPrefix(s) is { Length: > 0 } uid) return uid;
        return TopologyParentInstanceId(d) ?? endpointId;
    }

    /// DEVICE_STATE_ACTIVE 1, DISABLED 2, NOTPRESENT 4, UNPLUGGED 8; 0 when unreadable.
    internal static uint DeviceState(string endpointId)
    {
        try { return Device(endpointId)?.GetState() ?? 0; } catch (Exception) { return 0; }
    }

    /// ≙ getString(id, kAudioObjectPropertyName): the endpoint's display name, e.g. "Mikrofon (2- NT-USB Mini)".
    public static string DeviceName(string endpointId) =>
        Device(endpointId) is IMMDevice d ? StringProperty(d, PKEY_Device_FriendlyName) ?? endpointId : endpointId;

    /// ≙ findReal(): the first active render endpoint whose DeviceUid contains `realModelMatch`, ignoring case
    /// (deviation Windows, A23: device IDs are not case-sensitive on Windows; macOS `contains` is). The HID interface
    /// MI_00 is no audio endpoint and drops out by itself.
    public static string? FindReal() =>
        AllDevices(EDataFlow.eRender).FirstOrDefault(id => DeviceUid(id).Contains(realModelMatch, StringComparison.OrdinalIgnoreCase));

    /// ≙ findInput(): the first active capture endpoint whose DeviceUid begins with `restoreInputUIDPrefix` (§5.13);
    /// case-sensitive like `hasPrefix` (the prefix comes from the same DeviceUid, StatusMenu.UidPrefix).
    public static string? FindInput()
    {
        if (Config.RestoreInputUIDPrefix is not string prefix) return null;
        return AllDevices(EDataFlow.eCapture).FirstOrDefault(id => DeviceUid(id).StartsWith(prefix, StringComparison.Ordinal));
    }

    /// ≙ getProp(kAudioObjectSystemObject, kAudioHardwarePropertyDefault{Output,Input}Device): the default endpoint of a
    /// direction and role (§5.12). `ok` false: unreadable (≙ getProp returning nil); `id` null: there is no endpoint
    /// (E_NOTFOUND, ≙ kAudioObjectUnknown).
    public static (bool ok, string? id) DefaultDevice(EDataFlow flow, ERole role)
    {
        try
        {
            int hr = DeviceEnumerator().GetDefaultAudioEndpoint(flow, role, out IMMDevice? d);
            if (hr == E_NOTFOUND) return (true, null);
            if (hr < 0 || d == null) return (false, null);
            return (true, d.GetId());
        }
        catch (Exception) { return (false, null); }
    }

    /// The shared-mode device format (`PKEY_AudioEngine_DeviceFormat`, §5.8, A58): sample rate, valid bits per sample,
    /// float or integer, channels, channel mask (0 without WAVEFORMATEXTENSIBLE); null when unreadable.
    public static (uint rate, int bits, bool isFloat, int channels, uint channelMask)? DeviceFormat(string endpointId)
    {
        if (Device(endpointId) is not IMMDevice d) return null;
        try
        {
            IPropertyStore store = d.OpenPropertyStore(STGM_READ);
            store.GetValue(PKEY_AudioEngine_DeviceFormat, out PROPVARIANT pv);
            try
            {
                if (pv.vt != VT_BLOB) return null;
                int size = (int)(uint)(pv.p1 & 0xFFFFFFFF);   // BLOB.cbSize; on 64-bit followed by padding
                if (size < 16 || pv.p2 == IntPtr.Zero) return null;
                var b = new byte[size];
                Marshal.Copy(pv.p2, b, 0, size);
                return ParseWaveFormat(b);
            }
            finally { _ = PropVariantClear(ref pv); }
        }
        catch (Exception) { return null; }
    }

    /// WAVEFORMATEX / WAVEFORMATEXTENSIBLE: wFormatTag @0, nChannels @2, nSamplesPerSec @4, wBitsPerSample @14, cbSize @16,
    /// then (EXTENSIBLE) wValidBitsPerSample @18, dwChannelMask @20 and SubFormat @24.
    internal static (uint rate, int bits, bool isFloat, int channels, uint channelMask)? ParseWaveFormat(byte[] b)
    {
        if (b.Length < 16) return null;
        ushort tag = BitConverter.ToUInt16(b, 0);
        int channels = BitConverter.ToUInt16(b, 2);
        uint rate = BitConverter.ToUInt32(b, 4);
        int bits = BitConverter.ToUInt16(b, 14);
        bool isFloat = tag == WAVE_FORMAT_IEEE_FLOAT;
        uint mask = 0;
        if (tag == WAVE_FORMAT_EXTENSIBLE && b.Length >= 40 && BitConverter.ToUInt16(b, 16) >= 22)
        {
            int valid = BitConverter.ToUInt16(b, 18);
            if (valid > 0) bits = valid;   // ≙ mBitsPerChannel of a packed format: 24-in-32 counts as 24
            mask = BitConverter.ToUInt32(b, 20);
            isFloat = new Guid(b.AsSpan(24, 16)) == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }
        return (rate, bits, isFloat, channels, mask);
    }

    /// ≙ OSStatus in logs: `0` for S_OK, otherwise the HRESULT as `0x%08X` (A40, §9).
    public static string ErrText(int hr) => hr == 0 ? "0" : "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    // MARK: IPolicyConfig (undocumented, §5.8, §5.12; A13, A19)

    /// The policy-config object of the calling thread (the controller thread), or null with the HRESULT of the failed
    /// creation in `policyConfigError`.
    [ThreadStatic] static IPolicyConfig? t_policyConfig;
    [ThreadStatic] static int t_policyConfigError;

    /// Creates (once per thread) CPolicyConfigClient with IPolicyConfig; null when that fails (`error` = HRESULT).
    internal static IPolicyConfig? PolicyConfig(out int error)
    {
        error = t_policyConfigError;
        if (t_policyConfig is { } pc) return pc;
        _ = DeviceEnumerator();   // CoInitializeEx on this thread
        int hr = CoCreateInstance(CLSID_PolicyConfigClient, IntPtr.Zero, CLSCTX_ALL, typeof(IPolicyConfig).GUID, out nint p);
        if (hr < 0 || p == 0) { t_policyConfigError = error = hr < 0 ? hr : E_POINTER; return null; }
        t_policyConfigError = error = 0;
        return t_policyConfig = WrapCom<IPolicyConfig>(p);
    }

    /// ≙ setProp(kAudioObjectSystemObject, kAudioHardwarePropertyDefault{Output,Input}Device, id) for one Windows role:
    /// IPolicyConfig::SetDefaultEndpoint (render or capture, by the endpoint). Returns the HRESULT.
    public static int SetDefaultEndpoint(string endpointId, ERole role)
    {
        if (PolicyConfig(out int error) is not IPolicyConfig pc) return error;
        try { return pc.SetDefaultEndpoint(endpointId, role); }
        catch (Exception e) { return e.HResult; }
    }

    /// The shared-mode format write of the format pin (§5.8): device format WAVEFORMATEXTENSIBLE integer PCM
    /// `rate` Hz, `bits`/`bits` (packed, nBlockAlign = channels · bits/8), mix format the same as IEEE float 32-bit.
    /// Channel count and mask come from the current format (≙ `cur.mChannelsPerFrame`). Returns the HRESULT.
    public static int SetDeviceFormat(string endpointId, uint rate, int bits, int channels, uint channelMask)
    {
        if (PolicyConfig(out int error) is not IPolicyConfig pc) return error;
        if (channelMask == 0) channelMask = channels switch { 1 => 0x4u, 2 => 0x3u, _ => 0u };
        byte[] dev = WaveFormatExtensible(rate, bits, channels, channelMask, KSDATAFORMAT_SUBTYPE_PCM);
        byte[] mix = WaveFormatExtensible(rate, 32, channels, channelMask, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        unsafe
        {
            fixed (byte* d = dev)
            fixed (byte* m = mix)
            {
                try { return pc.SetDeviceFormat(endpointId, (nint)d, (nint)m); }
                catch (Exception e) { return e.HResult; }
            }
        }
    }

    /// The mix format of an endpoint (IPolicyConfig::GetMixFormat, read-only), for the measurements; null when
    /// unreadable.
    internal static (uint rate, int bits, bool isFloat, int channels, uint channelMask)? MixFormat(string endpointId)
    {
        if (PolicyConfig(out _) is not IPolicyConfig pc) return null;
        nint p = 0;
        try
        {
            if (pc.GetMixFormat(endpointId, out p) < 0 || p == 0) return null;
            int size = 18 + Marshal.ReadInt16(p, 16);
            var b = new byte[Math.Max(size, 18)];
            Marshal.Copy(p, b, 0, b.Length);
            return ParseWaveFormat(b);
        }
        catch (Exception) { return null; }
        finally { if (p != 0) Marshal.FreeCoTaskMem(p); }
    }

    /// WAVEFORMATEXTENSIBLE (40 bytes): wFormatTag 0xFFFE, nChannels, nSamplesPerSec, nAvgBytesPerSec, nBlockAlign,
    /// wBitsPerSample, cbSize 22, wValidBitsPerSample, dwChannelMask, SubFormat.
    internal static byte[] WaveFormatExtensible(uint rate, int bits, int channels, uint channelMask, Guid subFormat)
    {
        var b = new byte[40];
        int blockAlign = channels * bits / 8;
        BitConverter.TryWriteBytes(b.AsSpan(0), WAVE_FORMAT_EXTENSIBLE);
        BitConverter.TryWriteBytes(b.AsSpan(2), (ushort)channels);
        BitConverter.TryWriteBytes(b.AsSpan(4), rate);
        BitConverter.TryWriteBytes(b.AsSpan(8), rate * (uint)blockAlign);
        BitConverter.TryWriteBytes(b.AsSpan(12), (ushort)blockAlign);
        BitConverter.TryWriteBytes(b.AsSpan(14), (ushort)bits);
        BitConverter.TryWriteBytes(b.AsSpan(16), (ushort)22);
        BitConverter.TryWriteBytes(b.AsSpan(18), (ushort)bits);
        BitConverter.TryWriteBytes(b.AsSpan(20), channelMask);
        subFormat.TryWriteBytes(b.AsSpan(24));
        return b;
    }

    /// The snapshot's `realFormat` (§5.18): `"<Int(rate/1000)> kHz / <bits>-bit"`, 44 100 Hz → "44 kHz".
    public static string? DeviceFormatText(string endpointId) =>
        DeviceFormat(endpointId) is { } f
            ? (f.rate / 1000).ToString(CultureInfo.InvariantCulture) + " kHz / " + f.bits.ToString(CultureInfo.InvariantCulture) + "-bit"
            : null;

    /// One IMMNotificationClient for the whole app (≙ the three AudioObjectAddPropertyListenerBlock calls of start()).
    public static void RegisterEndpointNotifications(IMMNotificationClient client) =>
        DeviceEnumerator().RegisterEndpointNotificationCallback(client);

    /// The interface `T` activated on an endpoint (IMMDevice::Activate), as a wrapper of its own.
    internal static T? Activate<T>(string endpointId) where T : class =>
        Device(endpointId) is IMMDevice d ? WrapCom<T>(d.Activate(typeof(T).GUID, CLSCTX_ALL, IntPtr.Zero), unique: true) : null;

    static string? StringProperty(IMMDevice d, PROPERTYKEY key)
    {
        try
        {
            IPropertyStore store = d.OpenPropertyStore(STGM_READ);
            store.GetValue(key, out PROPVARIANT pv);
            try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p1) : null; }
            finally { _ = PropVariantClear(ref pv); }
        }
        catch (Exception) { return null; }
    }

    /// `{1}.USB\VID_…` → `USB\VID_…` (the `{n}.` prefix of the parent-instance property and of topology IDs).
    internal static string StripIdPrefix(string s)
    {
        if (s.StartsWith('{'))
        {
            int k = s.IndexOf("}.", StringComparison.Ordinal);
            if (k > 0) return s[(k + 2)..];
        }
        return s;
    }

    /// Fallback 1 of DeviceUid on its own (the render harness compares it with the primary source).
    internal static string? DeviceUidFromTopology(string endpointId) => Device(endpointId) is IMMDevice d ? TopologyParentInstanceId(d) : null;

    /// Fallback 1 of DeviceUid: the device topology's connector 0 names the adapter's device interface, e.g.
    /// `{2}.\\?\usb#vid_27ac&pid_1060&mi_01#8&1a2b3c4d&0&0001#{6994ad04-…}\elineoutwave`.
    static string? TopologyParentInstanceId(IMMDevice d)
    {
        IDeviceTopology? topo = null;
        try
        {
            topo = WrapCom<IDeviceTopology>(d.Activate(typeof(IDeviceTopology).GUID, CLSCTX_ALL, IntPtr.Zero), unique: true);
            return InstanceIdFromInterfacePath(topo.GetConnector(0).GetDeviceIdConnectedTo());
        }
        catch (Exception) { return null; }
        finally { ReleaseCom(topo); }
    }

    /// A device interface path back to its device instance ID: `\\?\usb#vid_…&mi_01#8&…&0001#{guid}\…` →
    /// `USB\VID_…&MI_01\8&…&0001` (the segments between the prefix and `#{`, `#` → `\`, upper case like the instance IDs).
    internal static string? InstanceIdFromInterfacePath(string path)
    {
        string s = StripIdPrefix(path);
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        int k = s.IndexOf("#{", StringComparison.Ordinal);
        return k > 0 ? s[..k].Replace('#', '\\').ToUpperInvariant() : null;
    }

    // MARK: Constants and Win32

    const uint DEVICE_STATE_ACTIVE = 0x1;
    const uint STGM_READ = 0x0;
    const uint CLSCTX_INPROC_SERVER = 0x1, CLSCTX_ALL = 0x17;
    const uint COINIT_MULTITHREADED = 0x0;
    const int E_NOTFOUND = unchecked((int)0x80070490);
    const int E_POINTER = unchecked((int)0x80004003);
    const ushort VT_LPWSTR = 31, VT_BLOB = 65;
    const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    /// CPolicyConfigClient (undocumented; the class behind the Sound control panel's "Set Default" and "Default Format").
    static readonly Guid CLSID_PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    /// Parent device instance ID with a `{1}.` prefix (the key measured in §2; not named in the SDK headers).
    static readonly PROPERTYKEY PKEY_DeviceParentInstanceId = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    /// The shared-mode device format (WAVEFORMATEX blob), documented.
    public static readonly PROPERTYKEY PKEY_AudioEngine_DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PROPVARIANT pvar);
}

// MARK: - Running state (≙ kAudioDevicePropertyDeviceIsRunningSomewhere)

/// Running detection of one render endpoint (docs/DESIGN.md §5.11): "running" means at least one audio session on it is
/// AudioSessionStateActive, i.e. a started stream, silent or not (the semantics of DeviceIsRunningSomewhere).
/// IAudioSessionNotification (new sessions) and IAudioSessionEvents (state changes) only enqueue a scan; the controller's
/// 1-s poll scans as a safety net. `runningChanged` is called only when the result differs from the last reported one,
/// otherwise every scan would cancel the 2-s wake. Controller thread only.
sealed class SessionWatcher
{
    readonly Action runningChanged;
    readonly SessionNotification created;
    readonly SessionEvents events;
    IAudioSessionManager2? manager;
    /// Sessions with our IAudioSessionEvents registered, by session instance identifier.
    readonly Dictionary<string, IAudioSessionControl2> watched = [];
    bool lastReported;

    /// The endpoint whose sessions are watched.
    public string? EndpointId { get; private set; }
    /// How many sessions have our IAudioSessionEvents (render harness).
    internal int WatchedCount => watched.Count;

    public SessionWatcher(Action<Action> enqueue, Action runningChanged)
    {
        this.runningChanged = runningChanged;
        created = new SessionNotification(() => enqueue(Scan));
        events = new SessionEvents(() => enqueue(Scan));
    }

    /// Watches the sessions of `endpointId` (null: none). A new endpoint instance gets a new session manager (§5.6),
    /// also under the same endpoint ID (`newInstance`: Windows keeps the ID across a USB re-enumeration); the old
    /// objects are released here, on the controller thread. A failed activation (manager null) is retried on the next
    /// call instead of being taken for attached.
    public void Attach(string? endpointId, bool newInstance = false)
    {
        if (!newInstance && endpointId == EndpointId && (endpointId == null || manager != null)) return;
        Detach();
        EndpointId = endpointId;
        if (endpointId != null)
        {
            try
            {
                manager = Activate<IAudioSessionManager2>(endpointId);
                if (manager != null)
                {
                    // GetSessionEnumerator and GetCount first, or no notifications arrive (§5.11).
                    IAudioSessionEnumerator e = WrapCom<IAudioSessionEnumerator>(manager.GetSessionEnumerator(), unique: true);
                    try { _ = e.GetCount(); } finally { ReleaseCom(e); }
                    manager.RegisterSessionNotification(created);
                }
            }
            catch (Exception) { ReleaseCom(manager); manager = null; }
        }
        Scan();
    }

    void Detach()
    {
        foreach (var c in watched.Values)
        {
            try { c.UnregisterAudioSessionNotification(events); } catch (Exception) { }
            ReleaseCom(c);
        }
        watched.Clear();
        if (manager != null)
        {
            try { manager.UnregisterSessionNotification(created); } catch (Exception) { }
            ReleaseCom(manager);
            manager = null;
        }
    }

    /// Looks at every session: notes the running state, watches new sessions, drops expired and vanished ones, and
    /// reports a change of the running state.
    public void Scan()
    {
        bool running = false;
        if (manager != null)
        {
            var seen = new HashSet<string>();
            bool complete = false;
            IAudioSessionEnumerator? e = null;
            try
            {
                e = WrapCom<IAudioSessionEnumerator>(manager.GetSessionEnumerator(), unique: true);
                int n = e.GetCount();
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 c = WrapCom<IAudioSessionControl2>(e.GetSession(i), unique: true);
                    bool keep = false;
                    try
                    {
                        AudioSessionState state = c.GetState();
                        if (state == AudioSessionState.Active) running = true;
                        if (state == AudioSessionState.Expired) continue;
                        string id = c.GetSessionInstanceIdentifier();
                        seen.Add(id);
                        if (!watched.ContainsKey(id))
                        {
                            c.RegisterAudioSessionNotification(events);
                            watched[id] = c;
                            keep = true;
                        }
                    }
                    catch (Exception) { }
                    finally { if (!keep) ReleaseCom(c); }
                }
                complete = true;
            }
            catch (Exception) { }
            finally { ReleaseCom(e); }
            if (complete)
            {
                foreach (string id in watched.Keys.Where(k => !seen.Contains(k)).ToArray())
                {
                    try { watched[id].UnregisterAudioSessionNotification(events); } catch (Exception) { }
                    ReleaseCom(watched[id]);
                    watched.Remove(id);
                }
            }
        }
        if (running != lastReported)
        {
            lastReported = running;
            runningChanged();
        }
    }

    /// ≙ reading DeviceIsRunningSomewhere: a live look at the session states, without reporting anything.
    public bool IsRunningNow()
    {
        if (manager == null) return false;
        IAudioSessionEnumerator? e = null;
        try
        {
            e = WrapCom<IAudioSessionEnumerator>(manager.GetSessionEnumerator(), unique: true);
            int n = e.GetCount();
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2 c = WrapCom<IAudioSessionControl2>(e.GetSession(i), unique: true);
                try { if (c.GetState() == AudioSessionState.Active) return true; }
                finally { ReleaseCom(c); }
            }
        }
        catch (Exception) { }
        finally { ReleaseCom(e); }
        return false;
    }
}

// MARK: - Endpoint volume (≙ kAudioDevicePropertyVolumeScalar of the real device's output elements)

/// IAudioEndpointVolume of one endpoint instance with its IAudioEndpointVolumeCallback (§5.7, §5.10, §6.4). Created and
/// used on the controller thread only; a new endpoint instance gets a new object (§5.6), the old one is released on the
/// controller thread. The callback only passes the notification on (`changed`, which only enqueues).
sealed class EndpointVolume : IDisposable
{
    readonly IAudioEndpointVolume volume;
    readonly VolumeCallback callback;
    bool registered;
    public string EndpointId { get; }

    EndpointVolume(string endpointId, IAudioEndpointVolume volume, VolumeCallback callback)
    {
        EndpointId = endpointId;
        this.volume = volume;
        this.callback = callback;
    }

    /// Activates IAudioEndpointVolume on `endpointId` and registers the change callback; null when that fails.
    public static EndpointVolume? Open(string endpointId, Action<VolumeNotification> changed)
    {
        IAudioEndpointVolume? v = null;
        try
        {
            v = Activate<IAudioEndpointVolume>(endpointId);
            if (v == null) return null;
            var ev = new EndpointVolume(endpointId, v, new VolumeCallback(changed));
            v.RegisterControlChangeNotify(ev.callback);
            ev.registered = true;
            return ev;
        }
        catch (Exception) { ReleaseCom(v); return null; }
    }

    public uint ChannelCount() { try { return volume.GetChannelCount(); } catch (Exception) { return 0; } }
    public float? ChannelScalar(uint ch) { try { return volume.GetChannelVolumeLevelScalar(ch); } catch (Exception) { return null; } }
    public float? MasterScalar() { try { return volume.GetMasterVolumeLevelScalar(); } catch (Exception) { return null; } }
    public float? ChannelDB(uint ch) { try { return volume.GetChannelVolumeLevel(ch); } catch (Exception) { return null; } }
    public float? MasterDB() { try { return volume.GetMasterVolumeLevel(); } catch (Exception) { return null; } }
    public bool? Muted() { try { return volume.GetMute() != 0; } catch (Exception) { return null; } }

    /// The writes, each with an event context (the app passes `ownGuid`, §5.7.1). Return the HRESULT.
    public int SetChannelScalar(uint ch, float level, in Guid context)
    {
        try { return volume.SetChannelVolumeLevelScalar(ch, level, context); } catch (Exception e) { return e.HResult; }
    }
    public int SetMasterScalar(float level, in Guid context)
    {
        try { return volume.SetMasterVolumeLevelScalar(level, context); } catch (Exception e) { return e.HResult; }
    }
    public int SetMute(bool mute, in Guid context)
    {
        try { return volume.SetMute(mute ? 1 : 0, context); } catch (Exception e) { return e.HResult; }
    }

    public void Dispose()
    {
        if (registered)
        {
            try { volume.UnregisterControlChangeNotify(callback); } catch (Exception) { }
            registered = false;
        }
        ReleaseCom(volume);
    }
}

/// AUDIO_VOLUME_NOTIFICATION_DATA: guidEventContext, bMuted, fMasterVolume, nChannels, afChannelVolumes[nChannels].
readonly record struct VolumeNotification(Guid EventContext, bool Muted, float Master, float[] Channels);

// MARK: - Callbacks (only enqueue, never block, §3.3)

[GeneratedComClass]
sealed partial class VolumeCallback(Action<VolumeNotification> changed) : IAudioEndpointVolumeCallback
{
    public void OnNotify(nint pNotify)
    {
        if (pNotify == 0) return;
        var ctx = Marshal.PtrToStructure<Guid>(pNotify);
        bool muted = Marshal.ReadInt32(pNotify, 16) != 0;
        float master = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pNotify, 20));
        int n = Math.Clamp(Marshal.ReadInt32(pNotify, 24), 0, 64);
        var channels = new float[n];
        for (int i = 0; i < n; i++) channels[i] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pNotify, 28 + 4 * i));
        changed(new VolumeNotification(ctx, muted, master, channels));
    }
}

/// ≙ the three system listeners of start(): device list, default output, default input; plus property changes for the
/// format listener (§5.8). Every device-list event is passed on the same way (§5.6), with the endpoint and whether it
/// stopped being active (`gone`: a state other than DEVICE_STATE_ACTIVE, or removed).
[GeneratedComClass]
sealed partial class EndpointNotifications(Action<string, bool> devicesChanged, Action<EDataFlow, ERole> defaultChanged, Action<string, PROPERTYKEY> propertyChanged) : IMMNotificationClient
{
    const uint DEVICE_STATE_ACTIVE = 0x1;
    public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => devicesChanged(pwstrDeviceId, dwNewState != DEVICE_STATE_ACTIVE);
    public void OnDeviceAdded(string pwstrDeviceId) => devicesChanged(pwstrDeviceId, false);
    public void OnDeviceRemoved(string pwstrDeviceId) => devicesChanged(pwstrDeviceId, true);
    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? pwstrDefaultDeviceId) => defaultChanged(flow, role);
    public void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => propertyChanged(pwstrDeviceId, key);
}

[GeneratedComClass]
sealed partial class SessionNotification(Action created) : IAudioSessionNotification
{
    /// The new session is not kept here (it would need an AddRef); the scan finds it.
    public void OnSessionCreated(nint newSession) => created();
}

[GeneratedComClass]
sealed partial class SessionEvents(Action stateChanged) : IAudioSessionEvents
{
    public void OnDisplayNameChanged(nint newDisplayName, nint eventContext) { }
    public void OnIconPathChanged(nint newIconPath, nint eventContext) { }
    public void OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext) { }
    public void OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel, nint eventContext) { }
    public void OnGroupingParamChanged(nint newGroupingParam, nint eventContext) { }
    public void OnStateChanged(AudioSessionState newState) => stateChanged();
    public void OnSessionDisconnected(int disconnectReason) => stateChanged();
}

// MARK: - COM declarations (mmdeviceapi.h, audiopolicy.h, devicetopology.h, propsys.h)

enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

[StructLayout(LayoutKind.Sequential)]
readonly struct PROPERTYKEY(Guid fmtid, uint pid) : IEquatable<PROPERTYKEY>
{
    public readonly Guid fmtid = fmtid;
    public readonly uint pid = pid;
    public bool Equals(PROPERTYKEY o) => fmtid == o.fmtid && pid == o.pid;
    public override bool Equals(object? o) => o is PROPERTYKEY k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(fmtid, pid);
    public static bool operator ==(PROPERTYKEY a, PROPERTYKEY b) => a.Equals(b);
    public static bool operator !=(PROPERTYKEY a, PROPERTYKEY b) => !a.Equals(b);
}

/// PROPVARIANT: vt, three reserved words, then a 16-byte (64-bit) or 8-byte (32-bit) union. VT_LPWSTR: p1 is the
/// string; VT_BLOB: cbSize in the low 32 bits of p1, pBlobData in p2.
[StructLayout(LayoutKind.Sequential)]
struct PROPVARIANT
{
    public ushort vt;
    public ushort reserved1, reserved2, reserved3;
    public nint p1;
    public nint p2;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
partial interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);
    [PreserveSig] int GetDevice(string pwstrId, out IMMDevice? ppDevice);
    void RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
partial interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint nDevice);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
partial interface IMMDevice
{
    /// Returns the requested interface pointer (void**); M2 activates only IAudioSessionManager2 and IDeviceTopology.
    nint Activate(in Guid iid, uint dwClsCtx, nint pActivationParams);
    IPropertyStore OpenPropertyStore(uint stgmAccess);
    string GetId();
    uint GetState();
}

/// Read side only: SetValue and Commit follow GetValue in the vtable and are not declared.
[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
partial interface IPropertyStore
{
    uint GetCount();
    PROPERTYKEY GetAt(uint iProp);
    void GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
partial interface IMMNotificationClient
{
    void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState);
    void OnDeviceAdded(string pwstrDeviceId);
    void OnDeviceRemoved(string pwstrDeviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? pwstrDefaultDeviceId);
    void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key);
}

/// IAudioSessionManager (first two slots) + IAudioSessionManager2 up to UnregisterSessionNotification.
[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
partial interface IAudioSessionManager2
{
    nint GetAudioSessionControl(nint audioSessionGuid, uint streamFlags);   // vtable layout only, not used
    nint GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags);     // vtable layout only, never called
    /// Returns IAudioSessionEnumerator*.
    nint GetSessionEnumerator();
    void RegisterSessionNotification(IAudioSessionNotification sessionNotification);
    void UnregisterSessionNotification(IAudioSessionNotification sessionNotification);
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
partial interface IAudioSessionEnumerator
{
    int GetCount();
    /// Returns IAudioSessionControl*.
    nint GetSession(int sessionCount);
}

/// IAudioSessionControl + IAudioSessionControl2 up to GetSessionInstanceIdentifier.
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
partial interface IAudioSessionControl2
{
    AudioSessionState GetState();
    nint GetDisplayName();                                 // not used
    void SetDisplayName(nint value, nint eventContext);    // vtable layout only, never called
    nint GetIconPath();                                    // not used
    void SetIconPath(nint value, nint eventContext);       // vtable layout only, never called
    Guid GetGroupingParam();                               // not used
    void SetGroupingParam(nint groupingParam, nint eventContext);   // vtable layout only, never called
    void RegisterAudioSessionNotification(IAudioSessionEvents newNotifications);
    void UnregisterAudioSessionNotification(IAudioSessionEvents newNotifications);
    nint GetSessionIdentifier();                           // not used
    string GetSessionInstanceIdentifier();
}

[GeneratedComInterface]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
partial interface IAudioSessionNotification
{
    void OnSessionCreated(nint newSession);
}

[GeneratedComInterface]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
partial interface IAudioSessionEvents
{
    void OnDisplayNameChanged(nint newDisplayName, nint eventContext);
    void OnIconPathChanged(nint newIconPath, nint eventContext);
    void OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);
    void OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel, nint eventContext);
    void OnGroupingParamChanged(nint newGroupingParam, nint eventContext);
    void OnStateChanged(AudioSessionState newState);
    void OnSessionDisconnected(int disconnectReason);
}

/// IAudioEndpointVolume up to GetVolumeRange (endpointvolume.h). The dB setters and the step methods are declared for the
/// vtable layout only and never called; the app writes scalars only.
[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
partial interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
    uint GetChannelCount();
    [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, nint pguidEventContext);              // vtable layout only, never called
    [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, in Guid pguidEventContext);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, nint pguidEventContext); // vtable layout only, never called
    [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, in Guid pguidEventContext);
    float GetChannelVolumeLevel(uint nChannel);
    float GetChannelVolumeLevelScalar(uint nChannel);
    [PreserveSig] int SetMute(int bMute, in Guid pguidEventContext);
    int GetMute();
    void GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    [PreserveSig] int VolumeStepUp(nint pguidEventContext);                                     // vtable layout only, never called
    [PreserveSig] int VolumeStepDown(nint pguidEventContext);                                   // vtable layout only, never called
    uint QueryHardwareSupport();
    void GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
partial interface IAudioEndpointVolumeCallback
{
    /// PAUDIO_VOLUME_NOTIFICATION_DATA, read by VolumeCallback.
    void OnNotify(nint pNotify);
}

/// IPolicyConfig (undocumented; IID of Windows 7 and later, CLSID CPolicyConfigClient) up to SetDefaultEndpoint. Only
/// GetMixFormat, GetDeviceFormat, SetDeviceFormat and SetDefaultEndpoint are used; the slots in between are declared for
/// the vtable layout only, with raw pointer parameters, and are never called. SetEndpointVisibility (the last slot) is
/// not declared.
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
partial interface IPolicyConfig
{
    /// WAVEFORMATEX** allocated with CoTaskMemAlloc.
    [PreserveSig] int GetMixFormat(string pszDeviceName, out nint ppFormat);
    [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, out nint ppFormat);
    [PreserveSig] int ResetDeviceFormat(nint pszDeviceName);                                    // vtable layout only, never called
    [PreserveSig] int SetDeviceFormat(string pszDeviceName, nint pEndpointFormat, nint mixFormat);
    [PreserveSig] int GetProcessingPeriod(nint pszDeviceName, int bDefault, nint pmftDefaultPeriod, nint pmftMinimumPeriod);   // not used
    [PreserveSig] int SetProcessingPeriod(nint pszDeviceName, nint pmftPeriod);                  // vtable layout only, never called
    [PreserveSig] int GetShareMode(nint pszDeviceName, nint pMode);                              // not used
    [PreserveSig] int SetShareMode(nint pszDeviceName, nint mode);                               // vtable layout only, never called
    [PreserveSig] int GetPropertyValue(nint pszDeviceName, int bFxStore, nint key, nint pv);    // not used
    [PreserveSig] int SetPropertyValue(nint pszDeviceName, int bFxStore, nint key, nint pv);    // vtable layout only, never called
    [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, ERole role);
}

[GeneratedComInterface]
[Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
partial interface IDeviceTopology
{
    uint GetConnectorCount();
    IConnector GetConnector(uint nIndex);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
partial interface IConnector
{
    int GetConnectorType();               // IConnector::GetType
    int GetDataFlow();
    void ConnectTo(nint connectTo);       // vtable layout only, never called (reserved for the system)
    void Disconnect();                    // vtable layout only, never called (reserved for the system)
    int IsConnected();
    nint GetConnectedTo();                // not used
    string GetConnectorIdConnectedTo();
    string GetDeviceIdConnectedTo();
}

// ≙ Helper/SpeakerDiscovery.swift.
//
// DNS-SD ("Bonjour") discovery of the LSX II, docs/DESIGN.md §5.16: WinRT DeviceWatcher over association endpoint
// services with the measured AQS (§2), callbacks on thread-pool threads. Decision M1 (§10.5): the alternative
// "DevQuery per P/Invoke" is no drop-in from .NET: DevCreateObjectQuery refuses every query whose callback is managed
// code with 0x8007007E, because it pins the module containing the callback and a managed entry point lies in no module
// (discovery measurement tool, not published). So the app keeps the WinRT projection (net10.0-windows10.0.22000.0).
//
// The rules (≙ the NetServiceBrowser/NetService delegate methods) live in SpeakerDiscovery and see only the
// backend-neutral events of IDnssdEvents; DnssdWatcher maps the DeviceWatcher events onto them.

using Windows.Devices.Enumeration;

// MARK: - DNS-SD ("Bonjour") discovery of the LSX II

static partial class Config
{
    /// The host to talk to right now: the override, else the discovered one, else null (still searching).
    public static string? SpeakerHost => SpeakerHostOverride ?? SpeakerDiscovery.shared.Host;
}

/// Finds the speaker on the local network so no hostname has to be configured. The LSX II advertises its
/// HTTP API as an `_http._tcp` DNS-SD service named "LSX II" whose host is `lsxii-<mac>.local`; both the
/// service name and the host prefix are accepted, so a speaker renamed in KEF Connect is still found. The
/// watcher keeps running: when the chosen service disappears the host is cleared and the next matching service is
/// used. Unlike macOS (main run loop) the callbacks come from the thread pool, so all state sits behind one lock.
// Design decision: Find the speaker automatically instead of shipping a default hostname — reason: a fixed
// default hostname is useless for anyone else's speaker. `speakerHost` stays as a manual override.
sealed class SpeakerDiscovery : IDnssdEvents
{
    public static readonly SpeakerDiscovery shared = new();

    /// Deviation (Windows, §5.16): the whole discovery state, not only the host, is guarded (≙ NSLock).
    private readonly Lock @lock = new();
    private readonly Action<string> log;
    private string? _host;
    private bool started;
    /// Fixed field, or the GC would collect the watcher (docs/DESIGN.md §5.16).
    private DnssdWatcher? watcher;
    private System.Threading.Timer? notFoundTimer;
    public const double notFoundAfter = 30;

    /// Every service currently announced, in the order of `Added` (≙ `advertised`).
    private readonly List<DnssdService> advertised = [];
    /// Services that were to be evaluated while their HostName was still empty; the next `Updated` that brings a
    /// HostName evaluates them (≙ `pending` + `didResolveAddress`; there is no resolve step, §5.16).
    private readonly HashSet<string> awaitingHost = [];
    private DnssdService? chosen;
    /// How many instances of each service (InstanceName + "." + Domain) are currently advertised, counted over
    /// distinct ids: the same speaker shows up once per network interface, and losing one must not drop the host.
    private readonly Dictionary<string, int> instances = [];
    /// Addition (A67): after an aborted watcher, the re-enumeration confirms or drops the chosen speaker.
    private bool restarting;
    private string? restartName;

    /// `log` replaces the app log (measurement tools only).
    internal SpeakerDiscovery(Action<string>? log = null) { this.log = log ?? (s => Log(s)); }

    /// Host name of the discovered speaker (`lsxii-….local`), or null while nothing matching is on the network.
    public string? Host { get { lock (@lock) return _host; } }

    /// The addresses of the chosen service (System.Devices.IpAddress, renewed by `Updated`) while `host` is its host
    /// name: connection candidates for Speaker (§5.15). Empty otherwise.
    public IReadOnlyList<string> AddressesFor(string host)
    {
        lock (@lock)
            return chosen != null && string.Equals(_host, host, StringComparison.OrdinalIgnoreCase) ? chosen.IpAddresses : [];
    }

    public void Start()
    {
        if (started) return;
        started = true;
        watcher = new DnssdWatcher(this);
        // Logged before the watcher starts: its callbacks come from the thread pool at once (macOS: only once the main
        // run loop turns, so `searching` always comes first there).
        log("speaker: searching for the LSX II via Bonjour");
        watcher.Start();
        // Networks that block mDNS would otherwise leave the app silent forever; say so once.
        notFoundTimer = new System.Threading.Timer(_ =>
        {
            if (Host == null) log($"speaker: no LSX II found via Bonjour after {(int)notFoundAfter} s; if mDNS is blocked set `speakerHost` (reg add HKCU\\{userKeyPath} /v speakerHost /d <host or IP> /f)");
        }, null, TimeSpan.FromSeconds(notFoundAfter), Timeout.InfiniteTimeSpan);
    }

    /// The speaker advertises as "LSX II" with host `lsxii-<mac>.local`; either identifies it (a renamed
    /// speaker keeps its host name). `host` is null while it is not known yet.
    public static bool Matches(string name, string? host) =>
        name.Contains("LSX II", StringComparison.CurrentCultureIgnoreCase)                 // ≙ localizedCaseInsensitiveContains
        || (host ?? "").ToLowerInvariant().StartsWith("lsxii-", StringComparison.Ordinal); // ≙ lowercased().hasPrefix

    private static string Key(DnssdService s) => s.InstanceName + "." + s.Domain;

    // MARK: IDnssdEvents (≙ NetServiceBrowserDelegate / NetServiceDelegate)

    public void Added(string id, IReadOnlyDictionary<string, object?> properties)
    {
        lock (@lock)
        {
            // Counted over distinct ids (§5.16): an Added for an id that is already announced only updates it.
            if (advertised.Exists(s => s.Id == id)) UpdatedLocked(id, properties);
            else AddedLocked(DnssdService.From(id, properties));
        }
    }

    private void AddedLocked(DnssdService service)
    {
        string k = Key(service);
        instances[k] = instances.GetValueOrDefault(k) + 1;
        advertised.Add(service);
        // Services with the speaker's name are always evaluated. Others only while no speaker is known yet (the
        // match may be on the host name); whatever is skipped is evaluated when the chosen speaker disappears
        // (`ResolveAdvertised`). macOS saves a resolve per flap this way; kept for the same decisions and logs.
        if (!(Matches(service.InstanceName, null) || chosen == null)) return;
        Resolve(service);
    }

    /// ≙ `resolve(_:)` + `netServiceDidResolveAddress(_:)`: DNS-SD on Windows reports the host name with the
    /// service (no resolve step, §5.16). Without one yet, the next `Updated` that brings it evaluates the service.
    private void Resolve(DnssdService service)
    {
        if (service.HostName.Length == 0) { awaitingHost.Add(service.Id); return; }
        awaitingHost.Remove(service.Id);
        string h = service.HostName;
        if (!instances.ContainsKey(Key(service)) || !Matches(service.InstanceName, h)) return;   // still announced
        string host = h.EndsWith('.') ? h[..^1] : h;
        if (restarting && _host != null)
        {
            // A67: the restarted watcher reports again what is still announced, in any order. Only the speaker chosen
            // before the abort is taken back (nothing changed, nothing logged); every other service waits for
            // EnumerationCompleted, so a second speaker reported first cannot replace one that is still there (DISC-06).
            if (chosen == null && string.Equals(host, _host, StringComparison.OrdinalIgnoreCase)) chosen = service;
            return;
        }
        if (chosen != null)   // keep the first speaker until it disappears; a changed host arrives as remove + add
        {
            if (Key(chosen) != Key(service)) log($"speaker: ignoring second match \"{service.InstanceName}\" at {host}; using {_host ?? "?"}");
            return;
        }
        chosen = service;
        _host = host;
        log($"speaker: found \"{service.InstanceName}\" at {host}");
    }

    /// Evaluate everything still announced: a second speaker (named or renamed) that was ignored or skipped
    /// while another one was chosen becomes the host now.
    private void ResolveAdvertised()
    {
        foreach (var s in advertised.ToArray()) Resolve(s);
    }

    public void Updated(string id, IReadOnlyDictionary<string, object?> properties)
    {
        lock (@lock) UpdatedLocked(id, properties);
    }

    private void UpdatedLocked(string id, IReadOnlyDictionary<string, object?> properties)
    {
        int i = advertised.FindIndex(s => s.Id == id);
        if (i < 0) return;
        DnssdService old = advertised[i], now = old.With(properties);
        // macOS sees a changed host as remove + add (§5.16); so does a changed name or domain (another key).
        if (Key(now) != Key(old) || (old.HostName.Length > 0 && now.HostName != old.HostName))
        {
            RemovedLocked(id);
            AddedLocked(now);
            return;
        }
        advertised[i] = now;
        if (chosen?.Id == id) chosen = now;   // new addresses for the connection candidates (§5.15)
        if (now.HostName.Length > 0 && awaitingHost.Contains(id)) Resolve(now);
    }

    public void Removed(string id)
    {
        lock (@lock) RemovedLocked(id);
    }

    private void RemovedLocked(string id)
    {
        int i = advertised.FindIndex(s => s.Id == id);
        if (i < 0) return;
        DnssdService service = advertised[i];
        advertised.RemoveAt(i);
        awaitingHost.Remove(id);
        string k = Key(service);
        instances[k] = Math.Max(0, instances.GetValueOrDefault(k, 1) - 1);
        if (instances[k] == 0) instances.Remove(k);
        if (chosen != null && Key(chosen) == k)
        {
            if (!instances.ContainsKey(k))
            {
                chosen = null;
                _host = null;
                log($"speaker: {service.InstanceName} disappeared from Bonjour");
                ResolveAdvertised();
            }
            else if (chosen.Id == id)
            {
                // Another instance of the same service remains (another network interface). macOS keeps the host and
                // the removed NetService; here the remaining instance supplies the addresses from now on.
                chosen = advertised.First(s => Key(s) == k);
            }
        }
    }

    public void EnumerationCompleted()
    {
        lock (@lock)
        {
            if (!restarting) return;
            restarting = false;
            if (chosen == null && _host != null)   // A67: the speaker did not come back with the restarted watcher
            {
                _host = null;
                log($"speaker: {restartName} disappeared from Bonjour");
                ResolveAdvertised();
            }
        }
    }

    /// Addition (A67): the watcher was aborted and starts again; its new enumeration reports everything still
    /// announced. The host stays until that enumeration has completed.
    public void Aborted()
    {
        log("speaker discovery: watcher aborted, restarting");
        lock (@lock)
        {
            restartName = chosen?.InstanceName ?? restartName;
            restarting = _host != null;
            chosen = null;
            advertised.Clear();
            awaitingHost.Clear();
            instances.Clear();
        }
    }

    /// Addition (A67): starting the aborted watcher again failed; DnssdWatcher tries again after `retryIn`. The host stays
    /// meanwhile (macOS keeps it too when its browser stops searching: `didNotSearch` is not handled there).
    public void RestartFailed(Exception error, TimeSpan retryIn) =>
        log($"speaker discovery: watcher restart failed (0x{error.HResult:X8} {error.GetType().Name}: {error.Message.Trim()}), retrying in {((int)retryIn.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)} s");
}

// MARK: - DNS-SD services and their watcher

/// The properties the discovery requests (docs/DESIGN.md §5.16), by their canonical names.
sealed record DnssdService(string Id, string InstanceName, string Domain, string HostName, int? PortNumber, string[] IpAddresses, string? NetworkAdapterId)
{
    public const string pHostName = "System.Devices.Dnssd.HostName";
    public const string pInstanceName = "System.Devices.Dnssd.InstanceName";
    public const string pDomain = "System.Devices.Dnssd.Domain";
    public const string pPortNumber = "System.Devices.Dnssd.PortNumber";
    public const string pIpAddress = "System.Devices.IpAddress";
    public const string pNetworkAdapterId = "System.Devices.Dnssd.NetworkAdapterId";
    public static readonly string[] requestedProperties = [pHostName, pInstanceName, pDomain, pPortNumber, pIpAddress, pNetworkAdapterId];

    public static DnssdService From(string id, IReadOnlyDictionary<string, object?> p) => new DnssdService(id, "", "", "", null, [], null).With(p);

    /// ≙ DeviceInformation.Update: the properties an event reports replace the old values; the others stay.
    public DnssdService With(IReadOnlyDictionary<string, object?> p) => this with
    {
        InstanceName = p.TryGetValue(pInstanceName, out var n) ? n as string ?? "" : InstanceName,
        Domain = p.TryGetValue(pDomain, out var d) ? d as string ?? "" : Domain,
        HostName = p.TryGetValue(pHostName, out var h) ? h as string ?? "" : HostName,
        PortNumber = p.TryGetValue(pPortNumber, out var port) ? port is long l ? (int)l : null : PortNumber,
        IpAddresses = p.TryGetValue(pIpAddress, out var a) ? a as string[] ?? [] : IpAddresses,
        NetworkAdapterId = p.TryGetValue(pNetworkAdapterId, out var na) ? na?.ToString() : NetworkAdapterId,
    };
}

/// The events of a DNS-SD watcher (DeviceWatcher Added, Updated, Removed, EnumerationCompleted and Stopped with
/// status Aborted). Properties by canonical name; strings as string, string lists as string[], integers as long,
/// GUIDs as Guid; an empty property is null. `Updated` carries the properties the watcher reports.
interface IDnssdEvents
{
    void Added(string id, IReadOnlyDictionary<string, object?> properties);
    void Updated(string id, IReadOnlyDictionary<string, object?> properties);
    void Removed(string id);
    void EnumerationCompleted();
    void Aborted();
    /// Starting the watcher again after Aborted threw; the next try follows after `retryIn`.
    void RestartFailed(Exception error, TimeSpan retryIn) { }
}

/// Browses `_http._tcp` in `local` with the WinRT DeviceWatcher as measured (docs/DESIGN.md §2): association endpoint
/// services of the DNS-SD protocol, the six properties of DnssdService. Works unpackaged; events come on thread-pool
/// threads. An aborted watcher is started again (Addition, A67): at once after a run whose enumeration completed;
/// when it aborts again before EnumerationCompleted, or Start() throws, the next start waits `restartDelayMin`,
/// doubling up to `restartDelayMax` (reset by EnumerationCompleted), so a watcher that keeps aborting cannot flood
/// the log (Addition, A70; both values from the macOS time constants).
sealed class DnssdWatcher
{
    public const string aqs = "System.Devices.AepService.ProtocolId:={4526e8c1-8aac-4153-9b16-55e86ada0e54} AND System.Devices.Dnssd.ServiceName:=\"_http._tcp\" AND System.Devices.Dnssd.Domain:=\"local\"";
    public static readonly TimeSpan restartDelayMin = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan restartDelayMax = TimeSpan.FromSeconds(600);
    private readonly DeviceWatcher watcher;
    private readonly IDnssdEvents events;
    private readonly Lock gate = new();
    private TimeSpan nextRestartDelay = TimeSpan.Zero;
    /// Fixed field, or the GC would collect the pending restart.
    private System.Threading.Timer? restartTimer;
    private bool stopped;

    public DnssdWatcher(IDnssdEvents events)
    {
        this.events = events;
        watcher = DeviceInformation.CreateWatcher(aqs, DnssdService.requestedProperties, DeviceInformationKind.AssociationEndpointService);
        watcher.Added += (_, info) => events.Added(info.Id, Properties(info.Properties));
        watcher.Updated += (_, update) => events.Updated(update.Id, Properties(update.Properties));
        watcher.Removed += (_, update) => events.Removed(update.Id);
        watcher.EnumerationCompleted += (_, _) =>
        {
            lock (gate) nextRestartDelay = TimeSpan.Zero;
            events.EnumerationCompleted();
        };
        watcher.Stopped += (w, _) =>
        {
            if (w.Status != DeviceWatcherStatus.Aborted) return;
            events.Aborted();
            ScheduleRestart();
        };
    }

    public void Start() => watcher.Start();

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            restartTimer?.Dispose();
            restartTimer = null;
        }
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
    }

    /// Starts the watcher again after the current delay, from a timer (not inside the watcher's own event); returns
    /// that delay.
    private TimeSpan ScheduleRestart()
    {
        lock (gate)
        {
            TimeSpan delay = nextRestartDelay;
            nextRestartDelay = delay == TimeSpan.Zero ? restartDelayMin : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, restartDelayMax.Ticks));
            if (stopped) return delay;
            restartTimer?.Dispose();
            restartTimer = new System.Threading.Timer(_ => Restart(), null, delay, Timeout.InfiniteTimeSpan);
            return delay;
        }
    }

    private void Restart()
    {
        lock (gate) if (stopped) return;
        try { watcher.Start(); }
        catch (Exception e)
        {
            TimeSpan retryIn = ScheduleRestart();
            events.RestartFailed(e, retryIn);
        }
    }

    /// The six requested properties in the IDnssdEvents conventions (the watcher adds a few of its own, ignored).
    private static Dictionary<string, object?> Properties(IReadOnlyDictionary<string, object> p)
    {
        var d = new Dictionary<string, object?>();
        foreach (var name in DnssdService.requestedProperties)
        {
            if (!p.TryGetValue(name, out var v)) continue;
            d[name] = v switch
            {
                null => null,
                string or string[] or Guid or bool or long => v,
                byte b => (long)b, ushort u => (long)u, short s => (long)s, uint u => (long)u, int i => (long)i, ulong ul => (long)ul,
                _ => v.ToString(),
            };
        }
        return d;
    }
}

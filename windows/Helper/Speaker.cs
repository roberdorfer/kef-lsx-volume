// ≙ Helper/Speaker.swift.
//
// The speaker's local HTTP API (docs/DESIGN.md §5.17), failure counting and session reset (§5.15). Requests and their
// answers run on thread-pool threads; the callbacks (`done`) are invoked there, and the controller re-queues them
// (≙ the URLSession delegate queue + `queue.async`).
//
// M1–M3 (§16) called only GET (getVolume, getSource); since M4 the send path calls setVolume and the menu's "Wake
// speaker / source USB" calls setSource (POSTs).

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// MARK: - Speaker API

sealed class Speaker
{
    /// ≙ makeSession(): an "ephemeral" HttpClient (no cookies, no cache, no system proxy) with a 3 s budget for name
    /// resolution, connection and answer. UseProxy = false: macOS sends `*.local` past the system proxy (default
    /// exception list), while the Windows exception `<local>` only covers names without a dot (§5.15).
    /// Connections go through `connector` (Happy-Eyeballs replacement, §5.15). Also used by the O17 measurement tool (not published).
    /// Headers as close to URLSession's defaults as .NET gets: `Accept: */*` and, through AutomaticDecompression,
    /// `Accept-Encoding: gzip, deflate, br`; URLSession's own User-Agent and Accept-Language have no Windows
    /// equivalent and are not imitated. Redirects are followed as by URLSession (.NET's default); a GET only ever
    /// becomes another GET.
    internal static HttpClient MakeSession(SpeakerConnector connector)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            // .NET runs connection attempts detached from the request that started them; the budget holds for them too.
            ConnectTimeout = SpeakerConnector.budget,
            ConnectCallback = connector.ConnectAsync,
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = SpeakerConnector.budget };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    /// Candidate addresses and the last good one survive a session reset (they are knowledge about the speaker,
    /// not connection state).
    private readonly SpeakerConnector connector = new();
    private HttpClient _session;

    public Speaker() { _session = MakeSession(connector); }

    /// The current HttpClient. Requests run on the controller thread while `ResetSession` may replace and dispose the
    /// client on a thread-pool thread (after three failures); the client is taken under the same lock (≙ `task(_:)`).
    /// A request that took the old client just before the reset ends with ObjectDisposedException, not counted.
    private HttpClient Session() { lock (failureLock) return _session; }

    /// Called (on an arbitrary thread) when three real failures follow a session reset without any success in
    /// between, i.e. a fresh HttpClient did not help. The controller decides what to do.
    public Action? onPersistentFailure = null;

    /// Replace the HttpClient. A long-running macOS helper was seen failing every speaker request within ~150 ms for
    /// minutes right after its login session became the console again (fast user switching), while curl from the
    /// same user reached the speaker; a restart cured it. Whether Windows has that problem is unknown; the mechanism
    /// costs nothing (§5.15). The controller calls this when the session becomes console, and Speaker calls it itself
    /// after three real consecutive failures (cancellations by a previous reset are not counted).
    public void ResetSession(string reason)
    {
        HttpClient old;
        lock (failureLock)
        {
            old = _session;
            _session = MakeSession(connector);
            consecutiveFailures = 0;
            resetsSinceSuccess += 1;
        }
        Log($"speaker: new network session ({reason})");
        // ≙ invalidateAndCancel(): running requests of the old client end as cancelled (not counted).
        old.CancelPendingRequests();
        old.Dispose();
    }

    /// `http://<host><path>?<query>`, the query built literally (Uri.EscapeDataString would turn `:` into `%3A`);
    /// null while no host is known (not discovered yet) or the host is not usable in a URL. An IPv6 override gets
    /// its brackets from UriBuilder.
    public Uri? Url(string path, (string name, string value)[] query)
    {
        if (Config.SpeakerHost is not string host) return null;   // not discovered yet
        try
        {
            return new UriBuilder
            {
                Scheme = "http",
                Host = host,
                Path = path,
                Query = query.Length == 0 ? "" : string.Join("&", query.Select(q => q.name + "=" + q.value)),
            }.Uri;
        }
        catch (UriFormatException) { return null; }
    }

    public void GetVolume(Action<int?> done)
    {
        if (Url("/api/getData", [("path", "player:volume"), ("roles", "value")]) is not Uri u) { done(null); return; }
        DataTask(new HttpRequestMessage(HttpMethod.Get, u), (data, _, err) =>
        {
            if (data == null || ParseVolume(data) is not int v) { NoteFailure("getVolume", err); done(null); return; }
            NoteSuccess();
            done(v);
        });
    }

    public void SetVolume(int v, Action<bool> done) =>
        SetData("player:volume", "{\"type\":\"i32_\",\"i32_\":" + v.ToString(CultureInfo.InvariantCulture) + "}", done);

    /// Physical source: wifi, bluetooth, tv, optical, analog, usb, standby.
    public void SetSource(string source, Action<bool> done) =>
        SetData("settings:/kef/play/physicalSource", "{\"type\":\"kefPhysicalSource\",\"kefPhysicalSource\":\"" + source + "\"}", done);

    public void GetSource(Action<string?> done)
    {
        if (Url("/api/getData", [("path", "settings:/kef/play/physicalSource"), ("roles", "value")]) is not Uri u) { done(null); return; }
        DataTask(new HttpRequestMessage(HttpMethod.Get, u), (data, _, err) =>
        {
            if (data == null || !ParseSource(data, out string? source)) { NoteFailure("getSource", err); done(null); return; }
            NoteSuccess();
            done(source);
        });
    }

    private void SetData(string path, string valueJSON, Action<bool> done)
    {
        // The speaker accepts writes only as POST with a JSON body (GET answers "Invalid method!").
        if (Url("/api/setData", []) is not Uri u) { done(false); return; }
        var req = new HttpRequestMessage(HttpMethod.Post, u)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"path\":\"" + path + "\",\"roles\":\"value\",\"value\":" + valueJSON + "}")),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");   // no "; charset=utf-8" (§5.17)
        DataTask(req, (data, status, err) =>
        {
            // player:volume answers "true"; settings paths echo {"value":…,"timestamp":…} (even for values the
            // speaker then ignores, so callers that care read the setting back).
            string body = data != null ? Encoding.UTF8.GetString(data) : "";
            bool ok = err == null && status == 200 && (body.StartsWith("true", StringComparison.Ordinal) || body.StartsWith("{\"value\"", StringComparison.Ordinal));
            if (ok) NoteSuccess(); else NoteFailure("setData " + path, err, status, body);
            done(ok);
        });
    }

    /// ≙ `dataTask(with:completionHandler:)`: (body, HTTP status, error). The status is not checked by the GETs;
    /// an answer with any status is data, only a failed request is an error.
    private void DataTask(HttpRequestMessage req, Action<byte[]?, int?, Exception?> completion)
    {
        HttpClient session = Session();
        _ = Send(session, req, completion);
    }

    private static async Task Send(HttpClient session, HttpRequestMessage req, Action<byte[]?, int?, Exception?> completion)
    {
        byte[]? data = null;
        int? status = null;
        Exception? err = null;
        try
        {
            using (req)
            using (var resp = await session.SendAsync(req).ConfigureAwait(false))
            {
                status = (int)resp.StatusCode;
                data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) { err = e; data = null; }
        completion(data, status, err);
    }

    // MARK: Parsing (≙ JSONSerialization + `as? [[String: Any]]`)

    /// Success only for a JSON array of objects whose first object has an integer `i32_`.
    internal static int? ParseVolume(byte[] data)
    {
        if (ArrayOfObjects(data) is not { } arr || arr.Length == 0) return null;
        if (!arr[0].TryGetProperty("i32_", out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt32(out int i)) return i;
        // NSNumber bridging also gives an Int for an integral double (15.0).
        return v.TryGetDouble(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;
    }

    /// Success for any JSON array of objects; the source is the first object's `kefPhysicalSource` string, if any.
    internal static bool ParseSource(byte[] data, out string? source)
    {
        source = null;
        if (ArrayOfObjects(data) is not { } arr) return false;
        if (arr.Length > 0 && arr[0].TryGetProperty("kefPhysicalSource", out var s) && s.ValueKind == JsonValueKind.String) source = s.GetString();
        return true;
    }

    private static JsonElement[]? ArrayOfObjects(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
            return items.All(e => e.ValueKind == JsonValueKind.Object) ? items : null;
        }
        catch (JsonException) { return null; }
    }

    // MARK: Failures

    // Log the underlying error of a failed speaker request, at most once per 10 s per kind, so the log says WHY
    // the speaker is unreachable (name resolution, refused, timeout, odd reply) instead of only "failed". The caller
    // still logs the failed action.
    private readonly Dictionary<string, DateTime> lastFailureLog = [];
    private int consecutiveFailures = 0;
    private int resetsSinceSuccess = 0;
    private readonly Lock failureLock = new();

    private void NoteSuccess()
    {
        lock (failureLock) { consecutiveFailures = 0; resetsSinceSuccess = 0; }
    }

    private void NoteFailure(string what, Exception? err, int? status = null, string body = "")
    {
        bool cancelled = IsOwnCancellation(err);
        bool reset = false, escalate = false, throttled;
        lock (failureLock)
        {
            if (!cancelled)
            {
                consecutiveFailures += 1;
                if (consecutiveFailures >= 3)
                {
                    if (resetsSinceSuccess == 0) reset = true; else { consecutiveFailures = 0; escalate = true; }
                }
            }
            DateTime now = DateTime.UtcNow;
            throttled = lastFailureLog.TryGetValue(what, out DateTime last) && (now - last).TotalSeconds < 10;
            if (!throttled) lastFailureLog[what] = now;
        }
        if (!throttled)
        {
            string detail = err != null ? ErrorDetail(err) : "no error";
            if (status is int st) detail += ", HTTP " + st.ToString(CultureInfo.InvariantCulture);
            if (body.Length > 0) detail += ", body: " + Prefix(body, 80);
            Log($"speaker {what} error: {detail}");
        }
        if (reset) ResetSession("3 consecutive failures");
        if (escalate) onPersistentFailure?.Invoke();
    }

    /// Not counted: only the own cancellations by ResetSession (§5.15): OperationCanceledException without a
    /// timeout, ObjectDisposedException. A timeout (TaskCanceledException with TimeoutException, ≙ −1001) counts.
    internal static bool IsOwnCancellation(Exception? err)
    {
        for (var e = err; e != null; e = e.InnerException) if (e is ObjectDisposedException) return true;
        return err is OperationCanceledException && err.InnerException is not TimeoutException;
    }

    /// Deviation (Windows, §5.15): `<type> <HttpRequestError>/<SocketError> (<code>): <message>` instead of the
    /// NSError domain and code. A timeout reads `TaskCanceledException Timeout: …`.
    internal static string ErrorDetail(Exception err)
    {
        var sb = new StringBuilder(err.GetType().Name);
        if (err is HttpRequestException h) sb.Append(' ').Append(h.HttpRequestError);
        else if (err is OperationCanceledException && err.InnerException is TimeoutException) sb.Append(" Timeout");
        SocketException? se = null;
        for (var e = err.InnerException; e != null && se == null; e = e.InnerException) se = e as SocketException;
        if (err is SocketException direct) se = direct;
        if (se != null) sb.Append(err is HttpRequestException ? "/" : " ").Append(se.SocketErrorCode).Append(" (").Append(se.ErrorCode.ToString(CultureInfo.InvariantCulture)).Append(')');
        sb.Append(": ").Append(err.Message);
        return sb.ToString();
    }

    /// ≙ Swift `prefix(80)` on a String: the first 80 characters (grapheme clusters).
    private static string Prefix(string s, int n)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        int count = 0, end = 0;
        while (count < n && e.MoveNext()) { count++; end = e.ElementIndex + ((string)e.Current).Length; }
        return s[..end];
    }
}

// MARK: - Connection setup (≙ URLSession's Happy Eyeballs and waitsForConnectivity)

/// ConnectCallback of the speaker's HttpClient (adapted, docs/DESIGN.md §5.15, A43). .NET connects the addresses of
/// a name one after another without Happy Eyeballs (dotnet/runtime #26177), and `getaddrinfo` fails here in bursts
/// longer than the budget (§2). So:
///  - candidates in this order, without duplicates: the last good address; IPv4, then the other addresses of the
///    chosen DNS-SD service (renewed by `Updated`); the resolver's answer as it arrives;
///  - attempts staggered by 250 ms (RFC 8305), the next one at once when an attempt fails; the first that connects
///    wins, the others are dropped;
///  - when the resolver delivers nothing and no known address answers, it resolves again every 250 ms (replaces
///    `waitsForConnectivity`); all within the 3 s budget, which counts as one failure at the end;
///  - when a known address answers, a resolver failure does not count. URL and Host header keep the host name.
sealed class SpeakerConnector
{
    public static readonly TimeSpan budget = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan stagger = TimeSpan.FromMilliseconds(250);

    private readonly Lock @lock = new();
    private (string host, IPAddress address)? lastGood;

    /// The DNS-SD addresses of the chosen service for a host name. Replaced by measurements only.
    internal Func<string, IReadOnlyList<string>> dnssdAddresses = SpeakerDiscovery.shared.AddressesFor;
    /// Receives one record per connection setup, after the resolver of that setup has finished too. Null in the app;
    /// the O17 measurement tool (not published) sets it.
    internal Action<ConnectTrace>? trace = null;

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;
        var t = new ConnectTrace(host, DateTime.UtcNow, Stopwatch.StartNew());
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        CancellationToken ct = cts.Token;
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);   // cancelled when one attempt has won

        var candidates = new List<IPAddress>();
        void Candidate(IPAddress a, string source)
        {
            if (candidates.Contains(a)) return;
            candidates.Add(a);
            t.candidates.Add(new(a.ToString(), source, t.Ms));
        }
        lock (@lock)
            if (lastGood is { } good && string.Equals(good.host, host, StringComparison.OrdinalIgnoreCase)) Candidate(good.address, "last");
        var dnssd = dnssdAddresses(host).Select(s => IPAddress.TryParse(s, out var a) ? a : null).OfType<IPAddress>().ToArray();
        foreach (var a in dnssd.Where(a => a.AddressFamily == AddressFamily.InterNetwork)) Candidate(a, "dnssd");
        foreach (var a in dnssd.Where(a => a.AddressFamily != AddressFamily.InterNetwork)) Candidate(a, "dnssd");
        bool literal = IPAddress.TryParse(host, out IPAddress? literalAddress);
        if (literal) Candidate(literalAddress!, "literal");

        Task<IPAddress[]>? resolving = literal ? null : Resolve(host, t, ct);
        bool resolverDelivered = literal;    // the latest resolve returned addresses
        Exception? resolverError = null;
        Task? retry = null;                  // the 250 ms pause before resolving again
        var attempts = new List<(Task<Socket> task, IPAddress address)>();
        var allAttempts = new List<Task>();
        Task staggerElapsed = Task.CompletedTask;
        int next = 0;
        Exception? lastError = null;
        Task budgetOver = Task.Delay(Timeout.Infinite, ct);
        try
        {
            while (true)
            {
                if (next < candidates.Count && staggerElapsed.IsCompleted)
                {
                    IPAddress a = candidates[next++];
                    var attempt = ConnectOne(a, port, t, attemptCts.Token);
                    attempts.Add((attempt, a));
                    allAttempts.Add(attempt);
                    staggerElapsed = Task.Delay(stagger, ct);
                }
                if (attempts.Count == 0 && next >= candidates.Count && resolving == null && retry == null)
                {
                    if (resolverDelivered) throw lastError ?? resolverError ?? new SocketException((int)SocketError.HostNotFound);
                    retry = Task.Delay(stagger, ct);   // nothing answers and the resolver had nothing: resolve again
                }
                var waits = new List<Task>(attempts.Count + 4) { budgetOver };
                waits.AddRange(attempts.Select(x => x.task));
                if (resolving != null) waits.Add(resolving);
                if (retry != null) waits.Add(retry);
                if (next < candidates.Count) waits.Add(staggerElapsed);
                Task done = await Task.WhenAny(waits).ConfigureAwait(false);

                if (done == budgetOver)
                {
                    cancellationToken.ThrowIfCancellationRequested();   // the handler's own timeout or cancellation
                    // The cause stays in the exception (message and inner exception), so the failure detail still says
                    // why no address answered (refused, name resolution, …).
                    var why = new List<string>(2);
                    if (lastError != null) why.Add("last connect error: " + Speaker.ErrorDetail(lastError));
                    if (resolverError != null) why.Add("resolver: " + Speaker.ErrorDetail(resolverError));
                    throw new TimeoutException(
                        $"no address of {host} answered within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s" + (why.Count > 0 ? " (" + string.Join("; ", why) + ")" : ""),
                        lastError ?? resolverError);
                }
                int i = attempts.FindIndex(x => x.task == done);
                if (i >= 0)
                {
                    var (task, address) = attempts[i];
                    attempts.RemoveAt(i);
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        Socket socket = task.Result;
                        lock (@lock) lastGood = (host, address);
                        t.winner = address.ToString();
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    lastError = task.Exception?.InnerException ?? lastError;
                    staggerElapsed = Task.CompletedTask;   // a failed attempt starts the next one at once
                }
                else if (done == resolving)
                {
                    if (resolving.Status == TaskStatus.RanToCompletion)
                    {
                        resolverDelivered = resolving.Result.Length > 0;
                        foreach (var a in resolving.Result) Candidate(a, "resolver");
                    }
                    else { resolverDelivered = false; resolverError = resolving.Exception?.InnerException ?? resolverError; }
                    resolving = null;
                }
                else if (done == retry)
                {
                    retry = null;
                    resolving = Resolve(host, t, ct);
                }
            }
        }
        catch (Exception e)
        {
            t.error = Speaker.ErrorDetail(e);
            throw;
        }
        finally
        {
            t.connectMs = t.Ms;
            t.withinBudget = t.connectMs <= budget.TotalMilliseconds;
            // Losing attempts are dropped: cancelled, and a socket that connected in the meantime is closed.
            attemptCts.Cancel();
            foreach (var (task, _) in attempts)
                _ = task.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); }, TaskScheduler.Default);
            // The resolver of this setup may still run (it is not needed any more, but its answer is part of the
            // trace); it ends within the budget. Then everything is released and the trace is complete.
            var outstanding = new List<Task>(allAttempts);
            if (resolving != null) outstanding.Add(resolving);
            _ = Task.WhenAll(outstanding).ContinueWith(_ =>
            {
                cts.Cancel();   // ends the budget and stagger delays
                attemptCts.Dispose();
                cts.Dispose();
                if (trace is { } sink) sink(t);
            }, TaskScheduler.Default);
        }
    }

    private static async Task<IPAddress[]> Resolve(string host, ConnectTrace t, CancellationToken ct)
    {
        var r = new ConnectTrace.ResolveRecord(t.Ms);
        t.resolves.Add(r);
        try
        {
            IPAddress[] result = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            r.addresses = result.Select(a => a.ToString()).ToArray();
            return result;
        }
        catch (Exception e) { r.error = Speaker.ErrorDetail(e); throw; }
        finally { r.endMs = t.Ms; }
    }

    private static async Task<Socket> ConnectOne(IPAddress address, int port, ConnectTrace t, CancellationToken ct)
    {
        var r = new ConnectTrace.AttemptRecord(address.ToString(), t.Ms);
        t.attempts.Add(r);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            r.ok = true;
            return socket;
        }
        catch (Exception e)
        {
            socket.Dispose();
            r.error = e is OperationCanceledException ? "cancelled" : Speaker.ErrorDetail(e);
            throw;
        }
        finally { r.endMs = t.Ms; }
    }
}

/// What one connection setup did (O17). Times in ms since the ConnectCallback started.
sealed class ConnectTrace(string host, DateTime startUtc, Stopwatch clock)
{
    public string host = host;
    public DateTime startUtc = startUtc;
    public double Ms => clock.Elapsed.TotalMilliseconds;
    public readonly List<CandidateRecord> candidates = [];
    public readonly List<AttemptRecord> attempts = [];
    public readonly List<ResolveRecord> resolves = [];
    public string? winner;
    public string? error;
    /// When the ConnectCallback returned or threw, and whether that was within the 3 s budget.
    public double connectMs;
    public bool withinBudget;

    public sealed record CandidateRecord(string address, string source, double atMs);
    public sealed class AttemptRecord(string address, double startMs)
    {
        public string address = address;
        public double startMs = startMs;
        public double endMs;
        public bool ok;
        public string? error;
    }
    public sealed class ResolveRecord(double startMs)
    {
        public double startMs = startMs;
        public double endMs;
        public string[]? addresses;
        public string? error;
    }
}

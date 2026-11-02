import Foundation

// MARK: - Speaker API

final class Speaker {
    private static func makeSession() -> URLSession {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForResource = 3
        // Observed: a freshly started helper got NSURLErrorNotConnectedToInternet (-1009) for
        // the LAN speaker right after launch; waiting for connectivity (within the 3 s budget) instead of
        // failing at once covers that transient.
        c.waitsForConnectivity = true
        return URLSession(configuration: c)
    }
    private var _session = Speaker.makeSession()
    /// Create a task on the current URLSession. Requests run on the controller queue while `resetSession`
    /// may replace and invalidate the session on a URLSession callback thread (after three failures);
    /// creating a task on an invalidated session throws, so the task is made under the same lock.
    private func task(_ make: (URLSession) -> URLSessionTask) -> URLSessionTask {
        failureLock.lock(); defer { failureLock.unlock() }
        return make(_session)
    }

    /// Called (on an arbitrary thread) when three real failures follow a session reset without any
    /// success in between, i.e. a fresh URLSession did not help. The controller decides what to do.
    var onPersistentFailure: (() -> Void)?

    /// Replace the URLSession. A long-running helper instance was seen failing every speaker
    /// request within ~150 ms for minutes right after its login session became the console again
    /// (fast user switching), while curl from the same user reached the speaker; a restart cured it.
    /// A fresh session resets CFNetwork's per-session connection and path state, so the controller
    /// calls this when the session becomes console, and Speaker calls it itself after three real
    /// consecutive failures (cancellations from a previous reset are not counted).
    func resetSession(reason: String) {
        failureLock.lock(); defer { failureLock.unlock() }
        _session.invalidateAndCancel()
        _session = Speaker.makeSession()
        consecutiveFailures = 0
        resetsSinceSuccess += 1
        log("speaker: new network session (\(reason))")
    }

    func url(_ path: String, _ query: [String: String]) -> URL? {
        guard let host = Config.speakerHost else { return nil }   // not discovered yet
        var comps = URLComponents()
        comps.scheme = "http"
        comps.host = host
        comps.path = path
        comps.queryItems = query.isEmpty ? nil : query.map { URLQueryItem(name: $0.key, value: $0.value) }
        return comps.url
    }

    func getVolume(_ done: @escaping (Int?) -> Void) {
        guard let u = url("/api/getData", ["path": "player:volume", "roles": "value"]) else { return done(nil) }
        task { $0.dataTask(with: u) { data, _, err in
            guard let data, let arr = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]], let v = arr.first?["i32_"] as? Int else {
                self.noteFailure("getVolume", err); return done(nil)
            }
            self.noteSuccess()
            done(v)
        } }.resume()
    }

    func setVolume(_ v: Int, _ done: @escaping (Bool) -> Void) {
        setData(path: "player:volume", valueJSON: "{\"type\":\"i32_\",\"i32_\":\(v)}", done)
    }

    /// Physical source: wifi, bluetooth, tv, optical, analog, usb, standby.
    func setSource(_ source: String, _ done: @escaping (Bool) -> Void) {
        setData(path: "settings:/kef/play/physicalSource", valueJSON: "{\"type\":\"kefPhysicalSource\",\"kefPhysicalSource\":\"\(source)\"}", done)
    }

    func getSource(_ done: @escaping (String?) -> Void) {
        guard let u = url("/api/getData", ["path": "settings:/kef/play/physicalSource", "roles": "value"]) else { return done(nil) }
        task { $0.dataTask(with: u) { data, _, err in
            guard let data, let arr = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
                self.noteFailure("getSource", err); return done(nil)
            }
            self.noteSuccess()
            done(arr.first?["kefPhysicalSource"] as? String)
        } }.resume()
    }

    private func setData(path: String, valueJSON: String, _ done: @escaping (Bool) -> Void) {
        // The speaker accepts writes only as POST with a JSON body (GET answers "Invalid method!").
        guard let u = url("/api/setData", [:]) else { return done(false) }
        var req = URLRequest(url: u)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = "{\"path\":\"\(path)\",\"roles\":\"value\",\"value\":\(valueJSON)}".data(using: .utf8)
        task { $0.dataTask(with: req) { data, resp, err in
            // player:volume answers "true"; settings paths echo {"value":…,"timestamp":…} (even for values
            // the speaker then ignores, so callers that care read the setting back).
            let body = data.map { String(decoding: $0, as: UTF8.self) } ?? ""
            let ok = err == nil && (resp as? HTTPURLResponse)?.statusCode == 200 && (body.hasPrefix("true") || body.hasPrefix("{\"value\""))
            if ok { self.noteSuccess() } else { self.noteFailure("setData \(path)", err, status: (resp as? HTTPURLResponse)?.statusCode, body: body) }
            done(ok)
        } }.resume()
    }

    // Log the underlying error of a failed speaker request, at most once per 10 s per kind, so the log
    // says WHY the speaker is unreachable (DNS, refused, local-network privacy, odd reply) instead of
    // only "failed". The caller still logs the failed action.
    private var lastFailureLog: [String: Date] = [:]
    private var consecutiveFailures = 0
    private var resetsSinceSuccess = 0
    private let failureLock = NSLock()
    private func noteSuccess() {
        failureLock.lock(); consecutiveFailures = 0; resetsSinceSuccess = 0; failureLock.unlock()
    }
    private func noteFailure(_ what: String, _ err: Error?, status: Int? = nil, body: String = "") {
        let nsErr = err as NSError?
        let cancelled = nsErr?.domain == NSURLErrorDomain && nsErr?.code == NSURLErrorCancelled
        failureLock.lock()
        var reset = false, escalate = false
        if !cancelled {
            consecutiveFailures += 1
            if consecutiveFailures >= 3 {
                if resetsSinceSuccess == 0 { reset = true } else { consecutiveFailures = 0; escalate = true }
            }
        }
        let throttled = lastFailureLog[what].map { Date().timeIntervalSince($0) < 10 } ?? false
        if !throttled { lastFailureLog[what] = Date() }
        failureLock.unlock()
        if !throttled {
            var detail = nsErr.map { "\($0.domain) \($0.code): \($0.localizedDescription)" } ?? "no error"
            if let status { detail += ", HTTP \(status)" }
            if !body.isEmpty { detail += ", body: \(body.prefix(80))" }
            log("speaker \(what) error: \(detail)")
        }
        if reset { resetSession(reason: "3 consecutive failures") }
        if escalate { onPersistentFailure?() }
    }
}

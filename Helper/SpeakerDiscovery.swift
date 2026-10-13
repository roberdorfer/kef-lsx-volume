import Foundation

// MARK: - Bonjour discovery of the LSX II

extension Config {
    /// The host to talk to right now: the override, else the discovered one, else nil (still searching).
    static var speakerHost: String? { speakerHostOverride ?? SpeakerDiscovery.shared.host }
}

/// Finds the speaker on the local network so no hostname has to be configured. The LSX II advertises
/// its HTTP API as an `_http._tcp` Bonjour service named "LSX II" whose host is `lsxii-<mac>.local`;
/// both the service name and the host prefix are accepted, so a speaker renamed in KEF Connect is
/// still found. The browser keeps running: when the chosen service disappears the host is cleared
/// and the next matching service is used. Must be started on the main thread (needs its run loop).
// Design decision: Find the speaker automatically instead of shipping a default hostname —
// reason: a fixed default hostname is useless for anyone else's speaker. `speakerHost` stays as a manual override.
final class SpeakerDiscovery: NSObject, NetServiceBrowserDelegate, NetServiceDelegate {
    static let shared = SpeakerDiscovery()

    private let browser = NetServiceBrowser()
    private var pending: [NetService] = []     // services being resolved (NetService must be retained)
    private var advertised: [NetService] = []  // every service currently announced, resolved or not
    private var chosen: NetService?
    /// How many instances of each service (name + domain) are currently advertised: the same speaker shows
    /// up once per network interface, and losing one interface must not drop the host.
    private var instances: [String: Int] = [:]
    private let lock = NSLock()
    private var _host: String?
    private var started = false
    private var notFoundTimer: Timer?
    static let notFoundAfter: TimeInterval = 30

    /// Host name of the discovered speaker (`lsxii-….local`), or nil while nothing matching is on the network.
    var host: String? { lock.lock(); defer { lock.unlock() }; return _host }

    func start() {
        guard !started else { return }
        started = true
        browser.delegate = self
        browser.searchForServices(ofType: "_http._tcp.", inDomain: "local.")
        log("speaker: searching for the LSX II via Bonjour")
        // Networks that block mDNS would otherwise leave the helper silent forever; say so once.
        notFoundTimer = Timer.scheduledTimer(withTimeInterval: Self.notFoundAfter, repeats: false) { [weak self] _ in
            if self?.host == nil { log("speaker: no LSX II found via Bonjour after \(Int(Self.notFoundAfter)) s; if mDNS is blocked set `speakerHost` (defaults write \(suiteName) speakerHost <host or IP>)") }
        }
    }

    /// The speaker advertises as "LSX II" with host `lsxii-<mac>.local`; either identifies it (a renamed
    /// speaker keeps its host name). `host` is nil before resolving.
    static func matches(name: String, host: String?) -> Bool {
        name.localizedCaseInsensitiveContains("LSX II") || (host ?? "").lowercased().hasPrefix("lsxii-")
    }

    private static func key(_ s: NetService) -> String { s.name + "." + s.domain }

    // MARK: NetServiceBrowserDelegate

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        instances[Self.key(service), default: 0] += 1
        advertised.append(service)
        // Services with the speaker's name are always resolved. Others are resolved only while no speaker
        // is known yet (the match may be on the host name, known only after resolving), so a network full
        // of HTTP devices does not cost a resolve per flap once the speaker has been found. Whatever was
        // skipped is resolved later when the chosen speaker disappears (`resolveAdvertised`).
        guard Self.matches(name: service.name, host: nil) || chosen == nil else { return }
        resolve(service)
    }

    private func resolve(_ service: NetService) {
        guard !pending.contains(where: { $0 === service }) else { return }
        service.delegate = self
        pending.append(service)
        service.resolve(withTimeout: 10)
    }

    /// Resolve everything still announced: a second speaker (named or renamed) that was ignored or skipped
    /// while another one was chosen becomes the host now.
    private func resolveAdvertised() { advertised.forEach(resolve) }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        if pending.contains(where: { $0 === service }) { service.stop() }   // a resolve still in flight must not choose it
        pending.removeAll { $0 === service }
        advertised.removeAll { $0 === service }
        let k = Self.key(service)
        instances[k] = max(0, (instances[k] ?? 1) - 1)
        if instances[k] == 0 { instances[k] = nil }
        if let c = chosen, Self.key(c) == k, instances[k] == nil {
            chosen = nil
            setHost(nil)
            log("speaker: \(service.name) disappeared from Bonjour")
            resolveAdvertised()
        }
    }

    // MARK: NetServiceDelegate

    func netServiceDidResolveAddress(_ service: NetService) {
        pending.removeAll { $0 === service }
        guard instances[Self.key(service)] != nil,   // still announced (the resolve may outlive a remove)
              let h = service.hostName, Self.matches(name: service.name, host: h) else { return }
        let host = h.hasSuffix(".") ? String(h.dropLast()) : h
        if let c = chosen {   // keep the first speaker until it disappears; a changed address arrives as remove + add
            if Self.key(c) != Self.key(service) { log("speaker: ignoring second match \"\(service.name)\" at \(host); using \(self.host ?? "?")") }
            return
        }
        chosen = service
        setHost(host)
        log("speaker: found \"\(service.name)\" at \(host)")
    }

    func netService(_ service: NetService, didNotResolve errorDict: [String: NSNumber]) {
        pending.removeAll { $0 === service }
    }

    private func setHost(_ h: String?) { lock.lock(); _host = h; lock.unlock() }
}

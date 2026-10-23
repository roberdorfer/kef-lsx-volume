import CoreAudio
import Foundation

// MARK: - Controller

final class Controller {
    let queue = DispatchQueue(label: suiteName)
    let speaker = Speaker()
    var proxy: AudioObjectID?
    var real: AudioObjectID?
    var listenersInstalledFor: AudioObjectID?

    var lastSentSpeaker: Int?          // last value we pushed to the speaker
    var lastKnownSpeaker: Int?         // last value read from the speaker
    var expectedProxyScalar: Float32?  // value we set on the proxy ourselves (echo suppression)
    var proxyIsDefault = false          // last observed: default output == proxy
    var restoreProxyOnReturn = false    // default left the proxy only because the USB device vanished
    var input: AudioObjectID?           // configured input device, if present
    var realListenersFor: AudioObjectID?
    var onConsole = true
    var inputIsDefault = false
    var restoreInputOnReturn = false
    var pendingSend: DispatchWorkItem?
    var sendInFlight = false
    var queuedValue: Int?
    var lastSpeakerContact: Date?       // last successful speaker API answer
    var consoleGainedAt: Date?          // when this session last became (or started as) the console
    var speakerSource: String?

    /// Read-only view for the menu bar UI; taken on the controller queue.
    struct Status {
        var speakerVolume: Int?
        var speakerReachable: Bool
        var speakerSource: String?
        var proxyPresent: Bool
        var realPresent: Bool
        var proxyIsDefault: Bool
        var inputPresent: Bool
        var inputIsDefault: Bool
        var realFormat: String?
        var onConsole: Bool
    }

    func snapshot() -> Status {
        queue.sync {
            var fmt: String?
            if let r = real, let st = realOutputStream(r), let cur = getProp(st, addr(kAudioStreamPropertyPhysicalFormat), AudioStreamBasicDescription()) {
                fmt = "\(Int(cur.mSampleRate / 1000)) kHz / \(cur.mBitsPerChannel)-bit"
            }
            return Status(speakerVolume: lastKnownSpeaker,
                          speakerReachable: lastSpeakerContact.map { Date().timeIntervalSince($0) < 3 * Config.pollInterval + 2 } ?? false,
                          speakerSource: speakerSource,
                          proxyPresent: proxy != nil, realPresent: real != nil, proxyIsDefault: proxyIsDefault,
                          inputPresent: input != nil, inputIsDefault: inputIsDefault, realFormat: fmt, onConsole: onConsole)
        }
    }

    /// Called by the UI after a setting changed: re-evaluate devices and re-apply the Mac value with the new range.
    func settingsChanged(pushVolume: Bool) {
        queue.async {
            self.rescan(); self.checkDefaultOutput(); self.checkDefaultInput()
            if pushVolume { self.pushProxyToSpeaker(reason: "settings changed") }
        }
    }

    func setSpeakerSource(_ src: String) {
        speaker.setSource(src) { ok in
            self.queue.async {
                log("speaker source -> \(src): \(ok ? "sent" : "request failed")")
                if ok { self.speakerSource = src }
                // The speaker echoes any value; read back what it actually did.
                self.queue.asyncAfter(deadline: .now() + 2) {
                    self.speaker.getSource { s in self.queue.async { self.speakerSource = s; log("speaker source now: \(s ?? "unknown")") } }
                }
            }
        }
    }

    func start() {
        var devAddr = addr(kAudioHardwarePropertyDevices)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &devAddr, queue) { _, _ in self.rescan() }
        var defAddr = addr(kAudioHardwarePropertyDefaultOutputDevice)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &defAddr, queue) { _, _ in self.checkDefaultOutput() }
        var defInAddr = addr(kAudioHardwarePropertyDefaultInputDevice)
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &defInAddr, queue) { _, _ in self.checkDefaultInput() }
        queue.async { self.rescan(); self.checkDefaultOutput(); self.checkDefaultInput() }
        onConsole = isConsoleSession()
        if onConsole { consoleGainedAt = Date() }
        log("console session: \(onConsole)")
        speaker.onPersistentFailure = { [weak self] in self?.queue.async { self?.speakerPersistentlyUnreachable() } }
        let poll = DispatchSource.makeTimerSource(queue: queue)
        poll.schedule(deadline: .now() + 1, repeating: Config.pollInterval)
        poll.setEventHandler { self.refreshConsole(); if self.onConsole { self.pollSpeaker() } }
        poll.resume()
        let pin = DispatchSource.makeTimerSource(queue: queue)
        pin.schedule(deadline: .now() + 2, repeating: 10)
        pin.setEventHandler { if self.onConsole { self.pinRealControls(); self.pinRealFormat() } }
        pin.resume()
        pollTimer = poll
        pinTimer = pin
        let src = DispatchSource.makeTimerSource(queue: queue)
        src.schedule(deadline: .now() + 3, repeating: 15)
        src.setEventHandler { if self.onConsole { self.speaker.getSource { s in self.queue.async { self.speakerSource = s } } } }
        src.resume()
        sourceTimer = src
    }
    var sourceTimer: DispatchSourceTimer?
    var pollTimer: DispatchSourceTimer?
    var pinTimer: DispatchSourceTimer?

    func rescan() {
        guard onConsole else { return }
        let p = findProxy()
        let r = findReal()
        if p != proxy { log("proxy device: \(p.map(String.init) ?? "none")") }
        let realReappeared = r != real && r != nil
        proxy = p   // before pinning: the USB unmute depends on the proxy volume
        if r != real { log("real device: \(r.map(String.init) ?? "none")"); if let r { pinRealControls(r); pinRealFormat(r); installRealListeners(r) } }
        real = r
        // The speaker may reset its own volume when the USB link re-enumerates (wake, source switch).
        // The Mac's value is the user's last intent: push it, never pull a reset value into the Mac.
        if realReappeared, listenersInstalledFor == p, p != nil { pushProxyToSpeaker(reason: "real device reappeared") }
        if r != nil, p != nil { firstRunDefaultOutput() }
        let i = findInput()
        let inputReappeared = i != input && i != nil
        if i != input { log("input device: \(i.map(String.init) ?? "none")") }
        input = i
        if inputReappeared, restoreInputOnReturn, let i {
            restoreInputOnReturn = false
            let def = getProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultInputDevice), AudioObjectID(0))
            if def != i {
                let err = setProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultInputDevice), i)
                log("input device back; restored it as default input (was \(def.map(String.init) ?? "none"), \(err))")
                inputIsDefault = err == noErr
            }
        }
        if realReappeared, restoreProxyOnReturn, Config.autoSwitchDefault, let p {
            restoreProxyOnReturn = false
            let def = getProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultOutputDevice), AudioObjectID(0))
            if def != p {
                let err = setProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultOutputDevice), p)
                log("real device back; restored proxy as default output (was \(def.map(String.init) ?? "none"), \(err))")
                proxyIsDefault = err == noErr
            }
        }
        if let p, listenersInstalledFor != p {
            var volAddr = addr(kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput)
            AudioObjectAddPropertyListenerBlock(p, &volAddr, queue) { _, _ in self.proxyVolumeChanged() }
            var runAddr = addr(kAudioDevicePropertyDeviceIsRunningSomewhere)
            AudioObjectAddPropertyListenerBlock(p, &runAddr, queue) { _, _ in self.proxyRunningChanged() }
            listenersInstalledFor = p
            // Initial sync: speaker -> proxy.
            speaker.getVolume { v in self.queue.async { if let v { self.lastKnownSpeaker = v; self.applyToProxy(v) } } }
        }
    }

    // MARK: auto-wake

    var pendingWake: DispatchWorkItem?

    func proxyIsRunning() -> Bool {
        proxy.flatMap { getProp($0, addr(kAudioDevicePropertyDeviceIsRunningSomewhere), UInt32(0)) }.map { $0 != 0 } ?? false
    }

    /// IO on the proxy started or stopped. After `autoWakeDelay` of continuous playback, wake the
    /// speaker if (and only if) it is in standby; other active sources are left alone.
    func proxyRunningChanged() {
        pendingWake?.cancel(); pendingWake = nil
        guard onConsole, Config.autoWakeOnPlayback, proxyIsRunning() else { return }
        let w = DispatchWorkItem { [self] in
            guard proxyIsRunning() else { return }
            speaker.getSource { s in
                self.queue.async {
                    self.speakerSource = s
                    guard s == "standby" else { return }
                    log("playback started while speaker in standby: waking it on USB")
                    self.setSpeakerSource("usb")
                }
            }
        }
        pendingWake = w
        queue.asyncAfter(deadline: .now() + Config.autoWakeDelay, execute: w)
    }

    func proxyScalar() -> Float32? { proxy.flatMap { getProp($0, addr(kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput), Float32(0)) } }

    func proxyVolumeChanged() {
        guard onConsole, let s = proxyScalar() else { return }
        pinRealControls()   // leaving 0 (keys or speaker remote) clears a kept USB mute right away
        if let e = expectedProxyScalar, abs(e - s) < 0.001 { expectedProxyScalar = nil; return }
        let v = Config.speakerVolume(forScalar: s)
        scheduleSend(v)
    }

    func pushProxyToSpeaker(reason: String) {
        guard let s = proxyScalar() else { return }
        let v = Config.speakerVolume(forScalar: s)
        log("push proxy -> speaker: \(v) (\(reason))")
        lastKnownSpeaker = nil
        lastSentSpeaker = nil
        scheduleSend(v)
    }

    func scheduleSend(_ v: Int) {
        queuedValue = v
        pendingSend?.cancel()
        let w = DispatchWorkItem { self.flushSend() }
        pendingSend = w
        queue.asyncAfter(deadline: .now() + 0.03, execute: w)
    }

    func flushSend() {
        guard !sendInFlight, let v = queuedValue else { return }
        queuedValue = nil
        if v == lastSentSpeaker && v == lastKnownSpeaker { return }
        sendInFlight = true
        // Reach every value from above (Config.approachFromAbove): when the value goes up, or when the
        // speaker's state is unknown (start, reset, re-enumeration), set a slightly higher value first and
        // the target right after. Going down from a known value already arrives from above.
        let from = lastKnownSpeaker ?? lastSentSpeaker
        let goingUp = from == nil || v > from!
        let via = goingUp ? Config.approachFromAbove(v, cap: Config.maxSpeakerVolume) : nil
        lastSentSpeaker = v
        let finish: (Bool) -> Void = { ok in
            self.queue.async {
                self.sendInFlight = false
                if ok {
                    self.lastKnownSpeaker = v; self.lastSpeakerContact = Date()
                    log("proxy -> speaker: \(v)" + (via.map { " (via \($0))" } ?? ""))
                } else { log("speaker setVolume \(v) failed") }
                if self.queuedValue != nil { self.flushSend() }
            }
        }
        guard let via else { speaker.setVolume(v, finish); return }
        speaker.setVolume(via) { ok in
            guard ok else { finish(false); return }
            self.queue.asyncAfter(deadline: .now() + Config.approachHold) {
                self.speaker.setVolume(v) { ok in
                    if !ok {
                        // The overshoot landed but the target did not: the speaker now sits at `via`, louder than
                        // asked. Record that truthfully (so the next poll does not mirror `via` to the Mac as a
                        // user change) and send the target once more; from `via` it goes down, so no new overshoot.
                        self.queue.async { self.lastKnownSpeaker = via; if self.queuedValue == nil { self.queuedValue = v } }
                    }
                    finish(ok)
                }
            }
        }
    }

    func pollSpeaker() {
        speaker.getVolume { v in
            self.queue.async {
                guard let v else { return }
                self.lastSpeakerContact = Date()
                if v != self.lastKnownSpeaker {
                    let previous = self.lastKnownSpeaker
                    self.lastKnownSpeaker = v
                    if self.sendInFlight || self.queuedValue != nil { return }
                    if let sent = self.lastSentSpeaker, sent == v { return }
                    if let previous, abs(v - previous) >= Config.resetJumpThreshold {
                        self.pushProxyToSpeaker(reason: "speaker jumped \(previous) -> \(v), treating as reset")
                        return
                    }
                    self.applyToProxy(v)
                }
            }
        }
    }

    func applyToProxy(_ speakerVolume: Int) {
        guard let p = proxy else { return }
        let s = Config.scalar(forSpeakerVolume: speakerVolume)
        if let cur = proxyScalar(), abs(cur - s) < 0.001 { return }
        expectedProxyScalar = s
        let err = setProp(p, addr(kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput), s)
        if err != noErr { log("set proxy scalar failed \(err)"); expectedProxyScalar = nil }
        else { log("speaker -> proxy: \(speakerVolume)") }
    }

    func pinRealControls(_ dev: AudioObjectID? = nil) {
        guard let r = dev ?? real else { return }
        for el: UInt32 in [1, 2, 0] {
            var a = addr(kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, el)
            guard AudioObjectHasProperty(r, &a) else { continue }
            if let cur = getProp(r, a, Float32(0)), cur < 0.999 {
                let err = setProp(r, a, Float32(1.0))
                log("pin real USB volume element \(el): \(cur) -> 1.0 (\(err))")
            }
        }
        guard Config.shouldUnmuteReal(proxyScalar: proxyScalar()) else { return }
        let m = addr(kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput)
        if let cur = getProp(r, m, UInt32(0)), cur != 0 {
            log("unmute real USB device (\(setProp(r, m, UInt32(0))))")
        }
    }

    func checkDefaultOutput() {
        guard onConsole, Config.autoSwitchDefault else { return }
        guard let def = getProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultOutputDevice), AudioObjectID(0)) else { return }
        let p = proxy ?? findProxy()
        if let r = real ?? findReal(), def == r, let p {
            let err = setProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultOutputDevice), p)
            log("default output was real LSX II; switched to proxy (\(err))")
            proxyIsDefault = err == noErr
            return
        }
        if def == p {
            proxyIsDefault = true
            restoreProxyOnReturn = false
        } else if proxyIsDefault, findReal() == nil {
            // Lost the default because the USB device went away, not by user choice.
            proxyIsDefault = false
            restoreProxyOnReturn = true
            log("default output left proxy while USB device absent (now \(def)); will restore on return")
        } else {
            proxyIsDefault = false
            restoreProxyOnReturn = false
        }
    }
}

extension Controller {
    func refreshConsole() {
        let now = isConsoleSession()
        if now != onConsole {
            onConsole = now
            log("console session: \(now)")
            if now {
                consoleGainedAt = Date()
                speaker.resetSession(reason: "session became console")
                rescan(); checkDefaultOutput(); checkDefaultInput(); pushProxyToSpeaker(reason: "session became console")
            }
        }
    }

    /// Last escalation for the stale-session failure (every speaker request failing for minutes after a
    /// user switch, cured only by restarting the process): if a fresh URLSession did not help within two
    /// minutes of becoming console, exit so launchd (KeepAlive) relaunches the helper. At most once per
    /// 10 minutes, so a speaker that is really off the network cannot keep the helper in a relaunch loop.
    func speakerPersistentlyUnreachable() {
        guard onConsole, let t = consoleGainedAt, Date().timeIntervalSince(t) < 120 else { return }
        if let last = Config.defaults.object(forKey: "lastSelfRelaunch") as? Date, Date().timeIntervalSince(last) < 600 { return }
        Config.defaults.set(Date(), forKey: "lastSelfRelaunch")
        log("speaker still unreachable after a new network session since this session became console: exiting so launchd relaunches the helper")
        exit(0)
    }

    /// First run for this user: make the proxy the default output once, if the speaker is attached. The
    /// proxy's UID is remembered, so a renamed proxy device (new UID) is made the default once more:
    /// macOS keeps the default output by UID, and the old one no longer exists.
    func firstRunDefaultOutput() {
        guard Config.autoSwitchDefault, Config.defaults.string(forKey: "defaultOutputSetFor") != proxyUID else { return }
        guard let p = proxy ?? findProxy(), (real ?? findReal()) != nil else { return }
        let err = setProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultOutputDevice), p)
        log("first run: proxy set as default output (\(err))")
        if err == noErr { Config.defaults.set(proxyUID, forKey: "defaultOutputSetFor"); proxyIsDefault = true }
    }

    func realOutputStream(_ dev: AudioObjectID) -> AudioObjectID? {
        var a = addr(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(dev, &a, 0, nil, &size) == noErr, size >= 4 else { return nil }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / 4)
        guard AudioObjectGetPropertyData(dev, &a, 0, nil, &size, &ids) == noErr else { return nil }
        return ids.first
    }

    /// Keep the real device at 96 kHz / 24-bit integer. The driver forces the rate too; the physical
    /// format (bit depth) can only be pinned from a normal process.
    func pinRealFormat(_ dev: AudioObjectID? = nil) {
        guard let r = dev ?? real, let st = realOutputStream(r) else { return }
        let fa = addr(kAudioStreamPropertyPhysicalFormat)
        guard let cur = getProp(st, fa, AudioStreamBasicDescription()) else { return }
        let isInt = cur.mFormatFlags & kAudioFormatFlagIsFloat == 0
        if abs(cur.mSampleRate - pinnedRealRate) < 0.5, cur.mBitsPerChannel == pinnedRealBits, isInt { return }
        let was = "\(Int(cur.mSampleRate)) Hz/\(cur.mBitsPerChannel)-bit\(isInt ? "" : " float")"
        var want = AudioStreamBasicDescription()
        want.mSampleRate = pinnedRealRate
        want.mFormatID = kAudioFormatLinearPCM
        want.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked
        want.mChannelsPerFrame = cur.mChannelsPerFrame
        want.mBitsPerChannel = pinnedRealBits
        want.mBytesPerFrame = cur.mChannelsPerFrame * pinnedRealBits / 8
        want.mFramesPerPacket = 1
        want.mBytesPerPacket = want.mBytesPerFrame
        let err = setProp(st, fa, want)
        log("pin real format: \(was) -> \(Int(pinnedRealRate)) Hz/\(pinnedRealBits)-bit (\(err))")
    }

    func installRealListeners(_ r: AudioObjectID) {
        guard realListenersFor != r else { return }
        realListenersFor = r
        // macOS may restore a remembered mute a moment after the device appears; react at once.
        var ma = addr(kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput)
        AudioObjectAddPropertyListenerBlock(r, &ma, queue) { _, _ in if self.onConsole { self.pinRealControls() } }
        var ra = addr(kAudioDevicePropertyNominalSampleRate)
        AudioObjectAddPropertyListenerBlock(r, &ra, queue) { _, _ in if self.onConsole { self.pinRealFormat() } }
        guard let st = realOutputStream(r) else { return }
        var fa = addr(kAudioStreamPropertyPhysicalFormat)
        AudioObjectAddPropertyListenerBlock(st, &fa, queue) { _, _ in if self.onConsole { self.pinRealFormat() } }
    }

    func checkDefaultInput() {
        guard onConsole else { return }
        guard Config.restoreInputUIDPrefix != nil else { return }
        guard let def = getProp(AudioObjectID(kAudioObjectSystemObject), addr(kAudioHardwarePropertyDefaultInputDevice), AudioObjectID(0)) else { return }
        let i = input ?? findInput()
        if let i, def == i {
            inputIsDefault = true
            restoreInputOnReturn = false
        } else if inputIsDefault, findInput() == nil {
            inputIsDefault = false
            restoreInputOnReturn = true
            log("default input left configured device while it is absent (now \(def)); will restore on return")
        } else {
            inputIsDefault = false
            restoreInputOnReturn = false
        }
    }
}


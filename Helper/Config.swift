// LSX II Volume Fixer — menu bar agent that pairs the KEFProxy HAL device with the real KEF LSX II.
//
// Responsibilities:
//  1. Mirror the proxy device's volume (set via Mac keys / Control Center) to the
//     speaker's own volume over its local HTTP API (player:volume, 0–100).
//  2. Mirror speaker volume changes (remote, app) back to the proxy device so the
//     Mac UI stays in sync.
//  3. Pin the real USB device's host volume at 0 dB so the USB path is bit-transparent.
//  4. When macOS makes the real USB device the default output (e.g. on plug-in),
//     switch the default output to the proxy device instead. When the USB link
//     drops, macOS moves the default output elsewhere (built-in speakers); if the
//     proxy was the default at that moment, restore it once the real device is back.
//     A deliberate device change by the user while USB is unplugged cancels that.
//  5. Optional, off by default: the same restore for the default *input* device
//     (`restoreInputUIDPrefix`), for setups where the microphone shares the USB hub.
//     Unrelated to the speaker itself; kept generic and opt-in on purpose.
//  6. Pin the real USB device's physical format to 96 kHz / 24-bit integer (the speaker's internal
//     DSP rate); any other format set by a user or app is reverted. The proxy itself only offers 96 kHz.
//  7. Multi-user: installed as a global LaunchAgent, one instance per login session. Only
//     the instance in the console (foreground) session acts; others idle until they own
//     the console. On a user's first run the proxy is made the default output once.
//  8. Menu bar item (StatusMenu.swift): live status, volume range sliders, the settings
//     below, speaker source/wake, and "apply for all users" (admin prompt).
//
// Design decision: Volume is controlled from the Mac keys with the standard
// macOS HUD, bit-perfect transport; remote/app-only control is not enough.

import CoreAudio
import CoreGraphics
import Foundation

let appName = "LSX II Volume Fixer"
let appSlug = "LSXIIVolumeFixer"   // bundle/executable/log file name (no spaces)
let appVersion = "1.0"
// Design decision: Identifiers use the GitHub reverse-DNS form io.github.roberdorfer.*.
let suiteName = "io.github.roberdorfer.lsxiivolumefixer"

// MARK: - Config

struct Config {
    static let defaults: UserDefaults = {
        // The app's own bundle id is not a valid suite name; .standard maps to the same domain then.
        Bundle.main.bundleIdentifier == suiteName ? UserDefaults.standard : UserDefaults(suiteName: suiteName)!
    }()
    /// Keys a user can set; also the set copied by "apply for all users".
    static let userKeys = ["speakerHost", "minSpeakerVolume", "maxSpeakerVolume", "autoSwitchDefault", "autoWakeOnPlayback", "restoreInputUIDPrefix", "showVolumeInMenuBar", "defaultOutputSetFor"]
    static let settingKeys = ["speakerHost", "minSpeakerVolume", "maxSpeakerVolume", "autoSwitchDefault", "autoWakeOnPlayback", "restoreInputUIDPrefix", "showVolumeInMenuBar"]
    /// The Bool-typed keys among `settingKeys` (written with `-bool` by "apply for all users").
    static let boolKeys = ["autoSwitchDefault", "autoWakeOnPlayback", "showVolumeInMenuBar"]
    /// Optional fixed speaker host name or IP (`defaults write … speakerHost`). Empty/unset: the speaker is
    /// found via Bonjour (`SpeakerDiscovery`), which is the normal case.
    static var speakerHostOverride: String? {
        let v = (defaults.string(forKey: "speakerHost") ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return v.isEmpty ? nil : v
    }
    // `speakerHost` (override, else the discovered host) lives in SpeakerDiscovery.swift so the pure-function
    // tests can build Config.swift alone.
    // Design decision: Mac 100 % maps to speaker 65 (−17.5 dB), not 100: the upper part of the old
    // 12–100 mapping was louder than useful, which wasted most key steps. The 16 macOS steps stay; only
    // the ceiling moves. Changed from the menu bar slider or `defaults write io.github.roberdorfer.lsxiivolumefixer maxSpeakerVolume N`.
    static var maxSpeakerVolume: Int { let v = defaults.integer(forKey: "maxSpeakerVolume"); return v > 0 ? min(v, 100) : 65 }
    // Design decision: The first key step is speaker 12
    // — reason: the quietest key step should be very quiet; 12 is reached from above
    // (see `approachFromAbove`). Mac 0 % stays speaker 0 + mute; speaker values below the minimum
    // are not reachable from the keys.
    static var minSpeakerVolume: Int { let v = defaults.integer(forKey: "minSpeakerVolume"); return v > 0 ? min(v, maxSpeakerVolume - 1) : 12 }

    /// Proxy scalars at or below this count as Mac volume 0.
    static let zeroScalar: Float32 = 0.0005
    /// One macOS volume key step on the 0–1 scalar scale (16 steps; Option+Shift gives 1/64).
    static let keyStep: Float32 = 1.0 / 16.0
    // Design decision: "Minimum" is the speaker value of the FIRST audible key step (Mac 1/16),
    // not of Mac 0 % — reason: with the old origin-at-0 % mapping the lowest key step landed at 15, not at
    // the chosen minimum. Mac 0 % stays speaker 0 + mute; finer Option+Shift steps below 1/16 clamp to
    // the minimum.
    // Design decision: The 16 key steps are spread EQUALLY in measured dB between Minimum and
    // Maximum: every key press makes the speaker louder by the same number of dB (reason: each step
    // should mean the same difference in dB, and the step size should follow only from the two range
    // values). The dB values come from the
    // MEASURED from-above curve of the speaker (`dB(_:)`), not from KEF's `settings:/mediaPlayer/volumeMap`
    // (that table is wrong for the USB path: it claims 2 dB per unit below 20 and 0.5 dB per unit above;
    // measured: 1 dB per unit from 8 to 60, 0.5 dB per unit 60–65).
    // Implementation detail (not part of the decision): each step target is rounded to the nearest speaker
    // value by measured dB, never below the previous step. With 12–65 (50.5 dB / 15 = 3.37 dB per step):
    // 12 15 19 22 25 29 32 36 39 42 46 49 52 56 59 65 — steps of 3–4 dB, the last one 3.5 dB.
    /// Target speaker value of each of the 16 key steps (index 0 = first step = minimum, 15 = maximum).
    /// The range defaults to the configured one; tests pass their own.
    static func keyStepTable(min lo: Int = minSpeakerVolume, max hi: Int = maxSpeakerVolume) -> [Int] {
        let stepDB = (dB(hi) - dB(lo)) / 15
        var table = [lo]
        var prev = lo
        for i in 1..<15 {
            let target = dB(lo) + stepDB * Double(i)
            // Nearest speaker value by measured dB, never below the previous step.
            var best = prev
            var bestErr = Double.infinity
            for v in prev...hi {
                let e = abs(dB(v) - target)
                if e < bestErr { bestErr = e; best = v }
            }
            table.append(best)
            prev = best
        }
        table.append(hi)
        return table
    }
    static func speakerVolume(forScalar s: Float32) -> Int {
        if s <= zeroScalar { return 0 }
        let t = keyStepTable()
        let pos = max(0, min(15, Double((s - keyStep) / (1 - keyStep)) * 15))
        let i = min(14, Int(pos)), f = pos - Double(i)
        return Int((Double(t[i]) + f * Double(t[i + 1] - t[i])).rounded())
    }
    static func scalar(forSpeakerVolume v: Int) -> Float32 {
        if v <= 0 { return 0 }
        let t = keyStepTable()
        if v <= t[0] { return keyStep }
        if v >= t[15] { return 1 }
        var i = 0
        while i < 14 && t[i + 1] < v { i += 1 }
        let f = t[i + 1] == t[i] ? 0.0 : Double(v - t[i]) / Double(t[i + 1] - t[i])
        return keyStep + Float32((Double(i) + f) / 15) * (1 - keyStep)
    }
    // Design decision: Clear a mute on the real USB device, except while the proxy's own volume is
    // 0.
    // Background: macOS stores mute per USB port and restores it when the speaker re-enumerates after
    // standby; the device is hidden behind the proxy, so nothing on screen shows it and the speaker stays silent.
    /// True when the real USB device should be unmuted; `proxyScalar` nil (no proxy) leaves it alone.
    static func shouldUnmuteReal(proxyScalar: Float32?) -> Bool { (proxyScalar ?? 0) > zeroScalar }
    static var autoSwitchDefault: Bool { defaults.object(forKey: "autoSwitchDefault") as? Bool ?? true }
    // Design decision: Also restore the default input device after a USB re-enumeration (useful when a
    // USB mic sits on the same hub and macOS falls back to the built-in mic) — kept as a generic, opt-in
    // setting; reason: it has nothing to do with the speaker itself.
    // `defaults write io.github.roberdorfer.lsxiivolumefixer restoreInputUIDPrefix "AppleUSBAudioEngine:<Vendor>:<Model>"` or via the menu.
    static var restoreInputUIDPrefix: String? {
        let v = defaults.string(forKey: "restoreInputUIDPrefix") ?? ""
        return v.isEmpty ? nil : v
    }
    // Design decision: Wake the speaker from standby and select USB automatically when the Mac
    // starts playing — reason: the manual "wake" item should not be needed in the
    // common case. Only from standby (never away from TV/Bluetooth/…), and only after sustained playback
    // (`autoWakeDelay`) so short system sounds do not wake the speaker.
    static var autoWakeOnPlayback: Bool { defaults.object(forKey: "autoWakeOnPlayback") as? Bool ?? true }
    static var autoWakeDelay: Double { 2.0 }
    /// Time in standby before switching back to USB (measured 2026-10-26: standby is reported within 0.5 s).
    static var powerCycleOffTime: Double { 3.0 }
    static var showVolumeInMenuBar: Bool { defaults.object(forKey: "showVolumeInMenuBar") as? Bool ?? true }

    static func set(_ value: Any?, forKey key: String) {
        if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
    }

    // MARK: Measured USB output curve (docs/usb-volume-curve.md)
    //
    // Measured 2026-10-12 at the listening position with five simultaneous tones (150 Hz – 7.3 kHz; all five
    // give the same steps within 0.3 dB). The speaker has TWO curves for `player:volume` on the USB path and a
    // simple rule for switching between them, confirmed on 230 measured steps:
    //  - reached from ABOVE: 1.0 dB per unit from 8 to 60, 0.5 dB per unit from 60 to 65 (`dB(_:)`);
    //  - reached from BELOW: identical above ≈40, steeper below (2 dB per unit around 13–16), and flat below
    //    13 (`dBFromBelow(_:)`);
    //  - raising the value never makes the output quieter than it is, lowering it never louder; so the
    //    output after a change is max(current, fromBelow(v)) when raising and min(current, fromAbove(v)) when
    //    lowering. Dwell time does not matter (0.1 s behaves like 4 s).
    // Hence the same value is up to 10 dB louder from above than from below (12: 10 dB, 16: 5 dB, 20: 2.7 dB,
    // 28: 0.9 dB, ≥40: 0), and 12→17 from below changes the output by under 1 dB.
    //
    // Design decision: A key step must give the same loudness regardless of the direction it is
    // reached from — reason: going up, steps 1 and 2 were indistinguishable, and the same value was up to
    // 10 dB louder from above. Every value is reached from above (brief overshoot, see
    // `approachFromAbove(_:)`), so only the from-above curve counts and it is the one the key steps are
    // placed on.

    /// Output level in dB relative to speaker 65 when the value is reached from above (the curve the keys
    /// use). Below 8 extrapolated (not measured, not reachable with the defaults).
    static func dB(_ v: Int) -> Double {
        if v <= 0 { return -120 }
        if v >= 60 { return -2.5 + 0.5 * Double(v - 60) }
        return Double(v) - 62.5
    }
    /// Output level in dB relative to speaker 65 when the value is reached from below (1 kHz, 1-unit steps
    /// from 8 upwards). Same scale as `dB(_:)`; equal to it from 41 up.
    static let fromBelowCurve: [Int: Double] = [12: -60.7, 13: -57.7, 14: -55.4, 15: -53.3, 16: -51.4, 17: -49.8,
        18: -48.2, 19: -46.6, 20: -45.1, 21: -43.7, 22: -42.3, 23: -41.1, 24: -39.8, 25: -38.6, 26: -37.6, 27: -36.4,
        28: -35.3, 29: -34.2, 30: -33.0, 31: -32.0, 32: -31.0, 33: -30.0, 34: -28.9, 35: -27.9, 36: -26.8, 37: -25.7,
        38: -24.6, 39: -23.6, 40: -22.7]
    static func dBFromBelow(_ v: Int) -> Double {
        if v <= 12 { return fromBelowCurve[12]! }      // flat: raising within 8–12 changes nothing
        return fromBelowCurve[v] ?? dB(v)
    }
    /// Value to set for a moment before `v` so that `v` is reached from above whatever the speaker's current
    /// state: the smallest overshoot whose from-below level is at least the from-above level of `v`
    /// (0.2 dB margin). 12 → 17, 17 → 20, 23 → 25, 28–39 → +1, 41 and up → none. Capped at `cap`.
    static func approachFromAbove(_ v: Int, cap: Int = 100) -> Int? {
        guard v > 0 else { return nil }
        if dBFromBelow(v) >= dB(v) { return nil }          // both curves agree (41 and up)
        let target = dB(v) + 0.2
        for k in 1...8 where v + k <= cap {
            if dBFromBelow(v + k) >= target { return v + k }
        }
        return nil
    }
    /// Pause between the overshoot and the target write; 0.1 s was measured to behave like 4 s.
    static var approachHold: Double { 0.1 }

    static var pollInterval: Double { let v = defaults.double(forKey: "pollInterval"); return v > 0 ? v : 1.0 }
    // A speaker volume jump of at least this many units between two polls, without a Mac-side change,
    // is treated as a speaker-side reset (e.g. 15 -> 53 after a USB re-enumeration) and
    // the Mac's value is pushed back instead of adopted. Remote presses move 1 unit per step.
    static var resetJumpThreshold: Int { let v = defaults.integer(forKey: "resetJumpThreshold"); return v > 0 ? v : 25 }
}

let proxyUID = "io.github.roberdorfer.kefproxy.device"
let realModelMatch = "27AC:1060"

let logURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/\(appSlug).log")
let logHandle: FileHandle? = {
    try? FileManager.default.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
    if !FileManager.default.fileExists(atPath: logURL.path) { FileManager.default.createFile(atPath: logURL.path, contents: nil) }
    let h = try? FileHandle(forWritingTo: logURL)
    h?.seekToEndOfFile()
    return h
}()
private let logFormatter: ISO8601DateFormatter = {
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return f
}()
func log(_ s: String) {
    let line = "\(logFormatter.string(from: Date())) \(s)\n".data(using: .utf8)!
    if let h = logHandle { h.write(line) } else { FileHandle.standardError.write(line) }
}

// Design decision: The proxy and the helper must work for every macOS user on the machine
// exactly as for the installing user.
// Implementation: global LaunchAgent in /Library, one helper instance per login session,
// speaker/default-device actions gated on owning the console so background sessions stay passive.
/// True when this login session owns the console (fast user switching puts other sessions in the background).
func isConsoleSession() -> Bool {
    guard let d = CGSessionCopyCurrentDictionary() as? [String: Any] else { return true }
    return (d[kCGSessionOnConsoleKey as String] as? Bool) ?? true
}

// Design decision: The output format stays fixed at 96 kHz / 24-bit everywhere and cannot be
// changed (Audio MIDI Setup, apps) — reason: the LSX II's DSP runs at 96 kHz with the
// speakers linked by cable, so 192 kHz only added a second resampling step.
// Physical format the real USB device is pinned to: 96 kHz / 24-bit integer.
let pinnedRealRate: Float64 = 96000
let pinnedRealBits: UInt32 = 24

/// German UI when the user's first preferred language is German, English otherwise.
let isGerman = (Locale.preferredLanguages.first ?? "").hasPrefix("de")
func L(_ en: String, _ de: String) -> String { isGerman ? de : en }


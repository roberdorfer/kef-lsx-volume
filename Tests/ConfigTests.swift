// Pure-function tests for the volume mapping in Helper/Config.swift. Built and run by `make test`;
// no XCTest so that `swiftc` alone suffices. The tests never touch the user's defaults: every call
// passes the range explicitly.
import Foundation

@main
struct ConfigTests {
    static var failures = 0
    static func check(_ ok: Bool, _ what: String) {
        if !ok { failures += 1; print("FAIL \(what)") }
    }

    static func main() {
        // Design decision: Equal measured-dB key steps between minimum and maximum — regression test.
        test_decision_keyStepsAreEqualInMeasuredDB()
        test_keyStepTable_endsAndMonotonic()
        test_keyStepTable_12to65_matchesDocumentedTable()
        test_approachFromAbove_landsOnFromAboveCurve()
        test_dB_curveShape()
        test_decision_speakerDiscoveryMatchRule()
        test_decision_identifiersUseGitHubReverseDNS()
        test_decision_outputFixedAt96kHz24bit()
        test_decision_realUSBMuteClearedUnlessProxyAtZero()
        if failures == 0 { print("ConfigTests: all passed") } else { print("ConfigTests: \(failures) failure(s)"); exit(1) }
    }

    static let ranges = [(12, 65), (12, 100), (20, 65), (30, 40), (60, 65), (12, 55), (8, 100)]

    static func test_keyStepTable_endsAndMonotonic() {
        for (lo, hi) in ranges {
            let t = Config.keyStepTable(min: lo, max: hi)
            check(t.count == 16, "\(lo)-\(hi): 16 entries, got \(t.count)")
            check(t.first == lo, "\(lo)-\(hi): first step is the minimum")
            check(t.last == hi, "\(lo)-\(hi): last step is the maximum")
            check(zip(t, t.dropFirst()).allSatisfy { $0 <= $1 }, "\(lo)-\(hi): monotone \(t)")
        }
    }

    static func test_decision_keyStepsAreEqualInMeasuredDB() {
        for (lo, hi) in ranges where hi - lo >= 15 {
            let t = Config.keyStepTable(min: lo, max: hi)
            let stepDB = (Config.dB(hi) - Config.dB(lo)) / 15
            // Each target is rounded to a whole speaker value (≤ 0.5 dB off), so two neighbouring steps
            // may differ from the ideal step by up to 1 dB; nothing larger is acceptable.
            for i in 1..<16 {
                let d = Config.dB(t[i]) - Config.dB(t[i - 1])
                check(abs(d - stepDB) <= 1.0 + 1e-9, "\(lo)-\(hi): step \(i) is \(d) dB, ideal \(stepDB)")
            }
            // And each step sits at the nearest speaker value to its ideal dB position.
            for i in 1..<15 {
                let ideal = Config.dB(lo) + stepDB * Double(i)
                check(abs(Config.dB(t[i]) - ideal) <= 0.5 + 1e-9, "\(lo)-\(hi): step \(i)=\(t[i]) is \(Config.dB(t[i]) - ideal) dB off its ideal")
            }
        }
    }

    static func test_keyStepTable_12to65_matchesDocumentedTable() {
        let expected = [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65]   // README, docs/usb-volume-curve.md
        check(Config.keyStepTable(min: 12, max: 65) == expected, "12-65 table \(Config.keyStepTable(min: 12, max: 65))")
    }

    static func test_approachFromAbove_landsOnFromAboveCurve() {
        for v in 8...100 {   // measured region only (below 8 both curves are extrapolated)
            if let via = Config.approachFromAbove(v, cap: 100) {
                check(via > v && via <= 100, "via \(via) for \(v) is above the target and within the cap")
                check(Config.dBFromBelow(via) >= Config.dB(v), "via \(via) from below is at least as loud as \(v) from above")
                check(Config.dBFromBelow(via - 1) < Config.dB(v) + 0.2 || via - 1 == v, "via \(via) for \(v) is the smallest overshoot")
            } else {
                check(Config.dBFromBelow(v) >= Config.dB(v) || v + 1 > 100, "\(v) needs no overshoot only when both curves agree")
            }
        }
        check(Config.approachFromAbove(12, cap: 100) == 17, "12 -> via 17")
        check(Config.approachFromAbove(17, cap: 100) == 20, "17 -> via 20")
        check(Config.approachFromAbove(41, cap: 100) == nil, "41 needs no overshoot")
        check(Config.approachFromAbove(64, cap: 65) == nil, "cap respected")
    }

    static func test_dB_curveShape() {
        check(Config.dB(65) == 0, "65 is the 0 dB reference")
        check(Config.dB(60) == -2.5 && Config.dB(12) == -50.5, "1 dB per unit to 60, 0.5 dB per unit above")
        check((1...100).allSatisfy { Config.dB($0) > Config.dB($0 - 1) }, "from-above curve strictly increasing")
        check((8...100).allSatisfy { Config.dBFromBelow($0) <= Config.dB($0) + 1e-9 }, "from below is never louder than from above (measured region)")
    }

    // Design decision: Identifiers are io.github.roberdorfer.* — regression test.
    static func test_decision_identifiersUseGitHubReverseDNS() {
        check(suiteName == "io.github.roberdorfer.lsxiivolumefixer", "defaults domain / bundle id")
        check(proxyUID == "io.github.roberdorfer.kefproxy.device", "proxy device UID")
        check(Config.userKeys.contains("defaultOutputSetFor") && !Config.userKeys.contains("firstRunDone"), "default-output marker is per proxy UID")
    }

    // Design decision: The speaker is found via Bonjour instead of a default hostname.
    static func test_decision_speakerDiscoveryMatchRule() {
        check(SpeakerDiscovery.matches(name: "LSX II", host: nil), "service name LSX II matches before resolving")
        check(SpeakerDiscovery.matches(name: "lsx ii", host: nil), "name match is case-insensitive")
        check(SpeakerDiscovery.matches(name: "Living room", host: "lsxii-0123456789ab.local."), "renamed speaker matches by host prefix")
        check(SpeakerDiscovery.matches(name: "Living room", host: "LSXII-0000.local"), "host prefix match is case-insensitive")
        check(!SpeakerDiscovery.matches(name: "Living room", host: nil), "unrelated name without host does not match")
        check(!SpeakerDiscovery.matches(name: "Printer", host: "brother-1234.local."), "unrelated service does not match")
        check(!SpeakerDiscovery.matches(name: "LS50", host: "ls50w2-abcd.local."), "other KEF model does not match")
    }

    // Design decision: The USB device is pinned to 96 kHz / 24-bit, the LSX II's internal DSP rate.
    static func test_decision_outputFixedAt96kHz24bit() {
        check(pinnedRealRate == 96000, "real device pinned to 96 kHz")
        check(pinnedRealBits == 24, "real device pinned to 24-bit")
    }

    // Design decision: A mute on the real USB device is cleared unless the proxy volume is 0.
    static func test_decision_realUSBMuteClearedUnlessProxyAtZero() {
        check(Config.shouldUnmuteReal(proxyScalar: 0.375), "proxy above 0: unmute real device")
        check(Config.shouldUnmuteReal(proxyScalar: Config.keyStep), "first key step: unmute real device")
        check(!Config.shouldUnmuteReal(proxyScalar: 0), "proxy at 0: leave real mute alone")
        check(!Config.shouldUnmuteReal(proxyScalar: nil), "no proxy: leave real mute alone")
        check(!Config.shouldUnmuteReal(proxyScalar: Config.zeroScalar), "zero threshold: leave real mute alone")
        check(Config.speakerVolume(forScalar: Config.zeroScalar) == 0, "same threshold maps to speaker 0")
    }
}
